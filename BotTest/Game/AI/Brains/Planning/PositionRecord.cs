using BotTest.Game.AI.Dataset;
using BotTest.Game.AI.Search;
using DuelSim.Engine;
using OmegaBotWrapper;
using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Where DuelSim takes each turn start's position record (its position dataset; user, 2026-11-25). Null outside DuelSim: live games
    /// go to the live tables instead (PositionStore.AddLive).
    /// </summary>
    public static class PositionSink
    {
        /// <summary>The bot, the turn, and the scores its evaluator gives that turn's start.</summary>
        public static Action<DuelBotWrapper, int, PositionScores>? OnPosition;
    }

    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// At a turn start (the real duel's bot only, never a search twin or a line): every score the bot's evaluator gives the position from
        /// its own view, hidden cards hidden as the search sees them: life points, both card advantage scores and their parts, the board
        /// score and every term and sub-row (board, dominance, threat, clocks), lethal exposure, the strategic state and standing. DuelSim
        /// takes it through PositionSink; a live game keeps it, with the cards the bot can see, until the duel's result is known.
        /// </summary>
        private void RecordPosition(DuelBotWrapper bot, BotStateHeader header, SearchView view, bool beforeDraw)
        {
            if (IsSearchTwin || !SearchHost.Available)
                return;
            var sink = PositionSink.OnPosition;
            string? liveLog = sink == null ? bot.DuelLog?.CurrentLogPath : null;
            if (sink == null && liveLog == null)
                return;
            if (liveLog != null)
            {
                // Live: never on the bot's path (the first turn's cold start took 1.8 s in DuelSim, later ones 2-15 ms). Worked out on a
                // background thread, at the turn start's values (header and view are this turn's own snapshot).
                var task = new Task(() => ScorePosition(bot, header, view, beforeDraw, null, liveLog));
                task.Start();
                return;
            }
            ScorePosition(bot, header, view, beforeDraw, sink, null);
        }

        private void ScorePosition(DuelBotWrapper bot, BotStateHeader header, SearchView view, bool beforeDraw,
            Action<DuelBotWrapper, int, PositionScores>? sink, string? liveLog)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (ours, theirs) = ViewSummaries(view);
                var evaluator = MakeEvaluator(view);
                var parts = evaluator.PositionParts(ours, theirs);
                // The board difference as the search's root centre counts it (Mcts.CaptureLineStart): the top-level parts summed.
                double boardDiff = parts.Where(p => !p.Key.Contains(BoardEvaluator.SubPart)).Sum(p => p.Value);
                var (ourCards, theirCards) = evaluator.CardAdvantageScores(ours, theirs);
                var counts = evaluator.CountAdvantage(ours, theirs);
                double? lethal;
                try { lethal = evaluator.LethalExposure(ours); }
                catch { lethal = null; }
                double Part(string name) => parts.GetValueOrDefault(name);
                var scores = new PositionScores
                {
                    Turn = header.CurrentTurn, OurTurn = header.CurrentPlayer == 0, BeforeDraw = beforeDraw,
                    LpUs = ours.LifePoints, LpThem = theirs.LifePoints,
                    CardScoreUs = ourCards, CardScoreThem = theirCards, CardNet = counts.Net,
                    HandUs = counts.OurHand, HandThem = counts.TheirHand, HandValueUs = counts.OurHandAdjustment, HandValueThem = counts.TheirHandAdjustment,
                    MonstersUs = counts.OurMonsters, MonstersThem = counts.TheirMonsters, SpellsUs = counts.OurSpells, SpellsThem = counts.TheirSpells,
                    SetTrapValueUs = counts.OurSetTrapAdjustment, SetTrapValueThem = counts.TheirSetTrapAdjustment,
                    GraveValueUs = counts.OurGraveValue, GraveValueThem = counts.TheirGraveValue, GravePairs = counts.GravePairs,
                    BoardDiff = boardDiff, BoardScore = 1 / (1 + Math.Exp(-boardDiff / BoardEvaluator.Scale)),
                    OurBoard = Part(BoardEvaluator.OurBoardDelta), TheirBoard = Part(BoardEvaluator.TheirBoardDelta),
                    Dominance = Part(BoardEvaluator.DominancePart), Threat = Part(BoardEvaluator.ThreatPart),
                    OurClock = Part(BoardEvaluator.OurClockPart), TheirClock = Part(BoardEvaluator.TheirClockPart),
                    LethalExposure = lethal,
                    StrategicState = StrategicState.ToString(),
                    Standing = StandingState.Assess(ours.LifePoints, ourCards, theirCards).ToString(),
                    RiskPress = RiskPressNow,
                    UnknownHand = Math.Max(0, view.EnemyHandCount - view.EnemyKnownHand.Count),
                    UnknownSets = view.EnemySpells.Count(c => c.Code <= 0),
                    MirrorChance = _unknownMirror, TorrentialChance = _unknownTorrential, OtherStopChance = _otherStopChance,
                    Parts = parts,
                };
                lock (OpponentDeck)
                    scores.Explosiveness = OpponentDeck.Explosiveness();
                if (liveLog != null)
                    scores.Cards = VisibleCards(view);
                scores.RecordMs = clock.Elapsed.TotalMilliseconds;
                if (sink != null)
                    sink(bot, header.CurrentTurn, scores);
                else
                    PositionStore.AddLive(liveLog!, bot.DeckName ?? "", BrainVersion, scores);
            }
            catch (Exception ex)
            {
                bot.LogNote($"position record: turn {header.CurrentTurn} not recorded ({ex.Message})");
            }
        }

        /// <summary>The cards this view shows, counted by side (0 us, 1 them), zone and code: their hidden cards aren't in it.</summary>
        private static List<(int Side, int Zone, int Code, int Count)> VisibleCards(SearchView view)
        {
            var counts = new Dictionary<(int, int, int), int>();
            void Add(int side, int zone, IEnumerable<int> codes)
            {
                foreach (int code in codes.Where(c => c > 0 && c != SearchView.InertCard))
                    counts[(side, zone, code)] = counts.GetValueOrDefault((side, zone, code)) + 1;
            }
            static bool FaceUp(ViewCard c) => (c.Position & 0x5) != 0;
            Add(0, PositionStore.Hand, view.OwnHand);
            Add(0, PositionStore.Monsters, view.OwnMonsters.Where(FaceUp).Select(c => c.Code));
            Add(0, PositionStore.SetMonsters, view.OwnMonsters.Where(c => !FaceUp(c)).Select(c => c.Code));
            Add(0, PositionStore.Spells, view.OwnSpells.Where(FaceUp).Select(c => c.Code));
            Add(0, PositionStore.SetSpells, view.OwnSpells.Where(c => !FaceUp(c)).Select(c => c.Code));
            Add(0, PositionStore.Grave, view.OwnGrave);
            Add(0, PositionStore.Banished, view.OwnBanished);
            Add(1, PositionStore.Hand, view.EnemyKnownHand.Take(view.EnemyHandCount));
            Add(1, PositionStore.Monsters, view.EnemyMonsters.Where(FaceUp).Select(c => c.Code));
            Add(1, PositionStore.SetMonsters, view.EnemyMonsters.Where(c => !FaceUp(c)).Select(c => c.Code));
            Add(1, PositionStore.Spells, view.EnemySpells.Where(FaceUp).Select(c => c.Code));
            Add(1, PositionStore.SetSpells, view.EnemySpells.Where(c => !FaceUp(c)).Select(c => c.Code));
            Add(1, PositionStore.Grave, view.EnemyGrave);
            Add(1, PositionStore.Banished, view.EnemyBanished);
            return counts.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value)).ToList();
        }
    }
}
