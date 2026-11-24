namespace BotTest.Game.AI.Bots.MasterDuel.Blackwing
{
    /// <summary>The Blackwing lists the brain plays (DeckBotBrain.Variant). Standard and Hybrid.</summary>
    public static class BlackwingVariant
    {
        public const string Standard = BotTest.Game.AI.Brains.DeckBotBrain.StandardVariant; // Blackwing
        /// <summary>BlackwingHybrid (2026-11-10): Blackwings leaning into the Vayu engine: 3 Vayu, Armageddon Knight, 2 Dark
        /// Grepher, Burial, Return from the Different Dimension; fewer Bora / Kalut / Blizzard. The Vayu setup summons go
        /// ahead of a plain Blackwing beater.</summary>
        public const string Hybrid = "Hybrid";
    }
}
