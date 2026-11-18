using BotTest.Game.AI.Brains;

namespace BotTest.Game.AI.Bots.MasterDuel.Vayu
{
    /// <summary>
    /// VayuRules rules, by name (generated from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// </summary>
    [RuleSet(typeof(VayuBotBrain))]
    public static class VayuRules
    {
        public const string ArmedWingSteppingStone = "vayu.armed-wing-stepping-stone";
        public const string BlackRoseWipe = "vayu.black-rose-wipe";
        public const string ChaosEffect = "vayu.chaos-effect";
        public const string ChaosSummon = "vayu.chaos-summon";
        public const string ComboLethal = "vayu.combo-lethal";
        public const string DadEffect = "vayu.dad-effect";
        public const string DadSummon = "vayu.dad-summon";
        public const string DadTrim = "vayu.dad-trim";
        public const string EffectSynchro = "vayu.effect-synchro";
        public const string ExtraDeckSynchro = "vayu.extra-deck-synchro";
        public const string GrepherEffect = "vayu.grepher-effect";
        public const string GrepherSpecialForLethal = "vayu.grepher-special-for-lethal";
        public const string HoldAscendant = "vayu.hold-ascendant";
        public const string HoldDadEffectForVayuPieces = "vayu.hold-dad-effect-for-vayu-pieces";
        public const string HoldGrepherForDad = "vayu.hold-grepher-for-dad";
        public const string HoldKnightForDad = "vayu.hold-knight-for-dad";
        public const string NecroForLastMoment = "vayu.necro-for-last-moment";
        public const string NecroGardna = "vayu.necro-gardna";
        public const string NecroNotOnOwnMonster = "vayu.necro-not-on-own-monster";
        public const string NecroProtectsOnlyClock = "vayu.necro-protects-only-clock";
        public const string PlagueSynchro = "vayu.plague-synchro";
        public const string RecursionForVayu = "vayu.recursion-for-vayu";
        public const string SummonArmageddon = "vayu.summon-armageddon";
        public const string SummonGrepher = "vayu.summon-grepher";
        public const string VayuPlagueLevel8 = "vayu.vayu-plague-level8";
        public const string VayuSynchro = "vayu.vayu-synchro";
    }
}
