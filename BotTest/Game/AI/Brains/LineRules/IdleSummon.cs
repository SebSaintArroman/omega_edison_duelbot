namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.idle-summon
        /// A monster summoned face-up that by the end of our turn never attacked, never used an effect and is still on the
        /// field: this cost each (not charged when no Battle Phase is possible this turn, e.g. the duel's first).
        /// </summary>
        [Tunable("")]
        public static readonly Tuned<double> IdleSummonCost = new(1.5);
    }
}
