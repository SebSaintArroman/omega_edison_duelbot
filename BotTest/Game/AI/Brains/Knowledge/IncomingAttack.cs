using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Their attacks: which of their monsters attacked, our monsters before their battle, and the attack coming at us now
    /// (AssessIncomingAttackAsync), for the chain answers.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Their monster zones we saw battling this turn (their turn number): Omega never marks their attackers, and the battler is gone by
        // the time a trigger after the battle asks us (seen 2026-11-18: Ryko, flipped by Chimeratech Fortress Dragon's attack, destroyed that
        // same Chimeratech: "the one that had attacked", while the other Chimeratech and Machina Fortress were still to come).
        private (int Turn, HashSet<int> Zones) _theirAttackedZones = (-1, new HashSet<int>());
        private (int Turn, HashSet<(int Zone, int Id)> Monsters) _opponentBeforeTheirTurn = (-1, new());
        private int _opponentSummonedAndAttackedWithThreat = -1;

        /// <summary>At every prompt of ours (PromptArrived) in their Battle Phase: the monster of theirs battling now has attacked.</summary>
        protected async Task TrackTheirAttackersAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            if (IsOwnTurn(header))
            {
                _opponentBeforeTheirTurn = (header.CurrentTurn, (await GetEnemyMonsterCardsAsync())
                    .Where(card => card.ID > 0 && card.IsFaceup())
                    .Select(card => ((int)card.CurrentCoordinates.Sequence, card.ID)).ToHashSet());
                return;
            }
            if (phase is not (DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle))
                return;
            var current = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (current.ID == 0 || current.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone)
                return;
            if (_theirAttackedZones.Turn != header.CurrentTurn)
                _theirAttackedZones = (header.CurrentTurn, new HashSet<int>());
            _theirAttackedZones.Zones.Add(current.CurrentCoordinates.Sequence);
            if (_opponentBeforeTheirTurn.Turn == header.CurrentTurn - 1 &&
                !_opponentBeforeTheirTurn.Monsters.Contains((current.CurrentCoordinates.Sequence, current.ID)) &&
                BoardAssessment.Threats((await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack())
                    .Select(card => card.Attack),
                    new[] { new BoardAssessment.Defender(current.Attack, true, false, current.ID) }).Count > 0)
                _opponentSummonedAndAttackedWithThreat = header.CurrentTurn;
        }

        // Our monsters on the field at the last decision before their Battle Phase this turn (their turn number), for counting
        // the attacks we got no prompt for.
        private (int Turn, int Count) _ourMonstersBeforeTheirBattle = (-1, 0);

        /// <summary>At every real decision: before their Battle Phase (their turn, or our turn ending), the count of our monsters.</summary>
        private async Task ObserveMonstersBeforeTheirBattleAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            bool theirBattle = !IsOwnTurn(header) &&
                               phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (theirBattle && _ourMonstersBeforeTheirBattle.Turn == header.CurrentTurn)
                return;
            int count = (await GetBotMonsterCardsAsync()).Count(card => card.ID != 0);
            // On our own turn the count is for their next turn (their Battle Phase may come with no prompt before it).
            _ourMonstersBeforeTheirBattle = (IsOwnTurn(header) ? header.CurrentTurn + 1 : header.CurrentTurn, count);
        }

        /// <summary>What letting the current opponent attack through would cost us.</summary>
        protected sealed record IncomingAttack(
            DynamicCard Attacker,
            DynamicCard Target,
            bool CostsUs,
            bool Lethal,
            bool OurOwnMonster,
            bool WorthRemovingAnyway,
            string Why)
        {
            /// <summary>
            /// Worth banishing even when letting it through costs nothing: a big threat, or a recruiter (removing it
            /// outside battle stops the replacement it would bring out).
            /// </summary>
            public bool WorthBanishingAnyway { get; init; }

            /// <summary>
            /// Worth destroying (Mirror Force) even when letting it through costs nothing: a big threat, or a recruiter
            /// that only recruits when destroyed by battle (Giant Rat). One that recruits on any destruction needs banishing.
            /// </summary>
            public bool WorthDestroyingAnyway { get; init; }

            /// <summary>Why removing it is worth it anyway (empty if it isn't). Only for removal notes, not Necro Gardna's.</summary>
            public string RemovalWhy { get; init; } = "";

            /// <summary>
            /// The only cost is life points: a non-lethal hit that leaves us within reach of their board next turn. A
            /// negate (Necro Gardna) shouldn't be spent on that: it works just as well next turn, at the last moment.
            /// </summary>
            public bool OnlyLifePointsCost { get; init; }
        }

        // Attacker text that rewards its owner when it hits (X-Saber Airbellum's discard, ...).
        private static readonly System.Text.RegularExpressions.Regex BattleDamageEffectText = new(
            @"inflicts? (battle )?damage to your opponent[^.]*:|direct attack[^.]*:|inflicts battle damage",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Reads the attack in progress: does letting it through cost us anything that matters? Life points alone
        /// don't, cards do. It costs us when:
        ///  - it is lethal (or this hit plus the attacks still to come are),
        ///  - it destroys a monster of ours that isn't expendable,
        ///  - it bounces (Grand Mole) a token or Extra Deck monster of ours (lost), a main-deck one just returns to hand,
        ///  - the attacker's "when it inflicts battle damage" effect would land (e.g. Airbellum's discard: a -1),
        ///  - the attacker dies in this battle and its "when destroyed" effect rewards them (Kittytail, Giant Rat).
        /// Removing an attacker we own (taken with Brain Control) is never right: it comes back at the End Phase.
        /// Strong attackers (Extra Deck, 2000+ ATK, bouncers, strong effects) are worth removing anyway.
        /// </summary>
        protected async Task<IncomingAttack?> AssessIncomingAttackAsync(List<DynamicCard> enemyMonsters)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (attacker.ID <= 0)
                return null;
            // Read the attacker's text (floater, bouncer) even if it was summoned this turn.
            await RefreshRewardOnDestroyAsync(enemyMonsters);
            var target = await CurrentField.GetBotBattlingMonster();
            bool direct = target.ID <= 0;
            int lp = await CurrentField.GetBotLifePoints();
            var costs = new List<string>();
            var fine = new List<string>();

            bool ourOwn = attacker.Owner == 0;
            if (ourOwn)
                fine.Add($"{CardNames.Get(attacker.ID)} is our own monster (they took control): it comes back to us");

            int damage = direct ? attacker.Attack
                : target.IsAttack() && attacker.Attack > target.Attack ? attacker.Attack - target.Attack : 0;
            int stillToCome = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, attacker))
                .Sum(card => card.Attack);
            bool lethal = damage >= lp || (direct && damage + stillToCome >= lp && stillToCome < lp);
            if (lethal)
                costs.Add($"{damage} damage would (help) finish us at {lp} LP");
            // Life points matter once they're within reach: a hit that leaves us where their current board kills us
            // next turn costs us, even if it isn't lethal now.
            int theirBoard = enemyMonsters.Where(card => card.IsFaceup()).Sum(card => card.Attack);
            int lpAfter = lp - damage - (direct ? stillToCome : 0);
            bool withinReach = !lethal && damage > 0 && !ourOwn && lpAfter <= theirBoard;
            if (withinReach)
                costs.Add($"it leaves us at {Math.Max(0, lpAfter)} LP, within reach of their board ({theirBoard} ATK) next turn");

            bool bounces = BouncesAttacker(attacker);
            if (!direct && bounces)
            {
                if (ReSummonCost(target) >= 100)
                    costs.Add($"it returns our {CardNames.Get(target.ID)} to the hand: a token / Extra Deck monster is lost");
                else
                    fine.Add($"it only returns our {CardNames.Get(target.ID)} to the hand (and itself too)");
            }
            else if (!direct)
            {
                bool destroyed = !DestroysByEffectInBattle(target, attacker) &&
                                 (target.IsAttack() ? attacker.Attack >= target.Attack : attacker.Attack > target.Defense);
                if (destroyed && !IsExpendable(target))
                    costs.Add($"it would destroy our {CardNames.Get(target.ID)}");
                else if (destroyed)
                    fine.Add($"our {CardNames.Get(target.ID)} is expendable");
                else
                    fine.Add($"our {CardNames.Get(target.ID)} survives");
            }

            if (damage > 0 && await TextMatchesAsync(attacker.ID, BattleDamageEffectText))
                costs.Add($"its battle-damage effect would land (a {damage} hit)");

            bool attackerDies = !direct && !bounces && (target.IsAttack() && target.Attack >= attacker.Attack || DestroysByEffectInBattle(target, attacker));
            if (attackerDies && RewardsDestruction(attacker))
                costs.Add($"it would die in battle and its effect would reward them: banish/stop it first");

            // Worth removing anyway: only a big threat (not e.g. Giant Rat or Kinka-byo). A recruiter is still worth
            // removing outside battle, which stops its replacement: by any removal if it only recruits when destroyed by
            // battle (Giant Rat), else only by banishing.
            bool strong = !ourOwn && (bounces || await IsBigThreatAsync(attacker));
            bool recruiter = !ourOwn && IsRecruiter(attacker);
            bool battleOnlyRecruiter = recruiter && RecruitsOnlyByBattle(attacker);

            bool costsUs = !ourOwn && costs.Count > 0 || lethal;
            string why = $"{CardNames.Get(attacker.ID)} attacks {(direct ? "directly" : CardNames.Get(target.ID))}: " +
                         (costs.Count > 0 ? string.Join("; ", costs) : "it costs us nothing that matters") +
                         (fine.Count > 0 ? $" ({string.Join("; ", fine)})" : "");
            string removalWhy = strong ? "; it is a big threat, worth removing anyway"
                : battleOnlyRecruiter ? "; removing it outside battle stops the monster it would bring out"
                : recruiter ? "; banishing it stops the monster it would bring out"
                : "";
            return new IncomingAttack(attacker, target, costsUs, lethal, ourOwn, strong, why)
            {
                WorthBanishingAnyway = strong || recruiter,
                WorthDestroyingAnyway = strong || battleOnlyRecruiter,
                RemovalWhy = removalWhy,
                OnlyLifePointsCost = withinReach && costs.Count == 1
            };
        }
    }
}
