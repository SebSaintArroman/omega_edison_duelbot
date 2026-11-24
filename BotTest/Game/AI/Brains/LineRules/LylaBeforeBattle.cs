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

        /// <summary>
        /// RULE line.lyla-before-battle (traps negated)
        /// The same, while no trap can be activated (face-up Jinzo, Royal Decree): there is no trap for her to clear the way past, so she
        /// attacks first and pops a set in Main Phase 2, clearing a monster and a set card (user, 2026-11-24).
        /// </summary>
        [Tunable("")]
        public static readonly Tuned<double> LylaBeforeBattleTrapsNegatedCost = new(1.0);
    }
}
