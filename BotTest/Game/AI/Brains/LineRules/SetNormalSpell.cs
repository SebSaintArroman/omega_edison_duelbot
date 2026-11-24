namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.set-normal-spell
        /// Setting a normal spell (worth less set than in hand, and it shows a card): this cost (user, 2026-11-19).
        /// </summary>
        [Tunable("Setting a normal spell (it is worth less set than in hand, and shows a card): this line cost. User, 2026-11-19: \"put the set spell value back to 0.8 and add back the line penalty of 0.5 for setting one\".")]
        public static readonly Tuned<double> SetNormalSpellCost = new(0.5);
    }
}
