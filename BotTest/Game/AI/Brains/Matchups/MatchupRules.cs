namespace BotTest.Game.AI.Brains.Matchups
{
    /// <summary>Rules for playing against specific decks (see Matchup), by name, gated like DeckRules.</summary>
    [RuleSet(typeof(DeckBotBrain))]
    public static class MatchupRules
    {
        /// <summary>Against X-Sabers: with Emmersblade on their field (Hyunlei next turn), don't overexpose backrow.</summary>
        public const string XSaberHyunleiBackrow = "matchup.xsaber.hyunlei-backrow";
    }
}
