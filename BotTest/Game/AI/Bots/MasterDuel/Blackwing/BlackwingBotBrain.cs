using BotTest.Game.AI.Brains;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Bots.MasterDuel.Blackwing
{
    /// <summary>
    /// Blackwing deck brain (Blackwing and BlackwingHybrid, see BlackwingVariant): the decision flow and the Vayu engine (Vayu
    /// synchros, Armageddon Knight, Dark Grepher, Burial, Dark Armed Dragon), copied from the Vayu brain (no link: Vayu fixes
    /// are copied by hand), then the Blackwing plays at the end of the file. Notes from the original Vayu brain follow.
    ///
    /// Iteration 1, from the first logged duel, where the bot:
    ///  - never activated a spell or effect (Upstart Goblin, Charge, MST, Lyla),
    ///  - never set a trap,
    ///  - summoned weak monsters in attack position into a stronger opponent monster,
    ///  - never used Necro Gardna from the graveyard while being attacked directly.
    ///
    /// The Blackwing deck (aggro) started from this Vayu brain; its own plays are at the end of this file.
    /// Every rule writes a "blackwing:" note into the duel log so its effect can be checked in the next duel.
    /// Anything not handled here falls back to DeckBotBrain / DefaultBotBrain.
    /// </summary>
    public sealed class BlackwingBotBrain : DeckBotBrain
    {
        // ------------------------------------------------------------------ tunable numbers (bot-tuning-<deck>.txt, user 2026-11-16)

        [Tunable("Dark Armed Dragon in hand that is live (or can be made live), in rough card points (discard / Allure choices).")]
        public static readonly Tuned<double> DadLiveHandValue = new(7);
        [Tunable("Dark Armed Dragon in hand that can't be made live, in rough card points.")]
        public static readonly Tuned<double> DadDeadHandValue = new(0.5);
        [Tunable("The search's setup value for a face-up Card Trooper of ours (a mill engine for Vayu and Dark Armed Dragon), card units.")]
        public static readonly Tuned<double> TrooperSetupValue = new(1.0);
        [Tunable("The search's setup value for Dark Armed Dragon in hand with exactly 3 DARKs in the graveyard (card units).")]
        public static readonly Tuned<double> DadLiveSetupValue = new(1.2);
        [Tunable("The search's setup value for Dark Armed Dragon in hand with 2 DARKs in the graveyard (card units).")]
        public static readonly Tuned<double> DadNearSetupValue = new(0.4);
        [Tunable("Burial / Miracle Dig pick rank (high first): a Vayu while we have more partners than Vayus.")]
        public static readonly Tuned<double> BurialRankVayuShort = new(10);
        [Tunable("Burial / Miracle Dig pick rank: a Vayu with as many partners as Vayus.")]
        public static readonly Tuned<double> BurialRankVayuEven = new(7.5);
        [Tunable("Burial / Miracle Dig pick rank: a Vayu beyond the partners.")]
        public static readonly Tuned<double> BurialRankVayuSpare = new(4);
        [Tunable("Burial / Miracle Dig pick rank: a partner while we have more Vayus than partners.")]
        public static readonly Tuned<double> BurialRankPartnerShort = new(10);
        [Tunable("Burial / Miracle Dig pick rank: a partner with as many Vayus as partners.")]
        public static readonly Tuned<double> BurialRankPartnerEven = new(1.5);
        [Tunable("Burial / Miracle Dig pick rank: a partner beyond the Vayus (below Necro Gardna).")]
        public static readonly Tuned<double> BurialRankPartnerSpare = new(0.3);
        [Tunable("Burial / Miracle Dig pick rank: Plaguespreader with none in the graveyard.")]
        public static readonly Tuned<double> BurialRankPlague = new(6);
        [Tunable("Burial / Miracle Dig pick rank: a second Plaguespreader.")]
        public static readonly Tuned<double> BurialRankPlagueSecond = new(1);
        [Tunable("Burial / Miracle Dig pick rank: Necro Gardna with none in the graveyard.")]
        public static readonly Tuned<double> BurialRankNecro = new(2);
        [Tunable("Burial / Miracle Dig pick rank: a second Necro Gardna.")]
        public static readonly Tuned<double> BurialRankNecroSecond = new(0.5);
        [Tunable("The turn planner's cost for a recursion play (Return from the Different Dimension / Burial) that brings Vayus back.")]
        public static readonly Tuned<double> RecursionPlayCost = new(2.5);
        [Tunable("The turn planner's cost for using the Normal Summon in a play.")]
        public static readonly Tuned<double> NormalSummonPlayCost = new(0.5);

        [Tunable("The search's value for Kalut in hand (its +1400 in battle), card units.")]
        public static readonly Tuned<double> KalutHandValue = new(1.0); // user, 2026-11-19: raise it (was 0.6)
        [Tunable("The search's value for a face-up Black Whirlwind (a search per Blackwing summon), card units.")]
        public static readonly Tuned<double> WhirlwindFieldValue = new(0.8);
        [Tunable("Tribute order: a Vayu of ours.")]
        public static readonly Tuned<double> TributeVayu = new(1);
        [Tunable("Tribute order: Blizzard, Kalut or Gale (small Blackwings, done once used).")]
        public static readonly Tuned<double> TributeSmallBlackwing = new(1.5);
        [Tunable("Icarus Attack chained to save our Blackwing as its free tribute: from this chance that it's doomed anyway.")]
        public static readonly Tuned<double> IcarusDoomedChance = new(0.5);

        // Non-Tuner Blackwings that can pair with Vayu in the graveyard (Vayu L1 + partner = Synchro level):
        // Sirocco L5 -> Armed Wing, Elphin L6 / Armed Wing L6 -> Armor Master, Armor Master L7 -> Silverwind.
        private (int Turn, int Id, string? Why) _specialAfterSirocco;

        /// <summary>
        /// About to special summon a big monster from hand onto an empty field with the Normal Summon unused: Sirocco
        /// (Level 5) is tribute-free only while they control a monster and we control none, so it goes first. Then
        /// the special summon follows (it doesn't use the Normal Summon), and both attack. Sirocco dying later is a Vayu
        /// partner in the graveyard too. Returns true with the Sirocco summon.
        /// </summary>
        private bool TrySiroccoFirst(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters, int followId, string followWhy, out IdleCmdResponse response)
        {
            response = default;
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || NormalSummonUsed(context.Header) ||
                botMonsters.Count > 0 || enemyMonsters.Count == 0)
                return false;
            var sirocco = (state.SummonableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(CardId.BlackwingSiroccoTheDawn));
            if (sirocco.ID <= 0)
                return false;
            Note(context, $"normal summon Blackwing - Sirocco the Dawn first: tribute-free only while our field is empty, " +
                          $"then special summon {CardNames.Get(followId)} alongside it ({followWhy})");
            _specialAfterSirocco = (context.Header.CurrentTurn, followId, followWhy);
            response = sirocco.NormalSummon();
            return true;
        }
        protected override int[] VayuPartnerIds => VayuPartners;

        private static readonly int[] VayuPartners =
        {
            CardId.BlackwingSiroccoTheDawn,
            CardId.BlackwingElphinTheRaven,
            CardId.BlackwingArmedWing,
            CardId.BlackwingArmorMaster
        };

        // Blackwing Synchros that make sending Sirocco unnecessary when already in (or headed for) the graveyard.
        private static readonly int[] SynchroVayuPartners =
        {
            CardId.BlackwingArmedWing,
            CardId.BlackwingArmorMaster
        };

        // Cheapest cards to discard first (Necro Gardna works from the graveyard; keep DAD/Gorz).
        private static readonly int[] PreferDiscard =
        {
            CardId.NecroGardna,
            CardId.PlaguespreaderZombie,
            CardId.BlackwingVayuTheEmblemOfHonor,
            CardId.BlackwingElphinTheRaven,
            CardId.BlackwingSiroccoTheDawn,
            CardId.ChaosSorcerer,
            CardId.GorzTheEmissaryOfDarkness,
            CardId.DarkArmedDragon
        };

        // Graveyard cards to banish first as costs (DAD / Chaos Sorcerer); graveyard engines last.
        private static readonly int[] PreferBanishFromGrave =
        {
            CardId.ArmageddonKnight,
            CardId.DarkGrepher,
            CardId.CardTrooper,
            CardId.RykoLightswornHunterPreErrata,
            CardId.LylaLightswornSorceressPreErrata,
            CardId.GorzTheEmissaryOfDarkness,
            CardId.ChaosSorcerer,
            CardId.DarkArmedDragon,
            // Graveyard pieces last: partners (spare ones are easy to replace), then Necro Gardna's negate, then
            // Plaguespreader and Vayu, which make plays.
            CardId.BlackwingSiroccoTheDawn,
            CardId.BlackwingElphinTheRaven,
            CardId.NecroGardna,
            CardId.PlaguespreaderZombie,
            CardId.BlackwingVayuTheEmblemOfHonor
        };

        // Banished monsters to bring back with Burial from a Different Dimension: Vayu and its partners, then the rest.
        private static readonly int[] BurialPriority =
        {
            CardId.BlackwingVayuTheEmblemOfHonor,
            CardId.BlackwingArmorMaster,
            CardId.BlackwingArmedWing,
            CardId.BlackwingSiroccoTheDawn,
            CardId.BlackwingElphinTheRaven,
            CardId.PlaguespreaderZombie,
            CardId.NecroGardna,
            CardId.DarkArmedDragon,
            CardId.ChaosSorcerer,
            CardId.GorzTheEmissaryOfDarkness,
            CardId.DarkGrepher,
            CardId.ArmageddonKnight
        };

        // Normal Summon priority when the summon is safe.
        private static readonly int[] SummonPriority =
        {
            CardId.ArmageddonKnight,
            CardId.DarkGrepher,
            CardId.BlackwingSiroccoTheDawn,
            CardId.LylaLightswornSorceressPreErrata,
            CardId.CardTrooper,
            CardId.BlackwingVayuTheEmblemOfHonor,
            CardId.PlaguespreaderZombie
        };

        // Face-down Set priority when summoning into a stronger monster would just lose it.
        private static readonly int[] SetPriority =
        {
            CardId.RykoLightswornHunterPreErrata,
            CardId.NecroGardna,
            CardId.DarkGrepher,
            CardId.ArmageddonKnight
        };

        // Extra Deck Synchro preference.
        private static readonly int[] SynchroPriority =
        {
            CardId.GoyoGuardianPreErrata,
            CardId.ColossalFighter,
            CardId.StardustDragon,
            CardId.BlackwingSilverwindTheAscendant,
            CardId.DarkEndDragon,
            CardId.BlackwingArmorMaster,
            CardId.BrionacDragonOfTheIceBarrierPreErrata,
            CardId.BlackwingArmedWing,
            // Catastor is mostly summoned for its effect (checked before this list, in the shared synchro helpers).
            CardId.AllyOfJusticeCatastor
        };

        // Cards this brain may choose to chain; any other optional chain prompt is passed without querying the field.
        private static readonly int[] ChainCandidates =
        {
            CardId.GorzTheEmissaryOfDarkness,
            CardId.MirrorForce,
            CardId.DimensionalPrison,
            CardId.NecroGardna,
            CardId.BottomlessTrapHole,
            CardId.TorrentialTribute,
            CardId.TrapDustshoot,
            // Shared staple rules (Solemn: a big play of theirs or ours; Return from the Different Dimension in their End Phase).
            CardId.SolemnJudgment,
            CardId.ReturnFromTheDifferentDimension
        };

        private int? _ownTurnParity;
        private int _activationTurn = -1;
        private readonly Dictionary<int, int> _activationsThisTurn = new();

        public BlackwingBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
            // Holding a single set back for Heavy Storm lost in every test so far (MachinaControl, Vayu): off.
            Rules.Set(DeckRules.HoldSetForStorm, 0);
            // An aggro deck: push more, hold less (see ApplyAggroTuning).
            ApplyAggroTuning();
        }

        // ------------------------------------------------------------------ main phase

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default) =>
            // A decided strong summon first flips our face-down Ryko to pop their backrow (Bottomless / Torrential).
            await FlipBeforeSummonAsync(context, await DecideMainPhaseAsync(context, ct));

        private async Task<IdleCmdResponse> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            StormHoldNoted = false;
            RememberOwnTurn(context.Header);
            await RefreshBackrowOddsAsync(context);
            var state = context.StateData;

            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            OpponentMemory.Observe(enemyMonsters);
            // Read the opponent's monsters (floaters, attacker-bouncers like Grand Mole) before planning anything.
            await RefreshRewardOnDestroyAsync(enemyMonsters);
            var enemySpells = await GetEnemySpellCardsAsync();
            var botMonsters = await GetBotMonsterCardsAsync();
            var botSpells = (await CurrentField.GetCardsInSpellZone()).ToList();
            var hand = await GetBotHandCardsAsync();
            // Black Whirlwind's holds, whichever play the brain makes first, over every move on offer (before the summon filter).
            WhirlwindHolds(context, state, hand, botSpells);
            // No pointless small-Blackwing summons and no Blackwing sets, whichever step would pick them.
            state = await WithoutPointlessSummonsAsync(context, state, hand, botMonsters, enemyMonsters);
            context = context with { StateData = state };
            // A free Bora with no purpose this turn is held whichever play the brain makes first (user, 2026-11-18: "bora should've fallen
            // into the 'save resources' category anyway": the brain chose Reinforcement of the Army first, so the Bora rule further down
            // never ran and the search's Bora summon on turn 1 paid nothing).
            BoraPurposeHold(context, state, botMonsters, enemyMonsters);
            // Sirocco's effect with nothing to gain, whichever play the brain makes first (user, 2026-11-18: "why isn't the rule about not
            // using sirocco's effect pointlessly stopping sirocco's effect being used pointlessly?").
            if (TryGetActivable(state, CardId.BlackwingSiroccoTheDawn, out var pointlessBoost) &&
                pointlessBoost.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                SiroccoBoostPointless(botMonsters, enemyMonsters, await CurrentField.GetBotLifePoints(isOwnSide: false)))
                Hold(pointlessBoost.ID, MainAction.Activate, BlackwingRules.SiroccoBoostPointless);
            // Icarus Attack in our turn outside Main Phase 1 waits for their turn: a hold, not a ban (user, 2026-11-25: "no hard bans"; Vayu
            // could have attacked Lyla, then Icarus taken both set cards in Main Phase 2).
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 && TryGetActivable(state, CardId.IcarusAttack, out var lateIcarus) &&
                Rule(BlackwingRules.IcarusWaitsForTheirTurn))
                Hold(lateIcarus.ID, MainAction.Activate, BlackwingRules.IcarusWaitsForTheirTurn);

            // 1. Free card advantage.
            // (Shared staple rule: Upstart Goblin, Charge of the Light Brigade, Reinforcement of the Army.)
            IdleCmdResponse response;
            if (await TryFreeCardAdvantageAsync(context, state) is IdleCmdResponse free) return free;
            // Blackwing plays: Allure, Black Whirlwind, free bodies, Gale, Sirocco's boost, Icarus Attack.
            if (await TryBlackwingPlaysAsync(context, state, hand, botMonsters, enemyMonsters, enemySpells) is IdleCmdResponse blackwing) return blackwing;
            // Miracle Dig / Burial from a Different Dimension: fired only at the moment a Vayu play needs what they return
            // (step 5: no Vayu play without them, and the play they make passes the spend rules). Until then the banished
            // zone simply counts as available in the Vayu calculations. Burial is otherwise set as bait with 2+ banished
            // monsters we care about, and chained only when the opponent's removal would destroy it (shared bait rule).
            int banished = await RefreshOurBanishedAsync();
            _banishedForBait = banished;

            // 2. Backrow removal.
            // Heavy Storm pushes big plays through (or wins on card count); Lyla; MST for a big play (otherwise it is set as
            // bait for the opponent's removal, step 8). Shared staple rules.
            if (await TryBackrowRemovalAsync(context, state, enemySpells.Count,
                    botSpells.Count(card => card.ID > 0 && !card.IsCode(CardId.HeavyStorm))) is IdleCmdResponse backrow)
                return backrow;
            // Shared monster staples (Smashing Ground, Brain Control; round-robin 36, 47).
            if (await TryMonsterStaplesAsync(context) is IdleCmdResponse monsterStaple)
                return monsterStaple;

            // 2b. Lethal from several plays together (each play alone may not be lethal, so the checks below would hold them all).
            var comboLethal = await TryComboLethalAsync(context, state, botMonsters, enemyMonsters, hand.Count);
            if (comboLethal.HasValue && Rule(BlackwingRules.ComboLethal))
                return comboLethal.Value;
            // Return from the Different Dimension: the banished monsters back for a lethal attack (shared staple rule).
            if (await TryReturnDimensionLethalAsync(context, state) is IdleCmdResponse returnLethal)
                return returnLethal;

            // 2c. Dark Armed Dragon in hand but more than 3 DARKs in the graveyard: the shared trim to exactly 3.
            if (await TryDadTrimAsync(context, state, hand) is IdleCmdResponse dadTrim)
                return dadTrim;

            // 3. Big special summons from hand: resources, so only when they buy something.
            // Dark Armed Dragon live right now while something this turn will move the DARK count off 3 (a synchro
            // whose materials go to the graveyard, Plaguespreader out for one): summon it first, the window closes.
            if (TryGetSpecialSummon(state, CardId.DarkArmedDragon, out _) &&
                (PlagueForDadTurn == context.Header.CurrentTurn ||
                 PlagueTargetTurn == context.Header.CurrentTurn && PlagueTargetSynchro > 0 ||
                 botMonsters.Any(card => card.IsCode(CardId.PlaguespreaderZombie))) &&
                TrySpecialSummon(context, state, CardId.DarkArmedDragon,
                    "Dark Armed Dragon is live now (exactly 3 DARKs in the graveyard); a synchro this turn would move the count off 3, so it goes first", out response))
                return response;
            // A special summon decided before Sirocco went down first (see TrySiroccoFirst): follow through.
            if (_specialAfterSirocco.Turn == context.Header.CurrentTurn && TryGetSpecialSummon(state, _specialAfterSirocco.Id, out _))
            {
                var (_, id, why) = _specialAfterSirocco;
                _specialAfterSirocco = default;
                if (TrySpecialSummon(context, state, id, $"{why} (after Sirocco)", out response))
                    return response;
            }
            if (TryGetSpecialSummon(state, CardId.DarkArmedDragon, out _) &&
                await JustifySpendAsync(context, "Dark Armed Dragon", newAtk: 2800, fieldMaterials: null, removal: true, cardId: CardId.DarkArmedDragon) is string dadWhy && Rule(BlackwingRules.DadSummon) &&
                (TrySiroccoFirst(context, state, botMonsters, enemyMonsters, CardId.DarkArmedDragon, dadWhy, out response) ||
                 TrySpecialSummon(context, state, CardId.DarkArmedDragon, dadWhy, out response)))
                return response;
            if (TryGetSpecialSummon(state, CardId.ChaosSorcerer, out _) &&
                await ChaosSorcererNeededAsync(context) is string chaosNeed &&
                await JustifySpendAsync(context, $"Chaos Sorcerer ({chaosNeed})", newAtk: 2300, fieldMaterials: null, removal: true, cardId: CardId.ChaosSorcerer) is string chaosWhy && Rule(BlackwingRules.ChaosSummon) &&
                (TrySiroccoFirst(context, state, botMonsters, enemyMonsters, CardId.ChaosSorcerer, chaosWhy, out response) ||
                 TrySpecialSummon(context, state, CardId.ChaosSorcerer, chaosWhy, out response)))
                return response;
            if (TryGetSpecialSummon(state, CardId.DarkGrepher, out _) &&
                ShouldSpecialSummonGrepher(state, hand, botMonsters, out string grepherReason) &&
                await GrepherSummonWinsAsync(context, enemyMonsters, botMonsters) is string grepherLethal &&
                TrySpecialSummon(context, state, CardId.DarkGrepher, $"{grepherLethal}; {grepherReason}", out response, rule: BlackwingRules.GrepherSpecialForLethal))
                return response;

            // 4. Removal effects on the field: only on cards the board can't already handle (or for lethal).
            if (TryGetActivable(state, CardId.DarkArmedDragon, out _) &&
                await DadEffectWhyAsync(context) is string dadEffectWhy &&
                await DestructionNegatedAsync(context, CardId.DarkArmedDragon) == null &&
                TryActivate(context, state, CardId.DarkArmedDragon, dadEffectWhy, out response, rule: BlackwingRules.DadEffect))
                return response;
            if (enemyMonsters.Any(card => card.IsFaceup()) && TryGetActivable(state, CardId.ChaosSorcerer, out _) &&
                await ChaosEffectWhyAsync(context, enemyMonsters, botMonsters) is string chaosEffectWhy &&
                TryActivate(context, state, CardId.ChaosSorcerer, chaosEffectWhy, out response, rule: BlackwingRules.ChaosEffect))
                return response;

            // 5. Graveyard setup (free) and synchro enablers (resources).
            // (Grepher's effect adds 2 DARKs to the graveyard: not past the 3 Dark Armed Dragon needs.)
            string? grepherDad = TryGetActivable(state, CardId.DarkGrepher, out _) ? await DadBlocksAddingDarksAsync(2) : null;
            if (grepherDad != null && !Rule(BlackwingRules.HoldGrepherForDad))
                grepherDad = null;
            if (grepherDad != null)
                Note(context, $"hold Dark Grepher's effect: {grepherDad}");
            if (grepherDad == null && TryGetActivable(state, CardId.DarkGrepher, out _) &&
                hand.Any(card => CardRules.IsGoodDarkDiscard(card.ID)) &&
                await GoodDiscardDeckSendAllowedAsync(CardId.DarkGrepher, context.Header, context.Bot) &&
                TryActivate(context, state, CardId.DarkGrepher, "discard a spare DARK to send a DARK from deck", out response, rule: BlackwingRules.GrepherEffect))
                return response;
            // Card Trooper: mill, or defence when the deck is too thin (shared staple rule).
            if (await TryCardTrooperAsync(context, state) is IdleCmdResponse trooperPlay)
                return trooperPlay;
            // Look one step ahead: Vayu's cost banishes 2 monsters, which can switch on Miracle Dig (it needs
            // 5+ of our monsters banished, returns 3), or Burial in hand can return them. Either way Vayu, its partner
            // and Plaguespreader come back for the next play, so this Vayu is nearly free.
            string? vayuRefund = TryGetActivable(state, CardId.BlackwingVayuTheEmblemOfHonor, out _)
                ? await RecursionAfterVayuAsync(state, hand)
                : null;
            // Judge Vayu on the synchro it will actually make (the same partner choice its cost prompt uses).
            var vayuPlan = TryGetActivable(state, CardId.BlackwingVayuTheEmblemOfHonor, out _)
                ? await PlannedVayuSynchroAsync()
                : (Level: 0, Atk: 0, Why: "");
            // No Vayu play now, but Miracle Dig / Burial would make one: fire it only if that synchro passes the same spend
            // rules. Miracle Dig first (it can't be set as bait; Burial can).
            // (Also when the only play now is Silverwind the Ascendant and it isn't needed: a returned Sirocco / Armed Wing
            // gives a smaller synchro instead, keeping Armor Master as a partner.)
            bool onlyUnneededAscendant = vayuPlan.Level == 8 && await AscendantNeededAsync(context) is not string &&
                                         (await RecursionVayuPlaysAsync()).After.Any(atk => atk < 2800);
            foreach (int recursion in new[] { CardId.MiracleDig, CardId.BurialFromADifferentDimension })
            {
                if ((vayuPlan.Atk <= 0 || onlyUnneededAscendant) && TryGetActivable(state, recursion, out _) &&
                    await MiracleDigVayuPlayAsync() is (int digAtk, int digSpares, string digWhat) &&
                    await JustifySpendAsync(context, $"{CardNames.Get(recursion)} for a Vayu play ({digWhat})", newAtk: digAtk, fieldMaterials: null, removal: false,
                        cardId: CardId.BlackwingVayuTheEmblemOfHonor, extraDeck: true, allowPush: digSpares > 0, spares: digSpares) is string digWhy &&
                    TryActivate(context, state, recursion, digWhy, out response, rule: BlackwingRules.RecursionForVayu))
                    return response;
            }

            // Silverwind the Ascendant ends the Vayu chain: Armor Master is the last partner, and a Level 8 partners nothing,
            // so any other Vayu is left stranded. Smallest synchro possible: make it only when it's needed.
            bool ascendantHeld = false;
            if (vayuPlan.Level == 8 && await AscendantNeededAsync(context) is not string && Rule(BlackwingRules.HoldAscendant))
            {
                Note(context, "hold Vayu's graveyard synchro: the only one on offer is Silverwind the Ascendant (Armor Master as partner), " +
                              "which ends the Vayu chain and strands the other Vayus; it's not needed now (not lethal, no big threat only 2800 answers, " +
                              "holding back doesn't lose), so keep Armor Master for when it is");
                ascendantHeld = true;
                Hold(CardId.BlackwingVayuTheEmblemOfHonor, MainAction.Activate, BlackwingRules.HoldAscendant);
            }

            // The more Vayus in the graveyard, the less each one needs saving: spare ones loosen what we hold for.
            int spareVayus = vayuPlan.Atk > 0 ? await SpareVayusAsync() : 0;
            if (vayuPlan.Atk > 0 && !ascendantHeld &&
                await JustifySpendAsync(context, $"Vayu's graveyard synchro ({vayuPlan.Why})", newAtk: vayuPlan.Atk, fieldMaterials: null, removal: false,
                    cardId: CardId.BlackwingVayuTheEmblemOfHonor, extraDeck: true, allowPush: spareVayus > 0, refundedBy: vayuRefund,
                    spares: spareVayus) is string vayuWhy &&
                TryActivate(context, state, CardId.BlackwingVayuTheEmblemOfHonor, vayuWhy, out response, rule: BlackwingRules.VayuSynchro))
            {
                _vayuPlannedLevel = vayuPlan.Level;
                _vayuPlannedTurn = context.Header.CurrentTurn;
                return response;
            }

            // Two alternative lines from the same Vayu: Armed Wing alone (judged just above, with the usual resource saving:
            // if it's worth making, that's the play and Plaguespreader stays for later), or Armed Wing + Plaguespreader
            // into a Level 8 (Colossal Fighter 2800, ...). The second spends two resources, so it's tried only when Armed
            // Wing alone isn't worth it, and each resource must pass its own spend check: Vayu for the Level 8, and
            // Plaguespreader (a one-shot that also costs a hand card) for what the Level 8 adds.
            if (vayuPlan.Level == 6 && !ascendantHeld && await PlaguespreaderUsableAsync() &&
                await BestSynchroForLevelAsync(8, enemyMonsters, botMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList())
                    is { } level8 &&
                await JustifySpendAsync(context, $"Vayu -> Armed Wing, then Plaguespreader + Armed Wing into {CardNames.Get(level8.Synchro.Id)}",
                    newAtk: level8.Synchro.Attack, fieldMaterials: null, removal: false, cardId: CardId.BlackwingVayuTheEmblemOfHonor,
                    extraDeck: true, allowPush: spareVayus > 0, refundedBy: vayuRefund, effectAnswer: level8.Answer, spares: spareVayus) is string comboWhy &&
                await JustifySpendAsync(context, $"Plaguespreader (one-shot) for {CardNames.Get(level8.Synchro.Id)} after Vayu's Armed Wing",
                    newAtk: level8.Synchro.Attack, fieldMaterials: null, removal: false, cardId: CardId.PlaguespreaderZombie,
                    extraDeck: true, effectAnswer: level8.Answer) is string &&
                // The whole combo as a routine (Vayu, then Plaguespreader, then the Level 8): checked and scored on the engine, and
                // followed step by step (no Plaguespreader without its synchro).
                await ContinueRoutineAsync(context, "Vayu + Plaguespreader Level 8", comboWhy,
                    new RoutineStep(MainAction.Activate, CardId.BlackwingVayuTheEmblemOfHonor, "Vayu's synchro (Blackwing Armed Wing)"),
                    new[]
                    {
                        new RoutineStep(MainAction.Activate, CardId.PlaguespreaderZombie, "Plaguespreader from the graveyard",
                            OnTake: () => { PlagueTargetSynchro = level8.Synchro.Id; PlagueTargetTurn = context.Header.CurrentTurn; }),
                        new RoutineStep(MainAction.SpSummon, level8.Synchro.Id, $"synchro summon {CardNames.Get(level8.Synchro.Id)}"),
                    }) &&
                TryActivate(context, state, CardId.BlackwingVayuTheEmblemOfHonor, comboWhy, out response, rule: BlackwingRules.VayuPlagueLevel8))
            {
                _vayuPlannedLevel = 6;
                _vayuPlannedTurn = context.Header.CurrentTurn;
                _vayuPlagueComboTurn = context.Header.CurrentTurn;
                _vayuPlagueComboSynchro = level8.Synchro.Id;
                return response;
            }
            // Its follow-up: Armed Wing is out, now Plaguespreader into the planned Level 8.
            if (_vayuPlagueComboTurn == context.Header.CurrentTurn &&
                botMonsters.Any(card => card.IsFaceup() && card.IsCode(CardId.BlackwingArmedWing)) &&
                TryActivate(context, state, CardId.PlaguespreaderZombie,
                    $"Plaguespreader + Blackwing Armed Wing into {CardNames.Get(_vayuPlagueComboSynchro)} (the plan when Vayu was used)", out response))
            {
                _vayuPlagueComboTurn = -1;
                PlagueTargetSynchro = _vayuPlagueComboSynchro;
                PlagueTargetTurn = context.Header.CurrentTurn;
                return response;
            }

            // A stepping stone: Armed Wing (2300) can't beat their 2400, but once it's in the graveyard (run over, or
            // traded) a second Vayu makes Armor Master (2500), which can. Not a strong play, so only when it's our only
            // way to answer that monster: a big threat at 2301-2500, no partner for Armor Master yet, another Vayu
            // waiting, and nothing else that beats it (DAD, Chaos Sorcerer, a Plaguespreader Level 8).
            if (vayuPlan.Level == 6 && !ascendantHeld && TryGetActivable(state, CardId.BlackwingVayuTheEmblemOfHonor, out _) &&
                await ArmedWingSteppingStoneAsync(state, enemyMonsters, botMonsters) is string stoneWhy &&
                TryActivate(context, state, CardId.BlackwingVayuTheEmblemOfHonor, stoneWhy, out response, rule: BlackwingRules.ArmedWingSteppingStone))
            {
                _vayuPlannedLevel = 6;
                _vayuPlannedTurn = context.Header.CurrentTurn;
                return response;
            }
            // Plaguespreader + a non-Tuner on the field into the best synchro (shared rule).
            if (await TryPlaguespreaderSynchroAsync(context, state, enemyMonsters, botMonsters, BlackwingRules.PlagueSynchro) is IdleCmdResponse plagueSynchroPlay)
            {
                _lastActivatedCard = CardId.PlaguespreaderZombie;
                return plagueSynchroPlay;
            }
            // A Tuner of ours already on the field + a non-Tuner: their synchro (shared).
            if (await TryFieldTunerSynchroAsync(context, state, enemyMonsters, botMonsters, DeckRules.FieldTunerSynchro) is IdleCmdResponse fieldSynchro)
                return fieldSynchro;

            // 6. Synchros from the Extra Deck: synchroing from the field goes -1, so only when the synchro is needed.
            // First any synchro on offer whose effect answers a threat the board can't (e.g. Catastor vs a non-DARK monster).
            // Our Brionac already out: bounce first (shared rule).
            if (await TryBrionacBounceAsync(context, state) is IdleCmdResponse bounce)
                return bounce;
            var extraSynchros = await ExtraDeckSynchrosAsync();
            var boardAtk = botMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList();
            int handCount = (await GetBotHandCardsAsync()).Count;
            foreach (var option in extraSynchros.Where(s => s.DestroysAllBut != null || BounceSynchroIds.Contains(s.Id)))
            {
                if (!TryGetSpecialSummon(state, option.Id, out var effectSynchro) ||
                    SynchroAnswer(option, enemyMonsters, boardAtk, handCount) is not string effectAnswer)
                    continue;
                var answered = SynchroAnswerTarget;
                if (await JustifySpendAsync(context, $"synchro {CardNames.Get(option.Id)}", newAtk: effectSynchro.Attack,
                        fieldMaterials: GuessSynchroMaterials(effectSynchro, botMonsters), removal: false, cardId: option.Id,
                        extraDeck: true, effectAnswer: effectAnswer) is string effectWhy &&
                    TrySpecialSummon(context, state, option.Id, effectWhy, out response, rule: BlackwingRules.EffectSynchro))
                {
                    // Summoned to battle that monster: the attack is the plan (it takes effect once the summon is made).
                    if (answered is DynamicCard prey)
                        CommitAttack(context.Header.CurrentTurn, option.Id, prey, $"{CardNames.Get(option.Id)} was summoned to battle {CardNames.Get(prey.ID)}");
                    return response;
                }
            }
            // After Plaguespreader: summon the synchro it was brought out for (shared).
            if (TryPlagueTargetSynchro(context, state) is IdleCmdResponse plagueTarget)
                return plagueTarget;
            foreach (var synchroId in SynchroPriority)
            {
                if (!TryGetSpecialSummon(state, synchroId, out var synchro))
                    continue;
                if (await JustifySpendAsync(context, $"synchro {CardNames.Get(synchroId)}", newAtk: synchro.Attack,
                        fieldMaterials: GuessSynchroMaterials(synchro, botMonsters), removal: false, cardId: synchroId, extraDeck: true) is string synchroWhy &&
                    TrySpecialSummon(context, state, synchroId, synchroWhy, out response, rule: BlackwingRules.ExtraDeckSynchro))
                    return response;
                break; // Only consider the best available synchro; if it isn't justified, lower ones aren't either.
            }
            // Black Rose wipes the whole field (itself included). Count what we'd actually lose: Black Rose plus our
            // cards that aren't its synchro materials, against everything the opponent has.
            if (TryGetSpecialSummon(state, CardId.BlackRoseDragon, out var blackRose))
            {
                int materials = GuessSynchroMaterials(blackRose, botMonsters).Count;
                int ourLoss = 1 + botMonsters.Count - materials + botSpells.Count;
                int theirLoss = enemyMonsters.Count + enemySpells.Count;
                if (theirLoss > ourLoss &&
                    await DestructionNegatedAsync(context, CardId.BlackRoseDragon) == null &&
                    TrySpecialSummon(context, state, CardId.BlackRoseDragon,
                        $"board wipe: opponent loses {theirLoss} cards, we lose {ourLoss} (Black Rose itself + {ourLoss - 1} other)", out response, rule: BlackwingRules.BlackRoseWipe))
                    return response;
            }

            // 6b. The aggro Normal Summon (Black Whirlwind searches; Blizzard revives).
            if (await TryBlackwingNormalSummonAsync(context, state) is IdleCmdResponse aggroSummon)
            {
                return aggroSummon;
            }

            // 7. Normal Summon or Set.
            var summonOrSet = await ChooseNormalSummonOrSetAsync(context, state, enemyMonsters, enemySpells, botMonsters, hand);
            if (summonOrSet.HasValue)
            {
                return summonOrSet.Value;
            }

            // 7b. No good play this turn (nothing worth summoning, Normal Summon still unused): draw with Jar of Greed /
            // Legacy of Yata-Garasu to look for one. Otherwise they stay set as bait for the opponent's removal.
            if (TryDrawWhenNoPlay(context, state, normalSummonUsed: NormalSummonUsed(context.Header)) is IdleCmdResponse draw)
                return draw;

            // 8. Set a trap in hand once no attack is coming this turn (shared staple rule: storm-proof first, no two for the
            // same issue, not while Gorz is live or into a likely Heavy Storm). Burial is set as bait too (IsSetCandidate).
            var (trapSet, battleAhead, gorzLive) = await ChooseTrapSetAsync(context, state, enemyMonsters, botMonsters, hand);
            if (trapSet is IdleCmdResponse setTrap)
                return setTrap;

            // The summon decision has been made above (including "keep it in hand"), so the generic fallback must not
            // summon or set monsters: its default would Normal/Tribute Summon whatever is listed first.
            var fallbackState = state;
            fallbackState.SummonableCards = Array.Empty<DynamicCard>();
            fallbackState.MonsterSetableCards = Array.Empty<DynamicCard>();
            if (battleAhead || gorzLive)
                fallbackState.SpellSetableCards = Array.Empty<DynamicCard>();
            return await base.OnIdleCmdAsync(context with { StateData = fallbackState }, ct);
        }

        /// <summary>
        /// The DARK Armageddon Knight would send from the deck, or 0 if nothing useful is left.
        /// Deck contents are inferred from copies already seen in hand, graveyard, banished and on the field.
        /// </summary>
        private async Task<int> BestArmageddonTargetAsync(List<DynamicCard> hand)
        {
            // Its effect would be declined anyway (Dark Armed Dragon in hand, graveyard already at 3+ DARKs).
            if (await DadBlocksAddingDarksAsync(1) != null)
                return 0;
            var seen = new List<DynamicCard>(hand);
            seen.AddRange(await GetBotGraveyardCardsAsync());
            seen.AddRange(await GetBotMonsterCardsAsync());
            seen.AddRange(await CurrentField.GetCardsInLocation(CardLocation.Removed));

            var (order, _) = await DarkSendPriorityAsync();
            foreach (int id in order.Where(CardRules.IsGoodDarkDiscard))
            {
                int copies = MainDeckCopies.TryGetValue(id, out int c) ? c : 0;
                if (seen.Count(card => card.IsCode(id)) < copies)
                    return id;
            }
            return 0;
        }

        // Main deck copies of the DARKs Armageddon Knight / Dark Grepher can send (Elphin is side deck only), per list.
        private Dictionary<int, int> MainDeckCopies => Variant == BlackwingVariant.Hybrid ? HybridCopies : StandardCopies;

        private static readonly Dictionary<int, int> HybridCopies = new()
        {
            [CardId.BlackwingVayuTheEmblemOfHonor] = 3,
            [CardId.BlackwingSiroccoTheDawn] = 3,
            [CardId.DarkGrepher] = 2,
            [CardId.ArmageddonKnight] = 1
        };

        private static readonly Dictionary<int, int> StandardCopies = new()
        {
            [CardId.BlackwingVayuTheEmblemOfHonor] = 3,
            [CardId.BlackwingSiroccoTheDawn] = 3,
            [CardId.NecroGardna] = 1,
            [CardId.PlaguespreaderZombie] = 1,
            [CardId.DarkGrepher] = 3,
            [CardId.ArmageddonKnight] = 3,
            [CardId.BlackwingElphinTheRaven] = 0
        };

        /// <summary>
        /// Which DARK to send from the deck (Armageddon Knight / Dark Grepher), best first:
        ///  - Plaguespreader if the opponent has a face-up 2300-2800 ATK monster a level 6 synchro can beat,
        ///  - Vayu (graveyard-only effect), then its partner Sirocco (unless a partner is already in or headed for the GY),
        ///  - a second / third Vayu,
        ///  - Plaguespreader (a synchro play of its own),
        ///  - Necro Gardna (a nice to have),
        ///  - everything else.
        /// </summary>
        /// <summary>
        /// Below this many expected extra plays, sending another Vayu isn't worth it over Plaguespreader (e.g. its only
        /// partner would be a field synchro that probably survives). Tune from logs.
        /// </summary>
        [Tunable("Which DARK to send from the deck (Armageddon Knight / Dark Grepher), best first: - Plaguespreader if the opponent has a face-up 2300-2800 ATK monster a level 6 synchro can beat, - Vayu (graveyard-only effect), then its partner Sirocco (unless a partner is already in or headed for the GY), - a second / third Vayu, - Plaguespreader (a synchro play of its own), - Necro Gardna (a nice to have), - e...")]
        public double UnpairedSendGain { get; set; } = 0.4;

        /// <summary>Chance a Vayu / partner in hand reaches the graveyard soon (set and destroyed, a cost...). Tune from logs.</summary>
        [Tunable("Chance a Vayu / partner in hand reaches the graveyard soon (set and destroyed, a cost...). Tune from logs.")]
        public double HandToGraveChance { get; set; } = 0.35;

        /// <summary>The same with a discard outlet (Dark Grepher in hand or on the field). Tune from logs.</summary>
        [Tunable("The same with a discard outlet (Dark Grepher in hand or on the field). Tune from logs.")]
        public double HandToGraveWithOutletChance { get; set; } = 0.8;

        private async Task<(List<int> Order, string Why)> DarkSendPriorityAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            var field = await GetBotMonsterCardsAsync();
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            int botLp = await CurrentField.GetBotLifePoints();

            bool vayuInGrave = grave.Any(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
            bool partnerInGrave = grave.Any(card => card.IsCode(VayuPartners));
            bool synchroPartnerExpected = grave.Any(card => card.IsCode(SynchroVayuPartners)) || field.Any(card => card.IsCode(SynchroVayuPartners));
            bool necroInGrave = grave.Any(card => card.IsCode(CardId.NecroGardna));
            bool plagueInGrave = grave.Any(card => card.IsCode(CardId.PlaguespreaderZombie));
            var plagueTarget = enemyMonsters.FirstOrDefault(card => card.IsFaceup() && card.Attack is >= 2300 and <= 2800);

            // The metric is expected LIVE pairs (Vayu + partner both in the graveyard). Only graveyard cards are sure;
            // everything else counts with the chance it gets there:
            //  - in hand: discarded (higher with a Dark Grepher outlet), or set/summoned and destroyed, or a cost;
            //  - on the field (a synchro partner, or Vayu/Sirocco themselves): run over in battle, Mirror Force,
            //    Torrential (banishing answers like Dimensional Prison keep it out, so they don't count).
            var hand = await GetBotHandCardsAsync();
            bool discardOutlet = hand.Concat(field).Any(card => card.IsCode(CardId.DarkGrepher));
            double handChance = discardOutlet ? HandToGraveWithOutletChance : HandToGraveChance;
            int vayusSure = grave.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
            int partnersSure = grave.Count(card => card.IsCode(VayuPartners));
            var vayuOdds = new List<double>();
            var partnerOdds = new List<double>();
            foreach (var card in hand)
            {
                if (card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) vayuOdds.Add(handChance);
                else if (card.IsCode(VayuPartners)) partnerOdds.Add(handChance);
            }
            foreach (var card in field.Where(card => card.ID > 0))
            {
                if (card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) vayuOdds.Add(ReachesGraveChance(card, enemyMonsters));
                else if (card.IsCode(VayuPartners) || card.IsCode(SynchroVayuPartners)) partnerOdds.Add(ReachesGraveChance(card, enemyMonsters));
            }
            double vayusHad = vayusSure + vayuOdds.Sum();
            double partnersHad = partnersSure + partnerOdds.Sum();

            // Expected live pairs: min(Vayus, partners) over every combination of the uncertain cards arriving.
            var uncertain = vayuOdds.Select(p => (P: p, Vayu: true)).Concat(partnerOdds.Select(p => (P: p, Vayu: false))).ToList();
            double ExpectedPlays(int extraVayus, int extraPartners)
            {
                double total = 0;
                int n = Math.Min(uncertain.Count, 12);
                for (int mask = 0; mask < 1 << n; mask++)
                {
                    double p = 1;
                    int vayus = vayusSure + extraVayus, partners = partnersSure + extraPartners;
                    for (int i = 0; i < n; i++)
                    {
                        bool arrives = (mask & (1 << i)) != 0;
                        p *= arrives ? uncertain[i].P : 1 - uncertain[i].P;
                        if (arrives && uncertain[i].Vayu) vayus++;
                        else if (arrives) partners++;
                    }
                    total += p * Math.Min(vayus, partners);
                }
                return total;
            }
            double now = ExpectedPlays(0, 0);
            double vayuGain = ExpectedPlays(1, 0) - now;
            double partnerGain = ExpectedPlays(0, 1) - now;

            // No defensive exception at low LP: the best defence is a good offence.
            var order = new List<int>();
            if (plagueTarget.ID > 0 && !plagueInGrave)
                order.Add(CardId.PlaguespreaderZombie);
            if (vayusSure == 0 && vayuOdds.Count == 0)
            {
                // No Vayu anywhere (graveyard, hand, field): get one, it's the scarce half (partners are easy to get:
                // Armageddon Knight, Grepher, a synchro dying).
                order.Add(CardId.BlackwingVayuTheEmblemOfHonor);
                order.Add(CardId.BlackwingSiroccoTheDawn);
            }
            else if (partnerGain > vayuGain)
            {
                order.Add(CardId.BlackwingSiroccoTheDawn); // a Vayu without a (likely) partner: complete the pair
                order.Add(CardId.BlackwingVayuTheEmblemOfHonor);
            }
            else if (vayuGain >= UnpairedSendGain)
            {
                order.Add(CardId.BlackwingVayuTheEmblemOfHonor);
            }
            else
            {
                // Neither half is likely to make a play: Plaguespreader (a play on its own) first, then Vayu (the hard
                // half of the next pair). Necro Gardna is a last resort, never ahead of these.
                if (!plagueInGrave)
                    order.Add(CardId.PlaguespreaderZombie);
                order.Add(CardId.BlackwingVayuTheEmblemOfHonor);
            }
            order.Add(CardId.BlackwingVayuTheEmblemOfHonor);
            // Necro Gardna is a last resort: after everything that makes or completes a play.
            if (!plagueInGrave)
                order.Add(CardId.PlaguespreaderZombie);
            order.AddRange(new[]
            {
                CardId.PlaguespreaderZombie,
                CardId.BlackwingSiroccoTheDawn,
                CardId.BlackwingElphinTheRaven,
                CardId.DarkGrepher,
                CardId.ArmageddonKnight,
                CardId.NecroGardna
            });

            string why = $"expected Vayus {vayusHad:0.##} ({vayusSure} in GY), partners {partnersHad:0.##} ({partnersSure} in GY) " +
                         "(hand and field cards counted by their chance of reaching the GY), " +
                         $"expected live pairs {now:0.##}: +{vayuGain:0.##} from a Vayu / +{partnerGain:0.##} from a partner, " +
                         $"Vayu in GY: {vayuInGrave}, partner in GY: {partnerInGrave}, synchro partner in GY/field: {synchroPartnerExpected}, " +
                         $"Necro in GY: {necroInGrave}, LP {botLp}" +
                         (plagueTarget.ID > 0 ? $", Plaguespreader target: {CardNames.Get(plagueTarget.ID)} {plagueTarget.Attack} ATK" : "");
            return (order.Distinct().ToList(), why);
        }

        // Set monsters whose loss isn't a straight -1: Ryko flips to destroy a card, Card Trooper draws when destroyed,
        // and graveyard-effect monsters (Necro Gardna, Vayu, Plaguespreader) are more useful destroyed than kept in hand:
        // setting them gets them "closer" to the graveyard.
        // Order = which to set first.
        // (Card Trooper is deliberately not here: its effect needs it face-up, so it's summoned instead.)
        private static readonly int[] ValueSets =
        {
            CardId.RykoLightswornHunterPreErrata,
            CardId.BlackwingVayuTheEmblemOfHonor,
            CardId.PlaguespreaderZombie,
            CardId.NecroGardna
        };

        protected override int[] ValueSetIds => ValueSets;

        // Icarus Attack needs a Winged Beast of ours to tribute: set it only with one on the field, and one copy at a time
        // (seen 2026-11-11: two set on turn 1 with only Dark Grepher out; Heavy Storm took both). Refreshed every decision.
        private int _wingedOnField, _icarusSet;

        private bool IcarusNotUsable(DynamicCard card) =>
            card.IsCode(CardId.IcarusAttack) && (_wingedOnField == 0 || _icarusSet > 0) && Rule(BlackwingRules.IcarusSetNeedsWing);

        // Burial is set as bait too, but only with 2+ banished monsters we care about to return when it is chained.
        private int _banishedForBait;
        protected override bool IsSetCandidate(DynamicCard card) =>
            base.IsSetCandidate(card) && !IcarusNotUsable(card) || card.IsCode(CardId.BurialFromADifferentDimension) && _banishedForBait >= BurialBaitAt;

        // Safe Normal Summon order. Dark Grepher's effect needs a DARK to discard; without one it's just a 1700 body, and
        // Lyla (same ATK) at least mills, so Grepher with nothing to pitch goes after the others.
        protected override int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) =>
            card.IsCode(CardId.DarkGrepher) && !hand.Any(c => CardRules.IsGoodDarkDiscard(c.ID))
                ? SummonPriority.Length
                : PriorityIndex(SummonPriority, card);

        // Armageddon Knight's summon effect does nothing with no useful DARK left to send.
        protected override async Task<bool> OnSummonEffectUselessAsync(DynamicCard card, List<DynamicCard> hand) =>
            card.IsCode(CardId.ArmageddonKnight) && await BestArmageddonTargetAsync(hand) == 0;

        /// <summary>This deck's Normal Summons, between the shared staple ones: Armageddon Knight (graveyard setup, any LP), then Dark Grepher with a spare DARK to discard.</summary>
        protected override async Task<IdleCmdResponse?> DeckSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> developers, List<DynamicCard> hand, bool danger, int botLp)
        {
            if (developers.TryGetByID(CardId.ArmageddonKnight, out var armageddon))
            {
                var target = await BestArmageddonTargetAsync(hand);
                // Graveyard setup is development, so do it at any life points.
                if (target != 0 && Rule(BlackwingRules.SummonArmageddon))
                {
                    Note(context, $"normal summon Armageddon Knight to send {CardNames.Get(target)} (LP {botLp})");
                    return armageddon.NormalSummon();
                }
            }
            if (!danger && hand.Count(card => CardRules.IsGoodDarkDiscard(card.ID)) > 0 &&
                developers.TryGetByID(CardId.DarkGrepher, out var grepher) && Rule(BlackwingRules.SummonGrepher))
            {
                Note(context, $"normal summon Dark Grepher, then discard a spare DARK to its effect (LP {botLp})");
                return grepher.NormalSummon();
            }
            return null;
        }

        private int _lastActivatedCard;

        // ------------------------------------------------------------------ chains

        public override async Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default)
        {
            _kalutOffered = null;
            // Our Goyo Guardian taking the monster it destroyed: always (seen 2026-11-10: Vayu passed on taking Card Trooper).
            if (OwnTakeControlTrigger(context) is int takeControl)
                return takeControl;
            var state = context.StateData;
            var options = state.SelectableCards ?? Array.Empty<DynamicCard>();

            if (state.Forced || options.Length == 0)
                return await base.OnSelectChainAsync(context, ct);

            // Always refresh the board, even for prompts we'll just pass: what happens in their turn (Grand Mole coming
            // out, bouncing and going back to their hand) must be seen to be remembered.
            await PrepareContextAsync(context, ct);

            bool botTurn = IsBotTurn(context.Header);
            var phase = (DuelPhase)context.Header.CurrentPhase;
            if (await TryBlackwingChainAsync(context, options, botTurn) is int blackwingChain)
                return blackwingChain;

            // Monster effects offered in the priority window right after the summon: use them there, so the opponent
            // can't remove the monster (Bottomless, etc.) before the effect resolves. Same conditions as in the main
            // phase. Lyla is the exception: her effect switches her to defence, so she attacks first (main phase).
            if (botTurn && options.Any(card => card.IsCode(PriorityWindowEffects)))
            {
                await PrepareContextAsync(context, ct);
                var hand = await GetBotHandCardsAsync();
                const string window = "use the effect in the priority window, before it can be removed";
                string? grepherDad = options.Any(card => card.IsCode(CardId.DarkGrepher)) ? await DadBlocksAddingDarksAsync(2) : null;
                if (grepherDad != null && !Rule(BlackwingRules.HoldGrepherForDad))
                    grepherDad = null;
                if (grepherDad != null)
                    Note(context, $"hold Dark Grepher's effect: {grepherDad}");
                if (grepherDad == null && options.Any(card => card.IsCode(CardId.DarkGrepher)) &&
                    hand.Any(card => CardRules.IsGoodDarkDiscard(card.ID)) &&
                    await GoodDiscardDeckSendAllowedAsync(CardId.DarkGrepher, context.Header, context.Bot) &&
                    TryChain(context, options, CardId.DarkGrepher, window, out int priorityIndex, rule: BlackwingRules.GrepherEffect))
                    return priorityIndex;
                if (options.Any(card => card.IsCode(CardId.CardTrooper)) && await TrooperCanMillAsync(context) &&
                    TryChainStaple(context, options, CardId.CardTrooper, window, out priorityIndex, rule: DeckRules.TrooperMill))
                {
                    MarkStapleActivated(CardId.CardTrooper); // so the "how many" prompt sends the maximum
                    return priorityIndex;
                }
                if (options.Any(card => card.IsCode(CardId.DarkArmedDragon)) &&
                    await DadEffectWhyAsync(context) is string dadWhy &&
                    await DestructionNegatedAsync(context, CardId.DarkArmedDragon) == null &&
                    TryChain(context, options, CardId.DarkArmedDragon, $"{dadWhy}; {window}", out priorityIndex, rule: BlackwingRules.DadEffect))
                    return priorityIndex;
                if (options.Any(card => card.IsCode(CardId.ChaosSorcerer)) &&
                    await ChaosEffectWhyAsync(context, await GetEnemyMonsterCardsAsync(), await GetBotMonsterCardsAsync()) is string chaosWhy &&
                    TryChain(context, options, CardId.ChaosSorcerer, $"{chaosWhy}; {window}", out priorityIndex, rule: BlackwingRules.ChaosEffect))
                    return priorityIndex;
            }

            // Bait cards (Jar of Greed, Legacy of Yata-Garasu, MST) set and chained when the opponent's removal would
            // hit them, and the Jar of Greed / Gorz trick: shared staple rules.
            // (Any set card of ours: one about to be removed is used if it does any good; user, 2026-11-12.)
            if (options.Any(IsOurSetCard))
            {
                await PrepareContextAsync(context, ct);
                await RefreshOurBanishedAsync();
                var bait = await TryChainBaitAsync(context);
                if (bait.HasValue)
                    return bait.Value;
                var jarForGorz = await TryDrawCardForGorzAsync(context);
                if (jarForGorz.HasValue)
                    return jarForGorz.Value;
            }

            // MST in response to the opponent's continuous card (Ultimate Offering, Call of the Haunted): destroying it
            // in the chain stops its effect too. Shared staple rule.
            if (options.Any(card => card.IsCode(CardId.MysticalSpaceTyphoon)) && context.Header.LastChainPlayer == 1)
            {
                await PrepareContextAsync(context, ct);
                var mstChain = await TryChainMstIntoContinuousAsync(context);
                if (mstChain.HasValue)
                    return mstChain.Value;
            }

            // Fast path: most chain prompts (e.g. MST in hand during every draw phase) need no field data.
            bool anyCandidate = options.Any(card => card.IsCode(ChainCandidates));
            if (!anyCandidate)
            {
                Note(context, "no chain candidates worth considering, pass");
                return -1;
            }
            // Responses to their attacks and summons (Gorz, Mirror Force, Dimensional Prison, Bottomless, Torrential,
            // Trap Dustshoot): shared staple rules; Necro Gardna through the attack-negate hook below.
            var response = await TryChainResponsesAsync(context, options, botTurn, ct);
            if (response.HasValue)
                return response.Value;

            return await base.OnSelectChainAsync(context, ct);
        }

        private static readonly int[] AttackNegates = { CardId.NecroGardna };

        // Necro Gardna counts as an attack answer for the shared "one answer per attack" rule.
        protected override int[] AttackNegateIds => AttackNegates;

        /// <summary>Necro Gardna from the graveyard, when the attack is worth protecting against (see WorthProtectingAsync).</summary>
        protected override async Task<int?> TryChainAttackNegateAsync(
            DecisionContext<SelectChainData> context,
            DynamicCard[] options,
            List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters,
            IncomingAttack? incoming)
        {
            if (!options.Any(card => card.IsCode(CardId.NecroGardna)) ||
                !await WorthProtectingAsync(context, botMonsters, enemyMonsters, incoming))
                return null;
            string why = incoming?.Why ?? "attacker unknown";
            return TryChainStaple(context, options, CardId.NecroGardna, $"negate the attack from the graveyard: {why}", out int index,
                rule: BlackwingRules.NecroGardna) ? index : null;
        }

        /// <summary>
        /// Whether the attack is worth spending Necro Gardna on: when letting it through costs us something (see
        /// AssessIncomingAttackAsync). An expendable monster (Armageddon Knight, Dark Grepher) is protected only if it is
        /// our only clock: without it, our board can't put the opponent on one.
        /// </summary>
        private async Task<bool> WorthProtectingAsync(
            DecisionContext<SelectChainData> context,
            List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters,
            IncomingAttack? incoming)
        {
            // Necro Gardna is for saving a monster that matters, stopping a hit that costs a card (Airbellum's discard,
            // Grand Mole bouncing a synchro), or not losing; life points alone aren't worth a card.
            if (incoming == null)
                return true; // can't tell what's attacking: be safe
            if (incoming.Lethal)
                return true;
            if (incoming.OurOwnMonster && Rule(BlackwingRules.NecroNotOnOwnMonster))
            {
                Note(context, $"keep Necro Gardna: {incoming.Why}");
                return false;
            }
            // Only life points at stake (not lethal now): Necro works just as well next turn, so keep it for the last
            // possible moment (the hit that would kill us), unless it protects a monster or a card in hand.
            if (incoming.OnlyLifePointsCost && Rule(BlackwingRules.NecroForLastMoment))
            {
                Note(context, $"keep Necro Gardna for the last moment (not lethal yet, and no card to protect): {incoming.Why}");
                return false;
            }
            if (!incoming.CostsUs)
            {
                // Still protect an expendable monster if it is our only clock.
                var target = incoming.Target;
                bool destroyed = target.ID > 0 && !BouncesAttacker(incoming.Attacker) &&
                                 (target.IsAttack() ? incoming.Attacker.Attack >= target.Attack : incoming.Attacker.Attack > target.Defense);
                if (destroyed && await IsOnlyClockAsync(context, botMonsters, enemyMonsters, target) && Rule(BlackwingRules.NecroProtectsOnlyClock))
                    return true;
                Note(context, $"keep Necro Gardna: {incoming.Why}");
                return false;
            }
            return true;
        }

        /// <summary>An expendable monster is still worth protecting if, without it, our board puts the opponent on no clock.</summary>
        private async Task<bool> IsOnlyClockAsync(
            DecisionContext<SelectChainData> context,
            List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters,
            DynamicCard target)
        {
            var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var withIt = botMonsters.Where(card => card.IsAttack()).Select(EffectiveAttack).ToList();
            var without = new List<int>(withIt);
            without.Remove(EffectiveAttack(target));

            int clockWith = BoardAssessment.TurnsToWin(withIt, defenders, oppLp);
            int clockWithout = BoardAssessment.TurnsToWin(without, defenders, oppLp);
            if (clockWith != BoardAssessment.NoClock && clockWithout == BoardAssessment.NoClock)
            {
                Note(context, $"{CardNames.Get(target.ID)} is expendable, but it is our only clock ({BoardAssessment.Describe(clockWith)}): protect it");
                return true;
            }

            Note(context, $"{CardNames.Get(target.ID)} is expendable (clock without it: {BoardAssessment.Describe(clockWithout)}): don't spend Necro Gardna on it");
            return false;
        }

        /// <summary>
        /// Hand-card values for this deck's key cards (see the shared version for the rest): Dark Armed Dragon is worth a
        /// lot when it's live (exactly 3 DARKs in the graveyard) or can be made live (trim with Vayu's banish, or our
        /// DARKs dying in this battle bring the count up to 3), and little when it can't; Vayu's partners (no graveyard
        /// effect of their own) cost little to discard, like the graveyard-effect cards.
        /// </summary>
        protected override async Task<(double Value, string Why)> HandCardValueAsync(DynamicCard card, List<DynamicCard> botMonsters)
        {
            if (card.IsCode(CardId.DarkArmedDragon))
            {
                var grave = await GetBotGraveyardCardsAsync();
                bool IsDark(DynamicCard c) => c.HasType(CardType.Monster) && (c.Attribute & (int)CardAttribute.Dark) != 0;
                int darks = grave.Count(IsDark);
                // Down: each Vayu + partner pair banished by Vayu's synchro takes 2. Up: our DARK monsters on the field
                // that can still die (this battle or later).
                int pairs = Math.Min(grave.Count(c => c.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)), grave.Count(c => c.IsCode(VayuPartners)));
                int lowest = darks - 2 * pairs;
                int highest = darks + botMonsters.Count(c => c.ID > 0 && IsDark(c));
                // Under 3: our DARKs dying can bring it up. Over 3: Vayu trims 2 at a time (an odd gap needs a DARK of
                // ours to die first to fix the parity).
                bool live = darks == 3 ||
                            darks < 3 && highest >= 3 ||
                            darks > 3 && lowest <= 3 && ((darks - 3) % 2 == 0 || highest > darks);
                return live
                    ? (DadLiveHandValue, $"Dark Armed Dragon, {(darks == 3 ? "live now" : "can be made live")} ({darks} DARKs in the graveyard): a very strong card")
                    : (DadDeadHandValue, $"Dark Armed Dragon, but it can't be made live ({darks} DARKs in the graveyard): nearly useless");
            }
            // Sirocco next to a Vayu makes a live pair: discarding a partner puts it where we want it. Close to free.
            if (card.IsCode(VayuPartners))
                return (DiscardedGraveyardPieceCost, $"{CardNames.Get(card.ID)} (a Vayu partner: discarding it costs little)");
            return await base.HandCardValueAsync(card, botMonsters);
        }

        /// <summary>
        /// Our answers to the opponent's Gorz (2700): the shared ones (a bigger monster, removal we hold), plus Dark Armed
        /// Dragon on the field or summonable, Chaos Sorcerer, or a synchro we can make that beats 2700.
        /// </summary>
        protected override async Task<bool> CanAnswerGorzAsync()
        {
            if (await base.CanAnswerGorzAsync())
                return true;
            var monsters = await GetBotMonsterCardsAsync();
            var hand = await GetBotHandCardsAsync();
            if (monsters.Concat(hand).Any(card => card.IsCode(CardId.DarkArmedDragon, CardId.ChaosSorcerer)))
                return true;
            // Vayu in the graveyard (Silverwind 2800 with Armor Master there) or Plaguespreader with a Level 4 (Goyo 2800).
            if (await BestVayuSynchroAtkAsync() > GorzAttack &&
                (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)))
                return true;
            return monsters.Any(card => card.Level == 4 && !card.HasType(CardType.Tuner)) &&
                   (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.PlaguespreaderZombie)) &&
                   hand.Count > 0;
        }

        // ------------------------------------------------------------------ Dark Armed Dragon: exactly 3 DARKs
        //
        // Dark Armed Dragon can only be Special Summoned with exactly 3 DARK monsters in our graveyard. Once we've drawn
        // it (only then: no point restricting the graveyard for a card we may never see), steer the count to 3:
        //  - below 3: send DARKs as usual (Armageddon Knight, Dark Grepher),
        //  - at 3: don't add more (decline Armageddon Knight's send, skip Grepher's +2, mill the minimum with Card Trooper),
        //  - above 3: trim with banishing costs: Vayu's synchro (Vayu + a Blackwing partner, -2), Chaos Sorcerer (-1).



        /// <summary>With DAD in hand, whether adding this many DARKs to the graveyard would take it past 3. Returns the reason, or null.</summary>
        private async Task<string?> DadBlocksAddingDarksAsync(int adding)
        {
            if (!await DadInHandAsync())
                return null;
            int darks = await DarksInGraveAsync();
            return darks + adding > 3
                ? $"Dark Armed Dragon is in hand and needs exactly 3 DARKs in the graveyard (have {darks}, this adds {adding})"
                : null;
        }

        // Blackwing synchro Vayu makes for each Level (Vayu L1 + partner), and its ATK (Vayu negates its effects).
        // The Blackwing synchro Vayu makes at each Level, and its ATK; 0 when none is left in our Extra Deck (user, 2026-11-11:
        // plans counted on an Armor Master after both were used, and Vayu's cost then only offered Sirocco).
        private int VayuSynchroAtk(int level) => !_extraVayuLevels.Contains(level) ? 0 : level switch { 6 => 2300, 7 => 2500, 8 => 2800, _ => 0 };

        // Levels whose Vayu synchro is still in our Extra Deck (refreshed with the field).
        private HashSet<int> _extraVayuLevels = new() { 6, 7, 8 };

        private static string VayuSynchroName(int level) => level switch
        {
            6 => CardNames.Get(CardId.BlackwingArmedWing),
            7 => CardNames.Get(CardId.BlackwingArmorMaster),
            8 => CardNames.Get(CardId.BlackwingSilverwindTheAscendant),
            _ => $"a Level {level} synchro"
        };

        /// <summary>
        /// Which partner to banish with Vayu: the lowest one that does the job, so the bigger ones stay for later.
        ///  1. Lethal: the lowest partner whose synchro makes our board lethal this turn.
        ///  2. A threat: the lowest partner whose synchro answers the biggest threat any of them can answer.
        ///  3. ATK doesn't matter and both routes are open: Armor Master, keeping the Armed Wing route (Sirocco -> Armed
        ///     Wing, + Plaguespreader = a Level 8) for later, unless we use it with Plaguespreader this turn;
        ///     otherwise the lowest.
        /// </summary>
        private async Task<((DynamicCard Card, int Index) Partner, string Why)> ChooseVayuPartnerAsync(List<(DynamicCard Card, int Index)> partners)
        {
            var lowestFirst = partners.OrderBy(x => x.Card.Level).ToList();
            var board = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList();
            var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool battleAhead = (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.Main1 && CurrentHeader.CurrentTurn > 1;

            if (battleAhead)
            {
                foreach (var x in lowestFirst)
                {
                    int atk = VayuSynchroAtk(x.Card.Level + 1);
                    if (atk > 0 && BoardAssessment.TurnsToWin(board.Append(atk), defenders, oppLp) == 1)
                        return (x, $"the lowest that makes us lethal this turn ({atk} ATK)");
                }
            }

            // Only big threats steer the partner: answering a small one (Giant Rat) isn't worth a bigger partner.
            var threats = (await BigThreatsAsync(BoardAssessment.Threats(board, defenders)))
                .Where(t => !t.BouncesAttacker).OrderByDescending(t => t.Power).ToList();
            foreach (var threat in threats)
            {
                foreach (var x in lowestFirst)
                {
                    int atk = VayuSynchroAtk(x.Card.Level + 1);
                    if (atk > threat.Power || (atk == threat.Power && threat.AttackPosition))
                        return (x, $"the lowest that answers {CardNames.Get(threat.Id)} ({threat.Power}) with {atk} ATK");
                }
            }

            // Both routes open (Sirocco -> Armed Wing, or an Armed Wing / Elphin partner -> Armor Master): Armor Master,
            // keeping Sirocco for the Armed Wing route, which pays off with Plaguespreader (Armed Wing L6 + Plaguespreader
            // = a Level 8). Only take the Armed Wing route now when we'll use it with Plaguespreader this turn: the
            // Level 8 answers something now and Plaguespreader can come back.
            var armedWingRoute = lowestFirst.FirstOrDefault(x => x.Card.Level + 1 == 6);
            var armorMasterRoute = lowestFirst.FirstOrDefault(x => x.Card.Level + 1 == 7);
            if (armedWingRoute.Card.ID > 0 && armorMasterRoute.Card.ID > 0)
            {
                if (await PlaguespreaderUsableAsync() &&
                    await BestSynchroForLevelAsync(8, await GetEnemyMonsterCardsAsync(), board) is { Answer: string needed } level8)
                    return (armedWingRoute, $"Armed Wing + Plaguespreader makes {CardNames.Get(level8.Synchro.Id)} this turn: {needed}");
                return (armorMasterRoute, "ATK doesn't matter: Armor Master, keeping Sirocco for the Armed Wing + Plaguespreader " +
                                          "Level 8 route (not used with Plaguespreader this turn)");
            }
            return (lowestFirst[0], "ATK doesn't matter: the lowest partner, keeping the bigger ones for later");
        }

        /// <summary>
        /// Whether Plaguespreader can come back from the graveyard: it must be there, and its cost puts a card from our
        /// hand on top of the deck, so we need at least one card in hand (an empty hand means no Plaguespreader route).
        /// </summary>
        private async Task<bool> PlaguespreaderUsableAsync() =>
            (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.PlaguespreaderZombie)) &&
            (await GetBotHandCardsAsync()).Count > 0;

        /// <summary>
        /// Whether, after a Vayu synchro, we could bring Vayu and its partner back (which makes this Vayu cheaper; the
        /// recursion itself is only fired later, when a Vayu play actually needs it):
        ///  - Miracle Dig in hand that isn't usable yet, but will be once Vayu's cost banishes 2 more (it needs
        ///    5+ of our monsters banished, and returns 3), or
        ///  - Burial from a Different Dimension in hand (returns up to 3 banished monsters).
        /// Returns the reason (for the log), or null.
        /// </summary>
        private async Task<string?> RecursionAfterVayuAsync(IdleCmdData state, List<DynamicCard> hand)
        {
            int banishedMonsters = (await CurrentField.GetCardsInLocation(CardLocation.Removed))
                .Count(card => card.ID > 0 && card.HasType(CardType.Monster));
            bool plagueBanished = (await CurrentField.GetCardsInLocation(CardLocation.Removed))
                .Any(card => card.IsCode(CardId.PlaguespreaderZombie));
            string extra = plagueBanished ? ", and Plaguespreader for another synchro" : "";
            if (hand.Any(card => card.IsCode(CardId.MiracleDig)) && !TryGetActivable(state, CardId.MiracleDig, out _) &&
                banishedMonsters < 5 && banishedMonsters + 2 >= 5)
                return $"its cost banishes 2 more, switching on Miracle Dig ({banishedMonsters} -> {banishedMonsters + 2} of 5 needed), " +
                       $"which brings Vayu and its partner back{extra}";
            if (hand.Any(card => card.IsCode(CardId.BurialFromADifferentDimension)))
                return $"Burial from a Different Dimension in hand brings Vayu and its partner back afterwards{extra}";
            return null;
        }

        // Banished monsters worth getting back with Burial: Vayu and its partners, Necro Gardna, Plaguespreader (they
        // work from the graveyard). Other monsters there don't really matter.
        private static readonly int[] BurialWorthy =
        {
            CardId.BlackwingVayuTheEmblemOfHonor,
            CardId.BlackwingSiroccoTheDawn,
            CardId.BlackwingElphinTheRaven,
            CardId.BlackwingArmedWing,
            CardId.BlackwingArmorMaster,
            CardId.NecroGardna,
            CardId.PlaguespreaderZombie
        };

        /// <summary>Burial-worthy banished monsters needed to set it as bait (chained when they try to remove it).</summary>
        [Tunable("Burial-worthy banished monsters needed to set it as bait (chained when they try to remove it).")]
        public int BurialBaitAt { get; set; } = 2;

        // Our Burial-worthy banished monsters, refreshed at main-phase / chain decisions.
        private int _ourBanishedMonsters;

        private async Task<int> RefreshOurBanishedAsync()
        {
            _ourBanishedMonsters = (await CurrentField.GetCardsInLocation(CardLocation.Removed))
                .Count(card => card.IsCode(BurialWorthy));
            return _ourBanishedMonsters;
        }

        // A set Burial is bait too, but only with enough worth bringing back when it's chained.
        // A set Icarus Attack with a Winged Beast out to tribute is bait as well: used rather than lost to their removal
        // (user, 2026-11-11).
        protected override IReadOnlyCollection<int> BaitCardIds =>
            DefaultBaitCards
                .Concat(_ourBanishedMonsters >= BurialBaitAt ? new[] { CardId.BurialFromADifferentDimension } : Array.Empty<int>())
                .Concat(_wingedOnField > 0 ? new[] { CardId.IcarusAttack } : Array.Empty<int>())
                .ToArray();

        /// <summary>Gorz hold roll: hand plays plus graveyard plays (each live Vayu pair, Plaguespreader).</summary>
        protected override async Task<int> AlternativePlaysAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            int pairs = Math.Min(grave.Count(c => c.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)), grave.Count(c => c.IsCode(VayuPartners)));
            int plague = grave.Any(c => c.IsCode(CardId.PlaguespreaderZombie)) ? 1 : 0;
            return await base.AlternativePlaysAsync() + pairs + plague;
        }

        // A Heavy Storm doesn't cost us a set Burial (Quick-Play: chained in response).
        protected override IReadOnlyCollection<int> StormProofSets { get; } = new[]
        {
            JarOfGreed, LegacyOfYataGarasu, TrapDustshoot, CardId.BurialFromADifferentDimension
        };

        /// <summary>
        /// Semi-expendable here, only while we have Vayu pieces to use them with:
        ///  - Armed Wing / Armor Master: once destroyed they're Vayu partners (losing one feeds the next Vayu synchro), but
        ///    they're still big bodies, so never thrown away as "cheap" (see IsCheapAttacker);
        ///  - Card Trooper (draws when destroyed): without Vayu pieces its mills are what finds them, so worth keeping.
        /// </summary>
        protected override bool IsExpendable(DynamicCard card) =>
            base.IsExpendable(card) ||
            card.IsCode(CardId.BlackwingArmedWing, CardId.BlackwingArmorMaster, CardId.CardTrooper) && _haveVayuPieces;

        // Whether we have a Vayu to work with: one in the graveyard, Armageddon Knight in hand (sends one), or Dark
        // Grepher in hand / on the field with another DARK in hand to discard (sends one). Refreshed every decision.
        private bool _haveVayuPieces;

        protected override void OnFieldRefreshed(FieldInfo field)
        {
            var extra = field.OwnSide.Extra ?? Array.Empty<int>();
            if (extra.Any(code => code > 0))
                _extraVayuLevels = new[] { (6, CardId.BlackwingArmedWing), (7, CardId.BlackwingArmorMaster), (8, CardId.BlackwingSilverwindTheAscendant) }
                    .Where(x => extra.Any(code => Search.SearchHost.Is(code, x.Item2))).Select(x => x.Item1).ToHashSet();
            var own = field.OwnSide;
            var grave = own.Grave ?? Array.Empty<int>();
            var hand = own.Hand ?? Array.Empty<int>();
            var monsters = (own.MonsterZones ?? Array.Empty<CardArray>())
                .Select(zone => zone.Cards is { Length: > 0 } cards ? cards[0] : 0).ToList();
            bool grepher = hand.Contains(CardId.DarkGrepher) || monsters.Contains(CardId.DarkGrepher);
            _wingedOnField = monsters.Count(id => OurBlackwings.Contains(id));
            _icarusSet = (own.SpellZones ?? Array.Empty<CardArray>())
                .Count(zone => zone.Cards is { Length: > 0 } cards && cards[0] == CardId.IcarusAttack);
            _haveVayuPieces = grave.Contains(CardId.BlackwingVayuTheEmblemOfHonor) ||
                              hand.Contains(CardId.ArmageddonKnight) ||
                              grepher && hand.Any(id => CardRules.IsGoodDarkDiscard(id));
        }

        // This brain has its own Gorz / Mirror Force / Torrential chain rules (see OnSelectChainAsync).
        // Version history (DuelSim records it per duel): 1 = 2026-11-09 first build (started from the Vayu brain: the
        // Vayu / Sirocco / DAD / synchro logic; the Blackwing aggro play is at the end of this file).
        public override int BrainVersion => 7; // v7: Blizzard chained beside Whirlwind, Icarus set/timing/tribute, Sirocco with Whirlwind; v6: the BlackwingHybrid list (variant flag), Return from the Different Dimension, Solemn chained

        /// <summary>
        /// Play search setup: Dark Armed Dragon in hand comes out with exactly 3 DARK monsters in our graveyard (2 is one
        /// mill away; 4+ shuts it off until one leaves).
        /// </summary>
        // The MCTS's card values for this deck (user, 2026-11-11): Vayu in the graveyard (a synchro), Kalut in hand (+1400 in
        // battle, user 2026-11-10), Black Whirlwind face-up (every Blackwing summon searches).
        internal override double SearchCardBonus(int code, BotTest.Game.AI.Brains.CardPlace place) =>
            // Hybrid: the Vayu brain's graveyard values (B3, user 2026-11-16: "hybrid should have the same vayu, sirocco, armageddon
            // and grepher stuff as vayu").
place == BotTest.Game.AI.Brains.CardPlace.Hand && Search.SearchHost.Is(code, CardId.BlackwingKalutTheMoonShadow) ? KalutHandValue
            : place == BotTest.Game.AI.Brains.CardPlace.Field && Search.SearchHost.Is(code, CardId.BlackWhirlwind) ? WhirlwindFieldValue
            : 0;

        /// <summary>
        /// Black Whirlwind ours (face-up, or in hand) and triggered this turn (its search already used on this turn's Normal Summon): a good
        /// Blackwing Normal Summon kept in hand for next turn is worth WhirlwindNextSummonValue (its summon then searches a smaller Blackwing).
        /// User, 2026-11-18: "you should get a bonus for keeping a good normal summon in hand for the next turn, but only if you've already
        /// normal summoned that turn"; "I want this only to apply if we did trigger blackwhirlwind this turn".
        /// </summary>
        internal override double SearchNextTurnValue(DuelSim.Engine.SideSummary own, IReadOnlyCollection<int> activatedThisTurn)
        {
            if (!activatedThisTurn.Any(c => Search.SearchHost.Is(c, CardId.BlackWhirlwind)))
                return 0;
            bool whirlwind = own.SpellCodes.Any(s => s.FaceUp && Search.SearchHost.Is(s.Code, CardId.BlackWhirlwind)) ||
                             own.HandCodes.Any(c => Search.SearchHost.Is(c, CardId.BlackWhirlwind));
            if (!whirlwind)
                return 0;
            bool goodSummon = own.HandCodes.Any(c => Search.SearchHost.Available && Search.SearchHost.Cards.Get(c) is { } r &&
                (r.Type & 0x1) != 0 && r.Level <= 4 && r.Attack >= WhirlwindGoodSummonAtk && (r.Name ?? "").StartsWith("Blackwing - ") &&
                !Search.SearchHost.Is(c, CardId.BlackwingVayuTheEmblemOfHonor));
            return goodSummon ? WhirlwindNextSummonValue : 0;
        }

        [Tunable("Black Whirlwind ours and triggered this turn: a Level 4-or-lower Blackwing of at least WhirlwindGoodSummonAtk ATK kept in hand for next turn is worth this (card units). User, 2026-11-18.")]
        public double WhirlwindNextSummonValue { get; set; } = 0.5;

        [Tunable("The ATK from which a Blackwing in hand is a good Normal Summon for Black Whirlwind next turn (it searches a smaller Blackwing).")]
        public int WhirlwindGoodSummonAtk { get; set; } = 1700; // user, 2026-11-18: "at least 1700 attack ... bora and above count"

        internal override double SearchSetupValue(DuelSim.Engine.SideSummary own)
        {
            double value = 0;
            // A face-up Card Trooper of ours is a mill engine for Vayu and Dark Armed Dragon (from the Vayu brain, 2026-11-10).
            if (own.Monsters.Any(m => m.FaceUp && Search.SearchHost.Is(m.Code, CardId.CardTrooper)))
                value += TrooperSetupValue;
            if (own.HandCodes.Any(code => Search.SearchHost.Is(code, CardId.DarkArmedDragon)))
            {
                int darks = own.GraveCodes.Count(code => (Search.SearchHost.Type(code) & 0x1) != 0 && Search.SearchHost.Attribute(code) == 0x20);
                value += darks == 3 ? DadLiveSetupValue : darks == 2 ? DadNearSetupValue : 0;
            }
            // Vayu pairs in the graveyard: each worth a share of the synchro it makes (user, 2026-11-17; replaces the Sirocco-only pair).
            return value;
        }

        protected override bool HandlesStapleChains => true;
        // ...and its own main-phase Heavy Storm / MST rules.
        protected override bool HandlesStapleIdle => true;

        // Into an empty field the generic brain attacks smallest first; Chaos Sorcerer always goes last.
        protected override IReadOnlyCollection<int> AttackLast { get; } = new[] { CardId.ChaosSorcerer };
        // Chaos Sorcerer's banish works from defence: with a reasonable Mirror Force chance it sits in defence.
        protected override IReadOnlyCollection<int> DefenceUnderMirrorForce { get; } = new[] { CardId.ChaosSorcerer };

        private static readonly int[] PriorityWindowEffects =
        {
            CardId.DarkGrepher, CardId.CardTrooper, CardId.DarkArmedDragon, CardId.ChaosSorcerer
        };


        // ------------------------------------------------------------------ yes/no prompts

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            var card = context.StateData.EffectCard;
            if (!await GoodDiscardDeckSendAllowedAsync(card.ID, context.Header, context.Bot))
                return false;

            // Armageddon Knight sends a DARK: not while that would take Dark Armed Dragon's graveyard past exactly 3.
            if (card.IsCode(CardId.ArmageddonKnight) && await DadBlocksAddingDarksAsync(1) is string dadWhy && Rule(BlackwingRules.HoldKnightForDad))
            {
                Note(context, $"don't use Armageddon Knight's effect: {dadWhy}");
                return false;
            }

            if (card.IsCode(CardId.BlackRoseDragon))
            {
                int enemyCards = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
                int botCards = (await GetBotMonsterCardsAsync()).Count + (await CurrentField.GetCardsInSpellZone()).Count();
                bool wipe = enemyCards > botCards && await DestructionNegatedAsync(context, CardId.BlackRoseDragon) == null && Rule(BlackwingRules.BlackRoseWipe);
                Note(context, $"Black Rose wipe? opponent {enemyCards} vs our {botCards} -> {(wipe ? "yes" : "no")}");
                return wipe;
            }

            if (card.IsCode(CardId.GorzTheEmissaryOfDarkness))
                return await GorzNowAsync(context, await GetEnemyMonsterCardsAsync()) != null;

            // Every other optional effect in this deck is worth using (Armageddon Knight, Ryko, Goyo, Colossal Fighter...).
            Note(context, $"use optional effect of {CardNames.Get(card.ID)}");
            return true;
        }

        // ------------------------------------------------------------------ card selection

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            // Sirocco's boost as the one-attack lethal: Bora (card rule, CardRules SiroccoTargets; user, 2026-11-25).
            if (await SiroccoLethalBoraPickAsync(context) is IList<byte> lethalBora)
            {
                Note(context, "Sirocco's boost is the one-attack lethal: on Bora");
                return lethalBora;
            }
            // Sirocco's boost: Bora / Shura, whoever activated it.
            if (SiroccoPick(context.StateData.SelectableCards ?? Array.Empty<DynamicCard>(), context.StateData.Minimum, context.StateData.Maximum) is IList<byte> hard)
            {
                Note(context, "Sirocco's boost on Bora / Shura");
                return hard;
            }
            // MST chained by a shared staple rule (e.g. at a continuous card being activated): its target first.
            if (StapleTargets(context) is IList<byte> stapleTarget)
                return stapleTarget;
            if (await SelectBlackwingAsync(context) is IList<byte> blackwingPick)
                return blackwingPick;
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int count = Math.Max(1, state.Minimum);
            var hint = (DHint)state.SelectionHint;

            if (cards.Length == 0 || count > cards.Length)
                return await base.OnSelectCardAsync(context, ct);

            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            var opponent = indexed.Where(x => x.Card.CurrentCoordinates.Controller != 0).ToList();

            // Burial from a Different Dimension / Miracle Dig (it returns 3): banished monsters back to the graveyard,
            // counting what's already there. Every banished Vayu first (the scarce half of a play), keeping one slot for
            // a partner (highest Level) only if the graveyard would otherwise have none. Then whatever makes the most
            // plays: partners while Vayus outnumber them, Plaguespreader (a synchro play of its own), and Necro Gardna
            // (only a nice to have) last. The opponent's cards only if the minimum forces it.
            if (hint != DHint.REMOVE && indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Removed))
            {
                var mine = indexed.Where(x => x.Card.CurrentCoordinates.Controller == 0).ToList();
                var grave = await GetBotGraveyardCardsAsync();
                int vayus = grave.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
                int partners = grave.Count(card => card.IsCode(VayuPartners));
                bool plague = grave.Any(card => card.IsCode(CardId.PlaguespreaderZombie));
                bool necro = grave.Any(card => card.IsCode(CardId.NecroGardna));

                double Value((DynamicCard Card, int Index) x)
                {
                    if (x.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor))
                        return vayus < partners ? BurialRankVayuShort : vayus == partners ? BurialRankVayuEven : BurialRankVayuSpare;
                    if (x.Card.IsCode(VayuPartners))
                        // A partner beyond the Vayus makes no play: below Necro Gardna (user, 2026-11-11: Miracle Dig returned a third
                        // Armed Wing for two Vayus, when Necro Gardna was the better pick).
                        return (partners < vayus ? BurialRankPartnerShort : partners == vayus ? BurialRankPartnerEven : BurialRankPartnerSpare) + x.Card.Level * 0.01; // higher Level first
                    if (x.Card.IsCode(CardId.PlaguespreaderZombie))
                        return plague ? BurialRankPlagueSecond : BurialRankPlague;
                    if (x.Card.IsCode(CardId.NecroGardna))
                        return necro ? BurialRankNecroSecond : BurialRankNecro;
                    return -PriorityIndex(BurialPriority, x.Card) * 0.01;
                }

                var picks = new List<(DynamicCard Card, int Index)>();
                var left = new List<(DynamicCard Card, int Index)>(mine);
                int slots = Math.Max(state.Maximum, state.Minimum);

                // Vayus first: they're the scarce half of every play (partners are much easier to get back: Armageddon
                // Knight, Grepher, a synchro dying). Keep one slot for a partner only if the graveyard would otherwise
                // have none, so at least one play is ready now.
                var banishedVayus = left.Where(x => x.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)).ToList();
                bool needPartner = partners == 0 && left.Any(x => x.Card.IsCode(VayuPartners));
                foreach (var vayu in banishedVayus.Take(Math.Max(needPartner ? Math.Min(1, slots) : 0, slots - (needPartner ? 1 : 0))))
                {
                    picks.Add(vayu);
                    left.Remove(vayu);
                    vayus++;
                }
                if (needPartner && picks.Count < slots)
                {
                    var partner = left.Where(x => x.Card.IsCode(VayuPartners)).OrderByDescending(x => x.Card.Level).First();
                    picks.Add(partner);
                    left.Remove(partner);
                    partners++;
                }

                while (picks.Count < slots && left.Count > 0)
                {
                    var best = left.OrderByDescending(Value).First();
                    picks.Add(best);
                    left.Remove(best);
                    if (best.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) vayus++;
                    else if (best.Card.IsCode(VayuPartners)) partners++;
                    else if (best.Card.IsCode(CardId.PlaguespreaderZombie)) plague = true;
                    else if (best.Card.IsCode(CardId.NecroGardna)) necro = true;
                }
                if (picks.Count < state.Minimum)
                    picks.AddRange(indexed.Where(x => !picks.Contains(x)).Take(state.Minimum - picks.Count));
                if (picks.Count > 0)
                {
                    Note(context, $"banished to graveyard: {string.Join(", ", picks.Select(x => DescribePick(x.Card)))}");
                    return picks.Select(x => (byte)x.Index).ToList();
                }
            }

            // Removal and targeting on the opponent's cards, and Charge of the Light Brigade's search: shared rules.
            if (await SelectOpponentTargetsAsync(context) is IList<byte> removal)
                return removal;
            if (await SelectLightswornSearchAsync(context) is IList<byte> search)
                return search;

            // A Blackwing to add from the deck (Black Whirlwind's search): what makes a play. Bora and Gale summon themselves
            // from the hand next to a Blackwing (free bodies once the Normal Summon is spent, and Gale is a Tuner for them),
            // then Kalut (+1400 from the hand), Blizzard, Shura, Vayu. A card we already hold counts after the others, so a
            // second search adds something new.
            if (hint == DHint.ATOHAND && indexed.Count > 0 &&
                indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck && IsBlackwing(x.Card)))
            {
                int[] order = { CardId.BlackwingBoraTheSpear, CardId.BlackwingGaleTheWhirlwind, CardId.BlackwingKalutTheMoonShadow,
                    CardId.BlackwingBlizzardTheFarNorth, CardId.BlackwingShuraTheBlueFlame, CardId.BlackwingVayuTheEmblemOfHonor };
                // Kalut summoned as the only Normal Summon with Whirlwind out: the search is Blizzard (user, 2026-11-10).
                if (LastMainMove is { Action: MainAction.Summon } kalutMove && kalutMove.Turn == context.Header.CurrentTurn &&
                    (kalutMove.CardId == CardId.BlackwingKalutTheMoonShadow || Search.SearchHost.CanonicalId(kalutMove.CardId) == CardId.BlackwingKalutTheMoonShadow))
                    order = new[] { CardId.BlackwingBlizzardTheFarNorth }.Concat(order).ToArray();
                var held = await GetBotHandCardsAsync();
                var pick = indexed
                    .OrderBy(x => held.Any(h => h.IsCode(x.Card.ID)) ? 1 : 0)
                    .ThenBy(x => StaplePriorityIndex(order, x.Card))
                    .First();
                Note(context, $"search {CardNames.Get(pick.Card.ID)}" +
                              (held.Any(h => h.IsCode(pick.Card.ID)) ? "" : " (not in hand yet)"));
                return new List<byte> { (byte)pick.Index };
            }

            if (hint == DHint.DISCARD)
                return Ordered(context, hint, indexed, count, PreferDiscard, preferLowValue: true);

            if (hint == DHint.TOGRAVE)
            {
                bool fromDeck = indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck);
                if (fromDeck)
                {
                    var tagged = indexed.Where(x => CardRules.IsGoodDarkDiscard(x.Card.ID)).ToList();
                    if (tagged.Count >= count)
                        indexed = tagged;
                }
                return fromDeck
                    ? Ordered(context, hint, indexed, count, await BuildSendFromDeckPriorityAsync(context), preferLowValue: false)
                    : Ordered(context, hint, indexed, count, PreferDiscard, preferLowValue: true);
            }

            if (hint == DHint.REMOVE && opponent.Count == 0 &&
                indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Grave))
            {
                // Vayu's cost: banish Vayu + a non-Tuner Blackwing partner; the partner's Level decides the synchro
                // (Sirocco L5 -> Armed Wing 2300, Armed Wing / Elphin L6 -> Armor Master 2500, Armor Master L7 ->
                // Silverwind the Ascendant 2800). Use the lowest partner that does the job, keeping the bigger ones.
                if (count == 1 && _lastActivatedCard == CardId.BlackwingVayuTheEmblemOfHonor && VayuWasTheMove(context.Header) &&
                    indexed.Any(x => x.Card.IsCode(VayuPartners)) && !indexed.All(x => x.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)))
                {
                    var candidates = indexed.Where(x => x.Card.IsCode(VayuPartners)).ToList();
                    // Make the synchro the decision to use Vayu was based on, if that partner is still there.
                    int? planned = _vayuPlannedTurn == context.Header.CurrentTurn ? _vayuPlannedLevel : null;
                    _vayuPlannedLevel = null;
                    var plannedPartner = candidates.FirstOrDefault(x => planned.HasValue && x.Card.Level + 1 == planned.Value);
                    if (plannedPartner.Card.ID > 0)
                    {
                        Note(context, $"Vayu's partner: {DescribePick(plannedPartner.Card)} -> {VayuSynchroName(planned!.Value)} (as planned when deciding to use Vayu)");
                        return new List<byte> { (byte)plannedPartner.Index };
                    }
                    var (partner, partnerWhy) = await ChooseVayuPartnerAsync(candidates);
                    Note(context, $"Vayu's partner: {DescribePick(partner.Card)} -> {VayuSynchroName(partner.Card.Level + 1)} ({partnerWhy})");
                    return new List<byte> { (byte)partner.Index };
                }
                return Ordered(context, hint, indexed, count, PreferBanishFromGrave, preferLowValue: true);
            }

            // Our own hand card to the top of the deck (Plaguespreader's cost): we draw it again next turn, so return
            // what we can't use before then. Monsters/spells that only work on our turn go first; traps we can set and
            // hand traps (Gorz) that protect us on the opponent's turn go last.
            if (hint == DHint.TODECK && opponent.Count == 0 &&
                indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Hand))
            {
                // Dark Armed Dragon that can be live this turn is the card we're doing this for: never send it back.
                // (Plaguespreader leaving the graveyard takes it 1 DARK closer to 3; Vayu trims 2 at a time.)
                var tdGrave = await GetBotGraveyardCardsAsync();
                int tdDarks = tdGrave.Count(c => c.HasType(CardType.Monster) && (c.Attribute & (int)CardAttribute.Dark) != 0)
                              - (tdGrave.Any(c => c.IsCode(CardId.PlaguespreaderZombie)) ? 1 : 0);
                int tdPairs = Math.Min(tdGrave.Count(c => c.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)), tdGrave.Count(c => c.IsCode(VayuPartners)));
                bool dadLiveThisTurn = tdDarks == 3 || tdDarks > 3 && (tdDarks - 3) % 2 == 0 && (tdDarks - 3) / 2 <= tdPairs;
                int Rank(DynamicCard card) =>
                    card.IsCode(CardId.DarkArmedDragon) && dadLiveThisTurn ? 5
                    : card.IsCode(CardId.GorzTheEmissaryOfDarkness) ? 3
                    : card.HasType(CardType.Trap) || card.HasType(CardType.QuickPlay) ? 2
                    : card.HasType(CardType.Spell) ? 1
                    : 0;
                var picks = indexed.OrderBy(x => Rank(x.Card)).ThenByDescending(x => x.Card.Level).Take(count).ToList();
                Note(context, $"TODECK from hand: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))} (can't be used before our next draw; " +
                              $"keep traps and Gorz{(dadLiveThisTurn ? ", and Dark Armed Dragon: it's live this turn once Plaguespreader leaves the graveyard" : "")})");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            return await base.OnSelectCardAsync(context, ct);
        }

        /// <summary>
        /// What to send from the deck to the graveyard (Armageddon Knight / Dark Grepher), based on the current graveyard:
        ///  1. complete a Vayu pair (Vayu if a partner is there; Sirocco if Vayu is there and no partner is there or expected),
        ///  2. Necro Gardna if there is none in the graveyard yet,
        ///  3. start a Vayu pair: Vayu, then Sirocco unless an Armed Wing / Armor Master is in the graveyard or on our field,
        ///  4. everything else.
        /// </summary>
        private async Task<int[]> BuildSendFromDeckPriorityAsync(DecisionContext<SelectCardData> context)
        {
            var (order, why) = await DarkSendPriorityAsync();
            Note(context, $"send-from-deck priority: {string.Join(" > ", order.Select(CardNames.Get))} ({why})");
            return order.ToArray();
        }

        // ------------------------------------------------------------------ resource management

        /// <summary>
        /// Vayus beyond the one we keep for answering a threat. Each Vayu play needs a Vayu and a partner, but partners
        /// recycle (the synchro we make goes to the graveyard when it dies and partners the next Vayu: Armed Wing ->
        /// Armor Master -> Silverwind), so over a game the Vayus in the graveyard are the limiting resource.
        /// </summary>
        private async Task<int> SpareVayusAsync() =>
            Math.Max(0, (await GetBotGraveyardCardsAsync()).Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) - 1);


        // Blackwing synchro Vayu would make from the graveyard, by partner (Vayu L1 + partner level).
        // Note: Vayu negates the summoned synchro's effects, so only its ATK matters here.
        /// <summary>The biggest synchro Vayu could make from our graveyard (for "could we beat X" checks).</summary>
        private async Task<int> BestVayuSynchroAtkAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            if (grave.Any(card => card.IsCode(CardId.BlackwingArmorMaster)))
                return 2800; // Silverwind
            if (grave.Any(card => card.IsCode(CardId.BlackwingElphinTheRaven, CardId.BlackwingArmedWing)))
                return 2500; // Armor Master
            return 2300;     // Armed Wing (with Sirocco)
        }

        // The synchro Level a decision to use Vayu was based on: the partner choice (Vayu's cost) then makes exactly that.
        private int? _vayuPlannedLevel;
        private int _vayuPlannedTurn = -1;
        // Vayu -> Armed Wing planned with Plaguespreader into a Level 8 this turn.
        private int _vayuPlagueComboTurn = -1;
        private int _vayuPlagueComboSynchro;

        /// <summary>
        /// Armed Wing as a stepping stone to Armor Master: returns why, or null. Needs a big threat at 2301-2500 that
        /// nothing else of ours answers, at least 2 Vayus in the graveyard, and no Armor Master partner (Armed Wing / Elphin)
        /// there yet, so this Armed Wing becomes it.
        /// </summary>
        private async Task<string?> ArmedWingSteppingStoneAsync(IdleCmdData state, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters)
        {
            var grave = await GetBotGraveyardCardsAsync();
            if (grave.Count(c => c.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) < 2 ||
                grave.Any(c => c.IsCode(CardId.BlackwingArmedWing, CardId.BlackwingElphinTheRaven)))
                return null;
            DynamicCard target = default;
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup() && card.GetRealPower() is > 2300 and <= 2500))
                if (await IsBigThreatAsync(enemy) && (target.ID <= 0 || enemy.GetRealPower() > target.GetRealPower()))
                    target = enemy;
            if (target.ID <= 0)
                return null;
            int power = target.GetRealPower();
            // Any other way to answer it?
            if (botMonsters.Any(card => card.IsFaceup() && EffectiveAttack(card) > power))
                return null;
            if (TryGetSpecialSummon(state, CardId.DarkArmedDragon, out _) || TryGetSpecialSummon(state, CardId.ChaosSorcerer, out _))
                return null;
            if (await PlaguespreaderUsableAsync() &&
                await BestSynchroForLevelAsync(8, enemyMonsters, botMonsters.Select(EffectiveAttack).ToList()) is { } l8 && l8.Synchro.Attack > power)
                return null;
            return $"Vayu -> Armed Wing as a stepping stone: it can't beat {CardNames.Get(target.ID)} ({power}) itself, but once it's in the " +
                   "graveyard the next Vayu makes Armor Master (2500), and nothing else of ours answers it";
        }

        /// <summary>
        /// The synchro Vayu would actually make right now: the same partner choice the cost prompt uses (lowest that does
        /// the job). Decisions about using Vayu are judged on this, and it is recorded so the cost prompt follows it.
        /// </summary>
        private async Task<(int Level, int Atk, string Why)> PlannedVayuSynchroAsync()
        {
            var partners = (await GetBotGraveyardCardsAsync()).Where(card => card.IsCode(VayuPartners))
                .Select((card, i) => (Card: card, Index: i)).ToList();
            if (partners.Count == 0)
                return (0, 0, "no partner in the graveyard");
            var (partner, why) = await ChooseVayuPartnerAsync(partners);
            int level = partner.Card.Level + 1;
            return (level, VayuSynchroAtk(level), $"{VayuSynchroName(level)} with {CardNames.Get(partner.Card.ID)}: {why}");
        }


        // Guess which field monsters a synchro would use: a tuner plus a non-tuner making up the level.
        private static List<DynamicCard> GuessSynchroMaterials(DynamicCard synchro, List<DynamicCard> botMonsters)
        {
            foreach (var tuner in botMonsters.Where(card => card.HasType(CardType.Tuner)))
            {
                var nonTuner = botMonsters.FirstOrDefault(card => !card.HasType(CardType.Tuner) && card.Level == synchro.Level - tuner.Level);
                if (nonTuner.ID > 0)
                    return new List<DynamicCard> { tuner, nonTuner };
            }
            return new List<DynamicCard>();
        }

        // ------------------------------------------------------------------ combined lethal

        private string? _lastComboPlan;

        private int _lastStrongPlays;
        private readonly List<int> _lastStrongPlayCards = new();
        private readonly List<int> _lastComboPlanCards = new();

        /// <summary>A big play worth clearing the backrow for: lethal (board or combined plan), or 2+ strong summons.</summary>
        protected override async Task<string?> BigPlayPendingAsync(DecisionContext<IdleCmdData> context)
        {
            await TryComboLethalAsync(context, context.StateData, await GetBotMonsterCardsAsync(), await GetEnemyMonsterCardsAsync(),
                (await GetBotHandCardsAsync()).Count, dryRun: true);
            if (_lastComboPlan != null)
            {
                PendingBigPlayCards.Clear();
                PendingBigPlayCards.AddRange(_lastComboPlanCards);
                return $"combined lethal [{_lastComboPlan}]";
            }
            return await base.BigPlayPendingAsync(context);
        }

        /// <summary>
        /// Strong monsters we can bring out this turn, from the same play list as the combined-lethal check (DAD, Chaos,
        /// Vayu's synchro, Plaguespreader's synchro, Extra Deck synchros, the Normal Summon), within our hand.
        /// </summary>
        protected override async Task<int> StrongSummonsAvailableAsync(DecisionContext<IdleCmdData> context)
        {
            // The dry run in BigPlayPendingAsync has just counted them (it runs before this).
            int generic = await base.StrongSummonsAvailableAsync(context);
            if (_lastStrongPlays >= generic)
            {
                PendingBigPlayCards.Clear();
                PendingBigPlayCards.AddRange(_lastStrongPlayCards);
                return _lastStrongPlays;
            }
            return generic;
        }

        /// <summary>One play the bot could make this turn, as its effect on the battle simulation.</summary>
        private sealed class Extension
        {
            public required string Name { get; init; }
            /// <summary>Execution order (summons before the effects and synchros that use them).</summary>
            public required int Order { get; init; }
            /// <summary>How much the play spends (a field synchro goes -1, the Normal Summon is nearly free); the cheapest lethal set wins.</summary>
            public required double Cost { get; init; }
            /// <summary>Cards from hand it needs: summoning from hand, or Plaguespreader's cost of returning a card to the deck.</summary>
            public int HandCost { get; init; }
            /// <summary>DARK monsters it banishes from our graveyard (DAD's effect 1, a Vayu synchro 2: Vayu and its Blackwing partner, Chaos Sorcerer 1).</summary>
            public int DarkCost { get; init; }
            /// <summary>The main-phase action the play is (for the turn plan): activate by default.</summary>
            public MainAction Act { get; init; } = MainAction.Activate;
            public List<int> AddedAtk { get; } = new();
            public List<int> RemovedAtk { get; } = new();
            public int RemovesDefenders { get; init; }
            /// <summary>The card the play uses (summoned, activated, or synchro summoned), for big-play commitments.</summary>
            public int CardId { get; init; }
            /// <summary>A play this one depends on (e.g. DAD's effect needs DAD summoned first).</summary>
            public Extension? Requires { get; init; }
            public required Func<IdleCmdResponse?> Execute { get; init; }
        }

        /// <summary>
        /// Adds up every play available this turn (DAD, Chaos Sorcerer, DAD's effect, the Normal Summon, Vayu,
        /// Plaguespreader, Extra Deck synchros), and if together they are lethal, drops the ones that aren't needed
        /// and makes the first remaining play. The next main-phase decision recomputes the plan from the new board.
        /// </summary>
        private async Task<IdleCmdResponse?> TryComboLethalAsync(
            DecisionContext<IdleCmdData> context,
            IdleCmdData state,
            List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters,
            int handCount,
            bool dryRun = false)
        {
            _lastComboPlan = null;
            _lastStrongPlays = 0;
            _lastStrongPlayCards.Clear();
            var header = context.Header;
            if ((DuelPhase)header.CurrentPhase != DuelPhase.Main1 || header.CurrentTurn <= 1 || !state.CanBattlePhase)
                return null;

            var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var baseAttackers = botMonsters.Select(EffectiveAttack).ToList();
            if (BoardAssessment.TurnsToWin(baseAttackers, defenders, oppLp) == 1)
                return null; // already lethal: spend nothing more

            var grave = await GetBotGraveyardCardsAsync();
            var freeMaterials = new List<DynamicCard>(botMonsters);
            var plays = new List<Extension>();
            string planText = "";
            string Why(string step) => $"combined lethal [{planText}]: {step}";
            IdleCmdResponse? Activate(int id, string step) => TryActivate(context, state, id, Why(step), out var r) ? r : null;
            IdleCmdResponse? Summon(int id, string step) => TrySpecialSummon(context, state, id, Why(step), out var r) ? r : null;

            // Vayu first, so DAD's effect knows which DARKs in the graveyard Vayu needs.
            bool vayu = TryGetActivable(state, CardId.BlackwingVayuTheEmblemOfHonor, out _);
            int vayuPlaysNow = 0;
            if (vayu)
            {
                // One play per Vayu + partner pair in the graveyard (two, three...), each Vayu paired with the next-highest
                // partner. Each later play depends on the one before (the biggest first). The plan records the synchro each
                // play counts on, so the cost prompt makes that one (by then other plays are out and the partner choice
                // alone might go smaller); the next decision re-plans from the new board.
                var graveNow = await GetBotGraveyardCardsAsync();
                int graveVayus = graveNow.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
                var levels = graveNow.Where(card => card.IsCode(VayuPartners)).Select(card => card.Level + 1)
                    .OrderByDescending(level => level).Take(graveVayus).Where(level => VayuSynchroAtk(level) > 0).ToList();
                Extension? previous = null;
                foreach (int vayuLevel in levels)
                {
                    int level = vayuLevel;
                    var play = new Extension
                    {
                        Name = $"Vayu's synchro ({VayuSynchroName(level)})", Order = 5, Cost = 2, DarkCost = 2, CardId = CardId.BlackwingVayuTheEmblemOfHonor,
                        Requires = previous,
                        Execute = () =>
                        {
                            _vayuPlannedLevel = level;
                            _vayuPlannedTurn = context.Header.CurrentTurn;
                            return Activate(CardId.BlackwingVayuTheEmblemOfHonor, "Vayu's graveyard synchro");
                        }
                    };
                    play.AddedAtk.Add(VayuSynchroAtk(level));
                    plays.Add(play);
                    previous = play;
                    vayuPlaysNow++;
                }
            }

            // Miracle Dig / Burial, then Vayu: the extra Vayu plays the recursion creates beyond those available now (with
            // no Vayu play now, all of them). Fired first; the next decision re-plans with Vayu live.
            int recursion = new[] { CardId.MiracleDig, CardId.BurialFromADifferentDimension }
                .FirstOrDefault(id => TryGetActivable(state, id, out _));
            if (recursion != 0)
            {
                var (nowPlays, afterPlays, vayusBack, _) = await RecursionVayuPlaysAsync();
                // The planner already counts every Vayu play from the graveyard as it is; the recursion adds the rest.
                var extra = afterPlays.OrderByDescending(atk => atk).Skip(Math.Max(vayuPlaysNow, nowPlays.Count)).ToList();
                if (extra.Count > 0)
                {
                    bool fromHand = (await GetBotHandCardsAsync()).Any(card => card.IsCode(recursion));
                    var play = new Extension
                    {
                        Name = $"{CardNames.Get(recursion)} then Vayu ({vayusBack} Vayu(s) back: +{string.Join(" + ", extra)} ATK)",
                        Order = 4, Cost = RecursionPlayCost, HandCost = fromHand ? 1 : 0, CardId = recursion,
                        Execute = () => Activate(recursion, $"{CardNames.Get(recursion)}: return banished Vayu(s) and partner for the Vayu play")
                    };
                    play.AddedAtk.AddRange(extra);
                    plays.Add(play);
                }
            }

            Extension? dad = null;
            bool dadOnField = botMonsters.Any(card => card.IsCode(CardId.DarkArmedDragon) && card.IsFaceup());
            if (!dadOnField && TryGetSpecialSummon(state, CardId.DarkArmedDragon, out _))
            {
                dad = new Extension
                {
                    Name = "Dark Armed Dragon", Order = 1, Cost = 3, HandCost = 1, Act = MainAction.SpSummon, CardId = CardId.DarkArmedDragon,
                    Execute = () => Summon(CardId.DarkArmedDragon, "special summon Dark Armed Dragon")
                };
                dad.AddedAtk.Add(2800);
                plays.Add(dad);
            }

            bool chaos = TryGetSpecialSummon(state, CardId.ChaosSorcerer, out _);
            if (chaos)
            {
                var play = new Extension
                {
                    Name = "Chaos Sorcerer", Order = 2, Cost = 3, HandCost = 1, DarkCost = 1, Act = MainAction.SpSummon, CardId = CardId.ChaosSorcerer,
                    Execute = () => Summon(CardId.ChaosSorcerer, "special summon Chaos Sorcerer")
                };
                play.AddedAtk.Add(2300);
                plays.Add(play);
            }

            // DARK monsters in our graveyard: shared between DAD's uses, Vayu's synchros and Chaos Sorcerer's cost (a combination
            // may not spend more than there are; each play's DarkCost).
            int graveDarks = grave.Count(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0);
            if (dadOnField || dad != null)
            {
                // One use per DARK in the graveyard (user, 2026-11-12, round-robin 7: three DARKs, two uses counted). Each destroys
                // any card: their monsters first, then their set cards.
                for (int i = 0; i < graveDarks; i++)
                {
                    plays.Add(new Extension
                    {
                        Name = "DAD's effect", Order = 4, Cost = 1, DarkCost = 1, RemovesDefenders = 1, Requires = dad, CardId = CardId.DarkArmedDragon,
                        Execute = () => Activate(CardId.DarkArmedDragon, "destroy a blocker or a set card")
                    });
                }
            }

            Extension? normalSummon = null;
            var summonable = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Level <= 4 && !card.HasType(CardType.Flip))
                .OrderByDescending(EffectiveAttack)
                .FirstOrDefault();
            if (summonable.ID > 0)
            {
                var card = summonable;
                normalSummon = new Extension
                {
                    Name = $"Normal Summon {CardNames.Get(card.ID)}", Order = 3, Cost = NormalSummonPlayCost, HandCost = 1, Act = MainAction.Summon, CardId = card.ID,
                    Execute = () =>
                    {
                        Note(context, Why($"normal summon {CardNames.Get(card.ID)}"));
                        return card.NormalSummon();
                    }
                };
                normalSummon.AddedAtk.Add(EffectiveAttack(card));
                plays.Add(normalSummon);
                freeMaterials.Add(card);
            }

            if (TryGetActivable(state, CardId.PlaguespreaderZombie, out _))
            {
                // Biggest synchro per Level left in our actual Extra Deck.
                var atkByLevel = (await ExtraDeckSynchrosAsync())
                    .GroupBy(s => s.Level).ToDictionary(g => g.Key, g => g.Max(s => s.Attack));
                int SynchroAtkForLevel(int level) => atkByLevel.TryGetValue(level, out int atk) ? atk : 0;
                var material = freeMaterials
                    .Where(card => !card.HasType(CardType.Tuner) && SynchroAtkForLevel(card.Level + 2) > 0)
                    .OrderByDescending(card => SynchroAtkForLevel(card.Level + 2))
                    .FirstOrDefault();
                if (material.ID > 0 && SynchroAtkForLevel(material.Level + 2) > 0)
                {
                    var play = new Extension
                    {
                        Name = $"Plaguespreader + {CardNames.Get(material.ID)} synchro", Order = 6, Cost = 4, HandCost = 1, CardId = CardId.PlaguespreaderZombie,
                        Requires = normalSummon != null && material.Equals(summonable) ? normalSummon : null,
                        Execute = () => Activate(CardId.PlaguespreaderZombie, "Plaguespreader for a synchro")
                    };
                    play.AddedAtk.Add(SynchroAtkForLevel(material.Level + 2));
                    play.RemovedAtk.Add(EffectiveAttack(material));
                    plays.Add(play);
                    freeMaterials.Remove(material);
                }
            }

            foreach (var synchroId in SynchroPriority)
            {
                if (!TryGetSpecialSummon(state, synchroId, out var synchro))
                    continue;
                var materials = GuessSynchroMaterials(synchro, freeMaterials);
                if (materials.Count == 0)
                    continue;
                var play = new Extension
                {
                    Name = $"synchro {CardNames.Get(synchroId)}", Order = 7, Cost = 5, Act = MainAction.SpSummon, CardId = synchroId,
                    Requires = normalSummon != null && materials.Any(m => m.Equals(summonable)) ? normalSummon : null,
                    Execute = () => Summon(synchroId, $"synchro {CardNames.Get(synchroId)}")
                };
                play.AddedAtk.Add(synchro.Attack);
                play.RemovedAtk.AddRange(materials.Select(EffectiveAttack));
                plays.Add(play);
                foreach (var material in materials)
                    freeMaterials.Remove(material);
            }

            // Strong summons we could make together this turn (for "big play" decisions such as Heavy Storm).
            int handLeft = handCount;
            foreach (var play in plays.Where(p => p.AddedAtk.Any(atk => atk >= StrongMonsterAttack)).OrderBy(p => p.HandCost))
            {
                if (play.HandCost > handLeft)
                    continue;
                handLeft -= play.HandCost;
                _lastStrongPlays++;
                _lastStrongPlayCards.Add(play.CardId);
            }

            if (plays.Count < 2)
                return null; // a single play is handled by its own lethal check below

            // DAD's uses take targets in the same order as the real targeting (DeckBotBrain.Targeting, user 2026-11-12): the turn's
            // first use a monster, then monsters bigger than DAD, then their set cards (a set trap is the likeliest thing to
            // stop the lethal), then the other monsters. So the plan only counts as lethal if it still is after that, and set
            // cards it leaves standing cost extra (SetCardLeftCost each).
            int setCards = (await GetEnemySpellCardsAsync()).Count(card => !card.IsFaceup());
            (int Turns, int SetLeft) Simulate(IEnumerable<Extension> set)
            {
                var attackers = new List<int>(baseAttackers);
                var remaining = defenders.OrderByDescending(d => d.Power).ToList();
                int setLeft = setCards, dadUse = DadUsesThisTurn;
                foreach (var play in set.OrderBy(p => p.Order))
                {
                    attackers.AddRange(play.AddedAtk);
                    foreach (int atk in play.RemovedAtk)
                        attackers.Remove(atk);
                    for (int i = 0; i < play.RemovesDefenders; i++)
                    {
                        bool dadUseNow = play.Name == "DAD's effect";
                        if (dadUseNow)
                            dadUse++;
                        if (dadUseNow && dadUse > 1 && !(remaining.Count > 0 && remaining[0].Power >= DadAttack) && setLeft > 0)
                            setLeft--;
                        else if (remaining.Count > 0)
                            remaining.RemoveAt(0);
                        else if (setLeft > 0)
                            setLeft--;
                    }
                }
                return (BoardAssessment.TurnsToWin(attackers, remaining, oppLp), setLeft);
            }

            // Try every combination (only a handful of plays) and keep the cheapest lethal one that can actually be
            // made: dependencies present, and enough cards in hand (e.g. Normal Summoning the last card in hand
            // leaves nothing for Plaguespreader to return to the deck).
            if (plays.Count > 12)
                plays = plays.OrderBy(p => p.Cost).Take(12).ToList();
            List<Extension>? plan = null;
            double planCost = double.MaxValue;
            for (int mask = 1; mask < 1 << plays.Count; mask++)
            {
                var set = plays.Where((_, i) => (mask & (1 << i)) != 0).ToList();
                double cost = set.Sum(p => p.Cost);
                if (cost >= planCost ||
                    set.Sum(p => p.HandCost) > handCount ||
                    set.Sum(p => p.DarkCost) > graveDarks ||
                    set.Any(p => p.Requires != null && !set.Contains(p.Requires)))
                    continue;
                var sim = Simulate(set);
                cost += SetCardLeftCost * sim.SetLeft;
                if (sim.Turns != 1 || cost >= planCost)
                    continue;
                plan = set;
                planCost = cost;
            }
            if (plan == null)
                return null;

            // DAD uses left over (DARKs to spare) go on their set cards before the attacks: a trap destroyed first can't stop the
            // lethal (round-robin 7: a third DAD use would have taken the Torrential instead of spending Lyla's effect on it).
            int setCardsLeft = Simulate(plan).SetLeft;
            if (setCardsLeft > 0 && (dadOnField || dad != null && plan.Contains(dad)))
            {
                int darksLeft = graveDarks - plan.Sum(p => p.DarkCost);
                var spare = plays.Where(p => p.Name == "DAD's effect" && !plan.Contains(p)).Take(Math.Min(darksLeft, setCardsLeft)).ToList();
                plan.AddRange(spare);
                setCardsLeft -= spare.Count;
            }
            bool backrowCleared = setCards > 0 && setCardsLeft == 0;

            plan = plan.OrderBy(p => p.Order).ToList();
            planText = string.Join(" + ", plan.Select(p => p.Name));
            if (dryRun)
            {
                _lastComboPlan = planText;
                _lastComboPlanCards.Clear();
                _lastComboPlanCards.AddRange(plan.Select(p => p.CardId));
                return null;
            }
            // Lethal on paper, but pushing several monsters into set cards can be stopped (Torrential, Mirror Force...),
            // leaving us overextended. Roll for it, unless we've committed to it (backrow cleared) or holding back loses.
            if (plan.Any(p => p.AddedAtk.Count > 0) && !plan.Any(p => CommittedToBigPlay(header, p.CardId)) && !backrowCleared)
            {
                var (through, throughWhy) = await PushGetsThroughAsync();
                var (goChance, aheadNote) = await LethalPushChanceAsync(through, BoardAssessment.TurnsToWin(baseAttackers, defenders, oppLp));
                if (through < 1 && !await WeLoseNextTurnAsync() && !await PushRollAsync(header, goChance))
                {
                    Note(context, $"hold combined lethal [{planText}]: {throughWhy}{aheadNote}; rolled to hold rather than overextend into it");
                    foreach (var p in plan)
                        Hold(p.CardId, null, BlackwingRules.ComboLethal);
                    return null;
                }
                if (through < 1)
                    planText += $"; {throughWhy}{aheadNote}; rolled to go for it";
            }
            // The whole sequence is handed over at once and followed (DeckBotBrain.TurnPlan, user 2026-11-12): the first play now,
            // the rest and the Battle Phase as the turn plan, so no other rule acts in between (round-robin 7: the Lyla rule
            // used her effect between the plan's steps and broke the lethal). Each later play's own bookkeeping (Vayu's planned
            // synchro, the Normal Summon used) runs as the plan takes it. The attacks: an all-out push, committed.
            for (int i = 0; i < plan.Count; i++)
            {
                var response = plan[i].Execute();
                if (!response.HasValue)
                    continue;
                var rest = plan.Skip(i + 1)
                    .Select(p => new RoutineStep(p.Act, p.CardId, p.Name, () => p.Execute()))
                    .Append(RoutineStep.Battle)
                    .ToList();
                if (!await ContinueRoutineAsync(context, "combined lethal", planText, new RoutineStep(plan[i].Act, plan[i].CardId, plan[i].Name), rest, lethal: true))
                    return null;
                CommitLethalPush(header.CurrentTurn, $"combined lethal [{planText}]");
                return response;
            }
            return null;
        }

        // ------------------------------------------------------------------ helpers

        // The optional rule is rolled only once the action is actually available (see DeckBotBrain.Rule).
        private bool TryActivate(DecisionContext<IdleCmdData> context, IdleCmdData state, int cardId, string reason, out IdleCmdResponse response, string? rule = null)
        {
            response = default;
            if (!TryGetActivable(state, cardId, out var card))
                return false;
            if (rule != null && !Rule(rule))
                return false;

            // Guard against looping on an effect the engine keeps offering.
            if (_activationTurn != context.Header.CurrentTurn)
            {
                _activationTurn = context.Header.CurrentTurn;
                _activationsThisTurn.Clear();
            }
            _activationsThisTurn.TryGetValue(cardId, out int used);
            if (used >= 3)
                return false;
            _activationsThisTurn[cardId] = used + 1;

            Note(context, $"activate {CardNames.Get(cardId)}: {reason}");
            _lastActivatedCard = cardId;
            response = card.MainActivateFromIndex();
            return true;
        }

        private bool TrySpecialSummon(DecisionContext<IdleCmdData> context, IdleCmdData state, int cardId, string reason, out IdleCmdResponse response, string? rule = null)
        {
            response = default;
            if (!TryGetSpecialSummon(state, cardId, out var card))
                return false;
            if (rule != null && !Rule(rule))
                return false;
            Note(context, $"special summon {CardNames.Get(cardId)}: {reason}");
            response = card.SpecialSummon();
            return true;
        }

        private bool TryChain(DecisionContext<SelectChainData> context, DynamicCard[] options, int cardId, string reason, out int index, string? rule = null)
        {
            index = -1;
            for (int i = 0; i < options.Length; i++)
            {
                if (options[i].IsCode(cardId))
                {
                    if (rule != null && !Rule(rule))
                        return false;
                    Note(context, $"chain {CardNames.Get(cardId)}: {reason}");
                    index = i;
                    return true;
                }
            }
            return false;
        }

        private IList<byte> Ordered(
            DecisionContext<SelectCardData> context,
            DHint hint,
            List<(DynamicCard Card, int Index)> indexed,
            int count,
            int[] priority,
            bool preferLowValue)
        {
            var picks = indexed
                .OrderBy(x => PriorityIndex(priority, x.Card))
                .ThenBy(x => preferLowValue ? x.Card.Attack : -x.Card.Attack)
                .Take(count)
                .ToList();
            Note(context, $"{hint}: {string.Join(", ", picks.Select(x => DescribePick(x.Card)))}");
            return picks.Select(x => (byte)x.Index).ToList();
        }

        private static int PriorityIndex(int[] priority, DynamicCard card)
        {
            int index = Array.FindIndex(priority, id => card.IsCode(id));
            return index < 0 ? priority.Length : index;
        }


        /// <summary>
        /// Special Summoning Dark Grepher from hand costs a level 5+ DARK, so it's rarely right. Only do it when:
        ///  1. a spare Sirocco/Elphin can pay the cost,
        ///  2. another good DARK discard remains for Grepher's effect afterwards,
        ///  3. the Normal Summon is already used, or another monster wants it
        ///     (otherwise just Normal Summon Grepher for free).
        /// </summary>
        /// <summary>
        /// Special Summoning Dark Grepher throws away a level 5+ DARK (Sirocco, or worse) for a 1700 body: almost always
        /// a terrible trade. Only when it wins the game this turn, counting what it sets up: Grepher's own 1700, plus
        /// the Vayu plays its DARKs complete. The summon cost (Sirocco / Elphin: a partner), the effect's discard from
        /// hand and its send from deck all land in the graveyard, so it can add a Vayu pair (Grepher 1700 + Armed Wing
        /// 2300 = 4000) or two Vayus next to partners already there. Not if we're lethal without it. Returns the reason,
        /// or null (logged).
        /// </summary>
        private async Task<string?> GrepherSummonWinsAsync(DecisionContext<IdleCmdData> context, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters)
        {
            const string hold = "don't Special Summon Dark Grepher: discarding a level 5+ DARK for it is only worth it when it wins this turn";
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               context.StateData.CanBattlePhase;
            if (!battleAhead)
            {
                Note(context, hold);
                return null;
            }

            var board = botMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(EffectiveAttack).ToList();
            var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var grave = await GetBotGraveyardCardsAsync();
            var hand = await GetBotHandCardsAsync();
            int graveVayus = grave.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
            var gravePartnerLevels = grave.Where(card => card.IsCode(VayuPartners)).Select(card => (int)card.Level).ToList();

            // Without Grepher: the board plus the Vayu plays we already have.
            var without = board.Concat(VayuPlayAtks(graveVayus, gravePartnerLevels)).ToList();
            if (BoardAssessment.TurnsToWin(without, defenders, oppLp) == 1)
            {
                Note(context, $"{hold} (we're lethal without it)");
                return null;
            }

            // With Grepher: the cost card, then the best discard from hand and the best send from deck.
            var cost = hand.Where(card => card.IsCode(CardId.BlackwingSiroccoTheDawn, CardId.BlackwingElphinTheRaven))
                .OrderBy(card => card.Level).FirstOrDefault();
            if (cost.ID <= 0)
                return null;
            var handLeft = hand.Where(card => !SameCard(card, cost)).ToList();
            var discards = handLeft.Where(card => CardRules.IsGoodDarkDiscard(card.ID)).Select(card => (Id: card.ID, Level: (int)card.Level)).Distinct().ToList();
            var deckDarks = await DeckDarksForGrepherAsync();

            int bestTotal = 0;
            string bestWhat = "";
            foreach (var discard in discards)
            {
                foreach (var send in deckDarks)
                {
                    int vayus = graveVayus;
                    var partners = new List<int>(gravePartnerLevels) { (int)cost.Level };
                    foreach (var (id, level) in new[] { discard, send })
                    {
                        if (id == CardId.BlackwingVayuTheEmblemOfHonor) vayus++;
                        else if (VayuPartners.Contains(id)) partners.Add(level);
                    }
                    var plays = VayuPlayAtks(vayus, partners);
                    int total = plays.Sum();
                    if (total > bestTotal)
                    {
                        bestTotal = total;
                        bestWhat = $"discard {CardNames.Get(discard.Id)}, send {CardNames.Get(send.Id)}: {plays.Count} Vayu play(s) ({string.Join(" + ", plays)} ATK)";
                    }
                    var with = board.Append(1700).Concat(plays).ToList();
                    if (BoardAssessment.TurnsToWin(with, defenders, oppLp) == 1)
                        return $"lethal this turn: Dark Grepher (1700) and its effect ({CardNames.Get(discard.Id)} discarded, " +
                               $"{CardNames.Get(send.Id)} sent) complete {plays.Count} Vayu play(s) ({string.Join(" + ", plays)} ATK)";
                }
            }
            if (discards.Count == 0 && BoardAssessment.TurnsToWin(board.Append(1700).Concat(VayuPlayAtks(graveVayus, gravePartnerLevels.Append((int)cost.Level).ToList())).ToList(), defenders, oppLp) == 1)
                return "lethal this turn with Dark Grepher's attack (and the Vayu play its cost completes)";

            Note(context, $"{hold}{(bestWhat != "" ? $" (best it sets up: {bestWhat}; not lethal)" : "")}");
            return null;
        }

        /// <summary>
        /// Chaos Sorcerer is removal (its banish), and its cost banishes a LIGHT and a DARK (often a Vayu half), so it's
        /// summoned only when needed: a face-up big threat for it to banish, lethal this turn, or holding back loses.
        /// Never just to start a clock (e.g. after MST cleared the way for a "big play"). Returns the reason, or null
        /// (noted once per turn).
        /// </summary>
        private async Task<string?> ChaosSorcererNeededAsync(DecisionContext<IdleCmdData> context)
        {
            var botMonsters = await GetBotMonsterCardsAsync();
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var board = botMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(EffectiveAttack).ToList();
            var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               context.StateData.CanBattlePhase;
            if (battleAhead)
            {
                // Banish the biggest defender (if face-up), then attack with the rest (Chaos can't attack after banishing).
                var biggest = enemyMonsters.Where(card => card.IsFaceup()).OrderByDescending(card => card.GetRealPower()).FirstOrDefault();
                var cleared = biggest.ID > 0
                    ? BoardAssessment.Defenders(enemyMonsters.Where(card => card.CurrentCoordinates.Sequence != biggest.CurrentCoordinates.Sequence).ToList(), OpponentMemory)
                    : defenders;
                if (BoardAssessment.TurnsToWin(board.Append(2300).ToList(), defenders, oppLp) == 1 ||
                    BoardAssessment.TurnsToWin(board, cleared, oppLp) == 1)
                    return "lethal this turn";
            }
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup()))
                if (await IsBigThreatAsync(enemy))
                    return $"banish their {CardNames.Get(enemy.ID)}";
            if (await WeLoseNextTurnAsync())
                return "holding back loses";
            if (_chaosHoldNotedTurn != context.Header.CurrentTurn)
            {
                _chaosHoldNotedTurn = context.Header.CurrentTurn;
                Note(context, "hold Chaos Sorcerer: no face-up big threat to banish, not lethal, and holding doesn't lose (it's removal, not a clock starter)");
                Hold(CardId.ChaosSorcerer, null, BlackwingRules.ChaosSummon);
            }
            return null;
        }

        private int _chaosHoldNotedTurn = -1;

        // The turn Plaguespreader left the graveyard to bring the DARK count to 3 for Dark Armed Dragon.

        /// <summary>Mirror Force chance from which DAD spends a graveyard piece to clear a set card before attacking. Tune from logs.</summary>

        /// <summary>
        /// Chaos Sorcerer already on the field: its banish costs nothing but its own attack this turn, so use it whenever
        /// there's a face-up target. Banishing also beats running over a monster that rewards destruction (Card
        /// Trooper's draw, a floater). The one exception: its attack is needed for lethal and banishing doesn't win.
        /// Returns the reason, or null.
        /// </summary>
        private async Task<string?> ChaosEffectWhyAsync<T>(DecisionContext<T> context, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters)
        {
            var targets = enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0).ToList();
            if (targets.Count == 0)
                return null;
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               (context.StateData is not IdleCmdData idle || idle.CanBattlePhase);
            if (battleAhead)
            {
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                var board = botMonsters.Where(card => card.IsFaceup() && card.IsAttack()).ToList();
                var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
                bool lethalWithAttack = BoardAssessment.TurnsToWin(board.Select(EffectiveAttack).ToList(), defenders, oppLp) == 1;
                var withoutChaos = board.Where(card => !card.IsCode(CardId.ChaosSorcerer)).Select(EffectiveAttack).ToList();
                bool lethalWithBanish = targets.Any(target => BoardAssessment.TurnsToWin(withoutChaos,
                    BoardAssessment.Defenders(enemyMonsters.Where(card => card.CurrentCoordinates.Sequence != target.CurrentCoordinates.Sequence).ToList(), OpponentMemory),
                    oppLp) == 1);
                if (lethalWithAttack && !lethalWithBanish)
                {
                    Note(context, "hold Chaos Sorcerer's effect: its attack is needed for lethal this turn");
                    return null;
                }
            }
            var rewarders = targets.Where(RewardsDestruction).ToList();
            return "Chaos Sorcerer is already on the field: banishing costs only its attack this turn" +
                   (rewarders.Count > 0 ? $", and banishing denies {string.Join(", ", rewarders.Select(card => CardNames.Get(card.ID)))} its destruction reward" : "");
        }

        /// <summary>
        /// Whether making Silverwind the Ascendant (the end of the Vayu chain) is needed now: it's lethal this turn, a big
        /// threat sits at 2500-2800 (only 2800 answers it; Armor Master's 2500 doesn't), or holding back loses. Returns
        /// the reason, or null.
        /// </summary>
        private async Task<string?> AscendantNeededAsync(DecisionContext<IdleCmdData> context)
        {
            var botMonsters = await GetBotMonsterCardsAsync();
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var board = botMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(EffectiveAttack).ToList();
            var defenders = BoardAssessment.Defenders(enemyMonsters, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               context.StateData.CanBattlePhase;
            if (battleAhead && BoardAssessment.TurnsToWin(board.Append(2800).ToList(), defenders, oppLp) == 1)
                return "lethal this turn";
            var big = await BigThreatsAsync(BoardAssessment.Threats(board, defenders));
            var onlyAscendant = big.FirstOrDefault(t => t.Power >= 2500 && (t.Power < 2800 || t.Power == 2800 && t.AttackPosition));
            if (onlyAscendant.Id > 0)
                return $"only 2800 answers {CardNames.Get(onlyAscendant.Id)} ({onlyAscendant.Power})";
            if (await WeLoseNextTurnAsync())
                return "holding back loses";
            return null;
        }

        /// <summary>
        /// The Vayu play Miracle Dig would make possible: simulates its pick (every banished Vayu first, one partner if
        /// the graveyard has none, then partners) on top of the graveyard, and returns the best synchro's ATK, the spare
        /// Vayus afterwards and a description; or null when it makes no play.
        /// </summary>
        private async Task<(int Atk, int Spares, string What)?> MiracleDigVayuPlayAsync()
        {
            var (_, plays, vayusBack, vayus) = await RecursionVayuPlaysAsync();
            if (plays.Count == 0)
                return null;
            return (plays.Max(), Math.Max(0, vayus - 1), $"{vayusBack} Vayu(s) back, {plays.Count} play(s): {string.Join(" + ", plays)} ATK");
        }

        /// <summary>
        /// Vayu plays (synchro ATKs) available now, and after Miracle Dig / Burial returns 3 banished monsters (its pick:
        /// every Vayu first, one partner if the graveyard has none, then partners), with the Vayus returned and the Vayu
        /// total afterwards.
        /// </summary>
        private async Task<(List<int> Now, List<int> After, int VayusBack, int VayusAfter)> RecursionVayuPlaysAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            var banished = (await CurrentField.GetCardsInLocation(CardLocation.Removed)).Where(card => card.ID > 0).ToList();
            int vayus = grave.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
            var partners = grave.Where(card => card.IsCode(VayuPartners)).Select(card => (int)card.Level).ToList();
            var banishedVayus = banished.Count(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor));
            var banishedPartners = banished.Where(card => card.IsCode(VayuPartners)).Select(card => (int)card.Level)
                .OrderByDescending(level => level).ToList();
            var now = VayuPlayAtks(vayus, partners);

            int slots = 3;
            bool needPartner = partners.Count == 0 && banishedPartners.Count > 0;
            int vayusBack = Math.Min(banishedVayus, needPartner ? Math.Max(Math.Min(1, slots), slots - 1) : slots);
            vayus += vayusBack;
            slots -= vayusBack;
            foreach (int level in banishedPartners)
            {
                if (slots == 0 || partners.Count >= vayus && !needPartner)
                    break;
                partners.Add(level);
                slots--;
                needPartner = false;
            }
            return (now, VayuPlayAtks(vayus, partners), vayusBack, vayus);
        }

        /// <summary>The ATK of each Vayu play available from these graveyard counts: Vayus paired with the highest-Level partners first.</summary>
        private List<int> VayuPlayAtks(int vayus, List<int> partnerLevels) =>
            partnerLevels.OrderByDescending(level => level).Take(vayus)
                .Select(level => VayuSynchroAtk(level + 1)).Where(atk => atk > 0).ToList();

        private static bool SameCard(DynamicCard a, DynamicCard b) =>
            a.ID == b.ID && a.CurrentCoordinates.Location == b.CurrentCoordinates.Location && a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;

        /// <summary>
        /// DARKs still in the deck that Grepher's effect could send (Vayu and its partners). Uses our deck's card ids when
        /// the server shows them; otherwise assumes 3 copies of each, minus those seen in the hand, graveyard, banished
        /// pile and field.
        /// </summary>
        private async Task<List<(int Id, int Level)>> DeckDarksForGrepherAsync()
        {
            var wanted = new[] { (CardId.BlackwingVayuTheEmblemOfHonor, 1), (CardId.BlackwingSiroccoTheDawn, 5), (CardId.BlackwingElphinTheRaven, 6) };
            var deckIds = ((await CurrentField.GetBotField()).OwnSide.Main ?? Array.Empty<int>()).Where(id => id > 0).ToList();
            if (deckIds.Count > 0)
                return wanted.Where(w => deckIds.Contains(w.Item1)).Select(w => (w.Item1, w.Item2)).ToList();

            var seen = (await GetBotHandCardsAsync())
                .Concat(await GetBotGraveyardCardsAsync())
                .Concat(await CurrentField.GetCardsInLocation(CardLocation.Removed))
                .Concat(await GetBotMonsterCardsAsync())
                .Select(card => card.ID).ToList();
            return wanted.Where(w => w.Item1 != CardId.BlackwingElphinTheRaven && seen.Count(id => id == w.Item1) < 3)
                .Select(w => (w.Item1, w.Item2)).ToList();
        }

        private static bool ShouldSpecialSummonGrepher(IdleCmdData state, List<DynamicCard> hand, List<DynamicCard> botMonsters, out string reason)
        {
            reason = "";
            var costCards = hand.Where(card => card.IsCode(CardId.BlackwingSiroccoTheDawn, CardId.BlackwingElphinTheRaven)).ToList();
            if (costCards.Count == 0)
                return false;

            // The cost uses one of them; something must remain for the effect.
            int goodDiscards = hand.Count(card => CardRules.IsGoodDarkDiscard(card.ID));
            if (goodDiscards < 2)
                return false;

            var summonable = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0).ToList();
            if (summonable.Count == 0)
            {
                reason = "normal summon already used; discard spare level 5+ DARK, another DARK left for the effect";
                return true;
            }

            // If only one card can pay the cost, it's gone after the summon, so it can't be what the Normal Summon is saved for.
            int? consumedByCost = costCards.Count == 1 ? costCards[0].ID : null;
            var otherNormalSummon = summonable.FirstOrDefault(card =>
                !card.IsCode(CardId.DarkGrepher) &&
                card.ID != consumedByCost &&
                !card.HasType(CardType.Flip) &&
                card.IsCode(SummonPriority) &&
                (card.Level <= 4 || (card.Level == 5 && botMonsters.Count == 0)));
            if (otherNormalSummon.ID > 0)
            {
                reason = $"keep the normal summon for {CardNames.Get(otherNormalSummon.ID)}; discard spare level 5+ DARK, another DARK left for the effect";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Main-phase and battle prompts only happen on the bot's own turn, so remember that turn's parity.
        /// Falls back to CurrentPlayer == 0, which matched the bot's turns in the first logged duel.
        /// </summary>
        private void RememberOwnTurn(BotStateHeader header) => _ownTurnParity = header.CurrentTurn % 2;

        private bool IsBotTurn(BotStateHeader header) =>
            _ownTurnParity.HasValue ? header.CurrentTurn % 2 == _ownTurnParity.Value : header.CurrentPlayer == 0;

        public override async Task<BattleCmdResponse> OnBattleCmdAsync(DecisionContext<BattleCmdData> context, CancellationToken ct = default)
        {
            RememberOwnTurn(context.Header);
            await PrepareContextAsync(context, ct);
            _battleHand = await GetBotHandCardsAsync();
            // Ending the turn from the Battle Phase skips Main Phase 2's Normal Summon: held with Black Whirlwind in hand or face-up (user, 2026-11-24).
            if (!NormalSummonUsed(context.Header) &&
                (_battleHand.Any(card => card.IsCode(CardId.BlackWhirlwind)) ||
                 (await CurrentField.GetCardsInSpellZone()).Any(card => card.IsFaceup() && card.IsCode(CardId.BlackWhirlwind))) &&
                Rule(BlackwingRules.NoSummonWithWhirlwindInHand))
                HoldPhaseMove("end", BlackwingRules.NoSummonWithWhirlwindInHand);
            return await base.OnBattleCmdAsync(context, ct);
        }

        // Our hand during this battle decision (for the Grand Mole rules below).
        private List<DynamicCard> _battleHand = new();

        // Kalut in hand: +1400 to a Blackwing in the damage step, so attacks are planned with it (user, 2026-11-10: "the brain
        // may want to make attacks accounting for kalut").
        protected override int HandBattleBoost(DynamicCard attacker) =>
            IsBlackwing(attacker) && _battleHand.Any(card => card.IsCode(CardId.BlackwingKalutTheMoonShadow)) ? 1400 : 0;

        // Bouncing Dark Grepher is a plus if we have a DARK to discard: re-summon it and use its effect again.
        protected override bool GainsFromBounce(DynamicCard attacker) =>
            base.GainsFromBounce(attacker) ||
            attacker.IsCode(CardId.DarkGrepher) && _battleHand.Any(card => CardRules.IsGoodDarkDiscard(card.ID));

        // Dark Armed Dragon (field or summonable from hand) and Chaos Sorcerer remove Grand Mole outright.
        protected override async Task<string?> BetterAnswerToBouncerAsync()
        {
            if (await base.BetterAnswerToBouncerAsync() is string shared)
                return shared;
            var field = await GetBotMonsterCardsAsync();
            if (field.Any(card => card.IsFaceup() && card.IsCode(CardId.DarkArmedDragon, CardId.ChaosSorcerer)))
                return "our Dark Armed Dragon / Chaos Sorcerer on the field";
            var grave = await GetBotGraveyardCardsAsync();
            int darks = grave.Count(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0);
            if (_battleHand.Any(card => card.IsCode(CardId.DarkArmedDragon)) && darks == 3)
                return "Dark Armed Dragon, summonable from our hand next main phase";
            bool light = grave.Any(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Light) != 0);
            if (_battleHand.Any(card => card.IsCode(CardId.ChaosSorcerer)) && darks > 0 && light)
                return "Chaos Sorcerer, summonable from our hand next main phase";
            return null;
        }

        private static void Note<T>(DecisionContext<T> context, string message) => context.Bot.LogNote($"blackwing: {message}");

        // ================================================================== the Blackwing plays
        // (Whirlwind, free bodies, Gale, Sirocco's boost, Icarus Attack, Kalut, the aggro Normal Summon; the Vayu engine above
        // came from the Vayu brain)

        private static readonly int[] OurBlackwings =
        {
            CardId.BlackwingSiroccoTheDawn, CardId.BlackwingShuraTheBlueFlame, CardId.BlackwingBoraTheSpear,
            CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingGaleTheWhirlwind, CardId.BlackwingBlizzardTheFarNorth,
            CardId.BlackwingVayuTheEmblemOfHonor, CardId.BlackwingElphinTheRaven,
            CardId.BlackwingArmedWing, CardId.BlackwingArmorMaster, CardId.BlackwingSilverwindTheAscendant
        };

        private static bool IsBlackwing(DynamicCard card) => card.IsCode(OurBlackwings);

        /// <summary>Aggro tuning (set in the constructor): push more, hold less.</summary>
        private void ApplyAggroTuning()
        {
            // Only hold resources when they're already on a quick clock; their set cards deter us less.
            HoldWhenClockAtMost = 2;
            PushSetTrapFactor = 0.85;
            PushOpenBoost = 2;
            // Chip damage matters for an aggro deck: weak attackers still hit, and direct hits go in with backrow up.
            Rules.Set(DeckRules.WeakDontAttackDirectly, 0);
            Rules.Set(DeckRules.NoChipIntoBackrow, 0);
        }

        // ------------------------------------------------------------------ main phase

        /// <summary>
        /// The Blackwing plays, before the shared ones: Allure of Darkness, Black Whirlwind face-up before any Normal
        /// Summon, free bodies (Bora / Gale next to a Blackwing), Gale halving the monster in our way, Sirocco's ATK
        /// boost for lethal or to beat their best monster, Icarus Attack on our turn.
        /// </summary>
        /// <summary>
        /// Sirocco's boost has nothing to gain: it's the only Blackwing face-up (user, 2026-11-17: "never use sirocco's effect if it's the only
        /// blackwing on the field. no point to do that ever"), or their field has no monster (2026-11-16: "using sirocco's effect is pointless
        /// if I don't have a monster on the field": split attacks deal the same).
        /// </summary>
        /// <summary>
        /// Sirocco's boost gains nothing: fewer than two face-up Blackwings, or their field empty without the boost making one lethal attack
        /// (user, 2026-11-25: "if sirocco's boost meant you could deal lethal in a single attack then it should use that because it removes gorz
        /// risk. but if that's not the case then it shouldn't be used if I have no monster"). The boosted Blackwing gets every other face-up
        /// Blackwing's ATK, and only it attacks: lethal when all their ATK together reaches their life points.
        /// </summary>
        private bool SiroccoBoostPointless(List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters, int theirLp)
        {
            var blackwings = botMonsters.Where(card => card.IsFaceup() && IsBlackwing(card)).ToList();
            bool singleLethal = blackwings.Sum(card => card.Attack) >= theirLp && theirLp > 0;
            return (blackwings.Count < 2 || enemyMonsters.Count == 0 && !singleLethal) && Rule(BlackwingRules.SiroccoBoostPointless);
        }

        /// <summary>The chain window after a summon offers Sirocco's boost too (seen 2026-11-18: chained with Sirocco the only monster): held there.</summary>
        protected override async Task RecordDeckChainHoldsAsync(DecisionContext<SelectChainData> context)
        {
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (!offered.Any(card => card.IsCode(CardId.BlackwingSiroccoTheDawn) && card.CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.MonsterZone }))
                return;
            if (SiroccoBoostPointless(await GetBotMonsterCardsAsync(), await GetEnemyMonsterCardsAsync(), await CurrentField.GetBotLifePoints(isOwnSide: false)))
                foreach (var card in offered.Where(card => card.IsCode(CardId.BlackwingSiroccoTheDawn)))
                    HoldKey(card.ID, "chain:", BlackwingRules.SiroccoBoostPointless);
        }

        /// <summary>Bora special summonable from the hand that adds no kill and no direct hit this turn (or no battle is ahead): held
        /// (BlackwingRules.BodyNeedsPurpose), so a line that summons it anyway pays for it.</summary>
        private void BoraPurposeHold(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            if (!TryGetSpecialSummon(state, CardId.BlackwingBoraTheSpear, out var body) || body.CurrentCoordinates.Location != (byte)CardLocation.Hand ||
                !Rule(BlackwingRules.BodyNeedsPurpose))
                return;
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            var up = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            var theirs = enemyMonsters.Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
            bool addsKill = Kills(up.Append(body).ToList(), theirs) > Kills(up, theirs);
            bool directHit = ClearsTheirBoard(up, theirs) && DirectHitMatters(body, up);
            if (!battleAhead || !addsKill && !directHit)
                Hold(CardId.BlackwingBoraTheSpear, MainAction.SpSummon, BlackwingRules.BodyNeedsPurpose);
        }

        // A Normal Summon that triggers Black Whirlwind: any Blackwing but Vayu (its summon doesn't trigger it: user, 2026-11-10).
        private static bool TriggersWhirlwind(DynamicCard card) => IsBlackwing(card) && !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor);

        /// <summary>
        /// Black Whirlwind's holds (user, 2026-11-24), the Normal Summon not made yet:
        /// <list type="number">
        /// <item>Whirlwind activated this turn, a Blackwing summon on offer: every other move is held (BlackwingRules.WhirlwindWaitsForSummon),
        /// so a line pays that cost once per move between Whirlwind and the summon ("whirlwind should be held until *right* before the summon";
        /// seen: Whirlwind, then Brain Control Solemned, then the summon).</item>
        /// <item>Whirlwind in hand (also with one face-up: both are played): a Blackwing's Normal Summon is held (BlackwingRules.SummonWithWhirlwindInHand).</item>
        /// <item>Whirlwind in hand or face-up: ending the turn is held (BlackwingRules.NoSummonWithWhirlwindInHand; the Battle Phase's end in OnBattleCmdAsync).</item>
        /// </list>
        /// </summary>
        private void WhirlwindHolds(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand, List<DynamicCard> botSpells)
        {
            if (NormalSummonUsed(context.Header))
                return;
            var none = Array.Empty<DynamicCard>();
            var summons = state.SummonableCards ?? none;
            bool faceUp = botSpells.Any(card => card.IsFaceup() && card.IsCode(CardId.BlackWhirlwind));
            bool inHand = hand.Any(card => card.IsCode(CardId.BlackWhirlwind));

            if (faceUp && WasCardActivatedThisTurn(CardId.BlackWhirlwind) && summons.Any(TriggersWhirlwind) &&
                Rule(BlackwingRules.WhirlwindWaitsForSummon))
            {
                const string rule = BlackwingRules.WhirlwindWaitsForSummon;
                foreach (var card in summons.Where(card => !TriggersWhirlwind(card)))
                    Hold(card.ID, MainAction.Summon, rule);
                foreach (var card in state.SpecialSummonableCards ?? none)
                    Hold(card.ID, MainAction.SpSummon, rule);
                foreach (var card in state.ReposableCards ?? none)
                    Hold(card.ID, MainAction.Repos, rule);
                foreach (var card in state.MonsterSetableCards ?? none)
                    Hold(card.ID, MainAction.SetMonster, rule);
                foreach (var card in state.SpellSetableCards ?? none)
                    Hold(card.ID, MainAction.SetSpell, rule);
                // A second Whirlwind isn't a gap: both are played right before the summon (user, 2026-11-24).
                foreach (var card in (state.ActivableCards ?? none).Where(card => !card.IsCode(CardId.BlackWhirlwind)))
                    Hold(card.ID, MainAction.Activate, rule);
                HoldPhaseMove("battle", rule);
                HoldPhaseMove("end", rule);
            }

            // Also with one face-up: two Whirlwinds search twice, so both are played (user, 2026-11-24).
            if (inHand && Rule(BlackwingRules.SummonWithWhirlwindInHand))
                foreach (var card in summons.Where(TriggersWhirlwind))
                    Hold(card.ID, MainAction.Summon, BlackwingRules.SummonWithWhirlwindInHand);

            if ((inHand || faceUp) && Rule(BlackwingRules.NoSummonWithWhirlwindInHand))
                HoldPhaseMove("end", BlackwingRules.NoSummonWithWhirlwindInHand);
        }

        private async Task<IdleCmdResponse?> TryBlackwingPlaysAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand,
            List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            int turn = context.Header.CurrentTurn;
            bool mainOne = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1;
            bool battleAhead = mainOne && state.CanBattlePhase && turn > 1;

            // Allure of Darkness is a shared staple (TryFreeCardAdvantageAsync: with a DARK to banish, or as the last-resort gamble).

            // Black Whirlwind face-up before the Normal Summon (each Blackwing Normal Summon then searches a smaller one).
            // Also with one face-up: two Whirlwinds search twice (user, 2026-11-24: "if you have two both should be played").
            if (!NormalSummonUsed(context.Header) && TryGetActivable(state, CardId.BlackWhirlwind, out var whirlwind) &&
                // Only when a Blackwing Normal Summon is actually coming (one still on offer after the summon filter): otherwise
                // Whirlwind waits (user, 2026-11-10).
                // Not Vayu: its summon doesn't trigger Whirlwind (user, 2026-11-10).
                (state.SummonableCards ?? Array.Empty<DynamicCard>()).Any(card => IsBlackwing(card) && card.Level <= 4 && !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) &&
                Rule(BlackwingRules.BlackWhirlwind))
            {
                Note(context, "activate Black Whirlwind before the Normal Summon: it turns the summon into a search");
                return whirlwind.MainActivateFromIndex();
            }

            // Otherwise Whirlwind waits in hand: face-up with no Blackwing Normal Summon coming this turn, it's only exposed to removal
            // (user, 2026-11-17: "no need to reactivate black whirlwind until you're about to normal summon - it's safer in hand"). Held,
            // so a line that activates it early pays for it.
            if (TryGetActivable(state, CardId.BlackWhirlwind, out var earlyWhirlwind) &&
                earlyWhirlwind.CurrentCoordinates.Location == (byte)CardLocation.Hand &&
                (NormalSummonUsed(context.Header) ||
                 !(state.SummonableCards ?? Array.Empty<DynamicCard>()).Any(card => IsBlackwing(card) && card.Level <= 4 && !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor))) &&
                Rule(BlackwingRules.WhirlwindWaitsForSummon))
            {
                Hold(earlyWhirlwind.ID, MainAction.Activate, BlackwingRules.WhirlwindWaitsForSummon);
                Hold(earlyWhirlwind.ID, MainAction.SetSpell, BlackwingRules.WhirlwindWaitsForSummon);
            }

            // Free bodies: Bora and Gale special summon themselves next to a Blackwing (aggro: more attackers, synchro fodder).
            // Gale only with a monster of theirs to shrink: it is mostly removal (user, 2026-11-12: "gale is really mostly removal -
            // it's mostly meant to be used when I have something you want to shrink"; seen summoned on turn 1 into an empty field).
            bool galeTarget = enemyMonsters.Any(card => card.IsFaceup() && card.Attack > 0);
            if (!galeTarget && TryGetSpecialSummon(state, CardId.BlackwingGaleTheWhirlwind, out _))
                Hold(CardId.BlackwingGaleTheWhirlwind, MainAction.SpSummon, BlackwingRules.GaleIsRemoval);
            foreach (int id in new[] { CardId.BlackwingBoraTheSpear, CardId.BlackwingGaleTheWhirlwind })
                if (TryGetSpecialSummon(state, id, out var body) && body.CurrentCoordinates.Location == (byte)CardLocation.Hand && Rule(BlackwingRules.FreeBody) &&
                    !(id == CardId.BlackwingGaleTheWhirlwind && !galeTarget && Rule(BlackwingRules.GaleIsRemoval)))
                {
                    // Round-robin 33: against their unknown set cards, a body the attack doesn't need stays in hand (Mirror Force
                    // took Shura and both Boras for one Gearframe).
                    if (id == CardId.BlackwingBoraTheSpear && await SpareBodyHoldAsync(context, id, body.Attack) is string spareHold)
                    {
                        Note(context, $"Bora waits: {spareHold}");
                        Hold(id, MainAction.SpSummon, DeckRules.SpareBodyWaits);
                        continue;
                    }
                    // A free body still needs a purpose this turn (user, 2026-11-13: "why summon the bora? what was the
                    // reasoning?"): its attack adds a kill our attackers up don't make, or a direct hit worth making once they
                    // clear the way. Otherwise it stays in hand (it's just as free later) rather than sit in front of their board.
                    if (id == CardId.BlackwingBoraTheSpear && Rule(BlackwingRules.BodyNeedsPurpose))
                    {
                        var up = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
                        var theirs = enemyMonsters.Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
                        bool addsKill = Kills(up.Append(body).ToList(), theirs) > Kills(up, theirs);
                        bool directHit = ClearsTheirBoard(up, theirs) && DirectHitMatters(body, up);
                        if (!battleAhead || !addsKill && !directHit)
                        {
                            Note(context, "Bora waits in hand: summoned now it adds no kill and no direct hit this turn (it's still free later)");
                            Hold(id, MainAction.SpSummon, BlackwingRules.BodyNeedsPurpose);
                            continue;
                        }
                    }
                    Note(context, $"special summon {CardNames.Get(id)}: a free body next to our Blackwing");
                    return body.SpecialSummon();
                }

            // Sirocco from hand when they have a monster and we have none: a free 2000 attacker.
            if (TryGetSpecialSummon(state, CardId.BlackwingSiroccoTheDawn, out var sirocco) &&
                sirocco.CurrentCoordinates.Location == (byte)CardLocation.Hand && Rule(BlackwingRules.SiroccoFree))
            {
                Note(context, "special summon Sirocco: a free 2000 attacker (they have a monster, we have none)");
                return sirocco.SpecialSummon();
            }

            // Gale: halve the monster in our way before attacking.
            if (battleAhead && TryGetActivable(state, CardId.BlackwingGaleTheWhirlwind, out var gale) &&
                gale.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
            {
                int ourBest = botMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(EffectiveAttack).DefaultIfEmpty(0).Max();
                var wall = enemyMonsters.Where(card => card.IsFaceup() && card.GetRealPower() >= ourBest && card.GetRealPower() / 2 < ourBest)
                    .OrderByDescending(card => card.GetRealPower()).FirstOrDefault();
                if (wall.ID > 0 && Rule(BlackwingRules.GaleHalve))
                {
                    PlanTarget(turn, wall, theirs: true);
                    Note(context, $"Gale halves {CardNames.Get(wall.ID)} ({wall.GetRealPower()}): our {ourBest} gets through");
                    // Halved for our best attacker to get through: that attack is the plan.
                    var hitter = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && EffectiveAttack(card) == ourBest).FirstOrDefault();
                    if (hitter.ID > 0)
                        CommitAttack(turn, hitter.ID, wall, $"Gale halved {CardNames.Get(wall.ID)} for {CardNames.Get(hitter.ID)} to get through");
                    return gale.MainActivateFromIndex();
                }
            }

            // Sirocco's effect with nothing to gain is held at the start of the decision (DecideMainPhaseAsync). Otherwise, into a suspected
            // Honest (user, 2026-11-17): the one boosted attacker battling a LIGHT monster of theirs (or a face-down one) hands Honest the boost.
            TryGetActivable(state, CardId.BlackwingSiroccoTheDawn, out var idleBoost);
            bool boostPointless = botMonsters.Count(card => card.IsFaceup() && IsBlackwing(card)) < 2 || enemyMonsters.Count == 0;
            if (!boostPointless && idleBoost.ID > 0 && idleBoost.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                     enemyMonsters.Any(card => !card.IsFaceup() || (card.Attribute & 0x10) != 0) &&
                     HonestSuspected(((await CurrentField.GetBotField()).EnemySide.Hand ?? Array.Empty<int>()).Length) && Rule(BlackwingRules.SiroccoBoostIntoHonest))
                Hold(idleBoost.ID, MainAction.Activate, BlackwingRules.SiroccoBoostIntoHonest);

            // Sirocco's effect: one Blackwing gets the total ATK of our face-up Blackwings (the others can't attack): for
            // lethal, or when no single attacker can beat their best monster.
            if (battleAhead && TryGetActivable(state, CardId.BlackwingSiroccoTheDawn, out var boost) &&
                boost.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
            {
                var wings = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && IsBlackwing(card)).ToList();
                int total = wings.Sum(card => card.Attack);
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                int split = wings.Sum(card => card.Attack);
                bool open = enemyMonsters.Count == 0;
                var biggest = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack()).OrderByDescending(card => card.Attack).FirstOrDefault();
                int ourBest = wings.Select(card => card.Attack).DefaultIfEmpty(0).Max();
                string? why = null;
                // (Their empty field's one-attack lethal is Sirocco's card rule: used right before the Battle Phase, CardRules SiroccoHelper.)
                if (wings.Count >= 2 && enemyMonsters.Count == 1 && biggest.ID > 0 && total - biggest.Attack >= oppLp)
                    why = $"one attacker at {total} through {CardNames.Get(biggest.ID)} is lethal";
                else if (wings.Count >= 2 && biggest.ID > 0 && biggest.Attack >= ourBest && total > biggest.Attack && enemyMonsters.Count == 1)
                    why = $"no single Blackwing beats {CardNames.Get(biggest.ID)} ({biggest.Attack}); together they do ({total})";
                // Bora into a face-down: with the boost it pierces for a big hit even if the face-down is Ryko, where on its own
                // it pierces for a few hundred and the others have nothing better to hit (user, 2026-11-10).
                var bora = wings.FirstOrDefault(card => card.IsCode(CardId.BlackwingBoraTheSpear));
                bool pierce = false;
                if (why == null && wings.Count >= 2 && bora.ID > 0 && enemyMonsters.Any(card => !card.IsFaceup()) &&
                    !enemyMonsters.Any(card => card.IsFaceup() && card.GetRealPower() >= total))
                {
                    why = $"Bora at {total} pierces their face-down monster: a big hit even if it's Ryko";
                    pierce = true;
                }
                // Planned with the Normal Summon still to come (user, 2026-11-13: "either summon the vayu before using sirocco's
                // effect, or set the vayu"; "I want it to plan ahead"): after the boost only the boosted Blackwing can attack, so
                // a Blackwing we'd Normal Summon goes first, and its ATK joins the boost total. One routine: the summon, then the
                // boost (checked in-engine like any routine). Not Kalut (worth more in hand) or Blizzard (its own rule).
                var summonFirst = NormalSummonUsed(context.Header) ? default
                    : (state.SummonableCards ?? Array.Empty<DynamicCard>())
                        .Where(card => IsBlackwing(card) && card.Level <= 4 &&
                                       !card.IsCode(CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingBlizzardTheFarNorth))
                        .OrderByDescending(card => card.Attack).FirstOrDefault();
                if (why != null && summonFirst.ID > 0 && Rule(BlackwingRules.SiroccoBoost) && Rule(BlackwingRules.SummonBeforeSiroccoBoost))
                {
                    string name = CardNames.Get(summonFirst.ID);
                    var first = new RoutineStep(MainAction.Summon, summonFirst.ID, $"normal summon {name} (its {summonFirst.Attack} joins the boost)");
                    var rest = new[] { new RoutineStep(MainAction.Activate, CardId.BlackwingSiroccoTheDawn, "Sirocco's boost") };
                    if (await ContinueRoutineAsync(context, "Sirocco boost", $"{why}; Normal Summon first: after the boost {name} couldn't attack, " +
                                                                              $"and its ATK adds to the total ({total} -> {total + summonFirst.Attack})", first, rest))
                    {
                        Note(context, $"normal summon {name} before Sirocco's boost: its ATK joins the boost, and after it {name} couldn't attack");
                        return summonFirst.NormalSummon();
                    }
                }
                if (why != null && Rule(BlackwingRules.SiroccoBoost))
                {
                    PlanTarget(turn, SiroccoTarget(wings, enemyMonsters) is { ID: > 0 } hard ? hard
                        : pierce ? bora : wings.OrderByDescending(card => card.Attack).First(), theirs: false);
                    Note(context, $"Sirocco's effect: {why}");
                    return boost.MainActivateFromIndex();
                }
            }

            // Icarus Attack on our turn: clear two cards before attacking when that pushes damage through, tributing our
            // least useful Blackwing (2-for-2).
            if (battleAhead && TryGetActivable(state, CardId.IcarusAttack, out var icarus) &&
                enemyMonsters.Count + enemySpells.Count >= 2 && IcarusTribute(botMonsters) is { ID: > 0 } fodder &&
                botMonsters.Count(card => card.IsFaceup() && card.IsAttack()) >= 2 && Rule(BlackwingRules.IcarusPush))
            {
                PlanTributes(turn, new[] { fodder }, "Icarus Attack");
                Note(context, $"activate Icarus Attack: tribute {CardNames.Get(fodder.ID)}, destroy two of their cards and attack through (2-for-2)");
                return icarus.MainActivateFromIndex();
            }
            return null;
        }

        /// <summary>
        /// The aggro Normal Summon: the biggest Blackwing with Black Whirlwind up (it searches a smaller one); Blizzard when a
        /// Level 4 or lower Blackwing is in the graveyard to revive; otherwise Shura > Bora > Kalut > Blizzard > Gale.
        /// </summary>
        private async Task<IdleCmdResponse?> TryBlackwingNormalSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (NormalSummonUsed(context.Header))
                return null;
            // Sirocco too when the server offers it (its tribute-free Normal Summon against their monsters): with Black Whirlwind
            // up it searches Kalut / Gale / Bora (seen 2026-11-11: Vayu was summoned instead, triggering nothing, into Kristya).
            var options = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => IsBlackwing(card) && (card.Level <= 4 || card.IsCode(CardId.BlackwingSiroccoTheDawn) && !OwnMonsters.Any(m => m.ID > 0))).ToList();
            if (options.Count == 0)
                return null;
            bool whirlwind = (await CurrentField.GetCardsInSpellZone()).Any(card => card.IsFaceup() && card.IsCode(CardId.BlackWhirlwind));
            bool reviveTarget = (await GetBotGraveyardCardsAsync()).Any(card => IsBlackwing(card) && card.Level <= 4 && card.HasType(CardType.Monster) &&
                                                                                 !card.HasType(CardType.Synchro) && !card.HasType(CardType.Tuner));
            int[] order = { CardId.BlackwingSiroccoTheDawn, CardId.BlackwingShuraTheBlueFlame, CardId.BlackwingBoraTheSpear, CardId.BlackwingKalutTheMoonShadow,
                CardId.BlackwingBlizzardTheFarNorth, CardId.BlackwingGaleTheWhirlwind, CardId.BlackwingVayuTheEmblemOfHonor };
            DynamicCard pick;
            string why;
            if (reviveTarget && options.FirstOrDefault(card => card.IsCode(CardId.BlackwingBlizzardTheFarNorth)) is { ID: > 0 } blizzard)
                (pick, why) = (blizzard, "Blizzard revives a Blackwing from the graveyard: two bodies (and a synchro)");
            else if (whirlwind && options.Any(card => !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)))
                (pick, why) = (options.Where(card => !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)).OrderByDescending(card => card.Attack).First(),
                    "Black Whirlwind searches a smaller Blackwing");
            else
            {
                // BlackwingHybrid leans into the Vayu engine: with nothing but a plain beater on offer, Armageddon Knight /
                // Dark Grepher setting up the graveyard (Vayu and a partner) comes first (user, 2026-11-10). Blizzard's revive
                // and Black Whirlwind's search above still win: they're card advantage.
                if (Variant == BlackwingVariant.Hybrid && Rule(BlackwingRules.HybridSetupFirst))
                {
                    var summonable = (state.SummonableCards ?? Array.Empty<DynamicCard>()).ToList();
                    if (await DeckSummonAsync(context, state, summonable, await GetBotHandCardsAsync(), danger: false,
                            await CurrentField.GetBotLifePoints()) is IdleCmdResponse setup)
                    {
                        Note(context, "hybrid: the Vayu graveyard setup goes ahead of a plain Blackwing beater");
                        return setup;
                    }
                }
                (pick, why) = (options.OrderBy(card => StaplePriorityIndex(order, card)).First(), "an attacker (aggro)");
            }
            if (!Rule(BlackwingRules.AggroSummon))
                return null;
            Note(context, $"normal summon {CardNames.Get(pick.ID)}: {why}");
            return pick.NormalSummon();
        }

        /// <summary>
        /// Takes summons that make no play out of the options, before any step can pick them (user, 2026-11-10):
        ///  - small Blackwings (Level 4 or lower) summoned face-up when they can't hit anything this turn and start no play
        ///    (Black Whirlwind's search, Blizzard's revive, a synchro with a monster already out, Bora / Gale coming out
        ///    next to it): they'd only be run over by a bigger monster. Kalut especially: its value is +1400 from the hand;
        ///  - every Blackwing set: no flip or graveyard effect to gain from being set, and setting monsters to stay alive
        ///    is never worth it.
        /// </summary>

        /// <summary>
        /// Sirocco's boost always goes on a Bora or a Shura when one is out (user, 2026-11-10): Bora pierces a defender or
        /// face-down, Shura's battle kill brings out another Blackwing. Bora against a defender / face-down (or an open field),
        /// Shura against face-up attackers. Default (no Bora or Shura): none.
        /// </summary>
        private static DynamicCard SiroccoTarget(IEnumerable<DynamicCard> ourMonsters, IEnumerable<DynamicCard> theirMonsters)
        {
            var wings = ourMonsters.Where(card => card.IsFaceup()).ToList();
            var bora = wings.FirstOrDefault(card => card.IsCode(CardId.BlackwingBoraTheSpear));
            var shura = wings.FirstOrDefault(card => card.IsCode(CardId.BlackwingShuraTheBlueFlame));
            bool defenders = theirMonsters.Any(card => !card.IsFaceup() || !card.IsAttack());
            bool attackers = theirMonsters.Any(card => card.IsFaceup() && card.IsAttack());
            if (shura.ID > 0 && (bora.ID <= 0 || attackers && !defenders))
                return shura;
            return bora;
        }

        // A planner line putting Sirocco's boost on anything but a Bora / Shura on offer pays the suggestion cost.
        internal override double PlanSuggestions(IReadOnlyList<Search.PlanStep> steps, int index, IReadOnlyList<string> offered, DuelSim.Engine.SideSummary theirs)
        {
            double cost = base.PlanSuggestions(steps, index, offered, theirs);
            if (index == 0 || !steps[index].Key.StartsWith("card:") ||
                !steps[index - 1].Key.StartsWith($"{(int)MainAction.Activate}:") || KeyCardId(steps[index - 1].Key) != CardId.BlackwingSiroccoTheDawn)
                return cost;
            static bool BoraOrShura(string key) => KeyCardId(key) is CardId.BlackwingBoraTheSpear or CardId.BlackwingShuraTheBlueFlame;
            return !BoraOrShura(steps[index].Key) && offered.Any(BoraOrShura) ? cost + SuggestionCost : cost;
        }

        /// <summary>Sirocco's target when its boost is the one-attack lethal and Bora is on offer: Bora, as the card rule says (AllowedTargets
        /// with the board as live sees it). Null otherwise.</summary>
        private async Task<IList<byte>?> SiroccoLethalBoraPickAsync(DecisionContext<SelectCardData> context)
        {
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (LastMainMove is not { Action: MainAction.Activate } move || move.Turn != CurrentTurnNumber ||
                !(move.CardId == CardId.BlackwingSiroccoTheDawn || Search.SearchHost.CanonicalId(move.CardId) == CardId.BlackwingSiroccoTheDawn) ||
                !cards.Any(card => card.IsCode(CardId.BlackwingBoraTheSpear) && card.CurrentCoordinates.Controller == 0))
                return null;
            var ours = await GetBotMonsterCardsAsync();
            var theirs = await GetEnemyMonsterCardsAsync();
            var field = await CurrentField.GetBotField();
            int likelyDefense;
            bool honest;
            lock (OpponentDeck)
            {
                likelyDefense = OpponentDeck.HighestSetDefense() ?? -1;
                honest = HonestSuspected((field.EnemySide.Hand ?? Array.Empty<int>()).Length);
            }
            var ruleContext = new CardRuleRegistry.Context(Array.Empty<int>(), Array.Empty<int>(), 0, 0, await CurrentField.GetBotLifePoints(isOwnSide: false), false,
                OwnMonsterIds: ours.Select(card => card.ID).ToArray(),
                OwnFaceUpAttackers: ours.Where(card => card.IsFaceup() && card.IsAttack()).Select(card => (card.ID, card.Attack)).ToList(),
                OpponentMonsterIds: theirs.Select(card => card.ID).ToArray(),
                OpponentMonsters: theirs.Select(card => (card.ID, card.Attack, card.Defense, card.IsFaceup(), card.IsAttack())).ToList(),
                OpponentSetSpellTraps: (await GetEnemySpellCardsAsync()).Count(card => !card.IsFaceup()),
                OpponentLikelySetDefense: likelyDefense, HonestSuspected: honest,
                OpponentGrave: field.EnemySide.Grave ?? Array.Empty<int>(),
                OpponentHand: (field.EnemySide.Hand ?? Array.Empty<int>()).Where(id => id > 0).ToArray());
            var allowed = CardRuleRegistry.AllowedTargets(CardId.BlackwingSiroccoTheDawn, ruleContext, cards.Select(card => card.ID).ToList());
            if (allowed.Count == cards.Length || !allowed.All(id => Search.SearchHost.Is(id, CardId.BlackwingBoraTheSpear)))
                return null;
            int index = Array.FindIndex(cards, card => card.IsCode(CardId.BlackwingBoraTheSpear) && card.CurrentCoordinates.Controller == 0);
            if (index < 0)
                return null;
            LastMainMove = default; // one target per activation
            return new List<byte> { (byte)index };
        }

        // Sirocco's target when the brain picks it, whoever activated Sirocco (a strong suggestion, user 2026-11-10: a
        // planner line boosting anything else pays the suggestion cost; see PlanSuggestions).
        private IList<byte>? SiroccoPick(DynamicCard[] cards, int minimum, int maximum)
        {
            if (LastMainMove is not { Action: MainAction.Activate } move || move.Turn != CurrentTurnNumber ||
                !(move.CardId == CardId.BlackwingSiroccoTheDawn || Search.SearchHost.CanonicalId(move.CardId) == CardId.BlackwingSiroccoTheDawn) ||
                minimum > 1 || cards.Any(card => card.CurrentCoordinates.Controller != 0 || card.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone))
                return null;
            var target = SiroccoTarget(cards, OpponentMonsters);
            int index = Array.FindIndex(cards, card => target.ID > 0 && card.CurrentCoordinates.Sequence == target.CurrentCoordinates.Sequence && card.CurrentCoordinates.Location == target.CurrentCoordinates.Location);
            if (index >= 0)
                LastMainMove = default; // one target per activation
            return index < 0 ? null : new List<byte> { (byte)index };
        }

        // Black Whirlwind always searches when it can (user, 2026-11-10: "it should always always trigger when possible").
        protected override bool AlwaysTriggers(DynamicCard card) => card.IsCode(CardId.BlackWhirlwind);

        // (Icarus Attack outside our Main Phase 1 was a ban here until 2026-11-25; it's a hold now: OnIdle, IcarusWaitsForTheirTurn.)

        private async Task<IdleCmdData> WithoutPointlessSummonsAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> hand, List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            bool whirlwind = (await CurrentField.GetCardsInSpellZone()).Any(card => card.IsFaceup() && card.IsCode(CardId.BlackWhirlwind));
            // A non-Tuner: Blizzard is a Tuner itself, so reviving Gale or Vayu leaves nothing to synchro with (user, 2026-11-10).
            bool reviveTarget = (await GetBotGraveyardCardsAsync()).Any(card => IsBlackwing(card) && card.Level <= 4 && card.HasType(CardType.Monster) &&
                                                                                 !card.HasType(CardType.Synchro) && !card.HasType(CardType.Tuner));
            bool bodyInHand = hand.Any(card => card.IsCode(CardId.BlackwingBoraTheSpear, CardId.BlackwingGaleTheWhirlwind));
            var ourFaceUp = botMonsters.Where(card => card.IsFaceup()).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);

            bool CanHit(DynamicCard card) =>
                battleAhead && (enemyMonsters.Count == 0 ||
                                enemyMonsters.Any(e => e.IsFaceup() && card.Attack > e.GetRealPower()) ||
                                enemyMonsters.Any(e => !e.IsFaceup()) && card.IsCode(CardId.BlackwingShuraTheBlueFlame, CardId.BlackwingBoraTheSpear));
            string? Play(DynamicCard card)
            {
                if (whirlwind && !card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) return "Black Whirlwind searches";
                if (card.IsCode(CardId.BlackwingBlizzardTheFarNorth) && reviveTarget) return "Blizzard revives a Blackwing to synchro with";
                if (bodyInHand) return "Bora / Gale come out next to it";
                bool tuner = card.HasType(CardType.Tuner);
                if (ourFaceUp.Any(m => m.HasType(CardType.Tuner) != tuner && !m.HasType(CardType.Synchro))) return "a synchro with our monster";
                return null;
            }

            var summonable = state.SummonableCards ?? Array.Empty<DynamicCard>();
            var keep = new List<DynamicCard>();
            foreach (var card in summonable)
            {
                // Blizzard: its revive on Normal Summon is the point (a Level 4 or lower Blackwing back, then a synchro): only
                // with a target in the graveyard; otherwise it waits in hand (user, 2026-11-10: summoning it first wastes it).
                if (card.IsCode(CardId.BlackwingBlizzardTheFarNorth))
                {
                    if (reviveTarget || !Rule(BlackwingRules.BlizzardNeedsTarget))
                        keep.Add(card);
                    else
                    {
                        Note(context, "don't summon Blizzard yet: no Level 4 or lower Blackwing in the graveyard for its revive");
                        Hold(card.ID, null, BlackwingRules.BlizzardNeedsTarget);
                    }
                    continue;
                }
                // Kalut: worth +1400 from the hand in battle, little as a 1400 body (user, 2026-11-10: "rarely worth
                // summoning"). Summoned only when it's the missing piece of a synchro (a Level in our Extra Deck that our
                // Tuner reaches with Kalut and not without it: seen 2026-11-10, Kalut + Blizzard made Level 5, which we
                // don't have, while Shura + Blizzard already made the 6s), or when its damage is lethal.
                if (card.IsCode(CardId.BlackwingKalutTheMoonShadow))
                {
                    var newLevels = await LevelsOnlyWithAsync(ourFaceUp, card.Level);
                    int ourAttack = ourFaceUp.Where(m => m.IsAttack() && !m.Attacked).Sum(m => m.Attack);
                    bool lethal = battleAhead && enemyMonsters.Count == 0 && ourAttack + card.Attack >= oppLp;
                    // The exception (user, 2026-11-10): Black Whirlwind out (or in hand) and Kalut our only Normal Summon: summon
                    // it, and Whirlwind searches Blizzard.
                    bool whirlwindHeld = whirlwind || hand.Any(h => h.IsCode(CardId.BlackWhirlwind));
                    bool onlySummon = summonable.All(other => other.IsCode(CardId.BlackwingKalutTheMoonShadow));
                    if (whirlwindHeld && onlySummon)
                        Note(context, "summon Kalut: our only Normal Summon, and Black Whirlwind searches Blizzard off it");
                    // Its +1400 (attacking or defending) needs another Blackwing of ours to battle. With no other Blackwing on the
                    // field or in hand (only Kaluts), the boost has nothing to go on: Kalut is judged as a monster like any other
                    // (user, 2026-11-12, round-robin 32: held two turns with nothing to boost while Gearframe and Returner hit for
                    // 2400 a turn). A summoned Kalut is then also something a second Kalut in hand can boost.
                    bool otherBlackwing = ourFaceUp.Any(m => IsBlackwing(m) && !m.IsCode(CardId.BlackwingKalutTheMoonShadow)) ||
                                          hand.Any(h => IsBlackwing(h) && h.HasType(CardType.Monster) && !h.IsCode(CardId.BlackwingKalutTheMoonShadow));
                    if (!otherBlackwing)
                        Note(context, "Kalut isn't kept in hand: no other Blackwing on the field or in hand for its +1400, so it's judged as a monster");
                    if (newLevels.Count > 0 || lethal || whirlwindHeld && onlySummon || !otherBlackwing || !Rule(BlackwingRules.KalutStaysInHand))
                        keep.Add(card);
                    else
                    {
                        Note(context, "keep Kalut in hand: +1400 in battle is worth more than a 1400 body (no new synchro or lethal from summoning it)");
                        Hold(card.ID, null, BlackwingRules.KalutStaysInHand);
                    }
                    continue;
                }
                if (!IsBlackwing(card) || card.Level > 4 || CanHit(card) || Play(card) != null)
                {
                    keep.Add(card);
                    continue;
                }
                if (Rule(BlackwingRules.NoPointlessSummon))
                {
                    Note(context, $"don't summon {CardNames.Get(card.ID)}: it can't hit anything this turn and starts no play" +
                                  (card.IsCode(CardId.BlackwingKalutTheMoonShadow) ? " (Kalut is worth +1400 from the hand)" : ""));
                    Hold(card.ID, MainAction.Summon, BlackwingRules.NoPointlessSummon);
                }
                else
                    keep.Add(card);
            }
            // Vayu may be set: it works from the graveyard, so being destroyed face-down feeds its synchro (user, 2026-11-10).
            var sets = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !IsBlackwing(card) || card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)).ToArray();
            var filtered = state;
            filtered.SummonableCards = keep.ToArray();
            filtered.MonsterSetableCards = sets;
            return filtered;
        }

        /// <summary>
        /// Synchro Levels in our Extra Deck that a non-Tuner of this Level would make with our face-up monsters (a Tuner
        /// plus any of our other non-Tuners) and that the board can't already make without it.
        /// </summary>
        private async Task<HashSet<int>> LevelsOnlyWithAsync(List<DynamicCard> ourFaceUp, int newLevel)
        {
            var materials = ourFaceUp.Where(m => m.HasType(CardType.Monster) && !m.HasType(CardType.Synchro) && m.Level > 0).ToList();
            var tuners = materials.Where(m => m.HasType(CardType.Tuner)).ToList();
            var others = materials.Where(m => !m.HasType(CardType.Tuner)).Select(m => m.Level).ToList();
            // Every total of the non-Tuners we could add (any subset; boards are small).
            var subsets = new HashSet<int> { 0 };
            foreach (int level in others)
                foreach (int sum in subsets.ToList())
                    subsets.Add(sum + level);
            var without = new HashSet<int>();
            var with = new HashSet<int>();
            foreach (var tuner in tuners)
                foreach (int sum in subsets)
                {
                    if (sum > 0)
                        without.Add(tuner.Level + sum);
                    with.Add(tuner.Level + sum + newLevel);
                }
            var extraLevels = (await ExtraDeckSynchrosAsync()).Select(s => s.Level).ToHashSet();
            with.ExceptWith(without);
            with.IntersectWith(extraLevels);
            return with;
        }

        /// <summary>
        /// Into an unknown face-down monster: Shura (destroying it summons a Blackwing from the deck), then Bora (piercing:
        /// damage even through a face-down defender). Not Gale: it's for cutting a big monster down (user, 2026-11-10).
        /// </summary>
        protected override DynamicCard? PreferredFaceDownAttacker(List<DynamicCard> attackers) =>
            attackers.FirstOrDefault(card => card.IsCode(CardId.BlackwingShuraTheBlueFlame)) is { ID: > 0 } shura ? shura
            : attackers.FirstOrDefault(card => card.IsCode(CardId.BlackwingBoraTheSpear)) is { ID: > 0 } bora ? bora
            : null;

        protected override string PreferredFaceDownWhy(DynamicCard attacker) =>
            attacker.IsCode(CardId.BlackwingShuraTheBlueFlame)
                ? "destroying it summons a Blackwing from the deck"
                : "piercing: it deals damage even through a face-down defender";

        // Icarus Attack's tribute: our least useful Blackwing (Vayu / Blizzard / Kalut / Gale first, then the smallest).
        private DynamicCard IcarusTribute(List<DynamicCard> botMonsters) =>
            botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && IsBlackwing(card))
                .OrderBy(card => card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor, CardId.BlackwingBlizzardTheFarNorth,
                    CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingGaleTheWhirlwind) ? 0 : 1)
                .ThenBy(card => card.Attack).FirstOrDefault();

        protected override double TributeCost(DynamicCard card) =>
            IsStolen(card) ? 0
            : card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor) ? TributeVayu
            : card.IsCode(CardId.BlackwingBlizzardTheFarNorth, CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingGaleTheWhirlwind) ? TributeSmallBlackwing
            : base.TributeCost(card);

        // ------------------------------------------------------------------ chains

        /// <summary>
        /// Blackwing chain plays: Kalut from hand in battle (+1400: win a battle we'd lose or tie, or for lethal), Icarus
        /// Attack on their turn when it's free: tributing a Blackwing that's dying anyway (attacked by a bigger monster,
        /// or hit by their removal), or as bait (they respond to it). Null when none applies.
        /// </summary>
        private sealed record KalutBattle((int Turn, byte OurZone, int Ours, byte TheirZone, int Theirs) Key, int BaseAtk, int Count);
        private KalutBattle? _kalutPending;
        private KalutBattle? _kalutOffered;

        public override void ResponseSent(BotState state, object? data, byte[] response)
        {
            base.ResponseSent(state, data, response);
            if (state != BotState.SelectChain)
                return;
            if (_kalutOffered is { } battle && data is SelectChainData { SelectableCards: { } cards } &&
                response.Length >= sizeof(int) && BitConverter.ToInt32(response, 0) is var index &&
                index >= 0 && index < cards.Length && cards[index].IsCode(BlackwingKalutTheMoonShadow))
                _kalutPending = battle with { Count = battle.Count + 1 };
            _kalutOffered = null;
        }

        private async Task<int?> TryBlackwingChainAsync(DecisionContext<SelectChainData> context, DynamicCard[] options, bool botTurn)
        {
            var phase = (DuelPhase)context.Header.CurrentPhase;
            int Index(int id) => Array.FindIndex(options, card => card.IsCode(id) && card.CurrentCoordinates.Controller == 0);
            // Black Whirlwind's trigger on our Blackwing's Normal Summon: a free search, every copy (seen 2026-11-10: both
            // passed on Sirocco's summon).
            int whirlwind = Array.FindIndex(options, card => card.IsCode(CardId.BlackWhirlwind) && card.CurrentCoordinates.Controller == 0 &&
                                                             card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
            if (whirlwind >= 0 && Rule(BlackwingRules.BlackWhirlwind))
            {
                Note(context, "chain Black Whirlwind: our Blackwing's Normal Summon searches a Blackwing with less ATK (a free card)");
                return whirlwind;
            }
            // Blizzard's revive on its Normal Summon: always (user, 2026-11-11). Alone it comes as a yes/no; with Black
            // Whirlwind triggering at the same time it comes as a chain prompt, which used to fall through to "pass".
            int blizzard = Array.FindIndex(options, card => card.IsCode(CardId.BlackwingBlizzardTheFarNorth) &&
                                                            card.CurrentCoordinates.Controller == 0 &&
                                                            card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (blizzard >= 0 && Rule(BlackwingRules.BlizzardRevive))
            {
                Note(context, "chain Blizzard: revive a Level 4 or lower Blackwing from the graveyard (a free body, and a synchro)");
                return blizzard;
            }
            var ours =await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var theirs = await CurrentField.GetBotBattlingMonster(isOwnSide: false);

            // Kalut: +1400 to our battling Blackwing when that wins (or stops a loss / tie), or the hit is lethal.
            // (Omega reports the attack's chain windows as BattleStart too: seen 2026-11-12, Blizzard attacked a 1500 counting on
            // Kalut, then Kalut was passed in a "BattleStart" window and Blizzard died. A battling monster of ours is required.)
            if (Index(CardId.BlackwingKalutTheMoonShadow) is int kalut and >= 0 && ours.ID > 0 && IsBlackwing(ours) &&
                phase is DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.BattleStep or DuelPhase.BattleStart or DuelPhase.Battle)
            {
                // Kaluts we already chained on this battle that haven't resolved yet (round-robin 18: a second Kalut went on
                // because Bora still read 1700). Their +1400 counts first; another one only goes when it still changes the
                // outcome (a win, or the damage for lethal: two Kaluts on a direct attack are +2800).
                var battleKey = (context.Header.CurrentTurn, ours.CurrentCoordinates.Sequence, ours.ID,
                    theirs.CurrentCoordinates.Sequence, theirs.ID);
                var pending = _kalutPending is { } sent && sent.Key == battleKey ? sent : new KalutBattle(battleKey, ours.Attack, 0);
                _kalutOffered = pending;
                int pendingBoost = Math.Max(0, pending.BaseAtk + 1400 * pending.Count - ours.Attack);
                int ourPower = ours.Attack + pendingBoost;
                bool weAttack = botTurn;
                int theirPower = theirs.ID > 0 || !theirs.Equals(default(DynamicCard)) ? (theirs.IsAttack() ? theirs.Attack : theirs.Defense) : 0;
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                bool flips = theirs.ID > 0 && ourPower <= theirPower && ourPower + 1400 > theirPower;
                bool lethal = weAttack && (theirs.ID <= 0 ? ourPower + 1400 >= oppLp : theirs.IsAttack() && ourPower + 1400 - theirPower >= oppLp);
                if ((flips || lethal) && Rule(BlackwingRules.KalutBoost))
                {
                    Note(context, $"Kalut: {CardNames.Get(ours.ID)} +1400 ({(lethal ? "lethal" : $"beats {CardNames.Get(theirs.ID)} {theirPower}")})" +
                                  (pendingBoost > 0 ? $", on top of +{pendingBoost} already on the chain" : ""));
                    return kalut;
                }
                if (pendingBoost > 0)
                    Note(context, $"no more Kalut: {CardNames.Get(ours.ID)} is {ourPower} with the +{pendingBoost} already on the chain, " +
                                  (theirs.ID > 0 ? $"and another +1400 doesn't change the battle with {CardNames.Get(theirs.ID)} ({theirPower}) or make it lethal"
                                                 : "and another +1400 isn't lethal"));
            }

            // Icarus Attack on their turn: free when our Blackwing dies anyway (attacked by a bigger monster).
            if (!botTurn && Index(CardId.IcarusAttack) is int icarus and >= 0)
            {
                var enemyCards = (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
                // Their chain targets one of our Blackwings: tribute that one, it's going anyway (user, 2026-11-11).
                // Round-robin 19: also when no target is reported (Omega rarely does), from what the link can remove (the shared
                // DoomedMonstersAsync: Gyzarus's "destroy up to 2 cards" against our Bora, Jar and Icarus). Icarus has a cost,
                // so it's weighed: likely doomed (at least an even chance), and two of their cards to hit.
                if (context.Header.LastChainPlayer == 1 && enemyCards >= 2)
                {
                    // Any Winged Beast of ours, face-up or set (user, 2026-11-18: "the cheapest winged beast to tribute is a doomed winged-beast,
                    // which should trigger this doomed logic even if icarus itself isn't doomed"), weighed like a doomed Icarus: the two cards it
                    // destroys against the tribute, which costs its worth times the chance it survives (DoomedRemovalWorthAsync plans it).
                    var doomed = (await DoomedMonstersAsync(context.Header))
                        .Where(d => d.Card.ID > 0 && (d.Card.Race & (int)CardRace.WingedBeast) != 0 && d.Chance >= IcarusDoomedChance)
                        .OrderByDescending(d => d.Chance).FirstOrDefault();
                    if (doomed.Card.ID > 0 && Rule(BlackwingRules.IcarusFreeTribute))
                    {
                        var (use, why) = await DoomedRemovalWorthAsync(context, options[icarus]);
                        Note(context, $"Icarus Attack: {CardNames.Get(doomed.Card.ID)} is about to go ({doomed.How}, {doomed.Chance:P0}): {why}");
                        if (use)
                        { await MarkAttackAnsweredAsync(context); return icarus; }
                    }
                }
                if (ours.ID > 0 && IsBlackwing(ours) && theirs.ID > 0 && theirs.Attack > ours.GetRealPower() && enemyCards >= 2 &&
                    Rule(BlackwingRules.IcarusFreeTribute))
                {
                    PlanTributes(context.Header.CurrentTurn, new[] { ours }, "Icarus Attack");
                    Note(context, $"Icarus Attack: {CardNames.Get(ours.ID)} dies to {CardNames.Get(theirs.ID)} anyway, so tribute it and destroy two of their cards");
                    { await MarkAttackAnsweredAsync(context); return icarus; }
                }
                // Their End Phase with two cards of theirs and a spare Blackwing: a 2-for-2 that clears the way for our turn.
                var fodder = IcarusTribute(await GetBotMonsterCardsAsync());
                if (phase == DuelPhase.End && enemyCards >= 2 && fodder.ID > 0 && (await GetBotMonsterCardsAsync()).Count >= 2 &&
                    Rule(BlackwingRules.IcarusEndPhase))
                {
                    PlanTributes(context.Header.CurrentTurn, new[] { fodder }, "Icarus Attack");
                    Note(context, $"their End Phase: Icarus Attack, tributing {CardNames.Get(fodder.ID)}: destroy two of their cards before our attack");
                    return icarus;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ selections

        /// <summary>
        /// Blackwing selections: Black Whirlwind's search (Kalut for battle, Gale / Bora as free bodies), Shura's summon from
        /// the deck (Bora), Blizzard's revive (the biggest Level 4 or lower), Allure's banish (the least useful DARK).
        /// </summary>
        private async Task<IList<byte>?> SelectBlackwingAsync(DecisionContext<SelectCardData> context)
        {
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            var hint = (DHint)state.SelectionHint;
            if (cards.Length == 0 || !cards.All(card => card.CurrentCoordinates.Controller == 0))
                return null;
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            bool From(CardLocation location) => indexed.All(x => x.Card.CurrentCoordinates.Location == (byte)location);
            IList<byte> Pick((DynamicCard Card, int Index) x, string what)
            {
                Note(context, $"{what}: {CardNames.Get(x.Card.ID)}");
                return new List<byte> { (byte)x.Index };
            }
            var hand = await GetBotHandCardsAsync();
            // Black Whirlwind's search: Bora / Gale (free bodies next to a Blackwing) unless one's in hand, then Kalut.
            if (hint == DHint.ATOHAND && From(CardLocation.Deck) && indexed.All(x => IsBlackwing(x.Card)))
            {
                // What gets us past the monster in our way (user, 2026-11-11: with Shura stuck on an 1800, "gale would've halved my
                // attack so that shura could get over it, kalut would've got you over me too. even blizzard would've let you have
                // a big synchro play next turn. bora didn't really accomplish anything"): Gale when halving their monster lets
                // our best attacker beat it, else Kalut (+1400 in battle) when that does; with nothing in the way, Blizzard (a
                // synchro next turn) unless we have one; Bora only as a body after those.
                static int Power(DynamicCard card) => card.IsAttack() ? card.Attack : card.Defense;
                int ourBest = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
                var wall = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && Power(card) >= ourBest)
                    .OrderBy(Power).FirstOrDefault();
                bool blizzardOwned = hand.Any(card => card.IsCode(CardId.BlackwingBlizzardTheFarNorth)) ||
                                     (await GetBotGraveyardCardsAsync()).Any(card => card.IsCode(CardId.BlackwingBlizzardTheFarNorth));
                int[] order;
                string why;
                if (wall.ID > 0 && ourBest > 0 && ourBest > wall.Attack / 2 && wall.IsAttack())
                    (order, why) = (new[] { CardId.BlackwingGaleTheWhirlwind, CardId.BlackwingKalutTheMoonShadow },
                        $"Gale halves {CardNames.Get(wall.ID)} ({wall.Attack}) so our {ourBest} gets over it");
                else if (wall.ID > 0 && ourBest > 0 && ourBest + 1400 > Power(wall))
                    (order, why) = (new[] { CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingGaleTheWhirlwind },
                        $"Kalut (+1400 in battle) takes our {ourBest} over {CardNames.Get(wall.ID)} ({Power(wall)})");
                else if (!blizzardOwned)
                    (order, why) = (new[] { CardId.BlackwingBlizzardTheFarNorth, CardId.BlackwingKalutTheMoonShadow },
                        "Blizzard: next turn it revives a Blackwing for a synchro");
                else
                    (order, why) = (new[] { CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingBoraTheSpear }, "a body or a battle boost");
                // When the wanted card isn't in the deck: Blizzard (a synchro next turn) before Kalut / Bora as plain bodies, unless
                // we already have one (seen 2026-11-13: Gale and Kalut gone, Bora searched over Blizzard).
                var fallback = (blizzardOwned ? Array.Empty<int>() : new[] { CardId.BlackwingBlizzardTheFarNorth })
                    .Concat(new[] { CardId.BlackwingKalutTheMoonShadow, CardId.BlackwingBoraTheSpear, CardId.BlackwingGaleTheWhirlwind,
                        CardId.BlackwingBlizzardTheFarNorth, CardId.BlackwingVayuTheEmblemOfHonor });
                var wanted = order;
                order = order.Concat(fallback).Distinct().ToArray();
                var chosen = indexed.OrderBy(x => StaplePriorityIndex(order, x.Card)).First();
                // The note says what was actually taken, and why the first choice wasn't.
                if (!chosen.Card.IsCode(wanted))
                    why = $"wanted {string.Join(" / ", wanted.Select(CardNames.Get))} ({why}), not in the deck; " +
                          (chosen.Card.IsCode(CardId.BlackwingBlizzardTheFarNorth) ? "Blizzard: next turn it revives a Blackwing for a synchro" : "the best body left");
                return Pick(chosen, $"Black Whirlwind searches ({why})");
            }
            // Shura's summon from the deck: Vayu first (user, 2026-11-12: "vayu is particularly useful to get for free as it has a
            // graveyard effect. most of the time shura should go for vayu first"), else the biggest attacker.
            if (hint == DHint.SPSUMMON && From(CardLocation.Deck) && indexed.All(x => IsBlackwing(x.Card)) &&
                indexed.Any(x => x.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)) && Rule(BlackwingRules.ShuraTakesVayu))
                return Pick(indexed.First(x => x.Card.IsCode(CardId.BlackwingVayuTheEmblemOfHonor)), "Shura summons from the deck (Vayu: free, and it works from the graveyard)");
            // Shura's summon from the deck / Blizzard's revive from the graveyard: the biggest attacker.
            if (hint == DHint.SPSUMMON && (From(CardLocation.Deck) || From(CardLocation.Grave)) && indexed.All(x => IsBlackwing(x.Card)))
                // A non-Tuner first: with Blizzard (a Tuner) it makes a synchro.
                return Pick(indexed.OrderBy(x => x.Card.HasType(CardType.Tuner) ? 1 : 0).ThenByDescending(x => x.Card.Attack).First(), "special summon");
            // Allure of Darkness's banish: the shared staple pick (AllureBanishRank).
            if (hint == DHint.REMOVE && From(CardLocation.Hand) && await AllureBanishPickAsync(context, force: true) is IList<byte> allure)
                return allure;
            return null;
        }
    }
}
