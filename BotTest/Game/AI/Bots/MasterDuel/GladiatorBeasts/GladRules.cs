using BotTest.Game.AI.Brains;

namespace BotTest.Game.AI.Bots.MasterDuel.GladiatorBeasts
{
    /// <summary>
    /// Gladiator Beasts brain rules, by name (from the Rule(...) gates in the code). Each gate fires with the probability tuning
    /// gives it (default 1: as written). See RuleGates and DuelSim's evolve.
    /// </summary>
    [RuleSet(typeof(GladiatorBeastsBotBrain))]
    public static class GladRules
    {
        public const string TagOut = "glads.tag-out";
        /// <summary>Shield Warrior stays in hand while Raigeki Break is there: it's the discard (CSV M6).</summary>
        [HoldCost(HoldCosts.Light)]
        public const string ShieldWarriorForRaigeki = "glads.shield-warrior-for-raigeki";
        /// <summary>A Gladiator Beast summoned with no use for its tag-out (it can't attack anything without dying and their biggest attacker
        /// runs it over next turn) loses what the tag-out is worth (CSV M6).</summary>
        [HoldCost(HoldCosts.Light)]
        public const string TagOutUnusable = "glads.tag-out-unusable";
        public const string TagPick = "glads.tag-pick";
        public const string Gyzarus = "glads.gyzarus";
        public const string GyzarusTag = "glads.gyzarus-tag";
        public const string Heraklinos = "glads.heraklinos";
        public const string HeraklinosNegate = "glads.heraklinos-negate";
        public const string BestiariForGyzarus = "glads.bestiari-for-gyzarus";
        public const string GladSummon = "glads.glad-summon";
        public const string WarChariot = "glads.war-chariot";
        public const string IndomitableRecycle = "glads.indomitable-recycle";
        /// <summary>Indomitable's GY effect held while firing it would leave no Gladiator Beast card in the GY (user, 2026-11-26).</summary>
        [HoldCost(HoldCosts.Standard)]
        public const string IndomitableKeepOne = "glads.indomitable-keep-one";
        /// <summary>Stack the fewest attack boosts (Skill Successor, Indomitable, its recycles) to get a Glad over their monster.</summary>
        public const string BoostOverWall = "glads.boost-over-wall";
        /// <summary>Raigeki Break on a big threat only (worth two cards): their attacker, or the wall our attacker can't get over.</summary>
        public const string RaigekiThreat = "glads.raigeki-threat";
        public const string Discard = "glads.discard";
        /// <summary>Routine: tag out into Bestiari, then Gyzarus destroys their set Ryko unflipped (round-robin 29b).</summary>
        public const string GyzarusForRyko = "glads.gyzarus-for-ryko";
    }
}
