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
        /// Which monster to Special Summon from the Extra Deck when several are possible (e.g. Vayu's synchro): the one that
        /// answers a threat our board can't (SynchroAnswer), otherwise the biggest. Never just the first in the list.
        /// </summary>
        private async Task<IList<byte>?> ExtraDeckSummonPickAsync(DecisionContext<SelectCardData> context)
        {
            var spOptions = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (context.StateData.SelectionHint != (int)DHint.SPSUMMON || spOptions.Length <= 1 ||
                !spOptions.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Extra && card.ID > 0))
                return null;
            await LearnBattleDestroyersAsync(spOptions.Select(card => card.ID));
            var enemy = await GetEnemyMonsterCardsAsync();
            var ourAtk = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup()).Select(card => card.Attack).ToList();
            int handCards = (await GetBotHandCardsAsync()).Count;
            var ranked = spOptions.Select((card, index) =>
                {
                    var option = new SynchroOption(card.ID, card.Level, card.Attack,
                        BattleDestroyersExcept.TryGetValue(card.ID, out var spared) ? spared : null, card.Attribute);
                    return (Index: index, Card: card, Answer: SynchroAnswer(option, enemy, ourAtk, handCards));
                })
                .OrderByDescending(x => x.Answer != null)
                .ThenByDescending(x => x.Card.Attack)
                .ToList();
            int take = Math.Max(1, context.StateData.Minimum);
            var picks = ranked.Take(take).ToList();
            context.Bot.LogNote($"deck brain: Extra Deck summon: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}" +
                                (picks[0].Answer != null ? $" ({picks[0].Answer})" : $" (the biggest on offer, {picks[0].Card.Attack} ATK)"));
            return picks.Select(x => (byte)x.Index).ToList();
        }
    }
}
