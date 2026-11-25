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
    /// Card selection prompts: targets, attack targets, Extra Deck summons, revives, costs.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// A card selection. The handlers, in order (the first with an answer decides):
        ///  1. the staples' planned targets (StapleTargets) and Allure's banish;
        ///  2. fusion materials we planned (Chimeratech Fortress Dragon's safety fuse, Overload Fusion...);
        ///  3. Brionac's discards and targets, as planned when it was activated;
        ///  4. removal on the opponent's cards (Selection/GenericTargetPicks.cs);
        ///  5. an Extra Deck summon among several (Selection/ExtraDeckSummonPick.cs);
        ///  6. a plain target; the attack target; a destroy / banish pick (Selection/...);
        ///  7. a revive from our graveyard (Selection/Rules/NoReviveGraveEffect.cs);
        ///  8. the default selection.
        /// </summary>
        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored($"[DECK BOT BRAIN] [OnSelectCard] => {(DHint)context.StateData.SelectionHint}");

            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            await HoldBounceSelfSummonersAsync(context); // Selection/Rules/BounceSelfSummoner.cs
            return StapleTargets(context)                                                                 // 1.
                ?? await AllureBanishPickAsync(context)
                ?? SelectPlannedMaterials(context, cards, context.StateData.Minimum, context.StateData.Maximum,
                       context.StateData.SelectionHint)                                                    // 2.
                ?? BrionacPick(context)                                                                    // 3.
                ?? await RemovalTargetPickAsync(context)                                                   // 4.
                ?? await ExtraDeckSummonPickAsync(context)                                                 // 5.
                ?? PlainTargetPick(context)                                                                // 6.
                ?? AttackTargetPick(context)
                ?? DestroyOrBanishPick(context)
                ?? await RevivePickAsync(context)                                                          // 7.
                ?? await DefaultCardSelectionAsync(context, ct);                                           // 8.
        }

        private async Task<IList<byte>?> DefaultCardSelectionAsync(DecisionContext<SelectCardData> context, CancellationToken ct)
        {
            ConsoleUtils.PrintColored("No specific defender index available, using default selection.");
            return await base.OnSelectCardAsync(context, ct);
        }

        /// <summary>
        /// Select/unselect prompts (e.g. Dark Grepher's summon cost, Chaos Sorcerer's banish cost) carry the same data as
        /// a normal card selection, so they go through the deck's own card choice (discard priorities, cost priorities)
        /// instead of the default "first card listed", which once threw away Gorz with Sirocco right next to it.
        /// The prompt repeats one card at a time; with nothing more required (minimum 0), finish.
        /// </summary>
        public override async Task<IList<byte>?> OnSelectUnselectCardAsync(DecisionContext<SelectUnselectCardData> context, CancellationToken ct = default)
        {
            var data = context.StateData;
            var cards = data.SelectableCards ?? Array.Empty<DynamicCard>();
            // Fusion materials we planned: all of them, not just the minimum.
            if (cards.Length > 0 && SelectPlannedMaterials(context, cards, data.Minimum, data.Maximum, data.SelectionHint, onePerPrompt: true) is IList<byte> materials)
                return materials;
            if (data.Minimum <= 0 || cards.Length == 0)
                return await base.OnSelectUnselectCardAsync(context, ct);
            var asSelect = new SelectCardData
            {
                SelectableCards = cards,
                Minimum = 1,
                Maximum = 1,
                SelectionHint = data.SelectionHint,
                Cancelable = data.Cancelable,
                SelectableCount = data.SelectableCount
            };
            var choice = await OnSelectCardAsync(new DecisionContext<SelectCardData>
            {
                Bot = context.Bot,
                State = context.State,
                Header = context.Header,
                StateData = asSelect,
                Field = context.Field,
                Features = context.Features
            }, ct);
            if (choice is { Count: > 0 } && choice[0] < cards.Length)
                return new List<byte> { choice[0] };
            return await base.OnSelectUnselectCardAsync(context, ct);
        }

        public override async Task<int> OnAnnounceCardAsync(DecisionContext<AnnounceCardData> context, CancellationToken ct = default)
        {
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnAnnounceCard");
            var cards = await DuelBotWrapper.GetMatchingCards(context.StateData.AppliableFilters ?? Array.Empty<int>());
            return BotActionUtilities.SelectRandomAnnouncedCard(cards, preferAlias: true);
        }

        protected IList<byte>? PreferCardSelection(SelectCardData state, params int[] preferredIds)
        {
            var selected = state.SelectableCards
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => x.Card.IsCode(preferredIds))
                .Take(state.Minimum)
                .Select(x => (byte)x.Index)
                .ToArray();

            return selected.Length >= state.Minimum ? selected : null;
        }
    }
}
