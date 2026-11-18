using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace OmegaBotWrapper.Heuristics
{
    /// <summary>
    /// Allows a heuristic brain to plug into the existing IBotActions pipeline.
    /// Existing decks can stay as-is; new decks can opt into IBotBrain directly.
    /// </summary>
    public sealed class BotBrainActionsAdapter : IBotActions
    {
        private readonly IBotBrain _brain;

        public BotBrainActionsAdapter(IBotBrain brain, string? deckName = null, string? deckHash = null)
        {
            _brain = brain;
            DeckName = deckName;
            DeckHash = deckHash;
        }

        private DuelBotWrapper? _botWrapper;

        public DuelBotWrapper? BotWrapper
        {
            get => _botWrapper;
            set
            {
                if (_botWrapper != null)
                {
                    _botWrapper.ResponseSent -= OnResponseSent;
                    _botWrapper.TurnStarted = null;
                    _botWrapper.PromptArrived = null;
                }
                _botWrapper = value;
                if (value != null)
                {
                    value.ResponseSent += OnResponseSent;
                    // From the start, so turn 1 has its snapshot too (the brain's first prompt can come after turn 1 began).
                    if (_brain is IDecisionRefiner refiner)
                    {
                        value.TurnStarted = header => refiner.TurnStartedAsync(value, header);
                        value.PromptArrived = (header, state) => refiner.PromptArrivedAsync(value, header, state);
                    }
                }
            }
        }

        private void OnResponseSent(BotState state, object? data, byte[] response) =>
            (_brain as IDecisionRefiner)?.ResponseSent(state, data, response);

        public string? DeckName { get; set; }

        public string? DeckHash { get; set; }

        public async Task<BattleCmdResponse> OnBattleCmd(BattleCmdData state)
        {
            (_brain as IDecisionRefiner)?.BeginDecision();
            var context = CreateContext(BotState.SelectBattleCmd, state, includeFieldSnapshot: true);
            // The turn plan's step, if one is being followed: the brain's rules had their say when the plan was chosen.
            if (_brain is IDecisionRefiner planner && await planner.PlannedBattleAsync(context) is BattleCmdResponse planned)
                return planned;
            var choice = await _brain.OnBattleCmdAsync(context);
            return _brain is IDecisionRefiner refiner ? await refiner.RefineBattleAsync(context, choice) : choice;
        }

        public async Task<IdleCmdResponse> OnIdleCmd(IdleCmdData state)
        {
            (_brain as IDecisionRefiner)?.BeginDecision();
            var context = CreateContext(BotState.SelectIdleCmd, state, includeFieldSnapshot: true);
            if (_brain is IDecisionRefiner planner && await planner.PlannedIdleAsync(context) is IdleCmdResponse planned)
                return planned;
            var choice = await _brain.OnIdleCmdAsync(context);
            return _brain is IDecisionRefiner refiner ? await refiner.RefineIdleAsync(context, choice) : choice;
        }

        public async Task<int> OnSelectChain(SelectChainData state)
        {
            var context = CreateContext(BotState.SelectChain, state);
            int index = await _brain.OnSelectChainAsync(context);
            // The play search replans mid-chain when something new was revealed (DeckBotBrain.MidChain).
            if (_brain is IDecisionRefiner chainRefiner && await chainRefiner.RefineChainAsync(context, index) is int refined)
                index = refined;
            (_brain as IDecisionRefiner)?.ChainAnswered(context, index);
            return index;
        }

        // A pick re-planned by the play search (something new revealed since the plan), before the plan and the brain.
        private async Task<IList<byte>?> RefinedPick(BotState state, object data) =>
            _brain is IDecisionRefiner refiner ? await refiner.RefinePickAsync(state, data) : null;

        public async Task<IList<byte>?> OnSelectCard(SelectCardData state) =>
            await RefinedPick(BotState.SelectCard, state) ??
            (Planned(BotState.SelectCard, state.SelectableCards, state.Minimum, state.Maximum, state.Cancelable) is { } planned
                ? planned
                : await _brain.OnSelectCardAsync(CreateContext(BotState.SelectCard, state, includeFieldSnapshot: true)));

        public async Task<IList<byte>?> OnSelectTribute(SelectTributeData state) =>
            await RefinedPick(BotState.SelectTribute, state) ??
            (Planned(BotState.SelectTribute, state.SelectableCards, state.Minimum, state.Maximum, state.Cancelable) is { } planned
                ? planned
                : await _brain.OnSelectTributeAsync(CreateContext(BotState.SelectTribute, state)));

        public Task<IList<byte>?> OnSelectSum(SelectSumData state) =>
            _brain.OnSelectSumAsync(CreateContext(BotState.SelectSum, state));

        public Task<IList<CardAttribute>> OnAnnounceAttribute(AnnounceAttributeData state) =>
            _brain.OnAnnounceAttributeAsync(CreateContext(BotState.AnnounceAttribute, state));

        public Task<IList<CardRace>> OnAnnounceRace(AnnounceRaceData state) =>
            _brain.OnAnnounceRaceAsync(CreateContext(BotState.AnnounceRace, state));

        public Task<int> OnAnnounceNumber(AnnounceNumberData state) =>
            _brain.OnAnnounceNumberAsync(CreateContext(BotState.AnnounceNumber, state));

        public Task<int> OnAnnounceCard(AnnounceCardData state) =>
            _brain.OnAnnounceCardAsync(CreateContext(BotState.AnnounceCard, state));

        public Task<int> OnAnnounceCardFilter(AnnounceCardFilterData state) =>
            _brain.OnAnnounceCardFilterAsync(CreateContext(BotState.AnnounceCardFilter, state));

        public async Task<IList<byte>?> OnSelectUnselectCard(SelectUnselectCardData state) =>
            await RefinedPick(BotState.SelectUnselectCard, state) ??
            (Planned(BotState.SelectUnselectCard, state.SelectableCards, Math.Min(state.Minimum, 1), 1, state.Minimum == 0 || state.Cancelable) is { } planned
                ? planned
                : await _brain.OnSelectUnselectCardAsync(CreateContext(BotState.SelectUnselectCard, state)));

        public Task<IList<short>> OnSelectCounter(SelectCounterData state) =>
            _brain.OnSelectCounterAsync(CreateContext(BotState.SelectCounter, state));

        public Task<int> OnSelectDisfield(SelectDisfieldData state) =>
            _brain.OnSelectDisfieldAsync(CreateContext(BotState.SelectDisfield, state));

        public Task<int> OnSelectPlace(SelectPlaceData state) =>
            _brain.OnSelectPlaceAsync(CreateContext(BotState.SelectPlace, state));

        public async Task<bool> OnSelectEffectYn(SelectEffectYnData state)
        {
            bool used = (_brain as IDecisionRefiner)?.PlannedEffectYn(state) is bool planned
                ? planned
                : await SearchedYesNo(BotState.SelectEffectYn) ?? await _brain.OnSelectEffectYnAsync(CreateContext(BotState.SelectEffectYn, state));
            (_brain as IDecisionRefiner)?.EffectYnAnswered(state, used);
            return used;
        }

        private static readonly string[] YesNoKeys = { "yes", "no" };

        /// <summary>The search's yes/no where no plan answers (IDecisionRefiner.SearchedChoiceAsync), or null.</summary>
        private async Task<bool?> SearchedYesNo(BotState state) =>
            _brain is IDecisionRefiner refiner && await refiner.SearchedChoiceAsync(state, YesNoKeys) is string key ? key == "yes" : null;

        public async Task<bool> OnSelectYesNo(SelectYesNoData state) =>
            (_brain as IDecisionRefiner)?.PlannedYesNo(BotState.SelectYesNo) is bool planned
                ? planned
                : state.Description != 31 && await SearchedYesNo(BotState.SelectYesNo) is bool searched ? searched // (31: "attack directly?", always yes)
                : await _brain.OnSelectYesNoAsync(CreateContext(BotState.SelectYesNo, state));

        public async Task<int> OnSelectOption(SelectOptionData state)
        {
            var options = state.Options ?? Array.Empty<int>();
            if ((_brain as IDecisionRefiner)?.PlannedOption(options) is int planned)
                return planned;
            if (_brain is IDecisionRefiner refiner && await refiner.SearchedChoiceAsync(BotState.SelectOption, options.Select(o => $"option:{o}").ToList()) is string key)
            {
                int index = Array.FindIndex(options, o => $"option:{o}" == key);
                if (index >= 0)
                    return index;
            }
            return await _brain.OnSelectOptionAsync(CreateContext(BotState.SelectOption, state));
        }

        public Task<byte> OnSelectRockPaperScissors() =>
            _brain.OnSelectRockPaperScissorsAsync();

        public async Task<CardPosition> OnSelectPosition(SelectPositionData state)
        {
            // The plan's position, else the search's (positions are decisions of the search's tree), else the brain's.
            var keys = (state.Positions ?? Array.Empty<byte>()).Select(p => $"pos:{p}").ToList();
            if (keys.Count > 1 && _brain is IDecisionRefiner refiner)
            {
                string? key = refiner.PlannedChoice(BotState.SelectPosition, keys) ?? await refiner.SearchedChoiceAsync(BotState.SelectPosition, keys);
                if (key != null && byte.TryParse(key.AsSpan(4), out byte position))
                    return (CardPosition)position;
            }
            return await _brain.OnSelectPositionAsync(CreateContext(BotState.SelectPosition, state));
        }

        public Task<IList<byte>> OnSortCard(SortCardData state) =>
            _brain.OnSortCardAsync(CreateContext(BotState.SortCard, state));

        // A planned card selection from a refining brain (the search's line), if it has one for this prompt.
        private IList<byte>? Planned(BotState state, DynamicCard[]? cards, int minimum, int maximum, bool cancelable) =>
            _brain is IDecisionRefiner refiner && cards is { Length: > 0 } ? refiner.PlannedCards(state, cards, minimum, maximum, cancelable) : null;

        private DecisionContext<TState> CreateContext<TState>(BotState state, TState stateData, bool includeFieldSnapshot = false)
        {
            if (BotWrapper == null)
                throw new InvalidOperationException("BotWrapper must be assigned before invoking brain actions.");

            FieldInfo? field = null;
            if (includeFieldSnapshot)
            {
                try
                {
                    field = BotWrapper.GetBotFieldInfo().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    BotLogger.Warn(BotWrapper.BotID, $"[{state}] Failed to fetch optional field snapshot for brain evaluation: {ex.Message}");
                }
            }

            return new DecisionContext<TState>
            {
                Bot = BotWrapper,
                State = state,
                Header = BotWrapper.CurrentStateHeader,
                StateData = stateData,
                Field = field
            };
        }
    }
}
