namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.brain-control-unused
        /// Brain Control whose monster goes back to them at the end of our turn (not tributed, not Synchro material, no win):
        /// this cost (user, 2026-11-19: "make it a 2-point hold").
        /// </summary>
        [Tunable("Brain Control whose monster goes back to them at the end of our turn (not tributed, not Synchro material, no win): this small cost. User, 2026-11-19: \"the goal of it is to tribute or synchro with my monster, or win that turn\".")]
        public static readonly Tuned<double> BrainControlUnusedCost = new(2); // user, 2026-11-19: "make it a 2-point hold"
    }
}
