using BotTest.Game.AI.Brains;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Bots.MasterDuel.Machina
{
    /// <summary>
    /// Machina brain (Machina Fortress + light control; plays the Machina Control and Machina Jinzo decks). The deck's engine: mill (Ryko, Lyla, Card Trooper,
    /// Charge, Hamster -> Ryko) and Fortress coming back from the graveyard.
    ///  - Machina Fortress is summoned with its own Level as part of the 8: from hand it discards itself + one other
    ///    Machine (cheapest first: the non-Machina Machines and Peacekeeper are fodder, Cyber Dragon included, since
    ///    Overload Fusion wants it in the graveyard); from the graveyard it discards Machina Force (a one-card revive).
    ///    Several non-Fortress discards are a last resort. Fortress summons are held until needed (spend rules), like
    ///    Vayu's: the discard isn't free.
    ///  - Fortress pays off when destroyed by battle (it destroys a card, normally the one that killed it), so it crashes
    ///    into bigger monsters.
    ///  - Machina Force is never summoned (no Commander Covington): it's the revive for a Fortress in the graveyard.
    ///  - Cyber Dragon: never next to a face-up Machine (their Chimeratech Fortress Dragon); used for pressure next to a
    ///    set Hamster / Ryko, or as Fortress fodder.
    ///  - Overload Fusion is a win-on-the-spot card: only for lethal, banishing the minimum (graveyard monsters are
    ///    Pot of Avarice fuel).
    ///  - Unions (Gearframe, Peacekeeper) equip to protect a Machine (never a Fortress: it wants to die in battle).
    /// Every rule writes a "machina:" note into the duel log.
    /// </summary>
    public sealed class MachinaBotBrain : DeckBotBrain
    {
        // ------------------------------------------------------------------ tunable numbers (bot-tuning-Machina.txt, user 2026-11-16)

        [Tunable("The search's value for Jinzo in our graveyard (Machina Jinzo list: Jinzo - Returner revives it), card units.")]
        public static readonly Tuned<double> GraveJinzoValue = new(0.4);
        [Tunable("The search's setup value for Machina Force with 8+ Levels of Machine fodder ready.")]
        public static readonly Tuned<double> ForceReadySetupValue = new(1.0);
        [Tunable("Machina Fortress (or Machina Force with a Fortress to revive) in hand, in rough card points.")]
        public static readonly Tuned<double> FortressHandValue = new(3.5);
        [Tunable("Machina Force in hand with no Fortress in the graveyard (only fodder), in rough card points.")]
        public static readonly Tuned<double> ForceFodderHandValue = new(1);
        [Tunable("Machina Fortress destroyed by battle: what it gains, the killer's threat + this share of its own (it comes back).")]
        public static readonly Tuned<double> FortressDeathOwnShare = new(0.5);

        // XX-Saber Gottoms is in the Extra Deck for one card: Tytannial, Princess of Camellias (user, 2026-11-10: "it should only go
        // for gottoms if the opponent has tytannial on the field, that's the only reason it's in there"; then: "this isn't a hard
        // rule ... just make them strong suggestions"). The brain doesn't summon it otherwise; a planner line that does pays the
        // suggestion cost, and the play search may still pick it when it clearly wins.
        // Whether Future Fusion would be a finishing push at the last main-phase decision (for the planner's suggestion cost).
        private bool _futureFusionFinishes;

        private bool TytannialOut => OpponentMonsters.Any(m => m.IsFaceup() && m.IsCode(CardId.FairyCheerGirl));

        // Or to get over a monster none of ours beats, that Gottoms' 3100 does (user, 2026-11-10: "you could pick gottoms for
        // example to get over a monster with between 2600-3000 atk").
        private bool GottomsGetsOver(IEnumerable<DynamicCard> ours) =>
            OpponentMonsters.Any(m => m.IsFaceup() && m.GetRealPower() is >= 2600 and < 3100 &&
                                      !ours.Any(own => own.IsFaceup() && own.IsAttack() && own.Attack > m.GetRealPower()));

        internal override double PlanSuggestions(IReadOnlyList<Search.PlanStep> steps, int index, IReadOnlyList<string> offered, DuelSim.Engine.SideSummary theirs) =>
            base.PlanSuggestions(steps, index, offered, theirs) +
            ReturnerSuggestion(steps, index) +
            // Future Fusion outside a finishing push (see the main phase).
            (steps[index].Key.StartsWith($"{(int)MainAction.Activate}:") && KeyCardId(steps[index].Key) == CardId.FutureFusion && !_futureFusionFinishes
                ? SuggestionCost : 0) +
            (steps[index].Key.StartsWith($"{(int)MainAction.SpSummon}:") && KeyCardId(steps[index].Key) == CardId.XXSaberGottoms &&
             !theirs.Monsters.Any(m => m.FaceUp && (m.Code == CardId.FairyCheerGirl || Search.SearchHost.CanonicalId(m.Code) == CardId.FairyCheerGirl))
                ? SuggestionCost : 0);

        // A hand Fortress discards itself + one of these (cheapest first). Machina Force only if nothing else.
        private static readonly int[] FortressFodder =
        {
            CardId.Mechanicalchaser,
            CardId.CyberDragon,
            CardId.MachinaPeacekeeper,
            CardId.CyberPhoenix,
            CardId.JinzoReturner, // an ordinary discard: its trigger doesn't activate when discarded for Fortress
            CardId.CardTrooper,
            CardId.MachinaGearframe,
            CardId.Jinzo,         // first only when it sets up Returner (no Jinzo in the GY yet; see JinzoSetUp)
            CardId.MachinaFortress,
            CardId.MachinaForce
        };

        // Fodder Machines: summoned only when nothing better is on (and never the last discard a hand Fortress needs).
        private static readonly int[] HeldFodder =
        {
            CardId.Mechanicalchaser,
            CardId.CyberPhoenix,
            CardId.MachinaPeacekeeper
        };

        // Pot of Avarice: graveyard monsters to shuffle back, best first (Force and Gearframe to find Force again). Cards
        // that work from the graveyard (Plaguespreader, Fortress) can go back too, but last.
        private static readonly int[] AvariceReturn =
        {
            CardId.MachinaForce,
            CardId.MachinaGearframe,
            CardId.RykoLightswornHunter,
            CardId.SuperNimbleMegaHamster,
            CardId.LylaLightswornSorceress,
            CardId.CardTrooper,
            CardId.Mechanicalchaser,
            CardId.CyberPhoenix,
            CardId.MachinaPeacekeeper,
            CardId.GorzTheEmissaryOfDarkness,
            CardId.DDCrow,
            CardId.CyberDragon,
            CardId.PlaguespreaderZombie,
            CardId.MachinaFortress
        };

        // Overload Fusion: Machines cheapest to banish first (Force and Gearframe are Pot of Avarice's best returns, so late).
        private static readonly int[] OverloadBanish =
        {
            CardId.ChimeratechFortressDragon,
            CardId.ChimeratechOverdragon,
            CardId.Mechanicalchaser,
            CardId.CyberPhoenix,
            CardId.MachinaPeacekeeper,
            CardId.CardTrooper,
            CardId.CyberDragon,
            CardId.MachinaGearframe,
            CardId.MachinaForce
        };

        private static readonly int[] OwnTriggers =
        {
            CardId.MachinaGearframe,
            CardId.MachinaPeacekeeper,
            CardId.SuperNimbleMegaHamster,
            CardId.RykoLightswornHunter,
            // MachinaJinzo: Armageddon Knight's send, Jinzo - Returner's revival (both on summon / when sent).
            CardId.ArmageddonKnight,
            CardId.JinzoReturner
        };

        private static readonly int[] ChainCandidates =
        {
            CardId.GorzTheEmissaryOfDarkness,
            CardId.MirrorForce,
            CardId.DimensionalPrison,
            CardId.BottomlessTrapHole,
            CardId.TorrentialTribute,
            CardId.TrapDustshoot,
            CardId.DDCrow,
            CardId.MysticalSpaceTyphoon,
            CardId.CardTrooper,
            CardId.SolemnJudgment,
            CardId.CompulsoryEvacuationDevice
        };

        // Set monsters that gain when flipped or destroyed: Hamster (sets Ryko), Ryko, then the floaters, lower priority:
        // Peacekeeper (searches Gearframe when destroyed). Not Cyber Phoenix: its effects only work face-up (never set).
        private static readonly int[] ValueSets =
        {
            CardId.SuperNimbleMegaHamster, CardId.RykoLightswornHunter,
            CardId.MachinaPeacekeeper
        };

        private int _madePlayTurn = -1;
        private int _activationTurn = -1;
        private readonly Dictionary<int, int> _activationsThisTurn = new();
        // The Fortress we're summoning: (turn, from hand?, hand sequence, planned other discard).
        private (int Turn, bool FromHand, int Sequence, int FodderId)? _fortressPlan;
        // The monster our union equips to (turn, zone).
        private (int Turn, int Sequence)? _equipPlan;
        private bool _fortressInGrave;

        public MachinaBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
            // Rules under test fire only some of the time, so every roll that skips them gives a same-situation
            // comparison in the rule record (does the rule help or hurt?). None left after round 2 (see below).
            foreach (string rule in ExplorationRules)
                Rules.Set(rule, ExplorationRate);
            // Holding a single set back for Heavy Storm lost in both rounds it was tested (fired vs skipped: -3.5 points
            // over 2,500 duels, -4.2 over 2,000): off. (The many-sets Storm hold was neutral: -1.1, +1.4; it stays.)
            Rules.Set(DeckRules.HoldSetForStorm, 0);
            // Setting Jinzo - Returner is a weak play: only now and then (user, 2026-11-10).
            Rules.Set(MachinaRules.ReturnerSet, 0.3);
        }

        // Round 1 (2026-11-09, 2,500 duels, v0): hold-for-bigger-attacker (+11.4 points fired vs skipped), compulsory-on-
        // attack (+3.4) and lyla-destroy (+2.4) graduated to always firing.
        // Round 2 (2026-11-09, 2,000 duels, v2): graduated to always firing: fortress-banish-risk (+9.2), trap-dustshoot
        // (+8.2), cheap-into-face-down (+5.7), weak-dont-attack-directly (+3.0; +0.1 in round 1), crash-for-battle-payoff
        // (+1.7); chain-bait neutral (-1.3), kept as designed.
        private static readonly string[] ExplorationRules = Array.Empty<string>();

        /// <summary>Fire rate of the rules under test (see ExplorationRules).</summary>
        [Tunable("Fire rate of the rules under test (see ExplorationRules).")]
        private static readonly Tuned<double> ExplorationRate = new(0.75);

        // Version history (DuelSim records it per duel): 1 = 2026-11-09 first versioned build (after the 2,000-duel rerun
        // batch: exploration rules, Phoenix / Gearframe / Catastor / Crow / Gorz fixes, side-aware planned targets).
        // 2 = round 1 tuning. 3 = round 2 tuning (above). 4 = user's notes: Fortress from hand always
        // discards itself, Gearframe Normal Summoned before Fortress, Ryko's flip not on our own card; shared: floaters attacked.
        public override int BrainVersion => 13; // v13: Returners target different Jinzos; Future Fusion's wipe costs our turn-end field (this turn and next). v12: DAD effect with spare DARKs (main phase + priority window); Returner crash/set rules. v11: Jinzo - Returner a value set with a Jinzo in the GY; traps negated under Jinzo (shared)

        /// <summary>
        /// Play search setup: Machina Fortress in hand with other Machines to discard for it (8+ Levels in all) comes out
        /// next turn.
        /// </summary>
        // The MCTS's card values for this deck (user, 2026-11-11): a Fortress in the graveyard comes back (discarding, or Force),
        // and in the Jinzo list a Jinzo in the graveyard is what Jinzo - Returner revives.
        internal override double SearchCardBonus(int code, BotTest.Game.AI.Brains.CardPlace place) =>
            place != BotTest.Game.AI.Brains.CardPlace.Grave ? 0
            : Variant == MachinaVariant.Jinzo && Search.SearchHost.Is(code, CardId.Jinzo) ? GraveJinzoValue
            : 0;

        internal override double SearchSetupValue(DuelSim.Engine.SideSummary own)
        {
            if (!own.HandCodes.Any(code => Search.SearchHost.Is(code, CardId.MachinaFortress)))
                return 0;
            int fodderLevels = own.HandCodes.Where(code => !Search.SearchHost.Is(code, CardId.MachinaFortress) &&
                                                           (Search.SearchHost.Type(code) & 0x1) != 0 && (Search.SearchHost.Race(code) & 0x20) != 0)
                .Sum(code => Search.SearchHost.Cards.Get(code)?.Level ?? 0);
            return fodderLevels >= 8 ? ForceReadySetupValue : 0;
        }

        protected override bool HandlesStapleChains => true;
        protected override bool HandlesStapleIdle => true;
        // Jinzo - Returner set with a Jinzo in the graveyard: destroyed, it revives that Jinzo (user, 2026-11-10: setting it
        // beat summoning it face-up; it isn't a blocker, it's the Jinzo engine). Without a Jinzo there, it's only a 1400 wall.
        // Also with a Jinzo in hand: the set Returner is its tribute next turn (and, tributed, revives a Jinzo from the GY).
        // Not a strong play (user, 2026-11-10: "setting it isn't very strong so don't do that much"): rolled once per turn
        // at the ReturnerSet rule's chance (set low in the constructor).
        protected override int[] ValueSetIds => (_lastGraveJinzos > 0 || _jinzoInHand) && ReturnerSetRolled() ? ValueSetsWithReturner : ValueSets;

        private int _returnerSetTurn = -1;
        private bool _returnerSetRoll;

        private bool ReturnerSetRolled()
        {
            if (_returnerSetTurn != CurrentTurnNumber)
                (_returnerSetTurn, _returnerSetRoll) = (CurrentTurnNumber, Rule(MachinaRules.ReturnerSet));
            return _returnerSetRoll;
        }

        /// <summary>Allure of Darkness' banish (MachinaJinzo): the shared pick, with the Jinzo engine kept late: Jinzo, and Jinzo -
        /// Returner (its tribute and reviver) the very last.</summary>
        protected override double AllureBanishRank(DynamicCard card, AllureView v) =>
            v.Hand.Count(other => other.IsCode(card.ID)) > 1 ? 0
            : card.IsCode(CardId.Jinzo) ? 4
            : card.IsCode(CardId.JinzoReturner) ? 5
            : base.AllureBanishRank(card, v);

        private static readonly int[] ValueSetsWithReturner = ValueSets.Append(CardId.JinzoReturner).ToArray();

        // ------------------------------------------------------------------ Future Fusion's wipe (MachinaJinzo)

        /// <summary>Machina Jinzo only: MST may target our own Future Fusion in the Draw Phase (user, 2026-11-24). No other deck.</summary>
        internal override bool SearchMstOwnFutureFusion => Variant == MachinaVariant.Jinzo;

        // The turn our face-up Future Fusion was activated (real duel); -1 with none on our field.
        private int _futureFusionTurn = -1;
        private int _futureFusionNoted = -1;

        /// <summary>Our face-up Future Fusion: remember the turn it turned up (its activation), and on our next turn note that everything
        /// on our field is lost at the Standby Phase after it (user, 2026-11-24).</summary>
        private async Task TrackFutureFusionAsync(DecisionContext<IdleCmdData> context, int turn)
        {
            if (Variant != MachinaVariant.Jinzo)
                return;
            bool onField = (await CurrentField.GetCardsInSpellZone()).Any(card => card.CurrentCoordinates.Controller == 0 && card.IsFaceup() && card.IsCode(CardId.FutureFusion));
            if (!onField)
            {
                _futureFusionTurn = -1;
                return;
            }
            if (_futureFusionTurn < 0)
                _futureFusionTurn = turn;
            if (turn == _futureFusionTurn + 2 && _futureFusionNoted != turn)
            {
                _futureFusionNoted = turn;
                Note(context, $"Future Fusion (activated turn {_futureFusionTurn}): Chimeratech Overdragon sends all our other cards to the graveyard " +
                              "at our next Standby Phase, so every card on our field at the end of this turn is lost to the search");
            }
        }

        /// <summary>
        /// MachinaJinzo: Future Fusion's second Standby Phase summons Chimeratech Overdragon, which sends all our other cards to the graveyard
        /// (user, 2026-11-24). On the turn it's activated, and on our next turn (when the wipe is still to come), every card on our field
        /// as the turn ends is lost, each costing its value by the board scorer (user: "cost them as their value by the card scorer");
        /// Future Fusion itself doesn't count. Only while it's still on our field.
        /// </summary>
        internal override Func<int, bool>? SearchFieldLostAfterTurn(DuelSim.Engine.SideSummary ownAtTurnEnd, IReadOnlyCollection<int> activatedThisTurn)
        {
            if (Variant != MachinaVariant.Jinzo ||
                !ownAtTurnEnd.SpellCodes.Any(s => s.FaceUp && Search.SearchHost.Is(s.Code, CardId.FutureFusion)))
                return null;
            bool activatedNow = activatedThisTurn.Any(code => Search.SearchHost.Is(code, CardId.FutureFusion));
            bool wipePending = _futureFusionTurn >= 0 && CurrentTurnNumber == _futureFusionTurn + 2;
            return activatedNow || wipePending ? code => !Search.SearchHost.Is(code, CardId.FutureFusion) : null;
        }

        // ------------------------------------------------------------------ main phase

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default) =>
            CountSetUnderJinzo(context, await FlipBeforeSummonAsync(context, await DecideMainPhaseAsync(context, ct)));

        private async Task<IdleCmdResponse> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            StormHoldNoted = false;
            await RefreshBackrowOddsAsync(context);
            var state = WithoutNeverSummons(context.StateData);
            // Gottoms: the brain summons it only against Tytannial (a strong suggestion; see PlanSuggestions).
            if (!TytannialOut && !GottomsGetsOver(await GetBotMonsterCardsAsync()))
                state.SpecialSummonableCards = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(CardId.XXSaberGottoms)).ToArray();
            // Cyber Phoenix only works face-up in attack position (its draw and its protection): never set it.
            state.MonsterSetableCards = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(CardId.CyberPhoenix)).ToArray();
            // Our lasting Jinzo negates our own traps: none are set under it (one Returner brought back this turn doesn't count).
            state = WithoutTrapsUnderJinzo(context, state);
            int turn = context.Header.CurrentTurn;
            await TrackFutureFusionAsync(context, turn);

            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            OpponentMemory.Observe(enemyMonsters);
            await RefreshRewardOnDestroyAsync(enemyMonsters);

            // Future Fusion stays in the deck brains, staple or not: each deck plays it its own way (user, 2026-11-12: "other
            // decks will use future fusion very differently ... it should live exclusively in the deck brains").
            // Future Fusion: a finishing move only (user, 2026-11-10). Its first Standby Phase sends every Jinzo / Returner and
            // a Fortress or two to the graveyard for a huge push; its second summons Chimeratech Overdragon, which sends all our
            // other cards (and so itself) to the graveyard. So only when the push over the next two turns should end the game:
            // their life points within reach, or their board and hand nearly empty. Otherwise it's held (the search may still).
            // No Jinzo - Returner left in the deck: Future Fusion is never activated (user, 2026-11-24), the search included
            // (DeckIdleMoveAllowedAsync below).
            if (TryGetActivable(state, CardId.FutureFusion, out _) && !await ReturnerLeftInDeckAsync(context.Bot))
            {
                Note(context, "Future Fusion held: no Jinzo - Returner left in the deck");
                state.ActivableCards = state.ActivableCards!.Where(card => !card.IsCode(CardId.FutureFusion)).ToArray();
            }
            if (TryGetActivable(state, CardId.FutureFusion, out var futureFusion))
            {
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                int theirField = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
                int theirHand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                string? finish = oppLp <= 4000 ? $"their {oppLp} LP are within a two-turn push"
                    : theirField <= 1 && theirHand <= 2 ? $"they're nearly out of cards ({theirField} on the field, {theirHand} in hand)"
                    : null;
                // Main Phase 1 only: its materials land in the graveyard at once, and only this turn's Battle Phase uses them before
                // their turn (user, 2026-11-12: "no point activating future fusion in mp2 ever - use it in mp1").
                if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1)
                    finish = null;
                _futureFusionFinishes = finish != null;
                if (finish != null && Rule(MachinaRules.FutureFusionFinish))
                {
                    Note(context, $"activate Future Fusion as the finishing push: {finish} (Overdragon clears our own field in two turns; " +
                                  "the search counts every card on our field at the end of this turn and the next as lost)");
                    return futureFusion.MainActivateFromIndex();
                }
                state.ActivableCards = state.ActivableCards!.Where(card => !card.IsCode(CardId.FutureFusion)).ToArray();
            }

            // Dark Armed Dragon, live (exactly 3 DARKs in the GY): 2800, and its effect destroys a card per DARK banished.
            // The window closes as soon as the DARK count moves, so it goes first.
            if (TryGetSpecialSummon(state, CardId.DarkArmedDragon, out var dad) && Rule(MachinaRules.DarkArmedDragon))
            {
                Note(context, "special summon Dark Armed Dragon: exactly 3 DARKs in the graveyard (the window closes when the count moves)");
                return dad.SpecialSummon();
            }
            var enemySpells = await GetEnemySpellCardsAsync();
            var botMonsters = await GetBotMonsterCardsAsync();
            var botSpells = (await CurrentField.GetCardsInSpellZone()).ToList();
            var hand = await GetBotHandCardsAsync();

            IdleCmdResponse Played(IdleCmdResponse response)
            {
                _madePlayTurn = turn;
                return response;
            }

            // 1. Free card advantage (Charge of the Light Brigade: shared).
            if (await TryFreeCardAdvantageAsync(context, state) is IdleCmdResponse free) return Played(free);

            // 1b. Dark Armed Dragon's effect: banish a spare DARK from the graveyard, destroy one of their cards. Always with a
            // spare one (user, 2026-11-10: "this should always be used really if you have any expendable darks in grave").
            if (TryGetActivable(state, CardId.DarkArmedDragon, out var dadEffect) &&
                dadEffect.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                await DadSpareWhyAsync() is string dadWhy && Rule(MachinaRules.DadEffect))
            {
                Note(context, $"Dark Armed Dragon's effect: {dadWhy}");
                _dadActivatedTurn = turn;
                return Played(dadEffect.MainActivateFromIndex());
            }

            // 2. Overload Fusion: only to win on the spot, banishing the minimum.
            if (await TryOverloadLethalAsync(context, state, enemyMonsters, botMonsters, botSpells) is IdleCmdResponse overload) return Played(overload);

            // 3. Pot of Avarice with five monsters worth returning (no graveyard-effect cards among them).
            if (await TryAvariceAsync(context, state, noPlay: false) is IdleCmdResponse avarice) return Played(avarice);

            // 4. Backrow removal (Heavy Storm, Lyla, MST for a big play: shared).
            if (await TryBackrowRemovalAsync(context, state, enemySpells.Count,
                    botSpells.Count(card => card.ID > 0 && !card.IsCode(CardId.HeavyStorm))) is IdleCmdResponse backrow)
                return Played(backrow);
            // Shared monster staples (Smashing Ground, Brain Control; round-robin 36, 47).
            if (await TryMonsterStaplesAsync(context) is IdleCmdResponse monsterStaple)
                return Played(monsterStaple);

            // 5. Machina Fortress, held until needed.
            if (await TryFortressAsync(context, state, hand, botMonsters, enemyMonsters) is IdleCmdResponse fortress) return Played(fortress);

            // 5b. Plaguespreader + a non-Tuner into a synchro (shared), and the synchro it was summoned for.
            if (TryPlagueTargetSynchro(context, state) is IdleCmdResponse plagueSynchro) return Played(plagueSynchro);
            if (await TryPlaguespreaderSynchroAsync(context, state, enemyMonsters, botMonsters, MachinaRules.PlagueSynchro) is IdleCmdResponse plague)
                return Played(plague);
            if (await TryFieldTunerSynchroAsync(context, state, enemyMonsters, botMonsters, DeckRules.FieldTunerSynchro) is IdleCmdResponse fieldSynchro)
                return Played(fieldSynchro);

            // 5c. Brionac: discard a cheap card to bounce their best card (an Extra Deck monster goes back for good).
            if (await TryBrionacAsync(context, state, hand) is IdleCmdResponse brionac) return Played(brionac);

            // 5d. Compulsory Evacuation Device on our own Gearframe (it already searched): Normal Summon it again to search again.
            if (await TryCompulsoryGearframeAsync(context, state, botMonsters, enemyMonsters) is IdleCmdResponse reuse) return Played(reuse);

            // 6. Card Trooper: mill (shared).
            if (await TryCardTrooperAsync(context, state) is IdleCmdResponse trooper) return Played(trooper);

            // 6b. Cyber Phoenix in attack position when they likely hold Dimensional Prison: it negates traps that target
            //     one of our Machines (Fortress attacks safely).
            if (TryPhoenixAgainstDPrison(context, state, botMonsters, enemySpells) is IdleCmdResponse phoenix)
            {
                return Played(phoenix);
            }

            // 7. Cyber Dragon for pressure while a set Hamster / Ryko sets up the mills (it must come first: it needs
            //    only the opponent to control a monster).
            if (TryCyberDragon(context, state, hand) is IdleCmdResponse cyber) return Played(cyber);

            // 8. Union equips.
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase &&
                               botMonsters.Any(card => card.IsFaceup() && card.IsAttack()) && await BattleWorthEnteringAsync() == null;
            if (TryUnionEquip(context, state, botMonsters, enemyMonsters, enemySpells, battleAhead) is IdleCmdResponse equip) return Played(equip);

            // 9. Set Ryko / Hamster (after attacking) when the Normal Summon isn't going to Gearframe: its flip destroys a
            //    card and mills while Fortress / Cyber Dragon attack.
            var (flipSet, waitForSet) = await TrySetFlipAsync(context, state, enemyMonsters, battleAhead);
            if (flipSet is IdleCmdResponse setFlip)
            {
                return Played(setFlip);
            }
            var nsState = HoldFodder(context, state, hand);
            if (waitForSet)
            {
                nsState.SummonableCards = Array.Empty<DynamicCard>();
                nsState.MonsterSetableCards = Array.Empty<DynamicCard>();
            }

            // 9b. Otherwise the Normal Summon or Set (shared choice), with the last Fortress discard held.
            var summonOrSet = await ChooseNormalSummonOrSetAsync(context, nsState, enemyMonsters, enemySpells, botMonsters, hand);
            if (summonOrSet.HasValue)
            {
                return Played(summonOrSet.Value);
            }

            // 10. No play at all this turn: Pot of Avarice anyway, to find one (like Jar of Greed).
            if (_madePlayTurn != turn && await TryAvariceAsync(context, state, noPlay: true) is IdleCmdResponse dig) return Played(dig);

            // 11. Traps (shared).
            var (trapSet, trapBattleAhead, gorzLive) = await ChooseTrapSetAsync(context, state, enemyMonsters, botMonsters, hand);
            if (trapSet is IdleCmdResponse setTrap)
                return setTrap;

            // 12. Cyber Dragon next to a face-up Machine, or next to Returner's doomed Jinzo, at the end of the turn: fuse it ourselves (shared).
            if (await TryChimeratechSafetyAsync(context, state, battleAhead) is IdleCmdResponse safety)
                return safety;

            var fallbackState = state;
            fallbackState.SummonableCards = Array.Empty<DynamicCard>();
            fallbackState.MonsterSetableCards = Array.Empty<DynamicCard>();
            fallbackState.SpecialSummonableCards = Array.Empty<DynamicCard>();
            if (trapBattleAhead || gorzLive)
                fallbackState.SpellSetableCards = Array.Empty<DynamicCard>();
            return await base.OnIdleCmdAsync(context with { StateData = fallbackState }, ct);
        }

        // The fodder Machines (Mechanicalchaser, Cyber Phoenix, Peacekeeper) are summoned when nothing better is on
        // (they're lower priority: see SafeSummonPriority / ValueSets), except the last discard a Fortress in hand needs.
        private IdleCmdData HoldFodder(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            if (!hand.Any(card => card.IsCode(CardId.MachinaFortress)))
                return state;
            var fodder = hand.Where(card => IsMachine(card) && !card.IsCode(CardId.MachinaFortress, CardId.MachinaForce)).ToList();
            if (fodder.Count != 1 || !fodder[0].IsCode(HeldFodder) || !Rule(MachinaRules.HoldFodder))
                return state;
            int last = fodder[0].ID;
            Note(context, $"keep {CardNames.Get(last)} in hand: it's the only discard for the Machina Fortress in hand");
            Hold(last, MainAction.Summon, MachinaRules.HoldFodder);
            Hold(last, MainAction.SetMonster, MachinaRules.HoldFodder);
            state.SummonableCards = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(last)).ToArray();
            state.MonsterSetableCards = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(last)).ToArray();
            return state;
        }

        // ------------------------------------------------------------------ Machina Fortress

        /// <summary>
        /// Machina Fortress, from hand (discard itself + one other Machine, cheapest first, Machina Force only if nothing
        /// else) or from the graveyard (discard Machina Force). Several non-Fortress discards only to win now or not lose.
        /// Held until needed: the spend rules decide (it answers a bigger attack-position monster by crashing into it).
        /// </summary>
        private async Task<IdleCmdResponse?> TryFortressAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> hand, List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            var options = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.IsCode(CardId.MachinaFortress)).ToList();
            var fromHand = options.Where(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand).ToList();
            var fromGrave = options.Where(card => card.CurrentCoordinates.Location == (byte)CardLocation.Grave).ToList();
            if (options.Count == 0)
                return null;
            if (SummonExposesChimeratech(options[0], botMonsters) is string exposed && !await DoomedJinzoFuseAheadAsync(botMonsters) &&
                Rule(DeckRules.NoChimeratechExposure))
            {
                Note(context, $"hold Machina Fortress: {exposed}");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, DeckRules.NoChimeratechExposure);
                return null;
            }

            // Gearframe first when the Normal Summon is free: it searches another Fortress (or Force), which the Fortress
            // can then discard, instead of Gearframe itself being discarded (user rule, 2026-11-09). Fortress still comes
            // this turn, after it.
            // Registers a hold, so a search line summoning Fortress first pays for it (2026-11-24: without one the search replaced
            // the brain's Gearframe with a Fortress first, 0.34 card units better, discarding Jinzo - Returner).
            if (!NormalSummonUsed(context.Header) && TryGetSummonable(state, CardId.MachinaGearframe, out _) && Rule(MachinaRules.GearframeBeforeFortress))
            {
                Note(context, "Machina Fortress waits: Normal Summon Gearframe first (it searches; then Fortress discards the search)");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, MachinaRules.GearframeBeforeFortress);
                return null;
            }

            // MachinaJinzo: a hand Fortress discarding Jinzo (or Returner, with a Jinzo already there) sets the Jinzo engine up.
            bool jinzoInGrave = (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.Jinzo));
            var handMachines = hand.Where(IsMachine).ToList();
            DynamicCard summon = default;
            int fodder = 0;
            string plan = "";
            string rule = MachinaRules.FortressFromHand;
            if (fromHand.Count > 0)
            {
                var self = fromHand[0];
                var others = handMachines.Where(card => card.CurrentCoordinates.Sequence != self.CurrentCoordinates.Sequence).ToList();
                // Any other Machine first, then Machina Force, Gearframe last (user, 2026-11-12: "any other machine discard
                // would've been better than force ... except gearframe, force was preferable to gearframe").
                var cheap = others.Where(card => !card.IsCode(CardId.MachinaForce) && !card.IsCode(CardId.MachinaGearframe))
                    .OrderBy(card => JinzoSetUp(card, jinzoInGrave) != null ? -1 : StaplePriorityIndex(FortressFodder, card)).FirstOrDefault();
                var gearframe = others.FirstOrDefault(card => card.IsCode(CardId.MachinaGearframe));
                if (cheap.ID > 0)
                    (summon, fodder, plan) = (self, cheap.ID, $"from hand, discarding itself + {CardNames.Get(cheap.ID)}");
                else if (fromGrave.Count == 0 && others.Any(card => card.IsCode(CardId.MachinaForce)))
                    (summon, fodder, plan) = (self, CardId.MachinaForce, "from hand, discarding itself + Machina Force (no other discard)");
                else if (gearframe.ID > 0)
                    (summon, fodder, plan) = (self, gearframe.ID, "from hand, discarding itself + Machina Gearframe (no other discard)");
            }
            if (summon.ID <= 0 && fromGrave.Count > 0 && handMachines.Any(card => card.IsCode(CardId.MachinaForce)))
            {
                (summon, fodder, plan) = (fromGrave[0], CardId.MachinaForce, "from the graveyard, discarding Machina Force");
                rule = MachinaRules.FortressFromGrave;
            }
            bool lastResort = false;
            if (summon.ID <= 0)
            {
                summon = fromGrave.Concat(fromHand).First();
                plan = "discarding several Machines (last resort)";
                rule = MachinaRules.FortressLastResort;
                lastResort = true;
            }

            // Fortress answers a bigger attack-position monster by crashing into it (its trigger destroys a card). Not a
            // Catastor-type: that destroys Fortress (EARTH) by its effect, not by battle, so there's no trigger.
            var bigger = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack() && card.Attack >= 2500 && !BouncesAttacker(card) &&
                                                     !DestroysAttributeInBattle(card.ID, FortressAttribute))
                .OrderByDescending(card => card.Attack).FirstOrDefault();
            // Only when it can crash this turn: in Main Phase 2 the "answer" was a summon that never attacked (2026-11-17, the free Gearframe
            // attack: every Battle Phase line then summoned Fortress in Main Phase 2 for a crash it couldn't make, and looked worse for it).
            bool crashThisTurn = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            string? answer = bigger.ID > 0 && crashThisTurn
                ? $"it crashes into {CardNames.Get(bigger.ID)} ({bigger.Attack}) and its trigger destroys it"
                : null;
            int spares = hand.Count(card => card.IsCode(CardId.MachinaFortress)) + (await GetBotGraveyardCardsAsync()).Count(card => card.IsCode(CardId.MachinaFortress)) - 1;
            // With no attacker of ours out at all, building a board beats holding (LP and tempo: an empty field just stalls).
            // Not just whenever it can attack. But when our turn would otherwise only be a Ryko / Hamster set (no attacker
            // of ours out) and they have pressure on us (attack-position monsters putting us on a clock), Fortress comes
            // out to answer it this turn (it beats or crashes into their monster).
            bool canAttackNow = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            bool noAttackerOfOurs = !botMonsters.Any(card => card.IsFaceup() && card.IsAttack());
            // (Fortress can't answer a Catastor-type: it destroys Fortress by its effect when they battle.)
            var pressure = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack() && card.ID > 0 && !BouncesAttacker(card) &&
                                                       !DestroysAttributeInBattle(card.ID, FortressAttribute))
                .OrderByDescending(card => card.Attack).FirstOrDefault();
            string? developing = null;
            if (canAttackNow && noAttackerOfOurs && pressure.ID > 0 && await TheirClockOnUsAsync() != BoardAssessment.NoClock &&
                Rule(MachinaRules.FortressForPressure))
                developing = $"they have pressure on us ({CardNames.Get(pressure.ID)} {pressure.Attack}) and our turn would only be a flip set: Fortress answers it this turn";
            // "No attacker out" is a Main Phase 1 reason: there Fortress develops and attacks. A Main Phase 2 summon (Fortress,
            // Vayu, any spend) is a bad idea in most scenarios (user, 2026-11-13, said several times): the only time is a
            // high chance we die next turn, which usually only shows after the battle went wrong. Otherwise the usual spend
            // weighing decides (it already discounts a summon that can't attack this turn).
            else if (noAttackerOfOurs && ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main2 || await WeLoseNextTurnAsync()) &&
                     Rule(MachinaRules.FortressWhenNoBoard))
                developing = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2
                    ? "their board is likely lethal next turn through what we have: we need the body now"
                    : "we have no attacker out: building a board beats holding";
            // MachinaJinzo: the discard itself pays (Jinzo to the GY for Returner, or Returner reviving Jinzo): a little more
            // ready to summon (user, 2026-11-10).
            // Only when Fortress attacks this turn: otherwise the set-up can wait for a turn it does (user, 2026-11-12, a turn-1
            // Fortress for it: "unnecessary to summon fortress this early - falls into the wasting resources category").
            if (developing == null && canAttackNow && summon.CurrentCoordinates.Location == (byte)CardLocation.Hand && fodder != 0 &&
                hand.FirstOrDefault(card => card.IsCode(fodder)) is { ID: > 0 } discard && JinzoSetUp(discard, jinzoInGrave) is string setUp)
                developing = setUp;
            // A costly discard (Machina Force, Gearframe, or several Machines) waits while something cheaper develops instead:
            // Armageddon Knight's Normal Summon (free, and it sends a DARK for later; user, 2026-11-12: "summon armageddon
            // should be used in preference of a fortress with a costly discard"), Cyber Dragon's free special summon, or a
            // Normal Summon that beats their attacker (user, 2026-11-12: "didnt need to discard jinzo and cydra - could've just
            // summoned cydra and attacked with it"). Not when Fortress answers a big monster, or holding loses.
            bool costlyDiscard = lastResort || fodder == CardId.MachinaForce || fodder == CardId.MachinaGearframe;
            string? cheaper = null;
            bool cheaperAnswers = false; // it beats their attacker too: as good an answer, even when we would lose otherwise
            if (costlyDiscard && answer == null)
            {
                bool summonFree = !NormalSummonUsed(context.Header);
                var beater = summonFree
                    ? (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level <= 4 && (pressure.ID <= 0 || card.Attack > pressure.Attack))
                        .OrderByDescending(card => card.Attack).FirstOrDefault()
                    : default;
                if (summonFree && TryGetSummonable(state, CardId.ArmageddonKnight, out _))
                    cheaper = "Normal Summon Armageddon Knight";
                else if (TryGetSpecialSummon(state, CardId.CyberDragon, out _) && (pressure.ID <= 0 || 2100 > pressure.Attack))
                    { cheaper = "special summon Cyber Dragon (free)" + (pressure.ID > 0 ? $": it beats {CardNames.Get(pressure.ID)}" : ""); cheaperAnswers = pressure.ID > 0; }
                else if (beater.ID > 0)
                { cheaper = $"Normal Summon {CardNames.Get(beater.ID)}" + (pressure.ID > 0 ? $": it beats {CardNames.Get(pressure.ID)}" : ""); cheaperAnswers = pressure.ID > 0; }
            }
            if (cheaper != null && (cheaperAnswers || !await WeLoseNextTurnAsync()) && Rule(MachinaRules.KnightBeforeCostlyFortress))
            {
                Note(context, $"Machina Fortress waits: its discard is costly ({plan}); {cheaper} instead");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, MachinaRules.KnightBeforeCostlyFortress);
                return null;
            }
            // A less expensive answer first (user, 2026-11-24: "there should be a 'can I answer this with a less expensive option'
            // check here ... it should have decided that the gearframe in hand answered the board better than fortress and used that
            // instead. when its lifepoints are high and neither person has much card advantage on the other, it should play a little
            // more conservatively"). Seen: turn 2, 8000 LP each, 6 cards each, their board one set monster and one set card;
            // Fortress came out discarding Jinzo - Returner when Gearframe alone handled the set monster.
            // Not when Fortress answers a big monster by crashing, wins this turn, or holding loses. Tied to the generic Conservative
            // strategic state (StandingState: high LP, cards even; user, 2026-11-24: "tie that conservative play to a state").
            if (answer == null && StrategicState == TagRules.BotStrategicState.Conservative &&
                CheaperAnswer(context, state, botMonsters, enemyMonsters) is string cheapAnswer &&
                !await FortressWinsNowAsync(botMonsters, enemyMonsters) && !await WeLoseNextTurnAsync() &&
                Rule(MachinaRules.CheaperAnswerFirst))
            {
                Note(context, $"Machina Fortress waits: {cheapAnswer}, and we're playing conservatively (high LP, cards even): the cheaper answer first, Fortress kept for a threat");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, MachinaRules.CheaperAnswerFirst);
                return null;
            }
            var why = await JustifySpendAsync(context, $"Machina Fortress ({plan})", newAtk: 2500, fieldMaterials: null, removal: false,
                // allowPush false: kept for a threat or a win, not just for a clock (only an open opponent's push roll still goes).
                cardId: CardId.MachinaFortress, allowPush: false, effectAnswer: answer, refundedBy: developing, spares: Math.Max(0, spares));
            if (why == null)
                return null;
            if (lastResort && !why.Contains("lethal") && !await WeLoseNextTurnAsync())
            {
                Note(context, "hold Machina Fortress: only several non-Fortress discards would pay for it, a last resort for winning now or not losing");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, MachinaRules.FortressLastResort);
                return null;
            }
            // A banished Fortress is gone for good (3 copies, nothing gets them back): a likely Bottomless Trap Hole on the
            // summon (from the deck-list read of their set cards) is worth holding for, the more so the fewer are left,
            // unless the summon wins this turn or holding loses.
            var (banishRisk, riskWhy) = await FortressBanishRiskAsync();
            int fortressesLeft = 3 - _fortressesBanished;
            double riskLimit = fortressesLeft >= 3 ? FortressBanishRiskLimit3 : fortressesLeft == 2 ? FortressBanishRiskLimit2 : FortressBanishRiskLimit1;
            if (banishRisk >= riskLimit && !why.Contains("lethal") && !await WeLoseNextTurnAsync() && Rule(MachinaRules.FortressBanishRisk))
            {
                Note(context, $"hold Machina Fortress: {riskWhy} ({banishRisk:P0}, our limit {riskLimit:P0} with {fortressesLeft} Fortress(es) not banished): " +
                              "a banished Fortress is gone for good");
                Hold(CardId.MachinaFortress, MainAction.SpSummon, MachinaRules.FortressBanishRisk);
                return null;
            }
            if (!Rule(rule))
                return null;
            _fortressCostPicks.Clear();
            _fortressSelfPicked = false;
            _fortressPlan = (context.Header.CurrentTurn, summon.CurrentCoordinates.Location == (byte)CardLocation.Hand,
                summon.CurrentCoordinates.Sequence, fodder);
            Note(context, $"special summon Machina Fortress {plan}: {why}");
            // Summoned to crash into their monster: that attack is the plan (round-robin 1: it went into Lyla instead).
            if (answer != null && why.Contains(answer) && canAttackNow)
                CommitAttack(context.Header.CurrentTurn, CardId.MachinaFortress, bigger, $"Machina Fortress was summoned to crash into {CardNames.Get(bigger.ID)}");
            return summon.SpecialSummon();
        }

        private const int FortressAttribute = (int)CardAttribute.Earth;

        /// <summary>
        /// A less expensive answer to their whole board than Machina Fortress: a monster of ours ready to attack, or a free one to bring
        /// out (Gearframe first, then the strongest Normal Summon, then Cyber Dragon), beating each monster of theirs (a face-down
        /// one by its likely DEF). Null if there's none.
        /// </summary>
        private string? CheaperAnswer(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            int toBeat = enemyMonsters.Where(card => card.ID > 0 || !card.IsFaceup())
                .Select(card => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense)
                .DefaultIfEmpty(-1).Max();
            string theirBoard = enemyMonsters.Count == 0 ? "their empty field" : toBeat < 0 ? "their board" : $"their board (biggest {toBeat})";
            var ready = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked && EffectiveAttack(card) > toBeat)
                .OrderByDescending(EffectiveAttack).FirstOrDefault();
            if (ready.ID > 0)
                return $"our {CardNames.Get(ready.ID)} ({EffectiveAttack(ready)}) already handles {theirBoard}";
            if (!NormalSummonUsed(context.Header))
            {
                var summons = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                    .Where(card => card.ID > 0 && card.Level <= 4 && !card.HasType(CardType.Flip) && card.Attack > toBeat).ToList();
                var pick = summons.FirstOrDefault(card => card.IsCode(CardId.MachinaGearframe));
                if (pick.ID <= 0)
                    pick = summons.OrderByDescending(card => card.Attack).FirstOrDefault();
                if (pick.ID > 0)
                    return $"Normal Summon {CardNames.Get(pick.ID)} ({pick.Attack}) handles {theirBoard}";
            }
            if (TryGetSpecialSummon(state, CardId.CyberDragon, out var cyber) && cyber.Attack > toBeat)
                return $"special summon Cyber Dragon (free, {cyber.Attack}) handles {theirBoard}";
            return null;
        }

        /// <summary>Whether Machina Fortress (2500) on top of our attackers makes lethal this turn.</summary>
        private async Task<bool> FortressWinsNowAsync(List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            var after = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack).ToList();
            after.Add(2500);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            return BoardAssessment.TurnsToWin(after, BoardAssessment.Defenders(enemyMonsters, OpponentMemory), oppLp) == 1;
        }

        /// <summary>
        /// The chance a Fortress summoned now gets banished on the spot: at least one of their set cards is Bottomless Trap
        /// Hole (from the deck-list read; a flat rate without it).
        /// </summary>
        private async Task<(double Risk, string Why)> FortressBanishRiskAsync()
        {
            var set = (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup()).ToList();
            if (set.Count == 0)
                return (0, "no set cards");
            double none = set.Aggregate(1.0, (acc, card) =>
                acc * (1 - (OpponentDeck.SetZoneChance(card.CurrentCoordinates.Sequence, BottomlessTrapHole) ?? SetCardIsBottomlessChance)));
            return (1 - none, $"{set.Count} set card(s) could be Bottomless Trap Hole");
        }

        /// <summary>Bottomless chance per set card without deck-list data. Tune from logs.</summary>
        [Tunable("Bottomless chance per set card without deck-list data. Tune from logs.")]
        public double SetCardIsBottomlessChance { get; set; } = 0.08;
        /// <summary>Bottomless chance from which a Fortress summon is held, with 3 / 2 / 1 Fortress(es) not banished. Tune from logs.</summary>
        [Tunable("Bottomless chance from which a Fortress summon is held, with 3 / 2 / 1 Fortress(es) not banished. Tune from logs.")]
        public double FortressBanishRiskLimit3 { get; set; } = 0.35;
        [Tunable("")]
        public double FortressBanishRiskLimit2 { get; set; } = 0.25;
        [Tunable("")]
        public double FortressBanishRiskLimit1 { get; set; } = 0.15;

        /// <summary>
        /// Machina Fortress's cost (discard Machines with Levels totalling 8+): the plan made when deciding the summon
        /// (itself + one Machine from hand, or Machina Force for the graveyard copy), topped up cheapest-first only if short.
        /// </summary>
        public override async Task<IList<byte>?> OnSelectSumAsync(DecisionContext<SelectSumData> context, CancellationToken ct = default)
        {
            var data = context.StateData;
            var cards = data.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0 || !cards.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand) || !cards.All(IsMachine))
                return await base.OnSelectSumAsync(context, ct);
            await PrepareContextAsync(context, ct);
            int Level(DynamicCard card) => card.OpParam1 > 0 ? card.OpParam1 : card.Level;
            int need = Math.Max(1, data.Sum);
            var picks = new List<int>();
            var plan = _fortressPlan is { } p && p.Turn == context.Header.CurrentTurn ? _fortressPlan : null;
            _fortressPlan = null;
            if (plan is { } fp)
            {
                if (fp.FromHand)
                {
                    int self = Array.FindIndex(cards, card => card.IsCode(CardId.MachinaFortress) && card.CurrentCoordinates.Sequence == fp.Sequence);
                    if (self < 0)
                        self = Array.FindIndex(cards, card => card.IsCode(CardId.MachinaFortress));
                    if (self >= 0)
                        picks.Add(self);
                }
                int fodder = Enumerable.Range(0, cards.Length).FirstOrDefault(i => !picks.Contains(i) && cards[i].IsCode(fp.FodderId), -1);
                if (fodder >= 0)
                    picks.Add(fodder);
            }
            else if (Array.FindIndex(cards, card => card.IsCode(CardId.MachinaFortress)) is int handFortress and >= 0)
            {
                // No plan (the summon didn't come through TryFortressAsync): a Fortress summoned from hand always discards
                // itself + one other Machine, never several other monsters (seen 2026-11-09: three discards, no Fortress).
                Note(context, "no Fortress plan: discard a Fortress itself first");
                picks.Add(handFortress);
            }
            // Top up cheapest-first (Force and Fortress last) if the plan didn't reach the sum.
            foreach (int i in Enumerable.Range(0, cards.Length).OrderBy(i => StaplePriorityIndex(FortressFodder, cards[i])))
            {
                if (picks.Sum(x => Level(cards[x])) >= need)
                    break;
                if (!picks.Contains(i))
                    picks.Add(i);
            }
            // Drop anything not needed (the engine wants no spare card).
            foreach (int i in picks.OrderByDescending(x => StaplePriorityIndex(FortressFodder, cards[x])).ToList())
                if (picks.Sum(x => Level(cards[x])) - Level(cards[i]) >= need && picks.Count > 1)
                    picks.Remove(i);
            if (picks.Sum(x => Level(cards[x])) < need)
                return await base.OnSelectSumAsync(context, ct);
            Note(context, $"Machina Fortress discards {string.Join(" + ", picks.Select(i => $"{CardNames.Get(cards[i].ID)} (L{Level(cards[i])})"))}");
            return picks.Select(i => (byte)i).ToList();
        }

        // The Fortress cost picks made so far (select/unselect: one card per prompt).
        private readonly List<int> _fortressCostPicks = new();
        private bool _fortressSelfPicked;

        /// <summary>
        /// Machina Fortress's cost as the server asks it: select/unselect, one card per prompt ("Select/unselect 1-4 for
        /// DISCARD"), not a sum selection. The same plan as OnSelectSumAsync: Fortress itself, then the planned Machine, then
        /// the cheapest only while short; once the Levels reach 8 the prompt lets us finish (minimum 0) and we do. Seen
        /// 2026-11-10 (and before): Peacekeeper, then Card Trooper, then Gearframe discarded, never the Fortress itself.
        /// </summary>
        public override async Task<IList<byte>?> OnSelectUnselectCardAsync(DecisionContext<SelectUnselectCardData> context, CancellationToken ct = default)
        {
            var data = context.StateData;
            var cards = data.SelectableCards ?? Array.Empty<DynamicCard>();
            // Future Fusion's materials one at a time: the next wanted one, then finish (see FutureFusionMaterials).
            if ((DHint)data.SelectionHint == DHint.FMATERIAL && cards.Length > 0 &&
                cards.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Deck) && Rule(MachinaRules.FutureFusionMaterials))
            {
                var wanted = FutureFusionMaterials(cards);
                // Still short with nothing wanted left: the first card that isn't banned (Gearframe and Force stay in the deck).
                int unbanned = Array.FindIndex(cards, card => !SearchMoveRules.FutureFusionBanned(card.ID));
                int next = wanted.Count > 0 ? wanted.Min() : data.Minimum > 0 ? Math.Max(0, unbanned) : -1;
                if (next >= 0)
                {
                    Note(context, $"Future Fusion sends {CardNames.Get(cards[next].ID)}");
                    return new List<byte> { (byte)next };
                }
                return await base.OnSelectUnselectCardAsync(context, ct); // done: finish
            }
            bool fortressCost = (DHint)data.SelectionHint == DHint.DISCARD && cards.Length > 0 &&
                                cards.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand && IsMachine(card));
            if (!fortressCost)
                return await base.OnSelectUnselectCardAsync(context, ct);
            if (data.Minimum <= 0)
            {
                // Enough Levels: finish (nothing more is discarded).
                _fortressPlan = null;
                _fortressCostPicks.Clear();
                _fortressSelfPicked = false;
                return await base.OnSelectUnselectCardAsync(context, ct);
            }
            await PrepareContextAsync(context, ct);
            var plan = _fortressPlan is { } p && p.Turn == context.Header.CurrentTurn ? _fortressPlan : null;
            int pick = -1;
            string why;
            if (!_fortressSelfPicked && (plan == null || plan.Value.FromHand))
            {
                // Fortress itself first (from hand it discards itself and comes back from the graveyard).
                pick = plan is { } fp ? Array.FindIndex(cards, card => card.IsCode(CardId.MachinaFortress) && card.CurrentCoordinates.Sequence == fp.Sequence) : -1;
                if (pick < 0)
                    pick = Array.FindIndex(cards, card => card.IsCode(CardId.MachinaFortress));
                if (pick >= 0)
                    _fortressSelfPicked = true;
            }
            why = pick >= 0 ? "the Fortress itself" : "";
            if (pick < 0 && plan is { } planned && !_fortressCostPicks.Contains(planned.FodderId))
            {
                pick = Array.FindIndex(cards, card => card.IsCode(planned.FodderId));
                why = "the planned discard";
            }
            if (pick < 0)
            {
                // Short of 8 Levels: the cheapest Machine (Force and Fortress last).
                pick = Enumerable.Range(0, cards.Length).OrderBy(i => StaplePriorityIndex(FortressFodder, cards[i])).First();
                why = "cheapest, still short of 8 Levels";
            }
            _fortressCostPicks.Add(cards[pick].ID);
            Note(context, $"Machina Fortress discards {CardNames.Get(cards[pick].ID)} ({why})");
            return new List<byte> { (byte)pick };
        }

        // ------------------------------------------------------------------ Ryko / Hamster every turn

        /// <summary>
        /// A face-down Ryko (or Hamster, whose flip sets a Ryko from the deck) with the Normal Summon when it isn't going to
        /// Gearframe (the best card), while Fortress / Cyber Dragon attack. Set after the battle (Main Phase 2, or when no attack is coming); until then
        /// the Normal Summon waits for it (Wait). Not while Gorz is being kept live or into Grand Mole.
        /// </summary>
        private async Task<(IdleCmdResponse? Set, bool Wait)> TrySetFlipAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, bool battleAhead)
        {
            // Gearframe is the best card: it always gets the Normal Summon first.
            if (NormalSummonUsed(context.Header) || TryGetSummonable(state, CardId.MachinaGearframe, out _))
                return (null, false);
            var setters = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsCode(CardId.SuperNimbleMegaHamster, CardId.RykoLightswornHunter)).ToList();
            // Hamster's flip is a Special Summon: worth nothing while Kristya (or any lock) is up (user, 2026-11-13).
            if (SpecialSummonLockedBy is string lockedBy && setters.RemoveAll(card => card.IsCode(CardId.SuperNimbleMegaHamster)) > 0)
                Note(context, $"not Super-Nimble Mega Hamster: its flip summons, and {lockedBy} stops Special Summons");
            // Not our Ryko into a face-down that may be theirs: it flips first and destroys ours (user, 2026-11-13).
            // (The same bar as playing around Ryko: LikelyRyko.)
            var maybeRyko = enemyMonsters.FirstOrDefault(card => !card.IsFaceup() && card.CurrentCoordinates.Location != 0 && LikelyRyko(card));
            if (maybeRyko.CurrentCoordinates.Location != 0 && setters.RemoveAll(card => card.IsCode(CardId.RykoLightswornHunter)) > 0)
            {
                Note(context, $"not Ryko: their face-down may be their own Ryko ({FaceDownRykoChance(maybeRyko):P0}), which flips first and destroys ours");
                Hold(CardId.RykoLightswornHunter, MainAction.SetMonster, DeckRules.NoRykoIntoFaceDown);
            }
            if (setters.Count == 0 || await KeepGorzLiveAsync(context, enemyMonsters) || SetIntoBouncerHold(context) != null)
                return (null, false);
            if (await SetsWaitForMainPhase2Async(context))
                return (null, true);
            // Hamster first (its flip sets another Ryko), and always into their face-down (their Ryko would counter ours).
            var pick = setters.OrderBy(card => card.IsCode(CardId.SuperNimbleMegaHamster) ? 0 : 1).First();
            if (!Rule(MachinaRules.SetFlipEveryTurn))
                return (null, false);
            Note(context, $"set {CardNames.Get(pick.ID)}: a flip monster with the Normal Summon (no Gearframe) while Fortress / Cyber Dragon attack");
            return (pick.SetMonster(), false);
        }

        // ------------------------------------------------------------------ Brionac, Compulsory, Cyber Phoenix

        // Brionac's discard: what we lose least by (Force without a Fortress in the graveyard is just fodder).
        private int DiscardRank(DynamicCard card) =>
            card.IsCode(CardId.MachinaForce) && !_fortressInGrave ? 0
            : card.IsCode(CardId.Mechanicalchaser) ? 1
            : card.IsCode(CardId.CyberPhoenix) ? 2
            : card.IsCode(CardId.MachinaPeacekeeper) ? 3
            : card.IsCode(CardId.CyberDragon) ? 4
            : card.IsCode(CardId.PlaguespreaderZombie) ? 5
            : card.IsCode(CardId.CardTrooper) ? 6
            : card.HasType(CardType.Spell) ? 7
            : card.IsCode(CardId.MachinaGearframe) ? 9
            : card.IsCode(CardId.MachinaForce, CardId.MachinaFortress, CardId.GorzTheEmissaryOfDarkness) ? 11
            : card.HasType(CardType.Trap) ? 10
            : 8;

        // Brionac itself is the shared staple (DeckBotBrain.Brionac.cs): Machina ranks the discards and keeps its key cards
        // (rank above 7) unless the bounce is lethal.
        protected override bool BrionacMayDiscard(DynamicCard card) => DiscardRank(card) <= 7;

        protected override Task<double> BrionacKeepValueAsync(DynamicCard card) => Task.FromResult((double)DiscardRank(card));

        private async Task<IdleCmdResponse?> TryBrionacAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            var response = await TryBrionacBounceAsync(context, state, MachinaRules.BrionacBounce);
            if (response != null)
                MarkActivated(context, CardId.BrionacDragonOfTheIceBarrier);
            return response;
        }

        /// <summary>
        /// Compulsory Evacuation Device on our own face-up Gearframe (it already searched) while our Normal Summon is
        /// unused: summon it again for another search. Not while their field has an Extra Deck monster or token (keep it
        /// for that).
        /// </summary>
        private async Task<IdleCmdResponse?> TryCompulsoryGearframeAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            if (NormalSummonUsed(context.Header) || (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 ||
                !TryGetActivable(state, CardId.CompulsoryEvacuationDevice, out var compulsory))
                return null;
            var gearframe = botMonsters.FirstOrDefault(card => card.IsFaceup() && card.IsCode(CardId.MachinaGearframe));
            if (gearframe.ID <= 0 || enemyMonsters.Any(card => card.IsFaceup() && BounceRemoves(card)) ||
                !Rule(MachinaRules.CompulsoryGearframe))
                return null;
            PlanTarget(context.Header.CurrentTurn, gearframe, theirs: false);
            Note(context, "activate Compulsory Evacuation Device on our own Machina Gearframe: Normal Summon it again to search again");
            MarkActivated(context, CardId.CompulsoryEvacuationDevice);
            return compulsory.MainActivateFromIndex();
        }

        /// <summary>
        /// Cyber Phoenix face-up in attack position negates spell/trap effects that target one of our Machines, so a
        /// Fortress attacks safely into a Dimensional Prison we know they have (seen in their hand, with a set card now).
        /// </summary>
        private IdleCmdResponse? TryPhoenixAgainstDPrison(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemySpells)
        {
            if (!KnownDPrisonInOpponentHand || !enemySpells.Any(card => !card.IsFaceup()) ||
                (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase ||
                !botMonsters.Any(card => card.IsFaceup() && card.IsAttack() && IsMachine(card)) ||
                !TryGetSummonable(state, CardId.CyberPhoenix, out var phoenix) ||
                SummonExposesChimeratech(phoenix, botMonsters) != null || !Rule(MachinaRules.PhoenixAgainstDPrison))
                return null;
            Note(context, "normal summon Cyber Phoenix: they hold Dimensional Prison (seen) and have a set card; in attack position it " +
                          "negates traps that target one of our Machines, so our attack goes through");
            return phoenix.NormalSummon();
        }

        // ------------------------------------------------------------------ Overload Fusion

        /// <summary>
        /// Overload Fusion -> Chimeratech Overdragon (800 ATK per material; it attacks monsters that many times, and sends
        /// all our other cards to the graveyard): only when it wins this turn, with the fewest materials that do it
        /// (graveyard Machines are Pot of Avarice fuel, and Fortresses are never banished).
        /// </summary>
        private async Task<IdleCmdResponse?> TryOverloadLethalAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, List<DynamicCard> botSpells)
        {
            if (!TryGetActivable(state, CardId.OverloadFusion, out var overload) ||
                (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase)
                return null;
            var grave = await GetBotGraveyardCardsAsync();
            var pool = grave.Concat(botMonsters.Where(card => card.IsFaceup()))
                .Where(card => card.ID > 0 && IsMachine(card) && !card.IsCode(CardId.MachinaFortress)).ToList();
            var cyber = pool.FirstOrDefault(card => card.IsCode(CardId.CyberDragon));
            if (cyber.ID <= 0)
                return null;
            var others = pool.Where(card => !(card.IsCode(CardId.CyberDragon) && card.CurrentCoordinates.Location == cyber.CurrentCoordinates.Location &&
                                              card.CurrentCoordinates.Sequence == cyber.CurrentCoordinates.Sequence))
                .OrderBy(card => StaplePriorityIndex(OverloadBanish, card)).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var defenders = enemyMonsters.Where(card => card.ID > 0 || !card.IsFaceup()).ToList();

            // Damage Overdragon deals with n materials: direct if they have no monsters; otherwise it must destroy each
            // of their monsters (one attack each, up to n), and only attack-position ones deal damage.
            int Damage(int n)
            {
                int atk = 800 * n;
                if (defenders.Count == 0)
                    return atk;
                if (defenders.Count > n)
                    return 0;
                int total = 0;
                foreach (var d in defenders)
                {
                    int stat = !d.IsFaceup() ? OpponentMemory.FaceDownDefense(d) : d.IsAttack() ? d.Attack : d.Defense;
                    if (atk <= stat)
                        return 0;
                    if (d.IsFaceup() && d.IsAttack())
                        total += atk - d.Attack;
                }
                return total;
            }
            int materials = 0;
            for (int n = 2; n <= 1 + others.Count; n++)
                if (Damage(n) >= oppLp) { materials = n; break; }
            if (materials == 0)
                return null;
            var (through, throughWhy) = await PushGetsThroughAsync();
            if (through < 1 && !await WeLoseNextTurnAsync() && !await PushRollAsync(context.Header, through))
            {
                Note(context, $"hold Overload Fusion: Chimeratech Overdragon ({800 * materials}) would be lethal, but {throughWhy}; rolled to hold");
                Hold(CardId.OverloadFusion, MainAction.Activate, MachinaRules.OverloadForLethal);
                return null;
            }
            if (!Rule(MachinaRules.OverloadForLethal))
                return null;
            var picked = new List<DynamicCard> { cyber };
            picked.AddRange(others.Take(materials - 1));
            PlanFusionMaterials(context.Header.CurrentTurn, picked, "Overload Fusion");
            int ourLoss = botMonsters.Count(card => !picked.Any(m => m.CurrentCoordinates.Location == card.CurrentCoordinates.Location &&
                                                                  m.CurrentCoordinates.Sequence == card.CurrentCoordinates.Sequence)) +
                          botSpells.Count(card => card.ID != 0 && !card.IsCode(CardId.OverloadFusion));
            Note(context, $"activate Overload Fusion: Chimeratech Overdragon with {materials} materials ({800 * materials} ATK) is lethal " +
                          $"against {oppLp} LP; banish only {string.Join(", ", picked.Select(card => CardNames.Get(card.ID)))} " +
                          $"(it sends our {ourLoss} other card(s) to the graveyard; {throughWhy})");
            return overload.MainActivateFromIndex();
        }

        // ------------------------------------------------------------------ Pot of Avarice

        /// <summary>
        /// Pot of Avarice: draw 2 for five graveyard monsters. For value when five non-graveyard-effect monsters can go
        /// back (Force and Gearframe best); with no play this turn, anyway (graveyard-effect cards then go back last).
        /// Cyber Dragon stays while Overload Fusion is in hand.
        /// </summary>
        private async Task<IdleCmdResponse?> TryAvariceAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, bool noPlay)
        {
            if (!TryGetActivable(state, CardId.PotOfAvarice, out var avarice))
                return null;
            var picks = await AvariceReturnsAsync();
            if (picks.Count < 5)
                return null;
            bool returnsEngine = picks.Any(card => card.IsCode(CardId.MachinaFortress, CardId.PlaguespreaderZombie));
            string what = string.Join(", ", picks.Select(card => CardNames.Get(card.ID)));
            if (!noPlay && returnsEngine)
                return null;
            if (!Rule(noPlay ? MachinaRules.AvariceWhenNoPlay : MachinaRules.AvariceForValue))
                return null;
            Note(context, noPlay
                ? $"activate Pot of Avarice: no play this turn, draw 2 to find one (returns {what})"
                : $"activate Pot of Avarice: draw 2, returning {what}");
            MarkActivated(context, CardId.PotOfAvarice);
            return avarice.MainActivateFromIndex();
        }

        // The five graveyard monsters Pot of Avarice returns, best first.
        private async Task<List<DynamicCard>> AvariceReturnsAsync()
        {
            bool overloadInHand = (await GetBotHandCardsAsync()).Any(card => card.IsCode(CardId.OverloadFusion));
            int Rank(DynamicCard card) =>
                card.IsCode(CardId.CyberDragon) && overloadInHand ? AvariceReturn.Length + 1
                : card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) ? 6 // back to the Extra Deck: fine
                : StaplePriorityIndex(AvariceReturn, card);
            return (await GetBotGraveyardCardsAsync()).Where(card => card.ID > 0 && card.HasType(CardType.Monster))
                .OrderBy(Rank).Take(5).ToList();
        }

        // ------------------------------------------------------------------ Cyber Dragon, unions

        private IdleCmdResponse? TryCyberDragon(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            // (Special Summonable only while just the opponent controls a monster, so nothing of ours is out: no
            // Chimeratech exposure.) Pressure: it attacks this turn, and a Ryko / Hamster set after battle sets up the mills.
            if (!TryGetSpecialSummon(state, CardId.CyberDragon, out var cyber))
                return null;
            bool canAttackNow = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            var setter = NormalSummonUsed(context.Header) ? default : (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.IsCode(CardId.SuperNimbleMegaHamster, CardId.RykoLightswornHunter));
            if (!canAttackNow && setter.ID <= 0)
                return null;
            // The only discard for a Fortress in hand stays in hand (Fortress is the bigger attacker).
            bool fortressInHand = hand.Any(card => card.IsCode(CardId.MachinaFortress));
            int otherFodder = hand.Count(card => IsMachine(card) && !card.IsCode(CardId.MachinaFortress, CardId.MachinaForce, CardId.CyberDragon));
            if (fortressInHand && otherFodder == 0)
                return null;
            if (!Rule(MachinaRules.CyberDragonPressure))
                return null;
            Note(context, "special summon Cyber Dragon: pressure (2100)" +
                          (canAttackNow ? ", it attacks this turn" : "") +
                          (setter.ID > 0 ? $", and a set {CardNames.Get(setter.ID)} sets up the mills" : ""));
            return cyber.SpecialSummon();
        }

        /// <summary>
        /// A face-up union (Gearframe, Peacekeeper) equips to protect a Machine: our best non-Fortress one to take a Machine
        /// off the field next to our Cyber Dragon, before attacking when Mirror Force is likelier than Dimensional Prison
        /// (D-Prison takes the union too), or to protect a bigger monster through their turn. A Fortress only against
        /// likely effect destruction it gets nothing from, and not when it means to crash (see FortressEquipWhy).
        /// </summary>
        private IdleCmdResponse? TryUnionEquip(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells, bool battleAhead)
        {
            var unions = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsCode(CardId.MachinaGearframe, CardId.MachinaPeacekeeper) &&
                               card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone).ToList();
            // Our face-up Cyber Phoenix negates the effects of spells/traps that target a Machine, the union's equip
            // included (user, 2026-11-13: "never equip gearframe while you have cyber phoenix on field"). An equipped union
            // comes off instead: back as a monster it can attack, where equipped it does nothing.
            var phoenix = botMonsters.FirstOrDefault(card => card.IsFaceup() && card.IsCode(CardId.CyberPhoenix));
            if (phoenix.ID > 0)
            {
                var equipped = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                    .FirstOrDefault(card => card.IsCode(CardId.MachinaGearframe, CardId.MachinaPeacekeeper) &&
                                            card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
                if (equipped.ID > 0 && phoenix.IsAttack() && Rule(MachinaRules.UnionUnequipForPhoenix))
                {
                    Note(context, $"unequip {CardNames.Get(equipped.ID)}: Cyber Phoenix negates its protection while equipped; as a monster it can attack");
                    MarkActivated(context, equipped.ID);
                    return equipped.MainActivateFromIndex();
                }
                if (unions.Count > 0 && Rule(MachinaRules.UnionNotWithPhoenix))
                {
                    Note(context, "no union equip: our Cyber Phoenix would negate its effect while equipped");
                    foreach (var union in unions)
                        Hold(union.ID, MainAction.Activate, MachinaRules.UnionNotWithPhoenix);
                    return null;
                }
            }
            foreach (var union in unions)
            {
                // Not a monster that wants to die (Cyber Phoenix draws, Peacekeeper searches, a battle-death payoff):
                // protecting it only stops its payoff (user, 2026-11-13: "it's useless protecting an expendable card which
                // you want to get destroyed!").
                var host = botMonsters
                    .Where(card => card.ID > 0 && card.IsFaceup() && IsMachine(card) && card.CurrentCoordinates.Sequence != union.CurrentCoordinates.Sequence &&
                                   !card.IsCode(CardId.MachinaFortress, CardId.MachinaGearframe, CardId.MachinaPeacekeeper) &&
                                   !IsExpendable(card) && BattleDeathPayoff(card) == null)
                    .OrderByDescending(card => card.Attack).FirstOrDefault();
                string? why = null;
                double threat = 0; // the chance the protection matters (0: a non-protection reason)
                var without = botMonsters.Where(card => card.CurrentCoordinates.Sequence != union.CurrentCoordinates.Sequence).ToList();
                bool hasHost = host.ID > 0;
                // The Cyber Dragon safety equip waits for Main Phase 2 when there's a battle ahead: Chimeratech comes on their
                // turn, and both attack first (user, 2026-11-12: "could've done that equip in mp2, and then you would've got to
                // attack with both monsters").
                if (hasHost && battleAhead && ChimeratechExposure(botMonsters) != null && ChimeratechExposure(without) == null &&
                    union.IsAttack() && Rule(MachinaRules.UnionEquipAfterBattle))
                {
                    Note(context, $"equip {CardNames.Get(union.ID)} in Main Phase 2: it attacks first (their Chimeratech comes on their turn)");
                    Hold(union.ID, MainAction.Activate, MachinaRules.UnionEquipAfterBattle);
                    continue;
                }
                if (hasHost && ChimeratechExposure(botMonsters) is string exposure && ChimeratechExposure(without) == null && Rule(MachinaRules.UnionEquipSafety))
                    why = $"{exposure}; equipped, it's no longer a monster";
                else if (hasHost && battleAhead && host.IsAttack())
                {
                    double dPrison = DPrisonChance; // shared: per set card, seen in their hand, evidence from attacks
                    if (MirrorForceChance > 0 && MirrorForceChance > dPrison && Rule(MachinaRules.UnionEquipForMirror))
                    {
                        why = $"it protects {CardNames.Get(host.ID)} from Mirror Force ({MirrorForceChance:P0}), likelier than Dimensional Prison ({dPrison:P0}), which would take both";
                        threat = MirrorForceChance;
                    }
                }
                else if (hasHost && !battleAhead && host.Attack > union.Attack && Rule(MachinaRules.UnionEquipProtect))
                    why = $"it protects {CardNames.Get(host.ID)} ({host.Attack}) through their turn";
                if (why == null)
                {
                    // Fortress as the host: not when it means to crash this turn (the union would die instead, no pop), but
                    // against likely effect destruction it doesn't get anything from: Mirror Force, Torrential, a flipped Ryko.
                    var fortress = botMonsters.FirstOrDefault(card => card.IsFaceup() && card.IsCode(CardId.MachinaFortress));
                    if (fortress.ID > 0 && FortressEquipWhy(fortress, enemyMonsters, enemySpells, battleAhead, out double fortressThreat) is string fortressWhy &&
                        Rule(MachinaRules.UnionEquipFortress))
                        (host, why, threat) = (fortress, fortressWhy, fortressThreat);
                }
                if (why == null)
                    continue;
                if (threat > 0 && UnionEquipNotWorth(union, host, threat, botMonsters, enemyMonsters, battleAhead) is string notWorth &&
                    Rule(MachinaRules.UnionEquipWorthIt))
                {
                    Note(context, $"don't equip {CardNames.Get(union.ID)} to {CardNames.Get(host.ID)} ({why}): {notWorth}");
                    Hold(union.ID, MainAction.Activate, MachinaRules.UnionEquipWorthIt);
                    continue;
                }
                _equipPlan = (context.Header.CurrentTurn, host.CurrentCoordinates.Sequence);
                Note(context, $"equip {CardNames.Get(union.ID)} to {CardNames.Get(host.ID)}: {why}");
                MarkActivated(context, union.ID);
                return union.MainActivateFromIndex();
            }
            return null;
        }

        /// <summary>
        /// Why a union should equip to this Fortress, or null. Not when it means to crash this turn (into an attack-position
        /// monster at least as big: the union would be destroyed instead, so no pop). Otherwise against likely effect
        /// destruction, which gives Fortress nothing: Mirror Force (when it attacks), Torrential Tribute, a face-down
        /// monster that could be Ryko (flipped by our attack or their flip summon). Before attacking, only when that beats
        /// the Dimensional Prison chance (it banishes Fortress and takes the union with it).
        /// </summary>
        private string? FortressEquipWhy(DynamicCard fortress, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells, bool battleAhead,
            out double threatChance)
        {
            threatChance = 0;
            bool crashing = battleAhead && fortress.IsAttack() &&
                            enemyMonsters.Any(card => card.IsFaceup() && card.IsAttack() && card.Attack >= fortress.Attack && !BouncesAttacker(card));
            if (crashing)
                return null;
            double mirror = battleAhead && fortress.IsAttack() ? MirrorForceChance : 0;
            // Their face-downs that could be Ryko (from the deck lists their cards fit): the chance at least one is.
            double ryko = 1 - enemyMonsters.Where(card => !card.IsFaceup()).Aggregate(1.0, (acc, card) => acc * (1 - FaceDownRykoChance(card)));
            double threat = 1 - (1 - mirror) * (1 - TorrentialChance) * (1 - ryko);
            double dPrison = battleAhead && fortress.IsAttack() ? DPrisonChance : 0;
            if (threat < UnionOnFortressMinThreat || threat <= dPrison)
                return null;
            threatChance = threat;
            var parts = new List<string>();
            if (mirror > 0) parts.Add($"Mirror Force {mirror:P0}");
            if (TorrentialChance > 0) parts.Add($"Torrential {TorrentialChance:P0}");
            if (ryko > 0) parts.Add($"a face-down that could be Ryko {ryko:P0}");
            return $"protects Machina Fortress from effect destruction it gets nothing from ({string.Join(", ", parts)}; {threat:P0} in all" +
                   $"{(dPrison > 0 ? $", vs Dimensional Prison {dPrison:P0}" : "")}); it isn't crashing this turn";
        }

        /// <summary>
        /// Why a protection equip isn't worth it, or null (user, 2026-11-13: "that just reduces your total attack on field. and
        /// you have unprotected fortresses either way so you're not any safer against ryko"):
        ///  - another monster of ours like the host stays unprotected (the removal just takes that one instead);
        ///  - the union's own attack this turn is worth more than the protection (threat chance x the host, in card units).
        /// </summary>
        private string? UnionEquipNotWorth(DynamicCard union, DynamicCard host, double threat, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters, bool battleAhead)
        {
            var twin = botMonsters.FirstOrDefault(card => card.ID > 0 && card.IsFaceup() && !IsExpendable(card) &&
                                                          card.CurrentCoordinates.Sequence != host.CurrentCoordinates.Sequence &&
                                                          card.CurrentCoordinates.Sequence != union.CurrentCoordinates.Sequence &&
                                                          !card.IsCode(CardId.MachinaGearframe, CardId.MachinaPeacekeeper) &&
                                                          (card.IsCode(host.ID) || card.Attack >= host.Attack));
            if (twin.ID > 0)
                return $"our {CardNames.Get(twin.ID)} stays unprotected either way, so we're no safer (it's not our one key monster)";
            if (!battleAhead || !union.IsAttack() || union.Attacked)
                return null;
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            double attackValue = enemyMonsters.Count(card => card.ID != 0 || !card.IsFaceup()) == 0 ? union.Attack / (double)LifePointsPerCard
                : enemyMonsters.Any(card => union.Attack > Power(card)) ? 1.0 : 0;
            double protectValue = threat * host.Attack / 1000.0;
            return attackValue > protectValue
                ? $"its own attack this turn ({attackValue:0.#} card(s)) is worth more than the protection ({threat:P0} x {CardNames.Get(host.ID)} = {protectValue:0.#})"
                : null;
        }

        /// <summary>Effect-destruction chance from which a union equips to a Fortress. Tune from logs.</summary>
        [Tunable("Effect-destruction chance from which a union equips to a Fortress. Tune from logs.")]
        public double UnionOnFortressMinThreat { get; set; } = 0.15;

        // Guard against looping on an effect the engine keeps offering.
        private void MarkActivated(DecisionContext<IdleCmdData> context, int cardId)
        {
            if (_activationTurn != context.Header.CurrentTurn)
            {
                _activationTurn = context.Header.CurrentTurn;
                _activationsThisTurn.Clear();
            }
            _activationsThisTurn.TryGetValue(cardId, out int used);
            _activationsThisTurn[cardId] = used + 1;
        }

        // ------------------------------------------------------------------ summon hooks

        // Gearframe first; the fodder Machines (Mechanicalchaser, Cyber Phoenix, Peacekeeper) last: still summoned for
        // pressure when nothing better is on.
        protected override int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) =>
            card.IsCode(CardId.MachinaGearframe) ? 0 : card.IsCode(HeldFodder) ? 2 : 1;

        /// <summary>Machina Gearframe: Normal Summon it for its search (Fortress, or Force for a Fortress in the graveyard).</summary>
        protected override async Task<IdleCmdResponse?> DeckSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> developers, List<DynamicCard> hand, bool danger, int botLp)
        {
            if (developers.TryGetByID(CardId.MachinaGearframe, out var gearframe) && Rule(MachinaRules.SummonGearframe))
            {
                Note(context, $"normal summon Machina Gearframe to search a Machina (LP {botLp})");
                return gearframe.NormalSummon();
            }
            if (developers.TryGetByID(CardId.JinzoReturner, out var returner) && await ReturnerCrashTargetAsync(context, state) is { ID: > 0 } target &&
                Rule(MachinaRules.ReturnerCrash))
            {
                Note(context, $"normal summon Jinzo - Returner to crash into {CardNames.Get(target.ID)} ({target.Attack}): destroyed, it revives " +
                              $"the Jinzo in the graveyard, which (2400) then kills {CardNames.Get(target.ID)}");
                // Committed: the Battle Phase isn't skipped and the crash is made (an inconsistency otherwise; seen 2026-11-12).
                CommitAttack(context.Header.CurrentTurn, CardId.JinzoReturner, target,
                    $"Jinzo - Returner was summoned to crash into {CardNames.Get(target.ID)} and revive Jinzo");
                return returner.NormalSummon();
            }
            return null;
        }

        private int _dadActivatedTurn = -1;

        /// <summary>
        /// A DARK in our graveyard that does nothing there: everything but Plaguespreader (a synchro from the graveyard) and the
        /// last Jinzo while Jinzo - Returner can still revive it (one in hand or in the deck).
        /// </summary>
        private async Task<List<DynamicCard>> SpareGraveDarksAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            var darks = grave.Where(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0).ToList();
            int jinzos = darks.Count(card => card.IsCode(CardId.Jinzo));
            int returnersSeen = grave.Count(card => card.IsCode(CardId.JinzoReturner)) +
                                (await CurrentField.GetCardsInLocation(CardLocation.Removed)).Count(card => card.CurrentCoordinates.Controller == 0 && card.IsCode(CardId.JinzoReturner));
            bool returnerLeft = returnersSeen < 3;
            return darks.Where(card => !card.IsCode(CardId.PlaguespreaderZombie) &&
                                       !(card.IsCode(CardId.Jinzo) && returnerLeft && jinzos <= 1)).ToList();
        }

        /// <summary>Why Dark Armed Dragon's effect fires now (a spare DARK to banish and a card of theirs to destroy), or null.</summary>
        private async Task<string?> DadSpareWhyAsync()
        {
            int theirs = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
            if (theirs == 0)
                return null;
            var spare = await SpareGraveDarksAsync();
            return spare.Count == 0 ? null
                : $"{spare.Count} spare DARK(s) in the graveyard ({string.Join(", ", spare.Select(card => CardNames.Get(card.ID)).Distinct())}): " +
                  $"destroying one of their {theirs} card(s) costs nothing that matters";
        }

        /// <summary>Dark Armed Dragon's cost: the least useful spare DARK (Returner and Gorz first; a spare Jinzo after them).</summary>
        private async Task<IList<byte>?> DadBanishPickAsync(DecisionContext<SelectCardData> context)
        {
            if (_dadActivatedTurn != context.Header.CurrentTurn || (DHint)context.StateData.SelectionHint != DHint.REMOVE)
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0 || cards.Any(card => card.CurrentCoordinates.Location != (byte)CardLocation.Grave || card.CurrentCoordinates.Controller != 0))
                return null;
            var spare = await SpareGraveDarksAsync();
            int Rank(DynamicCard card) =>
                !spare.Any(s => s.IsCode(card.ID)) ? 10
                : card.IsCode(CardId.JinzoReturner, CardId.GorzTheEmissaryOfDarkness, CardId.ArmageddonKnight) ? 0
                : card.IsCode(CardId.Jinzo) ? 2
                : 1;
            var pick = cards.Select((card, i) => (Card: card, Index: i)).OrderBy(x => Rank(x.Card)).First();
            Note(context, $"Dark Armed Dragon banishes {CardNames.Get(pick.Card.ID)}");
            return new List<byte> { (byte)pick.Index };
        }

        /// <summary>
        /// The one reason to Normal Summon Jinzo - Returner (user, 2026-11-10): crash it into a face-up attack-position monster
        /// that beats it (600) but not Jinzo (2400), with a Jinzo in the graveyard for its revival, which then attacks that
        /// monster in the same Battle Phase. Main Phase 1 with a Battle Phase ahead only. Null when there's no such target.
        /// </summary>
        private async Task<DynamicCard?> ReturnerCrashTargetAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || context.Header.CurrentTurn <= 1 || _lastGraveJinzos == 0)
                return null;
            return (await GetEnemyMonsterCardsAsync())
                .Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && card.GetRealPower() > 600 && card.GetRealPower() < 2400)
                .OrderByDescending(card => card.GetRealPower())
                .Cast<DynamicCard?>()
                .FirstOrDefault();
        }

        // A planner line that Normal Summons Jinzo - Returner without crashing it into something in the same line pays the
        // suggestion cost (user, 2026-11-10: "the only time to normal returner would be to crash it into something"); setting
        // it pays half ("setting it isn't very strong so don't do that much").
        private static double ReturnerSuggestion(IReadOnlyList<Search.PlanStep> steps, int index)
        {
            string key = steps[index].Key;
            if (KeyCardId(key) != CardId.JinzoReturner)
                return 0;
            if (key.StartsWith($"{(int)MainAction.SetMonster}:"))
                return SuggestionCost / 2;
            if (!key.StartsWith($"{(int)MainAction.Summon}:"))
                return 0;
            bool crashes = steps.Skip(index + 1).Any(step => step.Key.StartsWith("attack:") && KeyCardId(step.Key) == CardId.JinzoReturner);
            return crashes ? 0 : SuggestionCost;
        }

        /// <summary>
        /// Our own ways of dealing with a big monster (so Solemn Judgment stays down): a Fortress that can crash into it
        /// (on the field, or summonable: one in hand with another Machine to discard, or one in the graveyard with Force),
        /// or Brionac on the field. Then the shared set-trap answers.
        /// </summary>
        protected override async Task<string?> OtherAnswerToAsync(DynamicCard threat)
        {
            var ours = await GetBotMonsterCardsAsync();
            if (ours.Any(card => card.IsFaceup() && card.IsCode(CardId.BrionacDragonOfTheIceBarrier)))
                return "Brionac can bounce it";
            if (threat.Attack >= 2500)
            {
                if (ours.Any(card => card.IsFaceup() && card.IsCode(CardId.MachinaFortress)))
                    return "our Machina Fortress crashes into it and its trigger destroys it";
                var hand = await GetBotHandCardsAsync();
                bool handFortress = hand.Any(card => card.IsCode(CardId.MachinaFortress)) &&
                                    hand.Count(IsMachine) >= 2;
                bool graveFortress = _fortressInGrave && hand.Any(card => card.IsCode(CardId.MachinaForce));
                if (handFortress || graveFortress)
                    return "a Machina Fortress we can summon crashes into it and its trigger destroys it";
            }
            return await base.OtherAnswerToAsync(threat);
        }

        // Equal-ATK crash trades lost games here (2,000-duel analysis, 2026-11-08): this deck's monsters are worth more
        // than a 1-for-1 (and against Vayu a crashed synchro becomes a Vayu partner). Only expendable ones trade.
        protected override bool AllowCrashTrade(DynamicCard attacker, DynamicCard target) => IsExpendable(attacker);

        // Compulsory on our own monster: Gearframe is great back in hand (Normal Summon it again for another search);
        // Fortress isn't (it needs another discard to come back).
        // Fortress back in hand needs another discard to come again, so it's worth it only when that beats losing it:
        // we have a spare Machine to discard, and the removal would otherwise leave no cheap way back (it banishes, e.g.
        // D-Prison; or it destroys and there's no Force in hand to revive it from the graveyard).
        // A banished Fortress is gone for good (3 copies, nothing gets them back): always worth saving from a banish.
        protected override bool WorthSavingToHand(DynamicCard card, bool removalBanishes) =>
            card.IsCode(CardId.MachinaGearframe) ||
            (card.IsCode(CardId.MachinaFortress)
                ? removalBanishes || _spareFodderInHand > 0 && !_forceInHand
                : base.WorthSavingToHand(card, removalBanishes));

        /// <summary>
        /// Solemn Judgment for a Fortress about to be banished (when Compulsory can't save it): a probabilistic call,
        /// likelier the fewer Fortresses we have left (each banished one is gone for good).
        /// </summary>
        protected override double SolemnToProtectChance(DynamicCard ours, bool removalBanishes)
        {
            if (!ours.IsCode(CardId.MachinaFortress) || !removalBanishes)
                return 0;
            return Math.Min(1, SolemnSavesFortressChance + SolemnPerBanishedFortress * _fortressesBanished);
        }

        /// <summary>Base chance to Solemn a banish aimed at Fortress. Tune from logs.</summary>
        [Tunable("Base chance to Solemn a banish aimed at Fortress. Tune from logs.")]
        public double SolemnSavesFortressChance { get; set; } = 0.4;
        /// <summary>
        /// Added per Fortress already banished (fewer left, each one matters more). 0.3: with two gone, the last Fortress
        /// is always protected (0.4 + 2 x 0.3 = 100%). Tune from logs.
        /// </summary>
        [Tunable("Added per Fortress already banished (fewer left, each one matters more). 0.3: with two gone, the last Fortress is always protected (0.4 + 2 x 0.3 = 100%). Tune from logs.")]
        public double SolemnPerBanishedFortress { get; set; } = 0.3;

        // Fortress destroyed by battle destroys a card (and comes back from the graveyard).
        protected override string? BattleDeathPayoff(DynamicCard card) =>
            card.IsCode(CardId.MachinaFortress)
                ? "Machina Fortress destroyed by battle destroys a card of theirs (normally the one that killed it), and comes back from the graveyard"
                : card.IsCode(CardId.JinzoReturner) && _jinzoInGrave
                    ? "Jinzo - Returner destroyed by battle revives the Jinzo in the graveyard, which can still attack"
                    : base.BattleDeathPayoff(card);

        /// <summary>
        /// Fortress: its trigger destroys the monster that killed it, and it comes back from the graveyard (half its worth
        /// isn't lost). Jinzo - Returner: the Jinzo it revives (2400, its trap lock).
        /// </summary>
        protected override double BattleDeathGain(DynamicCard card, DynamicCard killer, List<DynamicCard> theirMonsters) =>
            card.IsCode(CardId.MachinaFortress) ? TargetThreat(killer) + TargetThreat(card) * FortressDeathOwnShare
            : card.IsCode(CardId.JinzoReturner) && _jinzoInGrave ? 2900
            : base.BattleDeathGain(card, killer, theirMonsters);

        // A Jinzo in our graveyard (Jinzo - Returner revives it when destroyed by battle).
        private bool _jinzoInGrave;

        /// <summary>
        /// Expendable here: Peacekeeper (searches a union when destroyed), Cyber Phoenix (draws), and Card Trooper once a
        /// Fortress is in the graveyard (its mills matter most while we still need one there).
        /// </summary>
        protected override bool IsExpendable(DynamicCard card) =>
            base.IsExpendable(card) ||
            card.IsCode(CardId.MachinaPeacekeeper, CardId.CyberPhoenix) ||
            card.IsCode(CardId.SuperNimbleMegaHamster) && card.IsFaceup() || // flipped: its job is done (still an 1100 hit)
            card.IsCode(CardId.CardTrooper) && _fortressInGrave;

        protected override void OnFieldRefreshed(FieldInfo field)
        {
            _fortressInGrave = (field.OwnSide.Grave ?? Array.Empty<int>()).Contains(CardId.MachinaFortress);
            _jinzoInGrave = (field.OwnSide.Grave ?? Array.Empty<int>()).Any(id => id == CardId.Jinzo || id == CardId.Jinzo + 20);
            var hand = field.OwnSide.Hand ?? Array.Empty<int>();
            _forceInHand = hand.Contains(CardId.MachinaForce);
            // Machines in hand that could be discarded for a Fortress besides Fortress / Force themselves.
            _spareFodderInHand = hand.Count(id => FortressFodder.Contains(id) && id != CardId.MachinaFortress && id != CardId.MachinaForce);
            _fortressesBanished = (field.OwnSide.Banish ?? Array.Empty<int>()).Count(id => id == CardId.MachinaFortress);

            // Jinzos on our field, and those back from the GY this turn (Jinzo - Returner's revival: destroyed in our End
            // Phase, so it doesn't lock our traps for their turn).
            static bool IsJinzo(int id) => id == CardId.Jinzo || id == CardId.Jinzo + 20;
            int onField = (field.OwnSide.MonsterZones ?? Array.Empty<CardArray>()).Count(zone => zone.Cards?.Any(IsJinzo) == true);
            int inGrave = (field.OwnSide.Grave ?? Array.Empty<int>()).Count(IsJinzo);
            int turn = CurrentTurnNumber;
            if (turn != _jinzoTurn)
                (_jinzoTurn, _temporaryJinzos) = (turn, 0);
            if (onField > _lastFieldJinzos && inGrave < _lastGraveJinzos)
                _temporaryJinzos += Math.Min(onField - _lastFieldJinzos, _lastGraveJinzos - inGrave);
            _temporaryJinzos = Math.Min(_temporaryJinzos, onField);
            (_lastFieldJinzos, _lastGraveJinzos) = (onField, inGrave);
            _jinzoInHand = hand.Any(IsJinzo);
            _graveDarks = (field.OwnSide.Grave ?? Array.Empty<int>())
                .Count(id => (Search.SearchHost.Type(id) & 0x1) != 0 && Search.SearchHost.Attribute(id) == 0x20);
            _dadStuck = DadStuck(field.OwnSide.Grave ?? Array.Empty<int>());
        }

        private bool _jinzoInHand;
        private int _graveDarks;
        private bool _dadStuck;

        private int _jinzoTurn = -1, _temporaryJinzos, _lastFieldJinzos, _lastGraveJinzos;

        /// <summary>
        /// A Jinzo of ours that stays: our traps can't be activated while it's out, so setting them only exposes them. Not one
        /// Jinzo - Returner brought back this turn: it's destroyed in our End Phase, and the traps are live on their turn
        /// (user, 2026-11-10).
        /// </summary>
        private bool JinzoLocksOurTraps => _lastFieldJinzos - _temporaryJinzos > 0;

        /// <summary>
        /// MachinaJinzo only: why discarding this card (a hand Fortress' cost) sets the Jinzo engine up, or null: Jinzo, with no
        /// Jinzo in the GY yet, becomes Jinzo - Returner's revive target. (Not Returner itself: discarded for Fortress its
        /// trigger doesn't activate, user 2026-11-10; it's an ordinary discard.)
        /// </summary>
        private string? JinzoSetUp(DynamicCard discard, bool jinzoInGrave) =>
            Variant == MachinaVariant.Jinzo && discard.IsCode(CardId.Jinzo) && !jinzoInGrave
                ? "the discard puts Jinzo in the graveyard for Jinzo - Returner"
                : null;

        // A Returner-revived Jinzo is destroyed in our End Phase: it's no Chimeratech material on their turn (when every
        // Jinzo out is one of those; a lasting one can't be told apart from a revived one, so then none counts as gone).
        protected override bool GoneByTheirTurn(DynamicCard card) =>
            card.IsCode(CardId.Jinzo) && _temporaryJinzos > 0 && _temporaryJinzos >= _lastFieldJinzos;

        // Under our lasting Jinzo, traps are worth less (dead until they remove it), not nothing (user, 2026-11-10: "you can still
        // set traps ... your opponent might remove it"): one a turn at most, so the hand isn't dumped into a dead backrow.
        private IdleCmdData WithoutTrapsUnderJinzo(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (!JinzoLocksOurTraps || !(state.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.HasType(CardType.Trap)))
                return state;
            int turn = context.Header.CurrentTurn;
            int setTraps = _setUnderJinzoTurn == turn ? _setUnderJinzo : 0;
            if (setTraps < 1)
                return state;
            Note(context, "hold the other traps: our Jinzo stays on the field and negates them (one set already, for when they remove it)");
            foreach (var trap in state.SpellSetableCards!.Where(card => card.HasType(CardType.Trap)))
                Hold(trap.ID, MainAction.SetSpell, MachinaRules.TrapsUnderJinzo);
            state.SpellSetableCards = state.SpellSetableCards!.Where(card => !card.HasType(CardType.Trap)).ToArray();
            return state;
        }

        private async Task<bool> ReturnerLeftInDeckAsync(DuelBotWrapper bot) =>
            (await OwnDeckLeftAsync(bot)).Any(id => Search.SearchHost.Is(id, CardId.JinzoReturner));

        /// <summary>The search's lines: Future Fusion is never activated with no Jinzo - Returner left in the deck (user, 2026-11-24).</summary>
        protected override async Task<bool> DeckIdleMoveAllowedAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse move)
        {
            if (move.Action != MainAction.Activate ||
                !(context.StateData.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(CardId.FutureFusion) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(activation => activation.ActionIndex == move.Index)))
                return true;
            return await ReturnerLeftInDeckAsync(context.Bot);
        }

        /// <summary>
        /// Machina's Future Fusion materials for Chimeratech Overdragon.
        /// </summary>
        private static List<int> FutureFusionMaterials(IReadOnlyList<DynamicCard> cards)
        {
            var picks = new List<int>();
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].IsCode(CardId.CyberDragon, CardId.Jinzo,
                    CardId.JinzoReturner, CardId.MachinaFortress))
                    picks.Add(i);
            return picks;
        }

        // Traps set this turn while our lasting Jinzo was out (counted when the set is sent).
        private int _setUnderJinzoTurn = -1, _setUnderJinzo;

        private IdleCmdResponse CountSetUnderJinzo(DecisionContext<IdleCmdData> context, IdleCmdResponse response)
        {
            if (response.Action == MainAction.SetSpell && JinzoLocksOurTraps &&
                (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(c =>
                    c.ActionIndex != null && c.ActionIndex.Length > (int)MainAction.SetSpell && c.ActionIndex[(int)MainAction.SetSpell] == response.Index) is { ID: > 0 } set &&
                set.HasType(CardType.Trap))
            {
                if (_setUnderJinzoTurn != context.Header.CurrentTurn)
                    (_setUnderJinzoTurn, _setUnderJinzo) = (context.Header.CurrentTurn, 0);
                _setUnderJinzo++;
            }
            return response;
        }

        private int _fortressesBanished;

        private bool _forceInHand;
        private int _spareFodderInHand;

        /// <summary>Machina Force is a one-card Fortress revive with a Fortress in the graveyard, otherwise just fodder.</summary>
        protected override async Task<(double Value, string Why)> HandCardValueAsync(DynamicCard card, List<DynamicCard> botMonsters)
        {
            if (card.IsCode(CardId.MachinaForce))
                return _fortressInGrave ? (FortressHandValue,"Machina Force (revives the Fortress in the graveyard)") : (ForceFodderHandValue, "Machina Force (only fodder: no Fortress in the graveyard)");
            if (card.IsCode(CardId.MachinaFortress))
                return (FortressHandValue, "Machina Fortress");
            return await base.HandCardValueAsync(card, botMonsters);
        }

        // ------------------------------------------------------------------ chains and prompts

        public override async Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default)
        {
            // Our Goyo Guardian taking the monster it destroyed: always (seen 2026-11-10: Vayu passed on taking Card Trooper).
            if (OwnTakeControlTrigger(context) is int takeControl)
                return takeControl;
            var state = context.StateData;
            var options = state.SelectableCards ?? Array.Empty<DynamicCard>();
            if (state.Forced || options.Length == 0)
                return await base.OnSelectChainAsync(context, ct);
            await PrepareContextAsync(context, ct);
            bool botTurn = IsOwnTurn(context.Header);

            // Ryko's flip destroys a card on the field: with nothing of theirs there it would hit our own (seen 2026-11-09).
            bool theirCards = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count > 0;
            // Our own monsters' optional triggers (Gearframe / Peacekeeper searches, Hamster's flip, Ryko).
            // (Not a union's equip: Gearframe / Peacekeeper on the field offered in a chain window is their equip, not a
            // trigger. Equipping is TryUnionEquip's call, and not before attacking: seen 2026-11-12, Gearframe equipped to
            // Fortress in Main Phase 1 here and the lethal attack lost an attacker.)
            bool UnionEquip(DynamicCard card) => card.IsCode(CardId.MachinaGearframe, CardId.MachinaPeacekeeper) &&
                                                 card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone;
            int trigger = Array.FindIndex(options, card => card.IsCode(OwnTriggers) && card.CurrentCoordinates.Controller == 0 && !UnionEquip(card) &&
                                                           (theirCards || !card.IsCode(CardId.RykoLightswornHunter)));
            if (trigger >= 0)
            {
                Note(context, $"use {CardNames.Get(options[trigger].ID)}'s effect");
                return trigger;
            }
            // Dark Armed Dragon's effect in the priority window right after its summon (before they can answer it), and again
            // whenever it's offered with a spare DARK in the graveyard.
            int dadIndex = Array.FindIndex(options, card => card.IsCode(CardId.DarkArmedDragon) && card.CurrentCoordinates.Controller == 0 &&
                                                            card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (dadIndex >= 0 && theirCards && await DadSpareWhyAsync() is string dadWhy && Rule(MachinaRules.DadEffect))
            {
                Note(context, $"chain Dark Armed Dragon's effect: {dadWhy}");
                // Its first use this turn takes a monster: a Bottomless / Torrential among their sets is chained in response
                // anyway, so DAD takes two cards (user, 2026-11-12: "it should almost always target a monster the first time").
                if (_dadActivatedTurn != context.Header.CurrentTurn)
                    MonsterBeforeSetsTurn = context.Header.CurrentTurn;
                _dadActivatedTurn = context.Header.CurrentTurn;
                return dadIndex;
            }
            // Card Trooper mills in the priority window after its summon.
            if (botTurn && options.Any(card => card.IsCode(CardId.CardTrooper)) && await TrooperCanMillAsync(context) &&
                TryChainStaple(context, options, CardId.CardTrooper, "mill in the priority window", out int trooperIndex, rule: DeckRules.TrooperMill))
            {
                MarkStapleActivated(CardId.CardTrooper);
                return trooperIndex;
            }
            // A set Jar of Greed / MST / Yata chained when the opponent's removal would hit it (shared bait rule). Before the
            // candidate check below: Jar of Greed isn't a chain candidate, and it was passed while Lyla destroyed it (user,
            // 2026-11-12: "missed a jar of greed chain when it was being destroyed. this should NEVER happen").
            if (options.Any(IsOurSetCard) &&
                await TryChainBaitAsync(context) is int bait)
                return bait;
            if (!options.Any(card => card.IsCode(ChainCandidates)))
                return -1;
            // Responses to their attacks, summons and graveyard targets (shared).
            var response = await TryChainResponsesAsync(context, options, botTurn, ct);
            return response ?? -1;
        }

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            var card = context.StateData.EffectCard;
            if (card.IsCode(CardId.GorzTheEmissaryOfDarkness))
                return await GorzNowAsync(context, await GetEnemyMonsterCardsAsync()) != null;
            if (card.IsCode(CardId.BlackRoseDragon))
            {
                int enemyCards = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
                int botCards = (await GetBotMonsterCardsAsync()).Count + (await CurrentField.GetCardsInSpellZone()).Count(c => c.ID != 0);
                bool wipe = enemyCards > botCards && await DestructionNegatedAsync(context, CardId.BlackRoseDragon) == null;
                Note(context, $"Black Rose wipe? opponent {enemyCards} vs our {botCards} -> {(wipe ? "yes" : "no")}");
                return wipe;
            }
            if (card.IsCode(CardId.RykoLightswornHunter) && (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count == 0)
            {
                Note(context, "don't use Ryko's flip: they have no card on the field (it would destroy our own)");
                return false;
            }
            if (card.IsCode(CardId.MachinaGearframe, CardId.MachinaPeacekeeper, CardId.SuperNimbleMegaHamster,
                    CardId.RykoLightswornHunter, CardId.MachinaFortress))
            {
                Note(context, $"use optional effect of {CardNames.Get(card.ID)}");
                return true;
            }
            return await base.OnSelectEffectYnAsync(context, ct);
        }

        // Graveyard Jinzos (sequence) Jinzo - Returner's links already targeted in this turn and phase's chain.
        private (int Turn, int Phase, HashSet<int> Sequences) _returnerTargets = (-1, -1, new HashSet<int>());

        /// <summary>
        /// MachinaJinzo: Jinzo - Returner's revive target. Several Returners on one chain (Future Fusion sending them all at once)
        /// each target a different Jinzo (user, 2026-11-24: "they should not target a jinzo that has already been targeted"): a
        /// second link on the same Jinzo fizzles. One already targeted is picked only when no other is left.
        /// </summary>
        private IList<byte>? ReturnerTargetPick(DecisionContext<SelectCardData> context, List<(DynamicCard Card, int Index)> indexed, DHint hint)
        {
            if (Variant != MachinaVariant.Jinzo || hint != DHint.SPSUMMON || context.StateData.Maximum != 1 ||
                !indexed.All(x => x.Card.CurrentCoordinates.Controller == 0 && x.Card.CurrentCoordinates.Location == (byte)CardLocation.Grave && x.Card.IsCode(CardId.Jinzo)))
                return null;
            int turn = context.Header.CurrentTurn, phase = context.Header.CurrentPhase;
            if (_returnerTargets.Turn != turn || _returnerTargets.Phase != phase)
                _returnerTargets = (turn, phase, new HashSet<int>());
            var taken = _returnerTargets.Sequences;
            var pick = indexed.FirstOrDefault(x => !taken.Contains(x.Card.CurrentCoordinates.Sequence));
            bool fresh = pick.Card.ID > 0;
            if (!fresh)
                pick = indexed[0];
            taken.Add(pick.Card.CurrentCoordinates.Sequence);
            Note(context, $"Jinzo - Returner targets the Jinzo in GY slot {pick.Card.CurrentCoordinates.Sequence}" +
                          (taken.Count > 1 ? fresh ? " (another link already targets the other)" : " (no untargeted Jinzo left)" : ""));
            return new List<byte> { (byte)pick.Index };
        }

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            if (StapleTargets(context) is IList<byte> stapleTarget)
                return stapleTarget;
            if (await AllureBanishPickAsync(context) is IList<byte> allureBanish)
                return allureBanish;
            if (await DadBanishPickAsync(context) is IList<byte> dadBanish)
                return dadBanish;
            // Brionac's discards and targets: as planned when it was activated (shared).
            if (BrionacPick(context) is IList<byte> brionacPick)
                return brionacPick;
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int count = Math.Max(1, state.Minimum);
            var hint = (DHint)state.SelectionHint;
            if (cards.Length == 0 || count > cards.Length)
                return await base.OnSelectCardAsync(context, ct);
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            bool allOurs = indexed.All(x => x.Card.CurrentCoordinates.Controller == 0);
            bool fromDeck = indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck);

            if (SelectPlannedMaterials(context, cards, state.Minimum, state.Maximum, state.SelectionHint) is IList<byte> materials)
                return materials;

            if (ReturnerTargetPick(context, indexed, hint) is IList<byte> returnerTarget)
                return returnerTarget;

            // Union equip target: the monster chosen when deciding the equip.
            if (_equipPlan is { } equip && equip.Turn == context.Header.CurrentTurn && allOurs &&
                indexed.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && x.Card.CurrentCoordinates.Sequence == equip.Sequence))
            {
                _equipPlan = null;
                var host = indexed.First(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && x.Card.CurrentCoordinates.Sequence == equip.Sequence);
                Note(context, $"equip target: {CardNames.Get(host.Card.ID)}");
                return new List<byte> { (byte)host.Index };
            }

            // Future Fusion offers Chimeratech Overdragon's materials from the deck.
            if (hint == DHint.FMATERIAL && fromDeck && Rule(MachinaRules.FutureFusionMaterials))
            {
                var wanted = FutureFusionMaterials(indexed.Select(x => x.Card).ToList());
                var picks = indexed.Where(x => wanted.Contains(x.Index)).ToList();
                while (picks.Count < count)
                    picks.Add(indexed.Where(x => !picks.Contains(x))
                        .OrderBy(x => SearchMoveRules.FutureFusionBanned(x.Card.ID)).First());
                if (picks.Count > state.Maximum)
                    picks = picks.Take(state.Maximum).ToList();
                Note(context, $"Future Fusion sends {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            // Armageddon Knight's send (a DARK from the deck): Jinzo - Returner when a Jinzo is in the GY (it revives that Jinzo
            // straight away), otherwise Jinzo (Returner's revive target later).
            if (hint == DHint.TOGRAVE && fromDeck && indexed.Any(x => x.Card.IsCode(CardId.Jinzo, CardId.JinzoReturner)) &&
                Rule(MachinaRules.ArmageddonSend))
            {
                bool jinzoInGrave = (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.Jinzo));
                int[] order = jinzoInGrave
                    ? new[] { CardId.JinzoReturner, CardId.Jinzo }
                    : new[] { CardId.Jinzo, CardId.JinzoReturner };
                var pick = indexed.OrderBy(x => StaplePriorityIndex(order, x.Card)).First();
                Note(context, $"Armageddon Knight sends {CardNames.Get(pick.Card.ID)}" +
                              (pick.Card.IsCode(CardId.JinzoReturner) ? ": it revives the Jinzo in the graveyard" : ": Jinzo - Returner's revive target"));
                return new List<byte> { (byte)pick.Index };
            }

            // Searches from the deck: Gearframe (a Machina), Peacekeeper (a union), Charge (Ryko / Lyla).
            if (hint == DHint.ATOHAND && fromDeck)
            {
                if (indexed.Any(x => x.Card.IsCode(CardId.MachinaFortress, CardId.MachinaForce)))
                {
                    // User, 2026-11-17: "the only time you should ever search anything that's not fortress is when you already have
                    // a fortress on field or in graveyard in which case you should search force ... or two fortress already in hand,
                    // in which case still get force"; "peacekeeper is never the right search from gearframe". The other cards are
                    // held, so the search's lines pay to pick them.
                    var hand = await GetBotHandCardsAsync();
                    int fortressesInHand = hand.Count(card => card.IsCode(CardId.MachinaFortress));
                    bool fortressOnField = (await GetBotMonsterCardsAsync()).Any(card => card.IsCode(CardId.MachinaFortress));
                    bool forForce = fortressOnField || _fortressInGrave || fortressesInHand >= 2;
                    int want = forForce ? CardId.MachinaForce : CardId.MachinaFortress;
                    string why = forForce
                        ? fortressOnField ? "a Fortress is on the field" : _fortressInGrave ? "a Fortress is in the graveyard" : "two Fortresses are already in hand"
                        : "Fortress first";
                    var order = new[] { want, CardId.MachinaFortress, CardId.MachinaForce, CardId.MachinaPeacekeeper };
                    var pick = indexed.OrderBy(x => StaplePriorityIndex(order, x.Card)).First();
                    if (Rule(MachinaRules.GearframeSearch))
                        foreach (var other in indexed.Select(x => x.Card.ID).Distinct().Where(id => !pick.Card.IsCode(id)))
                            HoldPick(other, MachinaRules.GearframeSearch);
                    Note(context, $"Machina Gearframe searches {CardNames.Get(pick.Card.ID)}" + (pick.Card.IsCode(want) ? $": {why}" : ""));
                    return new List<byte> { (byte)pick.Index };
                }
                if (indexed.Any(x => x.Card.IsCode(CardId.MachinaGearframe)))
                {
                    var pick = indexed.First(x => x.Card.IsCode(CardId.MachinaGearframe));
                    Note(context, "search Machina Gearframe (it searches in turn)");
                    return new List<byte> { (byte)pick.Index };
                }
                if (await SelectLightswornSearchAsync(context) is IList<byte> search)
                    return search;
            }

            // Pot of Avarice: the five graveyard monsters planned.
            if (hint is DHint.TODECK or DHint.TARGET && allOurs && indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Grave))
            {
                var returns = await AvariceReturnsAsync();
                var picks = new List<(DynamicCard Card, int Index)>();
                foreach (var r in returns)
                {
                    var match = indexed.FirstOrDefault(x => !picks.Contains(x) && x.Card.IsCode(r.ID));
                    if (match.Card.ID > 0)
                        picks.Add(match);
                }
                foreach (var x in indexed.Where(x => !picks.Contains(x)).OrderBy(x => StaplePriorityIndex(AvariceReturn, x.Card)))
                    if (picks.Count < count)
                        picks.Add(x);
                picks = picks.Take(Math.Max(count, Math.Min(state.Maximum, 5))).ToList();
                Note(context, $"return to the deck: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            // Fortress's trigger (or our Ryko flipped in battle): the monster it battled, unless something clearly better.
            if (hint == DHint.DESTROY && (DuelPhase)context.Header.CurrentPhase is DuelPhase.Battle or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal)
            {
                var battler = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                var killer = indexed.FirstOrDefault(x => x.Card.CurrentCoordinates.Controller != 0 && battler.ID > 0 &&
                                                        x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                                                        x.Card.CurrentCoordinates.Sequence == battler.CurrentCoordinates.Sequence);
                if (killer.Card.ID > 0 && Rule(MachinaRules.FortressPopsItsKiller))
                {
                    var best = indexed.Where(x => x.Card.CurrentCoordinates.Controller != 0).OrderByDescending(x => OpponentThreat(x.Card)).First();
                    var pick = OpponentThreat(best.Card) >= OpponentThreat(killer.Card) + BetterThanKillerMargin ? best : killer;
                    Note(context, pick.Index == killer.Index
                        ? $"destroy {CardNames.Get(killer.Card.ID)}: the monster it battled"
                        : $"destroy {DescribePick(best.Card)}: clearly better than {CardNames.Get(killer.Card.ID)}, the monster it battled");
                    return new List<byte> { (byte)pick.Index };
                }
            }

            if (await SelectOpponentTargetsAsync(context) is IList<byte> removal)
                return removal;

            bool fromHand = allOurs && indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Hand);
            // Other discards, and Plaguespreader's cost (a hand card on top of the deck): what we lose least by.
            if (hint is DHint.DISCARD or DHint.TOGRAVE or DHint.TODECK && fromHand)
            {
                // The End Phase hand-limit discard: a Fortress first (user, 2026-11-13: "discarded a fortress for bringing back
                // from the grave later"): from the graveyard it still comes back with its own effect.
                bool handLimit = hint == DHint.DISCARD && (DuelPhase)context.Header.CurrentPhase == DuelPhase.End && context.Header.CurrentPlayer == 0;
                var picks = indexed.OrderBy(x => handLimit && x.Card.IsCode(CardId.MachinaFortress) ? -1 : DiscardRank(x.Card)).Take(count).ToList();
                if (handLimit && picks.Any(x => x.Card.IsCode(CardId.MachinaFortress)))
                    Note(context, "hand-limit discard: Machina Fortress (it comes back from the graveyard with its own effect)");
                Note(context, $"{hint}: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            return await base.OnSelectCardAsync(context, ct);
        }

        /// <summary>How much more threat (OpponentThreat points) another card needs before Fortress's trigger skips its killer. Tune from logs.</summary>
        [Tunable("How much more threat (OpponentThreat points) another card needs before Fortress's trigger skips its killer. Tune from logs.")]
        public int BetterThanKillerMargin { get; set; } = 1000;

        private static void Note<T>(DecisionContext<T> context, string message) => context.Bot.LogNote($"machina: {message}");
    }
}
