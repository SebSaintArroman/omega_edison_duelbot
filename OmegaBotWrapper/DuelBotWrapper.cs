using OmegaBotWrapper.Struct;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Global wrapper to access the duel bot functionality
    /// </summary>
    public class DuelBotWrapper
    {
        private readonly OmegaApiClient _api;
        private readonly BotStateDispatcher _dispatcher;

        /// <summary>
        /// Which bot this instance is connected to
        /// </summary>
        public int BotID { get; private set; }
        /// <summary>
        /// Name of the deck the bot is currently assigned to
        /// </summary>
        public string DeckName { get; private set; }
        /// <summary>
        /// Deck code (hash) of the deck the bot is currently assigned to
        /// </summary>
        public string DeckHash { get; private set; }
        /// <summary>
        /// Per-duel decision log (created when Start is called). Null when logging is disabled or the bot is not running.
        /// </summary>
        public DuelLogger? DuelLog { get; private set; }
        /// <summary>
        /// Adds a reasoning note to the next decision written to the duel log. Safe to call at any time.
        /// </summary>
        public void LogNote(string note) => DuelLog?.AddNote(note);
        /// <summary>
        /// Called on the first poll that shows a new turn (CurrentTurn changed), before that poll's prompt is answered: the brain's turn-start
        /// snapshot (the search replays the turn from it). Null: nobody listening.
        /// </summary>
        public Func<BotStateHeader, Task>? TurnStarted { get; set; }
        /// <summary>Called at every prompt, before it is answered (whatever then answers it). Null: nobody listening.</summary>
        public Func<BotStateHeader, BotState, Task>? PromptArrived { get; set; }
        private int _lastTurnSeen;

        /// <summary>The cards the brain knows are in the opponent's hand (its deck read), for the log's "their hand" line. Null: none told.</summary>
        public Func<IReadOnlyList<int>>? KnownOpponentHand { get; set; }
        /// <summary>
        /// Creates a new bot wrapper with given deck
        /// </summary>
        public DuelBotWrapper(string deckName, string deckHash, OmegaApiClient? api = null)
        {
            DeckName = deckName;
            DeckHash = deckHash;
            BotID = -1;
            _api = api ?? OmegaApiClient.Default;
            _dispatcher = new BotStateDispatcher(this);
        }
        /// <summary>
        /// Creates a new bot wrapper backed by a heuristic brain while preserving the existing action pipeline.
        /// </summary>
        public DuelBotWrapper(string deckName, string deckHash, IBotBrain botBrain, OmegaApiClient? api = null)
            : this(new BotBrainActionsAdapter(botBrain, deckName, deckHash), api)
        {
        }
        /// <summary>
        /// Creates a new bot wrapper with given set of actions
        /// </summary>
        public DuelBotWrapper(IBotActions botActions, OmegaApiClient? api = null)
        {
            botActions.BotWrapper = this;
            DeckName = botActions.DeckName ?? string.Empty;
            DeckHash = botActions.DeckHash ?? string.Empty;
            BotID = -1;
            _api = api ?? OmegaApiClient.Default;
            _dispatcher = new BotStateDispatcher(this);
            botActions.RegisterActions(this);
        }
        /// <summary>
        /// Connects the bot to the given client bot slot
        /// </summary>
        /// <param name="slot">Slot of the bot that this instance will control or -1 for first available</param>
        /// <returns>True if the bot was found and connection was realized or False in case there was no slot available or the given slot was already taken or non-existant</returns>
        public async Task<bool> Connect(int slot = -1, CancellationToken ct = default)
        {
            var botList = await _api.GetBotList(ct);
            if (botList.BotCount <= 0)
                return false;
            if (slot >= 0)
            {
                if (botList.BotCount <= slot || botList.Bots[slot].DeckIndex != 0)
                    return false;
                BotID = botList.Bots[slot].BotID;
            }
            else
            {
                foreach (var bot in botList.Bots)
                {
                    if (bot.DeckIndex != 0)
                        continue;
                    var botState = await _api.GetBotState(bot.BotID, ct);
                    if (botState.CurrentStateHeader.CurrentStateCode != (byte)BotState.WaitingForDeck || botState.CurrentStateData.IsReady)
                        continue;
                    BotID = bot.BotID;
                    break;
                }
            }
            if (BotID < 0)
                return false;
            // A slot was found: a failure from here on is Omega refusing this deck, not "no slot yet" (seen 2026-11-13: random
            // deck selection never played Hero Frog or Gladiators, which looked like waiting for a slot).
            bool accepted = await _api.SetBotData(BotID, DeckName, DeckHash.ToSafeB64(), ct);
            LastConnectProblem = accepted ? null : $"Omega refused the deck \"{DeckName}\" (bot slot {BotID} was free)";
            if (!accepted)
                BotLogger.Log(BotID, "System", LastConnectProblem!);
            return accepted;
        }

        /// <summary>Why the last Connect failed after finding a free slot (the deck was refused), or null.</summary>
        public string? LastConnectProblem { get; private set; }
        /// <summary>
        /// Starts all the bot procedures
        /// </summary>
        /// <returns>Will only return once the bot has finished executing</returns>
        /// <summary>
        /// Milliseconds between polls of the server for a new decision. Omega: Const.HEARTBEAT_DELAY (50). A local simulator
        /// (DuelSim) holds each poll until there's a decision (long-poll), so it sets this to 0: the 50 ms sleeps were most of
        /// each simulated duel's time.
        /// </summary>
        public static int HeartbeatDelayMs { get; set; } = Const.HEARTBEAT_DELAY;

        public async Task Start(CancellationToken ct = default)
        {
            DuelLog = DuelLogOptions.Enabled ? new DuelLogger(this) : null;
            try
            {
                while (!ct.IsCancellationRequested && await Heartbeat(ct))
                {
                    if (HeartbeatDelayMs > 0)
                        await Task.Delay(HeartbeatDelayMs, ct);
                    else
                        await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                BotLogger.Log(BotID, "System", "Bot stopped by cancellation.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(BotID, "Heartbeat failed", ex);
            }
            finally
            {
                DuelLog?.Dispose();
                BotLogger.Log(BotID, "System", "Bot has finished execution");
            }
        }
        /// <summary>An answer was sent: the prompt's state, its data (null when the handler logs none) and the bytes sent.</summary>
        public event Action<BotState, object?, byte[]>? ResponseSent;
        /// <summary>
        /// Action to be taken at battle phase, should return the action and its index (or 0 if not applicable)
        /// </summary>
        public Func<BattleCmdData, Task<BattleCmdResponse>>? OnBattleCmdAction { get; set; }
        /// <summary>
        /// Action to be taken at main phase, should return the action and its index (or 0 if not applicable)
        /// </summary>
        public Func<IdleCmdData, Task<IdleCmdResponse>>? OnIdleCmdAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting something to chain, should return the index of the selected chain to activate or, if allowed, -1 for none
        /// </summary>
        public Func<SelectChainData, Task<int>>? OnSelectChainAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting card(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectCardData, Task<IList<byte>?>>? OnSelectCardAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting tribute(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectTributeData, Task<IList<byte>?>>? OnSelectTributeAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting a sum (eg. synchro/ritual), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectSumData, Task<IList<byte>?>>? OnSelectSumAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing attribute(s), should return the attribute(s) selected
        /// </summary>
        public Func<AnnounceAttributeData, Task<IList<CardAttribute>>>? OnAnnounceAttributeAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing race(s), should return the race(s) selected
        /// </summary>
        public Func<AnnounceRaceData, Task<IList<CardRace>>>? OnAnnounceRaceAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a number, should return the index of the number selected from the selectable options
        /// </summary>
        public Func<AnnounceNumberData, Task<int>>? OnAnnounceNumberAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a card, should return the card id that was selected
        /// </summary>
        public Func<AnnounceCardData, Task<int>>? OnAnnounceCardAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a card with filter, should return the card id that was selected
        /// </summary>
        public Func<AnnounceCardFilterData, Task<int>>? OnAnnounceCardFilterAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting/unselecting card(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectUnselectCardData, Task<IList<byte>?>>? OnSelectUnselectCardAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting counter(s), should return the amount of counters picked from each of the indexes that was present on the selectable options
        /// </summary>
        public Func<SelectCounterData, Task<IList<short>>>? OnSelectCounterAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting field zone for disabling, should return the integer representing the zone selected (use the 'Zones' class helper for values)
        /// </summary>
        public Func<SelectDisfieldData, Task<int>>? OnSelectDisfieldAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting field zone, should return the integer representing the zone selected (use the 'Zones' class helper for values)
        /// </summary>
        public Func<SelectPlaceData, Task<int>>? OnSelectPlaceAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting yes or no for an effect, should return true or false
        /// </summary>
        public Func<SelectEffectYnData, Task<bool>>? OnSelectEffectYnAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting yes or no, should return true or false
        /// </summary>
        public Func<SelectYesNoData, Task<bool>>? OnSelectYesNoAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting an option, should return the index of the selected option
        /// </summary>
        public Func<SelectOptionData, Task<int>>? OnSelectOptionAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting a rock paper scissors option, should return 1 = scissors, 2 = rock, 3 = paper 
        /// </summary>
        public Func<Task<byte>>? OnSelectRockPaperScissors { get; set; }
        /// <summary>
        /// Action to be taken when selecting position, should return the position selected
        /// </summary>
        public Func<SelectPositionData, Task<CardPosition>>? OnSelectPositionAction { get; set; }
        /// <summary>
        /// Action to be taken when sorting cards, should return the indexes of the selectable cards in the desired order
        /// </summary>
        public Func<SortCardData, Task<IList<byte>>>? OnSortCardAction { get; set; }
        public BotStateHeader CurrentStateHeader;
        /// <summary>
        /// Starts a decision without the server heartbeat (the play search drives a brain through an imagined line): field
        /// data cached for the last decision is dropped, and the header is the given one.
        /// </summary>
        public void BeginOfflineDecision(BotStateHeader header)
        {
            lock (_fieldCacheLock)
                _fieldGeneration++;
            CurrentStateHeader = header;
        }
        /// <summary>
        /// Main loop of actions from the bot
        /// </summary>
        /// <returns>Will return True everytime the bot finishes one loop without error, or False otherwise</returns>
        private async Task<bool> Heartbeat(CancellationToken ct = default)
        {
            // New heartbeat, new decision: field data cached during the last one is stale now.
            lock (_fieldCacheLock)
                _fieldGeneration++;
            CurrentStateHeader = await _api.GetBotStateHeader(BotID, ct);
            var state = (BotState)CurrentStateHeader.CurrentStateCode;
            // A new turn seen (the header is read every poll, prompt or not): the brain takes its turn-start snapshot before anything else.
            if (CurrentStateHeader.CurrentTurn > 0 && CurrentStateHeader.CurrentTurn != _lastTurnSeen)
            {
                _lastTurnSeen = CurrentStateHeader.CurrentTurn;
                if (TurnStarted != null)
                {
                    try { await TurnStarted(CurrentStateHeader); }
                    catch (Exception ex) { BotLogger.Error(BotID, "turn-start snapshot failed", ex); }
                }
            }
            // Every prompt, before anything answers it (the brain's turn record: its checkpoints, whichever path then answers).
            if (PromptArrived != null && state is not (BotState.None or BotState.WaitingForDeck or BotState.RockPaperScissors))
            {
                try { await PromptArrived(CurrentStateHeader, state); }
                catch (Exception ex) { BotLogger.Error(BotID, "prompt record failed", ex); }
            }
            var payload = await _dispatcher.DispatchAsync(state, ct);
            if (payload != null)
            {
                // Every answer we send, with the prompt it answered (the play search replays our turn from its last decision).
                try { ResponseSent?.Invoke(state, _dispatcher.LastData, payload); }
                catch { /* a listener must never cost the bot its answer */ }
            }
            if (payload == null)
            {
                if (DuelLog != null && state is BotState.None or BotState.WaitingForDeck)
                    await DuelLog.OnIdleAsync();
                return true;
            }
            return await SendResponse(payload, ct);
        }
        /// <summary>
        /// Queries information about the bot side of the field
        /// </summary>
        /// <returns>Structure containing the data</returns>
        // Per-decision cache of the bot's field. Nothing on the field changes while the bot is deciding (one heartbeat:
        // read the state, decide, log, respond), but the brains and the duel log ask for the field and individual cards
        // dozens of times per decision, and each ask is an HTTP call to Omega. A new heartbeat starts a new generation,
        // so every decision still sees a fresh field.
        private int _fieldGeneration;
        private readonly object _fieldCacheLock = new();

        /// <summary>The current decision's number: changes whenever the field may have changed (a new heartbeat or offline decision).</summary>
        public int FieldGeneration
        {
            get { lock (_fieldCacheLock) return _fieldGeneration; }
        }
        private (int Generation, Task<FieldInfo> Info)? _fieldCache;
        private readonly Dictionary<(int Controller, int Location, int Sequence), Task<DynamicCard>> _cardCache = new();
        private int _cardCacheGeneration = -1;

        public Task<FieldInfo> GetBotFieldInfo()
        {
            lock (_fieldCacheLock)
            {
                if (_fieldCache is { } cached && cached.Generation == _fieldGeneration && !cached.Info.IsFaulted && !cached.Info.IsCanceled)
                    return cached.Info;
                var fetch = _api.GetBotFieldInfo(BotID);
                _fieldCache = (_fieldGeneration, fetch);
                return fetch;
            }
        }

        public static async Task<FieldInfo> GetPlayerFieldInfo() =>
            await OmegaApiClient.Current.GetPlayerFieldInfo();

        public static async Task<DynamicCard> GetPlayerFieldCardInfo(int controller, int location, int sequence) =>
            await OmegaApiClient.Current.GetPlayerFieldCardInfo(controller, location, sequence);

        public Task<DynamicCard> GetBotFieldCardInfo(int controller, int location, int sequence)
        {
            lock (_fieldCacheLock)
            {
                if (_cardCacheGeneration != _fieldGeneration)
                {
                    _cardCache.Clear();
                    _cardCacheGeneration = _fieldGeneration;
                }
                var key = (controller, location, sequence);
                if (_cardCache.TryGetValue(key, out var cached) && !cached.IsFaulted && !cached.IsCanceled)
                    return cached;
                var fetch = FetchCardAsync();
                _cardCache[key] = fetch;
                return fetch;
            }

            async Task<DynamicCard> FetchCardAsync()
            {
                var card = await _api.GetBotFieldCardInfo(BotID, controller, location, sequence);
                // Visible cards carry their official id as Alias: lets the card database resolve server-only ids.
                if (card.ID > 0)
                    CardDatabase.RegisterAlias(card.ID, card.Alias);
                return card;
            }
        }

        public static async Task<DeckData> GetPlayerDeck() =>
            await OmegaApiClient.Current.GetPlayerDeck();

        internal Task<T> GetBotStateAs<T>(CancellationToken ct = default) =>
            _api.GetBotStateAs<T>(BotID, ct);

        internal async Task<bool> SendResponse(byte[] data, CancellationToken ct = default)
        {
            string encoded = data.Base64Encode().ToSafeB64();
            return await _api.SendBotResponse(BotID, encoded, ct);
        }

        public async Task<bool> SendResponse(BinaryWriter pkt)
        {
            byte[] data = ((MemoryStream)pkt.BaseStream).ToArray();
            return await SendResponse(data);
        }

        public static async Task<DBCardList> GetMatchingCards(params int[] filters) =>
            await OmegaApiClient.Current.GetMatchingCards(filters);
        /// <summary>
        /// While on end screen on rematchable duel it will send the rematch request as if you tapped rematch
        /// </summary>
        /// <returns>Rematch Duel</returns>
        public static async Task<string> RematchDuel() =>
            await OmegaApiClient.Current.RematchDuel();

        internal void LogBattleCmdState(BattleCmdData data)
        {
            BotLogger.Log(BotID, "BattleCmd", $"Attackable={data.AttackableCards?.Length ?? 0} Activable={data.ActivableCards?.Length ?? 0} CanM2={data.CanMainPhaseTwo} CanEnd={data.CanEndPhase}");
            if (data.AttackableCards != null)
            {
                for (int i = 0; i < data.AttackableCards.Length; i++)
                {
                    var c = data.AttackableCards[i];
                    int attackIdx = (c.ActionIndex != null && c.ActionIndex.Length > (int)Enum.BattleAction.Attack)
                        ? c.ActionIndex[(int)Enum.BattleAction.Attack]
                        : -1;
                    BotLogger.Log(BotID, "BattleCmd", $"  [{i}] ID={c.ID} ATK={c.Attack} Zone={c.CurrentCoordinates.Sequence} ActionIndex[Attack]={attackIdx} Attacked={c.Attacked} DirectOK={c.CanDirectAttack}");
                }
            }
        }
    }
}
