namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.effect-in-priority-window
        /// A monster's effect used in the chain window it gets (the priority window after its summon), not later: this bonus.
        /// Almost always right (user, 2026-11-15), so it wins the ties. Not Lyla (her effect waits for the Battle Phase), not a
        /// union equip.
        /// </summary>
        /// <summary>Bonus (card units) for a monster's effect used in a chain window of our turn (its priority window) rather than later.</summary>
        [Tunable("Bonus (card units) for a monster's effect used in a chain window of our turn (its priority window) rather than later.")]
        public static readonly Tuned<double> PriorityWindowBonus = new(0.3);
    }
}
