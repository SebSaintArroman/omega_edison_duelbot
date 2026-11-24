namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.jar-their-turn
        /// Jar of Greed (and Legacy of Yata-Garasu) fired on their turn when it isn't about to be destroyed, nor clearing our
        /// field for Gorz against an attack: this cost instead of EarlyTrapCost (user, 2026-11-19).
        /// </summary>
        [Tunable("Jar of Greed fired on their turn when it isn't about to be destroyed, nor clearing our field for Gorz against an attack (it's our only card, Gorz in hand): this cost, instead of EarlyTrapCost. User, 2026-11-19: those, or our own turn with no play, \"are the only legitimate times\".")]
        public static readonly Tuned<double> JarTheirTurnCost = new(1.5);
    }
}
