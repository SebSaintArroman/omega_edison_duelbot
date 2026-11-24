namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.allure-with-dark
        /// Allure of Darkness activated with a DARK in hand to banish: this bonus.
        /// </summary>
        /// <summary>Allure of Darkness activated with a DARK in hand to banish: this small bonus (user, 2026-11-18: "give allure a small activation
        /// bonus"). Without a DARK it takes the whole hand: no bonus (the brain holds it then).</summary>
        [Tunable("Allure of Darkness activated with a DARK in hand to banish: this small bonus (card units).")]
        public static readonly Tuned<double> AllureBonus = new(0.3);
    }
}
