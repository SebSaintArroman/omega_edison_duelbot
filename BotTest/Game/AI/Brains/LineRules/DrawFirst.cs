namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.draw-first
        /// A draw effect on offer and another move made first: this cost. Draws come first, so the rest of the turn is played
        /// with the new cards (user, 2026-11-13). Small: a rule holding a draw back (Upstart waiting for lethal) still wins.
        /// </summary>
        [Tunable("was 0.2 (CSV S4: too small to put draws first)")]
        public static readonly Tuned<double> DrawLaterCost = new(0.5); // was 0.2 (CSV S4: too small to put draws first)
    }
}
