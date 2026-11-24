namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.own-destroy
        /// Our own card picked for one of our effects that destroys (MST on our own set card): this cost per card
        /// (user, 2026-11-19: "HUGE").
        /// </summary>
        [Tunable("Our own card picked for one of our effects that destroys (Mystical Space Typhoon on our own set card): this cost per card. User, 2026-11-19: \"HUGE HUGE Error: mst destroy own card\".")]
        public static readonly Tuned<double> OwnDestroyCost = new(3);
    }
}
