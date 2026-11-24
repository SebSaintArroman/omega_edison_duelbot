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
        /// Removal on the opponent's cards (destroy / banish / return to hand / to deck; not plain "target", which may be one of
        /// ours): the shared best-target choice (SelectOpponentTargetsAsync).
        /// </summary>
        private async Task<IList<byte>?> RemovalTargetPickAsync(DecisionContext<SelectCardData> context) =>
            (DHint)context.StateData.SelectionHint is DHint.DESTROY or DHint.REMOVE or DHint.RTOHAND or DHint.TODECK &&
            await SelectOpponentTargetsAsync(context) is IList<byte> removal
                ? removal
                : null;

        /// <summary>A plain "target" prompt with nothing more specific: the highest-ATK cards on offer.</summary>
        private static IList<byte>? PlainTargetPick(DecisionContext<SelectCardData> context)
        {
            if (context.StateData.SelectionHint != (int)DHint.TARGET)
                return null;
            byte[] botCards = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                .Select((card, index) => new { Card = card, Index = index })
                .OrderByDescending(x => x.Card.Attack)
                .Take(context.StateData.Minimum)
                .Select(x => (byte)x.Index)
                .ToArray();
            if (!botCards.Any())
                return null;
            ConsoleUtils.PrintColored($"Selected {botCards.Length} cards for TARGET.");
            return botCards;
        }

        /// <summary>
        /// A destroy / banish prompt the removal choice didn't answer: the opponent's cards first. With only our own cards on
        /// offer (a forced destroy, e.g. Ryko's flip with their field empty): the one we lose least - a spent flip monster
        /// (Ryko itself), a token, then the weakest monster; spells/traps last.
        /// </summary>
        private IList<byte>? DestroyOrBanishPick(DecisionContext<SelectCardData> context)
        {
            if (context.StateData.SelectionHint is not ((int)DHint.DESTROY or (int)DHint.REMOVE))
                return null;
            string actionName = context.StateData.SelectionHint == (int)DHint.DESTROY ? "DESTROY" : "REMOVE";
            ConsoleUtils.PrintColored($"OnSelectCard - Selecting Card to {actionName}");

            byte[] playerCards = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => x.Card.Owner == 1)
                .Take(context.StateData.Minimum)
                .Select(x => (byte)x.Index)
                .ToArray();
            if (playerCards.Any())
            {
                ConsoleUtils.PrintColored($"Selected {playerCards.Length} cards for {actionName}.");
                return playerCards;
            }

            var ours = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                .Select((card, index) => (Card: card, Index: index))
                .Where(x => x.Card.CurrentCoordinates.Controller == 0)
                .ToList();
            if (ours.Count == 0 || ours.Count != (context.StateData.SelectableCards?.Length ?? 0))
                return null;
            var cheapest = ours
                .OrderBy(x => IsSpentFlipMonster(x.Card) ? 0 : x.Card.HasType(CardType.Token) ? 1 : x.Card.HasType(CardType.Monster) ? 2 : 3)
                .ThenBy(x => x.Card.HasType(CardType.Monster) ? x.Card.Attack : 0)
                .Take(Math.Max(1, context.StateData.Minimum))
                .ToList();
            context.Bot.LogNote($"deck brain: {actionName}: only our own cards on offer, the cheapest: {string.Join(", ", cheapest.Select(x => CardNames.Get(x.Card.ID)))}");
            return cheapest.Select(x => (byte)x.Index).ToList();
        }
    }
}
