using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Helpers for card rules: what a Normal Summon sets off.</summary>
    internal static partial class CardRules
    {
        internal static bool ThreatTriggersOnNormalSummon(int code) =>
            Search.SearchHost.Available && Search.SearchHost.Cards.Get(code)?.Text is { } text &&
            text.Contains("Normal Summoned", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("If this card", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("When this card", StringComparison.OrdinalIgnoreCase));
    }
}
