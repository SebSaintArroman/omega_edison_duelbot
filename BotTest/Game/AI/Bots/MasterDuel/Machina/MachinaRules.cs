using BotTest.Game.AI.Brains;

namespace BotTest.Game.AI.Bots.MasterDuel.Machina
{
    /// <summary>
    /// Machina brain rules, by name (from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// </summary>
    [RuleSet(typeof(MachinaBotBrain))]
    public static class MachinaRules
    {
        public const string AvariceForValue = "machina.avarice-for-value";
        public const string DarkArmedDragon = "machina.dark-armed-dragon";
        /// <summary>Dark Armed Dragon's effect with a spare DARK in the graveyard (main phase and priority window).</summary>
        public const string DadEffect = "machina.dad-effect";
        /// <summary>Normal Summon Jinzo - Returner to crash it into a monster Jinzo then kills (its revival).</summary>
        public const string ReturnerCrash = "machina.returner-crash";
        /// <summary>Set Jinzo - Returner (a weak play: rolled at a low chance, see the constructor).</summary>
        public const string ReturnerSet = "machina.returner-set";
        public const string ArmageddonSend = "machina.armageddon-send";
        public const string FutureFusionFinish = "machina.future-fusion-finish";
        public const string FutureFusionMaterials = "machina.future-fusion-materials";
        public const string AvariceWhenNoPlay = "machina.avarice-when-no-play";
        public const string BrionacBounce = "machina.brionac-bounce";
        public const string CompulsoryGearframe = "machina.compulsory-gearframe";
        public const string CyberDragonPressure = "machina.cyber-dragon-pressure";
        public const string FortressFromGrave = "machina.fortress-from-grave";
        public const string FortressFromHand = "machina.fortress-from-hand";
        [ResourceHold]
        public const string FortressLastResort = "machina.fortress-last-resort";
        /// <summary>A Fortress with a costly discard (Force, Gearframe, several Machines) waits while Armageddon Knight can be Normal Summoned instead.</summary>
        [ResourceHold]
        public const string KnightBeforeCostlyFortress = "machina.knight-before-costly-fortress";
        /// <summary>Fortress waits while a less expensive answer handles their board (a ready monster, Gearframe, a free summon), in the
        /// Conservative strategic state (high LP, cards even; user, 2026-11-24: play a little more conservatively): its probability is 0
        /// outside it (StateTuning: MachinaBotBrain.Rule.machina.cheaper-answer-first@Conservative = 1).</summary>
        [ResourceHold, InStates("Conservative")]
        public const string CheaperAnswerFirst = "machina.cheaper-answer-first";
        /// <summary>Fortress waits while Gearframe can be Normal Summoned first (it searches; Fortress then discards the search).</summary>
        public const string GearframeBeforeFortress = "machina.gearframe-before-fortress";
        /// <summary>The Cyber Dragon safety equip (against Chimeratech) waits for Main Phase 2 when the union can attack first.</summary>
        public const string UnionEquipAfterBattle = "machina.union-equip-after-battle";
        /// <summary>Gearframe searches Fortress; Force only with a Fortress already on the field or in the graveyard, or two in hand; never Peacekeeper (user, 2026-11-17).</summary>
        [HoldCost(HoldCosts.Strong)] // above the 2.0 Peacekeeper (5.5) outscores Fortress (3.5) by in hand
        public const string GearframeSearch = "machina.gearframe-search";
        /// <summary>Fortress held when a set card of theirs is likely Bottomless Trap Hole (a banished Fortress is gone for good).</summary>
        [TrapFear]
        public const string FortressBanishRisk = "machina.fortress-banish-risk";
        public const string FortressForPressure = "machina.fortress-for-pressure";
        public const string FortressPopsItsKiller = "machina.fortress-pops-its-killer";
        public const string FortressWhenNoBoard = "machina.fortress-when-no-board";
        public const string HoldFodder = "machina.hold-fodder";
        public const string OverloadForLethal = "machina.overload-for-lethal";
        public const string PhoenixAgainstDPrison = "machina.phoenix-against-d-prison";
        public const string PlagueSynchro = "machina.plague-synchro";
        public const string SetFlipEveryTurn = "machina.set-flip-every-turn";
        public const string SummonGearframe = "machina.summon-gearframe";
        public const string UnionEquipFortress = "machina.union-equip-fortress";
        /// <summary>With our Jinzo out, at most one trap is set a turn (it negates them; one waits for when they remove it).</summary>
        public const string TrapsUnderJinzo = "machina.traps-under-jinzo";
        public const string UnionEquipForMirror = "machina.union-equip-for-mirror";
        public const string UnionEquipProtect = "machina.union-equip-protect";
        public const string UnionEquipSafety = "machina.union-equip-safety";
        /// <summary>No union equip while our Cyber Phoenix is face-up: it negates the union's effect while equipped (user, 2026-11-13).</summary>
        public const string UnionNotWithPhoenix = "machina.union-not-with-phoenix";
        /// <summary>An equipped union comes off (back as a monster) while our Cyber Phoenix negates its effect.</summary>
        public const string UnionUnequipForPhoenix = "machina.union-unequip-for-phoenix";
        /// <summary>A protection equip only when the protection is worth more than the union's own attack this turn, and only
        /// on our one key monster (with others like it unprotected we're no safer; user, 2026-11-13).</summary>
        public const string UnionEquipWorthIt = "machina.union-equip-worth-it";
    }
}
