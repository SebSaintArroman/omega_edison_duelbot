namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.lyla-before-battle
        /// Lyla's effect in Main Phase 1, then the Battle Phase: a weak cost (Lyla clearing the way for a push is better than
        /// her post-battle gamble).
        /// </summary>
        [Tunable("")]
        public static readonly Tuned<double> LylaBeforeBattleCost = new(0.5);
    }
}
