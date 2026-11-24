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
    /// RULE deck.avoid-hand-limit-discard: over the hand limit before the End Phase, set spells/traps (traps first), then the
    /// deck's last resort.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Before ending the turn with more than 6 cards in hand (the End Phase discards the rest for nothing): a play that
        /// keeps a card instead. Spells/traps are set, traps first; then the deck's own last resort (DeckHandLimitPlay). Null
        /// when the hand is within the limit or nothing helps (the discard then takes the card that loses least).
        /// </summary>
        protected async Task<IdleCmdResponse?> HandLimitPlayAsync(DecisionContext<IdleCmdData> context)
        {
            var hand = await GetBotHandCardsAsync();
            int over = hand.Count - 6;
            if (over <= 0 || !Rule(DeckRules.AvoidHandLimitDiscard))
                return null;
            var sets = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0)
                .OrderBy(card => card.HasType(CardType.Trap) ? 0 : 1);
            foreach (var card in sets)
            {
                if (!await SharedSpellSetAllowedAsync(card))
                    continue;
                context.Bot.LogNote($"deck brain: set {CardNames.Get(card.ID)}: {hand.Count} cards in hand, the End Phase would discard {over} for nothing");
                return card.SetSpellTrap();
            }
            if (await DeckHandLimitPlayAsync(context, over) is IdleCmdResponse deckPlay)
                return deckPlay;
            return null;
        }

        /// <summary>A deck's own last resort against the hand-limit discard (Machina: summon a Fortress). Default none.</summary>
        protected virtual Task<IdleCmdResponse?> DeckHandLimitPlayAsync(DecisionContext<IdleCmdData> context, int over) =>
            Task.FromResult<IdleCmdResponse?>(null);
    }
}
