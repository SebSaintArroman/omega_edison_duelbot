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
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// RULE deck.reposition-to-defence
        /// A face-up attack-position monster that should be in defence (ShouldRepositionToDefense, MainPhase/Repositioning.cs)
        /// switches: the weakest first.
        /// </summary>
        private IdleCmdResponse? RepositionToDefence(DecisionContext<IdleCmdData> context, List<DynamicCard> opponentMonsters)
        {
            var toDefence = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsFaceup() && card.IsAttack() && ShouldRepositionToDefense(card, opponentMonsters))
                .OrderBy(card => card.Attack)
                .FirstOrDefault();
            if (toDefence.ID <= 0 || !Rule(DeckRules.RepositionToDefence))
                return null;
            ConsoleUtils.PrintColored($"Repositioning to defense: {toDefence.ID}");
            if (_mirrorForceReason.TryGetValue(toDefence.CurrentCoordinates.Sequence, out var mirrorWhy))
                context.Bot.LogNote($"deck brain: {CardNames.Get(toDefence.ID)} to defence: {mirrorWhy}");
            return toDefence.Reposition();
        }
    }
}
