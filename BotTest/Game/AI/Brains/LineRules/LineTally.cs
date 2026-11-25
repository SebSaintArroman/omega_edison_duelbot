using BotTest.Game.AI.Search;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The line penalties that depend on a sequence of our moves within our turn (user, 2026-11-13: rules whose hold only
    /// guessed what would happen later are checked against what the line actually does). Each line starts from the real
    /// turn's TurnRecord, so lines started later in the turn count the same history as the reused tree's older lines.
    /// <list type="number">
    /// <item>Lyla's effect in Main Phase 1, then the Battle Phase: LineRules.LylaBeforeBattleCost (weak: Lyla clearing the way for a push is
    /// better than her post-battle gamble).</item>
    /// <item>A union equip (Machina Gearframe / Peacekeeper) in Main Phase 1, then the Battle Phase: LineRules.EquipBeforeBattleCost.</item>
    /// <item>Miracle Fusion with no Battle Phase after it that turn: LineRules.FusionWithoutBattleCost.</item>
    /// <item>A monster summoned face-up that by the end of our turn never attacked, never used an effect and is still on the
    /// field: LineRules.IdleSummonCost each (no Battle Phase possible this turn, the duel's first: not charged).</item>
    /// </list>
    /// </summary>
    internal sealed class LineTally
    {


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

        /// <summary>No trap can be activated now (a face-up, unnegated Jinzo on either side, or a face-up Royal Decree).</summary>
        public Func<bool>? TrapsNegated { get; set; }

        /// <summary>Our face-up Jinzos while our face-up Cyber Dragon is out (else 0), and the Jinzos in our graveyard (set by the search).</summary>
        public Func<(int FaceUpWithCyber, int InGrave)>? OurJinzos { get; set; }

        // The doomed-Jinzo fuse (LineRules.DoomedJinzoUnfusedCost): Chimeratech offered in this line and not made yet; the fuse was open
        // at our last chance before the End Phase (graveyard Jinzos then); Returner revived a Jinzo in this line; the no-exposure holds broken.
        private bool _chimeratechSeen, _fuseOpen, _returnerRevived;
        private int _graveJinzosAtFuseChance;
        private double _exposureHolds;

        /// <summary>The line went against a brain hold (charged by the search): the Cyber Dragon exposure ones are remembered for a refund.</summary>
        public void OnHoldBroken(string rule, double cost)
        {
            if (rule == DeckRules.NoChimeratechExposure)
                _exposureHolds += cost;
        }

        /// <summary>Our last chance to fuse before the End Phase: whether Cyber Dragon + a Jinzo could be fused, and the Jinzos in the graveyard then.</summary>
        private void NoteFuseChance(bool chimeratechOffered)
        {
            if (OurJinzos?.Invoke() is not { } jinzos)
                return;
            _fuseOpen = chimeratechOffered && jinzos.FaceUpWithCyber > 0;
            _graveJinzosAtFuseChance = jinzos.InGrave;
        }


        // Our monsters' effects on offer before the Battle Phase (by card), and whether their board could remove a monster in it then.
        private readonly HashSet<int> _effectsBeforeBattle = new();
        private int _riskAtBattle = -1; // their removal-risk monsters as our Battle Phase began

        internal static readonly int[] Allure = { AllureOfDarkness };
        private static readonly int[] ChimeratechIds = { ChimeratechFortressDragon };
        private static readonly int[] JinzoIds = { Jinzo };
        private static readonly int[] ReturnerIds = { JinzoReturner };

        /// <summary>
        /// Each cost (positive) or bonus (negative) this tally adds to a line, by the rule's name: the score breakdown in the log (user,
        /// 2026-11-20: "a complete breakdown of what went into the main decision"). Null: nothing recorded.
        /// </summary>
        public Action<string, double>? Charged { get; set; }

        private double Charge(string rule, double cost)
        {
            Charged?.Invoke(rule, cost);
            return cost;
        }

        /// <summary>How many of our monsters other than this card could still attack this turn (set by the search, which sees the board).</summary>
        public Func<int, int>? OtherAttackers { get; set; }

        private double AttackLock(int code, Action<string>? note)
        {
            if (!LocksOtherAttacks(code) || OtherAttackers == null)
                return 0;
            int blocked = OtherAttackers(code);
            if (blocked <= 0)
                return 0;
            note?.Invoke($"  ({blocked} other monster(s) of ours can't attack after this effect: -{LineRules.AttackLockCost * blocked:0.#})");
            return Charge("Stops our other monsters attacking", LineRules.AttackLockCost * blocked);
        }

        private static readonly System.Text.RegularExpressions.Regex DrawText = new(
            @"\bdraw (\d|one|two|three) cards?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // A draw that only follows the card's own destruction or trip to the graveyard ("If this card is destroyed and sent to the GY:
        // Draw 1 card"): not a draw the player can activate.
        private static readonly System.Text.RegularExpressions.Regex DeathTriggerText = new(
            @"\bif this card\b[^.:;]*?\b(is|was) (destroyed|sent to)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> DrawCards = new();

        /// <summary>
        /// Whether this card's text has a draw the player activates (Pot of Greed, Upstart Goblin, Allure of Darkness...). A draw in a
        /// sentence that starts from the card's own destruction doesn't count (live 2026-11-25: Card Trooper's death draw made its mill
        /// "a draw effect on offer", so every main move after the mill paid DrawLaterCost and ending the turn beat attacking).
        /// </summary>
        internal static bool Draws(int code) => code > 0 && DrawCards.GetOrAdd(code, id =>
            SearchHost.Available && SearchHost.Cards.Get(id) is { } info &&
            System.Text.RegularExpressions.Regex.Split(info.Text ?? "", @"(?<=[.;])\s+")
                .Any(sentence => DrawText.IsMatch(sentence) && !DeathTriggerText.IsMatch(sentence)));

        internal static readonly int[] Lyla = { LylaLightswornSorceress, LylaLightswornSorceressPreErrata };
        internal static readonly int[] Unions = { MachinaGearframe, MachinaGearframePreErrata, MachinaPeacekeeper, MachinaPeacekeeperPreErrata };
        internal static readonly int[] MiracleFusion = { CardId.MiracleFusion };
        internal static readonly int[] BrainControl = { CardId.BrainControl };

        internal static readonly int[] ReturnFromTheDifferentDimension = { CardId.ReturnFromTheDifferentDimension };
        internal static readonly int[] TrapDustshoot = { CardId.TrapDustshoot };
        internal static readonly int[] JarOfGreed = { CardId.JarOfGreed, LegacyOfYataGarasu }; // and Legacy of Yata-Garasu: the same rules always (user, 2026-11-19)
        internal static readonly int[] Gorz = { GorzTheEmissaryOfDarkness };

        /// <summary>Our pick for an effect that destroys (any turn): each of our own cards picked costs LineRules.OwnDestroyCost.</summary>
        public double OnOurPick(SelectCardData pick, byte[] packet, Action<string>? note)
        {
            if (pick.SelectionHint != (int)DHint.DESTROY || packet.Length < 2)
                return 0;
            var cards = pick.SelectableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>();
            int own = 0;
            for (int i = 1; i <= packet[0] && i < packet.Length; i++)
                if (packet[i] < cards.Length && cards[packet[i]].CurrentCoordinates.Controller == 0)
                    own++;
            if (own == 0)
                return 0;
            note?.Invoke($"  ({own} of our own card(s) picked to destroy: -{LineRules.OwnDestroyCost * own:0.#})");
            return Charge("Destroys our own card", LineRules.OwnDestroyCost * own);
        }

        private bool _brainControlPending;
        private int _brainTarget, _brainTargetCopies;

        private static bool IsNormalSpell(int code) =>
            code > 0 && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x2) != 0 && (r.Type & (0x80 | 0x10000 | 0x20000 | 0x40000 | 0x80000)) == 0;

        private static bool IsTrap(int code) => code > 0 && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x4) != 0;

        /// <summary>Our chain answer on their turn: a trap fired in an eventless window (not their End Phase) costs LineRules.EarlyTrapCost.</summary>
        public double OnTheirTurnChain(SelectChainData chain, byte[] packet, bool eventless, bool attacking, Func<int, int, bool> doomed, int phase,
            Func<int>? ourFieldCards, Func<IEnumerable<int>>? ourHand, Action<string>? note)
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
                note?.Invoke($"  (Return from the Different Dimension on their turn, not about to be destroyed: -{LineRules.ReturnTheirTurnCost:0.#})");
                return Charge("Return from the Different Dimension on their turn", LineRules.ReturnTheirTurnCost);
            }
            // Jar of Greed (user, 2026-11-19): on their turn only when it's about to be destroyed, or against an attack with Jar our only card
            // and Gorz in hand (clearing the field for Gorz).
            if (Is(cards[index].ID, JarOfGreed))
            {
                var at = cards[index].CurrentCoordinates;
                if (doomed(at.Location, at.Sequence) || attacking && ourFieldCards?.Invoke() == 1 && ourHand?.Invoke().Any(code => Is(code, Gorz)) == true)
                    return 0;
                note?.Invoke($"  (Jar of Greed on their turn, not about to be destroyed nor clearing the field for Gorz: -{LineRules.JarTheirTurnCost:0.#})");
                return Charge("Jar of Greed on their turn", LineRules.JarTheirTurnCost);
            }
            // Trap Dustshoot fires at the earliest chance (its own rule and DustshootBonus): never charged for being early.
            if (!eventless || phase == (int)OmegaBotWrapper.Enum.DuelPhase.End || Is(cards[index].ID, TrapDustshoot))
                return 0;
            note?.Invoke($"  (a trap fired on their turn with nothing to answer: it could wait for the last moment: -{LineRules.EarlyTrapCost:0.#})");
            return Charge("Trap fired with nothing to answer", LineRules.EarlyTrapCost);
        }

        private int _sets, _equips;
        private bool _battle, _lyla, _fusion, _turnOver, _battleInLine, _attackedInLine;
        private readonly bool _noBattlePossible;
        private bool _normalSummonOrSetAvailable, _usedNormalSummonOrSet;
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
                case BattleCmdData battle when packet.Length >= 4 && BitConverter.ToUInt16(packet, 0) == (ushort)BattleAction.Attack:
                {
                    var attacker = SearchActions.AttackerFor(battle, BitConverter.ToUInt16(packet, 2));
                    if (attacker.ActionIndex == null)
                        return 0;
                    _attackedInLine = true;
                    _idleSummons.Remove(Canonical(attacker.ID));
                    return 0;
                }
                // Straight from the Battle Phase to the End Phase: the last chance to fuse was this (Returner may have revived its Jinzo in the battle).
                case BattleCmdData when packet.Length >= 2 && BitConverter.ToUInt16(packet, 0) == (ushort)BattleAction.ToEndPhase:
                    NoteFuseChance(_chimeratechSeen);
                    return 0;
                case SelectChainData chain when packet.Length >= 4:
                {
                    int index = BitConverter.ToInt32(packet, 0);
                    var cards = chain.SelectableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>();
                    if (index < 0 || index >= cards.Length)
                        return 0;
                    // A mandatory trigger isn't a use we chose (2026-11-24 live: Lyla's End Phase mill cleared her from the idle summons,
                    // so "summon Lyla, set, end the turn" never paid the idle charge and beat her attack and effect).
                    if (!chain.Forced)
                        _idleSummons.Remove(Canonical(cards[index].ID));
                    if (Is(cards[index].ID, ReturnerIds) && cards[index].CurrentCoordinates.Location == (byte)OmegaBotWrapper.Enum.CardLocation.Grave)
                        _returnerRevived = true;
                    // A monster's effect used in the window it gets (the priority window after its summon), not later: almost always
                    // right (user, 2026-11-15), so it wins the ties. Not Lyla (her effect waits for the Battle Phase), not a union
                    // equip (chained to Fortress's summon, a Bottomless takes both).
                    int code = cards[index].ID;
                    // Lyla's effect offered in a chain window (after her summon) counts like her idle activation.
                    if (!_battle && Is(code, Lyla) && cards[index].CurrentCoordinates is
                        { Controller: 0, Location: (byte)OmegaBotWrapper.Enum.CardLocation.MonsterZone })
                        _lyla = true;
                    double lockCost = cards[index].CurrentCoordinates.Controller == 0 ? AttackLock(code, note) : 0;
                    if (cards[index].CurrentCoordinates.Location == (byte)OmegaBotWrapper.Enum.CardLocation.MonsterZone &&
                        cards[index].CurrentCoordinates.Controller == 0 && !Is(code, Lyla) && !Is(code, Unions))
                    {
                        note?.Invoke($"  (a monster effect used in its chain window: +{LineRules.PriorityWindowBonus:0.#})");
                        return lockCost + Charge("Monster effect used in its chain window", -LineRules.PriorityWindowBonus);
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
                    if (Is(yn.EffectCard.ID, ReturnerIds))
                        _returnerRevived = true;
                    return 0;
            }
            return 0;
        }

        private double OnMainMove(IdleCmdData data, byte[] packet, Action<string>? note)
        {
            var action = (MainAction)packet[0];
            if (action is MainAction.Summon or MainAction.SetMonster)
                _usedNormalSummonOrSet = true;
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
                cost += Charge("Monster effect left until after the battle", LineRules.EffectAfterBattleCost);
                note?.Invoke($"  (a monster effect on offer before the battle used only after it, with their board able to remove it: -{LineRules.EffectAfterBattleCost:0.#})");
            }
            bool isDraw = action == MainAction.Activate && Draws(code);
            if (!isDraw && (data.ActivableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>()).Any(card => Draws(Canonical(card.ID))))
            {
                cost += Charge("Draw effect left for later", LineRules.DrawLaterCost);
                note?.Invoke($"  (a draw effect was on offer and this move came first: -{LineRules.DrawLaterCost:0.#})");
            }
            // The doomed-Jinzo fuse: each main move is a chance to make Chimeratech from Cyber Dragon + Returner's Jinzo; making it closes the chance
            // and, after Returner's revival in this line, refunds the line's Cyber Dragon exposure holds (that rule is relaxed on such a line).
            bool chimeratechOffered = (data.SpecialSummonableCards ?? Array.Empty<OmegaBotWrapper.Struct.DynamicCard>())
                .Any(card => Is(card.ID, ChimeratechIds));
            if (action == MainAction.SpSummon && Is(code, ChimeratechIds))
            {
                _chimeratechSeen = _fuseOpen = false;
                if (_returnerRevived && _exposureHolds > 0 && (OurJinzos?.Invoke().FaceUpWithCyber ?? 0) > 0)
                {
                    cost += Charge("Chimeratech from Returner's Jinzo: Cyber Dragon exposure refunded", -_exposureHolds);
                    note?.Invoke($"  (Chimeratech from Cyber Dragon + Returner's Jinzo: the Cyber Dragon exposure holds refunded, +{_exposureHolds:0.#})");
                    _exposureHolds = 0;
                }
            }
            else
            {
                _chimeratechSeen |= chimeratechOffered;
                NoteFuseChance(chimeratechOffered);
            }
            switch (action)
            {
                case MainAction.SetSpell or MainAction.SetMonster:
                    if (action == MainAction.SetSpell && IsNormalSpell(Canonical(code)))
                    {
                        cost += Charge("Normal spell set", LineRules.SetNormalSpellCost);
                        note?.Invoke($"  (a normal spell set: -{LineRules.SetNormalSpellCost:0.#})");
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
                        cost += Charge("Allure of Darkness with a DARK to banish", -LineRules.AllureBonus);
                        note?.Invoke($"  (Allure of Darkness with a DARK to banish: +{LineRules.AllureBonus:0.#})");
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
                        // Traps negated (Jinzo, Royal Decree): no trap for her to clear the way past (user, 2026-11-24).
                        bool trapsOff = TrapsNegated?.Invoke() == true;
                        double lylaCost = trapsOff ? LineRules.LylaBeforeBattleTrapsNegatedCost : LineRules.LylaBeforeBattleCost;
                        cost += Charge(trapsOff ? "Lyla's effect before the Battle Phase, traps negated" : "Lyla's effect before the Battle Phase", lylaCost);
                        note?.Invoke($"  (Lyla's effect in Main Phase 1, then the Battle Phase{(trapsOff ? ", with traps negated" : "")}: -{lylaCost:0.#})");
                    }
                    if (_equips > 0)
                    {
                        cost += Charge("Union equip before the Battle Phase", LineRules.EquipBeforeBattleCost * _equips);
                        note?.Invoke($"  ({_equips} union equip(s) in Main Phase 1, then the Battle Phase: -{LineRules.EquipBeforeBattleCost * _equips:0.#})");
                    }
                    break;
            }
            return cost;
        }

        public void NoteNormalSummonOrSetOpportunity() => _normalSummonOrSetAvailable = true;

        public double OnSkippedNormalSummon(SideSummary theirs, Action<string>? note)
        {
            if (!_normalSummonOrSetAvailable || _usedNormalSummonOrSet || theirs.Monsters.Count != 0 || LineRules.SkipNormalSummonCost <= 0)
                return 0;
            note?.Invoke($"  (skipped an available Normal Summon or monster set against an empty field: -{LineRules.SkipNormalSummonCost:0.##})");
            return Charge("Skipped Normal Summon or monster set", LineRules.SkipNormalSummonCost);
        }

        private int _dustshootsAtRoot = -1;

        /// <summary>The line reached the decision being searched: what the line-end rules count from (our Trap Dustshoots gone so far).</summary>
        public void AtRoot(SideSummary ours) => _dustshootsAtRoot = ours.GraveCodes.Count(code => Is(code, TrapDustshoot));

        /// <summary>
        /// The rules counted when the line ends. Returns the cost they add (a bonus is negative):
        ///  - RULE line.trap-dustshoot-early: each Trap Dustshoot of ours activated in the line (user, 2026-11-18: "add a rule to trap
        ///    dustshoot's effect that gets 1 point if it's activated (because it will remove a card almost always)"): the lines' unseen
        ///    hand cards of theirs are blanks, so it finds nothing there. Counted by the copies that resolved into our graveyard.
        ///  - Gladiator Beasts (decks that reward their battles): 1 for each of our Gladiator Beasts that survived attacking.
        /// </summary>
        public double AtLineEnd(SideSummary ours, IEnumerable<(int Player, int Code)> survivedAttacks, bool rewardGladiatorBattles,
            List<string>? trace)
        {
            double cost = 0;
            if (_dustshootsAtRoot >= 0 && ours.GraveCodes.Count(code => Is(code, TrapDustshoot)) - _dustshootsAtRoot is > 0 and var fired)
            {
                cost += Charge("Trap Dustshoot activated", -fired * LineRules.DustshootBonus);
                trace?.Add($"  (Trap Dustshoot activated: +{fired * LineRules.DustshootBonus:0.#})");
            }
            if (rewardGladiatorBattles)
            {
                int survivingGladiatorAttacks = survivedAttacks.Count(attack => attack.Player == 0 && CardRules.IsGladiatorBeast(attack.Code));
                if (survivingGladiatorAttacks > 0)
                    cost += Charge("Gladiator Beast survived attacking", -survivingGladiatorAttacks);
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
                cost += Charge("Battle Phase with no attack", LineRules.EmptyBattleCost);
                note?.Invoke($"  (the Battle Phase with no attack: -{LineRules.EmptyBattleCost:0.##})");
            }
            // Brain Control's monster back with them, its copies all there: never tributed or used as material (a win ends the line first).
            if (_brainTarget > 0 && theirs.Monsters.Count(m => Canonical(m.Code) == _brainTarget) >= _brainTargetCopies)
            {
                cost += Charge("Brain Control's monster not used", LineRules.BrainControlUnusedCost);
                note?.Invoke($"  (Brain Control without a tribute, a Synchro or a win: -{LineRules.BrainControlUnusedCost:0.#})");
            }
            // Returner's Jinzo destroyed in our End Phase while Cyber Dragon + it could have been fused into Chimeratech.
            if (_fuseOpen && ours.GraveCodes.Count(code => Is(code, JinzoIds)) - _graveJinzosAtFuseChance is > 0 and var unfused)
            {
                cost += Charge("Returner's Jinzo died without Chimeratech", LineRules.DoomedJinzoUnfusedCost * unfused);
                note?.Invoke($"  ({unfused} Jinzo destroyed in our End Phase, not fused with Cyber Dragon into Chimeratech: -{LineRules.DoomedJinzoUnfusedCost * unfused:0.#})");
            }
            if (_fusion)
            {
                cost += Charge("Miracle Fusion with no Battle Phase", LineRules.FusionWithoutBattleCost);
                note?.Invoke($"  (Miracle Fusion with no Battle Phase after it: -{LineRules.FusionWithoutBattleCost:0.#})");
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
                    cost += Charge("Summoned monster left idle", LineRules.IdleSummonCost * idle);
                    note?.Invoke($"  ({idle} monster(s) summoned that never attacked, used an effect or left the field: -{LineRules.IdleSummonCost * idle:0.#})");
                }
            }
            return cost;
        }
    }
}
