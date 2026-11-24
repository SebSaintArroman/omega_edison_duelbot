namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.early-trap
        /// A trap of ours fired on their turn in a window opened by nothing (no chain to answer, no attack, no summon just
        /// made; not their End Phase): it could wait for the last moment. This cost (user, 2026-11-19). Trap Dustshoot excepted.
        /// </summary>
        [Tunable("A trap of ours fired on their turn in a window opened by nothing (no chain to answer, no attack, no summon just made; not their End Phase): it could have waited for the last possible moment. User, 2026-11-19.")]
        public static readonly Tuned<double> EarlyTrapCost = new(1.5); // user, 2026-11-19: 1.5
    }
}
