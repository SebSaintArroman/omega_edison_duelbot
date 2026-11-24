namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.equip-before-battle
        /// A union equip (Machina Gearframe / Peacekeeper) in Main Phase 1, then the Battle Phase: this cost per equip.
        /// </summary>
        [Tunable("")]
        public static readonly Tuned<double> EquipBeforeBattleCost = new(1.5);
    }
}
