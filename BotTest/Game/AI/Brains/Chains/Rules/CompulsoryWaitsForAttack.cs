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
    /// RULE deck.compulsory-on-attack: on their turn, Compulsory Evacuation Device on their monsters prefers to wait for the attack.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// A small hold on Compulsory Evacuation Device in their turn's chain windows before the attack comes, unless the brain chose it here
        /// (saving our own monster) or it's doomed (HeroFrog's Draw Phase clear marks its backrow doomed). There are plenty of reasons to use it
        /// early, so this is only a nudge; the big cost is on bouncing a monster that comes straight back from the hand (BounceSelfSummoner.cs).
        /// User, 2026-11-25 (Machina #108, Compulsory in their Draw Phase on Faultroll): "the hold should be large against a monster that has a
        /// special summon effect from hand (like fortress or faultroll) if it's outside the attacking window, and small for everything else".
        /// </summary>
        private async Task HoldCompulsoryBeforeAttackAsync(DecisionContext<SelectChainData> context, int answer)
        {
            if (IsOwnTurn(context.Header) || await CurrentField.IsBotUnderAttack())
                return;
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            for (int i = 0; i < offered.Length; i++)
                if (i != answer && offered[i].IsCode(CompulsoryEvacuationDevice) && offered[i].CurrentCoordinates.Controller == 0 &&
                    !await DoomedAsync(offered[i]))
                    HoldKey(offered[i].ID, "chain:", DeckRules.CompulsoryOnAttack);
        }
    }
}
