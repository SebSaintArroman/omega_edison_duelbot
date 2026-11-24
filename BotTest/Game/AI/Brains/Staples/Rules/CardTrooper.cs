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
    /// Card Trooper: summoned to mill and grow, as long as the mill can't deck us out. RULES deck.summon-trooper,
    /// deck.trooper-deck-guard.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Card Trooper: mill 3 for graveyard fuel (and 1900 ATK) while the deck can take it; with the deck too thin to
        /// mill, it stays at 400 ATK, so it goes to defence instead.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryCardTrooperAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            // (Only reads the deck when Card Trooper can do something this turn.)
            bool inPlay = TryGetActivable(state, CardTrooper, out _) || TryGetReposable(state, CardTrooper, out _);
            if (!inPlay)
                return null;
            bool canMill = await TrooperCanMillAsync(context);
            if (canMill && TryActivateStaple(context, state, CardTrooper, "mill for graveyard fuel", out var response, DeckRules.TrooperMill))
                return response;
            if (!canMill && Rule(DeckRules.TrooperDefenceWhenThin) && TryGetReposable(state, CardTrooper, out var low) && low.IsFaceup() && low.IsAttack())
            {
                StapleNote(context, $"Card Trooper to defence: the deck is too thin to mill (under {MinDeckToMill} cards), so it stays at 400 ATK");
                return low.Reposition();
            }
            return null;
        }

        /// <summary>Whether our deck is big enough for Card Trooper to mill (see <see cref="MinDeckToMill"/>).</summary>
        protected async Task<bool> TrooperCanMillAsync<T>(DecisionContext<T> context)
        {
            int deck = (await CurrentField.GetBotField()).OwnSide.Main?.Length ?? 0;
            if (deck >= MinDeckToMill || !Rule(DeckRules.TrooperDeckGuard))
                return true;
            // Never say never: if Card Trooper at 1900 wins this turn and the deck has the 3 cards to mill, mill.
            if (deck >= 3 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && IsOwnTurn(context.Header))
            {
                var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Select(EffectiveAttack).ToList();
                var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
                if (BoardAssessment.TurnsToWin(attackers, defenders, await CurrentField.GetBotLifePoints(isOwnSide: false)) == 1)
                {
                    StapleNote(context, $"Card Trooper mills with only {deck} cards left in the deck: at 1900 it wins this turn");
                    return true;
                }
            }
            StapleNote(context, $"don't use Card Trooper: {deck} cards left in the deck (under {MinDeckToMill})");
            return false;
        }

        /// <summary>
        /// Card Trooper doesn't mill at all with fewer cards than this in the deck (it goes to defence instead). Low on
        /// purpose: running close to deck-out is fine.
        /// </summary>
        [Tunable("Card Trooper doesn't mill at all with fewer cards than this in the deck (it goes to defence instead). Low on purpose: running close to deck-out is fine.")]
        private static readonly Tuned<int> MinDeckToMill = new(5);
    }
}
