namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.return-dimension-their-turn
        /// Return from the Different Dimension fired on their turn, even against an attack, unless it's about to be destroyed:
        /// this cost instead of EarlyTrapCost (user, 2026-11-19).
        /// </summary>
        [Tunable("Return from the Different Dimension fired on their turn, even against an attack, unless Return is about to be destroyed: this cost, instead of EarlyTrapCost. Not on our turn (plenty of plays there). User, 2026-11-19.")]
        public static readonly Tuned<double> ReturnTheirTurnCost = new(1.5);
    }
}
