using BotTest.Game.AI.Brains;

namespace BotTest.Game.AI.Bots.MasterDuel.Blackwing
{
    /// <summary>
    /// BlackwingRules rules, by name (generated from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// </summary>
    [RuleSet(typeof(BlackwingBotBrain))]
    public static class BlackwingRules
    {
        // The Blackwing deck's own plays (the end of BlackwingBotBrain.cs).
        public const string AggroSummon = "blackwing.aggro-summon";
        public const string AllureDraw = "blackwing.allure-draw";
        /// <summary>Allure of Darkness waits for a DARK monster in hand (without one it banishes the whole hand); only a last resort, with the rest of the hand set.</summary>
        [HoldCost(HoldCosts.Critical)]
        public const string AllureNeedsDark = "blackwing.allure-needs-dark";
        /// <summary>Shura's summon from the deck takes Vayu first: free, and it works from the graveyard.</summary>
        public const string ShuraTakesVayu = "blackwing.shura-takes-vayu";
        /// <summary>Gale is mostly removal: no free-body summon when they have no face-up monster to shrink.</summary>
        public const string GaleIsRemoval = "blackwing.gale-is-removal";
        public const string BlackWhirlwind = "blackwing.black-whirlwind";
        /// <summary>Blizzard's revive on its Normal Summon: always (also when it shares a chain prompt with Black Whirlwind).</summary>
        public const string BlizzardRevive = "blackwing.blizzard-revive";
        /// <summary>Icarus Attack is set only with a Winged Beast of ours on the field to tribute, one copy at a time.</summary>
        public const string IcarusSetNeedsWing = "blackwing.icarus-set-needs-wing";
        /// <summary>Icarus Attack isn't fired in our own Main Phase 2: it waits for their turn (their attack, removal, End Phase). User, 2026-11-11
        /// (fired at Gorz + token in Main Phase 2); a hold since 2026-11-25 (was a search ban): worth breaking for a 2-for-3 like Vayu over Lyla first.</summary>
        [HoldCost(HoldCosts.Strong)] // user, 2026-11-25: "make mp2 icarus a heavy hold"
        public const string IcarusWaitsForTheirTurn = "blackwing.icarus-waits-for-their-turn";
        public const string FreeBody = "blackwing.free-body";
        public const string GaleHalve = "blackwing.gale-halve";
        /// <summary>BlackwingHybrid: Armageddon Knight / Dark Grepher's graveyard setup before a plain Blackwing beater.</summary>
        public const string HybridSetupFirst = "blackwing.hybrid-setup-first";
        public const string IcarusEndPhase = "blackwing.icarus-end-phase";
        public const string IcarusFreeTribute = "blackwing.icarus-free-tribute";
        public const string IcarusPush = "blackwing.icarus-push";
        public const string KalutBoost = "blackwing.kalut-boost";
        public const string SiroccoBoost = "blackwing.sirocco-boost";
        /// <summary>Sirocco's boost with nothing to gain: it's the only Blackwing, or their field has no monster and the boost isn't a one-attack lethal (user, 2026-11-16 / 2026-11-17 / 2026-11-25).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string SiroccoBoostPointless = "blackwing.sirocco-boost-pointless";
        /// <summary>Sirocco's boost with Honest suspected in their hand and a LIGHT (or face-down) monster of theirs out (user, 2026-11-17).</summary>
        public const string SiroccoBoostIntoHonest = "blackwing.sirocco-boost-into-honest";
        /// <summary>Black Whirlwind stays in hand until a Blackwing Normal Summon follows (user, 2026-11-17). Also: Whirlwind activated this turn with the Normal Summon still to come holds every other move before the summon, so a line
        /// pays this cost once per move between them (user, 2026-11-24: "increase the whirlwind waits for summon hold by how far apart the
        /// whirlwind and summon are").</summary>
        public const string WhirlwindWaitsForSummon = "blackwing.whirlwind-waits-for-summon";
        /// <summary>A Blackwing Normal Summoned with Black Whirlwind in hand, also with one face-up (two search twice): its search is lost (user, 2026-11-24).</summary>
        [HoldCost(2)]
        public const string SummonWithWhirlwindInHand = "blackwing.summon-with-whirlwind-in-hand";
        /// <summary>Ending the turn without a Normal Summon while Black Whirlwind is in hand or face-up (user, 2026-11-24).</summary>
        [HoldCost(2)]
        public const string NoSummonWithWhirlwindInHand = "blackwing.no-summon-with-whirlwind-in-hand";
        /// <summary>Bora's free summon only when its attack adds a kill or a direct hit worth making this turn.</summary>
        public const string BodyNeedsPurpose = "blackwing.body-needs-purpose";
        /// <summary>With the Normal Summon still to come, a Blackwing is summoned before Sirocco's boost (it joins the total; after the boost it couldn't attack).</summary>
        public const string SummonBeforeSiroccoBoost = "blackwing.summon-before-sirocco-boost";
        public const string SiroccoFree = "blackwing.sirocco-free";
        public const string ArmedWingSteppingStone = "blackwing.armed-wing-stepping-stone";
        public const string BlackRoseWipe = "blackwing.black-rose-wipe";
        public const string ChaosEffect = "blackwing.chaos-effect";
        public const string ChaosSummon = "blackwing.chaos-summon";
        public const string ComboLethal = "blackwing.combo-lethal";
        public const string DadEffect = "blackwing.dad-effect";
        public const string DadSummon = "blackwing.dad-summon";
        public const string DadTrim = "blackwing.dad-trim";
        public const string EffectSynchro = "blackwing.effect-synchro";
        public const string ExtraDeckSynchro = "blackwing.extra-deck-synchro";
        public const string GrepherEffect = "blackwing.grepher-effect";
        public const string GrepherSpecialForLethal = "blackwing.grepher-special-for-lethal";
        public const string HoldAscendant = "blackwing.hold-ascendant";
        public const string HoldDadEffectForVayuPieces = "blackwing.hold-dad-effect-for-vayu-pieces";
        public const string HoldGrepherForDad = "blackwing.hold-grepher-for-dad";
        public const string HoldKnightForDad = "blackwing.hold-knight-for-dad";
        public const string NecroForLastMoment = "blackwing.necro-for-last-moment";
        public const string NecroGardna = "blackwing.necro-gardna";
        public const string NecroNotOnOwnMonster = "blackwing.necro-not-on-own-monster";
        public const string NecroProtectsOnlyClock = "blackwing.necro-protects-only-clock";
        public const string PlagueSynchro = "blackwing.plague-synchro";
        public const string RecursionForVayu = "blackwing.recursion-for-vayu";
        public const string SummonArmageddon = "blackwing.summon-armageddon";
        /// <summary>No face-up small-Blackwing summon that can't hit anything and starts no play; no Blackwing sets.</summary>
        public const string NoPointlessSummon = "blackwing.no-pointless-summon";
        /// <summary>Blizzard is Normal Summoned only with a Level 4 or lower Blackwing in the graveyard to revive.</summary>
        public const string BlizzardNeedsTarget = "blackwing.blizzard-needs-target";
        /// <summary>Kalut stays in hand (+1400 in battle) unless summoning it makes a synchro or lethal.</summary>
        public const string KalutStaysInHand = "blackwing.kalut-stays-in-hand";
        public const string SummonGrepher = "blackwing.summon-grepher";
        public const string VayuPlagueLevel8 = "blackwing.vayu-plague-level8";
        public const string VayuSynchro = "blackwing.vayu-synchro";
    }
}
