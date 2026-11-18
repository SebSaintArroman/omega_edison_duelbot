using OmegaBotWrapper;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Where play search verdicts go besides the duel log (DuelSim's per-run search record): one event per searched
    /// decision, overridden or kept, with the rules that shaped the brain's choice. See DeckBotBrain.Search.
    /// </summary>
    public static class SearchEventSink
    {
        public static Action<DuelBotWrapper, string>? OnSearchEvent;

        internal static void Report(DuelBotWrapper? bot, string evt)
        {
            if (bot != null)
                OnSearchEvent?.Invoke(bot, evt);
        }
    }
}
