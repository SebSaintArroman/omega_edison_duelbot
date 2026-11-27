using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Helpers for card rules: the Frog engine.</summary>
    internal static partial class CardRules
    {
        internal static readonly int[] SwapFrogDeckTargets = { CardId.TreebornFrog, CardId.SwapFrog, CardId.DupeFrog, CardId.Unifrog, CardId.FlipFlopFrog, CardId.Substitoad };
    }
}
