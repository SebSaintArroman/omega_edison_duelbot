using Microsoft.Data.Sqlite;

namespace BotTest.Game.AI.Dataset
{
    /// <summary>
    /// Every score the bot's evaluator gives a position at a turn start, from the bot's own view (hidden cards hidden): what the dataset
    /// measures against the duel's result (user, 2026-11-25).
    /// </summary>
    public sealed class PositionScores
    {
        public int Turn;
        public bool OurTurn, BeforeDraw;
        public int LpUs, LpThem;
        public double CardScoreUs, CardScoreThem, CardNet;
        public int HandUs, HandThem, MonstersUs, MonstersThem, SpellsUs, SpellsThem, GravePairs;
        public double HandValueUs, HandValueThem, SetTrapValueUs, SetTrapValueThem, GraveValueUs, GraveValueThem;
        public double BoardDiff, BoardScore;
        public double OurBoard, TheirBoard, Dominance, Threat, OurClock, TheirClock;
        public double? LethalExposure, Explosiveness;
        public string StrategicState = "", Standing = "";
        public double RiskPress, MirrorChance, TorrentialChance, OtherStopChance;
        public int UnknownHand, UnknownSets;
        /// <summary>How long working this record out took (ms): it runs on the live bot's turn-start path.</summary>
        public double RecordMs;
        /// <summary>Every term of the board score by name, top-level and sub-rows ("BoardScoreDelta (ours) :: ...").</summary>
        public Dictionary<string, double> Parts = new();
        /// <summary>Cards the bot can see (live only; DuelSim records the engine's truth instead): side 0 us / 1 them, zone (Zones), code, count.</summary>
        public List<(int Side, int Zone, int Code, int Count)> Cards = new();
    }

    /// <summary>One player's real position at a turn start (DuelSim's engine; never shown to the bots).</summary>
    public sealed record TruthSide(int Lp, int Deck, int Extra, List<(int Zone, int Code, int Count)> Cards);

    /// <summary>A finished DuelSim duel for the dataset. Seat 0 went first; Winner -1 = draw / aborted.</summary>
    public sealed record SimDuel(string Run, int Number, string[] Decks, int[] Versions, string?[] Tunings, int Winner, string Reason, int Turns, bool Error,
        IReadOnlyList<(int Turn, int TurnPlayer, TruthSide[] Sides)> Truth, IReadOnlyDictionary<(int Turn, int Seat), PositionScores> Views);

    /// <summary>
    /// The position dataset: one SQLite database (positions.db) with DuelSim's games (sim_* tables) and live games (live_* tables) apart,
    /// so live play can be compared against self-play. Tables:
    ///  - sim_duels / live_duels: one row per duel, with its result;
    ///  - sim_positions / live_positions: one row per turn start per seat (DuelSim: both seats; live: the bot), every score as a column,
    ///    result = 1 win / 0 loss / 0.5 draw for that seat (live: null when the end wasn't seen; result_text has the logger's guess);
    ///  - sim_parts / live_parts: every board-score term and sub-row per position (part_names holds the names);
    ///  - sim_truth / sim_truth_cards: the engine's real position per turn and player (life points, deck sizes, and each card's count by zone);
    ///  - live_cards: the cards the live bot could see per position (no truth in live games);
    ///  - cards: card names; zones: zone names.
    /// The view sim_position_cards puts the truth beside each position as "us" / "them".
    /// </summary>
    public static class PositionStore
    {
        /// <summary>Zones of the card-count tables.</summary>
        public const int Hand = 0, Monsters = 1, SetMonsters = 2, Spells = 3, SetSpells = 4, Grave = 5, Banished = 6;
        private static readonly string[] ZoneNames = { "hand", "monsters", "set monsters", "spells", "set spells", "grave", "banished" };

        private static readonly object DbLock = new();
        private static string? _path;
        private static bool _ready;

        /// <summary>The database file: set by DuelSim (its dataset folder); otherwise DuelSim/dataset in this repo when found, else beside the duel logs.</summary>
        public static string DatabasePath
        {
            get => _path ??= DefaultPath();
            set { lock (DbLock) { if (_path == value) return; _path = value; _ready = false; PartIds.Clear(); } }
        }

        private static string DefaultPath()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "DuelSim", "DuelSim.csproj")))
                    return Path.Combine(dir.FullName, "DuelSim", "dataset", "positions.db");
            return Path.Combine(OmegaBotWrapper.Logging.DuelLogOptions.Directory, "positions.db");
        }

        // Every score column, shared by sim_positions and live_positions.
        private static readonly (string Name, string Type, Func<PositionScores, object?> Get)[] ScoreColumns =
        {
            ("before_draw", "INTEGER", s => s.BeforeDraw ? 1 : 0),
            ("lp_us", "INTEGER", s => s.LpUs), ("lp_them", "INTEGER", s => s.LpThem),
            ("card_score_us", "REAL", s => s.CardScoreUs), ("card_score_them", "REAL", s => s.CardScoreThem), ("card_net", "REAL", s => s.CardNet),
            ("hand_us", "INTEGER", s => s.HandUs), ("hand_them", "INTEGER", s => s.HandThem),
            ("hand_value_us", "REAL", s => s.HandValueUs), ("hand_value_them", "REAL", s => s.HandValueThem),
            ("monsters_us", "INTEGER", s => s.MonstersUs), ("monsters_them", "INTEGER", s => s.MonstersThem),
            ("spells_us", "INTEGER", s => s.SpellsUs), ("spells_them", "INTEGER", s => s.SpellsThem),
            ("set_trap_value_us", "REAL", s => s.SetTrapValueUs), ("set_trap_value_them", "REAL", s => s.SetTrapValueThem),
            ("grave_value_us", "REAL", s => s.GraveValueUs), ("grave_value_them", "REAL", s => s.GraveValueThem),
            ("grave_pairs", "INTEGER", s => s.GravePairs),
            ("board_diff", "REAL", s => s.BoardDiff), ("board_score", "REAL", s => s.BoardScore),
            ("our_board", "REAL", s => s.OurBoard), ("their_board", "REAL", s => s.TheirBoard),
            ("dominance", "REAL", s => s.Dominance), ("threat", "REAL", s => s.Threat),
            ("our_clock", "REAL", s => s.OurClock), ("their_clock", "REAL", s => s.TheirClock),
            ("lethal_exposure", "REAL", s => s.LethalExposure),
            ("strategic_state", "TEXT", s => s.StrategicState), ("standing", "TEXT", s => s.Standing),
            ("risk_press", "REAL", s => s.RiskPress),
            ("unknown_hand", "INTEGER", s => s.UnknownHand), ("unknown_sets", "INTEGER", s => s.UnknownSets),
            ("mirror_chance", "REAL", s => s.MirrorChance), ("torrential_chance", "REAL", s => s.TorrentialChance),
            ("other_stop_chance", "REAL", s => s.OtherStopChance), ("explosiveness", "REAL", s => s.Explosiveness),
            ("record_ms", "REAL", s => s.RecordMs),
        };

        private static string ScoreColumnsSql => string.Join(", ", ScoreColumns.Select(c => $"{c.Name} {c.Type}"));

        private static SqliteConnection Open()
        {
            var path = DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // Default Timeout: another process writing (a parallel run, the live bot) is waited for, not failed on.
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 60 }.ToString());
            connection.Open();
            if (!_ready)
            {
                Exec(connection, "PRAGMA journal_mode=WAL;");
                Exec(connection, $@"
CREATE TABLE IF NOT EXISTS cards(code INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE IF NOT EXISTS zones(id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE IF NOT EXISTS part_names(id INTEGER PRIMARY KEY, name TEXT UNIQUE);
CREATE TABLE IF NOT EXISTS sim_duels(id INTEGER PRIMARY KEY, run TEXT, duel INTEGER, time TEXT, deck0 TEXT, deck1 TEXT, version0 INTEGER, version1 INTEGER,
    tuning0 TEXT, tuning1 TEXT, winner INTEGER, reason TEXT, turns INTEGER, error INTEGER);
CREATE TABLE IF NOT EXISTS sim_positions(id INTEGER PRIMARY KEY, duel_id INTEGER REFERENCES sim_duels(id), seat INTEGER, went_first INTEGER,
    turn INTEGER, our_turn INTEGER, result REAL, {ScoreColumnsSql});
CREATE TABLE IF NOT EXISTS sim_parts(position_id INTEGER REFERENCES sim_positions(id), part_id INTEGER REFERENCES part_names(id), value REAL);
CREATE TABLE IF NOT EXISTS sim_truth(duel_id INTEGER REFERENCES sim_duels(id), turn INTEGER, player INTEGER, turn_player INTEGER, lp INTEGER, deck INTEGER, extra INTEGER);
CREATE TABLE IF NOT EXISTS sim_truth_cards(duel_id INTEGER, turn INTEGER, player INTEGER, zone INTEGER, code INTEGER, count INTEGER);
CREATE TABLE IF NOT EXISTS live_duels(id INTEGER PRIMARY KEY, log TEXT, deck TEXT, brain_version INTEGER, started TEXT, ended TEXT,
    result REAL, result_text TEXT, reason TEXT, turns INTEGER);
CREATE TABLE IF NOT EXISTS live_positions(id INTEGER PRIMARY KEY, duel_id INTEGER REFERENCES live_duels(id), turn INTEGER, our_turn INTEGER,
    result REAL, {ScoreColumnsSql});
CREATE TABLE IF NOT EXISTS live_parts(position_id INTEGER REFERENCES live_positions(id), part_id INTEGER REFERENCES part_names(id), value REAL);
CREATE TABLE IF NOT EXISTS live_cards(position_id INTEGER REFERENCES live_positions(id), side INTEGER, zone INTEGER, code INTEGER, count INTEGER);
CREATE INDEX IF NOT EXISTS sim_positions_duel ON sim_positions(duel_id);
CREATE INDEX IF NOT EXISTS sim_parts_position ON sim_parts(position_id);
CREATE INDEX IF NOT EXISTS sim_truth_duel ON sim_truth(duel_id, turn);
CREATE INDEX IF NOT EXISTS sim_truth_cards_duel ON sim_truth_cards(duel_id, turn);
CREATE INDEX IF NOT EXISTS live_positions_duel ON live_positions(duel_id);
CREATE INDEX IF NOT EXISTS live_parts_position ON live_parts(position_id);
CREATE INDEX IF NOT EXISTS live_cards_position ON live_cards(position_id);
CREATE VIEW IF NOT EXISTS sim_position_cards AS
    SELECT p.id AS position_id, p.duel_id, p.seat, p.turn, p.result, CASE WHEN c.player = p.seat THEN 'us' ELSE 'them' END AS side,
           z.name AS zone, c.code, k.name AS card, c.count
    FROM sim_positions p JOIN sim_truth_cards c ON c.duel_id = p.duel_id AND c.turn = p.turn
    JOIN zones z ON z.id = c.zone LEFT JOIN cards k ON k.code = c.code;");
                for (int zone = 0; zone < ZoneNames.Length; zone++)
                    Exec(connection, "INSERT OR IGNORE INTO zones(id, name) VALUES ($id, $name)", ("$id", zone), ("$name", ZoneNames[zone]));
                _ready = true;
            }
            return connection;
        }

        private static void Exec(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in args)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        private static long Insert(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql + "; SELECT last_insert_rowid();";
            foreach (var (name, value) in args)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return (long)command.ExecuteScalar()!;
        }

        private static long InsertPosition(SqliteConnection connection, string table, (string Name, object? Value)[] keys, PositionScores scores)
        {
            var columns = keys.Select(k => k.Name).Concat(ScoreColumns.Select(c => c.Name)).ToList();
            var args = keys.Concat(ScoreColumns.Select(c => (c.Name, c.Get(scores)))).Select(a => ("$" + a.Item1, a.Item2)).ToArray();
            return Insert(connection, $"INSERT INTO {table}({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))})", args);
        }

        private static readonly Dictionary<string, long> PartIds = new();

        private static void InsertParts(SqliteConnection connection, string table, long positionId, PositionScores scores)
        {
            foreach (var (name, value) in scores.Parts)
            {
                if (!PartIds.TryGetValue(name, out long partId))
                {
                    Exec(connection, "INSERT OR IGNORE INTO part_names(name) VALUES ($name)", ("$name", name));
                    using var find = connection.CreateCommand();
                    find.CommandText = "SELECT id FROM part_names WHERE name = $name";
                    find.Parameters.AddWithValue("$name", name);
                    PartIds[name] = partId = (long)find.ExecuteScalar()!;
                }
                Exec(connection, $"INSERT INTO {table}(position_id, part_id, value) VALUES ($p, $k, $v)", ("$p", positionId), ("$k", partId), ("$v", value));
            }
        }

        private static void InsertNames(SqliteConnection connection, IEnumerable<int> codes, Func<int, string?> nameOf)
        {
            foreach (int code in codes.Distinct())
                Exec(connection, "INSERT OR IGNORE INTO cards(code, name) VALUES ($c, $n)", ("$c", code), ("$n", nameOf(code)));
        }

        /// <summary>A finished DuelSim duel: its row, both seats' positions with the result, the engine's truth, card names.</summary>
        public static void WriteSimDuel(SimDuel duel, Func<int, string?> nameOf)
        {
            lock (DbLock)
            {
                using var connection = Open();
                Exec(connection, "BEGIN IMMEDIATE");
                try
                {
                long duelId = Insert(connection,
                    "INSERT INTO sim_duels(run, duel, time, deck0, deck1, version0, version1, tuning0, tuning1, winner, reason, turns, error) " +
                    "VALUES ($run, $duel, $time, $d0, $d1, $v0, $v1, $t0, $t1, $w, $r, $turns, $e)",
                    ("$run", duel.Run), ("$duel", duel.Number), ("$time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("$d0", duel.Decks[0]), ("$d1", duel.Decks[1]), ("$v0", duel.Versions[0]), ("$v1", duel.Versions[1]),
                    ("$t0", duel.Tunings[0]), ("$t1", duel.Tunings[1]), ("$w", duel.Winner), ("$r", duel.Reason), ("$turns", duel.Turns), ("$e", duel.Error ? 1 : 0));
                var codes = new HashSet<int>();
                foreach (var (turn, turnPlayer, sides) in duel.Truth)
                {
                    for (int player = 0; player < 2; player++)
                    {
                        var side = sides[player];
                        Exec(connection, "INSERT INTO sim_truth(duel_id, turn, player, turn_player, lp, deck, extra) VALUES ($d, $t, $p, $tp, $lp, $deck, $extra)",
                            ("$d", duelId), ("$t", turn), ("$p", player), ("$tp", turnPlayer), ("$lp", side.Lp), ("$deck", side.Deck), ("$extra", side.Extra));
                        foreach (var (zone, code, count) in side.Cards)
                        {
                            codes.Add(code);
                            Exec(connection, "INSERT INTO sim_truth_cards(duel_id, turn, player, zone, code, count) VALUES ($d, $t, $p, $z, $c, $n)",
                                ("$d", duelId), ("$t", turn), ("$p", player), ("$z", zone), ("$c", code), ("$n", count));
                        }
                    }
                    for (int seat = 0; seat < 2; seat++)
                    {
                        if (!duel.Views.TryGetValue((turn, seat), out var scores))
                            continue;
                        double result = duel.Winner < 0 ? 0.5 : duel.Winner == seat ? 1 : 0;
                        long positionId = InsertPosition(connection, "sim_positions", new (string, object?)[]
                        {
                            ("duel_id", duelId), ("seat", seat), ("went_first", seat == 0 ? 1 : 0), ("turn", turn), ("our_turn", turnPlayer == seat ? 1 : 0), ("result", result),
                        }, scores);
                        InsertParts(connection, "sim_parts", positionId, scores);
                    }
                }
                InsertNames(connection, codes, nameOf);
                Exec(connection, "COMMIT");
                }
                catch
                {
                    Exec(connection, "ROLLBACK");
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------ live games

        private sealed class LiveDuel
        {
            public required string Deck;
            public required int BrainVersion;
            public required DateTime Started;
            public readonly List<PositionScores> Positions = new();
        }

        private static readonly Dictionary<string, LiveDuel> LiveDuels = new();
        private static bool _liveHooked;

        /// <summary>
        /// A live turn start's scores, held until the duel ends (its log closes with the result), then written off the bot's path. Positions
        /// of a duel whose log never closes (the bot killed) are lost.
        /// </summary>
        public static void AddLive(string log, string deck, int brainVersion, PositionScores scores)
        {
            lock (LiveDuels)
            {
                if (!_liveHooked)
                {
                    _liveHooked = true;
                    var previous = OmegaBotWrapper.Logging.DuelLogOptions.DuelEnded;
                    OmegaBotWrapper.Logging.DuelLogOptions.DuelEnded = (bot, path, result, reason) =>
                    {
                        previous?.Invoke(bot, path, result, reason);
                        LiveDuelEnded(path, result, reason);
                    };
                }
                if (!LiveDuels.TryGetValue(log, out var duel))
                    LiveDuels[log] = duel = new LiveDuel { Deck = deck, BrainVersion = brainVersion, Started = DateTime.Now };
                duel.Positions.Add(scores);
            }
        }

        private static void LiveDuelEnded(string log, string resultText, string reason)
        {
            LiveDuel? duel;
            lock (LiveDuels)
                if (LiveDuels.Remove(log, out duel) == false)
                    return;
            double? result = resultText switch { "BOT WON" => 1, "BOT LOST" => 0, "DRAW" => 0.5, _ => null };
            // Off the bot's path: the write waits on the database, never the duel.
            Task.Run(() =>
            {
                try
                {
                    lock (DbLock)
                    {
                        using var connection = Open();
                        Exec(connection, "BEGIN IMMEDIATE");
                        try
                        {
                        long duelId = Insert(connection,
                            "INSERT INTO live_duels(log, deck, brain_version, started, ended, result, result_text, reason, turns) VALUES ($log, $deck, $v, $s, $e, $r, $rt, $why, $turns)",
                            ("$log", log), ("$deck", duel!.Deck), ("$v", duel.BrainVersion), ("$s", duel.Started.ToString("yyyy-MM-dd HH:mm:ss")),
                            ("$e", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$r", result), ("$rt", resultText), ("$why", reason),
                            ("$turns", duel.Positions.Count == 0 ? 0 : duel.Positions.Max(p => p.Turn)));
                        var codes = new HashSet<int>();
                        foreach (var scores in duel.Positions)
                        {
                            long positionId = InsertPosition(connection, "live_positions", new (string, object?)[]
                            {
                                ("duel_id", duelId), ("turn", scores.Turn), ("our_turn", scores.OurTurn ? 1 : 0), ("result", result),
                            }, scores);
                            InsertParts(connection, "live_parts", positionId, scores);
                            foreach (var (side, zone, code, count) in scores.Cards)
                            {
                                codes.Add(code);
                                Exec(connection, "INSERT INTO live_cards(position_id, side, zone, code, count) VALUES ($p, $s, $z, $c, $n)",
                                    ("$p", positionId), ("$s", side), ("$z", zone), ("$c", code), ("$n", count));
                            }
                        }
                        InsertNames(connection, codes, code => Search.SearchHost.Available ? Search.SearchHost.Cards.Get(code)?.Name : null);
                        Exec(connection, "COMMIT");
                        }
                        catch
                        {
                            Exec(connection, "ROLLBACK");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // (OmegaBotWrapper.BotLogger is internal to the wrapper: BotTest's console log instead.)
                    BotTest.Utils.ConsoleUtils.PrintColored($"[Dataset] live positions not written: {ex.Message}");
                }
            });
        }
    }
}
