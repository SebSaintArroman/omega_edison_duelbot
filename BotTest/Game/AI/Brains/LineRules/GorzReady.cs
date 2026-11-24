namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.gorz-ready
        /// Gorz in our hand and our field empty at the end of a line: this small bonus (Gorz can come out on their direct hit).
        /// </summary>
        [Tunable("Gorz in our hand and our field empty at the end of a line: this small bonus (card units): Gorz can come out on their direct hit.")]
        public static readonly Tuned<double> GorzReadyBonus = new(0.3);
    }
}
