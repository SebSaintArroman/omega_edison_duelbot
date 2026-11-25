using BotTest.Game.AI.Brains;
using BotTest.Utils;
using DuelSim.Engine;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// Our side of an imagined line played by our own brain (user, 2026-11-10: sensible lines beat random ones): a twin
    /// of the playing brain (same deck, its own search off) answers our off-tree decisions in a search duel, reading
    /// the board through <see cref="SearchBrainApi"/> instead of the server. One per search thread (brains aren't
    /// thread-safe). Anything it can't answer, or any error, falls back to the quick policy.
    /// </summary>
    internal sealed class SearchBrain
    {
        private readonly BotBrainActionsAdapter _adapter;
        private readonly DuelBotWrapper _bot;
        public SearchBrainApi Api { get; } = new();

        /// <summary>Decisions answered, and how long they took (ticks), for the bench.</summary>
        public static long Decisions, Ticks, Failures;
        /// <summary>The first failure's prompt and error (for the bench), or null.</summary>
        public static string? FirstFailure;

        private readonly DeckBotBrain _twin;
        private readonly bool _useForRollouts;
        private Prompt? _idlePrompt;
        private HashSet<(MainAction Action, int Index)>? _blockedIdleMoves;
        private int _startingTurn;
        private bool _normalSummonUsedAtStart;
        private IReadOnlyCollection<int> _activatedAtStart = Array.Empty<int>();

        public SearchBrain(DeckBotBrain playing, string deckName, string deckHash, bool useForRollouts = true)
        {
            _twin = playing.CreateSearchTwin();
            _useForRollouts = useForRollouts;
            _adapter = new BotBrainActionsAdapter(_twin, deckName, deckHash);
            _bot = new DuelBotWrapper(_adapter, Api);
        }

        /// <summary>
        /// The moves the twin held back at the decision it just answered (Decide), among these option keys: what a line pays for
        /// each (key -> cost, rule). The brain's rules as penalties, at every decision of a line.
        /// </summary>
        public Dictionary<string, (double Cost, string Rule)> HoldsFor(IEnumerable<string> keys) => _twin.HoldsFor(keys);

        /// <summary>Points this brain at the line being played (call before each line, on the search thread).</summary>
        public void Attach(DuelHost host, int turnOffset, int startingTurn, bool normalSummonUsedAtStart,
            IReadOnlyCollection<int> activatedAtStart, BoardEvaluator? evaluator = null)
        {
            _idlePrompt = null;
            // The twin's LP / card advantage standing at each position of this line, by the search's own evaluator (StandingState).
            _twin.SearchStanding = evaluator == null ? null : () => StandingState.Assess(evaluator, host.Summary(0), host.Summary(1));
            _startingTurn = startingTurn;
            _normalSummonUsedAtStart = normalSummonUsedAtStart;
            _activatedAtStart = activatedAtStart;
            Api.Host = host;
            Api.TurnOffset = turnOffset;
            Api.Reset();
            _readVersion = -1;
            // Everything the brain reads goes to this line's duel; nothing it says is printed.
            OmegaApiClient.Override.Value = Api;
            ConsoleUtils.Muted = true;
        }

        /// <summary>The twin's decisions that kept the board reads of the one before (same board), and those that read afresh: for the bench.</summary>
        public static long BoardReadsKept, BoardReadsFresh;

        // The board version the twin's board reads were made on (DuelHost.BoardVersion), or -1.
        private long _readVersion = -1;

        /// <summary>
        /// Starts one of the twin's decisions: its board reads (SearchBrainApi's views, the wrapper's and GameField's caches) are dropped
        /// only when the line's board has changed since they were made (line-time audit 2026-11-25: the twin re-read the whole board at
        /// every prompt, twice at a Main Phase prompt, Decide then BlockedIdleMoves). A board changed without the host noticing is caught by
        /// DuelHost's stale-board check.
        /// </summary>
        private BotStateHeader BeginDecision()
        {
            var host = Api.Host!;
            var header = Api.Header();
            if (host.BoardVersion == _readVersion)
            {
                Interlocked.Increment(ref BoardReadsKept);
                _bot.ContinueOfflineDecision(header);
                return header;
            }
            Interlocked.Increment(ref BoardReadsFresh);
            Api.Reset();
            _bot.BeginOfflineDecision(header);
            _readVersion = host.BoardVersion;
            _twin.SearchBoardVersion = _readVersion;
            return header;
        }

        public HashSet<(MainAction Action, int Index)> BlockedIdleMoves(Prompt prompt, IdleCmdData data)
        {
            if (_idlePrompt != prompt)
            {
                var header = BeginDecision();
                var context = new DecisionContext<IdleCmdData>
                {
                    Bot = _bot, State = BotState.SelectIdleCmd, Header = header, StateData = data
                };
                var host = Api.Host!;
                bool atStartingTurn = host.Turn == _startingTurn;
                bool normalSummonUsed = host.SearchNormalSummonOrSetUsed || atStartingTurn && _normalSummonUsedAtStart;
                var activated = host.ActivatedInTurn(0, host.Turn);
                _twin.PrepareIdleMoveRulesAsync(context, normalSummonUsed, atStartingTurn ? activated.Concat(_activatedAtStart) : activated)
                    .GetAwaiter().GetResult();
                _blockedIdleMoves = _twin.BlockedIdleMovesAsync(context, SearchActions.Idle(data).Select(move => move.Response))
                    .GetAwaiter().GetResult();
                _idlePrompt = prompt;
            }
            return _blockedIdleMoves!;
        }

        /// <summary>Our brain's answer to this prompt, as a bot packet, or null to let the policy answer.</summary>
        /// <param name="data">The prompt's options narrowed (SearchActions.WithoutAnswer): asked again after the search refused an answer.</param>
        public byte[]? Decide(Prompt prompt, object? data = null)
        {
            if (!_useForRollouts)
                return null;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                BeginDecision(); // the line may have moved on since the last decision
                var packet = (data ?? prompt.Data) switch
                {
                    IdleCmdData idle => SearchActions.Packet(_adapter.OnIdleCmd(idle).GetAwaiter().GetResult()),
                    BattleCmdData battle => SearchActions.Packet(_adapter.OnBattleCmd(battle).GetAwaiter().GetResult()),
                    SelectCardData select => List(_adapter.OnSelectCard(select).GetAwaiter().GetResult()),
                    SelectTributeData tribute => List(_adapter.OnSelectTribute(tribute).GetAwaiter().GetResult()),
                    SelectSumData sum => List(_adapter.OnSelectSum(sum).GetAwaiter().GetResult()),
                    SelectUnselectCardData unselect => List(_adapter.OnSelectUnselectCard(unselect).GetAwaiter().GetResult()),
                    SelectChainData chain => BitConverter.GetBytes(_adapter.OnSelectChain(chain).GetAwaiter().GetResult()),
                    SelectEffectYnData yn => new[] { (byte)(_adapter.OnSelectEffectYn(yn).GetAwaiter().GetResult() ? 1 : 0) },
                    SelectYesNoData yesNo => new[] { (byte)(_adapter.OnSelectYesNo(yesNo).GetAwaiter().GetResult() ? 1 : 0) },
                    SelectOptionData option => BitConverter.GetBytes(_adapter.OnSelectOption(option).GetAwaiter().GetResult()),
                    SelectPositionData position => new[] { (byte)_adapter.OnSelectPosition(position).GetAwaiter().GetResult() },
                    SelectPlaceData place => BitConverter.GetBytes(_adapter.OnSelectPlace(place).GetAwaiter().GetResult()),
                    _ => null,
                };
                Interlocked.Increment(ref Decisions);
                return packet;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Failures);
                FirstFailure ??= $"{prompt.Data?.GetType().Name}: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                Interlocked.Add(ref Ticks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
        }

        // [count][index...] as BotResponseWriter writes a card list; an empty list is a cancel / finish.
        private static byte[]? List(IList<byte>? picks)
        {
            if (picks == null)
                return null;
            var packet = new byte[picks.Count + 1];
            packet[0] = (byte)picks.Count;
            for (int i = 0; i < picks.Count; i++)
                packet[i + 1] = picks[i];
            return packet;
        }
    }
}
