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
    /// RULE deck.draw-when-no-play: a draw card is used when there's nothing better to do.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>No good play this turn (Main Phase 1, Normal Summon unused): draw with Legacy of Yata-Garasu / Jar of Greed to look for one.</summary>
        protected IdleCmdResponse? TryDrawWhenNoPlay(DecisionContext<IdleCmdData> context, IdleCmdData state, bool normalSummonUsed)
        {
            if ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && !normalSummonUsed &&
                (TryActivateStaple(context, state, LegacyOfYataGarasu, "no good play this turn: draw to look for one", out var response, DeckRules.DrawWhenNoPlay) ||
                 TryActivateStaple(context, state, JarOfGreed, "no good play this turn: draw to look for one", out response, DeckRules.DrawWhenNoPlay)))
                return response;
            return null;
        }
    }
}
