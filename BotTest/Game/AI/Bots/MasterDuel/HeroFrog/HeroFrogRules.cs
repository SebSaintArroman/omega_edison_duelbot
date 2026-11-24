using BotTest.Game.AI.Brains;
using OmegaBotWrapper.Enum;

namespace BotTest.Game.AI.Bots.MasterDuel.HeroFrog
{
    /// <summary>
    /// HeroFrog rules, by name (from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// </summary>
    [RuleSet(typeof(HeroFrogBotBrain))]
    public static class HeroFrogRules
    {
        internal static bool TreebornDoomsBackrow(bool ourTurn, DuelPhase phase, bool treebornInGrave, bool monarchInHand) =>
            treebornInGrave && (ourTurn ? phase == DuelPhase.Draw : phase == DuelPhase.End && monarchInHand);

        public const string AoiStrip = "herofrog.aoi-strip";
        public const string BattleFader = "herofrog.battle-fader";
        public const string BrainControlTribute = "herofrog.brain-control-tribute";
        public const string ClearZoneForTreeborn = "herofrog.clear-zone-for-treeborn";
        public const string EmergencyCall = "herofrog.emergency-call";
        public const string EnemyControllerStandby = "herofrog.enemy-controller-standby";
        public const string EnemyControllerTribute = "herofrog.enemy-controller-tribute";
        public const string JunkSynchron = "herofrog.junk-synchron";
        public const string JunkSynchro = "herofrog.junk-synchro";
        public const string MiracleFusion = "herofrog.miracle-fusion";
        public const string MonarchSummon = "herofrog.monarch-summon";
        public const string OceanReturnsZero = "herofrog.ocean-returns-zero";
        public const string OceanReturnsHero = "herofrog.ocean-returns-hero";
        public const string OneForOneFodder = "herofrog.one-for-one-fodder";
        public const string ProdigyFodder = "herofrog.prodigy-fodder";
        public const string PressureSummon = "herofrog.pressure-summon";
        public const string SwapBounce = "herofrog.swap-bounce";
        /// <summary>Swap Frog returns a Monarch to the hand only with Treeborn set up, and then in Main Phase 2 after it attacked unless their
        /// backrow or a monster is set (user, 2026-11-24: return it so it removes the best card they bring out next turn); MonarchReturnValue
        /// rewards the line that does.</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string MonarchReturnInMain2 = "herofrog.monarch-return-in-main2";
        /// <summary>Swap Frog's trigger sends Treeborn Frog, always (user, 2026-11-18).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string SwapSendsTreeborn = "herofrog.swap-sends-treeborn";
        /// <summary>With no Treeborn to send, Swap Frog sends Substitoad, then another Swap Frog, Dupe Frog last (user, 2026-11-18).</summary>
        [HoldCost(HoldCosts.Light)]
        public const string SwapSendOrder = "herofrog.swap-send-order";
        /// <summary>An unplanned special summon from the deck (Substitoad, One for One) goes for Swap Frog while Treeborn isn't set up: its trigger sends Treeborn to the graveyard (user, 2026-11-18).</summary>
        public const string TreebornSetupFirst = "herofrog.treeborn-setup-first";
        /// <summary>Spiritual Water Art - Aoi is never set without a face-up WATER monster of ours on the field (user, 2026-11-18).</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string AoiSetNeedsWater = "herofrog.aoi-set-needs-water";
        public const string HoldMiracleFusion = "herofrog.hold-miracle-fusion";
        public const string HeroForFusion = "herofrog.hero-for-fusion";
        public const string RaigekiBreak = "herofrog.raigeki-break";
        public const string SubstitoadEngine = "herofrog.substitoad-engine";
        public const string SubstitoadSummon = "herofrog.substitoad-summon";
        public const string SwapFrogSummon = "herofrog.swap-frog-summon";
        /// <summary>Swap Frog's own special summon (discard a WATER) as fodder: for a Monarch, or Substitoad's Dupe lock, when there's no other way.</summary>
        public const string SwapFodder = "herofrog.swap-fodder";
        public const string TreebornHoldsSets = "herofrog.treeborn-holds-sets";
        /// <summary>
        /// Frogs don't like redundancy on the backrow (user, 2026-11-11): one answer to their monsters (Aoi, Raigeki Break,
        /// Compulsory) set at a time, always. A second one must be used suboptimally just to clear our zone for Treeborn.
        /// </summary>
        public const string NoRedundantBackrow = "herofrog.no-redundant-backrow";
        /// <summary>A set card of ours their effect targets is gone anyway: use it now if it does anything at all (Compulsory on their best monster that isn't a spent flip, Raigeki Break with a cheap discard).</summary>
        public const string DoomedSetUsed = "herofrog.doomed-set-used";
        /// <summary>Miracle Fusion waits for a turn with a battle ahead (not Main Phase 2), unless holding it loses.</summary>
        public const string FusionWaitsForBattle = "herofrog.fusion-waits-for-battle";
        /// <summary>Normal Summon Dupe Frog from the hand to complete the Dupe lock (a Dupe out already, or Substitoad out with a Dupe left in the deck).</summary>
        public const string DupeLockFromHand = "herofrog.dupe-lock-from-hand";
        /// <summary>Swap Frog comes out in attack position when it can hit something this turn (1000 into an open field or a face-down).</summary>
        public const string SwapAttacks = "herofrog.swap-attacks";
        /// <summary>Brain Control / Enemy Controller wait unless the steal clears a second card (a Monarch alone handles one).</summary>
        public const string HoldSteal = "herofrog.hold-steal";
        /// <summary>
        /// Miracle Fusion is removal more than a monster (user, 2026-11-11): Absolute Zero leaving the field wipes their
        /// monsters, so their set cards don't stop it when they have monsters out (killing it kills their board). With no
        /// monster of theirs it is only a 2500 body that eats a trap: held unless it makes lethal, they're open, or we lose.
        /// </summary>
        public const string FusionIsRemoval = "herofrog.fusion-is-removal";
        /// <summary>Miracle Fusion never banishes Treeborn Frog (the engine) as its WATER material.</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string FusionSparesTreeborn = "herofrog.fusion-spares-treeborn";
        /// <summary>
        /// Losing on board (we lose next turn, or two of their attackers run over a Monarch): the Dupe lock (Substitoad into
        /// two Dupe Frogs) comes before a Monarch; a Monarch that dies next turn doesn't save us, the lock does.
        /// </summary>
        [HoldCost(HoldCosts.Strong)]
        public const string DupeLockFirst = "herofrog.dupe-lock-first";
        /// <summary>A Monarch planned this turn (its fodder or steal on the way) takes the Normal Summon: no other summon.</summary>
        public const string MonarchTakesSummon = "herofrog.monarch-takes-summon";
        /// <summary>Caius / Raiza's trigger skips their monsters our attackers (the Monarch included) beat in battle this turn: it takes what battle can't (a face-down, a set card).</summary>
        /// <summary>With the Dupe lock up (two Dupe Frogs), Substitoad turns spare fodder (Treeborn) into Des Frog every turn: a clock behind the lock.</summary>
        public const string LockThenClock = "herofrog.lock-then-clock";
        /// <summary>Raigeki Break at their End Phase only to clear our zone for a Treeborn in the graveyard, and never on a monster our board beats.</summary>
        public const string RaigekiClearsForTreeborn = "herofrog.raigeki-clears-for-treeborn";
        /// <summary>Both Dupe Frogs in hand: Swap Frog (special summon), Dupe, Swap Frog's self-bounce for one more summon, the second Dupe.</summary>
        [HoldCost(HoldCosts.Strong)]
        public const string DupeLockViaSwap = "herofrog.dupe-lock-via-swap";
        /// <summary>Miracle Fusion lacks a WATER (only Treeborn there) and is wanted: Normal Summon Swap Frog, whose trigger sends a non-Treeborn Frog to the graveyard.</summary>
        public const string FusionWaterViaSwap = "herofrog.fusion-water-via-swap";
        /// <summary>Swap Frog is never set face-down (its summon trigger and bounce are lost); with the summon otherwise unused it is Normal Summoned.</summary>
        public const string SwapNeverSet = "herofrog.swap-never-set";
        public const string MonarchTakesWhatBattleCant = "herofrog.monarch-takes-what-battle-cant";
    }
}
