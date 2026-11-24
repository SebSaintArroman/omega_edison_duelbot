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
    /// RULE deck.stat-boost-in-damage-step: a card that only changes ATK/DEF (Skill Successor, Rush Recklessly, Shrink) waits
    /// for the damage step, unless it is doomed or their card just activated reads ATK.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>A deck's own exception to "stat changes wait for the damage step" (Glads: Indomitable in a Main Phase while it can
        /// come back from the GY for another use). Default: none.</summary>
        protected virtual Task<bool> StatBoostMayGoEarlyAsync(DynamicCard card, DuelPhase phase) => Task.FromResult(false);

        /// <summary>Their spell/trap just activated (face-up since the last decision) reads ATK: Bottomless Trap Hole and the like.</summary>
        private async Task<bool> TheirActivationReadsAtkAsync()
        {
            if (_newFaceUpSpellZones.Count == 0)
                return false;
            foreach (var card in await GetEnemySpellCardsAsync())
                if (card.ID > 0 && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence) &&
                    await CardDatabase.GetAsync(card.ID) is DBCardInfo info && StatText.IsMatch(info.CardText ?? ""))
                    return true;
            return false;
        }

        private static readonly System.Text.RegularExpressions.Regex StatText = new(@"(ATK|DEF)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex NotOnlyStats = new(
            @"destroy|negate|Special Summon|draw|add d|add it|return|take control|cannot be destroyed|unaffected|Life Points|inflict|Set|Tribute",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A spell/trap whose effects only change ATK/DEF (Skill Successor, Rush Recklessly, Shrink): its text speaks of ATK or
        /// DEF and of nothing that removes, negates, summons, draws, searches, protects or burns.</summary>
        protected static async Task<bool> OnlyChangesStatsAsync(int cardId)
        {
            if (cardId <= 0)
                return false;
            // The engine's own card data first (always loaded, live and in DuelSim), Omega's database otherwise.
            if (Search.SearchHost.Available && Search.SearchHost.Cards.Get(cardId) is { } r)
                return (r.Type & 0x6) != 0 && StatText.IsMatch(r.Text ?? "") && !NotOnlyStats.IsMatch(r.Text ?? "");
            return await CardDatabase.GetAsync(cardId) is DBCardInfo info && (info.Type & 0x6) != 0 &&
                   StatText.IsMatch(info.CardText ?? "") && !NotOnlyStats.IsMatch(info.CardText ?? "");
        }

        /// <summary>Holds our offered cards that only change ATK/DEF, outside the damage step: unless the deck lets
        /// one go early (StatBoostMayGoEarlyAsync) or it's doomed.</summary>
        private async Task HoldEarlyStatChangesAsync(DecisionContext<SelectChainData> context, DuelPhase phase)
        {
            // Exception: their card just activated depends on ATK (Bottomless: "1500 or more ATK"): a stat change can answer it, e.g.
            // Shrink on our own monster to get under it (user, 2026-11-15).
            // "The damage step": an attack in progress (either side's). The phase reads BattleStart through the whole battle, live and in
            // DuelSim, so it can't tell (seen 2026-11-15: every damage-step boost was being held).
            // (Battle Phase only: the under-attack flag can be stale in the Main Phase - seen 2026-11-16, Skill Successor chained in Main Phase 1.)
            bool attackInProgress = phase is DuelPhase.Damage or DuelPhase.DamageCal ||
                                    phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle &&
                                    (await CurrentField.IsBotUnderAttack() || await CurrentField.IsBotUnderAttack(false));
            if (!attackInProgress && !await TheirActivationReadsAtkAsync())
            {
                var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
                for (int i = 0; i < cards.Length; i++)
                    if (cards[i].CurrentCoordinates.Controller == 0 && await OnlyChangesStatsAsync(cards[i].ID) &&
                        !await StatBoostMayGoEarlyAsync(cards[i], phase) && !await DoomedAsync(cards[i]))
                    {
                        HoldKey(cards[i].ID, "chain:", DeckRules.StatBoostInDamageStep);
                        if (!IsSearchTwin)
                            context.Bot.LogNote($"deck brain: {CardNames.Get(cards[i].ID)} held for the damage step (it only changes ATK/DEF)");
                    }
            }
        }
    }
}
