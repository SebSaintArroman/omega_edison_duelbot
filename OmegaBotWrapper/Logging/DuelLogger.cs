using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using System.Text;

namespace OmegaBotWrapper.Logging
{
    /// <summary>
    /// Global switches for duel logging.
    /// </summary>
    public static class DuelLogOptions
    {
        /// <summary>Turns duel logging on or off.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Folder where duel logs are written. Defaults to ./duel-logs under the working directory.</summary>
        public static string Directory { get; set; } = Path.Combine(Environment.CurrentDirectory, "duel-logs");

        /// <summary>
        /// Query each card on the field for ATK/DEF/position (one API call per occupied zone).
        /// Turn off if decisions feel slow.
        /// </summary>
        public static bool CaptureCardDetails { get; set; } = true;

        /// <summary>Include the full raw state data sent by the server in the .jsonl log.</summary>
        public static bool IncludeRawState { get; set; } = true;

        /// <summary>Minimum time between end-of-duel checks while the bot is idle.</summary>
        public static TimeSpan IdleCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Records every decision the bot makes during a duel, together with the board before
    /// the decision and what changed since the previous one. Writes one readable .log file
    /// and one machine-readable .jsonl file per duel.
    /// Logging must never break the bot, so every public method swallows its own errors.
    /// </summary>
    public sealed class DuelLogger : IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = { new StringEnumConverter() }
        };

        private readonly DuelBotWrapper _bot;
        private readonly object _sync = new();
        private readonly List<string> _pendingNotes = new();
        // When each pending note was written (same order as _pendingNotes), for the timestamps in the readable log.
        private readonly List<DateTime> _pendingNoteTimes = new();

        /// <summary>Clock time shown on every line of the readable log and the terminal narration.</summary>
        private static string Stamp(DateTime time) => time.ToString("HH:mm:ss.fff");

        private StreamWriter? _text;
        private StreamWriter? _json;
        private string? _currentBasePath;
        private int _decisionCount;
        private int _lastTurn = -1;
        private bool _duelEnded;
        private BoardSnapshot? _previous;
        private BoardSnapshot? _lastKnown;
        private DateTime _lastIdleCheck = DateTime.MinValue;
        private DateTime _duelStarted;

        public DuelLogger(DuelBotWrapper bot)
        {
            _bot = bot;
        }

        /// <summary>Path (without extension) of the log currently being written, if any.</summary>
        public string? CurrentLogPath => _currentBasePath;

        /// <summary>The number of the latest decision written to the log (its "#NNN").</summary>
        public int DecisionCount => _decisionCount;

        /// <summary>
        /// Raised after every recorded decision with a short summary: the chosen action and the brain's reasons
        /// (notes such as "vayu: ..." or "deck brain: ..."). Hosts print it to the terminal.
        /// </summary>
        public static event Action<string>? DecisionNarrated;

        // Brain reasoning notes (as opposed to scoring dumps and generic console chatter).
        // Brain notes are written as "<lower-case source>: <reason>" ("vayu: ...", "deck brain: ...").
        private static readonly System.Text.RegularExpressions.Regex ReasonNote = new(@"^[a-z][a-z ]*: ", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static bool IsReason(string note) =>
            ReasonNote.IsMatch(note) && !note.StartsWith("brain score", StringComparison.Ordinal);

        /// <summary>
        /// Adds a free-text note to the next recorded decision (brain reasoning, scores, console messages).
        /// </summary>
        public void AddNote(string note)
        {
            if (!DuelLogOptions.Enabled || string.IsNullOrWhiteSpace(note))
                return;
            lock (_sync)
            {
                if (_pendingNotes.Count < 300)
                {
                    _pendingNotes.Add(note.Trim());
                    _pendingNoteTimes.Add(DateTime.Now);
                }
            }
        }

        /// <summary>
        /// Records one decision: the options offered, the response chosen (and the validated response
        /// actually sent if it differs), the brain notes collected since the last decision and the board.
        /// </summary>
        public async Task RecordDecisionAsync(BotState state, object? stateData, object? response, object? sentResponse = null)
        {
            if (!DuelLogOptions.Enabled)
                return;

            try
            {
                var header = _bot.CurrentStateHeader;
                EnsureDuelFile(header);

                // Main/battle decisions always get a fresh, detailed board; other prompts reuse it when nothing moved.
                bool forceDetail = state is BotState.SelectIdleCmd or BotState.SelectBattleCmd;
                BoardSnapshot? board = await CaptureBoardAsync(forceDetail);
                // Look up names for any cards seen for the first time (cached, so this is cheap after the first few turns).
                await CardDatabase.EnsureAsync(IdsIn(board).Concat(IdsIn(stateData)));

                List<string> changes = board != null && _previous != null ? DiffBoards(_previous, board) : new List<string>();

                List<string> notes;
                List<DateTime> noteTimes;
                lock (_sync)
                {
                    notes = new List<string>(_pendingNotes);
                    noteTimes = new List<DateTime>(_pendingNoteTimes);
                    _pendingNotes.Clear();
                    _pendingNoteTimes.Clear();
                }
                var now = DateTime.Now;

                _decisionCount++;
                string options = SafeDescribe(() => DecisionDescriber.DescribeOptions(stateData));
                string chosen = SafeDescribe(() => DecisionDescriber.DescribeResponse(stateData, response));
                string? sent = null;
                if (sentResponse != null && !ResponsesEqual(response, sentResponse))
                    sent = SafeDescribe(() => DecisionDescriber.DescribeResponse(stateData, sentResponse));

                string phase = PhaseName(header.CurrentPhase);
                // What a pass doesn't chain to (user, 2026-11-17: "pass (no chain to <card name>) ... or game action if it's not a
                // card eg pass (no chain to going to end phase)"): a card that came face-up or out since the last decision, else the
                // phase we just moved into. Effects that change nothing on the board can't be seen here: those stay a plain pass.
                if (chosen == "PASS (no chain)" && ChainedTo(_previous, board, _previousPhase, phase) is { } chainedTo)
                    chosen = $"PASS (no chain to {chainedTo})";
                _previousPhase = phase;

                // Readable log
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"#{_decisionCount:000} | {Stamp(now)} | Turn {header.CurrentTurn} | {phase} | turn player {header.CurrentPlayer} | {state}" +
                              (board != null
                                  ? $" | LP bot {board.Bot.LifePoints} / opp {board.Opponent.LifePoints}" +
                                    $" | hands bot {board.Bot.HandCount} / opp {board.Opponent.HandCount}"
                                  : ""));
                if (changes.Count > 0)
                    sb.AppendLine($"  Since last: {string.Join("; ", changes)}");
                if (board != null && (_previous == null || BoardText(board) != BoardText(_previous)))
                {
                    sb.AppendLine($"  BOT (LP {board.Bot.LifePoints})");
                    sb.AppendLine(SideText(board.Bot, isBot: true, indent: "      "));
                    sb.AppendLine($"  OPP (LP {board.Opponent.LifePoints})");
                    sb.AppendLine(SideText(board.Opponent, isBot: false, indent: "      "));
                }
                else if (board != null)
                {
                    sb.AppendLine("  (board unchanged)");
                }
                sb.AppendLine($"  Options: {options}");
                // The generic fallback's "brain score" lines are noise in the readable log (still kept in the .jsonl).
                for (int i = 0; i < notes.Count; i++)
                    // (Rule events are machine data for DuelSim's analyze command: JSONL only.)
                    if (!notes[i].StartsWith("brain score", StringComparison.Ordinal) && !notes[i].StartsWith("rule-event|", StringComparison.Ordinal))
                        sb.AppendLine($"  . [{Stamp(i < noteTimes.Count ? noteTimes[i] : now)}] {notes[i]}");
                sb.AppendLine($"  => [{Stamp(now)}] {chosen}");
                if (sent != null)
                    sb.AppendLine($"  !! [{Stamp(now)}] validator changed the response, actually sent: {sent}");

                // Machine-readable log
                var record = new Dictionary<string, object?>
                {
                    ["type"] = "decision",
                    ["n"] = _decisionCount,
                    ["time"] = DateTimeOffset.Now,
                    ["turn"] = header.CurrentTurn,
                    ["phase"] = phase,
                    ["turnPlayer"] = header.CurrentPlayer,
                    ["lastChainPlayer"] = header.LastChainPlayer,
                    ["lastSummonPlayer"] = header.LastSummonPlayer,
                    ["state"] = state.ToString(),
                    ["options"] = options,
                    ["chosen"] = chosen,
                    ["sent"] = sent,
                    ["notes"] = notes,
                    ["changes"] = changes,
                    ["board"] = board,
                    ["response"] = ForJson(response),
                    ["sentResponse"] = sent != null ? ForJson(sentResponse) : null,
                    ["stateData"] = DuelLogOptions.IncludeRawState ? stateData : null
                };

                Write(sb.ToString(), record);

                // Short "what and why" summary for the terminal: the chosen action plus the brain's reasons.
                var reasons = notes.Where(IsReason).ToList();
                // (Zone choices too: Omega places cards itself, so "ZONE player 0, location 0x4, sequence 2" says nothing; user, 2026-11-16.)
                bool boringPass = state == BotState.SelectChain && reasons.Count == 0 || state == BotState.SelectPlace;
                if (!boringPass)
                {
                    var narration = new StringBuilder();
                    narration.Append($"[{Stamp(now)} T{header.CurrentTurn} {phase}] {chosen}");
                    // First in the expanded entry (user, 2026-11-16): every card in hand and on the field, each marked face-up or face-down.
                    if (board != null)
                    {
                        string Field(SideSnapshot s, bool isBot)
                        {
                            // (No zones: "I don't need to know the zones things are in, just list them", user 2026-11-16.)
                            var cards = s.Monsters.Select(c => FieldCardText(c, isBot))
                                .Concat(s.SpellTraps.Select(c => FieldCardText(c, isBot))).ToList();
                            return cards.Count == 0 ? "-" : string.Join(" · ", cards);
                        }
                        // Every card the bot knows is there (user, 2026-11-17: "are we listing known hand cards here? we should be. if none are
                        // known, say none known"): the brain's deck read when it tells us, else what the server shows.
                        IReadOnlyList<int> knownHand = board.Opponent.Hand;
                        try { if (_bot.KnownOpponentHand?.Invoke() is { } fromBrain) knownHand = fromBrain; } catch { }
                        string theirHand = $"{board.Opponent.HandCount} card(s), " + (knownHand.Count > 0 ? $"known: {string.Join(", ", knownHand.Select(CardNames.Get))}" : "none known");
                        narration.Append($"{Environment.NewLine}    detail our hand: {(board.Bot.Hand.Count == 0 ? "-" : string.Join(", ", board.Bot.Hand.Select(CardNames.Get)))}");
                        narration.Append($"{Environment.NewLine}    detail our field: {Field(board.Bot, true)}");
                        narration.Append($"{Environment.NewLine}    detail their hand: {theirHand}");
                        narration.Append($"{Environment.NewLine}    detail their field: {Field(board.Opponent, false)}");
                    }
                    foreach (var reason in reasons)
                        narration.Append($"{Environment.NewLine}    because {reason}");
                    if (sent != null)
                        narration.Append($"{Environment.NewLine}    (validator changed it to: {sent})");
                    // A summon or monster set: the monsters it could have played instead (user, 2026-11-12: "when a summon is
                    // made, please also list all the monsters in the hand below").
                    if (state == BotState.SelectIdleCmd && (chosen.StartsWith("NORMAL SUMMON") || chosen.StartsWith("SPECIAL SUMMON") || chosen.StartsWith("SET MONSTER")))
                    {
                        var offered = options.Split(" | ")
                            .Where(part => part.StartsWith("Summon:") || part.StartsWith("SpSummon:") || part.StartsWith("Set monster:"))
                            .Where(part => !part.EndsWith(": -"))
                            .ToList();
                        if (offered.Count > 0)
                            narration.Append($"{Environment.NewLine}    monsters on offer: {string.Join("; ", offered)}");
                    }
                    // Detail for the page's expandable entries (user, 2026-11-16: "a summary line on each action and then click in for
                    // detail about decision time, reasoning, cards in hand, what set cards are"): one "detail <what>: ..." line each.
                    var start = noteTimes.Count > 0 ? noteTimes[0] : now;
                    // Plain words (user, 2026-11-16: "tidy this please to make more human readable").
                    narration.Append($"{Environment.NewLine}    detail decision: #{_decisionCount} · {PromptName(state)} · took {(now - start).TotalSeconds:0.0} s");
                    if (board != null)
                    {
                        string Piles(SideSnapshot s) => $"{s.LifePoints} LP · {s.Grave.Count} in the graveyard · {s.Banished.Count} banished · {s.DeckCount} in the deck · {s.ExtraCount} in the Extra Deck";
                        narration.Append($"{Environment.NewLine}    detail ours: {Piles(board.Bot)}");
                        narration.Append($"{Environment.NewLine}    detail theirs: {Piles(board.Opponent)}");
                    }
                    if (TidyOptions(options) is string tidy)
                        narration.Append($"{Environment.NewLine}    detail could have: {tidy}");
                    // (Not every note: the deck brain's and the search's bookkeeping stays in the .log file, user 2026-11-16.)
                    DecisionNarrated?.Invoke(narration.ToString());
                }

                if (board != null)
                {
                    _previous = board;
                    _lastKnown = board;
                }
                _lastTurn = header.CurrentTurn;

                // A snapshot already showing 0 LP ends the duel (the idle check may never see it).
                if (board != null && (board.Bot.LifePoints <= 0 || board.Opponent.LifePoints <= 0))
                    EndDuel(board.Bot.LifePoints <= 0 && board.Opponent.LifePoints <= 0 ? "DRAW"
                            : board.Bot.LifePoints <= 0 ? "BOT LOST" : "BOT WON", "life points reached 0");
            }
            catch (Exception ex)
            {
                BotLogger.Warn(_bot.BotID, $"[DuelLog] Failed to record decision: {ex.Message}");
            }
        }

        /// <summary>
        /// Called while the bot has nothing to decide. Periodically checks life points so the
        /// end of the duel (and the winner) can be recorded.
        /// </summary>
        public async Task OnIdleAsync()
        {
            if (!DuelLogOptions.Enabled || _text == null || _duelEnded)
                return;
            if (DateTime.UtcNow - _lastIdleCheck < DuelLogOptions.IdleCheckInterval)
                return;
            _lastIdleCheck = DateTime.UtcNow;

            try
            {
                var field = await _bot.GetBotFieldInfo();
                int botLp = field.OwnSide.LifePoints;
                int oppLp = field.EnemySide.LifePoints;
                // Both 0 usually means the field was already cleared after the duel, not a draw: keep the last known LP.
                if (botLp == 0 && oppLp == 0)
                    return;
                if (_lastKnown != null)
                {
                    _lastKnown.Bot.LifePoints = botLp;
                    _lastKnown.Opponent.LifePoints = oppLp;
                }

                if (botLp <= 0 || oppLp <= 0)
                    EndDuel(botLp <= 0 && oppLp <= 0 ? "DRAW" : botLp <= 0 ? "BOT LOST" : "BOT WON", "life points reached 0");
            }
            catch
            {
                // Field info may be unavailable between duels; ignore.
            }
        }

        /// <summary>Writes the duel summary (if a duel is open) and closes the files.</summary>
        public void Dispose() => EndDuel(null, "bot stopped");

        private void EndDuel(string? result, string reason)
        {
            lock (_sync)
            {
                if (_text == null || _duelEnded)
                {
                    CloseFiles();
                    return;
                }

                _duelEnded = true;
                string finalResult = result ?? GuessResult();
                var duration = DateTime.Now - _duelStarted;

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("==================================================================");
                sb.AppendLine($"DUEL END [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]: {finalResult} ({reason})");
                sb.AppendLine($"Decisions: {_decisionCount} | Last turn seen: {_lastTurn} | Duration: {duration:hh\\:mm\\:ss}");
                if (_lastKnown != null)
                    sb.AppendLine($"Final LP: bot {_lastKnown.Bot.LifePoints} / opp {_lastKnown.Opponent.LifePoints}");
                sb.AppendLine("==================================================================");

                var record = new Dictionary<string, object?>
                {
                    ["type"] = "duel_end",
                    ["time"] = DateTimeOffset.Now,
                    ["result"] = finalResult,
                    ["reason"] = reason,
                    ["decisions"] = _decisionCount,
                    ["lastTurn"] = _lastTurn,
                    ["botLp"] = _lastKnown?.Bot.LifePoints,
                    ["oppLp"] = _lastKnown?.Opponent.LifePoints
                };

                WriteUnlocked(sb.ToString(), record);
                BotLogger.Log(_bot.BotID, "DuelLog", $"Duel log saved: {_currentBasePath}.log");
                CloseFiles();
            }
        }

        private string GuessResult()
        {
            if (_lastKnown == null)
                return "UNKNOWN";
            if (_lastKnown.Bot.LifePoints <= 0)
                return "BOT LOST";
            if (_lastKnown.Opponent.LifePoints <= 0)
                return "BOT WON";
            // The final blow wasn't seen: give the best guess from the last known life points.
            int bot = _lastKnown.Bot.LifePoints, opp = _lastKnown.Opponent.LifePoints;
            if (bot == opp)
                return $"UNKNOWN (last seen LP bot {bot} / opp {opp})";
            return $"PROBABLY {(bot < opp ? "BOT LOST" : "BOT WON")} (not seen to the end; last seen LP bot {bot} / opp {opp})";
        }

        private void EnsureDuelFile(BotStateHeader header)
        {
            lock (_sync)
            {
                // A turn counter going backwards means a new duel (e.g. rematch) started.
                bool newDuel = _text == null || _duelEnded || (_lastTurn > 1 && header.CurrentTurn < _lastTurn);
                if (!newDuel)
                    return;

                // lock is re-entrant, so EndDuel can take _sync again here.
                if (_text != null && !_duelEnded)
                    EndDuel(null, "new duel detected");

                System.IO.Directory.CreateDirectory(DuelLogOptions.Directory);
                string deck = string.Concat((_bot.DeckName ?? "deck").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                _currentBasePath = Path.Combine(DuelLogOptions.Directory, $"{stamp}_{deck}");
                _text = new StreamWriter(_currentBasePath + ".log", append: true, Encoding.UTF8) { AutoFlush = true };
                _json = new StreamWriter(_currentBasePath + ".jsonl", append: true, Encoding.UTF8) { AutoFlush = true };

                _decisionCount = 0;
                _lastTurn = -1;
                _duelEnded = false;
                _previous = null;
                _lastKnown = null;
                _duelStarted = DateTime.Now;

                string headerText =
                    "==================================================================\n" +
                    $"DUEL LOG | deck: {_bot.DeckName} | bot id: {_bot.BotID} | started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    "Each entry: what the bot was offered, its reasoning notes (.) and its choice (=>).\n" +
                    "'Since last' shows what happened between two decisions.\n" +
                    "==================================================================";

                WriteUnlocked(headerText, new Dictionary<string, object?>
                {
                    ["type"] = "duel_start",
                    ["time"] = DateTimeOffset.Now,
                    ["deck"] = _bot.DeckName,
                    ["botId"] = _bot.BotID
                });

                BotLogger.Log(_bot.BotID, "DuelLog", $"Logging duel to {_currentBasePath}.log");
            }
        }

        private void Write(string text, object record)
        {
            lock (_sync)
                WriteUnlocked(text, record);
        }

        private void WriteUnlocked(string text, object record)
        {
            _text?.Write(text.EndsWith('\n') ? text : text + Environment.NewLine);
            _json?.WriteLine(JsonConvert.SerializeObject(record, JsonSettings));
        }

        private void CloseFiles()
        {
            _text?.Dispose();
            _json?.Dispose();
            _text = null;
            _json = null;
        }

        private static string SafeDescribe(Func<string> describe)
        {
            try { return describe(); }
            catch (Exception ex) { return $"(could not describe: {ex.Message})"; }
        }

        private static IEnumerable<int> IdsIn(BoardSnapshot? board)
        {
            if (board == null)
                yield break;
            foreach (var side in new[] { board.Bot, board.Opponent })
            {
                foreach (var card in side.Monsters.Concat(side.SpellTraps))
                    yield return card.Id;
                foreach (int id in side.Hand.Concat(side.Grave).Concat(side.Banished))
                    yield return id;
            }
        }

        // Ids of cards in the prompt; revealed hand cards only carry their id in Alias.
        private static IEnumerable<int> IdsIn(object? stateData)
        {
            IEnumerable<DynamicCard> cards = stateData switch
            {
                IdleCmdData d => Concat(d.SummonableCards, d.SpecialSummonableCards, d.ReposableCards, d.MonsterSetableCards, d.SpellSetableCards, d.ActivableCards),
                BattleCmdData d => Concat(d.AttackableCards, d.ActivableCards),
                SelectChainData d => Concat(d.SelectableCards),
                SelectCardData d => Concat(d.SelectableCards),
                SelectUnselectCardData d => Concat(d.SelectableCards),
                SelectTributeData d => Concat(d.SelectableCards),
                SelectSumData d => Concat(d.SelectableCards, d.MandatoryCards),
                SortCardData d => Concat(d.SelectableCards),
                SelectEffectYnData d => new[] { d.EffectCard },
                _ => Array.Empty<DynamicCard>()
            };
            foreach (var card in cards)
            {
                if (card.ID > 0)
                    yield return card.ID;
                else if (card.CurrentCoordinates.Location == (byte)CardLocation.Hand && card.Alias > 0)
                    yield return card.Alias;
            }

            static IEnumerable<DynamicCard> Concat(params DynamicCard[]?[] arrays) =>
                arrays.Where(a => a != null).SelectMany(a => a!);
        }

        // Byte lists would otherwise serialize as base64; log selection indexes as plain numbers.
        private static object? ForJson(object? response) =>
            response is IEnumerable<byte> bytes ? bytes.Select(b => (int)b).ToArray() : response;

        private static bool ResponsesEqual(object? a, object? b)
        {
            if (a == null || b == null)
                return a == b;
            if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb && a is not string)
                return ea.Cast<object>().SequenceEqual(eb.Cast<object>());
            return a.Equals(b);
        }

        private static string PhaseName(int phase) =>
            System.Enum.IsDefined(typeof(DuelPhase), phase) ? ((DuelPhase)phase).ToString() : $"phase 0x{phase:X}";

        // ---------------------------------------------------------------- board snapshot

        public sealed class BoardSnapshot
        {
            public SideSnapshot Bot { get; set; } = new();
            public SideSnapshot Opponent { get; set; } = new();
        }

        public sealed class SideSnapshot
        {
            public int LifePoints { get; set; }
            public List<CardSnapshot> Monsters { get; set; } = new();
            public List<CardSnapshot> SpellTraps { get; set; } = new();
            public List<int> Hand { get; set; } = new();
            public int HandCount { get; set; }
            public List<int> Grave { get; set; } = new();
            public List<int> Banished { get; set; } = new();
            public int DeckCount { get; set; }
            public int ExtraCount { get; set; }
            public bool UnderAttack { get; set; }
        }

        public sealed class CardSnapshot
        {
            public int Zone { get; set; }
            public int Id { get; set; }
            public string Name { get; set; } = "?";
            public bool FaceUp { get; set; }
            public string Position { get; set; } = "";
            public int? Attack { get; set; }
            public int? Defense { get; set; }
            public int? BaseAttack { get; set; }
            public int? BaseDefense { get; set; }
            public int? Level { get; set; }
            public bool Disabled { get; set; }
            public int Materials { get; set; }
            public bool Attacked { get; set; }
            public bool Detailed { get; set; }
        }

        private string? _lastFieldSignature;

        private async Task<BoardSnapshot?> CaptureBoardAsync(bool forceDetail)
        {
            FieldInfo field;
            try
            {
                field = await _bot.GetBotFieldInfo();
            }
            catch
            {
                return null;
            }

            // Per-card queries are the expensive part (one API call each). If the field summary
            // (ids, LP, hand/grave/banish lists, attack flags) is identical to last time, reuse the snapshot.
            string signature = JsonConvert.SerializeObject(field);
            if (!forceDetail && _previous != null && signature == _lastFieldSignature)
                return _previous;
            _lastFieldSignature = signature;

            return new BoardSnapshot
            {
                Bot = await CaptureSideAsync(field.OwnSide, controller: 0, isBot: true),
                Opponent = await CaptureSideAsync(field.EnemySide, controller: 1, isBot: false)
            };
        }

        private async Task<SideSnapshot> CaptureSideAsync(CardInfo side, int controller, bool isBot)
        {
            var snapshot = new SideSnapshot
            {
                LifePoints = side.LifePoints,
                HandCount = side.Hand?.Length ?? 0,
                Hand = isBot ? (side.Hand ?? Array.Empty<int>()).ToList() : (side.Hand ?? Array.Empty<int>()).Where(id => id > 0).ToList(),
                Grave = (side.Grave ?? Array.Empty<int>()).ToList(),
                Banished = (side.Banish ?? Array.Empty<int>()).ToList(),
                DeckCount = side.Main?.Length ?? 0,
                ExtraCount = side.Extra?.Length ?? 0,
                UnderAttack = side.UnderAttack
            };

            snapshot.Monsters = await CaptureZonesAsync(side.MonsterZones, controller, CardLocation.MonsterZone);
            snapshot.SpellTraps = await CaptureZonesAsync(side.SpellZones, controller, CardLocation.SpellZone);
            return snapshot;
        }

        private async Task<List<CardSnapshot>> CaptureZonesAsync(CardArray[]? zones, int controller, CardLocation location)
        {
            var result = new List<CardSnapshot>();
            if (zones == null)
                return result;

            for (int zone = 0; zone < zones.Length; zone++)
            {
                int[] ids = zones[zone].Cards ?? Array.Empty<int>();
                bool hasId = ids.Any(cardId => cardId > 0);
                // Without per-card queries we can only see zones that report an id.
                // The bot's own cards always report an id, so only opponent zones need probing for face-down cards.
                if (ids.Length == 0 || (!hasId && (!DuelLogOptions.CaptureCardDetails || controller == 0)))
                    continue;

                int id = ids.FirstOrDefault(cardId => cardId > 0);
                var snap = new CardSnapshot
                {
                    Zone = zone,
                    Id = id,
                    Name = CardNames.Get(id),
                    Materials = Math.Max(0, ids.Length - 1)
                };

                if (DuelLogOptions.CaptureCardDetails)
                {
                    try
                    {
                        var card = await _bot.GetBotFieldCardInfo(controller, (int)location, zone);
                        if (card.ID > 0 || card.Position != 0)
                        {
                            snap.Id = card.ID > 0 ? card.ID : id;
                            snap.Name = CardNames.Get(snap.Id);
                            snap.FaceUp = card.IsFaceup();
                            snap.Position = location == CardLocation.MonsterZone
                                ? DecisionDescriber.Position(card.Position)
                                : (card.IsFaceup() ? "face-up" : "set");
                            if (location == CardLocation.MonsterZone && (card.IsFaceup() || controller == 0))
                            {
                                snap.Attack = card.Attack;
                                snap.Defense = card.Defense;
                                snap.BaseAttack = card.BaseAttack;
                                snap.BaseDefense = card.BaseDefense;
                                snap.Level = card.Level != 0 ? card.Level : card.Rank;
                            }
                            snap.Disabled = card.IsDisabled();
                            snap.Attacked = card.Attacked;
                            if (card.Overlays != null)
                                snap.Materials = card.Overlays.Length;
                            snap.Detailed = true;
                        }
                    }
                    catch
                    {
                        // Keep the id-only snapshot.
                    }
                }

                // Empty zones come back as id 0 with no position; face-down cards as id 0 with a position.
                if (snap.Id > 0 || !string.IsNullOrEmpty(snap.Position))
                    result.Add(snap);
            }

            return result;
        }

        private static string CardText(CardSnapshot c, bool isBot)
        {
            // Show the opponent's face-down cards by name when the server reveals the ID (useful in review).
            string name = c.Id <= 0
                ? "face-down card"
                : (!isBot && c.Detailed && !c.FaceUp ? $"face-down {c.Name}" : c.Name);

            var parts = new List<string>();
            if (c.Attack.HasValue)
                parts.Add($"{c.Attack}/{c.Defense}");
            if (!string.IsNullOrEmpty(c.Position))
                parts.Add(c.Position);
            if (c.Materials > 0)
                parts.Add($"{c.Materials} mat");
            if (c.Disabled)
                parts.Add("negated");
            if (c.Attacked)
                parts.Add("attacked");
            return parts.Count > 0 ? $"{name} ({string.Join(", ", parts)})" : name;
        }

        /// <summary>A prompt's kind in plain words, for the page's expanded entry.</summary>
        private static string PromptName(BotState state) => state switch
        {
            BotState.SelectIdleCmd => "main phase",
            BotState.SelectBattleCmd => "battle phase",
            BotState.SelectChain => "chain window",
            BotState.SelectCard => "card pick",
            BotState.SelectTribute => "tribute pick",
            BotState.SelectUnselectCard => "card pick",
            BotState.SelectEffectYn => "yes / no",
            BotState.SelectYesNo => "yes / no",
            BotState.SelectOption => "option pick",
            BotState.SelectPosition => "position",
            BotState.SelectPlace => "zone",
            _ => state.ToString(),
        };

        /// <summary>
        /// The options line in plain words: the empty groups and the yes/no flags dropped, the card decorations ([ATK/DEF], (bot M3),
        /// effect numbers) stripped, one group per " · ". Null when nothing is left.
        /// </summary>
        private static string? TidyOptions(string options)
        {
            if (string.IsNullOrWhiteSpace(options))
                return null;
            var groups = new List<string>();
            foreach (var raw in options.Split(" | "))
            {
                string part = raw.Trim();
                if (part.EndsWith(": -") || part.EndsWith(": no"))
                    continue;
                if (part.EndsWith(": yes"))
                {
                    string flag = part[..^5];
                    groups.Add(flag switch { "Battle" => "go to the Battle Phase", "M2" => "go to Main Phase 2", "End" => "end the turn", _ => flag });
                    continue;
                }
                part = System.Text.RegularExpressions.Regex.Replace(part, @"\s*\[\d+/\d+\]", "");
                part = System.Text.RegularExpressions.Regex.Replace(part, @"\s*\((bot|opp) (hand|[MS]\d|GY|deck|banished|extra)\)", "");
                part = System.Text.RegularExpressions.Regex.Replace(part, @"\s*<effect #\d+ of [^>]*>", "'s effect");
                part = System.Text.RegularExpressions.Regex.Replace(part, @"\s*<[^>]*>", "");
                groups.Add(part);
            }
            return groups.Count == 0 ? null : string.Join(" · ", groups);
        }

        /// <summary>A field card for the page's expanded entry: its name (ours always; theirs when known), whether it's face-up or
        /// face-down, and its stats and position when face-up.</summary>
        private static string FieldCardText(CardSnapshot c, bool isBot)
        {
            // (User, 2026-11-16: "you can just say atk, def, or set as the three possible positions ... if its stats are at their
            // normal stat totals you don't need to report them. also unknown card can just say unknown.")
            if (c.Id <= 0)
                return "unknown";
            var parts = new List<string>();
            if (!c.FaceUp)
                parts.Add("set");
            else if (c.Position.Contains("ATK"))
                parts.Add("atk");
            else if (c.Position.Contains("DEF"))
                parts.Add("def");
            if (c.FaceUp && c.Attack.HasValue && (c.Attack != c.BaseAttack || c.Defense != c.BaseDefense))
                parts.Add($"{c.Attack}/{c.Defense}");
            if (c.Disabled)
                parts.Add("negated");
            if (c.Attacked)
                parts.Add("attacked");
            return parts.Count == 0 ? c.Name : $"{c.Name} ({string.Join(", ", parts)})";
        }

        /// <summary>Multi-line, indented description of one side of the field.</summary>
        private static string SideText(SideSnapshot s, bool isBot, string indent = "")
        {
            string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(CardNames.Get));

            string monsters = s.Monsters.Count == 0
                ? "-"
                : string.Join(" | ", s.Monsters.Select(m => $"{ZoneLabels.Get(CardLocation.MonsterZone, m.Zone)}: {CardText(m, isBot)}"));
            string spells = s.SpellTraps.Count == 0
                ? "-"
                : string.Join(" | ", s.SpellTraps.Select(m => $"{ZoneLabels.Get(CardLocation.SpellZone, m.Zone)}: {CardText(m, isBot)}"));
            string hand = isBot
                ? $"Hand ({s.HandCount}): {(s.Hand.Count == 0 ? "-" : Names(s.Hand))}"
                : $"Hand ({s.HandCount})" + (s.Hand.Count > 0 ? $", known: {Names(s.Hand)}" : "");
            string grave = $"GY ({s.Grave.Count}): {(s.Grave.Count == 0 ? "-" : Names(s.Grave))}";
            string banished = $"Banished ({s.Banished.Count}): {(s.Banished.Count == 0 ? "-" : Names(s.Banished))}";

            var sb = new StringBuilder();
            sb.AppendLine($"{indent}Monsters: {monsters}");
            sb.AppendLine($"{indent}S/T:      {spells}");
            sb.AppendLine($"{indent}{hand}");
            sb.Append($"{indent}{grave} | {banished} | Deck {s.DeckCount} | Extra {s.ExtraCount}");
            return sb.ToString();
        }

        private static string BoardText(BoardSnapshot b) => SideText(b.Bot, true) + "\n" + SideText(b.Opponent, false);

        private string? _previousPhase;

        /// <summary>What a chain window just opened on: the newest face-up spell/trap or monster (either side), else a phase change.</summary>
        private static string? ChainedTo(BoardSnapshot? before, BoardSnapshot? after, string? previousPhase, string phase)
        {
            if (before != null && after != null)
            {
                static IEnumerable<CardSnapshot> New(List<CardSnapshot> was, List<CardSnapshot> now) =>
                    now.Where(c => c.Id > 0 && c.FaceUp && !was.Any(w => w.Zone == c.Zone && w.Id == c.Id && w.FaceUp));
                var spell = New(before.Opponent.SpellTraps, after.Opponent.SpellTraps).Concat(New(before.Bot.SpellTraps, after.Bot.SpellTraps)).FirstOrDefault();
                if (spell != null)
                    return spell.Name;
                var monster = New(before.Opponent.Monsters, after.Opponent.Monsters).Concat(New(before.Bot.Monsters, after.Bot.Monsters)).FirstOrDefault();
                if (monster != null)
                    return $"the summon of {monster.Name}";
            }
            string Words(string p) => p switch
            {
                "Draw" => "the Draw Phase", "Standby" => "the Standby Phase", "Main1" => "Main Phase 1", "BattleStart" => "the Battle Phase",
                "BattleStep" => "the battle step", "Damage" => "the damage step", "DamageCal" => "damage calculation",
                "Battle" => "the end of the Battle Phase", "Main2" => "Main Phase 2", "End" => "the End Phase", _ => p,
            };
            return previousPhase != null && previousPhase != phase ? $"going to {Words(phase)}" : null;
        }

        private static List<string> DiffBoards(BoardSnapshot before, BoardSnapshot after)
        {
            var changes = new List<string>();
            DiffSide("bot", before.Bot, after.Bot, isBot: true, changes);
            DiffSide("opp", before.Opponent, after.Opponent, isBot: false, changes);
            return changes;
        }

        private static void DiffSide(string who, SideSnapshot before, SideSnapshot after, bool isBot, List<string> changes)
        {
            int lp = after.LifePoints - before.LifePoints;
            if (lp != 0)
                changes.Add($"{who} LP {(lp > 0 ? "+" : "")}{lp} (now {after.LifePoints})");

            DiffList($"{who} monsters", before.Monsters.Select(m => CardText(m, isBot)), after.Monsters.Select(m => CardText(m, isBot)), changes);
            DiffList($"{who} S/T", before.SpellTraps.Select(m => CardText(m, isBot)), after.SpellTraps.Select(m => CardText(m, isBot)), changes);
            DiffList($"{who} GY", before.Grave.Select(CardNames.Get), after.Grave.Select(CardNames.Get), changes, onlyAdded: true);
            DiffList($"{who} banished", before.Banished.Select(CardNames.Get), after.Banished.Select(CardNames.Get), changes, onlyAdded: true);

            int hand = after.HandCount - before.HandCount;
            if (hand > 0 && isBot)
            {
                // Our draws (and cards returned to the hand) by name, with the whole hand after (user, 2026-11-12).
                var remaining = before.Hand.ToList();
                var drawn = after.Hand.Where(id => !remaining.Remove(id)).ToList();
                changes.Add($"{who} hand +{hand}" + (drawn.Count > 0 ? $": {string.Join(", ", drawn.Select(CardNames.Get))}" : "") +
                            $" (hand now: {string.Join(", ", after.Hand.Select(CardNames.Get))})");
            }
            else if (hand != 0)
                changes.Add($"{who} hand {(hand > 0 ? "+" : "")}{hand} (now {after.HandCount})");
        }

        private static void DiffList(string label, IEnumerable<string> before, IEnumerable<string> after, List<string> changes, bool onlyAdded = false)
        {
            var remaining = before.ToList();
            var added = new List<string>();
            foreach (var item in after)
            {
                if (!remaining.Remove(item))
                    added.Add(item);
            }

            if (added.Count > 0)
                changes.Add($"{label} +[{string.Join(", ", added)}]");
            if (!onlyAdded && remaining.Count > 0)
                changes.Add($"{label} -[{string.Join(", ", remaining)}]");
        }
    }
}
