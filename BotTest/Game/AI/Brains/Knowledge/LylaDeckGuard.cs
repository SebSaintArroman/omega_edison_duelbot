using BotTest.Game.Field;
using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Lyla's End Phase mill (3 a turn) against our deck size: below LylaDeckGuard cards she isn't summoned, and one on
    /// the field is better gone.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Our deck size at the last field read.
        private int _ourDeckCount = 40;

        /// <summary>Below this many cards in our deck, Lyla isn't summoned and one on the field crashes (her End Phase mill). Tune from logs.</summary>
        [Tunable("Below this many cards in our deck, Lyla isn't summoned and one on the field crashes (her End Phase mill). Tune from logs.")]
        public int LylaDeckGuard { get; set; } = 10;

        /// <summary>Whether our deck is too small for Lyla's End Phase mill (3 a turn).</summary>
        protected bool DeckTooSmallForLyla => _ourDeckCount < LylaDeckGuard;
    }
}
