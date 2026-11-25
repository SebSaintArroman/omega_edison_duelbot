using BotTest.Game.AI.Brains;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The MCTS rollout policy for the opponent's side (our side is played by our own brain twin when there is one): plays
    /// like a loose but sensible player. Plays cards more often than not, attacks only into what it beats (or directly),
    /// aims removal at the strongest card, pays costs with the weakest, revives and searches the strongest, puts beaters in
    /// attack position, and springs traps mostly when an attack comes. Random among what's left, so lines still vary.
    /// </summary>
    internal static class MctsPolicy
    {
        /// <summary>What the policy remembers within one line.</summary>
        internal sealed class LineState
        {
            public readonly int[] IdleActions = new int[2];
            /// <summary>ATK of the monster attacking now (the selected attacker from the last battle prompt).</summary>
            public int AttackerAtk;
            /// <summary>ATK of the other monsters that could still attack this Battle Phase (from the same battle prompt).</summary>
            public List<int> OtherAttackers = new();
        }

        /// <summary>A face-down monster's assumed strength (unknown: an average set monster).</summary>
        [Tunable("A face-down monster's assumed strength (unknown: an average set monster).", Section = "Search")]
        private static readonly Tuned<int> UnknownStrength = new(1500);

        public static byte[] Answer(Prompt prompt, Random rng, LineState line, DuelHost host)
        {
            switch (prompt.Data)
            {
                case IdleCmdData idle:
                {
                    var choices = SearchActions.Idle(idle);
                    var plays = new List<(SearchActions.Choice<IdleCmdResponse> Choice, double Weight)>();
                    bool canSummon = (idle.SummonableCards?.Length ?? 0) > 0;
                    // Played sensibly, cheaply (user, 2026-11-25: "make the simulated opponent's turn play out its moves sensibly", without
                    // slowing decisions): summons that beat the other side's board first, no pointless position changes, summons before battle.
                    // The other side's board: its strongest face-up attack-position monster and its weakest defender.
                    var other = host.Summary(1 - prompt.Player).Monsters;
                    int theirBest = other.Where(m => m.FaceUp && m.AttackPosition).Select(m => m.Attack).DefaultIfEmpty(0).Max();
                    int theirWeakest = other.Count == 0 ? -1
                        : other.Select(m => Strength(m.FaceUp, m.AttackPosition, m.Attack, m.Defense)).Min();
                    DynamicCard CardFor(DynamicCard[]? cards, MainAction action, ushort index) =>
                        (cards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.ActionIndex is { } ix && ix.Length > (int)action && ix[(int)action] == index);
                    double SummonWeight(DynamicCard card) =>
                        card.ID > 0 && (theirWeakest < 0 || card.Attack > theirWeakest) ? 5 : 3; // it can attack something (or directly)
                    double ReposWeight(DynamicCard card)
                    {
                        if ((card.Position & 0xA) != 0)
                            return 1; // face-down: a Flip Summon
                        bool attacking = (card.Position & 0x1) != 0;
                        if (!attacking)
                            return theirWeakest < 0 || card.Attack > theirWeakest ? 1 : 0; // to attack: only with something to beat
                        return card.Attack < theirBest && card.Defense > card.Attack ? 0.5 : 0; // to defence: only when it would lose and DEF is better
                    }
                    bool summonOnOffer = false;
                    if (line.IdleActions[prompt.Player]++ < 12)
                    {
                        foreach (var c in choices)
                        {
                            double w = c.Response.Action switch
                            {
                                MainAction.Summon => SummonWeight(CardFor(idle.SummonableCards, MainAction.Summon, c.Response.Index)),
                                MainAction.SpSummon => SummonWeight(CardFor(idle.SpecialSummonableCards, MainAction.SpSummon, c.Response.Index)),
                                MainAction.Activate => 3,
                                MainAction.SetMonster => canSummon ? 0.3 : 1,
                                MainAction.SetSpell => 0.8,
                                MainAction.Repos => ReposWeight(CardFor(idle.ReposableCards, MainAction.Repos, c.Response.Index)),
                                _ => 0,
                            };
                            if (w > 0)
                                plays.Add((c, w));
                            summonOnOffer |= w > 0 && c.Response.Action is MainAction.Summon or MainAction.SpSummon;
                        }
                    }
                    // Summons come before battle: going to battle with a summon still on offer is rare.
                    if (idle.CanBattlePhase)
                        plays.Add((choices.First(c => c.Key == "battle"), summonOnOffer ? 0.3 : 2));
                    else if (idle.CanEndPhase)
                        plays.Add((choices.First(c => c.Key == "end"), 1));
                    if (plays.Count == 0)
                        return prompt.Fallbacks.FirstOrDefault() ?? SearchActions.Packet((int)MainAction.ToEndPhase, 0);
                    double r = rng.NextDouble() * plays.Sum(p => p.Weight);
                    foreach (var (choice, weight) in plays)
                        if ((r -= weight) <= 0)
                            return SearchActions.Packet(choice.Response);
                    return SearchActions.Packet(plays[^1].Choice.Response);
                }
                case BattleCmdData battle:
                {
                    var choices = SearchActions.Battle(battle);
                    var attackers = (battle.AttackableCards ?? Array.Empty<DynamicCard>())
                        .Where(card => card.ActionIndex is { Length: > (int)BattleAction.Attack }).ToList();
                    if (attackers.Count > 0)
                    {
                        var defending = host.Summary(1 - prompt.Player);
                        var defenders = defending.Monsters.Select(m => new Defender(Strength(m.FaceUp, m.AttackPosition, m.Attack, m.Defense), m.AttackPosition)).ToList();
                        // Lethal first, always (user, 2026-11-12: "always assume the opponent will go for lethal if possible"):
                        // a monster to clear goes before the hits that need the field clear.
                        if (LethalPlan(attackers.Select(a => a.Attack).ToList(), defenders, defending.LifePoints, fixedFirst: false) is { } plan)
                        {
                            int pick = Enumerable.Range(0, attackers.Count).Where(i => plan[i] >= 0).DefaultIfEmpty(Array.IndexOf(plan, Direct)).First();
                            return Attack(line, battle, attackers[pick]);
                        }
                        // Otherwise the strongest monster that beats something (or hits directly) attacks, mostly.
                        var worth = attackers.Where(a => defenders.Count == 0 || defenders.Any(d => a.Attack > d.Strength))
                            .OrderByDescending(a => a.Attack).ToList();
                        var chosen = worth.Count > 0 ? worth[0] : attackers[0];
                        if (rng.NextDouble() < (worth.Count > 0 ? 0.95 : 0.1))
                            return Attack(line, battle, chosen);
                    }
                    var activate = choices.Where(c => c.Key.StartsWith("bact:")).ToList();
                    if (activate.Count > 0 && rng.NextDouble() < 0.2)
                        return SearchActions.Packet(activate[rng.Next(activate.Count)].Response);
                    var leave = choices.FirstOrDefault(c => c.Key == "main2") ?? choices.FirstOrDefault(c => c.Key == "end");
                    return leave != null ? SearchActions.Packet(leave.Response) : SearchActions.Packet((int)BattleAction.ToEndPhase, 0);
                }
                case SelectChainData chain:
                {
                    int count = chain.SelectableCards?.Length ?? 0;
                    // A hand effect during an attack (Honest, Kalut): the reason they hold it, so nearly always (user,
                    // 2026-11-11: a known Honest must be played around, so the lines must see it used).
                    if (host.AttackPending && !chain.Forced && chain.SelectableCards is { } offered &&
                        Array.FindIndex(offered, c => c.CurrentCoordinates.Location == (byte)CardLocation.Hand) is int fromHand and >= 0 &&
                        rng.NextDouble() < 0.9)
                        return BitConverter.GetBytes(fromHand);
                    double chance = host.AttackPending ? 0.6 : 0.25;
                    if (count > 0 && (chain.Forced || rng.NextDouble() < chance))
                        return BitConverter.GetBytes(rng.Next(count));
                    return BitConverter.GetBytes(chain.Forced ? 0 : -1);
                }
                case SelectEffectYnData:
                    return new byte[] { (byte)(rng.NextDouble() < 0.8 ? 1 : 0) };
                case SelectYesNoData:
                    return new byte[] { (byte)rng.Next(2) };
                case SelectOptionData option:
                    return BitConverter.GetBytes(rng.Next(Math.Max(1, option.Options?.Length ?? 1)));
                case SelectPositionData position:
                {
                    var options = position.Positions ?? Array.Empty<byte>();
                    if (options.Length == 0)
                        break;
                    var record = SearchHost.Cards.Get(position.CardID);
                    bool beater = record == null || record.Attack >= 1600 || record.Attack >= record.RealDefense;
                    byte wanted = beater ? (byte)0x1 : (byte)0x4;
                    byte pick = options.Contains(wanted) && rng.NextDouble() < 0.9 ? wanted : options[rng.Next(options.Length)];
                    return new[] { pick };
                }
                case SelectCardData select:
                {
                    var cards = select.SelectableCards ?? Array.Empty<DynamicCard>();
                    if (select.SelectionHint == (int)DHint.ATTACKTARGET)
                        return AttackTarget(cards, line, host.LifePoints(1 - prompt.Player), rng);
                    return Picks(cards, select.Minimum, select.Maximum, rng);
                }
                case SelectTributeData tribute:
                    // Tributes: the weakest of ours (Picks orders our field cards weakest first).
                    return Picks(tribute.SelectableCards ?? Array.Empty<DynamicCard>(), tribute.Minimum, tribute.Minimum, rng);
                case SelectUnselectCardData unselect:
                {
                    var cards = unselect.SelectableCards ?? Array.Empty<DynamicCard>();
                    if (cards.Length == 0 || (unselect.Minimum == 0 && rng.NextDouble() < 0.5))
                        return new byte[] { 0 };
                    return new byte[] { 1, Picks(cards, 1, 1, rng)[1] };
                }
            }
            // Sums, zones, counters, sorting, announcements: the safe default answer.
            return prompt.Fallbacks.FirstOrDefault() ?? Array.Empty<byte>();
        }

        /// <summary>What a monster takes to beat in battle (a face-down one: an average set monster).</summary>
        private static int Strength(bool faceUp, bool attackPosition, int attack, int defense) =>
            !faceUp ? UnknownStrength : attackPosition ? attack : defense;

        private static int Strength(DynamicCard card) =>
            Strength((card.Position & 0x5) != 0, (card.Position & 0x3) != 0, card.Attack, card.Defense);

        private readonly record struct Defender(int Strength, bool AttackPosition);

        private const int Direct = -1, Idle = -2;

        /// <summary>The attack declared now: its ATK, and the other attackers still to come (for the lethal check at the target pick).</summary>
        internal static void NoteAttacker(LineState line, BattleCmdData battle, DynamicCard attacker)
        {
            line.AttackerAtk = attacker.Attack;
            line.OtherAttackers = (battle.AttackableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !(card.CurrentCoordinates.Sequence == attacker.CurrentCoordinates.Sequence &&
                                 card.CurrentCoordinates.Location == attacker.CurrentCoordinates.Location))
                .Select(card => card.Attack).ToList();
        }

        private static byte[] Attack(LineState line, BattleCmdData battle, DynamicCard attacker)
        {
            NoteAttacker(line, battle, attacker);
            BattleCmdResponse response = (BattleAction.Attack, (ushort)attacker.ActionIndex![(int)BattleAction.Attack]);
            return SearchActions.Packet(response);
        }

        /// <summary>
        /// A way for these attackers to deal at least <paramref name="lp"/> this Battle Phase: for each attacker the defender
        /// it attacks (an index), <see cref="Direct"/> or <see cref="Idle"/>; null when there's none. Direct hits need every
        /// defender cleared by the others; a clear needs the attacker to beat it (a tie with an attack-position monster clears
        /// both); attack-position monsters it beats take the difference as damage. With <paramref name="fixedFirst"/> the first
        /// attacker is attacking now and must take a defender. Face-down monsters count as an average set monster (the
        /// attacker doesn't know them).
        /// </summary>
        private static int[]? LethalPlan(List<int> atks, List<Defender> defenders, int lp, bool fixedFirst)
        {
            if (atks.Sum() < lp || atks.Count > 6 || defenders.Count > 7)
                return null;
            var plan = new int[atks.Count];
            var taken = new bool[defenders.Count];
            bool Try(int i, int damage, int cleared)
            {
                if (i == atks.Count)
                {
                    int direct = 0;
                    for (int k = 0; k < atks.Count; k++)
                        if (plan[k] == Direct)
                            direct += atks[k];
                    return (direct == 0 || cleared == defenders.Count) && damage + direct >= lp;
                }
                for (int d = 0; d < defenders.Count; d++)
                {
                    if (taken[d])
                        continue;
                    var def = defenders[d];
                    bool beats = atks[i] > def.Strength || def.AttackPosition && atks[i] == def.Strength;
                    if (!beats)
                        continue;
                    taken[d] = true;
                    plan[i] = d;
                    if (Try(i + 1, damage + (def.AttackPosition ? atks[i] - def.Strength : 0), cleared + 1))
                        return true;
                    taken[d] = false;
                }
                if (i == 0 && fixedFirst)
                    return false;
                plan[i] = Direct;
                if (Try(i + 1, damage, cleared))
                    return true;
                plan[i] = Idle;
                return Try(i + 1, damage, cleared);
            }
            return Try(0, 0, 0) ? plan : null;
        }

        /// <summary>
        /// The attack target: the one that keeps a lethal line going when there is one, else the most valuable monster the
        /// attacker beats, else the one that hurts least.
        /// </summary>
        private static byte[] AttackTarget(DynamicCard[] cards, LineState line, int lp, Random rng)
        {
            if (cards.Length == 0)
                return new byte[] { 0 };
            int attackerAtk = line.AttackerAtk;
            var lethal = LethalPlan(new[] { attackerAtk }.Concat(line.OtherAttackers).ToList(),
                cards.Select(card => new Defender(Strength(card), (card.Position & 0x3) != 0)).ToList(), lp, fixedFirst: true);
            if (lethal != null)
                return new byte[] { 1, (byte)lethal[0] };
            int best = 0;
            double bestValue = double.NegativeInfinity;
            for (int i = 0; i < cards.Length; i++)
            {
                int strength = Strength(cards[i]);
                bool faceUp = (cards[i].Position & 0x5) != 0;
                double value = attackerAtk > strength ? 1000 + (faceUp ? cards[i].Attack : 500) : -(strength - attackerAtk);
                value += rng.NextDouble() * 50; // ties at random
                if (value > bestValue)
                {
                    bestValue = value;
                    best = i;
                }
            }
            return new byte[] { 1, (byte)best };
        }

        /// <summary>
        /// Card picks for effects: their cards strongest first (removal), our field cards weakest first (costs,
        /// tributes), our cards elsewhere strongest first (revive, search). Mostly the top of that order, sometimes random.
        /// </summary>
        private static byte[] Picks(DynamicCard[] cards, int min, int max, Random rng)
        {
            int count = cards.Length;
            if (count == 0)
                return new byte[] { 0 };
            min = Math.Clamp(min, 0, count);
            max = Math.Clamp(Math.Max(min, max), 1, count);
            int take = min == max ? min : min == 0 ? (rng.NextDouble() < 0.7 ? 1 : 0) : min;
            if (take == 0)
                return new byte[] { 0 };
            double Order(DynamicCard c)
            {
                var at = c.CurrentCoordinates;
                bool onField = at.Location is (byte)CardLocation.MonsterZone or (byte)CardLocation.SpellZone;
                double strength = (c.Position & 0x5) != 0 || !onField ? c.Attack : UnknownStrength;
                if (at.Controller != 0)
                    return -strength - (onField ? 500 : 0);
                return onField ? strength : -strength;
            }
            var order = Enumerable.Range(0, count).ToList();
            if (rng.NextDouble() < 0.8)
                order = order.OrderBy(i => Order(cards[i]) + rng.NextDouble()).ToList();
            else
                order = order.OrderBy(_ => rng.Next()).ToList();
            var picks = order.Take(take).Select(i => (byte)i).ToList();
            picks.Insert(0, (byte)picks.Count);
            return picks.ToArray();
        }
    }
}
