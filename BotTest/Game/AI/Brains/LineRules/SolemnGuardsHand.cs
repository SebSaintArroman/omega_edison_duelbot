namespace BotTest.Game.AI.Brains
{
    /// <summary>Costs (and bonuses) the play search charges a line for what it does: one rule per file.</summary>
    internal static partial class LineRules
    {
        /// <summary>
        /// RULE line.solemn-guards-hand
        /// A set Solemn Judgment gains SolemnGuardWeight times the worth of our strongest card still in hand (capped at
        /// SolemnGuardCap; only from SolemnGuardMinWorth): it protects that play next turn.
        /// </summary>
        [Tunable("A set Solemn Judgment gains this times the worth of our strongest card still in hand (capped at SolemnGuardCap): it protects that play next turn.")]
        public static readonly Tuned<double> SolemnGuardWeight = new(0.3);

        [Tunable("The most a hand card's worth counts for in the Solemn bonus (card units).")]
        public static readonly Tuned<double> SolemnGuardCap = new(2);

        [Tunable("A hand card counts as strong for the Solemn bonus from this worth (effect points plus ATK value).")]
        public static readonly Tuned<double> SolemnGuardMinWorth = new(2);
    }
}
