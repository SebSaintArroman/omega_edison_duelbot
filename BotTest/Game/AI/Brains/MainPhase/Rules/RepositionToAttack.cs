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
        /// RULE deck.reposition-to-attack (with deck.hold-for-gorz, Main Phase side)
        /// A defence-position monster that should attack (ShouldRepositionToAttack, MainPhase/Repositioning.cs) switches: the
        /// strongest first. Into a field with no cards at all the attack is a direct one, which Gorz can answer: decide about
        /// Gorz first and commit (user, 2026-11-13: "this chance to hold back should've been ran before switching hamster to
        /// attack mode. make the decision as early as possible and then commit to it"). The Gorz roll is kept for the turn, so
        /// the Battle Phase follows the same decision.
        /// </summary>
        private async Task<IdleCmdResponse?> RepositionToAttackAsync(DecisionContext<IdleCmdData> context, List<DynamicCard> opponentMonsters)
        {
            var toAttack = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsDefense() && ShouldRepositionToAttack(card, opponentMonsters))
                .OrderByDescending(card => card.Attack)
                .FirstOrDefault();
            if (toAttack.ID > 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 &&
                !opponentMonsters.Any(card => card.ID != 0 || !card.IsFaceup()) && (await GetEnemySpellCardsAsync()).Count == 0 &&
                await FearGorzAsync(context) && Rule(DeckRules.HoldForGorz))
            {
                context.Bot.LogNote($"deck brain: {CardNames.Get(toAttack.ID)} stays in defence: we're holding direct attacks this turn (possible Gorz)");
                toAttack = default;
            }
            if (toAttack.ID <= 0 || !Rule(DeckRules.RepositionToAttack))
                return null;
            ConsoleUtils.PrintColored($"Repositioning to attack: {toAttack.ID}");
            return toAttack.Reposition();
        }
    }
}
