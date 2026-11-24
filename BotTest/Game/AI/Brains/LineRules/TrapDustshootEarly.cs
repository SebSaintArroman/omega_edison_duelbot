namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.trap-dustshoot-early
        /// Trap Dustshoot fired at its first chance: this bonus (see the field's own doc in the tuning file).
        /// </summary>
        [Tunable("A line that activates our Trap Dustshoot gains this (card units): it removes a card from their hand almost always, but the lines' unseen hand cards are blanks (user, 2026-11-18: \"1 point if it's activated\").")]
        public static readonly Tuned<double> DustshootBonus = new(1.0);
    }
}
