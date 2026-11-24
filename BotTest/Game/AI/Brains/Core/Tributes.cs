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
    /// Tributes (Tribute Summons and tribute costs: Enemy Controller, Substitoad, Spiritual Water Art - Aoi...). Every
    /// deck tributes, so the choice is shared: the cheapest monsters go first (a stolen monster, which goes back at the
    /// End Phase anyway; tokens; expendable monsters), the deck's best last. Deck brains price their own cards
    /// (<see cref="TributeCost"/>: Treeborn Frog comes back every Standby Phase, so it's free) and can plan the exact
    /// tributes when deciding a play (<see cref="PlanTributes"/>).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private (int Turn, List<(int Location, int Sequence)> Cards, string What)? _tributePlan;

        /// <summary>Remembers the tributes for the play we're about to make (used by the next tribute prompt this turn).</summary>
        protected void PlanTributes(int turn, IEnumerable<DynamicCard> tributes, string what) =>
            _tributePlan = (turn, tributes.Select(card => ((int)card.CurrentCoordinates.Location, (int)card.CurrentCoordinates.Sequence)).ToList(), what);

        /// <summary>
        /// What tributing this monster costs us (lower goes first). Default: a stolen monster 0 (it goes back anyway), a
        /// token 1, an expendable monster 2, anything else 3 + its ATK in thousands. Deck brains price their own cards.
        /// </summary>
        protected virtual double TributeCost(DynamicCard card)
        {
            if (IsStolen(card))
                return 0;
            // Lyla whose End Phase mill would deck us out: she has to go, at any cost (user, 2026-11-12).
            if (card.IsCode(LylaLightswornSorceress) && DeckTooSmallForLyla)
                return 0;
            // The order (user, 2026-11-13, round-robin 50): Treeborn Frog is always the most expendable (it comes back); then a
            // monster with a graveyard effect (Vayu: it wants to be there); then expendable monsters, smallest first; tokens after
            // them (a token is always less expendable than an expendable monster with lower stats), totally expendable under
            // 1500 ATK and creeping up above; then everything else by ATK.
            if (card.IsCode(TreebornFrog))
                return TributeTreeborn;
            if (LylaSpent(card))
                return TributeLylaSpent;
            int atk = Math.Max(0, EffectiveAttack(card));
            if (card.HasType(CardType.Token))
                return atk < TributeTokenFrom ? TributeTokenSmall + atk / 10000.0 : TributeTokenBig + (atk - TributeTokenFrom) / 1000.0;
            if (IsExpendable(card))
                return HasGraveyardEffect(card) ? TributeGraveEffect : TributeExpendable + atk / 10000.0;
            return TributeOther + atk / 1000.0;
        }

        /// <summary>The turn we last used Lyla's effect (it switches her to defence).</summary>
        protected int LylaEffectTurn { get; set; } = -10;

        /// <summary>
        /// Lyla after her effect: face-up in defence, and she can't switch back to attack before our next turn. Until then
        /// she's semi-expendable: a great tribute or synchro material, not worth protecting (user, 2026-11-12).
        /// </summary>
        protected bool LylaSpent(DynamicCard card) =>
            card.IsCode(LylaLightswornSorceress) && card.IsFaceup() && !card.IsAttack() &&
            _currentHeader.CurrentTurn <= LylaEffectTurn + 1;

        /// <summary>A Lyla of ours we'd rather see gone or used up: spent (LylaSpent), or her mill would deck us out.</summary>
        protected bool LylaToUseUp(DynamicCard card) => card.IsCode(LylaLightswornSorceress) && card.IsFaceup() && (LylaSpent(card) || DeckTooSmallForLyla);

        /// <summary>An opponent's monster we control for now (Enemy Controller, Brain Control): it goes back at the End Phase.</summary>
        protected static bool IsStolen(DynamicCard card) =>
            card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && card.Owner != card.CurrentCoordinates.Controller;

        /// <summary>Our monsters on the field, cheapest tribute first.</summary>
        protected IEnumerable<DynamicCard> CheapestTributes(IEnumerable<DynamicCard> ours) =>
            ours.Where(card => card.ID > 0 || card.CurrentCoordinates.Controller == 0).OrderBy(TributeCost);

        public override async Task<IList<byte>?> OnSelectTributeAsync(DecisionContext<SelectTributeData> context, CancellationToken ct = default)
        {
            var data = context.StateData;
            var cards = data.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0)
                return await base.OnSelectTributeAsync(context, ct);
            await PrepareContextAsync(context, ct);
            int need = Math.Max(1, data.Minimum);
            var picks = new List<int>();
            if (_tributePlan is { } plan && plan.Turn == context.Header.CurrentTurn)
            {
                foreach (var (location, sequence) in plan.Cards)
                {
                    int i = Array.FindIndex(cards, card => card.CurrentCoordinates.Location == location && card.CurrentCoordinates.Sequence == sequence);
                    if (i >= 0 && !picks.Contains(i) && picks.Count < need)
                        picks.Add(i);
                }
                if (picks.Count > 0)
                    context.Bot.LogNote($"deck brain: tribute for {plan.What} as planned");
                _tributePlan = null;
            }
            foreach (int i in Enumerable.Range(0, cards.Length).OrderBy(i => TributeCost(cards[i])))
            {
                if (picks.Count >= need)
                    break;
                if (!picks.Contains(i))
                    picks.Add(i);
            }
            context.Bot.LogNote($"deck brain: tribute {string.Join(" + ", picks.Select(i => $"{CardNames.Get(cards[i].ID)} (cost {TributeCost(cards[i]):0.#})"))}");
            return picks.Select(i => (byte)i).ToList();
        }

        /// <summary>A tribute asked for as a card selection (some costs use the release hint): the same choice.</summary>
        protected IList<byte>? SelectTributeCard(DecisionContext<SelectCardData> context)
        {
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            if ((DHint)state.SelectionHint != DHint.RELEASE || cards.Length == 0 || !cards.All(card => card.CurrentCoordinates.Controller == 0))
                return null;
            int need = Math.Max(1, state.Minimum);
            // The planned tributes first, as for a tribute prompt (seen 2026-11-10: Substitoad's cost comes as a card selection,
            // the plan "tribute Substitoad itself for the second Dupe Frog" was ignored, and the cheapest, the first Dupe, went).
            var picks = new List<int>();
            if (_tributePlan is { } plan && plan.Turn == context.Header.CurrentTurn)
            {
                foreach (var (location, sequence) in plan.Cards)
                {
                    int i = Array.FindIndex(cards, card => card.CurrentCoordinates.Location == location && card.CurrentCoordinates.Sequence == sequence);
                    if (i >= 0 && !picks.Contains(i) && picks.Count < need)
                        picks.Add(i);
                }
                if (picks.Count > 0)
                    context.Bot.LogNote($"deck brain: tribute for {plan.What} as planned");
                _tributePlan = null;
            }
            foreach (int i in Enumerable.Range(0, cards.Length).OrderBy(i => TributeCost(cards[i])))
                if (picks.Count < need && !picks.Contains(i))
                    picks.Add(i);
            context.Bot.LogNote($"deck brain: tribute {string.Join(" + ", picks.Select(i => CardNames.Get(cards[i].ID)))}");
            return picks.Select(i => (byte)i).ToList();
        }
    }
}
