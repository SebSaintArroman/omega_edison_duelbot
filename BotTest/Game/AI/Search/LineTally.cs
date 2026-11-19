using BotTest.Game.AI.Brains;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The line penalties that depend on a sequence of our moves within our turn (user, 2026-11-13: rules whose hold only
    /// guessed what would happen later are checked against what the line actually does). Each line starts from the real
    /// turn's TurnRecord, so lines started later in the turn count the same history as the reused tree's older lines.
    /// <list type="number">
    /// <item>Lyla's effect in Main Phase 1, then the Battle Phase: LylaBeforeBattleCost (weak: Lyla clearing the way for a push is
    /// better than her post-battle gamble).</item>
    /// <item>A union equip (Machina Gearframe / Peacekeeper) in Main Phase 1, then the Battle Phase: EquipBeforeBattleCost.</item>
    /// <item>Miracle Fusion with no Battle Phase after it that turn: FusionWithoutBattleCost.</item>
    /// <item>A monster summoned face-up that by the end of our turn never attacked, never used an effect and is still on the
    /// field: IdleSummonCost each (no Battle Phase possible this turn, the duel's first: not charged).</item>
    /// </list>
    /// </summary>
    internal sealed class LineTally
    {
        [Tunable("")]
        public static double LylaBeforeBattleCost = 0.5;
        [Tunable("")]
        public static double EquipBeforeBattleCost = 1.5;
        [Tunable("")]
        public static double FusionWithoutBattleCost = 1.5;
        [Tunable("")]
        public static double IdleSummonCost = 1.5;
        [Tunable("was 0.2 (CSV S4: too small to put draws first)")]
        public static double DrawLaterCost = 0.5; // was 0.2 (CSV S4: too small to put draws first)
        /// <summary>Bonus (card units) for a monster's effect used in a chain window of our turn (its priority window) rather than later.</summary>
        [Tunable("Bonus (card units) for a monster's effect used in a chain window of our turn (its priority window) rather than later.")]
        public static double PriorityWindowBonus = 0.3;
        /// <summary>
        /// Per other monster of ours that could still attack, for an effect of ours that stops them attacking (Sirocco's boost, Chaos
        /// Sorcerer's banish): the downside of the effect, which a big enough gain (the boost winning a battle) overcomes. User,
        /// 2026-11-16: "stopping your things attacking is a negative part of an effect, it should have that".
        /// </summary>
        [Tunable("Per other monster of ours that could still attack, for an effect of ours that stops them attacking (Sirocco's boost, Chaos Sorcerer's banish): the downside of the effect, which a big enough gain (the boost winning a battle) overcomes. User, 2026-11-16: \"stopping your things attacking is a negative part of an effect, it should have that\".")]
        public static double AttackLockCost = 0.4;

        /// <summary>A line that enters the Battle Phase and makes no attack (user, 2026-11-16: "maybe there should be a very small penalty for moving to battle phase if not planning to attack").</summary>
        [Tunable("A line that enters the Battle Phase and makes no attack: this small cost (card units).")]
        public static double EmptyBattleCost = 2; // user, 2026-11-18: "make that bigger" (was 1); 2026-11-17: "it should outweigh the doing stuff in mp2 bonuses" (was 0.1)

        // "Other monsters you control cannot attack", "you cannot attack with other monsters", "only this card can attack".
        private static readonly System.Text.RegularExpressions.Regex AttackLockText = new(
            @"other monsters? (you control )?cannot attack|cannot attack with (any )?other monsters?|only this card can attack|" +
            @"monsters? you control cannot attack[^.]{0,40}except (this card|the targeted)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> AttackLockCards = new();

        private static bool LocksOtherAttacks(int code) => code > 0 && AttackLockCards.GetOrAdd(code, id =>
            SearchHost.Available && SearchHost.Cards.Get(id) is { } info && AttackLockText.IsMatch(info.Text ?? ""));

        /// <summary>Whether a DARK monster is in our hand right now (set by the search, which sees the board).</summary>
        public Func<bool>? DarkInHand { get; set; }

        /// <summary>Their monsters that can remove one of ours in our Battle Phase: face-down ones (a flip: Ryko), face-up ones with a
        /// "destroyed by battle" effect (Machina Fortress). More sources can join this count later.</summary>
        public Func<int>? BattleRemovalRisk { get; set; }

        [Tunable("A monster effect of ours on offer in Main Phase 1, used only in Main Phase 2 after a Battle Phase while their board could remove that monster in the battle (a face-down flip, Fortress's battle trigger): this cost. User, 2026-11-18: \"if a monster's effect could be used in mp1 or mp2 with no difference, it should be done in mp1\".")]
        public static double EffectAfterBattleCost = 0.5;

        // Our monsters' effects on offer before the Battle Phase (by card), and whether their board could remove a monster in it then.
        private readonly HashSet<int> _effectsBeforeBattle = new();
        private int _riskAtBattle = -1; // their removal-risk monsters as our Battle Phase began

        /// <summary>Allure of Darkness activated with a DARK in hand to banish: this small bonus (user, 2026-11-18: "give allure a small activation
        /// bonus"). Without a DARK it takes the whole hand: no bonus (the brain holds it then).</summary>
        [Tunable("Allure of Darkness activated with a DARK in hand to banish: this small bonus (card units).")]
        public static double AllureBonus = 0.3;
        internal static readonly int[] Allure = { 1475311 };

        /// <summary>How many of our monsters other than this card could still attack this turn (set by the search, which sees the board).</summary>
        public Func<int, int>? OtherAttackers { get; set; }

        private double AttackLock(int code, Action<string>? note)
        {
            if (!LocksOtherAttacks(code) || OtherAttackers == null)
                return 0;
            int blocked = OtherAttackers(code);
            if (blocked <= 0)
                return 0;
            note?.Invoke($"  ({blocked} other monster(s) of ours can't attack after this effect: -{AttackLockCost * blocked:0.#})");
            return AttackLockCost * blocked;
        }

        private static readonly System.Text.RegularExpressions.Regex DrawText = new(
            @"\bdraw (\d|one|two|three) cards?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> DrawCards = new();

        /// <summary>Whether this card's text draws cards (Pot of Greed, Upstart Goblin, Allure of Darkness...).</summary>
        internal static bool Draws(int code) => code > 0 && DrawCards.GetOrAdd(code, id =>
            SearchHost.Available && SearchHost.Cards.Get(id) is { } info && DrawText.IsMatch(info.Text ?? ""));

        internal static readonly int[] Lyla = { 22624373, 22624393 };
        internal static readonly int[] Unions = { 42940404, 42940424, 78349103, 78349123 };
        internal static readonly int[] MiracleFusion = { 45906428 };
        internal static readonly int[] BrainControl = { 87910978 };

        [Tunable("Setting a normal spell (it is worth less set than in hand, and shows a card): this line cost. User, 2026-11-19: \"put the set spell value back to 0.8 and add back the line penalty of 0.5 for setting one\".")]
        public static double SetNormalSpellCost = 0.5;
        [Tunable("Brain Control whose monster goes back to them at the end of our turn (not tributed, not Synchro material, no win): this small cost. User, 2026-11-19: \"the goal of it is to tribute or synchro with my monster, or win that turn\".")]
        public static double BrainControlUnusedCost = 2; // user, 2026-11-19: "make it a 2-point hold"
        [Tunable("A trap of ours fired on their turn in a window opened by nothing (no chain to answer, no attack, no summon just made; not their End Phase): it could have waited for the last possible moment. User, 2026-11-19.")]
        public static double EarlyTrapCost = 1.5; // user, 2026-11-19: 1.5
        [Tunable("Return from the Different Dimension fired on their turn, even against an attack, unless Return is about to be destroyed: this cost, instead of EarlyTrapCost. Not on our turn (plenty of plays there). User, 2026-11-19.")]
        public static double ReturnTheirTurnCost = 1.5;
        internal static readonly int[] ReturnFromTheDifferentDimension = { 27174286 };

        private bool _brainControlPending;
        private int _brainTarget, _brainTargetCopies;

        private static bool IsNormalSpell(int code) =>
            code > 0 && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x2) != 0 && (r.Type & (0x80 | 0x10000 | 0x20000 | 0x40000 | 0x80000)) == 0;

        private static bool IsTrap(int code) => code > 0 && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x4) != 0;

        /// <summary>Our chain answer on their turn: a trap fired in an eventless window (not their End Phase) costs EarlyTrapCost.</summary>
        public double OnTheirTurnChain(SelectChainData chain, byte[] packet, bool eventless, bool attacking, Func<int, int, bool> doomed, int phase, Action<string>? note)
        {
            if (packet.Length < 4)
                return 0;
            int index = BitConverter.ToInt32(packet, 0);
            var cards = chain.SelectableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>();
            if (index < 0 || index >= cards.Length || cards[index].CurrentCoordinates.Controller != 0 || !IsTrap(Canonical(cards[index].ID)))
                return 0;
            // Return from the Different Dimension on their turn costs, unless it's about to be destroyed (user, 2026-11-19).
            if (Is(cards[index].ID, ReturnFromTheDifferentDimension))
            {
                var at = cards[index].CurrentCoordinates;
                // Even against an attack (user, 2026-11-19: "add the return their turn cost even if it is answering an attack too"): only a
                // Return about to be destroyed is free.
                if (doomed(at.Location, at.Sequence))
                    return 0;
                note?.Invoke($"  (Return from the Different Dimension on their turn, not about to be destroyed: -{ReturnTheirTurnCost:0.#})");
                return ReturnTheirTurnCost;
            }
            if (!eventless || phase == (int)OmegaBotWrapper.Enum.DuelPhase.End)
                return 0;
            note?.Invoke($"  (a trap fired on their turn with nothing to answer: it could wait for the last moment: -{EarlyTrapCost:0.#})");
            return EarlyTrapCost;
        }

        private int _sets, _equips;
        private bool _battle, _lyla, _fusion, _turnOver, _battleInLine, _attackedInLine;
        private readonly bool _noBattlePossible;
        private readonly List<int> _idleSummons;

        public LineTally(TurnRecord record, bool battleAlreadyEntered, bool noBattlePossible)
        {
            _sets = record.SetsBeforeBattle;
            _equips = record.EquipsBeforeBattle;
            _lyla = record.LylaBeforeBattle;
            _fusion = record.FusionWithoutBattle;
            _battle = record.BattleEntered || battleAlreadyEntered;
            _idleSummons = record.IdleSummons.Select(Canonical).ToList();
            _noBattlePossible = noBattlePossible;
            Start = record.HoldCosts;
        }

        /// <summary>The cost the line starts with: the holds our real turn already broke.</summary>
        public double Start { get; }

        private static int Canonical(int id) => SearchHost.Available && SearchHost.CanonicalId(id) is > 0 and var official ? official : id;
        internal static bool Is(int code, int[] ids) => code > 0 && (ids.Contains(code) || ids.Contains(Canonical(code)));

        /// <summary>Our answer to a prompt in our turn (any kind). Returns the cost it adds.</summary>
        public double OnOurAnswer(Prompt prompt, byte[] packet, Action<string>? note)
        {
            if (_turnOver || packet.Length < 1)
                return 0;
            switch (prompt.Data)
            {
                case IdleCmdData idle:
                    return OnMainMove(idle, packet, note);
                case BattleCmdData battle when packet[0] == (byte)BattleAction.Attack && (battle.AttackableCards?.Length ?? 0) > 0:
                    // Omega attacks with the first attacker in zone order, whatever index is sent.
                    _attackedInLine = true;
                    _idleSummons.Remove(Canonical(battle.AttackableCards![0].ID));
                    return 0;
                case SelectChainData chain when packet.Length >= 4:
                {
                    int index = BitConverter.ToInt32(packet, 0);
                    var cards = chain.SelectableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>();
                    if (index < 0 || index >= cards.Length)
                        return 0;
                    _idleSummons.Remove(Canonical(cards[index].ID));
                    // A monster's effect used in the window it gets (the priority window after its summon), not later: almost always
                    // right (user, 2026-11-15), so it wins the ties. Not Lyla (her effect waits for the Battle Phase), not a union
                    // equip (chained to Fortress's summon, a Bottomless takes both).
                    int code = cards[index].ID;
                    double lockCost = cards[index].CurrentCoordinates.Controller == 0 ? AttackLock(code, note) : 0;
                    if (cards[index].CurrentCoordinates.Location == (byte)OmegaBotWrapper.Enum.CardLocation.MonsterZone &&
                        cards[index].CurrentCoordinates.Controller == 0 && !Is(code, Lyla) && !Is(code, Unions))
                    {
                        note?.Invoke($"  (a monster effect used in its chain window: +{PriorityWindowBonus:0.#})");
                        return lockCost - PriorityWindowBonus;
                    }
                    return lockCost;
                }
                case SelectCardData pick when _brainControlPending && packet.Length >= 2 && packet[0] >= 1:
                {
                    _brainControlPending = false;
                    var cards = pick.SelectableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>();
                    if (packet[1] < cards.Length && cards[packet[1]].CurrentCoordinates.Controller == 1)
                    {
                        _brainTarget = Canonical(cards[packet[1]].ID);
                        _brainTargetCopies = cards.Count(c => c.CurrentCoordinates.Controller == 1 && Canonical(c.ID) == _brainTarget);
                    }
                    return 0;
                }
                case SelectEffectYnData yn when packet[0] != 0:
                    _idleSummons.Remove(Canonical(yn.EffectCard.ID));
                    return 0;
            }
            return 0;
        }

        private double OnMainMove(IdleCmdData data, byte[] packet, Action<string>? note)
        {
            var action = (MainAction)packet[0];
            string? key = SearchActions.Idle(data).FirstOrDefault(c => SearchActions.Packet(c.Response).AsSpan().SequenceEqual(packet))?.Key;
            int code = key != null ? SearchActions.KeyCardId(key) : 0;
            double cost = 0;
            // A draw effect on offer that this move isn't: draws come first, so the rest of the turn is played with the new
            // cards (user, 2026-11-13: "prioritise effects that draw earlier in the turn, so that the lines can play out as
            // early as possible"). Small: a rule holding a draw back (Upstart Goblin waiting for lethal) still wins.
            // Main Phase 1: our monsters' effects on offer now (Grepher's discard, a Blackwing's ignition), and their battle-removal threat.
            if (!_battle)
            {
                foreach (var card in data.ActivableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>())
                    if (card.CurrentCoordinates is { Controller: 0, Location: (byte)OmegaBotWrapper.Enum.CardLocation.MonsterZone })
                        _effectsBeforeBattle.Add(Canonical(card.ID));
            }
            // Main Phase 2: such an effect used only now, after a battle that could have removed its monster (user, 2026-11-18: their Fortress's
            // trigger, a face-down): it should have gone first.
            // Only in genuine danger (user, 2026-11-18: "only when that monster is in genuine danger of being removed. dont start firing everything
            // willy nilly"): one of their risky monsters was fought in the battle (gone, or flipped face-up since it began).
            else if (action == MainAction.Activate && key != null && key.Contains("@0.4.") && _effectsBeforeBattle.Contains(Canonical(code)) &&
                     _riskAtBattle > 0 && BattleRemovalRisk != null && BattleRemovalRisk() < _riskAtBattle)
            {
                cost += EffectAfterBattleCost;
                note?.Invoke($"  (a monster effect on offer before the battle used only after it, with their board able to remove it: -{EffectAfterBattleCost:0.#})");
            }
            bool isDraw = action == MainAction.Activate && Draws(code);
            if (!isDraw && (data.ActivableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>()).Any(card => Draws(Canonical(card.ID))))
            {
                cost += DrawLaterCost;
                note?.Invoke($"  (a draw effect was on offer and this move came first: -{DrawLaterCost:0.#})");
            }
            switch (action)
            {
                case MainAction.SetSpell or MainAction.SetMonster:
                    if (action == MainAction.SetSpell && IsNormalSpell(Canonical(code)))
                    {
                        cost += SetNormalSpellCost;
                        note?.Invoke($"  (a normal spell set: -{SetNormalSpellCost:0.#})");
                    }
                    if (!_battle)
                        _sets++;
                    break;
                case MainAction.Summon or MainAction.SpSummon when code > 0:
                    _idleSummons.Add(code);
                    break;
                case MainAction.Activate:
                    _idleSummons.Remove(code);
                    if (Is(code, Allure) && DarkInHand?.Invoke() == true)
                    {
                        cost -= AllureBonus;
                        note?.Invoke($"  (Allure of Darkness with a DARK to banish: +{AllureBonus:0.#})");
                    }
                    cost += AttackLock(code, note);
                    if (!_battle && Is(code, Lyla))
                        _lyla = true;
                    // A union's activation on the field is its equip.
                    if (!_battle && Is(code, Unions) && key!.Contains($"@0.{(int)OmegaBotWrapper.Enum.CardLocation.MonsterZone}."))
                        _equips++;
                    if (Is(code, MiracleFusion))
                        _fusion = true;
                    if (Is(code, BrainControl))
                        _brainControlPending = true;
                    break;
                case MainAction.ToBattlePhase when !_battle:
                    _riskAtBattle = BattleRemovalRisk?.Invoke() ?? -1;
                    _battle = true;
                    _battleInLine = true;
                    _fusion = false;
                    // (Sets in Main Phase 1 aren't charged here any more: the brain's hold-sets-for-battle holds charge them, one rule for one
                    // circumstance; user, 2026-11-18: "why do we have two holds of set for battle phase? we should only have one".)
                    if (_lyla)
                    {
                        cost += LylaBeforeBattleCost;
                        note?.Invoke($"  (Lyla's effect in Main Phase 1, then the Battle Phase: -{LylaBeforeBattleCost:0.#})");
                    }
                    if (_equips > 0)
                    {
                        cost += EquipBeforeBattleCost * _equips;
                        note?.Invoke($"  ({_equips} union equip(s) in Main Phase 1, then the Battle Phase: -{EquipBeforeBattleCost * _equips:0.#})");
                    }
                    break;
            }
            return cost;
        }

        /// <summary>Our turn is over (ours: our side's board as it ended). Returns the cost of what never paid off.</summary>
        public double OnOurTurnEnd(SideSummary ours, SideSummary theirs, Action<string>? note)
        {
            if (_turnOver)
                return 0;
            _turnOver = true;
            double cost = 0;
            if (_battleInLine && !_attackedInLine)
            {
                cost += EmptyBattleCost;
                note?.Invoke($"  (the Battle Phase with no attack: -{EmptyBattleCost:0.##})");
            }
            // Brain Control's monster back with them, its copies all there: never tributed or used as material (a win ends the line first).
            if (_brainTarget > 0 && theirs.Monsters.Count(m => Canonical(m.Code) == _brainTarget) >= _brainTargetCopies)
            {
                cost += BrainControlUnusedCost;
                note?.Invoke($"  (Brain Control without a tribute, a Synchro or a win: -{BrainControlUnusedCost:0.#})");
            }
            if (_fusion)
            {
                cost += FusionWithoutBattleCost;
                note?.Invoke($"  (Miracle Fusion with no Battle Phase after it: -{FusionWithoutBattleCost:0.#})");
            }
            if (!_noBattlePossible)
            {
                var onField = ours.Monsters.Where(m => m.FaceUp).Select(m => Canonical(m.Code)).ToList();
                int idle = 0;
                foreach (int code in _idleSummons)
                    if (onField.Remove(code))
                        idle++;
                if (idle > 0)
                {
                    cost += IdleSummonCost * idle;
                    note?.Invoke($"  ({idle} monster(s) summoned that never attacked, used an effect or left the field: -{IdleSummonCost * idle:0.#})");
                }
            }
            return cost;
        }
    }
}
