using OmegaBotWrapper;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// How fast the current board would win if neither player changed anything.
    /// </summary>
    public enum ClockSpeed
    {
        /// <summary>2 turns or fewer.</summary>
        Quick,
        /// <summary>3 to 5 turns.</summary>
        Long,
        /// <summary>6 to 8 turns.</summary>
        VeryLong,
        /// <summary>9 to 10 turns: barely a clock.</summary>
        AlmostNone,
        /// <summary>The board can't get through at all.</summary>
        None
    }

    /// <summary>
    /// Generic "should I spend a resource?" reasoning, following the general rules:
    ///  1. trade favourably in card count (don't go -1 for nothing),
    ///  2. put the opponent on a clock (a board that wins in a few turns if unanswered forces them to act),
    ///  3. deal with the opponent's good cards (threats the current board can't handle).
    /// Spend a resource only if it wins this turn, answers a threat, or creates/speeds up a clock
    /// when the opponent isn't already on one. Otherwise hold it for later threats.
    /// </summary>
    public static class BoardAssessment
    {
        public const int NoClock = int.MaxValue;

        /// <summary>
        /// Assumed DEF of an opponent's face-down monster. The bot must not peek at hidden cards, so it assumes the
        /// format's usual face-down monster, Ryko, Lightsworn Hunter (200/100): any attacker beats it in battle,
        /// but its flip effect will usually cost the bot a card.
        /// </summary>
        public static int AssumedFaceDownDefense { get; set; } = 100;

        public readonly record struct Defender(int Power, bool AttackPosition, bool FaceDown, int Id, bool HonestBacked = false)
        {
            /// <summary>Returns the monster that battles it to the hand (Grand Mole): it soaks up an attacker for no damage.</summary>
            public bool BouncesAttacker => Id > 0 && KnownAttackerBouncers.ContainsKey(Id);
        }

        /// <summary>
        /// Card IDs whose text returns the monster battling them to the hand (e.g. Neo-Spacian Grand Mole). Filled by the
        /// deck brains as they read opponent card text, so every clock and lethal calculation can account for them.
        /// </summary>
        /// <remarks>Concurrent: the search's brain twins add to it from their own threads.</remarks>
        public static System.Collections.Concurrent.ConcurrentDictionary<int, byte> KnownAttackerBouncers { get; } = new();

        /// <summary>
        /// True for an opponent card the bot must treat as unknown. The server still sends the real identity
        /// (Alias) and stats of face-down cards in prompt data, so never read Alias/ATK/DEF/Type of these cards.
        /// Cards in the opponent's hand that appear in a prompt have been revealed by an effect, so they are fair game.
        /// </summary>
        public static bool IsHiddenFromBot(DynamicCard card) =>
            card.CurrentCoordinates.Controller != 0 &&
            card.ID <= 0 &&
            card.CurrentCoordinates.Location != (byte)OmegaBotWrapper.Enum.CardLocation.Hand;

        /// <summary>Identity of an opponent card the bot is allowed to know (revealed cards carry it only in Alias), or 0.</summary>
        public static int KnownId(DynamicCard card) =>
            card.ID > 0 ? card.ID : IsHiddenFromBot(card) ? 0 : card.Alias;

        public static ClockSpeed SpeedOf(int turns) => turns switch
        {
            <= 2 => ClockSpeed.Quick,
            <= 5 => ClockSpeed.Long,
            <= 8 => ClockSpeed.VeryLong,
            NoClock => ClockSpeed.None,
            _ => ClockSpeed.AlmostNone
        };

        public static string Describe(int turns) =>
            turns == NoClock ? "no clock" : $"{turns}-turn clock ({SpeedOf(turns)})";


        /// <summary>
        /// Opponent monsters as the bot is allowed to see them. A face-down card the bot has seen face-up in the same
        /// zone (see <see cref="OpponentMemory"/>) is treated as that card; any other face-down card is assumed to be Ryko.
        /// </summary>
        public static List<Defender> Defenders(IEnumerable<DynamicCard> enemyMonsters, OpponentMemory? memory = null)
        {
            var monsters = enemyMonsters.Where(card => card.ID > 0 || card.Position != 0).ToList();
            memory?.Observe(monsters);
            // A known Honest in their hand (user, 2026-11-11) wins one battle for their LIGHT monster: the strongest face-up
            // attack-position LIGHT one is marked. Its power stays real: attacking into it still forces Honest out (a 1-for-1, and
            // it has to be got past some time: user, 2026-11-11); what it stops is spending more on a push (see JustifySpendAsync).
            var honestBacked = memory is { KnownHonest: > 0 }
                ? monsters.Where(card => card.IsFaceup() && card.IsAttack() && (card.Attribute & 0x10) != 0)
                    .OrderByDescending(card => card.GetRealPower()).Take(memory.KnownHonest)
                    .Select(card => (card.CurrentCoordinates.Location, card.CurrentCoordinates.Sequence)).ToHashSet()
                : new HashSet<(byte, byte)>();

            return monsters.Select(card =>
            {
                if (card.IsFaceup())
                    return new Defender(card.GetRealPower(), card.IsAttack(), false, card.ID,
                        honestBacked.Contains((card.CurrentCoordinates.Location, card.CurrentCoordinates.Sequence)));
                if (memory != null && memory.TryRecall(card, out var known))
                    // Face-down cards are in defence position and lose stat changes, so use the base DEF.
                    return new Defender(known.BaseDefense, false, false, known.Id);
                return new Defender(memory?.FaceDownDefense(card) ?? AssumedFaceDownDefense, false, true, 0);
            }).ToList();
        }

        /// <summary>
        /// Turns needed to win if the board stays as it is: each turn the bot's attackers destroy whatever
        /// they can beat (weakest sufficient attacker per defender, strongest defender first) and attack
        /// directly once the field is clear. Opponent monsters are assumed not to attack back.
        /// </summary>
        public static int TurnsToWin(IEnumerable<int> attackerAtk, IEnumerable<Defender> defenders, int opponentLp, int maxTurns = 10)
        {
            var attackers = attackerAtk.Where(atk => atk > 0).ToList();
            var opponents = defenders.ToList();
            int lp = opponentLp;
            if (lp <= 0)
                return 0;
            if (attackers.Count == 0)
                return NoClock;

            for (int turn = 1; turn <= maxTurns; turn++)
            {
                var available = attackers.OrderBy(atk => atk).ToList();
                int damage = 0;
                bool progress = false;

                // A monster like Grand Mole sends its attacker (and itself) back to the hand: it costs us an attacker,
                // whatever its ATK, and deals no damage. Spend the weakest one on it.
                foreach (var bouncer in opponents.Where(d => d.BouncesAttacker).ToList())
                {
                    if (available.Count == 0)
                        break;
                    attackers.Remove(available[0]);
                    available.RemoveAt(0);
                    opponents.Remove(bouncer);
                    progress = true;
                }

                foreach (var defender in opponents.OrderByDescending(d => d.Power).ToList())
                {
                    int index = available.FindIndex(atk => atk > defender.Power);
                    if (index >= 0)
                    {
                        int atk = available[index];
                        available.RemoveAt(index);
                        opponents.Remove(defender);
                        progress = true;
                        if (defender.AttackPosition)
                            damage += atk - defender.Power;
                        continue;
                    }

                    // Equal ATK against an attack-position monster: both are destroyed (a trade clears the way).
                    int trade = defender.AttackPosition ? available.FindIndex(atk => atk == defender.Power) : -1;
                    if (trade >= 0)
                    {
                        available.RemoveAt(trade);
                        attackers.Remove(defender.Power);
                        opponents.Remove(defender);
                        progress = true;
                    }
                }

                if (opponents.Count == 0)
                {
                    damage += available.Sum();
                    progress |= available.Count > 0;
                }

                if (!progress)
                    return NoClock;

                lp -= damage;
                if (lp <= 0)
                    return turn;
            }

            return NoClock;
        }

        /// <summary>
        /// Opponent face-up monsters that none of the bot's monsters can get rid of in battle
        /// (beat outright, or trade with at equal ATK when the defender is in attack position).
        /// </summary>
        public static List<Defender> Threats(IEnumerable<int> attackerAtk, IEnumerable<Defender> defenders)
        {
            int best = attackerAtk.DefaultIfEmpty(0).Max();
            return defenders
                .Where(d => !d.FaceDown)
                .Where(d => d.Power > best || (d.Power == best && !d.AttackPosition))
                .ToList();
        }

        public static string DescribeThreats(IEnumerable<Defender> threats) =>
            string.Join(", ", threats.Select(t => $"{CardNames.Get(t.Id)} ({t.Power})"));
    }
}
