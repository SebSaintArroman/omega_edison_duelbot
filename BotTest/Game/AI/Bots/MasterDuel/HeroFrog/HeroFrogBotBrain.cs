using BotTest.Game.AI.Brains;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Bots.MasterDuel.HeroFrog
{
    /// <summary>
    /// HeroFrog (Frog Monarchs + an Elemental HERO / Miracle Fusion package) deck brain. The deck's engine:
    ///  - Treeborn Frog comes back every Standby Phase while we control no Spell/Trap, so tributing it is free (a Tribute
    ///    Summon, Enemy Controller's cost: in our Standby Phase it's back the same turn). So the Spell/Trap zone stays
    ///    clear: only cards we'll activate during their next turn are set (Aoi with a WATER to tribute, Raigeki Break with
    ///    a discard, Trap Dustshoot into 4+ cards), and anything still set is used by their End Phase.
    ///  - Monarchs (Caius banishes, Raiza tops, Thestalos discards) off cheap tributes: a stolen monster first (Brain
    ///    Control / Enemy Controller: it goes back anyway), then Treeborn, then Dupe Frog (it searches a Frog). Their
    ///    triggers are mandatory: Caius / Raiza with nothing of theirs on the field would hit our own card, so not then.
    ///  - Substitoad isn't once per turn: tribute a monster, special summon a Frog from the deck, again and again (Swap
    ///    Frog dumps Treeborn to the graveyard), ending on Dupe Frog and then tributing Substitoad itself for the second
    ///    Dupe Frog: with two Dupe Frogs they can't target anything for an attack (the Dupe lock).
    ///  - Miracle Fusion: Absolute Zero from graveyard materials (a HERO + a WATER); when it leaves the field it destroys
    ///    all their monsters (Ocean returns it in our Standby Phase to set that off; tributing it does too).
    /// Every rule writes a "herofrog:" note into the duel log.
    /// </summary>
    public sealed class HeroFrogBotBrain : DeckBotBrain
    {
        // ------------------------------------------------------------------ tunable numbers (bot-tuning-HeroFrog.txt, user 2026-11-16)

        [Tunable("The search's setup value for Treeborn Frog in our graveyard with no spell/trap of ours out (it comes back).")]
        public static double TreebornLiveValue = 0.6; // an exception to "no deck graveyard values", like Jinzo (user, 2026-11-18: "what happened to this deck getting a bonus for setting treeborn frog up?")
        [Tunable("The search's setup value for Treeborn Frog in our graveyard while a spell/trap of ours blocks it (negative: a cost).")]
        public static double TreebornBlockedValue = -2; // blocked by our spell/trap it can't come back: cancels its GY reading (1.0) and costs 1.0 more, above the set-card bonuses (user, 2026-11-18)
        [Tunable("The search's setup value for the Dupe Frog lock (two face-up Dupe Frogs).")]
        public static double DupeLockValue = 2.0;
        [Tunable("The search's setup value for one face-up Dupe Frog.")]
        public static double DupeOneValue = 0.3;
        [Tunable("A Monarch is held when our cheapest tribute costs at least this (a boss-level monster of ours).")]
        public static double MonarchHoldTributeCost = 4.5;
        [Tunable("Caius targets their DARK monster (+1000 damage) when it's at least this share of their biggest threat.")]
        public static double CaiusDarkTargetShare = 0.8;
        [Tunable("Raiza's value on an Extra Deck monster or token (gone for good): its threat x this.")]
        public static double RaizaGoneForGoodFactor = 1.5;
        [Tunable("Raiza's value on a face-up Level 5+ main-deck monster (comes straight back): its threat x this.")]
        public static double RaizaComesBackFactor = 0.5;
        [Tunable("Tribute order: Absolute Zero while they have 2+ monsters (its wipe is live).")]
        public static double TributeAbsoluteZeroLive = 0.5;
        [Tunable("Tribute order: Absolute Zero otherwise.")]
        public static double TributeAbsoluteZero = 5;
        [Tunable("Tribute order: a Dupe Frog while the lock (2 Dupes) is up.")]
        public static double TributeDupeLock = 4;
        [Tunable("Tribute order: a Dupe Frog otherwise.")]
        public static double TributeDupe = 2.4;
        [Tunable("Tribute order: a token.")]
        public static double TributeToken = 1.5;
        [Tunable("Tribute order: Infernal Prodigy.")]
        public static double TributeInfernalProdigy = 1.6;
        [Tunable("Tribute order: Battle Fader.")]
        public static double TributeBattleFader = 1.7;
        [Tunable("Tribute order: Swap Frog.")]
        public static double TributeSwapFrog = 2;
        [Tunable("Tribute order: Junk Synchron.")]
        public static double TributeJunkSynchron = 2.5;
        [Tunable("Tribute order: Substitoad.")]
        public static double TributeSubstitoad = 3.2;
        [Tunable("Tribute order: Elemental HERO Ocean / Stratos.")]
        public static double TributeOceanStratos = 3.3;
        [Tunable("Tribute order: Des Frog (expendable once it has attacked).")]
        public static double TributeDesFrog = 1.8;

        private static readonly int[] Monarchs =
        {
            HeroFrogCardId.CaiusTheShadowMonarch, HeroFrogCardId.RaizaTheStormMonarch, HeroFrogCardId.ThestalosTheFirestormMonarch
        };

        private static readonly int[] TributeSummons =
        {
            HeroFrogCardId.CaiusTheShadowMonarch, HeroFrogCardId.RaizaTheStormMonarch, HeroFrogCardId.ThestalosTheFirestormMonarch,
            HeroFrogCardId.DesFrog
        };

        private static readonly int[] Heroes =
        {
            HeroFrogCardId.EvilHeroInfernalProdigy, HeroFrogCardId.ElementalHeroStratos, HeroFrogCardId.ElementalHeroOcean,
            HeroFrogCardId.ElementalHeroAbsoluteZero
        };

        // Miracle Fusion's WATER material, cheapest first (Treeborn never: it comes back every turn).
        private static readonly int[] WaterMaterials =
        {
            HeroFrogCardId.SwapFrog, HeroFrogCardId.DupeFrog, HeroFrogCardId.Substitoad, HeroFrogCardId.DesFrog,
            HeroFrogCardId.ElementalHeroOcean
        };

        // Swap Frog's trigger: the Level 2- WATER Aqua sent from the deck to the graveyard, best first (user, 2026-11-18): Treeborn (it comes
        // back), then Substitoad, then another Swap Frog; Dupe Frog last (its search only fires from the field).
        private static readonly int[] SwapFrogSends =
        {
            HeroFrogCardId.TreebornFrog, HeroFrogCardId.Substitoad, HeroFrogCardId.SwapFrog, HeroFrogCardId.DupeFrog
        };

        private static readonly int[] OwnTriggers =
        {
            HeroFrogCardId.SwapFrog, HeroFrogCardId.DupeFrog, HeroFrogCardId.TreebornFrog, HeroFrogCardId.ElementalHeroStratos,
            HeroFrogCardId.JunkSynchron
        };

        private static readonly int[] ChainCandidates =
        {
            HeroFrogCardId.GorzTheEmissaryOfDarkness, HeroFrogCardId.TrapDustshoot, HeroFrogCardId.HeavyStorm,
            HeroFrogCardId.CompulsoryEvacuationDevice, HeroFrogCardId.JarOfGreed
        };

        private static readonly Dictionary<int, int> Copies = new()
        {
            [HeroFrogCardId.TreebornFrog] = 2,
            [HeroFrogCardId.DupeFrog] = 2,
            [HeroFrogCardId.SwapFrog] = 3,
            [HeroFrogCardId.Substitoad] = 3,
            [HeroFrogCardId.DesFrog] = 0,
            [HeroFrogCardId.BattleFader] = 1,
        };

        private int _activationTurn = -1;
        private readonly Dictionary<int, int> _activationsThisTurn = new();
        // What a card we're about to use picks: a discard from hand, a monster from the deck, Enemy Controller's mode.
        private int _discardPick;
        private int _deckSummonPick;
        private int _enemyControllerMode; // 0: none planned, 1: change position, 2: take control (effect index + 1)

        // Field facts (OnFieldRefreshed).
        private bool _treebornInGrave;
        private bool _treebornOnField;
        private int _ourSpellTraps;
        private int _enemyMonsters;
        private int _dupesOnField;

        public HeroFrogBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
            // Battle Fader and Aoi were tested at 75% (fired vs skipped, rounds 2 and 3, v2): Battle Fader -3.1 then +1.9,
            // Aoi -0.2 then -3.0: neutral within the noise, so both fire always again (as designed).
        }

        // Version history (DuelSim records it per duel): 1 = 2026-11-09 first versioned build (Treeborn-first Substitoad,
        // steals only to clear two cards, Raigeki on the biggest attacker). 2 = round 1: crash trades only with expendable
        // monsters, Raiza avoids topdecking a Monarch that comes straight back, pressure summons into an open field,
        // Gorz tribute summoned when dead in hand, Miracle Fusion off a field HERO (and a HERO summoned for it), Battle
        // Fader / Aoi under test. 3 = round 3: no rules under test. 4 = user's notes (2026-11-09): new list, Swap Frog
        // Normal Summoned + bounces itself in Main Phase 2, Treeborn always the cheapest tribute, Monarchs readily, Battle
        // Fader only vs lethal, Miracle Fusion graveyard-first and held when not needed, no Gorz tribute; shared: floaters
        // attacked.
        public override int BrainVersion => 8; // v8 (2026-11-11 sims): Fusion is removal + spares Treeborn, steal only past our battle, Dupe lock first when losing, Monarch takes the summon; v7 (2026-11-11 comments): Dupe lock in MP1 / from hand, no redundant backrow, doomed sets, EC for game only, Swap attacks + bounces, no Prodigy body, Fusion waits for battle; v6: turn planner, Dupe lock goal fix, MST bait fixes

        /// <summary>
        /// Play search setup: Treeborn Frog in our graveyard comes back every Standby Phase, but only while we control no
        /// spells/traps (Treeborn first): worth more with an empty backrow, less with one.
        /// </summary>
        // (The frog engine's plays were once protected from the search: its quick random playouts couldn't value them. Lines are
        // played by the brain now, so the search's better move is played here too; user, 2026-11-17: "I don't like the sound of it".)

        /// <summary>Treeborn Frog comes back in our Standby Phase (face-up defence, as the brain revives it) while we control no spells/traps.</summary>
        internal override void ProjectStandby(Search.SearchView view)
        {
            if (view.OwnSpells.Count > 0)
                return;
            foreach (int id in new[] { HeroFrogCardId.TreebornFrog, HeroFrogCardId.TreebornFrog + 20 })
                while (view.ReviveFromGrave(id, 0x4))
                {
                }
        }

        internal override Func<DuelSim.Engine.SideSummary, double> SearchSetupFor(Search.SearchView view)
        {
            static bool IsTreeborn(int code) => Search.SearchHost.Is(code, HeroFrogCardId.TreebornFrog);
            if (view.OwnGrave.Any(IsTreeborn) || view.OwnMonsters.Any(monster => IsTreeborn(monster.Code)))
                return SearchSetupValue;

            return own => SearchSetupValue(own) + (own.GraveCodes.Any(IsTreeborn) ? 5 : 0);
        }

        internal override double SearchSetupValue(DuelSim.Engine.SideSummary own)
        {
            double value = 0;
            // Treeborn in the graveyard comes back every Standby Phase, but only while we control no spells/traps.
            if (own.GraveCodes.Any(code => Search.SearchHost.Is(code, HeroFrogCardId.TreebornFrog)))
                value += own.SpellCodes.Count == 0 ? TreebornLiveValue : TreebornBlockedValue;
            // The Dupe lock: with two face-up Dupe Frogs they can't attack at all (each one can't be attacked while the other
            // is there). One is worth a little (it soaks attacks); two, the lock, a lot (user, 2026-11-10: the search ended the
            // turn rather than make the second Dupe Frog).
            // On the field Dupe Frog is treated as "Des Frog", and the engine reports that code: tell it by Level 2 (Des Frog
            // is 5), as IsDupe does (seen 2026-11-10: the lock counted nothing, so the turn planner never went for it).
            int dupes = own.Monsters.Count(m => m.FaceUp && (Search.SearchHost.Is(m.Code, HeroFrogCardId.DupeFrog) ||
                                                              m.Code == HeroFrogCardId.DesFrog && m.Level == 2));
            value += dupes >= 2 ? DupeLockValue : dupes == 1 ? DupeOneValue : 0;
            return value;
        }

        // Equal-ATK crash trades lost here (round 1: 41.8% won, -16.6 genre-controlled lift, n 273): a Monarch is worth
        // more than the 1-for-1. Only expendable monsters (Treeborn, Dupe Frog...) trade.
        protected override bool AllowCrashTrade(DynamicCard attacker, DynamicCard target) => IsExpendable(attacker);

        protected override bool HandlesStapleChains => true;
        protected override bool HandlesStapleIdle => true;
        protected override int[] ValueSetIds => ValueSets;

        private static readonly int[] ValueSets = { HeroFrogCardId.TreebornFrog, HeroFrogCardId.DupeFrog, HeroFrogCardId.SwapFrog };

        /// <summary>Omega's pre-errata copies are the official id + 20 (Stratos, Swap Frog, Brain Control, Absolute Zero).</summary>
        public static bool IsId(DynamicCard card, int id) => card.IsCode(id) || card.IsCode(id + 20);

        private static bool IsId(DynamicCard card, int[] ids) => ids.Any(id => IsId(card, id));

        /// <summary>Dupe Frog is named "Des Frog" while on the field: tell it apart by its stats (100 / 2000, Level 2).</summary>
        private static bool IsDupe(DynamicCard card) =>
            IsId(card, HeroFrogCardId.DupeFrog) ||
            card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && card.IsCode(HeroFrogCardId.DesFrog) && card.Level == 2;

        private static bool IsWater(DynamicCard card) => (card.Attribute & (int)CardAttribute.Water) != 0;

        private static bool IsHero(DynamicCard card) => IsId(card, Heroes);

        // ------------------------------------------------------------------ main phase

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default) =>
            await FlipBeforeSummonAsync(context, await DecideMainPhaseAsync(context, ct));

        private async Task<IdleCmdResponse> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            StormHoldNoted = false;
            await RefreshBackrowOddsAsync(context);
            var state = WithoutNeverSummons(context.StateData);
            int turn = context.Header.CurrentTurn;

            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            OpponentMemory.Observe(enemyMonsters);
            await RefreshRewardOnDestroyAsync(enemyMonsters);
            var enemySpells = await GetEnemySpellCardsAsync();
            var botMonsters = await GetBotMonsterCardsAsync();
            // Counted fresh every decision (it was only updated as a side effect of InDeckAsync and went stale: seen 2026-11-11,
            // the Swap Frog route to the lock stopped after the first Dupe because the count still said 0).
            _dupesOnField = botMonsters.Count(card => card.IsFaceup() && IsDupe(card));
            var botSpells = (await CurrentField.GetCardsInSpellZone()).ToList();
            var hand = await GetBotHandCardsAsync();

            // 1. Free card advantage (Upstart Goblin, Reinforcement of the Army: shared), E - Emergency Call.
            if (await TryFreeCardAdvantageAsync(context, state) is IdleCmdResponse free) return free;
            if (TryGetActivable(state, HeroFrogCardId.EEmergencyCall, out var call) && Rule(HeroFrogRules.EmergencyCall))
            {
                Note(context, "activate E - Emergency Call: search an Elemental HERO");
                MarkActivated(context, HeroFrogCardId.EEmergencyCall);
                return call.MainActivateFromIndex();
            }

            // 2. Backrow removal (Heavy Storm: shared; it also clears our own zone for Treeborn).
            if (await TryBackrowRemovalAsync(context, state, enemySpells.Count, botSpells.Count(card => card.ID > 0 && !card.IsCode(HeroFrogCardId.HeavyStorm))) is IdleCmdResponse backrow)
                return backrow;

            // 4. Miracle Fusion: Absolute Zero from graveyard materials.
            if (await TryMiracleFusionAsync(context, state) is IdleCmdResponse fusion) return fusion;

            // 5. Monarchs: fodder first if there is none (Prodigy, One for One), steal a monster to tribute (Brain Control,
            //    Enemy Controller), then the Tribute Summon.
            // The Dupe lock first when we're losing on board: a Monarch that dies next turn doesn't save us.
            if (await TryDupeLockFirstAsync(context, state, botMonsters, enemyMonsters) is IdleCmdResponse lockFirst)
            {
                return lockFirst;
            }
            if (await TryDupeLockViaSwapAsync(context, state, hand, botMonsters, enemyMonsters) is IdleCmdResponse swapLock)
                return swapLock;
            if (await TryMonarchSetupAsync(context, state, hand, botMonsters, enemyMonsters) is IdleCmdResponse setup)
            {
                HoldOtherSummons(state);
                return setup;
            }
            if (await TryMonarchAsync(context, state, botMonsters, enemyMonsters, enemySpells) is IdleCmdResponse monarch)
            {
                HoldOtherSummons(state);
                return monarch;
            }

            // 6. Substitoad again and again: dump Frogs, end on two Dupe Frogs (the lock).
            if (await TrySwapFodderForLockAsync(context, state, botMonsters) is IdleCmdResponse fodder) return fodder;
            if (await TrySubstitoadAsync(context, state, botMonsters) is IdleCmdResponse toad) return toad;
            if (await TryDupeFromHandAsync(context, state, botMonsters) is IdleCmdResponse dupeFromHand)
            {
                return dupeFromHand;
            }

            // 7. Junk Synchron: revive a Level 2 and synchro (Catastor / Magical Android), and the synchro it planned.
            if (TryPlannedSynchro(context, state) is IdleCmdResponse synchro) return synchro;
            if (await TryJunkSynchronAsync(context, state, botMonsters, enemyMonsters) is IdleCmdResponse junk)
            {
                return junk;
            }

            // 8. Raigeki Break on our turn: clear a threat or a set card in our way (a cheap discard: Treeborn best).
            if (await TryRaigekiMainAsync(context, state, hand, enemyMonsters, enemySpells) is IdleCmdResponse raigeki) return raigeki;
            // 8a. Shared monster staples (Brain Control's other uses, Smashing Ground; round-robin 36, 47), after our own
            // Monarch-tribute uses above.
            if (await TryMonsterStaplesAsync(context) is IdleCmdResponse monsterStaple) return monsterStaple;

            // 8b. Substitoad is the engine: Normal Summon it (ahead of setting a Frog) while a Dupe / Swap Frog is in the deck.
            if (await TrySummonSubstitoadAsync(context, state) is IdleCmdResponse sub)
            {
                return sub;
            }

            // 8b1. Swap Frog: Normal Summon it (no special summon by discarding: like Dark Grepher), its trigger sends
            //      Treeborn to the graveyard: Treeborn live first.
            if (await TryNormalSwapAsync(context, state) is IdleCmdResponse swapNs)
            {
                return swapNs;
            }

            // 8b2. Miracle Fusion in hand with a WATER in the graveyard but no HERO there: summon a HERO (Stratos first:
            //      its search / pop resolves, then Miracle Fusion banishes it from the field for Absolute Zero).
            if (await TryHeroForFusionAsync(context, state, hand) is IdleCmdResponse hero)
            {
                return hero;
            }

            // 8c. Pressure: an attacker (Stratos, Ocean...) into an open field: it hits this turn.
            if (await TryPressureSummonAsync(context, state, enemyMonsters) is IdleCmdResponse pressure)
            {
                return pressure;
            }

            // 8d. Main Phase 2 (or no battle): Swap Frog returns itself to the hand (safe for next turn instead of being
            //     destroyed; no extra Frog is summoned).
            if (await TrySwapBounceAsync(context, state, botMonsters) is IdleCmdResponse bounce) return bounce;

            // 9. The Normal Summon or Set (shared choice; Stratos first). Never Infernal Prodigy as a body (user, 2026-11-11:
            //    its only purpose is the special summon from the hand as tribute fodder for a Monarch, done above, or in a
            //    pinch a HERO on the field for Miracle Fusion, TryHeroForFusionAsync above).
            var nsState = state;
            foreach (var prodigy in (state.SummonableCards ?? Array.Empty<DynamicCard>()).Concat(state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                         .Where(card => IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy)))
                Hold(prodigy.ID, null, HeroFrogRules.ProdigyFodder);
            nsState.SummonableCards = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !IsId(card, TributeSummons) && !IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy)).ToArray();
            // Never Swap Frog face-down: its effects are its summon trigger (a Frog to the graveyard) and its bounce, both lost
            // when set; set it is a 500 DEF wall (seen 2026-11-11: set as a "value set", and Gearframe ran it over). Summoned,
            // it dumps a Frog and goes back to the hand in Main Phase 2 to do it again.
            foreach (var swapSet in (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Where(card => IsId(card, HeroFrogCardId.SwapFrog)))
                Hold(swapSet.ID, MainAction.SetMonster, HeroFrogRules.SwapNeverSet);
            nsState.MonsterSetableCards = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy) && !(IsId(card, HeroFrogCardId.SwapFrog) && Rule(HeroFrogRules.SwapNeverSet))).ToArray();
            var summonOrSet = await ChooseNormalSummonOrSetAsync(context, nsState, enemyMonsters, enemySpells, botMonsters, hand);
            if (!summonOrSet.HasValue && !NormalSummonUsed(context.Header) && TryGetSummonable(state, HeroFrogCardId.SwapFrog, out var swapBody) &&
                await InDeckAsync(HeroFrogCardId.TreebornFrog) + await InDeckAsync(HeroFrogCardId.SwapFrog) + await InDeckAsync(HeroFrogCardId.DupeFrog) > 0)
            {
                Note(context, "normal summon Swap Frog rather than leave the summon unused: its trigger sends a Frog to the graveyard, and it can return to the hand in Main Phase 2");
                summonOrSet = swapBody.NormalSummon();
            }
            if (summonOrSet.HasValue)
            {
                return summonOrSet.Value;
            }

            // 10. Spell/Trap sets: only cards we'll activate during their turn (Treeborn needs our zone clear).
            var setState = await WithPlannedSetsOnly(context, state, hand, botMonsters);
            var (trapSet, trapBattleAhead, gorzLive) = await ChooseTrapSetAsync(context, setState, enemyMonsters, botMonsters, hand);
            if (trapSet is IdleCmdResponse setTrap)
                return setTrap;

            var fallbackState = setState;
            fallbackState.SummonableCards = Array.Empty<DynamicCard>();
            fallbackState.MonsterSetableCards = Array.Empty<DynamicCard>();
            fallbackState.SpecialSummonableCards = Array.Empty<DynamicCard>();
            // Our own plays are decided above: the generic fallback only moves on (battle / end), or sets what passed.
            fallbackState.ActivableCards = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !IsId(card, new[] { HeroFrogCardId.Substitoad, HeroFrogCardId.SwapFrog, HeroFrogCardId.EnemyController,
                    HeroFrogCardId.BrainControl, HeroFrogCardId.MiracleFusion, HeroFrogCardId.OneForOne, HeroFrogCardId.RaigekiBreak,
                    HeroFrogCardId.SpiritualWaterArtAoi, HeroFrogCardId.ElementalHeroOcean })).ToArray();
            if (trapBattleAhead || gorzLive)
                fallbackState.SpellSetableCards = Array.Empty<DynamicCard>();
            return await base.OnIdleCmdAsync(context with { StateData = fallbackState }, ct);
        }

        // ------------------------------------------------------------------ Spell/Trap zone for Treeborn

        /// <summary>
        /// Treeborn comes back only while we control no Spell/Trap, so only what we'll activate during their next turn is
        /// set: Aoi with a WATER monster of ours to tribute, Raigeki Break with another card to discard, Trap Dustshoot into
        /// a hand of 4+ (theirs after the draw). Never Enemy Controller / Brain Control (played from the hand).
        /// </summary>
        private async Task<IdleCmdData> WithPlannedSetsOnly(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand, List<DynamicCard> botMonsters)
        {
            var sets = state.SpellSetableCards ?? Array.Empty<DynamicCard>();
            if (sets.Length == 0)
                return state;
            int oppHand = await OppHandAsync();
            bool water = botMonsters.Any(card => card.ID > 0 && card.IsFaceup() && IsWater(card));
            var kept = new List<DynamicCard>();
            // Frogs don't like redundancy on the backrow (user, 2026-11-11): one answer to their monsters at a time (Aoi,
            // Raigeki Break, Compulsory: the same issue), always, whether Treeborn is in the graveyard yet or still on the
            // field / in the deck: each extra set card is one more that must be used (suboptimally) before Treeborn can come
            // back, and a Monarch drawn then has nothing to tribute (seen 2026-11-10 and 2026-11-11: Raigeki Break and
            // Compulsory both set; one had to be fired just to clear the zone).
            int[] monsterAnswers = { HeroFrogCardId.SpiritualWaterArtAoi, HeroFrogCardId.RaigekiBreak, HeroFrogCardId.CompulsoryEvacuationDevice };
            bool answerSet = (await CurrentField.GetCardsInSpellZone()).Any(card => card.ID > 0 && monsterAnswers.Any(id => IsId(card, id)));
            foreach (var card in sets)
            {
                string? why = null;
                if (monsterAnswers.Any(id => IsId(card, id)) && answerSet && Rule(HeroFrogRules.NoRedundantBackrow))
                {
                    Note(context, $"don't set {CardNames.Get(card.ID)}: we already have an answer to their monsters set (no redundant backrow: every set card holds Treeborn Frog back)");
                    Hold(card.ID, MainAction.SetSpell, HeroFrogRules.NoRedundantBackrow);
                    continue;
                }
                // Aoi needs cards in their hand to look at: set into an empty hand it only sits there holding Treeborn back
                // (seen 2026-11-11: set with their hand at 0; Treeborn died and couldn't return while Aoi waited).
                if (IsId(card, HeroFrogCardId.SpiritualWaterArtAoi) && water && oppHand + 1 >= 2)
                    why = "Aoi: we have a WATER monster to tribute on their turn";
                else if (IsId(card, HeroFrogCardId.RaigekiBreak) && hand.Count >= 2)
                    why = "Raigeki Break: we have a card to discard on their turn";
                else if (IsId(card, HeroFrogCardId.TrapDustshoot) && oppHand + 1 >= 4)
                    why = $"Trap Dustshoot: they'll have {oppHand + 1} cards after their draw";
                else if (IsId(card, HeroFrogCardId.JarOfGreed))
                    why = "Jar of Greed: drawn on their turn at the latest";
                else if (IsId(card, HeroFrogCardId.CompulsoryEvacuationDevice))
                    why = "Compulsory: on their attack, or their best monster at their End Phase";
                if (why != null)
                    kept.Add(card);
                else if (!Rule(HeroFrogRules.TreebornHoldsSets))
                    kept.Add(card);
                else if (card.HasType(CardType.Trap))
                {
                    Note(context, $"don't set {CardNames.Get(card.ID)}: we couldn't use it on their turn, and a set card stops Treeborn Frog coming back");
                    Hold(card.ID, MainAction.SetSpell, HeroFrogRules.TreebornHoldsSets);
                }
            }
            state.SpellSetableCards = kept.ToArray();
            return state;
        }

        // ------------------------------------------------------------------ Swap Frog

        /// <summary>
        /// Swap Frog by Normal Summon (no special summon by discarding a WATER: like Dark Grepher, the discard isn't
        /// worth it): its trigger sends Treeborn from the deck to the graveyard. While Treeborn isn't live yet.
        /// </summary>
        private async Task<IdleCmdResponse?> TryNormalSwapAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (_treebornInGrave || _treebornOnField || await InDeckAsync(HeroFrogCardId.TreebornFrog) == 0 || !Rule(HeroFrogRules.SwapFrogSummon))
                return null;
            // Ocean in hand: Swap Frog comes out by discarding it instead (user, 2026-11-10: "most of the time you'll discard it
            // for swap frog"), keeping the Normal Summon.
            var ocean = (await GetBotHandCardsAsync()).FirstOrDefault(card => IsId(card, HeroFrogCardId.ElementalHeroOcean));
            if (ocean.ID > 0 && TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out var swapSpecial))
            {
                _discardPick = ocean.ID;
                Note(context, "special summon Swap Frog discarding Elemental HERO Ocean: its trigger sends Treeborn Frog to the graveyard, and the Normal Summon stays free");
                return swapSpecial.SpecialSummon();
            }
            if (NormalSummonUsed(context.Header) || !TryGetSummonable(state, HeroFrogCardId.SwapFrog, out var swap))
                return null;
            Note(context, "normal summon Swap Frog: its trigger sends Treeborn Frog to the graveyard (Treeborn live first)");
            return swap.NormalSummon();
        }

        /// <summary>
        /// Swap Frog's effect on itself in Main Phase 2 (or with no battle ahead): back to the hand, safe for next turn
        /// rather than destroyed on the field. We take more damage; that matters little. No extra Frog is summoned.
        /// </summary>
        private async Task<IdleCmdResponse?> TrySwapBounceAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters)
        {
            // Only in Main Phase 2 (or turn 1, which has no Battle Phase): on the field Swap Frog attacks for 1000 and pumps
            // Absolute Zero (a WATER monster) first (user, 2026-11-10). A Main Phase 1 whose Battle Phase would be skipped
            // (nothing of ours can attack) counts as no battle ahead: bounce now, or the turn ends with it still out (seen
            // 2026-11-11: Swap Frog in defence, the Battle Phase skipped, no bounce).
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               await BattleWorthEnteringAsync() == null;
            bool summonLeft = !NormalSummonUsed(context.Header) &&
                              ((state.SummonableCards ?? Array.Empty<DynamicCard>()).Length + (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Length) > 0;
            if (battleAhead || summonLeft)
                return null;
            var swap = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => IsId(card, HeroFrogCardId.SwapFrog) && card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (swap.ID <= 0 || !Rule(HeroFrogRules.SwapBounce))
                return null;
            PlanTarget(context.Header.CurrentTurn, swap, theirs: false);
            Note(context, "Swap Frog returns itself to the hand: safe for next turn instead of being destroyed");
            MarkActivated(context, HeroFrogCardId.SwapFrog);
            return swap.MainActivateFromIndex();
        }

        // ------------------------------------------------------------------ Miracle Fusion

        private async Task<IdleCmdResponse?> TryMiracleFusionAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (!TryGetActivable(state, HeroFrogCardId.MiracleFusion, out var fusion))
                return null;
            // Absolute Zero is for a turn it attacks (or wipes their board): not in Main Phase 2, where it only banishes the
            // materials for nothing (user, 2026-11-11: "why miracle fusion on mp2? ... just set up the dupe lock and pass"),
            // unless holding it loses the duel.
            bool fusionBattleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            // Miracle Fusion is removal more than a monster (user, 2026-11-11): Absolute Zero leaving the field destroys all
            // their monsters, so whatever removes it (a trap, a bigger attacker, Brionac) takes their board with it. With
            // monsters of theirs out it works in Main Phase 2 too (seen 2026-11-11: held in Main Phase 2 against their Gorz
            // and token, which would have attacked into it and died with it).
            var theirMonsters = (await GetEnemyMonsterCardsAsync()).Where(card => !IsStolen(card)).ToList();
            bool wipeTargets = theirMonsters.Count >= 2 || theirMonsters.Any(card => card.IsFaceup() && card.Attack >= 2500);
            bool losing = await WeLoseNextTurnAsync();
            if (!fusionBattleAhead && !wipeTargets && !losing && Rule(HeroFrogRules.FusionWaitsForBattle))
            {
                Note(context, "hold Miracle Fusion: no battle ahead this turn (Absolute Zero would only sit there); next turn it attacks");
                Hold(HeroFrogCardId.MiracleFusion, MainAction.Activate, HeroFrogRules.FusionWaitsForBattle);
                return null;
            }
            var grave = (await GetBotGraveyardCardsAsync()).Where(card => card.ID > 0 && card.HasType(CardType.Monster)).ToList();
            // Graveyard materials first, always (an Absolute Zero there is a HERO too; seen 2026-11-09 banishing Ocean
            // from the field with a Zero in the graveyard).
            var hero = grave.Where(IsHero)
                .OrderBy(card => IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy) ? 0 : IsId(card, HeroFrogCardId.ElementalHeroAbsoluteZero) ? 1
                    : IsId(card, HeroFrogCardId.ElementalHeroOcean) ? 2 : 3)
                .FirstOrDefault();
            // No HERO in the graveyard: one of ours on the field (its summon effect already used) is banished instead.
            if (hero.ID <= 0)
                hero = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && IsHero(card) && !card.HasType(CardType.Fusion))
                    .OrderBy(card => IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy) ? 0 : IsId(card, HeroFrogCardId.ElementalHeroStratos) ? 1 : 2)
                    .FirstOrDefault();
            if (hero.ID <= 0)
                return null;
            var water = grave.Where(card => IsWater(card) && !IsId(card, HeroFrogCardId.TreebornFrog) &&
                                            !(card.CurrentCoordinates.Location == hero.CurrentCoordinates.Location &&
                                              card.CurrentCoordinates.Sequence == hero.CurrentCoordinates.Sequence && card.IsCode(hero.ID)))
                .OrderBy(card => StaplePriorityIndex(WaterMaterials, card)).FirstOrDefault();
            if (water.ID <= 0)
            {
                // The only WATER left is Treeborn Frog: never banish the engine for it (seen 2026-11-11: the search fused
                // Treeborn from the field with Absolute Zero from the graveyard, and the deck had no plays after).
                Hold(HeroFrogCardId.MiracleFusion, MainAction.Activate, HeroFrogRules.FusionSparesTreeborn);
                // A WATER for it through Swap Frog: its Normal Summon trigger sends a Frog from the deck (not Treeborn) to the
                // graveyard, and Miracle Fusion follows (seen 2026-11-11: Miracle Fusion sat in hand for five turns against a
                // lone Goyo Guardian; Absolute Zero there was 4000 ATK with two Dupes and Substitoad beside it). Room for Swap
                // Frog and Absolute Zero both.
                if ((wipeTargets || losing) && !NormalSummonUsed(context.Header) && TryGetSummonable(state, HeroFrogCardId.SwapFrog, out var swapForWater) &&
                    (await GetBotMonsterCardsAsync()).Count <= 3 && Rule(HeroFrogRules.FusionWaterViaSwap))
                {
                    _swapSendForFusionTurn = context.Header.CurrentTurn;
                    Note(context, "normal summon Swap Frog: its trigger sends a WATER Frog to the graveyard, the material Miracle Fusion lacks (not Treeborn)");
                    return swapForWater.NormalSummon();
                }
                return null;
            }
            // Held when our board already has the attack it needs (user rule, 2026-11-09): our best attacker beats
            // everything of theirs, unless Absolute Zero makes this turn lethal.
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() &&
                !(card.CurrentCoordinates.Location == hero.CurrentCoordinates.Location && card.CurrentCoordinates.Sequence == hero.CurrentCoordinates.Sequence)).ToList(); // (not the HERO it banishes)
            var theirs = await GetEnemyMonsterCardsAsync();
            int ourBest = ours.Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            int theirBest = theirs.Select(card => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense)
                .DefaultIfEmpty(0).Max();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            // Absolute Zero gains 500 ATK for each other WATER monster on the field (two Dupes and Substitoad: 4000).
            int zeroAtk = 2500 + 500 * (await GetBotMonsterCardsAsync()).Count(card => card.IsFaceup() && IsWater(card) &&
                !(card.CurrentCoordinates.Sequence == hero.CurrentCoordinates.Sequence && card.CurrentCoordinates.Location == hero.CurrentCoordinates.Location));
            bool zeroLethal = theirs.Count == 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase &&
                              ours.Sum(EffectiveAttack) < oppLp && ours.Sum(EffectiveAttack) + zeroAtk >= oppLp;
            // No monster of theirs: nothing for the wipe to hit, Absolute Zero is only a 2500 body that eats one of their
            // traps (seen 2026-11-11: fused into an empty field and two sets, Bottomless Trap Hole banished it for free).
            // Fine when it makes lethal, when they have no set card (2500 of pressure), or when holding loses.
            if (theirs.Count == 0 && (await GetEnemySpellCardsAsync()).Any(card => !card.IsFaceup()) && !zeroLethal && !losing &&
                Rule(HeroFrogRules.FusionIsRemoval))
            {
                Note(context, "hold Miracle Fusion: it's removal (Absolute Zero wipes their monsters when it leaves) and they have no monster; " +
                              "into their set cards it would only eat a trap");
                Hold(HeroFrogCardId.MiracleFusion, MainAction.Activate, HeroFrogRules.FusionIsRemoval);
                return null;
            }
            if (ourBest >= 1800 && ourBest > theirBest && !zeroLethal && theirs.Count < 2 && Rule(HeroFrogRules.HoldMiracleFusion))
            {
                Note(context, $"hold Miracle Fusion: our {ourBest} already beats their board ({theirBest}); keep it for when we need it");
                Hold(HeroFrogCardId.MiracleFusion, MainAction.Activate, HeroFrogRules.HoldMiracleFusion);
                return null;
            }
            if (!Rule(HeroFrogRules.MiracleFusion))
                return null;
            PlanFusionMaterials(context.Header.CurrentTurn, new[] { hero, water }, "Miracle Fusion");
            Note(context, $"activate Miracle Fusion: Absolute Zero from the graveyard ({CardNames.Get(hero.ID)} + {CardNames.Get(water.ID)}), {zeroAtk} ATK; " +
                          "when it leaves the field it destroys all their monsters");
            MarkActivated(context, HeroFrogCardId.MiracleFusion);
            return fusion.MainActivateFromIndex();
        }

        // ------------------------------------------------------------------ Monarchs

        /// <summary>
        /// Before a Monarch: steal one of their monsters to tribute (Brain Control; Enemy Controller tributing a cheap
        /// monster, Treeborn best), or make fodder when we have none (Infernal Prodigy on an empty field, One for One).
        /// </summary>
        private async Task<IdleCmdResponse?> TryMonarchSetupAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand,
            List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            if (NormalSummonUsed(context.Header) || !hand.Any(card => IsId(card, TributeSummons)))
                return null;
            var steal = BestStealTarget(enemyMonsters, brainControl: false);
            bool mainOne = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1;
            // Stealing only pays when it clears a second card: the Monarch alone deals with a lone card.
            string? twoCards = await StealClearsTwoAsync(hand, enemyMonsters,
                mainOne && state.CanBattlePhase && context.Header.CurrentTurn > 1 ? botMonsters : null);
            // Only when the Monarch really comes out this turn (round-robin 25b: the hold stood on turns with no Monarch summon;
            // user, 2026-11-13: "plan this all from the start"): a tribute on the field, or fodder we can make this turn.
            bool monarchComing = CheapestTributes(botMonsters.Where(card => card.ID > 0)).Any() ||
                                 botMonsters.All(card => card.ID <= 0) && (TryGetSpecialSummon(state, HeroFrogCardId.EvilHeroInfernalProdigy, out _) ||
                                                                          TryGetActivable(state, HeroFrogCardId.OneForOne, out _) ||
                                                                          TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out _));
            if (twoCards == null && monarchComing && (TryGetActivable(state, HeroFrogCardId.BrainControl, out _) || TryGetActivable(state, HeroFrogCardId.EnemyController, out _)))
            {
                Note(context, "keep Brain Control / Enemy Controller: the Monarch deals with their card by itself");
                Hold(HeroFrogCardId.BrainControl, MainAction.Activate, HeroFrogRules.HoldSteal);
                Hold(HeroFrogCardId.EnemyController, MainAction.Activate, HeroFrogRules.HoldSteal);
            }

            // Brain Control (800 LP): their monster becomes the tribute, and the Monarch's trigger takes another card.
            if (twoCards != null && TryGetActivable(state, HeroFrogCardId.BrainControl, out var brain) && BestStealTarget(enemyMonsters, brainControl: true) is { ID: > 0 } bcTarget &&
                await CurrentField.GetBotLifePoints() > 800 + 1000 && Rule(HeroFrogRules.BrainControlTribute))
            {
                PlanTarget(context.Header.CurrentTurn, bcTarget, theirs: true);
                Note(context, $"activate Brain Control on {CardNames.Get(bcTarget.ID)}: tribute it for the Monarch in hand ({twoCards})");
                MarkActivated(context, HeroFrogCardId.BrainControl);
                return await MonarchRoutineAsync(context, brain.MainActivateFromIndex(), new RoutineStep(MainAction.Activate, HeroFrogCardId.BrainControl, "Brain Control"), hand, enemyMonsters, stolen: 1);
            }
            // Enemy Controller (take control): tribute our cheapest monster (Treeborn comes back), take theirs, tribute it.
            var cheap = CheapestTributes(botMonsters.Where(card => card.ID > 0)).FirstOrDefault();
            if (twoCards != null && TryGetActivable(state, HeroFrogCardId.EnemyController, out var controller) && steal.ID > 0 && cheap.ID > 0 &&
                TributeCost(cheap) <= ExpendableTributeCost && Rule(HeroFrogRules.EnemyControllerTribute))
            {
                _enemyControllerMode = 2;
                PlanTributes(context.Header.CurrentTurn, new[] { cheap }, "Enemy Controller");
                PlanTarget(context.Header.CurrentTurn, steal, theirs: true);
                Note(context, $"activate Enemy Controller: tribute {CardNames.Get(cheap.ID)}, take {CardNames.Get(steal.ID)} and tribute it for the Monarch ({twoCards})");
                MarkActivated(context, HeroFrogCardId.EnemyController);
                return await MonarchRoutineAsync(context, controller.MainActivateFromIndex(), new RoutineStep(MainAction.Activate, HeroFrogCardId.EnemyController, "Enemy Controller"), hand, enemyMonsters, stolen: 1);
            }
            if (botMonsters.Any(card => card.ID > 0))
                return null;
            // No monster at all: Infernal Prodigy (special summon with no monsters), or One for One (a Level 1 from the deck).
            if (TryGetSpecialSummon(state, HeroFrogCardId.EvilHeroInfernalProdigy, out var prodigy) && Rule(HeroFrogRules.ProdigyFodder))
            {
                Note(context, "special summon Infernal Prodigy: tribute fodder for the Monarch in hand");
                return await MonarchRoutineAsync(context, prodigy.SpecialSummon(), new RoutineStep(MainAction.SpSummon, HeroFrogCardId.EvilHeroInfernalProdigy, "special summon Infernal Prodigy"), hand, enemyMonsters, stolen: 0);
            }
            if (TryGetActivable(state, HeroFrogCardId.OneForOne, out var oneForOne))
            {
                var send = hand.Where(card => card.HasType(CardType.Monster) && !IsId(card, TributeSummons) || hand.Count(c => IsId(c, TributeSummons)) >= 2 && IsId(card, TributeSummons))
                    .OrderBy(DiscardRank).FirstOrDefault();
                int target = await InDeckAsync(HeroFrogCardId.TreebornFrog) > 0 && !(send.ID > 0 && IsId(send, HeroFrogCardId.TreebornFrog) && await InDeckAsync(HeroFrogCardId.TreebornFrog) < 1)
                    ? HeroFrogCardId.TreebornFrog
                    : HeroFrogCardId.Substitoad;
                if (send.ID > 0 && Rule(HeroFrogRules.OneForOneFodder))
                {
                    _discardPick = send.ID;
                    _deckSummonPick = target;
                    Note(context, $"activate One for One: send {CardNames.Get(send.ID)}, special summon {CardNames.Get(target)} as tribute fodder for the Monarch");
                    MarkActivated(context, HeroFrogCardId.OneForOne);
                    return await MonarchRoutineAsync(context, oneForOne.MainActivateFromIndex(), new RoutineStep(MainAction.Activate, HeroFrogCardId.OneForOne, "One for One"), hand, enemyMonsters, stolen: 0);
                }
            }
            // No other way: Swap Frog's own special summon (discarding a WATER) is the fodder (user, 2026-11-10: it's normally
            // not worth the discard, but a Monarch coming out is).
            if (TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out var swapFodder) && Rule(HeroFrogRules.SwapFodder))
            {
                Note(context, "special summon Swap Frog (discarding a WATER): the only tribute fodder for the Monarch in hand");
                return await MonarchRoutineAsync(context, swapFodder.SpecialSummon(), new RoutineStep(MainAction.SpSummon, HeroFrogCardId.SwapFrog, "special summon Swap Frog"), hand, enemyMonsters, stolen: 0);
            }
            return null;
        }

        /// <summary>
        /// The Monarch setup as a routine (DeckBotBrain.TurnPlan, user 2026-11-12): the setup move now, then "tribute summon a
        /// Monarch", whichever is in hand (Caius first, then Raiza, Thestalos, Des Frog). Caius / Raiza only when their field
        /// still has a card after the setup (a steal takes one of theirs): their trigger is mandatory and would hit ours.
        /// With their field empty the routine ends in Thestalos (or Des Frog); with neither in hand, there's no routine.
        /// </summary>
        private async Task<IdleCmdResponse?> MonarchRoutineAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse first, RoutineStep firstStep,
            List<DynamicCard> hand, List<DynamicCard> enemyMonsters, int stolen)
        {
            int theirCards = enemyMonsters.Count + (await GetEnemySpellCardsAsync()).Count - stolen;
            int[] allowed = theirCards > 0
                ? new[] { HeroFrogCardId.CaiusTheShadowMonarch, HeroFrogCardId.RaizaTheStormMonarch, HeroFrogCardId.ThestalosTheFirestormMonarch, HeroFrogCardId.DesFrog }
                : new[] { HeroFrogCardId.ThestalosTheFirestormMonarch, HeroFrogCardId.DesFrog };
            var inHand = allowed.Where(id => hand.Any(card => IsId(card, id))).ToArray();
            if (inHand.Length == 0)
            {
                Note(context, $"{firstStep.Label}: no Monarch in hand that fits (their field {(theirCards > 0 ? "has cards" : "is empty: not Caius / Raiza")}), so no routine follows");
                return first;
            }
            string label = theirCards > 0 ? "tribute summon a Monarch" : "tribute summon Thestalos / Des Frog (their field is empty: not Caius / Raiza)";
            // Rejected by the engine check (the Monarch can't follow): no setup either, the brain decides without it.
            return await ContinueRoutineAsync(context, "Monarch setup", $"tribute fodder, then a Monarch ({string.Join(" / ", inHand.Select(CardNames.Get))})", firstStep,
                new[] { new RoutineStep(MainAction.Summon, 0, label, AnyOf: inHand) }) ? first : null;
        }

        /// <summary>
        /// Why stealing a monster to tribute is worth a card: it clears a second card the Monarch alone wouldn't. With 2+
        /// cards on their field and Caius / Raiza in hand, the steal takes one and the Monarch's trigger the other. With
        /// only Thestalos / Des Frog in hand (no field answer), the steal is the only way their monster goes. Null when
        /// the Monarch would deal with their card by itself.
        /// </summary>
        private async Task<string?> StealClearsTwoAsync(List<DynamicCard> hand, List<DynamicCard> enemyMonsters, List<DynamicCard>? ourAttackers = null)
        {
            // Their monsters our attackers already beat in battle this turn don't need a card (seen 2026-11-11: Enemy
            // Controller took Dark Grepher for Raiza while our Gorz ran it over anyway; Raiza alone could have taken the set
            // card). Greedy: each attacker, weakest first, takes the weakest face-up monster it beats.
            var theirs = enemyMonsters.Where(card => !IsStolen(card)).ToList();
            // The Monarch attacks too (user, 2026-11-13, round-robin 24: "only if they have more than one card that can't just
            // be attacked over ... raiza and caius can target backrow too if they can attack over all monsters on the field"):
            // its 2400 counts among the attackers, so its trigger is free for the backrow.
            int monarchAtk = hand.Where(card => IsId(card, TributeSummons)).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            int beaten = ourAttackers == null ? 0
                : BeatenInBattle(theirs, ourAttackers.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack)
                    .Append(monarchAtk).Where(atk => atk > 0)).Count;
            int field = theirs.Count - beaten + (await GetEnemySpellCardsAsync()).Count;
            bool fieldMonarch = hand.Any(card => IsId(card, HeroFrogCardId.CaiusTheShadowMonarch) || IsId(card, HeroFrogCardId.RaizaTheStormMonarch));
            if (fieldMonarch && field >= 2)
                return $"they have {field} cards: the steal clears one and the Monarch the other";
            if (!fieldMonarch && field >= 1)
                return "our Monarch can't touch their field: the steal is the only way their monster goes";
            return null;
        }

        /// <summary>
        /// Their face-up monsters our attackers run over this turn (one attacker each; each attacker, weakest first, takes the
        /// weakest monster it beats). Face-down ones never count: what they are (Ryko, a Hamster) is unknown.
        /// </summary>
        private static List<DynamicCard> BeatenInBattle(List<DynamicCard> theirs, IEnumerable<int> attackerAtks)
        {
            var targets = theirs.Where(card => card.IsFaceup()).OrderBy(card => card.IsAttack() ? card.Attack : card.Defense).ToList();
            var beaten = new List<DynamicCard>();
            int next = 0;
            foreach (int atk in attackerAtks.OrderBy(atk => atk))
                if (next < targets.Count && atk > (targets[next].IsAttack() ? targets[next].Attack : targets[next].Defense))
                    beaten.Add(targets[next++]);
            return beaten;
        }

        /// <summary>Their face-up monster most worth taking (Brain Control: not Extra Deck monsters or tokens).</summary>
        private DynamicCard BestStealTarget(List<DynamicCard> enemyMonsters, bool brainControl) =>
            enemyMonsters.Where(card => card.ID > 0 && card.IsFaceup() &&
                                        (!brainControl || !(card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) ||
                                                            card.HasType(CardType.Xyz) || card.HasType(CardType.Token) || card.HasType(CardType.Ritual))))
                .OrderByDescending(OpponentThreat).FirstOrDefault();

        /// <summary>
        /// Tribute Summon a Monarch off our cheapest tribute (stolen > Treeborn > Dupe Frog...). Caius (banish; DARK +1000)
        /// or Raiza (top of the deck) need a card of theirs on the field: their trigger is mandatory and would hit ours.
        /// Thestalos (random discard) wants a hand; Des Frog is the body when nothing else fits.
        /// </summary>
        private async Task<IdleCmdResponse?> TryMonarchAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            if (NormalSummonUsed(context.Header))
                return null;
            var options = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => IsId(card, TributeSummons)).ToList();
            if (options.Count == 0)
                return null;
            var tribute = CheapestTributes(botMonsters.Where(card => card.ID > 0)).FirstOrDefault();
            if (tribute.ID <= 0)
                return null;
            // Monarchs come readily: Caius / Raiza remove a card on summon, so they're worth it even when 2400 ATK is
            // smaller than their monsters. Only a boss-level tribute (a fusion / synchro of ours) holds them.
            if (TributeCost(tribute) >= MonarchHoldTributeCost)
            {
                Note(context, $"hold the Monarch: our cheapest tribute is {CardNames.Get(tribute.ID)} ({EffectiveAttack(tribute)})");
                foreach (var monarch in options)
                    Hold(monarch.ID, MainAction.Summon, HeroFrogRules.MonarchSummon);
                return null;
            }
            int oppHand = await OppHandAsync();
            // Their best card on the field for Caius / Raiza (a DARK monster gives Caius 1000 damage too).
            var targets = enemyMonsters.Where(card => !IsStolen(card)).ToList();
            // Not a monster our attackers (the Monarch itself included) run over this turn anyway: the trigger takes a card
            // battle can't, a face-down monster or a set card (seen 2026-11-11: Raiza put Gearframe back on their deck,
            // then attacked their face-down Hamster, which brought Ryko; Raiza on the face-down, then Gearframe by battle).
            if ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1 &&
                Rule(HeroFrogRules.MonarchTakesWhatBattleCant))
            {
                var attackers = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                                                          !(card.CurrentCoordinates.Sequence == tribute.CurrentCoordinates.Sequence && card.IsCode(tribute.ID)))
                    .Select(EffectiveAttack).Append(2400);
                var beaten = BeatenInBattle(targets, attackers);
                if (beaten.Count > 0 && (targets.Count > beaten.Count || enemySpells.Count > 0))
                    targets = targets.Where(card => !beaten.Any(b => b.CurrentCoordinates.Sequence == card.CurrentCoordinates.Sequence)).ToList();
            }
            var bestMonster = targets.OrderByDescending(OpponentThreat).FirstOrDefault();
            var darkMonster = targets.Where(card => card.IsFaceup() && (card.Attribute & (int)CardAttribute.Dark) != 0)
                .OrderByDescending(OpponentThreat).FirstOrDefault();
            var anySpell = enemySpells.Where(card => !card.IsFaceup()).Concat(enemySpells).FirstOrDefault();
            DynamicCard target = targets.Count > 0 ? bestMonster : anySpell;
            bool hasTarget = targets.Count > 0 || enemySpells.Count > 0;
            if (hasTarget && darkMonster.ID > 0 && OpponentThreat(darkMonster) >= OpponentThreat(bestMonster) * CaiusDarkTargetShare)
                target = darkMonster;

            DynamicCard pick = default;
            string why = "";
            DynamicCard Find(int id) => options.FirstOrDefault(card => IsId(card, id));
            if (hasTarget && Find(HeroFrogCardId.CaiusTheShadowMonarch) is { ID: > 0 } caius)
                (pick, why) = (caius, $"banish {DescribePick(target)}" + ((target.Attribute & (int)CardAttribute.Dark) != 0 && target.IsFaceup() ? " (DARK: +1000 damage)" : ""));
            else if (hasTarget && Find(HeroFrogCardId.RaizaTheStormMonarch) is { ID: > 0 } raiza)
            {
                // Raiza only delays a main-deck card: they draw it again. Best on an Extra Deck monster or token (gone
                // for good); a Level 5+ main-deck monster comes straight back off a free tribute (a Raiza / Monarch loop
                // with their Treeborn ran a mirror to the turn limit), so it counts half.
                double RaizaValue(DynamicCard card) =>
                    OpponentThreat(card) * (card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) || card.HasType(CardType.Xyz) ||
                                            card.HasType(CardType.Token) ? RaizaGoneForGoodFactor
                        : card.IsFaceup() && card.Level >= 5 ? RaizaComesBackFactor : 1);
                if (targets.Count > 0)
                    target = targets.OrderByDescending(RaizaValue).First();
                (pick, why) = (raiza, $"put {DescribePick(target)} on top of their deck");
            }
            else if (oppHand > 0 && Find(HeroFrogCardId.ThestalosTheFirestormMonarch) is { ID: > 0 } thestalos)
                (pick, why) = (thestalos, $"discard one of their {oppHand} hand card(s) at random");
            else if (Find(HeroFrogCardId.DesFrog) is { ID: > 0 } des)
                (pick, why) = (des, "a 1900 body");
            else if (!hasTarget && Find(HeroFrogCardId.ThestalosTheFirestormMonarch) is { ID: > 0 } emptyThestalos)
                (pick, why) = (emptyThestalos, "a 2400 body (their hand is empty)");
            if (pick.ID <= 0)
            {
                if (!hasTarget)
                {
                    Note(context, "hold Caius / Raiza: they have no card on the field, and the trigger is mandatory (it would hit our own card)");
                    Hold(HeroFrogCardId.CaiusTheShadowMonarch, MainAction.Summon, HeroFrogRules.MonarchSummon);
                    Hold(HeroFrogCardId.RaizaTheStormMonarch, MainAction.Summon, HeroFrogRules.MonarchSummon);
                }
                return null;
            }
            if (!Rule(HeroFrogRules.MonarchSummon))
                return null;
            PlanTributes(context.Header.CurrentTurn, new[] { tribute }, CardNames.Get(pick.ID));
            if (IsId(pick, HeroFrogCardId.CaiusTheShadowMonarch) || IsId(pick, HeroFrogCardId.RaizaTheStormMonarch))
                PlanTarget(context.Header.CurrentTurn, target, theirs: true);
            Note(context, $"tribute summon {CardNames.Get(pick.ID)} (tributing {CardNames.Get(tribute.ID)}): {why}");
            return pick.NormalSummon();
        }

        /// <summary>
        /// Miracle Fusion in hand, a WATER material in the graveyard (not Treeborn), but no HERO in the graveyard or on the
        /// field: Normal Summon a HERO (Stratos > Ocean > Prodigy) so Miracle Fusion can banish it for Absolute Zero.
        /// </summary>
        private async Task<IdleCmdResponse?> TryHeroForFusionAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            if (NormalSummonUsed(context.Header) || !hand.Any(card => IsId(card, HeroFrogCardId.MiracleFusion)))
                return null;
            var grave = (await GetBotGraveyardCardsAsync()).Where(card => card.ID > 0 && card.HasType(CardType.Monster)).ToList();
            var ours = await GetBotMonsterCardsAsync();
            if (grave.Any(card => IsHero(card) && !card.HasType(CardType.Fusion)) ||
                ours.Any(card => card.IsFaceup() && IsHero(card) && !card.HasType(CardType.Fusion)) ||
                !grave.Any(card => IsWater(card) && !IsId(card, HeroFrogCardId.TreebornFrog)))
                return null;
            var pick = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => IsHero(card) && card.Level <= 4)
                .OrderBy(card => IsId(card, HeroFrogCardId.ElementalHeroStratos) ? 0 : IsId(card, HeroFrogCardId.ElementalHeroOcean) ? 1 : 2)
                .FirstOrDefault();
            if (pick.ID <= 0 || !Rule(HeroFrogRules.HeroForFusion))
                return null;
            Note(context, $"normal summon {CardNames.Get(pick.ID)}: Miracle Fusion banishes it with a WATER from the graveyard for Absolute Zero");
            return pick.NormalSummon();
        }

        /// <summary>
        /// Pressure (speed up the clock when they're open): Normal Summon an attacker into a field it can attack through
        /// this turn: their field empty, or every monster of theirs face-up in attack position and smaller. Stratos first
        /// (it searches / pops too), then the biggest.
        /// </summary>
        private async Task<IdleCmdResponse?> TryPressureSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> enemyMonsters)
        {
            if (NormalSummonUsed(context.Header) || (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase ||
                context.Header.CurrentTurn <= 1)
                return null;
            var candidates = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Level <= 4 && card.Attack >= 1500 && !card.HasType(CardType.Flip) &&
                               !IsId(card, HeroFrogCardId.ElementalHeroOcean)).ToList(); // Ocean: a weak card, Swap Frog's discard, not a beater
            if (candidates.Count == 0)
                return null;
            bool open(DynamicCard attacker) => enemyMonsters.All(card => card.IsFaceup() && card.IsAttack() && card.Attack < attacker.Attack);
            var pick = candidates.Where(open)
                .OrderBy(card => IsId(card, HeroFrogCardId.ElementalHeroStratos) ? 0 : 1).ThenByDescending(card => card.Attack).FirstOrDefault();
            if (pick.ID <= 0 || !Rule(HeroFrogRules.PressureSummon))
                return null;
            Note(context, $"normal summon {CardNames.Get(pick.ID)} ({pick.Attack}): " +
                          (enemyMonsters.Count == 0 ? "their field is empty, it attacks directly" : "it beats everything on their field") + " (pressure)");
            // Committed: summoned to hit this turn, so the battle that follows isn't dropped by a later rule.
            if ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1)
                CommitAttackThisTurn(context.Header.CurrentTurn, pick.ID, $"{CardNames.Get(pick.ID)} was summoned to hit this turn (pressure)");
            await Task.CompletedTask;
            return pick.NormalSummon();
        }

        // ------------------------------------------------------------------ Substitoad / the Dupe lock

        /// <summary>
        /// Substitoad (not once per turn, the deck's best card): tribute a monster, special summon a Frog from the deck.
        ///  - Treeborn first: until a Treeborn is in the graveyard (or on the field to be tributed), Swap Frog (its trigger
        ///    sends Treeborn from the deck), or Treeborn itself if no Swap Frog is left.
        ///  - Then, Main Phase 1 with a battle ahead: Des Frog (1900, it attacks); only with Treeborn set up (or Swap Frog in
        ///    hand to set it up). The Dupe lock waits for Main Phase 2.
        ///  - Main Phase 2 (or no battle): Dupe Frogs off the cheap monsters (Des Frog included once it has attacked),
        ///    ending by tributing Substitoad itself for the second Dupe Frog: they can't attack at all. Alone, Substitoad
        ///    becomes Des Frog once Treeborn is set up.
        /// </summary>
        /// <summary>
        /// The Dupe lock with Substitoad alone and nothing cheap to tribute: Swap Frog's special summon (discarding a WATER)
        /// is the first tribute (Swap Frog -> Dupe Frog, then Substitoad itself -> the second Dupe Frog). Normally Swap
        /// Frog's special summon isn't worth the discard; for the lock it is, when there's no other way (user, 2026-11-10).
        /// </summary>
        private async Task<IdleCmdResponse?> TrySwapFodderForLockAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters)
        {
            bool mainTwo = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2 || !state.CanBattlePhase || context.Header.CurrentTurn == 1;
            var ours = botMonsters.Where(card => card.ID > 0 && card.IsFaceup()).ToList();
            bool toadAlone = ours.Count > 0 && ours.All(card => IsId(card, HeroFrogCardId.Substitoad) || IsDupe(card)) &&
                             ours.Any(card => IsId(card, HeroFrogCardId.Substitoad));
            // With a Dupe Frog out already, Substitoad itself gives the second: no fodder needed.
            int dupesNeeded = 2;
            if (!mainTwo || !toadAlone || _dupesOnField >= 1 || !TryGetActivable(state, HeroFrogCardId.Substitoad, out _) ||
                await InDeckAsync(HeroFrogCardId.DupeFrog) < dupesNeeded || !TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out var swap) ||
                TryGetSpecialSummon(state, HeroFrogCardId.EvilHeroInfernalProdigy, out _) || !Rule(HeroFrogRules.SwapFodder))
                return null;
            Note(context, "special summon Swap Frog (discarding a WATER): fodder for Substitoad, for the Dupe lock (no other monster to tribute)");
            return swap.SpecialSummon();
        }

        /// <summary>
        /// Dupe Frog from the hand completes the lock (user, 2026-11-11: "make sure it's setting up the dupe lock"): Normal
        /// Summon it with a Dupe Frog already out (two Dupes: they can't attack at all), or with Substitoad out and a Dupe
        /// left in the deck (Substitoad then tributes itself for the second).
        /// </summary>
        private async Task<IdleCmdResponse?> TryDupeFromHandAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters)
        {
            if (NormalSummonUsed(context.Header) || !TryGetSummonable(state, HeroFrogCardId.DupeFrog, out var dupe))
                return null;
            bool toadOut = botMonsters.Any(card => card.IsFaceup() && IsId(card, HeroFrogCardId.Substitoad));
            bool completes = _dupesOnField >= 1 || toadOut && await InDeckAsync(HeroFrogCardId.DupeFrog) >= 1;
            if (!completes || !Rule(HeroFrogRules.DupeLockFromHand))
                return null;
            Note(context, _dupesOnField >= 1
                ? "normal summon Dupe Frog from the hand: the second Dupe Frog, they can't attack at all (the Dupe lock)"
                : "normal summon Dupe Frog from the hand: Substitoad then tributes itself for the second (the Dupe lock)");
            return dupe.NormalSummon();
        }

        /// <summary>The turn the Dupe lock was put ahead of everything else (Substitoad then goes straight for the Dupes).</summary>
        private int _lockFirstTurn = -1;

        /// <summary>
        /// Losing on board, the Dupe lock comes before a Monarch (seen 2026-11-11: at 300 LP against Gorz, Goyo and Armed Wing
        /// the bot summoned Thestalos, which died next turn and so did we; Substitoad into two Dupe Frogs stops every attack).
        /// Normal Summon Substitoad when it can reach two Dupe Frogs this turn (a monster to tribute and two Dupes in the deck,
        /// or one Dupe out and one in the deck).
        /// </summary>
        private async Task<IdleCmdResponse?> TryDupeLockFirstAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters)
        {
            if (NormalSummonUsed(context.Header) || !TryGetSummonable(state, HeroFrogCardId.Substitoad, out var toad))
                return null;
            int dupesInDeck = await InDeckAsync(HeroFrogCardId.DupeFrog);
            bool fodder = botMonsters.Any(card => card.ID > 0 && !IsDupe(card) || card.ID <= 0 && card.CurrentCoordinates.Location != 0 && !card.IsFaceup());
            bool reachesLock = fodder && dupesInDeck >= 2 || _dupesOnField >= 1 && dupesInDeck >= 1;
            if (!reachesLock)
                return null;
            int overMonarch = enemyMonsters.Count(card => card.IsFaceup() && card.IsAttack() && card.Attack > 2400);
            bool losing = await WeLoseNextTurnAsync();
            if (!losing && overMonarch < 2)
                return null;
            foreach (var monarch in (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => IsId(card, TributeSummons)))
                Hold(monarch.ID, MainAction.Summon, HeroFrogRules.DupeLockFirst);
            if (!Rule(HeroFrogRules.DupeLockFirst))
                return null;
            _lockFirstTurn = context.Header.CurrentTurn;
            Note(context, $"normal summon Substitoad for the Dupe lock first: {(losing ? "we lose next turn otherwise" : $"{overMonarch} of their attackers run over a Monarch")}; " +
                          "two Dupe Frogs stop every attack");
            return toad.NormalSummon();
        }

        /// <summary>The turn Swap Frog was Normal Summoned to put a WATER in the graveyard for Miracle Fusion (its send picks that, not Treeborn).</summary>
        private int _swapSendForFusionTurn = -1;

        /// <summary>
        /// The set the frog deck holds back: Spiritual Water Art - Aoi without a WATER monster of ours on the field
        /// ("should never be set unless you have a water monster on field"). (Enemy Controller may be set with a plan to use it: the Treeborn
        /// blocking penalty, TreebornBlockedValue, charges a set left in place.)
        /// </summary>
        protected override async Task DeckHoldsAsync(DecisionContext<IdleCmdData> context)
        {
            var setable = context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>();
            if (setable.Any(card => IsId(card, HeroFrogCardId.SpiritualWaterArtAoi)) &&
                !(await GetBotMonsterCardsAsync()).Any(card => (card.Attribute & (int)CardAttribute.Water) != 0) &&
                Rule(HeroFrogRules.AoiSetNeedsWater))
                Hold(HeroFrogCardId.SpiritualWaterArtAoi, MainAction.SetSpell, HeroFrogRules.AoiSetNeedsWater);
        }

        /// <summary>The turn the Swap Frog route to the Dupe lock started (its steps follow one another).</summary>
        private int _swapLockTurn = -1;

        /// <summary>
        /// The Dupe lock from the hand through Swap Frog, when the Dupes are in hand rather than the deck (seen 2026-11-11: two
        /// Dupe Frogs, Substitoad and Swap Frog in hand, the deck out of Dupes; Substitoad came out with nothing to make, and
        /// an OTK followed). Special summon Swap Frog (discarding a WATER that isn't a Dupe), Normal Summon a Dupe Frog, Swap
        /// Frog returns itself to the hand (that grants one more summon of a small WATER Aqua), the second Dupe Frog.
        /// Wanted when we're losing on board or have no Monarch to play instead.
        /// </summary>
        private async Task<IdleCmdResponse?> TryDupeLockViaSwapAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand,
            List<DynamicCard> botMonsters, List<DynamicCard> enemyMonsters)
        {
            int turn = context.Header.CurrentTurn;
            int dupesInHand = hand.Count(IsDupe);
            if (dupesInHand == 0 || dupesInHand + _dupesOnField < 2)
                return null;
            var swapOnField = botMonsters.FirstOrDefault(card => card.IsFaceup() && IsId(card, HeroFrogCardId.SwapFrog));
            var swapEffect = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => IsId(card, HeroFrogCardId.SwapFrog) && card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);

            // The route under way: its next step.
            if (_swapLockTurn == turn)
            {
                // The extra summon Swap Frog's bounce granted: the second Dupe Frog.
                if (_dupesOnField >= 1 && swapOnField.ID <= 0 && TryGetSummonable(state, HeroFrogCardId.DupeFrog, out var second))
                {
                    Note(context, "normal summon the second Dupe Frog (Swap Frog's extra summon): the Dupe lock, they can't attack at all");
                    return second.NormalSummon();
                }
                if (_dupesOnField >= 1 && swapEffect.ID > 0)
                {
                    PlanTarget(turn, swapEffect, theirs: false);
                    Note(context, "Swap Frog returns itself to the hand: one more summon of a small WATER Aqua, the second Dupe Frog");
                    MarkActivated(context, HeroFrogCardId.SwapFrog);
                    return swapEffect.MainActivateFromIndex();
                }
                if (_dupesOnField == 0 && !NormalSummonUsed(context.Header) && swapOnField.ID > 0 && TryGetSummonable(state, HeroFrogCardId.DupeFrog, out var first))
                {
                    Note(context, "normal summon Dupe Frog: the first half of the Dupe lock (Swap Frog's bounce brings the second)");
                    return first.NormalSummon();
                }
                return null;
            }

            // Start it: the Normal Summon free, Swap Frog coming out (or out already with its effect unused), and the Dupes not
            // reachable another way (Substitoad with Dupes in the deck goes through TryDupeLockFirstAsync / TrySubstitoadAsync).
            if (NormalSummonUsed(context.Header) || await InDeckAsync(HeroFrogCardId.DupeFrog) >= 2)
                return null;
            var discard = hand.Where(card => card.HasType(CardType.Monster) && IsWater(card) && !IsDupe(card) && !IsId(card, HeroFrogCardId.SwapFrog) &&
                                             DiscardRank(card) <= 4)
                .OrderBy(DiscardRank).FirstOrDefault();
            bool swapFromHand = swapOnField.ID <= 0 && TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out _) && discard.ID > 0;
            if (!swapFromHand && !(swapOnField.ID > 0 && swapEffect.ID > 0))
                return null;
            bool losing = await WeLoseNextTurnAsync() || enemyMonsters.Count(card => card.IsFaceup() && card.IsAttack() && card.Attack > 2400) >= 2;
            bool monarchPlay = hand.Any(card => IsId(card, TributeSummons));
            if (!losing && monarchPlay)
                return null;
            if (!Rule(HeroFrogRules.DupeLockViaSwap))
                return null;
            _swapLockTurn = turn;
            if (swapFromHand && TryGetSpecialSummon(state, HeroFrogCardId.SwapFrog, out var swap))
            {
                _discardPick = discard.ID;
                Note(context, $"special summon Swap Frog (discarding {CardNames.Get(discard.ID)}): the route to the Dupe lock with both Dupe Frogs in hand " +
                              "(Dupe, then Swap Frog's bounce for one more summon, the second Dupe)");
                return swap.SpecialSummon();
            }
            var dupe = (state.SummonableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(IsDupe);
            if (dupe.ID <= 0)
                return null;
            Note(context, "normal summon Dupe Frog: the first half of the Dupe lock (Swap Frog's bounce brings the second)");
            return dupe.NormalSummon();
        }

        /// <summary>The Monarch takes the Normal Summon this turn: any other summon or set breaks the plan (a held move).</summary>
        private void HoldOtherSummons(IdleCmdData state)
        {
            foreach (var card in (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => !IsId(card, TributeSummons)))
                Hold(card.ID, MainAction.Summon, HeroFrogRules.MonarchTakesSummon);
            foreach (var card in (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Where(card => !IsId(card, TributeSummons)))
                Hold(card.ID, MainAction.SetMonster, HeroFrogRules.MonarchTakesSummon);
        }

        private async Task<IdleCmdResponse?> TrySubstitoadAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters)
        {
            if (!TryGetActivable(state, HeroFrogCardId.Substitoad, out var toad) || ActivationsThisTurn(context, HeroFrogCardId.Substitoad) >= 8)
                return null;
            int dupesInDeck = await InDeckAsync(HeroFrogCardId.DupeFrog);
            int swapsInDeck = await InDeckAsync(HeroFrogCardId.SwapFrog);
            int treebornInDeck = await InDeckAsync(HeroFrogCardId.TreebornFrog);
            int desInDeck = await InDeckAsync(HeroFrogCardId.DesFrog);
            var ours = botMonsters.Where(card => card.ID > 0 && card.IsFaceup()).ToList();
            var toads = ours.Where(card => IsId(card, HeroFrogCardId.Substitoad)).ToList();
            bool alone = ours.Count == toads.Count;
            bool IsDes(DynamicCard card) => IsId(card, HeroFrogCardId.DesFrog) && !IsDupe(card);
            // A battle ahead only when one is worth entering: with nothing that can attack (Treeborn, Substitoad), the lock
            // goes up in Main Phase 1 rather than waiting for a Battle Phase that does nothing (user, 2026-11-11).
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1 &&
                               await BattleWorthEnteringAsync() == null;
            // Tribute: the cheapest monster that isn't a Dupe Frog (it stays for the lock) or the last Substitoad (the
            // engine); Des Frog once the battle is over.
            var others = CheapestTributes(ours.Where(card => !IsDupe(card) && !IsId(card, HeroFrogCardId.Substitoad) &&
                                                             (TributeCost(card) < 3 || IsDes(card) && !battleAhead))).ToList();
            if (toads.Count > 1)
                others.AddRange(toads.Skip(1));
            bool dumpReady = treebornInDeck > 0 && !_treebornInGrave && swapsInDeck > 0;
            bool desOnField = ours.Any(IsDes);
            bool worthShielding = ours.Any(card => !IsDupe(card) && !IsId(card, HeroFrogCardId.Substitoad) && EffectiveAttack(card) >= 1800);
            // Treeborn live is the engine's first priority: in the graveyard (or on the field, to be tributed there), or
            // another way to get it there this turn (Swap Frog in hand with a cheap WATER to discard: its trigger sends it).
            var hand = await GetBotHandCardsAsync();
            bool treebornSetUp = _treebornInGrave || _treebornOnField;
            bool anotherWay = treebornInDeck > 0 && hand.Any(card => IsId(card, HeroFrogCardId.SwapFrog)) &&
                              hand.Count(card => IsWater(card) && card.HasType(CardType.Monster) && DiscardRank(card) <= 4) >= 2;
            bool desAllowed = desInDeck > 0 && !desOnField && (treebornSetUp || anotherWay);
            DynamicCard tribute = default;
            int summon = 0;
            string why = "";
            bool lockFirst = _lockFirstTurn == context.Header.CurrentTurn && dupesInDeck > 0;
            if (lockFirst && others.Count > 0)
                (tribute, summon, why) = (others[0], HeroFrogCardId.DupeFrog, "the Dupe lock first (we're losing on board): Dupe Frog");
            else if (lockFirst && toads.Count > 0 && _dupesOnField >= 1)
                (tribute, summon, why) = (toads[0], HeroFrogCardId.DupeFrog, "the Dupe lock first: Substitoad itself for the second Dupe Frog (they can't attack at all)");
            else if (!treebornSetUp && dumpReady && (others.Count > 0 || alone))
                (tribute, summon, why) = (others.Count > 0 ? others[0] : toads[0], HeroFrogCardId.SwapFrog,
                    "Treeborn first: Swap Frog's trigger sends Treeborn Frog to the graveyard (it comes back every turn)");
            else if (!treebornSetUp && treebornInDeck > 0 && swapsInDeck == 0 && others.Count > 0)
                (tribute, summon, why) = (others[0], HeroFrogCardId.TreebornFrog, "Treeborn first (no Swap Frog left): Treeborn Frog from the deck");
            else if (battleAhead)
            {
                if (desAllowed && (others.Count > 0 || alone))
                    (tribute, summon, why) = (others.Count > 0 ? others[0] : toads[0], HeroFrogCardId.DesFrog,
                        "Treeborn is set up: Des Frog (1900) attacks this turn; the Dupe lock waits for Main Phase 2");
            }
            else if (alone)
            {
                if (desAllowed)
                    (tribute, summon, why) = (toads[0], HeroFrogCardId.DesFrog, "alone, Treeborn set up: Substitoad becomes Des Frog (1900)");
            }
            else if (_dupesOnField >= 2 && desAllowed && others.Count > 0 && Rule(HeroFrogRules.LockThenClock))
                // The lock is up: spare fodder (Treeborn comes back every Standby Phase) becomes a clock behind it (seen
                // 2026-11-11: two Dupes, Treeborn and Substitoad sat for eight turns doing nothing).
                (tribute, summon, why) = (others[0], HeroFrogCardId.DesFrog, "the Dupe lock is up: spare fodder becomes Des Frog (1900), a clock behind the lock");
            else if (dupesInDeck > 0 && others.Count > 0)
                (tribute, summon, why) = (others[0], HeroFrogCardId.DupeFrog, "Dupe Frog for the lock");
            else if (dupesInDeck > 0 && toads.Count > 0 && (_dupesOnField >= 1 || worthShielding))
                (tribute, summon, why) = (toads[0], HeroFrogCardId.DupeFrog, _dupesOnField >= 1
                    ? "the last step: Substitoad itself for the second Dupe Frog (they can't attack at all)"
                    : "Substitoad itself for Dupe Frog: they can only attack the Dupe, not our big monster");
            if (summon == 0 || !Rule(HeroFrogRules.SubstitoadEngine))
                return null;
            PlanTributes(context.Header.CurrentTurn, new[] { tribute }, "Substitoad");
            _deckSummonPick = summon;
            Note(context, $"activate Substitoad: tribute {CardNames.Get(tribute.ID)}, special summon {CardNames.Get(summon)}: {why}");
            MarkActivated(context, HeroFrogCardId.Substitoad);
            return toad.MainActivateFromIndex();
        }

        /// <summary>
        /// Normal Summon Substitoad (the engine: Frogs battle-proof, and the loop to two Dupe Frogs) while a Dupe or Swap Frog
        /// is still in the deck, unless Stratos can come out instead (it searches / pops first; Substitoad next turn).
        /// </summary>
        private async Task<IdleCmdResponse?> TrySummonSubstitoadAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (NormalSummonUsed(context.Header) || !TryGetSummonable(state, HeroFrogCardId.Substitoad, out var toad) ||
                TryGetSummonable(state, HeroFrogCardId.ElementalHeroStratos, out _))
                return null;
            if (await InDeckAsync(HeroFrogCardId.DupeFrog) + await InDeckAsync(HeroFrogCardId.SwapFrog) == 0 || !Rule(HeroFrogRules.SubstitoadSummon))
                return null;
            Note(context, "normal summon Substitoad: the engine (Frogs can't be destroyed by battle; tribute into Frogs from the deck, ending on two Dupe Frogs)");
            return toad.NormalSummon();
        }

        // ------------------------------------------------------------------ Junk Synchron

        /// <summary>Junk Synchron revives a Level 2 (negated) for a Level 5 synchro that answers a threat, or when we have no attacker.</summary>
        private async Task<IdleCmdResponse?> TryJunkSynchronAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters)
        {
            if (NormalSummonUsed(context.Header) || !TryGetSummonable(state, HeroFrogCardId.JunkSynchron, out var junk))
                return null;
            var revive = (await GetBotGraveyardCardsAsync()).Where(card => card.ID > 0 && card.HasType(CardType.Monster) && card.Level == 2 && !card.HasType(CardType.Tuner))
                .OrderBy(card => IsId(card, HeroFrogCardId.DupeFrog) ? 1 : 0).FirstOrDefault();
            if (revive.ID <= 0)
                return null;
            var ourAtk = botMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList();
            var option = await BestSynchroForLevelAsync(3 + revive.Level, enemyMonsters, ourAtk);
            if (option is not { } plan)
                return null;
            bool noAttacker = !botMonsters.Any(card => card.IsFaceup() && card.IsAttack() && EffectiveAttack(card) >= 1500);
            if (plan.Answer == null && !noAttacker)
                return null;
            if (!Rule(HeroFrogRules.JunkSynchron))
                return null;
            PlanTarget(context.Header.CurrentTurn, revive, theirs: false);
            _junkSynchroTarget = (context.Header.CurrentTurn, plan.Synchro.Id);
            Note(context, $"normal summon Junk Synchron: revive {CardNames.Get(revive.ID)} for {CardNames.Get(plan.Synchro.Id)}" +
                          (plan.Answer != null ? $" ({plan.Answer})" : " (we have no attacker)"));
            return junk.NormalSummon();
        }

        private (int Turn, int Id)? _junkSynchroTarget;

        private IdleCmdResponse? TryPlannedSynchro(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (_junkSynchroTarget is not { } plan || plan.Turn != context.Header.CurrentTurn || !TryGetSpecialSummon(state, plan.Id, out var synchro))
                return null;
            _junkSynchroTarget = null;
            if (!Rule(HeroFrogRules.JunkSynchro))
                return null;
            Note(context, $"synchro summon {CardNames.Get(plan.Id)} with Junk Synchron");
            return synchro.SpecialSummon();
        }

        // ------------------------------------------------------------------ Raigeki Break

        private async Task<IdleCmdResponse?> TryRaigekiMainAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand,
            List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            if (!TryGetActivable(state, HeroFrogCardId.RaigekiBreak, out var raigeki) || hand.Count == 0)
                return null;
            var discard = hand.OrderBy(DiscardRank).First();
            if (DiscardRank(discard) > 3)
                return null;
            DynamicCard target = default;
            foreach (var card in enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0).OrderByDescending(OpponentThreat))
                if (await IsBigThreatAsync(card)) { target = card; break; }
            if (target.ID <= 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && enemySpells.Any(card => !card.IsFaceup()))
                target = enemySpells.First(card => !card.IsFaceup());
            if (target.Equals(default(DynamicCard)) || !Rule(HeroFrogRules.RaigekiBreak))
                return null;
            _discardPick = discard.ID;
            PlanTarget(context.Header.CurrentTurn, target, theirs: true);
            Note(context, $"activate Raigeki Break: discard {CardNames.Get(discard.ID)}, destroy {DescribePick(target)}");
            MarkActivated(context, HeroFrogCardId.RaigekiBreak);
            return raigeki.MainActivateFromIndex();
        }

        // ------------------------------------------------------------------ costs and values

        // What we lose least by discarding / sending from the hand (Treeborn works from the graveyard).
        private int DiscardRank(DynamicCard card) =>
            IsId(card, HeroFrogCardId.TreebornFrog) ? 0
            : IsId(card, HeroFrogCardId.SwapFrog) ? 1
            : IsId(card, HeroFrogCardId.DesFrog) ? 2
            : IsId(card, HeroFrogCardId.DupeFrog) ? 2
            : IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy) ? 3
            : IsId(card, HeroFrogCardId.Substitoad) ? 7 // the deck's best card
            : IsId(card, HeroFrogCardId.ElementalHeroOcean) ? 1 // a weak card, in for being a WATER HERO: Swap Frog's discard (user, 2026-11-10)
            : IsId(card, HeroFrogCardId.JunkSynchron) ? 4
            : IsId(card, Monarchs) ? 6
            : IsId(card, HeroFrogCardId.BattleFader) ? 6
            : IsId(card, HeroFrogCardId.GorzTheEmissaryOfDarkness) ? 8
            : card.HasType(CardType.Spell) || card.HasType(CardType.Trap) ? 5
            : 5;

        /// <summary>
        /// Tribute cost: a stolen monster 0; Absolute Zero when leaving the field clears their board (2+ monsters or a big
        /// one) 0.5; Treeborn ~free while our zone is clear (it comes back); Dupe Frog cheap (it searches a Frog) unless
        /// it's half of the lock; then the small Frogs, the engine (Substitoad), the HEROs.
        /// </summary>
        protected override double TributeCost(DynamicCard card)
        {
            // Treeborn is always the cheapest tribute: it comes back (user rule, 2026-11-09). Only a banish is worth
            // protecting it from.
            if (IsId(card, HeroFrogCardId.TreebornFrog))
                return -1;
            if (IsStolen(card))
                return 0;
            if (IsId(card, HeroFrogCardId.ElementalHeroAbsoluteZero))
                return _enemyMonsters >= 2 ? TributeAbsoluteZeroLive : TributeAbsoluteZero;
            if (IsDupe(card))
                return _dupesOnField >= 2 ? TributeDupeLock : TributeDupe; // the lock piece: above the small Frogs (its search is a bonus)
            if (card.HasType(CardType.Token)) return TributeToken;
            if (IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy)) return TributeInfernalProdigy;
            if (IsId(card, HeroFrogCardId.BattleFader)) return TributeBattleFader;
            if (IsId(card, HeroFrogCardId.SwapFrog)) return TributeSwapFrog;
            if (IsId(card, HeroFrogCardId.JunkSynchron)) return TributeJunkSynchron;
            if (IsId(card, HeroFrogCardId.Substitoad)) return TributeSubstitoad;
            if (IsId(card, HeroFrogCardId.ElementalHeroOcean) || IsId(card, HeroFrogCardId.ElementalHeroStratos)) return TributeOceanStratos;
            if (IsId(card, HeroFrogCardId.DesFrog)) return TributeDesFrog; // expendable once it has attacked (or when it can't attack)
            return base.TributeCost(card);
        }

        // Substitoad is the engine, never expendable (the shared default counts any small Level 4 or lower monster as expendable:
        // seen 2026-11-11, it was sent into a face-down Ryko as the "cheap" attacker and the flip killed it).
        protected override bool IsExpendable(DynamicCard card) =>
            !IsId(card, HeroFrogCardId.Substitoad) &&
            (base.IsExpendable(card) ||
             IsId(card, HeroFrogCardId.TreebornFrog) || IsDupe(card) || IsId(card, HeroFrogCardId.EvilHeroInfernalProdigy) ||
             IsId(card, HeroFrogCardId.BattleFader) || IsId(card, HeroFrogCardId.SwapFrog));

        // Stratos first (it searches Ocean or pops a Spell/Trap), then Substitoad (the engine), Ocean, Junk Synchron.
        protected override int SafeSummonPriority(DynamicCard card, List<DynamicCard> hand) =>
            IsId(card, HeroFrogCardId.ElementalHeroStratos) ? 0
            : IsId(card, HeroFrogCardId.Substitoad) ? 1
            : IsId(card, HeroFrogCardId.ElementalHeroOcean) ? 9 // a very low priority summon (user, 2026-11-10)
            : 3;

        protected override string? BattleDeathPayoff(DynamicCard card) =>
            IsId(card, HeroFrogCardId.ElementalHeroAbsoluteZero) ? "Absolute Zero leaving the field destroys all their monsters"
            : base.BattleDeathPayoff(card);

        /// <summary>
        /// Absolute Zero: every monster of theirs destroyed. (Not Dupe Frog: its search is a nice to have, and every Frog it
        /// could get is worse than Dupe on the field, so it's never worth crashing; user, 2026-11-12. Not Treeborn Frog:
        /// its graveyard effect isn't triggered by being destroyed, and coming back gains nothing; user, 2026-11-12. Payoffs are
        /// only effects that trigger when the card is destroyed.)
        /// </summary>
        protected override double BattleDeathGain(DynamicCard card, DynamicCard killer, List<DynamicCard> theirMonsters) =>
            IsId(card, HeroFrogCardId.ElementalHeroAbsoluteZero) ? theirMonsters.Sum(TargetThreat)
            : base.BattleDeathGain(card, killer, theirMonsters);

        protected override void OnFieldRefreshed(FieldInfo field)
        {
            var grave = field.OwnSide.Grave ?? Array.Empty<int>();
            _treebornInGrave = grave.Contains(HeroFrogCardId.TreebornFrog);
            var zones = field.OwnSide.MonsterZones ?? Array.Empty<CardArray>();
            _treebornOnField = zones.Any(zone => zone.Cards?.Contains(HeroFrogCardId.TreebornFrog) == true);
            _ourSpellTraps = (field.OwnSide.SpellZones ?? Array.Empty<CardArray>()).Count(zone => zone.Cards?.Any(id => id != 0) == true);
            _enemyMonsters = (field.EnemySide.MonsterZones ?? Array.Empty<CardArray>()).Count(zone => zone.Cards?.Length > 0);
        }

        /// <summary>Copies of a card still in our deck: the deck's count minus those seen in hand, graveyard, banished, on the field.</summary>
        private async Task<int> InDeckAsync(int id)
        {
            if (!Copies.TryGetValue(id, out int total))
                return 0;
            int seen = 0;
            foreach (var location in new[] { CardLocation.Hand, CardLocation.Grave, CardLocation.Removed, CardLocation.MonsterZone })
            {
                var cards = await CurrentField.GetCardsInLocation(location);
                seen += cards.Count(card => card.Owner == 0 && (id == HeroFrogCardId.DupeFrog ? IsDupe(card) : IsId(card, id) && !(id == HeroFrogCardId.DesFrog && IsDupe(card))));
            }
            if (id == HeroFrogCardId.DupeFrog)
                _dupesOnField = (await GetBotMonsterCardsAsync()).Count(card => card.IsFaceup() && IsDupe(card));
            return Math.Max(0, total - seen);
        }

        private async Task<int> OppHandAsync() => (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;

        private int ActivationsThisTurn(DecisionContext<IdleCmdData> context, int cardId) =>
            _activationTurn == context.Header.CurrentTurn && _activationsThisTurn.TryGetValue(cardId, out int used) ? used : 0;

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

        // ------------------------------------------------------------------ chains

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
            var phase = (DuelPhase)context.Header.CurrentPhase;
            int Index(int id) => Array.FindIndex(options, card => IsId(card, id) && card.CurrentCoordinates.Controller == 0);

            if (Index(HeroFrogCardId.SpiritualWaterArtAoi) is int aoi and >= 0 && Rule(HeroFrogRules.AoiStrip))
                return aoi;

            bool swapHasTarget = false;
            if (Index(HeroFrogCardId.SwapFrog) >= 0)
            {
                foreach (int target in CardRules.SwapFrogDeckTargets)
                    if (await InDeckAsync(target) > 0)
                    {
                        swapHasTarget = true;
                        break;
                    }
            }
            // Our own optional triggers: Swap Frog's dump, Dupe Frog's search, Treeborn's revive, Stratos, Junk Synchron.
            int trigger = Array.FindIndex(options, card => IsId(card, OwnTriggers) && card.CurrentCoordinates.Controller == 0 &&
                                                           (!IsId(card, HeroFrogCardId.SwapFrog) || swapHasTarget) &&
                                                           card.CurrentCoordinates.Location != (byte)CardLocation.Hand &&
                                                           card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone);
            if (trigger >= 0)
            {
                Note(context, $"use {CardNames.Get(options[trigger].ID)}'s effect");
                return trigger;
            }

            // A set card of ours their effect targets (Lyla, MST): it's gone anyway, so use it now if it does anything at all.
            if (await TryDoomedSetAsync(context, options) is int doomed)
                return doomed;

            // Ocean (our Standby Phase): Absolute Zero back to the Extra Deck to wipe their monsters, or a HERO back.
            if (Index(HeroFrogCardId.ElementalHeroOcean) is int ocean and >= 0 && botTurn && await OceanTargetAsync(context) is DynamicCard oceanTarget)
            {
                PlanTarget(context.Header.CurrentTurn, oceanTarget, theirs: false);
                return ocean;
            }

            // Our Standby Phase: Enemy Controller tributing Treeborn (it comes back this same Standby Phase), take their
            // monster, and tribute it for a Monarch in Main Phase 1.
            if (botTurn && phase == DuelPhase.Standby && Index(HeroFrogCardId.EnemyController) is int ec and >= 0 &&
                await StandbyStealAsync(context) is int steal)
                return steal;

            if (!botTurn)
            {
                // Battle Fader on a direct attack (Gorz instead when it can come: no cards of ours and not lethal).
                if (Index(HeroFrogCardId.BattleFader) is int fader and >= 0 && await FaderNowAsync(context))
                    return fader;
                bool clearForTreeborn = phase == DuelPhase.End && await TreatBackrowAsDoomedAsync(false, phase);
                if (clearForTreeborn && Index(HeroFrogCardId.CompulsoryEvacuationDevice) is int compulsory and >= 0)
                {
                    var best = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 || !card.IsFaceup())
                        .OrderByDescending(card => card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) || card.HasType(CardType.Token) ? 1 : 0)
                        .ThenByDescending(OpponentThreat).FirstOrDefault();
                    if (!best.Equals(default(DynamicCard)) && (best.ID > 0 || !best.IsFaceup()))
                    {
                        PlanTarget(context.Header.CurrentTurn, best, theirs: true);
                        context.Bot.LogNote($"mandatory action: activate Compulsory Evacuation Device on {DescribePick(best)} (clear for Treeborn)");
                        return compulsory;
                    }
                }
                // Raigeki Break: on an attack that hurts, or at their End Phase so our zone is clear for Treeborn.
                if (Index(HeroFrogCardId.RaigekiBreak) is int raigeki and >= 0 && await RaigekiNowAsync(context) is string raigekiWhy &&
                    Rule(HeroFrogRules.RaigekiBreak))
                {
                    Note(context, $"activate Raigeki Break: {raigekiWhy}");
                    return raigeki;
                }
                if (clearForTreeborn && Array.FindIndex(options, IsOurSetCard) is int set and >= 0)
                {
                    context.Bot.LogNote($"mandatory action: activate {CardNames.Get(options[set].ID)} (clear for Treeborn)");
                    return set;
                }
            }

            // A set Jar of Greed (or other bait) their effect is about to remove: use it now (shared bait rule; user, 2026-11-12:
            // a Jar of Greed lost to their removal "should NEVER happen").
            if (options.Any(IsOurSetCard) &&
                await TryChainBaitAsync(context) is int bait)
                return bait;
            if (!options.Any(card => IsId(card, ChainCandidates)))
                return -1;
            var response = await TryChainResponsesAsync(context, options, botTurn, ct);
            return response ?? -1;
        }

        protected override async Task<bool> TreatBackrowAsDoomedAsync(bool ourTurn, DuelPhase phase)
        {
            if (phase != (ourTurn ? DuelPhase.Draw : DuelPhase.End))
                return false;
            bool treebornInGrave = (await GetBotGraveyardCardsAsync()).Any(card => IsId(card, HeroFrogCardId.TreebornFrog));
            return HeroFrogRules.TreebornDoomsBackrow(ourTurn, phase, treebornInGrave,
                !ourTurn && treebornInGrave && (await GetBotHandCardsAsync()).Any(card => IsId(card, Monarchs)));
        }

        internal override Func<bool, DuelPhase, DuelSim.Engine.SideSummary, bool> SearchBackrowDoomRule =>
            (ourTurn, phase, own) => HeroFrogRules.TreebornDoomsBackrow(ourTurn, phase,
                own.GraveCodes.Any(code => Search.SearchHost.Is(code, HeroFrogCardId.TreebornFrog)),
                !ourTurn && own.HandCodes.Any(code => Monarchs.Any(monarch => Search.SearchHost.Is(code, monarch))));

        /// <summary>Ocean's Standby return: Absolute Zero (wipes their 2+ monsters or a big threat), else Stratos / a HERO from the graveyard.</summary>
        private async Task<DynamicCard?> OceanTargetAsync<T>(DecisionContext<T> context)
        {
            var ours = await GetBotMonsterCardsAsync();
            var enemy = await GetEnemyMonsterCardsAsync();
            var zero = ours.FirstOrDefault(card => card.IsFaceup() && IsId(card, HeroFrogCardId.ElementalHeroAbsoluteZero));
            bool bigThreat = false;
            foreach (var card in enemy.Where(card => card.IsFaceup() && card.ID > 0))
                if (await IsBigThreatAsync(card)) { bigThreat = true; break; }
            if (zero.ID > 0 && (enemy.Count >= 2 || bigThreat) && Rule(HeroFrogRules.OceanReturnsZero))
            {
                Note(context, $"Ocean returns Absolute Zero to the Extra Deck: it leaves the field and destroys their {enemy.Count} monster(s) (Miracle Fusion makes it again)");
                return zero;
            }
            var hero = (await GetBotGraveyardCardsAsync()).Where(card => IsHero(card) && !card.HasType(CardType.Fusion))
                .OrderBy(card => IsId(card, HeroFrogCardId.ElementalHeroStratos) ? 0 : 1).FirstOrDefault();
            if (hero.ID > 0 && Rule(HeroFrogRules.OceanReturnsHero))
            {
                Note(context, $"Ocean returns {CardNames.Get(hero.ID)} from the graveyard to the hand");
                return hero;
            }
            return null;
        }

        /// <summary>
        /// A set card of ours that their effect targets for removal (Lyla's destroy, MST): it's lost when the chain resolves,
        /// so the bar for using it is only whether it does anything (user, 2026-11-11): Compulsory bounces their best monster
        /// that isn't a spent flip monster (bouncing a face-up Ryko lets them set it again); Raigeki Break, whose only cost is
        /// the discard, hits their best card when we have a cheap discard. Null when nothing of ours is targeted.
        /// </summary>
        private async Task<int?> TryDoomedSetAsync(DecisionContext<SelectChainData> context, DynamicCard[] options)
        {
            for (int i = 0; i < options.Length; i++)
            {
                var card = options[i];
                if (card.CurrentCoordinates.Controller != 0 || card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone ||
                    !(IsId(card, HeroFrogCardId.CompulsoryEvacuationDevice) || IsId(card, HeroFrogCardId.RaigekiBreak)) ||
                    !await OurSetCardTargetedAsync(card))
                    continue;
                var theirs = (await GetEnemyMonsterCardsAsync()).Where(m => m.ID > 0 || !m.IsFaceup()).ToList();
                if (IsId(card, HeroFrogCardId.CompulsoryEvacuationDevice))
                {
                    var best = theirs.Where(m => !(m.IsFaceup() && HasFlipEffect(m)))
                        .OrderByDescending(m => m.HasType(CardType.Fusion) || m.HasType(CardType.Synchro) || m.HasType(CardType.Token) ? 1 : 0)
                        .ThenByDescending(OpponentThreat).FirstOrDefault();
                    if ((best.ID > 0 || (best.CurrentCoordinates.Location != 0 && !best.IsFaceup())) && Rule(HeroFrogRules.DoomedSetUsed))
                    {
                        PlanTarget(context.Header.CurrentTurn, best, theirs: true);
                        Note(context, $"Compulsory Evacuation Device is targeted by their effect (gone anyway): bounce {DescribePick(best)} now");
                        return i;
                    }
                }
                else
                {
                    var hand = await GetBotHandCardsAsync();
                    var target = theirs.Where(m => m.IsFaceup()).OrderByDescending(OpponentThreat).FirstOrDefault();
                    if (target.ID <= 0)
                        target = (await GetEnemySpellCardsAsync()).FirstOrDefault(s => s.ID > 0 || !s.IsFaceup());
                    bool cheapDiscard = hand.Any(h => DiscardRank(h) <= 4);
                    if (cheapDiscard && (target.ID > 0 || (target.CurrentCoordinates.Location != 0 && !target.IsFaceup())) && Rule(HeroFrogRules.DoomedSetUsed))
                    {
                        PlanTarget(context.Header.CurrentTurn, target, theirs: true);
                        Note(context, $"Raigeki Break is targeted by their effect (gone anyway): destroy {DescribePick(target)} now, its only cost is the discard");
                        return i;
                    }
                }
            }
            return null;
        }

        private async Task<int?> StandbyStealAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int index = Array.FindIndex(options, card => IsId(card, HeroFrogCardId.EnemyController) && card.CurrentCoordinates.Controller == 0);
            var hand = await GetBotHandCardsAsync();
            var treeborn = (await GetBotMonsterCardsAsync()).FirstOrDefault(card => card.IsFaceup() && IsId(card, HeroFrogCardId.TreebornFrog));
            var steal = BestStealTarget(await GetEnemyMonsterCardsAsync(), brainControl: false);
            // Enemy Controller (user, 2026-11-10): its position-change effect is rubbish; the good one is the second: tribute
            // our Treeborn Frog to take one of their monsters, in the Standby Phase so Treeborn comes straight back. Free:
            // their monster for the turn, to tribute for a Monarch or simply to attack with.
            if (index < 0 || treeborn.ID <= 0 || steal.ID <= 0 || _ourSpellTraps > 0 || !Rule(HeroFrogRules.EnemyControllerStandby))
                return null;
            var enemy = await GetEnemyMonsterCardsAsync();
            string? use = null;
            if (hand.Any(card => IsId(card, TributeSummons)) && await StealClearsTwoAsync(hand, enemy, await GetBotMonsterCardsAsync()) is string twoCards)
                use = $"tribute it for the Monarch ({twoCards})";
            else
            {
                // Without a Monarch: only to go for game (user, 2026-11-11: a borrowed monster is theirs again at the End Phase,
                // and a spent Ryko taken for a 200 hit was pointless): their field otherwise empty, and the borrowed monster plus
                // our attackers reach their life points.
                var rest = enemy.Where(card => !(card.ID == steal.ID && card.CurrentCoordinates.Sequence == steal.CurrentCoordinates.Sequence)).ToList();
                int ourAttack = (await GetBotMonsterCardsAsync())
                    .Where(card => card.IsFaceup() && card.IsAttack() && !IsId(card, HeroFrogCardId.TreebornFrog))
                    .Sum(EffectiveAttack);
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                if (rest.Count == 0 && ourAttack + steal.Attack >= oppLp)
                    use = $"attack for game with it ({steal.Attack} + our {ourAttack} reaches their {oppLp} LP)";
            }
            if (use == null)
                return null;
            _enemyControllerMode = 2;
            PlanTributes(context.Header.CurrentTurn, new[] { treeborn }, "Enemy Controller");
            PlanTarget(context.Header.CurrentTurn, steal, theirs: true);
            Note(context, $"Standby Phase: Enemy Controller tributing Treeborn Frog (it comes back this Standby Phase), take {CardNames.Get(steal.ID)} " +
                          $"and {use}");
            return index;
        }

        private async Task<bool> FaderNowAsync(DecisionContext<SelectChainData> context)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            // Only when this attack plus their attacks still to come would be lethal (then on the first one): it ends
            // the whole Battle Phase. Otherwise take the damage (user rule, 2026-11-09).
            int damage = attacker.Attack + (await GetEnemyMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                               !(card.CurrentCoordinates.Sequence == attacker.CurrentCoordinates.Sequence && card.ID == attacker.ID))
                .Sum(card => card.Attack);
            int lp = await CurrentField.GetBotLifePoints();
            var hand = await GetBotHandCardsAsync();
            bool lethal = damage >= lp;
            bool gorzReady = hand.Any(card => IsId(card, HeroFrogCardId.GorzTheEmissaryOfDarkness)) &&
                             (await GetBotMonsterCardsAsync()).Count == 0 && !(await CurrentField.GetCardsInSpellZone()).Any();
            if (!lethal && gorzReady)
            {
                Note(context, $"hold Battle Fader: take {damage} and Gorz comes out");
                return false;
            }
            if (!lethal)
                return false;
            if (!Rule(HeroFrogRules.BattleFader))
                return false;
            Note(context, $"Battle Fader: stop the direct attack ({damage}{(lethal ? ", lethal" : "")}) and end the battle");
            return true;
        }

        /// <summary>Direct-attack damage from which Battle Fader comes out (LP matter little above ~1500). Tune from logs.</summary>
        [Tunable("Direct-attack damage from which Battle Fader comes out (LP matter little above ~1500). Tune from logs.")]
        public int FaderMinDamage { get; set; } = 1500;

        private async Task<string?> RaigekiNowAsync(DecisionContext<SelectChainData> context)
        {
            var hand = await GetBotHandCardsAsync();
            if (hand.Count == 0)
                return null;
            var discard = hand.OrderBy(DiscardRank).First();
            var phase = (DuelPhase)context.Header.CurrentPhase;
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage or DuelPhase.DamageCal && attacker.ID > 0)
            {
                var defender = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                bool hurts = defender.ID <= 0 ? attacker.Attack >= 1500 : !IsExpendable(defender) && attacker.Attack > defender.GetRealPower();
                if (hurts)
                {
                    // The attacker that hurts most: this one, or a bigger one that hasn't attacked yet (it would hit us next).
                    var later = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                        card.Attack > attacker.Attack).OrderByDescending(card => card.Attack).FirstOrDefault();
                    var target = later.ID > 0 ? later : attacker;
                    _discardPick = discard.ID;
                    PlanTarget(context.Header.CurrentTurn, target, theirs: true);
                    return later.ID > 0
                        ? $"destroy {CardNames.Get(later.ID)} ({later.Attack}), bigger than the attacker {CardNames.Get(attacker.ID)} ({attacker.Attack}) and still to attack, discarding {CardNames.Get(discard.ID)}"
                        : $"destroy the attacker {CardNames.Get(attacker.ID)} ({attacker.Attack}), discarding {CardNames.Get(discard.ID)}";
                }
            }
            if (phase == DuelPhase.End)
            {
                // Clearing our zone only matters when Treeborn is in the graveyard waiting for it; with Treeborn already on the
                // field the set Raigeki Break waits for their attack. Never on a monster our board runs over next turn anyway
                // (seen 2026-11-11: Raigeki Break + Enemy Controller spent on Sirocco at their End Phase, Treeborn already
                // out and our Caius bigger).
                if (!_treebornInGrave || !Rule(HeroFrogRules.RaigekiClearsForTreeborn))
                    return null;
                var ourAttackers = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack()).Select(EffectiveAttack);
                var theirMonsters = await GetEnemyMonsterCardsAsync();
                var beaten = BeatenInBattle(theirMonsters, ourAttackers);
                var enemy = theirMonsters.Where(card => !beaten.Any(b => b.CurrentCoordinates.Sequence == card.CurrentCoordinates.Sequence))
                    .Concat(await GetEnemySpellCardsAsync()).ToList();
                if (enemy.Count == 0)
                    return null;
                // Not a set card they can chain in response (a Jar of Greed just draws them a card: round-robin 25a); one set
                // this turn can't be activated yet, so it's fine (user, 2026-11-13).
                bool Chainable(DynamicCard card) => card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && !card.IsFaceup() &&
                                                    !EnemySetThisTurn(card.CurrentCoordinates.Sequence);
                var target = enemy.OrderBy(card => Chainable(card) ? 1 : 0).ThenByDescending(OpponentThreat).First();
                _discardPick = discard.ID;
                PlanTarget(context.Header.CurrentTurn, target, theirs: true);
                return $"their End Phase: destroy {DescribePick(target)} (discarding {CardNames.Get(discard.ID)}) so our zone is clear for Treeborn" +
                       (Chainable(target) ? " (a set card they could chain, but nothing better)" : "");
            }
            return null;
        }

        // ------------------------------------------------------------------ prompts

        // A planner line taking Stratos' destroy over its search (both on offer) pays the suggestion cost: it searches almost
        // always (user, 2026-11-10), and a planned option is answered before the brain is asked.
        internal override double PlanSuggestions(IReadOnlyList<Search.PlanStep> steps, int index, IReadOnlyList<string> offered, DuelSim.Engine.SideSummary theirs)
        {
            double cost = base.PlanSuggestions(steps, index, offered, theirs);
            static int Desc(string key) => key.StartsWith("option:") && int.TryParse(key.AsSpan(7), out int d) ? d : -1;
            static bool Stratos(int d) => d >= 0 && (d >> 4 == HeroFrogCardId.ElementalHeroStratos || d >> 4 == HeroFrogCardId.ElementalHeroStratos + 20);
            int chosen = Desc(steps[index].Key);
            var stratos = offered.Select(Desc).Where(Stratos).OrderBy(d => d & 0xF).ToList();
            return Stratos(chosen) && stratos.Count >= 2 && chosen == stratos[0] ? cost + SuggestionCost : cost;
        }

        public override async Task<int> OnSelectOptionAsync(DecisionContext<SelectOptionData> context, CancellationToken ct = default)
        {
            var options = context.StateData.Options ?? Array.Empty<int>();
            // The nth choice of this card (1 = first), by its string index: the numbering differs between servers (EDOPro
            // numbers Enemy Controller's / Stratos' choices 1 and 2, Omega 0 and 1), the order doesn't. Asking for index 2
            // found nothing on Omega, and the default switched their monster to defence (seen twice, 2026-11-10).
            int Find(int official, int nth)
            {
                var own = Enumerable.Range(0, options.Length).Where(i => options[i] >> 4 == official || options[i] >> 4 == official + 20)
                    .OrderBy(i => options[i] & 0xF).ToList();
                return nth >= 1 && nth <= own.Count ? own[nth - 1] : -1;
            }
            // Enemy Controller: the mode we planned (1: change a position, 2: take control).
            if (_enemyControllerMode > 0 && Find(HeroFrogCardId.EnemyController, _enemyControllerMode) is int ec and >= 0)
            {
                _enemyControllerMode = 0;
                return ec;
            }
            // Stratos: almost always search a HERO (user, 2026-11-10). Destroying their Spell/Traps only when that clears the
            // way for lethal this turn: no monster of theirs, our attackers deal their life points, and set cards could stop it.
            // Only one choice offered (the other has no target): that one.
            int stratosChoices = options.Count(o => o >> 4 == HeroFrogCardId.ElementalHeroStratos || o >> 4 == HeroFrogCardId.ElementalHeroStratos + 20);
            if (stratosChoices == 1)
                return Find(HeroFrogCardId.ElementalHeroStratos, 1);
            if (stratosChoices >= 2)
            {
                int destroy = Find(HeroFrogCardId.ElementalHeroStratos, 1);
                int search = Find(HeroFrogCardId.ElementalHeroStratos, 2);
                await PrepareContextAsync(context, ct);
                bool theirSets = (await GetEnemySpellCardsAsync()).Any(card => !card.IsFaceup());
                bool open = (await GetEnemyMonsterCardsAsync()).Count == 0;
                int ourAttack = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Sum(card => card.Attack);
                bool lethal = open && theirSets && ourAttack >= await CurrentField.GetBotLifePoints(isOwnSide: false);
                // Or nothing left to search: the deck's two HERO targets (Ocean, Infernal Prodigy) are both out of the deck
                // already (user, 2026-11-10: "you may as well destroy").
                bool nothingToSearch = await InDeckAsync(HeroFrogCardId.ElementalHeroOcean) == 0 && await InDeckAsync(HeroFrogCardId.EvilHeroInfernalProdigy) == 0;
                bool destroyIt = lethal || nothingToSearch;
                Note(context, lethal ? "Stratos: destroy their Spell/Traps: it clears the way for lethal"
                    : nothingToSearch ? "Stratos: destroy their Spell/Traps: no HERO left in the deck to search" : "Stratos: search a HERO");
                return destroyIt ? destroy : search;
            }
            return await base.OnSelectOptionAsync(context, ct);
        }

        public override async Task<CardPosition> OnSelectPositionAsync(DecisionContext<SelectPositionData> context, CancellationToken ct = default)
        {
            var positions = context.StateData.Positions ?? Array.Empty<byte>();
            int id = context.StateData.CardID;
            bool smallFrog = id is HeroFrogCardId.DupeFrog or HeroFrogCardId.TreebornFrog or HeroFrogCardId.Substitoad or HeroFrogCardId.SwapFrog
                or HeroFrogCardId.SwapFrog + 20 or HeroFrogCardId.BattleFader;
            // Swap Frog in attack position when it can hit something this turn (user, 2026-11-11: "may as well have summoned
            // that swap frog in attack and attacked with it"): our Main Phase 1 with a battle to come, and their field empty,
            // a face-down (assumed beaten by 1000), or a face-up monster it beats.
            if (id is HeroFrogCardId.SwapFrog or HeroFrogCardId.SwapFrog + 20 && positions.Contains((byte)CardPosition.FaceUpAttack) &&
                context.Header.CurrentPlayer == 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1)
            {
                await PrepareContextAsync(context, ct);
                var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 || !card.IsFaceup()).ToList();
                bool canHit = theirs.Count == 0 || theirs.Any(card => !card.IsFaceup() ? 1000 > OpponentMemory.FaceDownDefense(card) : 1000 > card.GetRealPower());
                if (canHit && Rule(HeroFrogRules.SwapAttacks))
                {
                    Note(context, "Swap Frog in attack position: it can attack this turn");
                    return CardPosition.FaceUpAttack;
                }
            }
            if (smallFrog && positions.Contains((byte)CardPosition.FaceUpDefence))
                return CardPosition.FaceUpDefence;
            return await base.OnSelectPositionAsync(context, ct);
        }

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            var card = context.StateData.EffectCard;
            if (card.IsCode(HeroFrogCardId.GorzTheEmissaryOfDarkness))
                return await GorzNowAsync(context, await GetEnemyMonsterCardsAsync()) != null;
            // Ocean (our Standby Phase, asked as a yes/no): only with a target worth it, never Ocean itself (user, 2026-11-10:
            // "ocean shouldn't return itself to the hand"; seen: it bounced itself, the only HERO on offer).
            if (IsId(card, HeroFrogCardId.ElementalHeroOcean))
            {
                if (await OceanTargetAsync(context) is DynamicCard target)
                {
                    PlanTarget(context.Header.CurrentTurn, target, theirs: false);
                    return true;
                }
                Note(context, "don't use Ocean: nothing worth returning (never Ocean itself)");
                return false;
            }
            // Swap Frog never sends itself away (user, 2026-11-12, round-robin 40: with no frogs left in the deck it was the only
            // legal card, twice, and the tribute fodder it was summoned as went to the graveyard). No frog in the deck: don't.
            if (IsId(card, HeroFrogCardId.SwapFrog))
            {
                int frogsInDeck = 0;
                foreach (int id in SwapFrogSends)
                    frogsInDeck += await InDeckAsync(id);
                if (frogsInDeck == 0)
                {
                    Note(context, "don't use Swap Frog's effect: no frog left in the deck to send, and it never sends itself");
                    return false;
                }
            }
            if (IsId(card, OwnTriggers))
            {
                Note(context, $"use optional effect of {CardNames.Get(card.ID)}");
                return true;
            }
            return await base.OnSelectEffectYnAsync(context, ct);
        }

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            if (StapleTargets(context) is IList<byte> stapleTarget)
                return stapleTarget;
            if (SelectTributeCard(context) is IList<byte> tribute)
                return tribute;
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int count = Math.Max(1, state.Minimum);
            var hint = (DHint)state.SelectionHint;
            if (cards.Length == 0 || count > cards.Length)
                return await base.OnSelectCardAsync(context, ct);
            if (SelectPlannedMaterials(context, cards, state.Minimum, state.Maximum, state.SelectionHint) is IList<byte> materials)
                return materials;
            var indexed = cards.Select((card, i) => (Card: card, Index: i)).ToList();
            bool allOurs = indexed.All(x => x.Card.CurrentCoordinates.Controller == 0);
            bool In(CardLocation location) => indexed.All(x => (x.Card.CurrentCoordinates.Location & (byte)location) != 0);
            IList<byte> Pick(IEnumerable<(DynamicCard Card, int Index)> order, string what)
            {
                var picks = order.Take(count).ToList();
                Note(context, $"{what}: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}");
                return picks.Select(x => (byte)x.Index).ToList();
            }

            // Fusion materials with no plan (the search's line fired Miracle Fusion): graveyard before field, Treeborn Frog
            // last (the engine), cheapest first.
            if (hint == DHint.FMATERIAL && allOurs)
                return Pick(indexed.OrderBy(x => IsId(x.Card, HeroFrogCardId.TreebornFrog) ? 1 : 0)
                    .ThenBy(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Grave ? 0 : 1)
                    .ThenBy(x => EffectiveAttack(x.Card)), "fusion material (unplanned)");

            // (Swap Frog summoned for Miracle Fusion's WATER used to send a Frog other than Treeborn. No more: Treeborn always; user, 2026-11-18:
            // "swap frog should always send treeborn frog".)

            // Miracle Fusion: which fusion -> Absolute Zero.
            if (hint == DHint.SPSUMMON && In(CardLocation.Extra) && indexed.Any(x => IsId(x.Card, HeroFrogCardId.ElementalHeroAbsoluteZero)))
                return Pick(indexed.OrderBy(x => IsId(x.Card, HeroFrogCardId.ElementalHeroAbsoluteZero) ? 0 : 1), "fusion summon");

            // A monster from the deck (Substitoad, One for One): the one we planned. Unplanned (the search activated it), Treeborn first while
            // it isn't set up: Swap Frog (its trigger sends Treeborn to the graveyard), else Dupe Frog > Treeborn > Swap Frog (user, 2026-11-18:
            // "what happened to ... the routine about going into swap frog, sending treeborn and then bouncing swap?"). Without a plan, the
            // other picks are held while the setup is on offer, so a line that skips it pays for it.
            if (hint == DHint.SPSUMMON && allOurs && indexed.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck))
            {
                int planned = _deckSummonPick;
                _deckSummonPick = 0;
                bool setUpTreeborn = planned == 0 && !_treebornInGrave && !_treebornOnField && await InDeckAsync(HeroFrogCardId.TreebornFrog) > 0 &&
                                     indexed.Any(x => IsId(x.Card, HeroFrogCardId.SwapFrog));
                var order = setUpTreeborn
                    ? new[] { HeroFrogCardId.SwapFrog, HeroFrogCardId.DupeFrog, HeroFrogCardId.TreebornFrog, HeroFrogCardId.Substitoad, HeroFrogCardId.BattleFader }
                    : new[] { planned, HeroFrogCardId.DupeFrog, HeroFrogCardId.TreebornFrog, HeroFrogCardId.SwapFrog, HeroFrogCardId.Substitoad, HeroFrogCardId.BattleFader };
                if (setUpTreeborn && Rule(HeroFrogRules.TreebornSetupFirst))
                    foreach (int other in indexed.Where(x => !IsId(x.Card, HeroFrogCardId.SwapFrog)).Select(x => x.Card.ID).Distinct())
                        HoldPick(other, HeroFrogRules.TreebornSetupFirst);
                return Pick(indexed.OrderBy(x => Array.FindIndex(order, id => id > 0 && IsId(x.Card, id)) is int i and >= 0 ? i : order.Length), "special summon from the deck");
            }

            // Swap Frog's trigger: Treeborn into the graveyard, always (user, 2026-11-18). Any other card it could send is held, so a search line
            // that sends one (Dupe Frog, for its search) pays for it.
            if (hint == DHint.TOGRAVE && allOurs && indexed.Any(x => IsId(x.Card, HeroFrogCardId.TreebornFrog) && x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck) &&
                Rule(HeroFrogRules.SwapSendsTreeborn))
                foreach (int other in indexed.Where(x => !IsId(x.Card, HeroFrogCardId.TreebornFrog)).Select(x => x.Card.ID).Distinct())
                    HoldPick(other, HeroFrogRules.SwapSendsTreeborn);
            // No Treeborn left: the rest of the order (Substitoad, Swap Frog, Dupe Frog last), each card below the best one on offer held lightly.
            else if (hint == DHint.TOGRAVE && allOurs && indexed.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck) &&
                     indexed.Any(x => IsId(x.Card, HeroFrogCardId.SwapFrog) || IsId(x.Card, HeroFrogCardId.DupeFrog) || IsId(x.Card, HeroFrogCardId.Substitoad)) &&
                     Rule(HeroFrogRules.SwapSendOrder))
            {
                var fromDeck = indexed.Where(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck).ToList();
                int best = fromDeck.Min(x => StaplePriorityIndex(SwapFrogSends, x.Card));
                foreach (int worse in fromDeck.Where(x => StaplePriorityIndex(SwapFrogSends, x.Card) > best).Select(x => x.Card.ID).Distinct())
                    HoldPick(worse, HeroFrogRules.SwapSendOrder);
            }
            // Swap Frog's trigger: Treeborn into the graveyard.
            if (hint == DHint.TOGRAVE && allOurs && In(CardLocation.Deck | CardLocation.MonsterZone) && indexed.Any(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck))
                return Pick(indexed.Where(x => !(x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && IsId(x.Card, HeroFrogCardId.SwapFrog)))
                    .OrderBy(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.Deck ? 0 : 1)
                    .ThenBy(x => StaplePriorityIndex(SwapFrogSends, x.Card)), "Swap Frog sends to the graveyard (never itself)");

            // Searches from the deck (or Dupe Frog's from the graveyard too).
            if (hint == DHint.ATOHAND && allOurs && In(CardLocation.Deck | CardLocation.Grave))
            {
                var hand = await GetBotHandCardsAsync();
                var ours = await GetBotMonsterCardsAsync();
                bool Has(int id) => hand.Any(card => IsId(card, id)) || ours.Any(card => IsId(card, id));
                int[] order;
                if (indexed.Any(x => IsId(x.Card, HeroFrogCardId.SwapFrog) || IsId(x.Card, HeroFrogCardId.Substitoad) || IsId(x.Card, HeroFrogCardId.TreebornFrog)))
                    // Dupe Frog's search: the engine (Substitoad) if we have none, Swap Frog (a body + a Treeborn dump), Treeborn.
                    order = Has(HeroFrogCardId.Substitoad)
                        ? new[] { HeroFrogCardId.SwapFrog, HeroFrogCardId.TreebornFrog, HeroFrogCardId.Substitoad, HeroFrogCardId.DesFrog }
                        : new[] { HeroFrogCardId.Substitoad, HeroFrogCardId.SwapFrog, HeroFrogCardId.TreebornFrog, HeroFrogCardId.DesFrog };
                else
                    // HERO / Warrior searches: Stratos (it searches again or pops a Spell/Trap), then Ocean, Junk Synchron, Prodigy.
                    order = Has(HeroFrogCardId.ElementalHeroStratos)
                        ? new[] { HeroFrogCardId.ElementalHeroOcean, HeroFrogCardId.JunkSynchron, HeroFrogCardId.EvilHeroInfernalProdigy, HeroFrogCardId.ElementalHeroStratos }
                        : new[] { HeroFrogCardId.ElementalHeroStratos, HeroFrogCardId.ElementalHeroOcean, HeroFrogCardId.JunkSynchron, HeroFrogCardId.EvilHeroInfernalProdigy };
                return Pick(indexed.OrderBy(x => Array.FindIndex(order, id => IsId(x.Card, id)) is int i and >= 0 ? i : order.Length), "search");
            }

            // Aoi: their hand revealed: send their best card to the graveyard.
            if (indexed.All(x => x.Card.CurrentCoordinates.Controller != 0 && x.Card.CurrentCoordinates.Location == (byte)CardLocation.Hand))
            {
                ObserveRevealedOpponentHand(cards);
                return Pick(indexed.OrderByDescending(x => HandThreat(x.Card)), "from their hand");
            }

            // Discards / sends from our hand (Swap Frog, Raigeki Break, One for One): the planned card, else the cheapest.
            if (hint is DHint.DISCARD or DHint.TOGRAVE && allOurs && In(CardLocation.Hand))
            {
                int planned = _discardPick;
                _discardPick = 0;
                return Pick(indexed.OrderBy(x => planned > 0 && IsId(x.Card, planned) ? -1 : DiscardRank(x.Card)), $"{hint}");
            }

            if (await SelectOpponentTargetsAsync(context) is IList<byte> removal)
                return removal;
            return await base.OnSelectCardAsync(context, ct);
        }

        // A card of theirs seen in their hand (Aoi): big plays and backrow-breakers first, then the biggest monster.
        private static int HandThreat(DynamicCard card)
        {
            if (card.HasType(CardType.Monster))
                return card.Attack + card.Level * 100;
            if (card.HasType(CardType.Trap))
                return 2600;
            return 2800; // a spell: their removal / revival / searches
        }

        private static void Note<T>(DecisionContext<T> context, string message) => context.Bot.LogNote($"herofrog: {message}");
    }
}
