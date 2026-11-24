using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Resolves bot states to handlers, preserving the existing behavior while keeping DuelBotWrapper lean.
    /// </summary>
    internal sealed class BotStateDispatcher
    {
        private delegate Task<byte[]?> StateHandler(CancellationToken ct);

        private readonly DuelBotWrapper _bot;
        private readonly IReadOnlyDictionary<BotState, StateHandler> _handlers;

        public BotStateDispatcher(DuelBotWrapper bot)
        {
            _bot = bot;
            _handlers = new Dictionary<BotState, StateHandler>
            {
                [BotState.SelectBattleCmd] = HandleBattleCmdAsync,
                [BotState.SelectIdleCmd] = HandleIdleCmdAsync,
                [BotState.SelectCard] = HandleSelectCardAsync,
                [BotState.SelectUnselectCard] = HandleSelectUnselectCardAsync,
                [BotState.SelectChain] = HandleSelectChainAsync,
                [BotState.SelectCounter] = HandleSelectCounterAsync,
                [BotState.SelectDisfield] = HandleSelectDisfieldAsync,
                [BotState.SelectEffectYn] = HandleSelectEffectYnAsync,
                [BotState.SelectOption] = HandleSelectOptionAsync,
                [BotState.SelectPlace] = HandleSelectPlaceAsync,
                [BotState.SelectPosition] = HandleSelectPositionAsync,
                [BotState.SelectSum] = HandleSelectSumAsync,
                [BotState.SelectTribute] = HandleSelectTributeAsync,
                [BotState.AnnounceAttribute] = HandleAnnounceAttributeAsync,
                [BotState.AnnounceCard] = HandleAnnounceCardAsync,
                [BotState.AnnounceNumber] = HandleAnnounceNumberAsync,
                [BotState.AnnounceRace] = HandleAnnounceRaceAsync,
                [BotState.AnnounceCardFilter] = HandleAnnounceCardFilterAsync,
                [BotState.SelectYesNo] = HandleSelectYesNoAsync,
                [BotState.RockPaperScissors] = HandleRockPaperScissorsAsync,
                [BotState.SortCard] = HandleSortCardAsync,
                [BotState.WaitingForDeck] = NoResponseAsync,
                [BotState.None] = NoResponseAsync
            };
        }

        public Task<byte[]?> DispatchAsync(BotState state, CancellationToken ct = default)
        {
            LastData = null;
            if (_handlers.TryGetValue(state, out var handler))
                return handler(ct);

            BotLogger.Warn(_bot.BotID, $"[{state}] No dispatcher registered, skipping.");
            return Task.FromResult<byte[]?>(null);
        }

        private static Task<byte[]?> NoResponseAsync(CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        /// <summary>
        /// Writes the decision to the duel log (if enabled). <paramref name="chosen"/> is what the bot answered,
        /// <paramref name="sent"/> is what was actually sent after validation.
        /// </summary>
        private Task LogDecisionAsync(BotState state, object? data, object? chosen, object? sent = null)
        {
            LastData = data;
            return _bot.DuelLog?.RecordDecisionAsync(state, data, chosen, sent) ?? Task.CompletedTask;
        }

        /// <summary>The data of the prompt last answered (for DuelBotWrapper.ResponseSent).</summary>
        public object? LastData { get; private set; }

        private async Task<byte[]?> HandleBattleCmdAsync(CancellationToken ct)
        {
            if (_bot.OnBattleCmdAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectBattleCmd}] No handler registered, skipping.");
                return null;
            }

            var battleState = await _bot.GetBotStateAs<BattleCmdState>(ct);
            ActivationIndexRepair.Repair(battleState.CurrentStateData.ActivableCards, battleState.CurrentStateData.ActivableDescriptions);
            _bot.LogBattleCmdState(battleState.CurrentStateData);
            var battleChosen = await _bot.OnBattleCmdAction(battleState.CurrentStateData);
            var battleResponse = BotResponseValidator.ValidateBattleCmd(
                battleChosen,
                battleState.CurrentStateData, _bot.BotID);
            BotLogger.Log(_bot.BotID, "BattleCmd", $"Response -> Action={battleResponse.Action}({(int)battleResponse.Action}) Index={battleResponse.Index}");
            await LogDecisionAsync(BotState.SelectBattleCmd, battleState.CurrentStateData, battleChosen, battleResponse);

            return BotResponseWriter.WriteBattleCmd(battleResponse);
        }

        private async Task<byte[]?> HandleIdleCmdAsync(CancellationToken ct)
        {
            if (_bot.OnIdleCmdAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectIdleCmd}] No handler registered, skipping.");
                return null;
            }

            var idleState = await _bot.GetBotStateAs<IdleCmdState>(ct);
            ActivationIndexRepair.Repair(idleState.CurrentStateData.ActivableCards, idleState.CurrentStateData.ActivableDescriptions);
            var idleChosen = await _bot.OnIdleCmdAction(idleState.CurrentStateData);
            var idleResponse = BotResponseValidator.ValidateIdleCmd(
                idleChosen,
                idleState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SelectIdleCmd, idleState.CurrentStateData, idleChosen, idleResponse);
            return BotResponseWriter.WriteIdleCmd(idleResponse);
        }

        private async Task<byte[]?> HandleSelectCardAsync(CancellationToken ct)
        {
            if (_bot.OnSelectCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectCard}] No handler registered, skipping.");
                return null;
            }

            var selectCardState = await _bot.GetBotStateAs<SelectCardState>(ct);
            var selectCardData = selectCardState.CurrentStateData;
            var selectCardChosen = await _bot.OnSelectCardAction(selectCardData) ?? Array.Empty<byte>();
            var selectCardResponse = BotResponseValidator.ValidateCardSelection(
                selectCardChosen,
                selectCardData.Minimum, selectCardData.Maximum, selectCardData.SelectableCards?.Length ?? 0,
                "SelectCard", _bot.BotID);
            await LogDecisionAsync(BotState.SelectCard, selectCardData, selectCardChosen, selectCardResponse);
            return BotResponseWriter.WriteCardSelection(selectCardResponse);
        }

        private async Task<byte[]?> HandleSelectUnselectCardAsync(CancellationToken ct)
        {
            if (_bot.OnSelectUnselectCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectUnselectCard}] No handler registered, skipping.");
                return null;
            }

            var selectUnselectCardState = await _bot.GetBotStateAs<SelectUnselectCardState>(ct);
            var selectUnselectCardData = selectUnselectCardState.CurrentStateData;
            var selectUnselectCardChosen = await _bot.OnSelectUnselectCardAction(selectUnselectCardData) ?? Array.Empty<byte>();
            // Select/unselect answers ONE card per prompt (the prompt repeats until the selection is complete), or none
            // to finish: never pad up to the overall minimum, which sends several cards and stalls the duel.
            var selectUnselectCardResponse = BotResponseValidator.ValidateCardSelection(
                selectUnselectCardChosen,
                Math.Min(1, selectUnselectCardData.Minimum), 1, selectUnselectCardData.SelectableCards?.Length ?? 0,
                "SelectUnselectCard", _bot.BotID);
            await LogDecisionAsync(BotState.SelectUnselectCard, selectUnselectCardData, selectUnselectCardChosen, selectUnselectCardResponse);
            return BotResponseWriter.WriteCardSelection(selectUnselectCardResponse);
        }

        private async Task<byte[]?> HandleSelectChainAsync(CancellationToken ct)
        {
            if (_bot.OnSelectChainAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectChain}] No handler registered, skipping.");
                return null;
            }

            var selectChainState = await _bot.GetBotStateAs<SelectChainState>(ct);
            var selectChainChosen = await _bot.OnSelectChainAction(selectChainState.CurrentStateData);
            var selectChainResponse = BotResponseValidator.ValidateSelectChain(
                selectChainChosen,
                selectChainState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SelectChain, selectChainState.CurrentStateData, selectChainChosen, selectChainResponse);
            return BotResponseWriter.WriteSelectChain(selectChainResponse);
        }

        private async Task<byte[]?> HandleSelectCounterAsync(CancellationToken ct)
        {
            if (_bot.OnSelectCounterAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectCounter}] No handler registered, skipping.");
                return null;
            }

            var selectCounterState = await _bot.GetBotStateAs<SelectCounterState>(ct);
            var selectCounterChosen = await _bot.OnSelectCounterAction(selectCounterState.CurrentStateData);
            var selectCounterResponse = BotResponseValidator.ValidateSelectCounter(
                selectCounterChosen,
                selectCounterState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SelectCounter, selectCounterState.CurrentStateData, selectCounterChosen, selectCounterResponse);
            return BotResponseWriter.WriteSelectCounter(selectCounterResponse);
        }

        private async Task<byte[]?> HandleSelectDisfieldAsync(CancellationToken ct)
        {
            if (_bot.OnSelectDisfieldAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectDisfield}] No handler registered, skipping.");
                return null;
            }

            var selectDisfieldState = await _bot.GetBotStateAs<SelectDisfieldState>(ct);
            var disfieldData = selectDisfieldState.CurrentStateData;
            BotLogger.Log(_bot.BotID, "SelectDisfield", $"CardID={disfieldData.CardID} Player={disfieldData.Player} Location=0x{disfieldData.Location:X2} Filter=0x{disfieldData.Filter:X8}");
            var selectDisfieldChosen = await _bot.OnSelectDisfieldAction(disfieldData);
            var selectDisfieldResponse = BotResponseValidator.ValidateZoneSelection(
                selectDisfieldChosen,
                disfieldData.Filter, disfieldData.Player, (byte)disfieldData.Location,
                "SelectDisfield", _bot.BotID);
            LogZoneSelectionResponse("SelectDisfield", selectDisfieldResponse);
            await LogDecisionAsync(BotState.SelectDisfield, disfieldData, selectDisfieldChosen, selectDisfieldResponse);
            return BotResponseWriter.WriteZoneSelection(selectDisfieldResponse);
        }

        private async Task<byte[]?> HandleSelectEffectYnAsync(CancellationToken ct)
        {
            if (_bot.OnSelectEffectYnAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectEffectYn}] No handler registered, skipping.");
                return null;
            }

            var selectEffectYnState = await _bot.GetBotStateAs<SelectEffectYnState>(ct);
            var selectEffectYnResponse = await _bot.OnSelectEffectYnAction(selectEffectYnState.CurrentStateData);
            await LogDecisionAsync(BotState.SelectEffectYn, selectEffectYnState.CurrentStateData, selectEffectYnResponse);
            return BotResponseWriter.WriteBoolean(selectEffectYnResponse);
        }

        private async Task<byte[]?> HandleSelectOptionAsync(CancellationToken ct)
        {
            if (_bot.OnSelectOptionAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectOption}] No handler registered, skipping.");
                return null;
            }

            var selectOptionState = await _bot.GetBotStateAs<SelectOptionState>(ct);
            var selectedOptionChosen = await _bot.OnSelectOptionAction(selectOptionState.CurrentStateData);
            var selectedOptionResponse = BotResponseValidator.ValidateSelectOption(
                selectedOptionChosen,
                selectOptionState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SelectOption, selectOptionState.CurrentStateData, selectedOptionChosen, selectedOptionResponse);
            return BotResponseWriter.WriteSelectOption(selectedOptionResponse);
        }

        private async Task<byte[]?> HandleSelectPlaceAsync(CancellationToken ct)
        {
            if (_bot.OnSelectPlaceAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectPlace}] No handler registered, skipping.");
                return null;
            }

            var selectPlaceState = await _bot.GetBotStateAs<SelectPlaceState>(ct);
            var placeData = selectPlaceState.CurrentStateData;
            BotLogger.Log(_bot.BotID, "SelectPlace", $"CardID={placeData.CardID} Player={placeData.Player} Location=0x{placeData.Location:X2} Filter=0x{placeData.Filter:X8}");
            var selectPlaceChosen = await _bot.OnSelectPlaceAction(placeData);
            var selectPlaceResponse = BotResponseValidator.ValidateZoneSelection(
                selectPlaceChosen,
                placeData.Filter, placeData.Player, (byte)placeData.Location,
                "SelectPlace", _bot.BotID);
            LogZoneSelectionResponse("SelectPlace", selectPlaceResponse);
            await LogDecisionAsync(BotState.SelectPlace, placeData, selectPlaceChosen, selectPlaceResponse);
            // Omega's AI_Custom passes this int to PostSelectPlace, which (like WindBot) expects a
            // zone BITMASK (1 << seq), not [player, location, seq]. Otherwise it falls back to the
            // default order 2,1,3,0,4.
            int selectPlaceMask = 1 << BitConverter.GetBytes(selectPlaceResponse)[2];
            BotLogger.Log(_bot.BotID, "SelectPlace", $"Sending zone mask=0x{selectPlaceMask:X2} (seq {BitConverter.GetBytes(selectPlaceResponse)[2]})");
            return BotResponseWriter.WriteZoneSelection(selectPlaceMask);
        }

        private async Task<byte[]?> HandleSelectPositionAsync(CancellationToken ct)
        {
            if (_bot.OnSelectPositionAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectPosition}] No handler registered, skipping.");
                return null;
            }

            var selectPositionState = await _bot.GetBotStateAs<SelectPositionState>(ct);
            var selectPositionChosen = await _bot.OnSelectPositionAction(selectPositionState.CurrentStateData);
            var selectPositionResponse = BotResponseValidator.ValidateSelectPosition(
                selectPositionChosen,
                selectPositionState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SelectPosition, selectPositionState.CurrentStateData, selectPositionChosen, selectPositionResponse);
            return BotResponseWriter.WriteSelectPosition(selectPositionResponse);
        }

        private async Task<byte[]?> HandleSelectSumAsync(CancellationToken ct)
        {
            if (_bot.OnSelectSumAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectSum}] No handler registered, skipping.");
                return null;
            }

            var selectSumState = await _bot.GetBotStateAs<SelectSumState>(ct);
            var selectSumData = selectSumState.CurrentStateData;
            var selectSumChosen = await _bot.OnSelectSumAction(selectSumData) ?? Array.Empty<byte>();
            // "Sum or greater" prompts (e.g. Machina Fortress: discard Levels totalling 8+) come with min/max 0, which
            // means no count limit, not "pick nothing".
            int sumCount = selectSumData.SelectableCards?.Length ?? 0;
            int sumMax = selectSumData.Maximum > 0 ? selectSumData.Maximum : sumCount;
            var selectSumResponse = BotResponseValidator.ValidateCardSelection(
                selectSumChosen,
                selectSumData.Minimum, sumMax, sumCount,
                "SelectSum", _bot.BotID);
            await LogDecisionAsync(BotState.SelectSum, selectSumData, selectSumChosen, selectSumResponse);
            return BotResponseWriter.WriteCardSelection(selectSumResponse);
        }

        private async Task<byte[]?> HandleSelectTributeAsync(CancellationToken ct)
        {
            if (_bot.OnSelectTributeAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectTribute}] No handler registered, skipping.");
                return null;
            }

            var selectTributeState = await _bot.GetBotStateAs<SelectTributeState>(ct);
            var selectTributeData = selectTributeState.CurrentStateData;
            var selectTributeChosen = await _bot.OnSelectTributeAction(selectTributeData) ?? Array.Empty<byte>();
            var selectTributeResponse = BotResponseValidator.ValidateCardSelection(
                selectTributeChosen,
                selectTributeData.Minimum, selectTributeData.Maximum, selectTributeData.SelectableCards?.Length ?? 0,
                "SelectTribute", _bot.BotID);
            await LogDecisionAsync(BotState.SelectTribute, selectTributeData, selectTributeChosen, selectTributeResponse);
            return BotResponseWriter.WriteCardSelection(selectTributeResponse);
        }

        private async Task<byte[]?> HandleAnnounceAttributeAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceAttributeAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceAttribute}] No handler registered, skipping.");
                return null;
            }

            var announceAttributeState = await _bot.GetBotStateAs<AnnounceAttributeState>(ct);
            var announceAttributeChosen = await _bot.OnAnnounceAttributeAction(announceAttributeState.CurrentStateData);
            var announceAttributeResponse = BotResponseValidator.ValidateAnnounceAttribute(
                announceAttributeChosen,
                announceAttributeState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.AnnounceAttribute, announceAttributeState.CurrentStateData, announceAttributeChosen, announceAttributeResponse);
            return BotResponseWriter.WriteAnnounceAttribute(announceAttributeResponse);
        }

        private async Task<byte[]?> HandleAnnounceCardAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceCard}] No handler registered, skipping.");
                return null;
            }

            var announceCardState = await _bot.GetBotStateAs<AnnounceCardState>(ct);
            var announceCardChosen = await _bot.OnAnnounceCardAction(announceCardState.CurrentStateData);
            var announceCardResponse = BotResponseValidator.ValidateAnnounceCard(
                announceCardChosen,
                "AnnounceCard", _bot.BotID);
            await LogDecisionAsync(BotState.AnnounceCard, announceCardState.CurrentStateData, announceCardChosen, announceCardResponse);
            return BotResponseWriter.WriteAnnounceCard(announceCardResponse);
        }

        private async Task<byte[]?> HandleAnnounceNumberAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceNumberAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceNumber}] No handler registered, skipping.");
                return null;
            }

            var announceNumberState = await _bot.GetBotStateAs<AnnounceNumberState>(ct);
            var announceNumberChosen = await _bot.OnAnnounceNumberAction(announceNumberState.CurrentStateData);
            var announceNumberResponse = BotResponseValidator.ValidateAnnounceNumber(
                announceNumberChosen,
                announceNumberState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.AnnounceNumber, announceNumberState.CurrentStateData, announceNumberChosen, announceNumberResponse);
            // Handlers choose a value, but the engine expects the index of that value in the option list
            // (sending the value itself, e.g. 3 from [3, 2, 1], is out of range and loses the duel).
            int announceNumberIndex = Array.IndexOf(announceNumberState.CurrentStateData.SelectableOptions ?? Array.Empty<int>(), announceNumberResponse);
            return BotResponseWriter.WriteAnnounceNumber(Math.Max(announceNumberIndex, 0));
        }

        private async Task<byte[]?> HandleAnnounceRaceAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceRaceAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceRace}] No handler registered, skipping.");
                return null;
            }

            var announceRaceState = await _bot.GetBotStateAs<AnnounceRaceState>(ct);
            var announceRaceChosen = await _bot.OnAnnounceRaceAction(announceRaceState.CurrentStateData);
            var announceRaceResponse = BotResponseValidator.ValidateAnnounceRace(
                announceRaceChosen,
                announceRaceState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.AnnounceRace, announceRaceState.CurrentStateData, announceRaceChosen, announceRaceResponse);
            return BotResponseWriter.WriteAnnounceRace(announceRaceResponse);
        }

        private async Task<byte[]?> HandleAnnounceCardFilterAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceCardFilterAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceCardFilter}] No handler registered, skipping.");
                return null;
            }

            var announceCardFilterState = await _bot.GetBotStateAs<AnnounceCardFilterState>(ct);
            var announceCardFilterChosen = await _bot.OnAnnounceCardFilterAction(announceCardFilterState.CurrentStateData);
            var announceCardFilterResponse = BotResponseValidator.ValidateAnnounceCard(
                announceCardFilterChosen,
                "AnnounceCardFilter", _bot.BotID);
            await LogDecisionAsync(BotState.AnnounceCardFilter, announceCardFilterState.CurrentStateData, announceCardFilterChosen, announceCardFilterResponse);
            return BotResponseWriter.WriteAnnounceCard(announceCardFilterResponse);
        }

        private async Task<byte[]?> HandleSelectYesNoAsync(CancellationToken ct)
        {
            if (_bot.OnSelectYesNoAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectYesNo}] No handler registered, skipping.");
                return null;
            }

            var selectYesNoState = await _bot.GetBotStateAs<SelectYesNoState>(ct);
            var selectYesNoResponse = await _bot.OnSelectYesNoAction(selectYesNoState.CurrentStateData);
            await LogDecisionAsync(BotState.SelectYesNo, selectYesNoState.CurrentStateData, selectYesNoResponse);
            return BotResponseWriter.WriteBoolean(selectYesNoResponse);
        }

        private async Task<byte[]?> HandleRockPaperScissorsAsync(CancellationToken ct)
        {
            if (_bot.OnSelectRockPaperScissors == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.RockPaperScissors}] No handler registered, skipping.");
                return null;
            }

            var rpsChosen = await _bot.OnSelectRockPaperScissors();
            var rpsResponse = BotResponseValidator.ValidateRockPaperScissors(rpsChosen, _bot.BotID);
            await LogDecisionAsync(BotState.RockPaperScissors, null, rpsChosen, rpsResponse);
            return BotResponseWriter.WriteRockPaperScissors(rpsResponse);
        }

        private async Task<byte[]?> HandleSortCardAsync(CancellationToken ct)
        {
            if (_bot.OnSortCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SortCard}] No handler registered, skipping.");
                return null;
            }

            var sortCardState = await _bot.GetBotStateAs<SortCardState>(ct);
            var sortCardChosen = await _bot.OnSortCardAction(sortCardState.CurrentStateData);
            var sortCardResponse = BotResponseValidator.ValidateSortCard(
                sortCardChosen,
                sortCardState.CurrentStateData, _bot.BotID);
            await LogDecisionAsync(BotState.SortCard, sortCardState.CurrentStateData, sortCardChosen, sortCardResponse);
            return BotResponseWriter.WriteSortCard(sortCardResponse);
        }

        private void LogZoneSelectionResponse(string stateName, int response)
        {
            BotLogger.Log(_bot.BotID, stateName,
                $"Response=0x{response:X8} (bytes: {response & 0xFF},{(response >> 8) & 0xFF},{(response >> 16) & 0xFF},{(response >> 24) & 0xFF})");
        }
    }
}
