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

        public SearchBrain(DeckBotBrain playing, string deckName, string deckHash)
        {
            _twin = playing.CreateSearchTwin();
            _adapter = new BotBrainActionsAdapter(_twin, deckName, deckHash);
            _bot = new DuelBotWrapper(_adapter, Api);
        }

        /// <summary>
        /// The moves the twin held back at the decision it just answered (Decide), among these option keys: what a line pays for
        /// each (key -> cost, rule). The brain's rules as penalties, at every decision of a line.
        /// </summary>
        public Dictionary<string, (double Cost, string Rule)> HoldsFor(IEnumerable<string> keys) => _twin.HoldsFor(keys);

        /// <summary>Points this brain at the line being played (call before each line, on the search thread).</summary>
        public void Attach(DuelHost host, int turnOffset)
        {
            Api.Host = host;
            Api.TurnOffset = turnOffset;
            Api.Reset();
            // Everything the brain reads goes to this line's duel; nothing it says is printed.
            OmegaApiClient.Override.Value = Api;
            ConsoleUtils.Muted = true;
        }

        /// <summary>Our brain's answer to this prompt, as a bot packet, or null to let the policy answer.</summary>
        public byte[]? Decide(Prompt prompt)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Api.Reset(); // the line moved on since the last decision
                _bot.BeginOfflineDecision(Api.Header());
                var packet = prompt.Data switch
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
