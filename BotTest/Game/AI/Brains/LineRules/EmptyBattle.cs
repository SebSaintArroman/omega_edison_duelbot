namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.empty-battle
        /// A line that enters the Battle Phase and makes no attack: this cost (user, 2026-11-17/18: it should outweigh the
        /// Main Phase 2 gains).
        /// </summary>
        /// <summary>A line that enters the Battle Phase and makes no attack (user, 2026-11-16: "maybe there should be a very small penalty for moving to battle phase if not planning to attack").</summary>
        [Tunable("A line that enters the Battle Phase and makes no attack: this small cost (card units).")]
        public static readonly Tuned<double> EmptyBattleCost = new(2); // user, 2026-11-18: "make that bigger" (was 1); 2026-11-17: "it should outweigh the doing stuff in mp2 bonuses" (was 0.1)
    }
}
