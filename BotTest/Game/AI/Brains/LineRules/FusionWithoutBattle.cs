namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.fusion-without-battle
        /// Miracle Fusion with no Battle Phase after it that turn: this cost.
        /// </summary>
        [Tunable("")]
        public static readonly Tuned<double> FusionWithoutBattleCost = new(1.5);
    }
}
