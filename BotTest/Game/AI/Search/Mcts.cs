using BotTest.Game.AI.Brains;
using BotTest.Game.AI.Bots.MasterDuel.Blackwing;
using System.Collections.Concurrent;
using System.Diagnostics;
using DuelSim.Engine;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Search
{
    internal sealed record SearchSettings(
        int BudgetMs = 1500,
        int Threads = 1,
        int MaxIterations = int.MaxValue,
        /// <summary>UCB exploration constant (values are position scores, 0-1).</summary>
        double Exploration = 0.5,
        /// <summary>Bonus for our brain's own pick at each of our decisions in the tree, fading with visits (progressive bias).</summary>
        double PriorBonus = 0.1,
        /// <summary>Turns played per line: 2 = the rest of ours and their whole reply (user, 2026-11-11); 3 = also our next turn.</summary>
        int Horizon = 2,
        /// <summary>Progressive widening for card picks with many answers: at most 1 + Widening x sqrt(visits) children.</summary>
        double Widening = 2,
        /// <summary>
        /// Progressive widening at our own main / battle decisions: at most 1 + MainWidening x sqrt(visits) answers open, the
        /// brain's own first, then the moves it didn't hold back, the held ones last. The tree deepens along lines like the
        /// brain's before it branches into rule-breaking ones (user, 2026-11-11: "search through similar lines to the brain").
        /// </summary>
        double MainWidening = 3,
        /// <summary>Debugging: record the play-by-play of up to TraceLines lines whose first choice is this root key.</summary>
        string? TraceRoot = null,
        int TraceLines = 0,
        /// <summary>Root moves to send lines to until each has FocusLines, the one with the fewest first (user, 2026-11-16: both sides of a
        /// replacement need the same lines; the search's own exploration spreads lines over every root move and hardly visits one
        /// that scores far lower, so a decision took 30-300 s to fill the two that matter).</summary>
        IReadOnlyList<string>? FocusRoots = null,
        int FocusLines = 0);

    /// <param name="Mean">The move's score: its line value (MctsNode.LineValue), 0-1.</param>
    /// <param name="Average">The plain average of every line through it (what Mean was before 2026-11-16), for the log.</param>
    internal sealed record RootStat(string Key, string Label, int Visits, double Mean, double Average = double.NaN, double StdErr = 1,
        double Unexplored = double.NaN);

    /// <summary>
    /// What this search's lines through one root choice ended in, averaged (for the log: user, 2026-11-13, "the log should
    /// show more"): how often they won or lost outright, LP and board value (card units) on each side, and the cost paid
    /// for going against the brain's rules.
    /// </summary>
    /// <param name="Parts">The score breakdown, averaged over the lines (card units, against the decision's position; user, 2026-11-20: "a
    /// complete breakdown of what went into the main decision"). BoardEvaluator.LethalPart is the average lethal exposure, a chance.</param>
    internal sealed record RootDetail(int Lines, double WinRate, double LossRate, double OurLp, double TheirLp, double OurBoard, double TheirBoard, double Penalty,
        IReadOnlyDictionary<string, double>? Parts = null)
    {
        public string Describe() =>
            $"won {WinRate:P0}, lost {LossRate:P0} of {Lines} lines; at the end LP {OurLp:0} vs {TheirLp:0}, board {OurBoard:0.0} vs {TheirBoard:0.0} card units" +
            (Penalty > 0.05 ? $", rules broken cost {Penalty:0.0}" : "");
    }

    internal sealed record PlanStep(BotState State, string Key, string Label);

    /// <summary>One step of a move's best line: whose answer it is, the lines behind it, its score (the node's line value, 0-1) and
    /// standard error. Deeper steps have fewer lines, so their uncertainty grows.</summary>
    /// <param name="TheirTurn">The step comes after our turn ended (on their turn).</param>
    /// <param name="Rollout">Past the tree's end: the brain's own play in the line that made the last tree step (no score of its own).</param>
    internal sealed record LineStepStat(string Label, bool Ours, bool TheirTurn, int Visits, double Value, double StdErr, bool Rollout = false);

    internal sealed record SearchOutcome(List<RootStat> Root, int Iterations, int Failures, TimeSpan Elapsed, string? FirstFailure,
        /// <summary>The best line's next own pick after the root choice (e.g. the attack target), or null.</summary>
        string? FollowUpKey,
        /// <summary>A difference between the rebuilt board and the bot's view (monsters / spells-traps per side), or null when they match.</summary>
        string? PositionMismatch = null,
        /// <summary>
        /// Per root choice, our follow-up steps (chains, discards, targets, yes/no) up to our next main or
        /// battle decision: followed when the MCTS's move replaces the brain's (see DeckBotBrain.FollowPlan).
        /// </summary>
        IReadOnlyDictionary<string, List<PlanStep>>? Plans = null,
        /// <summary>Recorded lines (SearchSettings.TraceRoot), each a play-by-play ending with its score.</summary>
        IReadOnlyList<string>? Traces = null,
        /// <summary>Lines behind the root (this search and every earlier one that reached it: dead time, earlier decisions).</summary>
        int TreeVisits = 0,
        /// <summary>The deepest line in the tree below the root, in decisions.</summary>
        int Depth = 0,
        /// <summary>The most-visited line from the root through our turn (main, battle and picks), for the log.</summary>
        IReadOnlyList<PlanStep>? BestLine = null,
        /// <summary>The brain's holds at the root: option key -> (what a line paid for the move, the rule).</summary>
        IReadOnlyDictionary<string, (double Cost, string Rule)>? RootHolds = null,
        /// <summary>Our brain's own answer at the root (the real brain's pick live, the twin's in the bench), or null.</summary>
        string? RootPrior = null,
        /// <summary>For the top root choices: the most-visited line through our turn that starts with that choice (for the log).</summary>
        IReadOnlyDictionary<string, List<PlanStep>>? RootLines = null,
        /// <summary>Per root choice followed by our own card pick (an attack target): each pick tried, with visits and mean.</summary>
        IReadOnlyDictionary<string, List<RootStat>>? FollowUps = null,
        /// <summary>Per root choice: what this search's lines through it ended in (see RootDetail).</summary>
        IReadOnlyDictionary<string, RootDetail>? Details = null,
        /// <summary>Pairs of root moves whose best lines reach the same position within our turn (transpositions: another order of the same play).</summary>
        IReadOnlyList<(string A, string B)>? Transposed = null,
        /// <summary>Per root choice (all of them): its best line through the tree, every step with its score and uncertainty (the log's
        /// breakdown; user, 2026-11-20: "the full best line it's following, with a confidence interval on each score").</summary>
        IReadOnlyDictionary<string, List<LineStepStat>>? ScoredLines = null)
    {
        public TimeSpan ReportElapsed { get; init; }

        /// <summary>Real options no line found at the root (a replayed game that differs from the real one): never searched, so the
        /// other options' scores can't decide (user, 2026-11-24). Set by the replaying searches (MidChain, TurnLog).</summary>
        public IReadOnlyList<string> NeverOffered { get; init; } = Array.Empty<string>();

        /// <summary>The lines that reached the root, and per move how many of them left it out of their own move list, by why (MctsNode.Missing).</summary>
        public int RootArrivals { get; init; }
        public IReadOnlyDictionary<string, (int Brain, int Rule, int Engine)>? RootMissing { get; init; }

        /// <summary>
        /// For the log: how often this move was in the lines' own move lists at the root, and why it was left out ("" when never left out
        /// or nothing was recorded).
        /// </summary>
        public string OfferNote(string key)
        {
            if (RootArrivals <= 0 || RootMissing == null || !RootMissing.TryGetValue(key, out var why))
                return "";
            int left = why.Brain + why.Rule + why.Engine;
            if (left == 0)
                return "";
            var reasons = new List<string>();
            if (why.Brain > 0) reasons.Add($"the brain blocked it {why.Brain}");
            if (why.Rule > 0) reasons.Add($"a search rule filtered it {why.Rule}");
            if (why.Engine > 0) reasons.Add($"the line's game didn't offer it {why.Engine}");
            return $" [on offer in {RootArrivals - left} of {RootArrivals} lines; left out: {string.Join(", ", reasons)}]";
        }

        public RootStat? Stat(string key) => Root.FirstOrDefault(r => r.Key == key) ??
            (SearchActions.SameMoveIn(key, Root.Select(r => r.Key)) is { } same ? Root.FirstOrDefault(r => r.Key == same) : null);
    }

    /// <summary>One decision in the MCTS tree: its statistics, and the answers tried from it.</summary>
    internal sealed class MctsNode
    {
        private static long _nextLockOrder;
        public readonly object Lock = new();
        // A transposition can lock two peers. Their order must be independent of the path that reached them.
        public readonly long LockOrder = Interlocked.Increment(ref _nextLockOrder);
        public readonly Dictionary<string, MctsNode> Children = new();
        public string Label = "";
        /// <summary>Each child's move as named at this node (its own Label can belong to another parent after a transposition merge: seen
        /// 2026-11-18, "change position of Bora" logged as "set Reinforcement of the Army", the move the merged node was first reached by).</summary>
        public Dictionary<string, string>? ChildLabels;

        /// <summary>At a search's root: every option key a line found offered there (call under this node's lock). A real option missing
        /// from it was never searched at all (SearchAtDecisions.NeverOffered).</summary>
        public HashSet<string>? Offered;

        /// <summary>
        /// At a search's root (our main phase and chain decisions): the lines that reached it, and per move the lines whose own move list
        /// left it out, by why (call under this node's lock): the brain twin blocked it, a search rule filtered it, or the line's engine
        /// didn't offer it at all. A move short of lines that most lines leave out can't get its lines by focus (user, 2026-11-25).
        /// </summary>
        public int RootArrivals;
        public Dictionary<string, (int Brain, int Rule, int Engine)>? Missing;

        /// <summary>The name of the move to this child here (call under this node's lock).</summary>
        public string LabelOf(string key, MctsNode child) => ChildLabels != null && ChildLabels.TryGetValue(key, out var label) ? label : child.Label;

        public int Visits;
        public double Total;
        /// <summary>Times this node was a legal answer when its parent was visited (ISMCTS availability).</summary>
        public int Available;
        /// <summary>The kind of decision this node's children answer.</summary>
        public BotState ChildState;
        /// <summary>Whose decision this node's children are (0 = us).</summary>
        public int ChildPlayer = -1;
        /// <summary>Our brain's own answer at this decision (the first time it was asked), for the progressive bias.</summary>
        public string? PriorKey;
        public bool PriorAsked;
        /// <summary>The answers our brain held back here (option key -> what a line pays for it, and the rule), once asked.</summary>
        public Dictionary<string, (double Cost, string Rule)>? Holds;
        /// <summary>At their card pick: how many genuinely different picks they had (copies of the same card, wherever they are,
        /// count once), or -1 when not one of their picks. More than one: we couldn't know what they'd choose.</summary>
        public int DistinctPicks = -1;
        /// <summary>The decision key this node was registered under (our decisions), or null.</summary>
        public string? Key;
        /// <summary>Lines through this node met an open outcome before our next decision that isn't a branch of the tree: a pick of
        /// theirs among more than one different card answered outside it (over 80 to pick from), or chance (a coin, a die, a
        /// random selection). Above 1: the decision after it can't have been planned.</summary>
        public int OpenBelow;
        /// <summary>Steps into the line (tree nodes above it) when this node was filed as a position, or -1: a transposition only merges
        /// nodes at the same depth.</summary>
        public int Depth = -1;
        /// <summary>The search start (root decision) whose lines first reached this node: transpositions only merge nodes of one start, so a
        /// continued tree never mixes in lines from before the real game moved on (2026-11-17: after Charge resolved, an old "Charge, then
        /// change position" node was merged under the new root, and its stale score and label stood in for the brain's Gorz summon).</summary>
        public MctsNode? Origin;
        /// <summary>Its parent's visits when it was created: Available over the parent's visits since then is how often its answer was on offer.</summary>
        public int ParentVisitsAtCreation;
        /// <summary>Our moves after this node to the end of our turn, as the first line that created it played them outside the tree (the
        /// rollout): the log's best line goes on with them where the tree ends (user, 2026-11-20: "show the full line to end of turn").</summary>
        public List<string>? RolloutTail;
        public double Mean => Visits == 0 ? 0 : Total / Visits;

        /// <summary>
        /// Forgets the line counts and scores of this node and everything below it, keeping the moves, the brain's priors and holds. A kept
        /// node's lines were played from an earlier decision: their values carry that line's costs from above this node (rule breaks, order
        /// penalties) and that search's turn record, so they don't compare with this decision's own lines (user, 2026-11-24: "end the turn"
        /// won on 1903 kept lines with no fresh one, and a lethal attack lost to MST on our own Torrential). Used when a decision searches.
        /// </summary>
        public void ClearLines()
        {
            var seen = new HashSet<MctsNode>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<MctsNode>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!seen.Add(node))
                    continue;
                lock (node.Lock)
                {
                    (node.Visits, node.Total, node.SumSq, node.Available, node.ParentVisitsAtCreation) = (0, 0, 0, 0, 0);
                    (node.CachedScore, node.ScoreCachedAt) = (0, -1);
                    foreach (var child in node.Children.Values)
                        stack.Push(child);
                }
            }
        }
        /// <summary>A root move's score (LineValue), kept for choosing which root move the next line tries, and its Visits when worked out
        /// (-1: never). Refreshed as its lines grow (Mcts.RootScores).</summary>
        public double CachedScore;
        public int ScoreCachedAt = -1;
        /// <summary>The sum of its lines' squared scores (for how settled its score is).</summary>
        public double SumSq;
        /// <summary>The margin of error of its lines' average score (the standard error, 0-1): small = settled, more lines won't move it.</summary>
        public double StdErr => Visits < 2 ? 1 : Math.Sqrt(Math.Max(0, SumSq / Visits - Mean * Mean) / Visits);

        /// <summary>
        /// What a line through this node reaches when we play it well: at our own decisions, the best well-explored answer's value (at
        /// least <paramref name="minVisits"/> lines behind it), recursively; at theirs and where nothing is explored enough, the plain
        /// average. The plain average punished every move with choices after it (manual games 2026-11-16: "end the turn", with
        /// nothing to decide, scored exactly; "summon Bora" averaged every pick, chain and attack its lines tried, good and bad, so
        /// a line worth 90 read as 76 and the bot ended turn after turn with a full hand).
        /// </summary>
        public double LineValue(int minVisits) => LineValue(minVisits, new Dictionary<MctsNode, double>(ReferenceEqualityComparer.Instance));

        /// <summary>
        /// While none of our follow-up choices here has its LineValueVisits yet (so the score is still the plain average over all of them,
        /// bad branches included): the best follow-up's mean among those with a few lines (UnexploredMinVisits). NaN once one has its
        /// lines, or with no choice of ours to make. Seen 2026-11-19: "go to the Battle Phase" 1.3 below "end the turn" on 116 lines,
        /// its "make no attack" branches in the average; "end the turn" has no follow-ups, so it settled at once and the search stopped.
        /// </summary>
        public double UnexploredBest(int lineValueVisits)
        {
            lock (Lock)
            {
                if (ChildPlayer != 0 || Children.Count < 2 || Children.Values.Any(c => c.Visits >= lineValueVisits))
                    return double.NaN;
                var tried = Children.Values.Where(c => c.Visits >= Mcts.UnexploredMinVisits).ToList();
                return tried.Count == 0 ? double.NaN : tried.Max(c => c.Mean);
            }
        }

        // Memoised: transpositions make the tree a graph (two orders of the same play share a subtree), so an unmemoised walk
        // revisits shared subtrees once per path, which blew up on a board with four set cards (seen 2026-11-16: the bot stopped
        // answering after Jar of Greed). A node met again while its own value is being worked out answers its mean.
        private double LineValue(int minVisits, Dictionary<MctsNode, double> memo)
        {
            if (memo.TryGetValue(this, out double known))
                return known;
            memo[this] = Mean;
            List<MctsNode> explored;
            lock (Lock)
            {
                if (ChildPlayer != 0 || Children.Count == 0)
                    return Mean;
                // Only answers on offer nearly every time a line got here (LineValueAvailableShare): one that exists only after a lucky draw
                // (Jar of Greed drawing Future Fusion) isn't ours to choose, and taking the best of those read every draw as the best one
                // (seen 2026-11-19: Jar of Greed 62, setting Ryko 70 in another run, end the turn 41, by which draws came up).
                explored = Children.Values.Where(c => c.Visits >= minVisits &&
                    c.Available >= Mcts.LineValueAvailableShare * Math.Max(1, Visits - c.ParentVisitsAtCreation)).ToList();
            }
            if (explored.Count == 0)
            {
                // None has its lines yet: the plain average would count every stupid branch our side of the lines tried deep down
                // (user, 2026-11-19: "I don't want decisions penalised because the lines they draw play stupidly deep down the line";
                // seen: every attack line went on into a Main Phase 2 Plaguespreader + Goyo, -2.5 each). Read it through the best
                // follow-up with a few lines instead, one standard error down for its luck, never below the average.
                List<MctsNode> tried;
                lock (Lock)
                    tried = Children.Values.Where(c => c.Visits >= Mcts.UnexploredMinVisits &&
                        c.Available >= Mcts.LineValueAvailableShare * Math.Max(1, Visits - c.ParentVisitsAtCreation)).ToList();
                double floor = Mean;
                foreach (var child in tried)
                {
                    double v = child.LineValue(minVisits, memo) - Mcts.UnexploredErrors * child.StdErr;
                    if (v >= 0 && v <= 1 && v > floor)
                        floor = v;
                }
                memo[this] = floor;
                return floor;
            }
            // Each follow-up one standard error down for its luck before taking the best, as with the unexplored ones above, never below the
            // average: the best of many thin follow-ups is mostly the luckiest one (user, 2026-11-24, Dark Grepher's ~16 picks of 45-150 lines
            // beat Armageddon Knight's few thick ones: the same play scored +3.43 over 1828 lines and +3.85 over 273 by when it was activated).
            double best = double.NegativeInfinity;
            foreach (var child in explored)
                best = Math.Max(best, child.LineValue(minVisits, memo) - Mcts.UnexploredErrors * child.StdErr);
            best = Math.Max(best, Mean);
            // Scores are 0-1: anything else is a damaged node (counted for the bench), not a line worth playing.
            if (best < 0 || best > 1 || double.IsNaN(best))
            {
                Interlocked.Increment(ref Mcts.DamagedNodes);
                best = Mean;
            }
            memo[this] = best;
            return best;
        }
    }

    /// <summary>
    /// One MCTS tree, kept across searches: dead time builds it from our next turn's start, each decision continues it, and
    /// a decision finds its node again by the board and the answers on offer (DecisionKey), so the work carries over.
    /// </summary>
    internal sealed class MctsTree
    {
        public MctsNode Root { get; } = new();
        private readonly ConcurrentDictionary<string, MctsNode> _decisions = new();
        private const int MaxDecisions = 200_000;
        public long Iterations;

        public string Label { get; init; } = "";

        /// <summary>
        /// The position every search of this tree measures its lines from: the first search's root, kept for the later decisions that
        /// continue the tree. Each search used to measure from its own root, so a kept node mixed lines measured from two positions
        /// (live 2026-11-24, Machina turn 3: "end the turn" kept +0.84 from the search before Gearframe's search added Machina Force,
        /// which its lines counted as a gain; the next decision's lines, measured with Force already in hand, couldn't match it, and the
        /// bot ended its turn without attacking). Set once (Mcts.CaptureLineStart).
        /// </summary>
        internal Mcts.TreeBaseline? Baseline;

        public void Register(string key, MctsNode node)
        {
            node.Key ??= key;
            if (_decisions.Count < MaxDecisions && _decisions.TryAdd(key, node))
                _byBoard.GetOrAdd(BoardOf(key), _ => new ConcurrentBag<(string, MctsNode)>()).Add((key, node));
        }

        // Decisions by board (the key without its options): the fallback lookup below.
        private readonly ConcurrentDictionary<string, ConcurrentBag<(string Key, MctsNode Node)>> _byBoard = new();

        private static string BoardOf(string key) => key.LastIndexOf('#') is int hash and >= 0 ? key[..hash] : key;
        private static HashSet<string> OptionsOf(string key) =>
            key.LastIndexOf('#') is int hash and >= 0 ? key[(hash + 1)..].Split(';').ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

        /// <summary>
        /// The decision with this key; else one on the same board whose lines were offered every answer the real game offers and more
        /// (the engine and Omega disagree on an option now and then: seen 2026-11-17, a union's unequip offered in the lines right after
        /// it equipped, never by Omega, and the next decision found no node: a DESYNC). The extra answers are never played: only the
        /// real game's options are legal. The best-explored such node.
        /// </summary>
        /// <summary>
        /// For a DESYNC report: the filed decision closest to this key (the most key parts equal, the same kind of prompt), and which
        /// parts differ (part: ours vs the tree's). Null when the tree has none of that kind.
        /// </summary>
        public (string Key, string Differences)? Closest(string key)
        {
            static string[] Parts(string k) => k.Split('|', '#');
            string kind = key.Split('|')[0];
            var mine = Parts(key);
            string[] names = { "prompt", "our LP", "their LP", "our hand", "our monsters", "our spells/traps", "our GY",
                               "their monsters (face-up + face-down count)", "their spells/traps (face-up + set count)", "their GY", "options" };
            (string Key, int Same)? best = null;
            foreach (var filed in _decisions.Keys)
            {
                if (!filed.StartsWith(kind + "|"))
                    continue;
                var theirs = Parts(filed);
                int same = mine.Zip(theirs).Count(p => p.First == p.Second);
                if (best == null || same > best.Value.Same)
                    best = (filed, same);
            }
            if (best == null)
                return null;
            var other = Parts(best.Value.Key);
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(mine.Length, other.Length); i++)
            {
                string a = i < mine.Length ? mine[i] : "-", b = i < other.Length ? other[i] : "-";
                if (a != b)
                    diffs.Add($"{(i < names.Length ? names[i] : $"part {i}")}: [{a}] vs the tree's [{b}]");
            }
            return (best.Value.Key, string.Join("; ", diffs));
        }

        public MctsNode? Find(string key)
        {
            if (_decisions.TryGetValue(key, out var node))
                return node;
            if (!_byBoard.TryGetValue(BoardOf(key), out var sameBoard))
                return null;
            var real = OptionsOf(key);
            return sameBoard.Where(c => OptionsOf(c.Key).IsSupersetOf(real)).Select(c => c.Node).OrderByDescending(n => n.Visits).FirstOrDefault();
        }

        // Positions for transpositions, by a stricter key than decisions (DecisionKey.Strict): only an exact match is merged.
        private readonly ConcurrentDictionary<string, MctsNode> _positions = new();

        public void RegisterPosition(string strictKey, MctsNode node)
        {
            if (_positions.Count < MaxDecisions)
                _positions.TryAdd(strictKey, node);
        }

        public MctsNode? FindPosition(string strictKey) => _positions.TryGetValue(strictKey, out var node) ? node : null;

        /// <summary>
        /// The way from an earlier decision of ours (<paramref name="from"/>) down to the decision with this key: whether the tree
        /// reaches it, and on the way the first pick of theirs among more than one genuinely different card (its count), or 0
        /// when every pick of theirs on the way had a single distinct answer (the plan accounted for it). User, 2026-11-13: "if
        /// they target any card ... with an effect that had more than one valid target, this should also trigger a rebuild. If
        /// they only have one valid target though, the planner should have accounted for that target ... and then continue."
        /// </summary>
        public (bool Reached, int OpenChoice) TheirChoicesOnTheWay(MctsNode from, string key)
        {
            var parent = new Dictionary<MctsNode, MctsNode>(ReferenceEqualityComparer.Instance) { [from] = from };
            var queue = new Queue<MctsNode>();
            queue.Enqueue(from);
            while (queue.Count > 0 && parent.Count < MaxDecisions)
            {
                var node = queue.Dequeue();
                if (!ReferenceEquals(node, from) && node.Key == key)
                {
                    for (var at = parent[node]; ; at = parent[at])
                    {
                        if (at.ChildPlayer == 1 && at.DistinctPicks > 1)
                            return (true, at.DistinctPicks);
                        if (at.OpenBelow > 1)
                            return (true, at.OpenBelow);
                        if (ReferenceEquals(at, from))
                            break;
                    }
                    return (true, 0);
                }
                List<MctsNode> children;
                lock (node.Lock)
                    children = node.Children.Values.ToList();
                foreach (var child in children)
                    if (parent.TryAdd(child, node))
                        queue.Enqueue(child);
            }
            return (false, 0);
        }
        public int Decisions => _decisions.Count;
    }

    /// <summary>
    /// Conventional Monte Carlo tree search over our turn and their whole reply (user, 2026-11-11: "a conventional mcts",
    /// long combos the brain can't see, "scored by the brain's rules about what cards are good"). Each iteration deals a
    /// fresh guess at the hidden cards (our deck order and draws, their hand, sets and deck from the deck read: information-set
    /// MCTS), rebuilds the position in the engine, then:
    ///  1. selects down the tree by UCB, at every decision of the horizon (ours maximise the score, theirs minimise it),
    ///     our brain's own pick getting a fading bonus (progressive bias), card picks with many answers widening slowly;
    ///  2. expands one new node;
    ///  3. rolls out to the horizon: our side played by our own brain twin, theirs by the quick policy (MctsPolicy);
    ///  4. scores the end position with BoardEvaluator (the deck brain's card values), minus the strong suggestions the
    ///     line's own choices went against;
    ///  5. backs the score up the path.
    /// The threads share one tree (virtual loss keeps them apart).
    /// </summary>
    internal sealed class Mcts
    {
        /// <summary>
        /// The engine turn that is our turn in the lines: turn 1 (the rebuilt position is our decision's), or for a search from the turn's start
        /// (FromTurnStart) our rebuilt turn, or our next turn after their rebuilt one.
        /// </summary>
        private int SearchTurn => FromTurnStart is not { } r ? 1 : r.Ours ? r.RebuiltTurn : r.RebuiltTurn + 1;

        /// <summary>The engine turn the root's decision is in: our turn, or theirs for a root in their turn.</summary>
        private int RootTurn => FromTurnStart?.RebuiltTurn ?? SearchTurn;

        /// <summary>Lines stop when this turn begins: after their reply to our turn (Horizon), or, for a root in their turn, after our next
        /// turn (user, 2026-11-18: "it should check until the end of its own turn").</summary>
        private int StopTurn => FromTurnStart is { Ours: false } ? SearchTurn + 1 : SearchTurn + _settings.Horizon;

        /// <summary>
        /// The search from the start of the real turn (plan "the search makes every decision", step 3): each line rebuilds the turn's snapshot and
        /// replays the turn as it really went (TurnReplay.Cursor) to the decision, the last checkpoint, which is the root (its key: RootKey).
        /// </summary>
        public TurnReplay? FromTurnStart { get; init; }

        /// <summary>The root decision's key (DecisionKey with its options) for a search from the turn's start.</summary>
        public string? RootKey { get; init; }

        /// <summary>
        /// The real decision's option keys, for a search that replays to it (Replay, FromTurnStart). The replayed game can put a card in
        /// another zone than the real one, and a field card's key holds its zone: at the root such an option is filed under the real key
        /// (SearchActions.ToLiveKeys), or it gets no lines and loses by default (user, 2026-11-24: Brain Control's Card Trooper in M4 never
        /// searched as a tribute, Machina Fortress tributed instead).
        /// </summary>
        public IReadOnlyCollection<string>? LiveRootKeys { get; init; }

        private static void MarkOpen(MctsNode node, int choices)
        {
            if (choices <= 1)
                return;
            lock (node.Lock)
                node.OpenBelow = Math.Max(node.OpenBelow, choices);
        }

        /// <summary>A card pick's genuinely different answers (copies of one card count once; all of them required: one), or null
        /// when the prompt isn't a card pick.</summary>
        private static int? TheirPickCards(Prompt prompt)
        {
            var (cards, min) = prompt.Data switch
            {
                SelectCardData d => (d.SelectableCards, d.Minimum),
                SelectTributeData d => (d.SelectableCards, d.Minimum),
                SelectUnselectCardData d => (d.SelectableCards, d.Minimum),
                _ => (null, 0),
            };
            if (cards == null || cards.Length == 0)
                return null;
            int distinct = cards.Select(c => SearchHost.CanonicalId(c.ID) is > 0 and var id ? id : c.ID).Distinct().Count();
            return min >= cards.Length ? 1 : distinct;
        }

        /// <summary>
        /// How many genuinely different answers a card pick has: each pick by its card(s) alone, without where they are, so two
        /// copies of the same card count once (user, 2026-11-13: Jinzo - Returner with two Jinzo in the graveyard "has to bring
        /// back one, but it makes no difference which"). A "cancel" isn't a card.
        /// </summary>
        private static int DistinctTargets(IEnumerable<string> keys) =>
            keys.Where(k => k.StartsWith("card:") || k.StartsWith("cards:"))
                .Select(k => string.Join('+', k[(k.IndexOf(':') + 1)..].Split('+').Select(c => c.Split('@')[0]).OrderBy(c => c, StringComparer.Ordinal)))
                .Distinct().Count();

        /// <summary>Most prompts one line may take before it's abandoned as a loop.</summary>
        private const int MaxPromptsPerLine = 3000;

        /// <summary>Steps of our lines that went against one of the brain's holds and paid for it (for the bench).</summary>
        public static long HoldsCharged;
        /// <summary>Held moves that matched an answer on offer at a tree node (for the bench).</summary>
        public static long HoldsMatched;
        // (CSV S5's clear-the-way bonus was dropped, user 2026-11-16: the exposure already rewards clearing their set cards first.)

        /// <summary>Lines that met a position another order of moves had reached (transpositions), for the bench and the log.</summary>
        public static long Transpositions;
        /// <summary>Nodes whose best line's value fell outside 0-1 (MctsNode.LineValue): should stay 0.</summary>
        public static long DamagedNodes;

        // One writer per worker. The watchdog only needs a snapshot when a worker has stalled.
        private sealed class LineProgress
        {
            private int _turn, _phase, _promptCount;
            private volatile Prompt? _prompt;

            public void Record(int turn, int phase, Prompt prompt, int count)
            {
                _turn = turn;
                _phase = phase;
                _promptCount = count;
                _prompt = prompt;
            }

            public void Reset() => _prompt = null;

            public string Describe() => _prompt is { } prompt
                ? $"turn {_turn}, phase 0x{_phase:x}, {prompt.State} for player {prompt.Player}, prompt {_promptCount} of the line"
                : "starting or returning an engine";
        }

        [Tunable("A search thread still busy this long past its budget (ms) is reported as stuck and left behind.", Section = "Search")]
        public static readonly Tuned<int> WatchdogGraceMs = new(20000);

        private sealed record Option(string Key, string Label, byte[] Packet, string? MandatoryReason = null);

        private readonly SearchView _view;
        private readonly MctsTree _tree;
        private readonly MctsNode _start;
        private readonly BotState _rootState;
        private readonly string? _priorKey;
        private readonly IReadOnlyCollection<int> _usedThisTurn;
        private readonly SearchSettings _settings;
        private readonly BoardEvaluator _evaluator;
        private readonly Func<SearchBrain>? _brainFactory;

        /// <summary>Moves no line of ours may contain (card code, action): the deck's very rare bans. Default: all allowed.</summary>
        public Func<int, MainAction, bool>? MayChoose { get; init; }
        internal int GladiatorSetForRoutine { get; init; }

        /// <summary>The state at the root, or an explicit state for a routine being checked.</summary>
        private TagRules.BotStrategicState? _stateOverride;
        public TagRules.BotStrategicState State
        {
            get => _stateOverride ?? _view.StrategicState;
            init => _stateOverride = value;
        }

        private ThreatenedState.Assessment AssessThreats(DuelHost host, SideSummary own, SideSummary enemy) =>
            MoveRules.AssessThreats(host, own, enemy);

        /// <summary>The deck's strong suggestions: a cost (card units) when the step at this index goes against one.</summary>
        public Func<IReadOnlyList<PlanStep>, int, IReadOnlyList<string>, SideSummary, double>? Suggestions { get; init; }

        /// <summary>
        /// A routine the brain's move starts (user, 2026-11-13: "score routines on the engine. *everything* should be tested and
        /// scored in-engine"): lines through <c>RootKey</c> follow its remaining main / battle steps while they're on offer
        /// (then the brain), so that move's score is the routine's outcome and it's weighed against every other move as usual.
        /// </summary>
        public (string RootKey, IReadOnlyList<PlanStep> Steps, Func<PlanStep, IReadOnlyCollection<string>, string?> Match)? Routine { get; init; }

        /// <summary>
        /// Whether a root move is settled (its lines, score, lines average and error, 0-1): it gets no more lines while another move isn't
        /// (user, 2026-11-24: set Icarus Attack took 1485 lines on a score settled long before, while Reinforcement of the Army, the
        /// right play, got 174). Null: no move is set aside (the bench).
        /// </summary>
        public Func<int, double, double, double, bool>? RootSettled { get; init; }

        /// <summary>The real turn is in the set window at this decision (it has set a card this Main Phase): lines start there.</summary>
        public bool StartInSetWindow { get; init; }

        /// <summary>The real turn has played out its set plan this Main Phase: lines set no more spells/traps.</summary>
        public bool StartSetPlanDone { get; init; }

        /// <summary>Most set plans offered under the set window (every combination of the settable spells/traps fits well below it).</summary>
        private const int MaxSetPlans = 128;

        /// <summary>
        /// The set plans under the set window: every combination of the spell/trap sets on offer (copies of a card counted), at least
        /// one card, no more than the free zones, with every always-set card in it (user, 2026-11-24: "it should choose all the sets it
        /// wants to make ... then execute them in a random order"). Each plan is one move; its risk (Heavy Storm on the turn's sets,
        /// single removal, Icarus Attack) is scored by the lines.
        /// </summary>
        private static List<Option> SetPlans(IdleCmdData idle, List<Option> spellSets, int room)
        {
            var byKey = spellSets.ToDictionary(o => o.Key);
            var copies = new Dictionary<string, int>();
            foreach (var card in idle.SpellSetableCards ?? Array.Empty<DynamicCard>())
            {
                string key = $"{(int)MainAction.SetSpell}:{SearchActions.CardKey(card)}";
                if (byKey.ContainsKey(key) && card.ActionIndex != null && card.ActionIndex.Length > (int)MainAction.SetSpell)
                    copies[key] = copies.GetValueOrDefault(key) + 1;
            }
            foreach (var option in spellSets)
                copies.TryAdd(option.Key, 1);
            var keys = copies.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var mandatory = spellSets.Where(o => o.MandatoryReason != null).Select(o => o.Key).ToHashSet();
            // Every copy of an always-set card is in the plan (2026-11-24: one of two Jars of Greed was set, the other kept in hand);
            // with more copies than free zones, the plan fills the zones with always-set cards only.
            int mandatoryCopies = mandatory.Sum(m => copies.GetValueOrDefault(m));
            var plans = new List<Option>();
            var chosen = new List<string>();
            bool MandatoryMet() => mandatoryCopies <= room
                ? mandatory.All(m => chosen.Count(k => k == m) == copies.GetValueOrDefault(m))
                : chosen.Count == room && chosen.All(mandatory.Contains);
            void Build(int i)
            {
                if (plans.Count >= MaxSetPlans)
                    return;
                if (i == keys.Count)
                {
                    if (chosen.Count == 0 || chosen.Count > room || !MandatoryMet())
                        return;
                    string label = "set " + string.Join(", ", chosen.Select(k => byKey[k].Label.StartsWith("set ") ? byKey[k].Label[4..] : byKey[k].Label));
                    plans.Add(new Option(SearchActions.SetPlanKey(chosen), label, byKey[chosen[0]].Packet));
                    return;
                }
                for (int n = 0; n <= copies[keys[i]] && chosen.Count + n <= room; n++)
                {
                    for (int c = 0; c < n; c++)
                        chosen.Add(keys[i]);
                    Build(i + 1);
                    chosen.RemoveRange(chosen.Count - n, n);
                }
            }
            Build(0);
            return plans;
        }

        /// <summary>
        /// Mid-chain search (DeckBotBrain.MidChain): the position is the last searched decision of our turn, and each line first
        /// plays the real game's answers since then (and their revealed cards) to reach the chain window that is the root.
        /// </summary>
        public ReplayScript? Replay { get; init; }

        /// <summary>
        /// Our field effects already used this turn, as "official id:effect description" (the rebuilt cards forgot their once-per-turn
        /// counts): not offered again at our main decisions and chain windows. By effect, not by card (seen 2026-11-15: Gearframe's
        /// summon trigger used hid its equip effect). Null: the older per-card list (usedThisTurn).
        /// </summary>
        public IReadOnlyCollection<string>? UsedEffects { get; init; }

        private bool EffectUsed(int cardId, long description) =>
            UsedEffects != null && UsedEffects.Contains($"{(SearchHost.CanonicalId(cardId) is > 0 and var official ? official : cardId)}:{description}");

        /// <summary>Lines whose replay didn't reach the chain window (for the log: each one is a bug to find), and the first reason.</summary>
        public int ReplayMisses => _replayMisses;
        private int _replayMisses;
        public string? FirstReplayMiss { get; private set; }

        private string ReplayMiss(string why)
        {
            if (Interlocked.Increment(ref _replayMisses) == 1)
                FirstReplayMiss = why;
            if (CollectReplayMisses)
                AllReplayMisses.Add(why);
            return "replay: " + why;
        }

        /// <summary>Keep every replay miss's reason (DuelSim replay-check), not only the first.</summary>
        public bool CollectReplayMisses { get; init; }
        public ConcurrentBag<string> AllReplayMisses { get; } = new();

        /// <param name="start">The node whose decision is the root prompt (the tree's root, or a decision found again).</param>
        /// <param name="priorKey">The real brain's pick at the root (its answer gets the progressive bias there).</param>
        /// <param name="rootHolds">The real brain's holds at the root: option key -> (cost, rule) a line pays for the move.</param>
        public Mcts(SearchView view, MctsTree tree, MctsNode start, BotState rootState, string? priorKey, IReadOnlyCollection<int> usedThisTurn,
            SearchSettings settings, BoardEvaluator evaluator, Func<SearchBrain>? brainFactory,
            IReadOnlyDictionary<string, (double Cost, string Rule)>? rootHolds = null)
        {
            _view = view;
            if (rootHolds != null)
                lock (start.Lock)
                    start.Holds ??= new Dictionary<string, (double, string)>(rootHolds);
            _tree = tree;
            _start = start;
            _rootState = rootState;
            _priorKey = priorKey;
            _usedThisTurn = usedThisTurn;
            _settings = settings;
            _evaluator = evaluator;
            _brainFactory = brainFactory;
            if (priorKey != null)
                lock (start.Lock)
                    if (!start.PriorAsked)
                        (start.PriorKey, start.PriorAsked) = (priorKey, true);
        }

        // ---------------------------------------------------------------- engine pool and warm-up

        /// <summary>Engine duels between searches: each keeps its loaded card scripts, so a new search starts warm.</summary>
        private static readonly ConcurrentBag<PuzzleEngine> EnginePool = new();

        private sealed class PoolReturn : IDisposable
        {
            private readonly PuzzleEngine _engine;
            public PoolReturn(PuzzleEngine engine) => _engine = engine;
            public void Dispose() => EnginePool.Add(_engine);
        }

        private static readonly object WarmLock = new();

        /// <summary>
        /// Gets the search ready for a duel's cards: scores every card (EffectEvaluator values, kept across searches) and
        /// makes sure the engine pool holds one duel per search thread that has already loaded these cards' scripts. Engines
        /// past half their life are rebuilt here, in idle time. Returns at once when everything is already warm.
        /// </summary>
        /// <returns>How long it took (ms).</returns>
        public static long WarmUp(IReadOnlyCollection<int> cardIds, int threads)
        {
            if (!SearchHost.Available || cardIds.Count == 0)
                return 0;
            var clock = Stopwatch.StartNew();
            lock (WarmLock)
            {
                var ids = cardIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
                int key = ids.Aggregate(17, (h, id) => unchecked(h * 31 + id));
                SearchHost.Register(ids);
                var values = new CardValues(Array.Empty<int>(), Array.Empty<int>());
                foreach (int id in ids)
                    values.Get(id, 0);

                var engines = new List<PuzzleEngine>();
                while (EnginePool.TryTake(out var engine))
                    engines.Add(engine);
                var rng = new Random();
                while (engines.Count < threads)
                    engines.Add(new PuzzleEngine(SearchHost.Cards, SearchHost.Scripts, rng.Next()));
                string? lua = null;
                foreach (var engine in engines)
                {
                    if (engine.Uses > PuzzleEngine.RebuildEvery / 2)
                        engine.Discard(); // rebuild now, while idle
                    if (engine.WarmedFor != key)
                    {
                        // A position holding every card (all in the decks) makes the engine load every script once.
                        if (lua == null)
                        {
                            var position = new SearchPosition();
                            int n = 0;
                            foreach (int id in ids.Where(SearchHost.Knows))
                                position.Add(id, n++ % 2, SearchPosition.LocDeck, 0, SearchPosition.PosFaceDown);
                            lua = position.ToLua(DuelHost.PuzzleFlags);
                        }
                        engine.Load(lua, out _)?.Dispose();
                        engine.WarmedFor = key;
                    }
                    EnginePool.Add(engine);
                }
            }
            return clock.ElapsedMilliseconds;
        }

        private static int _warming;

        /// <summary>WarmUp on a low-priority background thread (the opponent's turn, or as we end ours).</summary>
        public static void WarmUpInBackground(IReadOnlyCollection<int> cardIds, int threads)
        {
            if (!SearchHost.Available || cardIds.Count == 0 || Interlocked.Exchange(ref _warming, 1) == 1)
                return;
            var thread = new Thread(() =>
            {
                try { WarmUp(cardIds, threads); }
                catch { /* only an optimisation */ }
                finally { Interlocked.Exchange(ref _warming, 0); }
            }) { IsBackground = true, Name = "mcts warm-up", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        // ---------------------------------------------------------------- fears and reserves, per strategic state (StateTuning)

        [Tunable("How much a line's exposure to Torrential Tribute counts (1 = in full). Per strategic state: Desperate 0 (user, 2026-11-24: \"remove the fear of heavy storm and traps\").", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> TorrentialFear = new(1);
        [Tunable("How much a line's exposure to Mirror Force counts (1 = in full). Per strategic state: Desperate 0.", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> MirrorForceFear = new(1);
        [Tunable("How much a line's exposure to Heavy Storm counts (1 = in full). Per strategic state: Desperate 0.", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> HeavyStormFear = new(1);
        [Tunable("How much a line's exposure to single removal on its best monster counts (1 = in full). Per strategic state: Desperate 0.", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> SingleRemovalFear = new(1);
        [Tunable("How much a line's exposure to Icarus Attack counts (1 = in full). Per strategic state: Desperate 0.", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> IcarusFear = new(1);
        [Tunable("While pressuring (PressuringState: an open opponent, our attackers on their clock), monsters in hand and graveyard reserves count this much extra in the card score, so lines keep them back. Per strategic state: Desperate 0.", StateDefaults = "Desperate=0")]
        public static readonly Tuned<double> PressuringReserveWeight = new(1);

        // ---------------------------------------------------------------- the search

        private int _maxDepth;

        /// <summary>Searches until the budget (or MaxIterations, or cancellation), then reports the root's answers.</summary>
        public SearchOutcome Run(int seed, CancellationToken ct = default, ThreadPriority priority = ThreadPriority.Normal)
        {
            // Every line of this search reads the static numbers of the root's strategic state (StateTuning), its threads included.
            using var stateScope = StateTuning.Use(State);
            SearchHost.Register(_view.AllKnownIds());
            var clock = Stopwatch.StartNew();
            int threads = Math.Max(1, _settings.Threads);
            var iterations = new int[threads];
            var failures = new int[threads];
            var progress = new LineProgress?[threads];
            string? firstFailure = null;
            var deadline = _settings.BudgetMs <= 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(_settings.BudgetMs);
            long maxIterations = _settings.MaxIterations;
            long done = 0;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lineCancellation = stop.Token;

            void Work(int t)
            {
                var lineProgress = new LineProgress();
                Volatile.Write(ref progress[t], lineProgress);
                var rng = new Random(seed * 7919 + t);
                // A pooled engine duel keeps the card scripts it has loaded from one decision to the next (see WarmUp).
                var engine = EnginePool.TryTake(out var pooled) ? pooled : new PuzzleEngine(SearchHost.Cards, SearchHost.Scripts, rng.Next());
                using var giveBack = new PoolReturn(engine);
                var brain = _brainFactory?.Invoke();
                while (clock.Elapsed < deadline && Interlocked.Read(ref done) < maxIterations && !lineCancellation.IsCancellationRequested)
                {
                    string? failure = Iterate(rng, engine, brain, lineProgress, lineCancellation);
                    iterations[t]++;
                    Interlocked.Increment(ref done);
                    if (failure != null)
                    {
                        failures[t]++;
                        Interlocked.CompareExchange(ref firstFailure, failure, null);
                        // A position the engine can't build won't build next time either.
                        if (failure.StartsWith("position"))
                            break;
                    }
                }
            }

            // Dedicated threads, not the thread pool: live, the pool is busy with the bot's own blocking server calls and hands
            // out workers slowly (seen 2026-11-10: about 6 lines in 2-4 s instead of hundreds in 1 s).
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                try { Work(t); }
                catch (OperationCanceledException) when (lineCancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    failures[t]++;
                    Interlocked.CompareExchange(ref firstFailure, $"search thread failed: {ex.Message}", null);
                }
            }) { IsBackground = true, Name = $"mcts {t}", Priority = priority }).ToList();
            foreach (var worker in workers)
                worker.Start();
            // Watchdog (2026-11-17: bots went silent mid-decision until the server gave up): a thread still busy WatchdogGraceMs past the
            // budget is stuck in one line; it's reported (where its line was) and left behind, and the search returns what it has.
            var grace = TimeSpan.FromMilliseconds(WatchdogGraceMs);
            var hardStop = _settings.BudgetMs > 0 ? deadline + grace : TimeSpan.MaxValue;
            var cancellationStop = TimeSpan.MaxValue;
            for (int t = 0; t < workers.Count; t++)
            {
                var worker = workers[t];
                while (!worker.Join(0))
                {
                    var now = clock.Elapsed;
                    if (ct.IsCancellationRequested && cancellationStop == TimeSpan.MaxValue)
                        cancellationStop = now + grace;
                    var cutoff = cancellationStop < hardStop ? cancellationStop : hardStop;
                    if (now < cutoff)
                    {
                        worker.Join((int)Math.Clamp(Math.Ceiling((cutoff - now).TotalMilliseconds), 1, 100));
                        continue;
                    }
                    stop.Cancel();
                    string where = Volatile.Read(ref progress[t])?.Describe() ?? "starting or returning an engine";
                    string report = $"search WATCHDOG: thread '{worker.Name}' did not return within {WatchdogGraceMs / 1000} s of the search stopping " +
                                    $"(last seen: {where}); using the lines that finished";
                    Console.WriteLine(report);
                    Interlocked.CompareExchange(ref firstFailure, report, null);
                    break;
                }
            }
            Interlocked.Add(ref _tree.Iterations, iterations.Sum());
            var workerElapsed = clock.Elapsed;
            var outcome = Outcome(workerElapsed, iterations.Sum(), failures.Sum(), firstFailure);
            return outcome with { ReportElapsed = clock.Elapsed - workerElapsed };
        }

        /// <summary>Lines a follow-up answer needs before a move's value is read through it (MctsNode.LineValue). Tune from logs.</summary>
        [Tunable("Lines a follow-up answer needs before a move's value is read through it (MctsNode.LineValue). Tune from logs.", Section = "Search")]
        public static readonly Tuned<int> LineValueVisits = new(30);
        [Tunable("Lines a follow-up needs before UnexploredBest counts its mean: a move still scored on the average of its follow-ups, one of which already looks as good as the best move, keeps getting lines.")]
        public static readonly Tuned<int> UnexploredMinVisits = new(10);
        [Tunable("While no follow-up of ours has LineValueVisits, a move is read through its best follow-up with UnexploredMinVisits, this many standard errors down (its luck), never below its plain average. User, 2026-11-19: decisions not penalised for the lines' bad play deep down. Since 2026-11-24 also taken off explored follow-ups before their best is taken (the best of many thin ones is the luckiest).")]
        public static readonly Tuned<double> UnexploredErrors = new(1);

        /// <summary>Brain holds that only exist for the Battle Phase: a line that never enters it doesn't pay them.</summary>

        /// <summary>A follow-up answer counts for a move's value only when it was on offer at least this share of the times lines reached
        /// that decision: an answer that depends on chance (a draw, a random effect) isn't ours to pick, so it counts through the average.</summary>
        [Tunable("A follow-up answer counts for a move's value only when it was on offer at least this share of the times lines reached that decision (an answer that depends on a lucky draw isn't ours to pick).", Section = "Search")]
        public static readonly Tuned<double> LineValueAvailableShare = new(0.9);

        /// <summary>This search's line totals per root key, averaged.</summary>
        private Dictionary<string, RootDetail> Details()
        {
            lock (_details)
                return _details.Where(kv => kv.Value.Lines > 0).ToDictionary(kv => kv.Key, kv =>
                {
                    var d = kv.Value;
                    double n = d.Lines;
                    // Averaged over the lines that didn't lose (PartLines keeps their count).
                    var parts = _parts.TryGetValue(kv.Key, out var sums) && sums.TryGetValue(PartLines, out var scored) && scored > 0
                        ? sums.ToDictionary(p => p.Key, p => p.Key == PartLines ? p.Value : p.Value / scored) : null;
                    return new RootDetail(d.Lines, d.Wins / n, d.Losses / n, d.OurLp / n, d.TheirLp / n, d.OurBoard / n, d.TheirBoard / n, d.Penalty / n, parts);
                });
        }

        // Per root key, this search's line totals (see RootDetail).
        private readonly Dictionary<string, (int Lines, int Wins, int Losses, double OurLp, double TheirLp, double OurBoard, double TheirBoard, double Penalty)> _details = new();
        // Per root key, the score breakdown's parts summed over its lines that didn't lose (under _details' lock).
        private readonly Dictionary<string, Dictionary<string, double>> _parts = new();
        /// <summary>The breakdown's count of lines behind its parts (not a part).</summary>
        internal const string PartLines = "~lines";

        private sealed record LineStart(SideSummary Ours, SideSummary Theirs, CardPiles OurPiles, CardPiles TheirPiles,
            BoardEvaluator.AdvantageCounts Advantage, int OpponentNormalDraws, bool Pressuring,
            IReadOnlyDictionary<string, double> Parts, double Center);

        private LineStart? _lineStart;
        private readonly object _lineStartLock = new();

        /// <summary>The position a tree's lines are measured from (MctsTree.Baseline): the first search's root.</summary>
        internal sealed record TreeBaseline(SideSummary Ours, SideSummary Theirs, CardPiles OurPiles, CardPiles TheirPiles, bool Pressuring);

        // The line's starting point: the tree's baseline position (its first search's root; this search's root when it is the first),
        // valued by this search's evaluator, so every line in the tree is measured from the same position.
        private void CaptureLineStart(DuelHost host)
        {
            if (Volatile.Read(ref _lineStart) != null)
                return;
            lock (_lineStartLock)
            {
                if (_lineStart != null)
                    return;
                var here = host.Summary(0);
                var hereTheirs = host.Summary(1);
                var fresh = new TreeBaseline(here, hereTheirs, ReadCardPiles(host, 0), ReadCardPiles(host, 1),
                    PressuringState.Applies(here, hereTheirs));
                var baseline = Interlocked.CompareExchange(ref _tree.Baseline, fresh, null) ?? fresh;
                var (ours, theirs, pressuring) = (baseline.Ours, baseline.Theirs, baseline.Pressuring);
                var parts = _evaluator.PositionParts(ours, theirs);
                double center = parts.Where(p => !p.Key.Contains(BoardEvaluator.SubPart)).Sum(p => p.Value);
                // Their normal draws are counted in this search's own simulated duel (no draw of theirs falls between two decisions of one tree).
                Volatile.Write(ref _lineStart, new(ours, theirs, baseline.OurPiles, baseline.TheirPiles,
                    _evaluator.CountAdvantage(ours, theirs, pressuring), host.OpponentNormalDraws.Count, pressuring, parts, center));
            }
        }

        internal readonly record struct CardPiles(int[] Banished, int[] ExtraDeck);
        private static CardPiles ReadCardPiles(DuelHost host, int side) => new(host.BanishedCodes(side), host.ExtraDeckCodes(side));

        private static void RecordCardChanges(Dictionary<string, double> parts, string board, string advantage,
            SideSummary start, SideSummary end, CardPiles startPiles, CardPiles endPiles)
        {
            void Compare<T>(string zone, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> name, bool onField)
                where T : notnull
            {
                var changes = new Dictionary<T, int>();
                foreach (var card in before)
                    changes[card] = changes.GetValueOrDefault(card) - 1;
                foreach (var card in after)
                    changes[card] = changes.GetValueOrDefault(card) + 1;
                foreach (var (card, change) in changes)
                {
                    if (change == 0)
                        continue;
                    string direction = change > 0 ? "Added" : "Removed";
                    string row = OmegaBotWrapper.Logging.ScoreBreakdown.CardChanges + $"{zone}: {direction} {name(card)}";
                    if (onField)
                        parts[board + row] = parts.GetValueOrDefault(board + row) + Math.Abs(change);
                    parts[advantage + row] = parts.GetValueOrDefault(advantage + row) + Math.Abs(change);
                }
            }

            static string Card(int code) => code <= 0 || code == SearchView.InertCard ? "unknown card" : OmegaBotWrapper.Logging.CardNames.GetWithId(code);
            Compare("Monsters", start.Monsters, end.Monsters,
                m => $"{Card(m.Code)} ({(m.FaceUp ? "face-up" : "set")}, {(m.AttackPosition ? "attack" : "defence")}, {m.Attack} ATK/{m.Defense} DEF, level {m.Level}{(m.Negated ? ", negated" : "")})", true);
            Compare("Spells/traps", start.SpellCodes, end.SpellCodes,
                c => $"{Card(c.Code)} ({(c.FaceUp ? "face-up" : "set")})", true);
            Compare("Hand", start.HandCodes, end.HandCodes, Card, false);
            Compare("Graveyard", start.GraveCodes, end.GraveCodes, Card, false);
            Compare("Deck", start.DeckCodes, end.DeckCodes, Card, false);
            Compare("Banished", startPiles.Banished, endPiles.Banished, Card, false);
            Compare("Extra Deck", startPiles.ExtraDeck, endPiles.ExtraDeck, Card, false);
        }

        /// <summary>The root's answers, most visited first, with the line below each.</summary>
        private SearchOutcome Outcome(TimeSpan elapsed, int iterations, int failures, string? firstFailure)
        {
            List<(string Key, string Label, MctsNode Node)> MoveSnapshot(MctsNode parent) =>
                parent.Children.Where(kv => IsMove(kv.Key)).Select(kv => (kv.Key, parent.LabelOf(kv.Key, kv.Value), kv.Value)).ToList();
            static List<RootStat> ScoreMoves(List<(string Key, string Label, MctsNode Node)> moves) =>
                moves.Select(m => new RootStat(m.Key, m.Label, m.Node.Visits, m.Node.LineValue(LineValueVisits),
                    m.Node.Mean, m.Node.StdErr, m.Node.UnexploredBest(LineValueVisits)))
                    .OrderByDescending(s => s.Visits).ThenByDescending(s => s.Mean).ToList();
            List<(string Key, string Label, MctsNode Node)> rootMoves;
            Dictionary<string, (double Cost, string Rule)>? rootHolds;
            int rootVisits, rootArrivals;
            Dictionary<string, (int Brain, int Rule, int Engine)>? rootMissing;
            lock (_start.Lock)
            {
                rootMoves = MoveSnapshot(_start);
                rootVisits = _start.Visits;
                rootHolds = _start.Holds == null ? null : new Dictionary<string, (double, string)>(_start.Holds);
                rootArrivals = _start.RootArrivals;
                rootMissing = _start.Missing == null ? null : new Dictionary<string, (int, int, int)>(_start.Missing);
            }
            // Scoring walks the graph and locks each node it visits. Never hold a parent lock across that walk.
            var stats = ScoreMoves(rootMoves);
            var rootNodes = rootMoves.ToDictionary(m => m.Key, m => m.Node);
            string? followUp = null;
            // Per root choice whose next decision is our own card pick (an attack target): every pick tried there, with its stats.
            var followUps = new Dictionary<string, List<RootStat>>();
            foreach (var s in stats.Take(8))
            {
                var child = rootNodes[s.Key];
                List<(string Key, string Label, MctsNode Node)>? picks;
                lock (child.Lock)
                    picks = child.ChildState is BotState.SelectCard && child.ChildPlayer == 0 ? MoveSnapshot(child) : null;
                if (picks == null)
                    continue;
                if (s == stats[0])
                    followUp = picks.OrderByDescending(p => p.Node.Visits).Select(p => p.Key).FirstOrDefault();
                followUps[s.Key] = ScoreMoves(picks);
            }
            return new SearchOutcome(stats, iterations, failures, elapsed, firstFailure, followUp, _mismatch, Plans(stats), _traces.ToList(),
                rootVisits, _maxDepth, BestLine(), rootHolds, _start.PriorKey,
                stats.Take(5).ToDictionary(s => s.Key, s => BestLine(s.Key)), followUps, Details(), Transposed(stats),
                stats.ToDictionary(s => s.Key, s => ScoredLine(s.Key)))
            {
                RootArrivals = rootArrivals,
                RootMissing = rootMissing,
            };
        }

        /// <summary>
        /// A line at the root (our main phase or chain decision): which of the decision's moves its own move list (<paramref name="offered"/>)
        /// left out, and why (MctsNode.Missing). The moves: the engine's in this line, and the real decision's (LiveRootKeys).
        /// </summary>
        private void NoteRootMissing(Prompt prompt, List<Option> offered, SearchBrain? brain)
        {
            List<string> engineKeys;
            var blocked = new HashSet<string>();
            switch (prompt.Data)
            {
                case IdleCmdData idle:
                {
                    var all = SearchActions.Idle(idle);
                    engineKeys = all.Select(c => c.Key).ToList();
                    if (brain != null)
                    {
                        var moves = brain.BlockedIdleMoves(prompt, idle); // the same prompt's: computed once (OptionsFor asked already)
                        blocked.UnionWith(all.Where(c => moves.Contains((c.Response.Action, c.Response.Index))).Select(c => c.Key));
                    }
                    break;
                }
                case SelectChainData chain:
                    engineKeys = SearchActions.ChainOptionKeys(chain);
                    break;
                default:
                    return;
            }
            // Under the real decision's keys where only a zone differs (as the root's options: AsLiveKeys).
            var map = LiveRootKeys == null ? new Dictionary<string, string>() : SearchActions.ToLiveKeys(engineKeys, LiveRootKeys);
            string Live(string key) => map.TryGetValue(key, out var real) ? real : key;
            var have = offered.Select(o => o.Key).ToHashSet();
            var engineLive = engineKeys.Select(Live).ToHashSet();
            var blockedLive = blocked.Select(Live).ToHashSet();
            var decisionKeys = engineLive.Union(LiveRootKeys ?? Array.Empty<string>()).ToList();
            lock (_start.Lock)
            {
                _start.RootArrivals++;
                var missing = _start.Missing ??= new Dictionary<string, (int, int, int)>();
                foreach (string key in decisionKeys)
                {
                    var (b, r, e) = missing.TryGetValue(key, out var had) ? had : (0, 0, 0);
                    if (!engineLive.Contains(key)) e++;
                    else if (have.Contains(key)) { missing[key] = (b, r, e); continue; }
                    else if (blockedLive.Contains(key)) b++;
                    else r++;
                    missing[key] = (b, r, e);
                }
            }
        }

        /// <summary>
        /// A root move's best line through the tree with each step's score (for the log's breakdown): our answers by the best score,
        /// theirs by the most lines (as StepAt), on past the end of our turn into theirs while the tree has explored answers. Passes are
        /// walked past, not shown.
        /// </summary>
        private List<LineStepStat> ScoredLine(string firstKey)
        {
            // To the end of our turn (user, 2026-11-20: "show the full line to end of turn ... they get very uncertain but that's fine"):
            // every tree step with lines behind it (the settled pick where one has MinPlanVisits, else the most-visited answer), then,
            // where the tree ends, the brain's own play to the turn's end in the line that made the last tree step (its RolloutTail).
            var steps = new List<LineStepStat>();
            var seen = new HashSet<MctsNode>(ReferenceEqualityComparer.Instance);
            var node = _start;
            bool turnOver = false;
            for (int guard = 0; guard < 80; guard++)
            {
                KeyValuePair<string, MctsNode> next;
                string label;
                int player;
                lock (node.Lock)
                {
                    if (node.Children.Count == 0)
                        break;
                    if (guard == 0)
                    {
                        if (!node.Children.TryGetValue(firstKey, out var first))
                            break;
                        next = new KeyValuePair<string, MctsNode>(firstKey, first);
                    }
                    else if (StepAt(node, ours: node.ChildPlayer == 0) is { } step)
                        next = step;
                    else if (node.Children.Where(kv => IsMove(kv.Key) && kv.Value.Visits > 0)
                                 .OrderByDescending(kv => kv.Value.Visits).ThenByDescending(kv => kv.Value.Mean).FirstOrDefault() is { Value: not null } thin)
                        next = thin;
                    else
                        break;
                    player = node.ChildPlayer;
                    label = node.LabelOf(next.Key, next.Value);
                }
                var child = next.Value;
                if (!seen.Add(child))
                    break;
                if (next.Key != "pass")
                {
                    int visits;
                    double err;
                    lock (child.Lock)
                    {
                        visits = child.Visits;
                        err = child.StdErr;
                    }
                    steps.Add(new LineStepStat(player == 0 ? $"[{State}] {label}" : label,
                        player == 0, false, visits, child.LineValue(LineValueVisits), err));
                }
                node = child;
                if (player == 0 && next.Key == "end")
                {
                    turnOver = true;
                    break;
                }
            }
            if (!turnOver && node != _start)
            {
                List<string>? rest;
                lock (node.Lock)
                    rest = node.RolloutTail?.ToList();
                foreach (var label in rest ?? new List<string>())
                    steps.Add(new LineStepStat($"[{State}] {label}", true, false, 0, double.NaN, double.NaN, Rollout: true));
            }
            return steps;
        }

        private void AttachBrain(SearchBrain? brain, DuelHost host)
        {
            bool startsAtDecision = FromTurnStart == null;
            brain?.Attach(host, Math.Max(0, _view.RealTurn - (1 + _view.SkipTurns)), RootTurn,
                startsAtDecision && _view.NormalSummonUsed, startsAtDecision ? _view.ActivatedThisTurn : Array.Empty<int>(), _evaluator);
            // A line rebuilt at the decision (not replayed from the turn's start): the real turn's Normal Summons so far.
            if (startsAtDecision && _view.NormalSummonUsed)
                host.MarkNormalSummonUsed(0);
            if (startsAtDecision && _view.EnemyNormalSummonUsed)
                host.MarkNormalSummonUsed(1);
        }

        /// <summary>One iteration. Returns a failure note, or null when it scored a line.</summary>
        private string? Iterate(Random rng, PuzzleEngine engine, SearchBrain? brain, LineProgress progress, CancellationToken ct)
        {
            long start = Stopwatch.GetTimestamp();
            var position = _view.Determinize(rng);
            SearchTimings.Add(ref SearchTimings.Cards, position.Cards.Count);
            long dealt = Stopwatch.GetTimestamp();
            var lua = position.ToLua(_view.FirstTurnOfDuel ? DuelHost.PuzzleFlags & ~0x02UL : DuelHost.PuzzleFlags); // 0x02: attacks on the first turn
            SearchTimings.Add(ref SearchTimings.Deal, dealt - start);
            SearchTimings.Add(ref SearchTimings.GeneratePuzzle, Stopwatch.GetTimestamp() - dealt);
            var host = engine.Load(lua, out var error);
            if (host == null)
                return $"position: {error}";
            try
            {
                AttachBrain(brain, host);
                var failure = Play(rng, host, position, brain, progress, ct);
                if (failure != null)
                    engine.Discard(); // a line that went wrong: start the next one on a fresh duel
                return failure;
            }
            finally
            {
                long disposing = Stopwatch.GetTimestamp();
                host.Dispose();
                progress.Reset();
                SearchTimings.Add(ref SearchTimings.Dispose, Stopwatch.GetTimestamp() - disposing);
                SearchTimings.Add(ref SearchTimings.Lines, 1);
            }
        }

        private readonly ConcurrentQueue<string> _traces = new();

        // A step of the path: the node entered, and the virtual loss put on it while the line is played.
        private readonly record struct PathStep(MctsNode Node, double VirtualLoss);

        private string? Play(Random rng, DuelHost host, SearchPosition position, SearchBrain? brain, LineProgress progress, CancellationToken ct)
        {
            var trace = _settings.TraceLines > 0 ? new List<string>() : null;
            string? rootKey = null;
            TagRules.BotStrategicState? lastTracedStrategicState = null;
            if (trace != null)
                host.Event += line => trace.Add(line);
            long phaseStart = Stopwatch.GetTimestamp();
            int prompts = 0;
            host.SetLifePoints(position.LifePoints[0], position.LifePoints[1]);

            var path = new List<PathStep>();
            var node = _start;
            bool inTree = true, rootReached = false, autoBattle = _view.InBattlePhase, setWindowChosen = false;
            // The line's set plan (SearchActions.SetWindowKey): chosen once in the set window, its sets still to play (in a random order).
            bool setPlanChosen = false;
            var pendingSets = new List<string>();
            bool PlanDone() => setPlanChosen && pendingSets.Count == 0 && host.Turn == SearchTurn;
            int positionWindowPhase = -1; // the turn and Main Phase (turn * 0x10000 + phase) in which the line opened the position window (SearchMoveRules), or -1
            bool anchorReached = false;
            int replayNext = 0, extraPrompts = 0;
            long revealsUsed = 0;
            int pendingTargets = -1;
            var line = new MctsPolicy.LineState();
            var steps = new List<PlanStep>();
            double penalty = 0;
            int previousBattles = host.SurvivingAttackers.Count;
            // The score breakdown of this line (card units, by name; user, 2026-11-20): Cost records a named part of penalty (a cost
            // positive, a bonus negative); what penalty holds beyond them shows as one "other" part.
            var lineParts = new Dictionary<string, double>();
            double namedPenalty = 0;
            void Cost(string name, double cost)
            {
                if (cost == 0)
                    return;
                namedPenalty += cost;
                lineParts[name] = (lineParts.TryGetValue(name, out var had) ? had : 0) - cost;
            }
            // The node this line created (the tree's new leaf) and our moves after it to the end of our turn (its RolloutTail).
            MctsNode? tailNode = null;
            List<string>? tail = null;
            int lastTurn = 0, depth = 0;
            string? lastKey = null; // the answer that led into the current node (for transpositions)
            bool attacked = false;
            (SideSummary Ours, SideSummary Theirs, bool Attacked)? turnEnd = null;
            bool followRoutine = false;
            int randomSeen = host.RandomEvents;
            int routineNext = 0;
            // The sequence penalties of our turn (LineTally), starting from what the real turn has already done (TurnRecord).
            var tally = new LineTally(_view.Record, _view.InBattlePhase || _view.BattlePhaseDone, _view.FirstTurnOfDuel)
            {
                // Their board can remove a monster of ours in our battle: a face-down monster, or a face-up one with a "destroyed by battle" effect.
                BattleRemovalRisk = () => host.Summary(1).Monsters.Count(m => !m.FaceUp ||
                    SearchHost.Available && SearchHost.Cards.Get(m.Code)?.Text is { } text &&
                    System.Text.RegularExpressions.Regex.IsMatch(text, @"destroyed by battle", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                // No trap can be activated: a face-up, unnegated Jinzo on either side, or a face-up Royal Decree.
                TrapsNegated = () => host.Summary(0).Monsters.Concat(host.Summary(1).Monsters)
                                         .Any(m => m.FaceUp && !m.Negated && LineTally.Is(m.Code, new[] { CardId.Jinzo })) ||
                                     host.Summary(0).SpellCodes.Concat(host.Summary(1).SpellCodes)
                                         .Any(s => s.FaceUp && LineTally.Is(s.Code, new[] { CardId.RoyalDecree })),
                // Our face-up Jinzos next to our face-up Cyber Dragon, and our graveyard's Jinzos (the doomed-Jinzo fuse).
                OurJinzos = () =>
                {
                    var ours = host.Summary(0);
                    bool cyber = ours.Monsters.Any(m => m.FaceUp && LineTally.Is(m.Code, new[] { CardId.CyberDragon }));
                    return (cyber ? ours.Monsters.Count(m => m.FaceUp && LineTally.Is(m.Code, new[] { CardId.Jinzo })) : 0,
                            ours.GraveCodes.Count(c => LineTally.Is(c, new[] { CardId.Jinzo })));
                },
                // A DARK monster in our hand (Allure of Darkness banishes one instead of the whole hand).
                DarkInHand = () => host.Summary(0).HandCodes.Any(c => (SearchHost.Type(c) & 0x1) != 0 && (SearchHost.Attribute(c) & 0x20) != 0),
                // Our other face-up attack-position monsters, while the Battle Phase is still to come or under way (#135).
                OtherAttackers = code =>
                {
                    if (host.Turn != SearchTurn || _view.FirstTurnOfDuel ||
                        host.Phase is not ((int)OmegaBotWrapper.Enum.DuelPhase.Main1 or (int)OmegaBotWrapper.Enum.DuelPhase.BattleStart or
                            (int)OmegaBotWrapper.Enum.DuelPhase.BattleStep or (int)OmegaBotWrapper.Enum.DuelPhase.Battle))
                        return 0;
                    var attackers = host.Summary(0).Monsters.Where(m => m.FaceUp && m.AttackPosition).ToList();
                    int self = attackers.FindIndex(m => m.Code == code);
                    return Math.Max(0, attackers.Count - (self >= 0 ? 1 : 0));
                },
            };
            penalty += tally.Start;
            Cost("Brain rules our turn already broke", tally.Start);
            tally.Charged = Cost;
            Action<string>? note = trace != null ? trace.Add : null;
            void NoteOurMainMove(Prompt p, byte[] packet, List<Option>? knownOptions = null)
            {
                if (trace != null && p.Player == 0 && p.Data is IdleCmdData or SelectChainData or SelectEffectYnData or SelectPositionData &&
                    OptionsFor(p, host.TurnPlayer == 0, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, brain: brain, setPlanDone: PlanDone()) is { Count: > 0 } traceOptions &&
                    MatchOption(traceOptions, packet, p.State) is { MandatoryReason: { } reason } required)
                    trace.Add($"  mandatory action: {required.Label} ({reason})");
                // Outside the tree, our moves to the end of our turn: the new leaf's rollout tail (for the log's line).
                if (tail != null && !inTree && rootReached && p.Player == 0 && host.Turn == SearchTurn && tail.Count < 60 &&
                    (knownOptions ?? OptionsFor(p, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, brain: brain, setPlanDone: PlanDone())) is { Count: > 0 } offered &&
                    MatchOption(offered, packet, p.State) is { Key: not "pass" } taken)
                    tail.Add(taken.Label);
                if (rootReached && p.Player == 0 && host.Turn == SearchTurn)
                {
                    if (p.Data is IdleCmdData && !host.SearchNormalSummonOrSetUsed &&
                        !(FromTurnStart == null && SearchTurn == RootTurn && _view.NormalSummonUsed) &&
                        knownOptions?.Any(option => option.Packet.Length >= 2 &&
                            BitConverter.ToUInt16(option.Packet, 0) is (ushort)MainAction.Summon or (ushort)MainAction.SetMonster) == true &&
                        host.CountIn(1, SearchPosition.LocMZone) == 0)
                        tally.NoteNormalSummonOrSetOpportunity();
                    penalty += tally.OnOurAnswer(p, packet, note);
                }
                else if (rootReached && p.Player == 0 && host.TurnPlayer != 0 && p.Data is SelectChainData theirTurnChain)
                    penalty += tally.OnTheirTurnChain(theirTurnChain, packet, host.EventlessWindow, host.Attacking,
                        (loc, seq) => host.Doomed(0, loc, seq) || loc == (byte)CardLocation.SpellZone &&
                            _evaluator.BackrowDoomRule?.Invoke(false, (DuelPhase)host.Phase, host.Summary(0)) == true, host.Phase,
                        () => host.CountIn(0, SearchPosition.LocMZone) + host.CountIn(0, SearchPosition.LocSZone),
                        () => host.Summary(0).HandCodes, note);
                if (rootReached && p.Player == 0 && p.Data is SelectCardData destroyPick)
                    penalty += tally.OnOurPick(destroyPick, packet, note);
            }

            // Overexposure along the line (user, 2026-11-15: part of the score): Torrential at each of our summons, Mirror Force at
            // each of our attacks (the worst point of each: they spring it then), Heavy Storm on our sets as the turn ends.
            // Peaks start below any value: an exposure is negative when we threaten lethal and the trap they must spend costs them more than
            // it takes (BoardEvaluator.Floor); a line that never met one counts 0 (Peak below).
            double peakTorrential = double.NegativeInfinity, peakMirror = double.NegativeInfinity, heavyExposure = 0,
                peakSpot = double.NegativeInfinity, peakIcarus = double.NegativeInfinity;
            // The same peaks without monsters of theirs we control (C1, user 2026-11-16): used when those all went back (or died) by the end
            // of our turn, i.e. borrowed ones (Brain Control). One still ours as the turn ends (Goyo's) counts.
            double ownTorrential = double.NegativeInfinity, ownMirror = double.NegativeInfinity, ownSpot = double.NegativeInfinity, ownIcarus = double.NegativeInfinity;
            bool sawForeign = false;
            SideSummary Own(SideSummary o)
            {
                if (o.Foreign == 0)
                    return o;
                sawForeign = true;
                return host.Summary(0, excludeForeign: true);
            }
            // Single-card removal and Icarus Attack: at our summons, our attacks and as our turn ends (they can use them any time).
            void NoteLesser(SideSummary o, SideSummary t)
            {
                peakSpot = Math.Max(peakSpot, _evaluator.SpotExposure(o, t));
                peakIcarus = Math.Max(peakIcarus, _evaluator.IcarusExposure(o, t));
                var own = Own(o);
                ownSpot = Math.Max(ownSpot, _evaluator.SpotExposure(own, t));
                ownIcarus = Math.Max(ownIcarus, _evaluator.IcarusExposure(own, t));
            }
            int ourMonsters = -1, ourSpells = -1, drawsAtRoot = 0;
            int OurCards() => host.Summary(0).HandCodes.Count(code => code != SearchView.InertCard) +
                host.CountIn(0, SearchPosition.LocMZone) + host.CountIn(0, SearchPosition.LocSZone);
            int ourCardsAtRoot = -1; // CSV S15: a won line's cards spent
            var main2Codes = new List<int>(_view.Main2Summoned); // our Main Phase 2 summons (the real turn's, then the line's)
            IReadOnlyCollection<int> activatedInTurn = Array.Empty<int>(); // as our turn ended: the cards we activated in it
            bool lylaEffectUsed = false; // as our turn ended: Lyla's Main Phase destroy used in it (the line's, or the real turn's before a direct rebuild)
            bool LylaEffectUsedInOurTurn() =>
                host.ActivatedInMainPhase(0, SearchTurn).Any(code => SearchHost.Is(code, LylaLightswornSorceress) || SearchHost.Is(code, LylaLightswornSorceressPreErrata)) ||
                FromTurnStart == null && _view.LylaEffectUsed;
            List<int>? lastOwnCodes = null;
            // Our activations in our turn: the line's (the engine's record), and before a direct rebuild the real turn's (the view's).
            IReadOnlyCollection<int> ActivatedInOurTurn() =>
                host.ActivatedInTurn(0, SearchTurn).Concat(FromTurnStart == null ? _view.ActivatedThisTurn : Enumerable.Empty<int>()).ToHashSet();
            double mirrorRead = 1; // the line's own Mirror Force read (BoardEvaluator.MirrorReadResidual)
            void NoteMirror()
            {
                if (!rootReached || host.Turn != SearchTurn)
                    return;
                var (o, t) = (host.Summary(0), host.Summary(1));
                peakMirror = Math.Max(peakMirror, _evaluator.MirrorExposure(o, t, mirrorRead));
                ownMirror = Math.Max(ownMirror, _evaluator.MirrorExposure(Own(o), t, mirrorRead));
                NoteLesser(o, t);
                // Their unknown sets stay down in the line (they're inert): an attack worth a Mirror Force for them lowers the chance for the
                // line's later attacks, as the brain's read does between real attacks (StapleEvidence).
                var attackers = o.Monsters.Where(m => m.FaceUp && m.AttackPosition).ToList();
                if (attackers.Count >= 2 || attackers.Any(m => m.Attack >= _evaluator.StrongMonsterAttack))
                    mirrorRead *= _evaluator.MirrorReadResidual;
            }
            void NoteSummons()
            {
                // Our monsters that turned up in our Main Phase 2: out of our clock (SearchView.Main2Summoned).
                if (host.Turn == SearchTurn)
                {
                    var codesNow = host.MonsterCodes(0); // not the whole side summary (its hand, graveyard and deck reads): line-time audit 2026-11-25
                    // (The duel's first turn counts as Main Phase 2: no battle possible; user, 2026-11-18.)
                    if ((host.Phase == (int)DuelPhase.Main2 || _view.FirstTurnOfDuel && host.Phase == (int)DuelPhase.Main1) && lastOwnCodes != null)
                    {
                        var before = lastOwnCodes.ToList();
                        main2Codes.AddRange(codesNow.Where(c => !before.Remove(c)));
                    }
                    lastOwnCodes = codesNow;
                }
                int now = host.CountIn(0, SearchPosition.LocMZone);
                if (ourMonsters >= 0 && now > ourMonsters)
                {
                    var (o, t) = (host.Summary(0), host.Summary(1));
                    peakTorrential = Math.Max(peakTorrential, _evaluator.TorrentialExposure(o, t));
                    ownTorrential = Math.Max(ownTorrential, _evaluator.TorrentialExposure(Own(o), t));
                    NoteLesser(o, t);
                }
                ourMonsters = now;
                // A card set or placed in our spell/trap zone: single removal and Icarus Attack see the board as it is now.
                int spellsNow = host.CountIn(0, SearchPosition.LocSZone);
                if (ourSpells >= 0 && spellsNow > ourSpells)
                    NoteLesser(host.Summary(0), host.Summary(1));
                ourSpells = spellsNow;
            }

            void Undo()
            {
                foreach (var step in path)
                    lock (step.Node.Lock)
                    {
                        step.Node.Visits--;
                        step.Node.Total -= step.VirtualLoss;
                    }
            }

            var cursor = FromTurnStart is { } fromStart ? new TurnReplay.Cursor(fromStart)
            {
                IsRoot = (p, h) => IsRootPrompt(p, h, RootKey, rng),
            } : null;
            (int Attack, int Links, HashSet<string> Options)? attackResponse = null;
            Prompt? prompt;
            try
            {
                while ((prompt = host.NextPrompt(StopTurn, ct)) != null)
                {
                    prompts++;
                    if (trace != null && prompt.Player == 0)
                    {
                        var strategicState = State == TagRules.BotStrategicState.Desperate
                            ? State : AssessThreats(host, host.Summary(0), host.Summary(1)).State;
                        trace.Add($"  strategic state: {TagRules.DescribeStrategicState(strategicState, lastTracedStrategicState)} at {prompt.State}");
                        lastTracedStrategicState = strategicState;
                    }
                    progress.Record(host.Turn, host.Phase, prompt, prompts);
                    // Chance (a coin, a die, a random selection) during our turn, while the line is still in the tree: what came
                    // up couldn't be known, so the decision after it is re-planned (marked on the node it happened under).
                    if (host.RandomEvents != randomSeen)
                    {
                        randomSeen = host.RandomEvents;
                        if (rootReached && inTree && host.Turn == SearchTurn)
                            MarkOpen(node, 2);
                    }
                    // A line that never ends (an engine prompt loop) mustn't hang the search: abandoned as failed.
                    if (prompts > MaxPromptsPerLine)
                    {
                        Undo();
                        return "line stopped: over 3000 prompts (a loop)";
                    }
                    if (host.Turn != lastTurn && lastTurn == SearchTurn && rootReached && turnEnd == null)
                    {
                        turnEnd = (host.Summary(0), host.Summary(1), attacked); // our turn just ended: the board their backrow would have hit
                        activatedInTurn = ActivatedInOurTurn();
                        lylaEffectUsed = LylaEffectUsedInOurTurn();
                        heavyExposure = _evaluator.HeavyExposure(turnEnd.Value.Ours, turnEnd.Value.Theirs);
                        NoteLesser(turnEnd.Value.Ours, turnEnd.Value.Theirs);
                        penalty += tally.OnOurTurnEnd(turnEnd.Value.Ours, turnEnd.Value.Theirs, note);
                    }
                    if (host.Turn != lastTurn)
                    {
                        lastTurn = host.Turn;
                        line.IdleActions[0] = line.IdleActions[1] = 0;
                    }
                    if (prompt.Attempts > 0)
                    {
                        // The engine rejected our answer: fall back to its safe answers in turn.
                        int fallback = prompt.Attempts - 1;
                        if (fallback >= prompt.Fallbacks.Count)
                        {
                            Undo();
                            return rootReached ? $"no valid answer to {prompt.State}" : $"position: no valid answer to {prompt.State} before our decision";
                        }
                        host.Respond(prompt, prompt.Fallbacks[fallback]);
                        continue;
                    }

                    // From the turn's start: the real turn replayed (both sides) until the decision; a line that leaves the real game is dropped.
                    if (cursor != null && !rootReached)
                    {
                        var (replayed, reachedRoot, miss) = cursor.Step(prompt, host, rng);
                        if (miss != null)
                        {
                            Undo();
                            return ReplayMiss($"from the turn's start, checkpoint {cursor.Next + 1}: {miss}");
                        }
                        if (!reachedRoot)
                        {
                            Answer(host, prompt, replayed!);
                            continue;
                        }
                        rootReached = true;
                        if (prompt.Data is SelectChainData attackChain && host.Attacking && !host.InDamageStep &&
                            FromTurnStart is { } replayAtAttack && replayAtAttack.Checkpoints[^1].View.AttackInProgress != null)
                            attackResponse = (host.AttackDeclarations, host.ChainLinksStarted,
                                SearchActions.ChainOptionKeys(attackChain).ToHashSet());
                        ourMonsters = host.CountIn(0, SearchPosition.LocMZone);
                        ourSpells = host.CountIn(0, SearchPosition.LocSZone);
                        drawsAtRoot = host.OurCardsDrawn;
                        host.HideUnseenDeckReveals();
                        ourCardsAtRoot = OurCards();
                        tally.AtRoot(host.Summary(0));
                        (setWindowChosen, setPlanChosen) = (StartInSetWindow, StartSetPlanDone);
                        CaptureLineStart(host);
                        long at = Stopwatch.GetTimestamp();
                        SearchTimings.Add(ref SearchTimings.ToRoot, at - phaseStart);
                        phaseStart = at;
                    }

                    // A repeated offer in the same chain window keeps the root's pass.
                    if (rootKey == "pass" && attackResponse is { } passed &&
                        host.Attacking && !host.InDamageStep && host.AttackDeclarations == passed.Attack &&
                        host.ChainLinksStarted == passed.Links && prompt.Player == 0 &&
                        prompt.Data is SelectChainData { Forced: false } repeated &&
                        passed.Options.IsSupersetOf(SearchActions.ChainOptionKeys(repeated)))
                    {
                        Answer(host, prompt, BitConverter.GetBytes(-1));
                        continue;
                    }

                    bool ours = prompt.Player == 0 && (host.Turn == SearchTurn || host.Turn == RootTurn);
                    if (ours && rootReached)
                        NoteSummons();
                    if (ours && !rootReached)
                    {
                        if (autoBattle && prompt.State == BotState.SelectIdleCmd)
                        {
                            // The real decision is in the Battle Phase: the rebuilt turn goes straight there.
                            autoBattle = false;
                            if (((IdleCmdData)prompt.Data).CanBattlePhase)
                            {
                                Answer(host, prompt, SearchActions.Packet((int)MainAction.ToBattlePhase, 0));
                                continue;
                            }
                        }
                        if (Replay is { } replay)
                        {
                            // Mid-chain: from the last searched decision, the real game's answers in order, then the chain window.
                            if (!anchorReached && prompt.State == replay.AnchorState)
                            {
                                anchorReached = true;
                                CheckPosition(host);
                            }
                            if (anchorReached)
                            {
                                if (replayNext < replay.Ours.Count)
                                {
                                    var step = replay.Ours[replayNext];
                                    if (SameKind(prompt.State, step.State) && ReplayKeys.Packet(prompt.Data, step) is { } replayed)
                                    {
                                        replayNext++;
                                        Answer(host, prompt, replayed);
                                        continue;
                                    }
                                    // A pick Omega asked one card at a time (select / unselect) and the engine all at once (a sum: Machina Fortress's
                                    // discard; seen 2026-11-18 live, "pick Machina Fortress, SelectUnselectCard, not on offer at SelectSum"): the run
                                    // of one-card answers as the sum's cards.
                                    if (prompt.Data is SelectSumData sum && step.State == BotState.SelectUnselectCard &&
                                        ReplayKeys.SumPacket(sum, replay.Ours.Skip(replayNext).ToList(), out int sumSteps) is { } summed)
                                    {
                                        replayNext += sumSteps;
                                        Answer(host, prompt, summed);
                                        continue;
                                    }
                                    // A prompt the real game never showed us (an optional one): declined, and the replay goes on.
                                    if (!SameKind(prompt.State, step.State) && Passive(prompt) is { } declined && extraPrompts++ < 8)
                                    {
                                        Answer(host, prompt, declined);
                                        continue;
                                    }
                                    Undo();
                                    return ReplayMiss($"step {replayNext + 1} ({step.Label}, {step.State}) not on offer at {prompt.State}");
                                }
                                if (!IsRootPrompt(prompt, host, replay.TargetKey, rng))
                                {
                                    if (Passive(prompt) is { } skip && extraPrompts++ < 8)
                                    {
                                        Answer(host, prompt, skip);
                                        continue;
                                    }
                                    Undo();
                                    return ReplayMiss($"reached {prompt.State} [{DecisionKey.Of(prompt.State, host.Summary(0), host.Summary(1), ReplayOptionKeys(prompt, host, rng))}], " +
                                                      $"not the window [{replay.TargetKey}]");
                                }
                                rootReached = true;
                                ourMonsters = host.CountIn(0, SearchPosition.LocMZone);
                                ourSpells = host.CountIn(0, SearchPosition.LocSZone);
                                drawsAtRoot = host.OurCardsDrawn;
                                host.HideUnseenDeckReveals();
                                ourCardsAtRoot = OurCards();
                                tally.AtRoot(host.Summary(0));
                                (setWindowChosen, setPlanChosen) = (StartInSetWindow, StartSetPlanDone);
                                CaptureLineStart(host);
                                long reachedAt = Stopwatch.GetTimestamp();
                                SearchTimings.Add(ref SearchTimings.ToRoot, reachedAt - phaseStart);
                                phaseStart = reachedAt;
                            }
                        }
                        else if (prompt.State == _rootState)
                        {
                            rootReached = true;
                            ourMonsters = host.CountIn(0, SearchPosition.LocMZone);
                            ourSpells = host.CountIn(0, SearchPosition.LocSZone);
                            drawsAtRoot = host.OurCardsDrawn;
                            host.HideUnseenDeckReveals();
                            ourCardsAtRoot = OurCards();
                            tally.AtRoot(host.Summary(0));
                            (setWindowChosen, setPlanChosen) = (StartInSetWindow, StartSetPlanDone);
                            CaptureLineStart(host);
                            CheckPosition(host);
                            long now = Stopwatch.GetTimestamp();
                            SearchTimings.Add(ref SearchTimings.ToRoot, now - phaseStart);
                            phaseStart = now;
                        }
                    }
                    // ...and their pick for it takes what it really targeted.
                    if (!rootReached && Replay is { } targetReplay && prompt.Player == 1 && pendingTargets >= 0 &&
                        ReplayKeys.TheirTargets(prompt.Data, targetReplay.Theirs[pendingTargets]) is { } targeted)
                    {
                        pendingTargets = -1;
                        Answer(host, prompt, targeted);
                        continue;
                    }
                    // ...and their other picks among our cards take what really left (a discard from our hand, a card of ours removed).
                    if (!rootReached && Replay is { } outcomeReplay && prompt.Player == 1 && ReplayKeys.TheirPickFromOutcome(prompt.Data, outcomeReplay) is { } outcomePick)
                    {
                        Answer(host, prompt, outcomePick);
                        continue;
                    }
                    // Mid-chain replay: their cards we saw come into play are played where the real game played them.
                    if (!rootReached && Replay is { } theirReplay && prompt.Player == 1 && prompt.Data is SelectChainData theirChain)
                    {
                        byte[]? revealed = null;
                        for (int r = 0; r < theirReplay.Theirs.Count && revealed == null; r++)
                            if ((revealsUsed & (1L << r)) == 0 && theirReplay.Theirs[r].AfterOurAnswers <= replayNext &&
                                ReplayKeys.Reveal(theirChain, theirReplay.Theirs[r]) is { } play)
                            {
                                revealsUsed |= 1L << r;
                                revealed = play;
                                pendingTargets = r;
                            }
                        if (revealed != null)
                        {
                            Answer(host, prompt, revealed);
                            continue;
                        }
                    }
                    // The line's set plan: its next set, without branching (the order is random, so it never matters).
                    if (pendingSets.Count > 0 && ours && rootReached && prompt.Data is IdleCmdData && host.Turn == SearchTurn)
                    {
                        var setsOffered = OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, brain: brain);
                        Option? nextSet = null;
                        while (nextSet == null && pendingSets.Count > 0)
                        {
                            string setKey = pendingSets[0];
                            pendingSets.RemoveAt(0);
                            nextSet = setsOffered?.FirstOrDefault(o => o.Key == setKey); // a card no longer settable is skipped
                        }
                        if (nextSet != null)
                        {
                            trace?.Add($"  set plan: {nextSet.Label}");
                            NoteOurMainMove(prompt, nextSet.Packet, setsOffered);
                            Answer(host, prompt, nextSet.Packet);
                            continue;
                        }
                    }
                    // The routine the brain's move started: its next main / battle step while it's on offer (out of the tree: the
                    // routine is the line), else the routine is over and the brain plays on.
                    if (followRoutine && ours && Routine is { } routine && routineNext < routine.Steps.Count &&
                        prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd && routine.Steps[routineNext].State == prompt.State)
                    {
                        var offeredNow = OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, brain: brain, setPlanDone: PlanDone());
                        if (offeredNow != null && routine.Match(routine.Steps[routineNext], offeredNow.Select(o => o.Key).ToList()) is string routineKey)
                        {
                            routineNext++;
                            inTree = false;
                            var packet = offeredNow.First(o => o.Key == routineKey).Packet;
                            if (routineKey.StartsWith("attack:") && prompt.Data is BattleCmdData battle)
                            {
                                attacked = true;
                                NoteMirror();
                                MctsPolicy.NoteAttacker(line, battle, SearchActions.AttackerFor(battle, BitConverter.ToUInt16(packet, 2)));
                            }
                            trace?.Add($"  routine step: {routineKey}");
                            NoteOurMainMove(prompt, packet, offeredNow);
                            Answer(host, prompt, packet);
                            continue;
                        }
                        routineNext = routine.Steps.Count; // a step not on offer: the routine breaks off here
                    }
                    if (rootReached && inTree && OptionsFor(prompt, ours, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, allowSetsBeforePlays: node == _start, brain: brain, setPlanDone: PlanDone())
                            is { Count: > 0 } rawOptions &&
                        (node == _start && LiveRootKeys != null ? AsLiveKeys(rawOptions, LiveRootKeys) : rawOptions) is var options &&
                        (node != _start || NoteOffered(node, prompt, LiveRootKeys)) &&
                        (options.Count > 1 || ours && (prompt.Data is IdleCmdData ||
                         prompt.Data is SelectChainData && (node == _start || FromTurnStart != null) ||
                         // A yes/no a rule answers (one answer in the lines) is still the root when it's the live decision: its lines count.
                         node == _start && YesNoLegalKeys(prompt) != null)))
                    {
                        // One kind of prompt per node (seen 2026-11-18: a Main Phase search continued from a node that also held the "yes" / "no"
                        // of Armageddon Knight's trigger, and listed "no" among its moves): a line meeting another kind of prompt (or the other
                        // player's) at a node that already has answers goes on in a sub-node for that kind ("@" key, never a move).
                        MctsNode? kindNode = null;
                        lock (node.Lock)
                            if (node != _start && node.Children.Count > 0 && (node.ChildState != prompt.State || node.ChildPlayer != prompt.Player))
                            {
                                string kindKey = $"@{prompt.State}/{prompt.Player}";
                                if (!node.Children.TryGetValue(kindKey, out kindNode))
                                    node.Children[kindKey] = kindNode = new MctsNode { Label = $"({prompt.State})", Available = 1 };
                            }
                        if (kindNode != null)
                        {
                            lock (kindNode.Lock)
                                kindNode.Visits++;
                            path.Add(new PathStep(kindNode, 0));
                            node = kindNode;
                        }
                        if (node == _start && ours)
                            NoteRootMissing(prompt, options, brain);
                        // Our main / battle decisions are filed by board and answers, so a later decision finds them again.
                        if (ours && prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd)
                        {
                            // Match the live idle prompt's legal actions even when search policy leaves
                            // only one move, for example ending the turn after the last set.
                            var legalKeys = prompt.Data is IdleCmdData idle
                                ? SearchActions.IdleOptionKeys(idle)
                                : options.Select(o => o.Key);
                            string decision = DecisionKey.Of(prompt.State, host.Summary(0), host.Summary(1), legalKeys);
                            // A transposition (user, 2026-11-15: "the same moves in a different order"): this board and these answers were
                            // reached by another order of moves this turn. The line carries on in that node, so both orders share what
                            // follows (one position, one score below the meeting point). Each line's own costs (order penalties, broken
                            // holds) stay with the moves it made above it.

                            // The same legal prompt can have different search choices after entering the set window.
                            string strictPosition = decision + "#" + host.Fingerprint() + "#" + (setWindowChosen ? "set" : "") + (PlanDone() ? "plan" : "") + (positionWindowPhase == host.Turn * 0x10000 + host.Phase ? "pos" : "");
                            // Only a node at the same depth (the same number of steps into the line): the same board can't be reached
                            // with a different number of moves, and a merge with one further down would make a loop in the tree.
                            if (_tree.FindPosition(strictPosition) is { } same && same != node && same != _start && node != _start && path.Count > 0 &&
                                path[^1].Node == node && lastKey != null && same.Depth == path.Count && same.Origin == _start && !path.Any(s => s.Node == same))
                            {
                                var parent = path.Count >= 2 ? path[^2].Node : _start;
                                bool linked = false;
                                lock (parent.Lock)
                                    if (parent.Children.TryGetValue(lastKey, out var current) && current == node)
                                    {
                                        parent.Children[lastKey] = same;
                                        linked = true;
                                    }
                                if (linked)
                                {
                                    var first = node.LockOrder < same.LockOrder ? node : same;
                                    var second = first == node ? same : node;
                                    lock (first.Lock)
                                        lock (second.Lock)
                                        {
                                            same.Visits += node.Visits;
                                            same.Total += node.Total;
                                            same.SumSq += node.SumSq;
                                            // Moved, not copied: a node with more than one parent (merged into before) was merged again from
                                            // the next parent, and its lines counted twice each time (seen 2026-11-16: a node's score of 10^20).
                                            node.Visits = 0;
                                            node.Total = 0;
                                            node.SumSq = 0;
                                            foreach (var kv in node.Children)
                                                same.Children.TryAdd(kv.Key, kv.Value);
                                        }
                                    path[^1] = new PathStep(same, path[^1].VirtualLoss);
                                    node = same;
                                    Interlocked.Increment(ref Transpositions);
                                }
                            }
                            else
                            {
                                if (node.Depth < 0)
                                    node.Depth = path.Count;
                                node.Origin ??= _start;
                                _tree.Register(decision, node);
                                _tree.RegisterPosition(strictPosition, node);
                            }
                        }
                        // From the turn's start, our other decisions (chain windows, picks, yes/no, positions) are filed too: the next real
                        // prompt of the turn continues the search from its node (user, 2026-11-18: reuse the replayed turn across decisions).
                        else if (ours && FromTurnStart != null)
                        {
                            node.Origin ??= _start;
                            var legalKeys = prompt.Data is SelectChainData chain
                                ? SearchActions.ChainOptionKeys(chain)
                                : YesNoLegalKeys(prompt) ?? options.Select(o => o.Key);
                            _tree.Register(DecisionKey.Of(prompt.State, host.Summary(0), host.Summary(1), legalKeys), node);
                        }
                        // The spell/trap sets on offer are one move here, the set window (SearchActions.SetWindowKey); its set plans below.
                        var spellSets = ours && prompt.Data is IdleCmdData && host.Turn == SearchTurn
                            ? options.Where(o => o.Key.StartsWith(SearchActions.SetSpellPrefix, StringComparison.Ordinal)).ToList()
                            : new List<Option>();
                        var treeOptions = options;
                        if (spellSets.Count > 0)
                        {
                            string? always = spellSets.Select(o => o.MandatoryReason).FirstOrDefault(r => r != null);
                            treeOptions = options.Where(o => !o.Key.StartsWith(SearchActions.SetSpellPrefix, StringComparison.Ordinal))
                                .Append(new Option(SearchActions.SetWindowKey, "go to the set window", spellSets[0].Packet, always)).ToList();
                        }
                        // Our brain's own answer here, once per node: the progressive bias toward the brain's play.
                        if (prompt.Player == 0 && brain != null && !node.PriorAsked)
                        {
                            var packet = brain.Decide(prompt);
                            string? prior = packet == null ? null : MatchKey(options, packet, prompt.State);
                            if (prior != null && spellSets.Count > 0 && prior.StartsWith(SearchActions.SetSpellPrefix, StringComparison.Ordinal))
                                prior = SearchActions.SetWindowKey; // the brain's set: its way into the set window
                            // ...and the moves it held back here: the brain's rules as penalties on the lines that break them.
                            var holds = brain.HoldsFor(options.Select(o => o.Key));
                            Interlocked.Add(ref HoldsMatched, holds.Count);
                            lock (node.Lock)
                                if (!node.PriorAsked)
                                {
                                    (node.PriorKey, node.PriorAsked) = (prior, true);
                                    node.Holds ??= holds;
                                }
                        }
                        var idleNode = node;
                        var (child, key, isNew, loss) = Select(node, treeOptions, rng, prompt.Player, prompt.State);
                        lastKey = key;
                        path.Add(new PathStep(child, loss));
                        depth++;
                        if (node == _start)
                        {
                            rootKey = key;
                            followRoutine = Routine is { } r && r.RootKey == key;
                        }
                        if (ours)
                        {
                            (double Cost, string Rule)? held = null;
                            lock (node.Lock)
                                if (node.Holds != null && node.Holds.TryGetValue(key, out var h))
                                    held = h;
                            if (held is { } broken)
                            {
                                penalty += broken.Cost;
                                tally.OnHoldBroken(broken.Rule, broken.Cost);
                                Cost($"Brain rule {broken.Rule}", broken.Cost);
                                Interlocked.Increment(ref HoldsCharged);
                                trace?.Add($"  (against the brain's hold {broken.Rule}: -{broken.Cost:0.#})");
                            }
                        }
                        if (ours && key == SearchActions.SetWindowKey && spellSets.Count > 0 && prompt.Data is IdleCmdData setIdle)
                        {
                            // The set window: one set plan (every spell/trap it sets) picked below it, then played in a random order.
                            setWindowChosen = true;
                            setPlanChosen = true;
                            trace?.Add("  tree move: go to the set window");
                            steps.Add(new PlanStep(prompt.State, key, child.Label));
                            var plans = SetPlans(setIdle, spellSets, Math.Max(1, 5 - host.CountIn(0, SearchPosition.LocSZone)));
                            if (plans.Count == 0) // more always-set cards than free zones: one card at a time
                                plans = spellSets.Select(o => new Option(SearchActions.SetPlanKey(new[] { o.Key }), o.Label, o.Packet)).ToList();
                            Dictionary<string, (double Cost, string Rule)>? idleHolds;
                            lock (idleNode.Lock)
                                idleHolds = idleNode.Holds;
                            // A plan holding a card the brain held back pays for each such card; the others are tried first (Select).
                            var planHolds = new Dictionary<string, (double Cost, string Rule)>();
                            foreach (var plan in plans)
                            {
                                var heldCards = SearchActions.SetPlanCards(plan.Key).Where(k => idleHolds?.ContainsKey(k) == true).Select(k => idleHolds![k]).ToList();
                                if (heldCards.Count > 0)
                                    planHolds[plan.Key] = (heldCards.Sum(h => h.Cost), string.Join(", ", heldCards.Select(h => h.Rule).Distinct()));
                            }
                            lock (child.Lock)
                                child.Holds ??= planHolds;
                            var (planChild, planKey, planNew, planLoss) = Select(child, plans, rng, 0, prompt.State);
                            lastKey = planKey;
                            path.Add(new PathStep(planChild, planLoss));
                            depth++;
                            var planCards = SearchActions.SetPlanCards(planKey);
                            foreach (var cardKey in planCards)
                                if (idleHolds != null && idleHolds.TryGetValue(cardKey, out var cardHold))
                                {
                                    penalty += cardHold.Cost;
                                    Cost($"Brain rule {cardHold.Rule}", cardHold.Cost);
                                    Interlocked.Increment(ref HoldsCharged);
                                    trace?.Add($"  (against the brain's hold {cardHold.Rule}: -{cardHold.Cost:0.#})");
                                }
                            steps.Add(new PlanStep(prompt.State, planKey, planChild.Label));
                            trace?.Add($"  tree move: {planChild.Label}");
                            pendingSets.Clear();
                            pendingSets.AddRange(planCards.OrderBy(_ => rng.Next()));
                            var firstSet = spellSets.First(o => o.Key == pendingSets[0]);
                            pendingSets.RemoveAt(0);
                            trace?.Add($"  set plan: {firstSet.Label}");
                            node = planChild;
                            inTree = !isNew && !planNew;
                            NoteOurMainMove(prompt, firstSet.Packet, options);
                            var newLeaf = isNew ? child : planNew ? planChild : null;
                            if (newLeaf != null && rootReached && host.Turn == SearchTurn && newLeaf.RolloutTail == null)
                                (tailNode, tail) = (newLeaf, new List<string>());
                            Answer(host, prompt, firstSet.Packet);
                            continue;
                        }
                        if (ours && key.StartsWith("attack:"))
                            { attacked = true; NoteMirror(); }
                        var selectedOption = treeOptions.First(o => o.Key == key);
                        if (trace != null && prompt.Player == 0 && prompt.Data is IdleCmdData)
                            trace.Add($"  tree move: {selectedOption.Label}");
                        if (ours && selectedOption.Key.StartsWith("3:", StringComparison.Ordinal))
                            setWindowChosen = true;
                        if (ours && selectedOption.Key.StartsWith("4:", StringComparison.Ordinal))
                            setWindowChosen = true;
                        if (ours && OpensPositionWindow(prompt, selectedOption.Packet, host))
                            positionWindowPhase = host.Turn * 0x10000 + host.Phase;
                        if (key.StartsWith("attack:") && prompt.Data is BattleCmdData attacking)
                        {
                            MctsPolicy.NoteAttacker(line, attacking, SearchActions.AttackerFor(attacking,
                                BitConverter.ToUInt16(selectedOption.Packet, 2)));
                        }
                        // The deck's strong suggestions, for our own choices in our turn.
                        if (ours && Suggestions != null)
                        {
                            steps.Add(new PlanStep(prompt.State, key, child.Label));
                            try
                            {
                                double suggested = Suggestions(steps, steps.Count - 1, options.Select(o => o.Key).ToList(), host.Summary(1));
                                penalty += suggested;
                                Cost("Deck's suggestions", suggested);
                            }
                            catch { /* a suggestion that fails costs nothing */ }
                        }
                        node = child;
                        inTree = !isNew;
                        NoteOurMainMove(prompt, selectedOption.Packet, options);
                        // A new leaf in our turn: record our moves after it (the rollout) until our turn ends.
                        if (isNew && rootReached && host.Turn == SearchTurn && child.RolloutTail == null)
                            (tailNode, tail) = (child, new List<string>());
                        Answer(host, prompt, selectedOption.Packet);
                        continue;
                    }
                    // Before our decision the rebuilt turn only replays phases the real turn has already had (Draw, Standby):
                    // nobody uses anything optional there, or the position would change before we decide (seen 2026-11-10: our
                    // set Jar of Greed and their traps fired in the replayed Standby Phase).
                    if (!rootReached && Passive(prompt) is { } pass)
                    {
                        Answer(host, prompt, pass);
                        continue;
                    }
                    if (MoveRules.ForcedSearchPick(prompt, host) is byte gardna)
                    {
                        trace?.Add("  Reinforcement of the Army searches Necro Gardna for Dark Grepher");
                        Answer(host, prompt, new byte[] { 1, gardna });
                        continue;
                    }
                    // Rollout: our side by our own brain (as the bot really would play), theirs by the quick policy.
                    if (prompt.Player == 0 && brain != null && brain.Decide(prompt) is { } brainPacket)
                    {
                        List<Option>? legal = null;
                        // A rollout brain is a proposal, not permission to bypass a hard search rule or phase window.
                        // Let the brain enter the set window when it has declined the other plays.
                        if (prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd or BotState.SelectChain or BotState.SelectPosition &&
                            OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, allowSetsBeforePlays: true, brain: brain, setPlanDone: PlanDone())
                                is { Count: > 0 } offered)
                        {
                            legal = offered;
                            (var proposed, brainPacket, var refused) = AskAgainIfRefused(brain, prompt, legal, brainPacket);
                            var choice = proposed ?? RolloutFallback(legal);
                            if (trace != null && prompt.Data is IdleCmdData or BattleCmdData)
                                trace.Add($"  {(proposed == null ? "rollout fallback" : "rollout brain")}: {choice.Label}" +
                                          (refused.Count > 0 ? $" (refused first: {string.Join(", ", refused)})" : "") +
                                          (proposed == null ? $" (the brain's answer {Convert.ToHexString(brainPacket ?? Array.Empty<byte>())} isn't among the search's moves: " +
                                                              $"{string.Join(", ", legal.Select(o => $"{o.Label} {Convert.ToHexString(o.Packet)}"))})" : ""));
                            brainPacket = choice.Packet;
                        }
                        if (prompt.Data is IdleCmdData && brainPacket.Length >= 2 &&
                            (BitConverter.ToUInt16(brainPacket, 0) is (ushort)MainAction.SetMonster or (ushort)MainAction.SetSpell))
                            setWindowChosen = true;
                        if (OpensPositionWindow(prompt, brainPacket, host))
                            positionWindowPhase = host.Turn * 0x10000 + host.Phase;
                        if (prompt.Data is BattleCmdData && brainPacket.Length >= 1 && brainPacket[0] == (byte)BattleAction.Attack && host.Turn == SearchTurn)
                            { attacked = true; NoteMirror(); }
                        if (prompt.Data is BattleCmdData battling && brainPacket.Length >= 4 && BitConverter.ToUInt16(brainPacket, 0) == (ushort)BattleAction.Attack)
                            MctsPolicy.NoteAttacker(line, battling, SearchActions.AttackerFor(battling, BitConverter.ToUInt16(brainPacket, 2)));
                        NoteOurMainMove(prompt, brainPacket, legal);
                        Answer(host, prompt, brainPacket);
                        continue;
                    }
                    // A pick of theirs in our turn the tree didn't branch on (over 80 cards to pick from): open if it had more than
                    // one genuinely different answer.
                    if (prompt.Player == 1 && rootReached && inTree && host.Turn == SearchTurn && TheirPickCards(prompt) is { } pickFrom)
                        MarkOpen(node, pickFrom);
                    var policyPacket = MctsPolicy.Answer(prompt, rng, line, host);
                    List<Option>? policyLegalOptions = null;
                    if (prompt.Player == 0 && prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd or BotState.SelectChain or BotState.SelectPosition &&
                        OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, brain: brain, setPlanDone: PlanDone()) is { Count: > 0 } policyLegal)
                    {
                        policyLegalOptions = policyLegal;
                        var proposed = MatchOption(policyLegal, policyPacket, prompt.State);
                        var choice = proposed ?? RolloutFallback(policyLegal);
                        if (trace != null && prompt.Data is IdleCmdData)
                            trace.Add($"  {(proposed == null ? "quick-policy fallback" : "quick policy")}: {choice.Label}");
                        policyPacket = choice.Packet;
                    }
                    if (prompt.Player == 0 && prompt.Data is IdleCmdData && policyPacket.Length >= 2 &&
                        BitConverter.ToUInt16(policyPacket, 0) is (ushort)MainAction.SetMonster or (ushort)MainAction.SetSpell)
                        setWindowChosen = true;
                    if (OpensPositionWindow(prompt, policyPacket, host))
                        positionWindowPhase = host.Turn * 0x10000 + host.Phase;
                    if (prompt.Player == 0 && host.Turn == SearchTurn && prompt.Data is BattleCmdData && policyPacket.Length >= 1 && policyPacket[0] == (byte)BattleAction.Attack)
                        { attacked = true; NoteMirror(); }
                    NoteOurMainMove(prompt, policyPacket, policyLegalOptions);
                    Answer(host, prompt, policyPacket);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                Undo();
                throw;
            }
            finally
            {
                SearchTimings.Add(ref SearchTimings.Prompts, prompts);
                SearchTimings.Add(ref (rootReached ? ref SearchTimings.Playout : ref SearchTimings.ToRoot), Stopwatch.GetTimestamp() - phaseStart);
            }
            if (!rootReached)
            {
                Undo();
                return host.Result?.Error is { } ? $"position: duel stopped before our decision ({host.Result.Reason})" : "position: our decision never came up";
            }
            if (host.Result?.Error is { } err)
            {
                Undo();
                return $"line stopped: {host.Result.Reason}";
            }
            long scoring = Stopwatch.GetTimestamp();
            int drawn = (host.Turn >= StopTurn ? host.OurCardsDrawnAtTurnStart : host.OurCardsDrawn) - drawsAtRoot;
            double drawBonus = drawn * BoardEvaluator.DrawBonus;
            if (drawn > 0)
                trace?.Add($"  drew {drawn} card(s), draw bonus {drawBonus:+0.##;-0.##;0} card units");
            if (trace != null)
            {
                var ours = host.Summary(0);
                int blankHand = ours.HandCodes.Count(code => code == SearchView.InertCard);
                int blankGrave = ours.GraveCodes.Count(code => code == SearchView.InertCard);
                if (blankHand + blankGrave > 0)
                    trace.Add($"  unscored placeholders: {blankHand} in hand, {blankGrave} in grave");
            }
            // A line that ended within our turn (a win, or a horizon of 1): its board now is the one their backrow would hit.
            if (turnEnd == null && host.Turn == SearchTurn)
            {
                turnEnd = (host.Summary(0), host.Summary(1), attacked);
                activatedInTurn = ActivatedInOurTurn();
                lylaEffectUsed = LylaEffectUsedInOurTurn();
                heavyExposure = _evaluator.HeavyExposure(turnEnd.Value.Ours, turnEnd.Value.Theirs);
                NoteLesser(turnEnd.Value.Ours, turnEnd.Value.Theirs);
            }
            // Monsters of theirs we controlled during the turn, all gone back (or gone) as it ended: borrowed, not ours to lose (C1).
            if (sawForeign && turnEnd is { } ended && ended.Ours.Foreign == 0)
                (peakTorrential, peakMirror, peakSpot, peakIcarus) = (ownTorrential, ownMirror, ownSpot, ownIcarus);
            static double Peak(double p) => double.IsNegativeInfinity(p) ? 0 : p;
            (peakTorrential, peakMirror, peakSpot, peakIcarus) = (Peak(peakTorrential), Peak(peakMirror), Peak(peakSpot), Peak(peakIcarus));
            // How much each exposure is feared in this strategic state (StateTuning; Desperate: none, user 2026-11-24: "It should however
            // remove the fear of heavy storm and traps"). The sitting duck (their next attacker) stays.
            (peakTorrential, peakMirror, heavyExposure, peakSpot, peakIcarus) = (peakTorrential * TorrentialFear, peakMirror * MirrorForceFear,
                heavyExposure * HeavyStormFear, peakSpot * SingleRemovalFear, peakIcarus * IcarusFear);
            double sittingDuck = turnEnd is { } atEnd ? _evaluator.SittingDuckExposure(atEnd.Ours, atEnd.Theirs, lylaEffectUsed) : 0;
            double exposure = peakTorrential + peakMirror + heavyExposure + peakSpot + peakIcarus + sittingDuck;
            if (Math.Abs(exposure) > 0.05)
                trace?.Add($"  (overexposure: Torrential {peakTorrential:0.00}, Mirror Force {peakMirror:0.00}, Heavy Storm {heavyExposure:0.00}, " +
                           $"single removal {peakSpot:0.00}, Icarus Attack {peakIcarus:0.00}, sitting duck {sittingDuck:0.00})");
            var start = Volatile.Read(ref _lineStart)!;
            var endOurs = host.Summary(0);
            var endTheirs = host.Summary(1);
            if (host.Turn > SearchTurn)
                penalty += tally.OnSkippedNormalSummon(turnEnd?.Theirs ?? endTheirs, note);
            // Compare both positions at the decision's reserve prices so changing state doesn't revalue untouched cards.
            var endCounts = _evaluator.CountAdvantage(endOurs, endTheirs, start.Pressuring);
            var startCounts = start.Advantage;
            double weight = BoardEvaluator.PlusOneBonus;
            int ourHand = endCounts.OurHand - startCounts.OurHand;
            int ourMonstersDelta = endCounts.OurMonsters - startCounts.OurMonsters;
            int ourSpellsDelta = endCounts.OurSpells - startCounts.OurSpells;
            int gravePairs = endCounts.GravePairs - startCounts.GravePairs;
            var normalDraws = host.OpponentNormalDraws;
            int theirHand = startCounts.TheirHand - endCounts.TheirHand + normalDraws.Count - start.OpponentNormalDraws;
            int theirMonstersDelta = startCounts.TheirMonsters - endCounts.TheirMonsters;
            int theirSpellsDelta = startCounts.TheirSpells - endCounts.TheirSpells;
            double ourHandAdjustment = endCounts.OurHandAdjustment - startCounts.OurHandAdjustment;
            double theirHandAdjustment = startCounts.TheirHandAdjustment - endCounts.TheirHandAdjustment;
            for (int i = start.OpponentNormalDraws; i < normalDraws.Count; i++)
                theirHandAdjustment += _evaluator.HandCardValue(normalDraws[i], 1) - 1;
            double ourSetTrapAdjustment = endCounts.OurSetTrapAdjustment - startCounts.OurSetTrapAdjustment;
            double theirSetTrapAdjustment = startCounts.TheirSetTrapAdjustment - endCounts.TheirSetTrapAdjustment;
            double ourGrave = endCounts.OurGraveValue - startCounts.OurGraveValue;
            double theirGrave = startCounts.TheirGraveValue - endCounts.TheirGraveValue;
            double pressuringHand = (endCounts.PressuringHandValue - startCounts.PressuringHandValue) * PressuringReserveWeight;
            double pressuringGrave = (endCounts.PressuringGraveValue - startCounts.PressuringGraveValue) * PressuringReserveWeight;
            double ourAdvantage = (ourHand + ourHandAdjustment + BoardEvaluator.MonsterCardValue * ourMonstersDelta + ourSpellsDelta + ourSetTrapAdjustment +
                gravePairs + ourGrave + pressuringHand + pressuringGrave) * weight;
            double theirAdvantage = (theirHand + theirHandAdjustment + BoardEvaluator.MonsterCardValue * theirMonstersDelta + theirSpellsDelta + theirSetTrapAdjustment +
                theirGrave) * weight;
            double advantage = ourAdvantage + theirAdvantage;
            trace?.Add($"  ({BoardEvaluator.OurAdvantageDelta}: {ourAdvantage:+0.##;-0.##;0}; {BoardEvaluator.TheirAdvantageDelta}: {theirAdvantage:+0.##;-0.##;0})");
            if (trace != null)
            {
                trace.Add($"  (hand value adjustments: ours {ourHandAdjustment * weight:+0.###;-0.###;0}; opponent {theirHandAdjustment * weight:+0.###;-0.###;0})");
                trace.Add($"  (graveyard value changes: ours {ourGrave * weight:+0.###;-0.###;0}; opponent {theirGrave * weight:+0.###;-0.###;0}; live Vayu pairs {gravePairs * weight:+0.###;-0.###;0})");
            }
            if (start.Pressuring && trace != null)
                trace.Add($"  (pressuring reserves: hand {pressuringHand * weight:+0.###;-0.###;0}; graveyard {pressuringGrave * weight:+0.###;-0.###;0})");
            // The line rules counted at the end of the line (LineTally.AtLineEnd).
            penalty += tally.AtLineEnd(host.Summary(0), host.SurvivingAttackers.Skip(previousBattles), _evaluator.RewardGladiatorBattles, trace);

            // The breakdown's line parts beside the rules: what's left of penalty unnamed, card advantage, overexposure by kind, draws.
            Dictionary<string, double>? parts = null;
            if (rootKey != null)
            {
                parts = lineParts;
                if (Math.Abs(penalty - namedPenalty) > 1e-9)
                    Cost("Other rule costs", penalty - namedPenalty);
                double risk = _evaluator.RiskDiscount;
                void Add(string name, double v)
                {
                    if (v != 0)
                        parts[name] = (parts.TryGetValue(name, out var had) ? had : 0) + v;
                }
                parts[BoardEvaluator.OurAdvantageDelta] = ourAdvantage;
                parts[BoardEvaluator.TheirAdvantageDelta] = theirAdvantage;
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Hand count", ourHand * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Hand value adjustment", ourHandAdjustment * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Non-token monsters", BoardEvaluator.MonsterCardValue * ourMonstersDelta * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "spells/traps on the field", ourSpellsDelta * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Set trap value adjustment", ourSetTrapAdjustment * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Live Vayu pairs", gravePairs * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Graveyard value", ourGrave * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Pressuring: monsters in hand", pressuringHand * weight);
                Add(BoardEvaluator.OurAdvantageDelta + BoardEvaluator.SubPart + "Pressuring: graveyard reserves", pressuringGrave * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "Hand count excluding normal draws", theirHand * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "Hand value adjustment", theirHandAdjustment * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "Non-token monsters", BoardEvaluator.MonsterCardValue * theirMonstersDelta * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "spells/traps on the field", theirSpellsDelta * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "Destroyed traps", theirSetTrapAdjustment * weight);
                Add(BoardEvaluator.TheirAdvantageDelta + BoardEvaluator.SubPart + "Graveyard value", theirGrave * weight);
                if (host.Result?.Winner != 1)
                {
                    RecordCardChanges(parts, BoardEvaluator.OurBoardDelta, BoardEvaluator.OurAdvantageDelta, start.Ours, endOurs, start.OurPiles, ReadCardPiles(host, 0));
                    RecordCardChanges(parts, BoardEvaluator.TheirBoardDelta, BoardEvaluator.TheirAdvantageDelta, start.Theirs, endTheirs, start.TheirPiles, ReadCardPiles(host, 1));
                }
                Add("Exposure to Torrential Tribute", -peakTorrential * risk);
                Add("Exposure to Mirror Force", -peakMirror * risk);
                Add("Exposure to Heavy Storm", -heavyExposure * risk);
                Add("Exposure to single removal", -peakSpot * risk);
                Add("Exposure to Icarus Attack", -peakIcarus * risk);
                Add("Sitting duck (attackable monsters)", -sittingDuck * risk);
                Add("Cards drawn", drawBonus);
            }
            double value = _evaluator.Evaluate(host, penalty - advantage + exposure * _evaluator.RiskDiscount - drawBonus, turnEnd,
                winRisk: Math.Max(0, exposure), cardsSpent: ourCardsAtRoot >= 0 ? Math.Max(0, ourCardsAtRoot - OurCards()) : 0, length: prompts,
                center: start.Center, noClock: main2Codes, activatedThisTurn: activatedInTurn,
                parts: parts, centerParts: start.Parts);
            if (rootKey != null)
            {
                double ourBoard = _evaluator.BoardScore(endOurs, 0, endTheirs);
                double theirBoard = _evaluator.BoardScore(endTheirs, 1, endOurs);
                int winner = host.Result?.Winner ?? -1;
                lock (_details)
                {
                    _details.TryGetValue(rootKey, out var d);
                    _details[rootKey] = (d.Lines + 1, d.Wins + (winner == 0 ? 1 : 0), d.Losses + (winner == 1 ? 1 : 0),
                        d.OurLp + Math.Max(0, host.LifePoints(0)), d.TheirLp + Math.Max(0, host.LifePoints(1)), d.OurBoard + ourBoard, d.TheirBoard + theirBoard,
                        d.Penalty + penalty);
                    // A lost line scores 0 outright, not by parts: its share shows as the loss rate.
                    if (parts != null && winner != 1)
                    {
                        if (!_parts.TryGetValue(rootKey, out var sums))
                            _parts[rootKey] = sums = new Dictionary<string, double>();
                        sums[PartLines] = (sums.TryGetValue(PartLines, out var lines) ? lines : 0) + 1;
                        foreach (var (name, v) in parts)
                            sums[name] = (sums.TryGetValue(name, out var had) ? had : 0) + v;
                    }
                }
            }
            if (tailNode != null && tail != null)
                lock (tailNode.Lock)
                    tailNode.RolloutTail ??= tail;
            if (trace != null && rootKey == _settings.TraceRoot && _traces.Count < _settings.TraceLines)
                _traces.Enqueue(string.Join("\n", trace) + $"\n  => position score {BoardEvaluator.Cu(value)} card units" + (penalty > 0 ? $" (suggestions cost {penalty:0.0})" : ""));
            SearchTimings.Add(ref SearchTimings.Evaluate, Stopwatch.GetTimestamp() - scoring);
            lock (_start.Lock)
            {
                _start.Visits++;
                _start.Total += value;
                _start.SumSq += value * value;
            }
            foreach (var step in path)
                lock (step.Node.Lock)
                {
                    step.Node.Total += value - step.VirtualLoss; // its visit was counted on the way down
                    step.Node.SumSq += value * value;
                }
            if (depth > _maxDepth)
                _maxDepth = depth;
            return null;
        }

        /// <summary>
        /// UCB pick at a decision of <paramref name="player"/> (we maximise the score, they minimise it). An answer not tried
        /// yet comes first (our brain's own first, then kept until it has LineValueVisits lines); card picks with many answers open new ones only as visits grow
        /// (progressive widening). The child entered carries a virtual loss until the line is scored.
        /// </summary>
        private (MctsNode Child, string Key, bool IsNew, double Loss) Select(MctsNode node, List<Option> options, Random rng, int player, BotState state)
        {
            // A visit that would look bad to whoever picks here, so the other threads spread out meanwhile.
            double loss = player == 0 ? 0 : 1;
            // At the root our moves are chosen by their score, not their lines average, and settled ones are set aside (RootScores).
            var (rootScores, settledRoots) = node == _start && player == 0 && state is BotState.SelectIdleCmd or BotState.SelectBattleCmd
                ? RootScores(options) : (null, null);
            MctsNode chosen;
            string chosenKey;
            lock (node.Lock)
            {
                node.ChildState = state;
                node.ChildPlayer = player;
                if (player != 0 && node.DistinctPicks < 0 && state is BotState.SelectCard or BotState.SelectTribute or BotState.SelectUnselectCard)
                    node.DistinctPicks = DistinctTargets(options.Select(o => o.Key));
                foreach (var option in options)
                    if (node.Children.TryGetValue(option.Key, out var existing))
                        existing.Available++;

                bool picks = state is BotState.SelectCard or BotState.SelectTribute or BotState.SelectUnselectCard;
                bool ourMain = player == 0 && state is BotState.SelectIdleCmd or BotState.SelectBattleCmd;
                int open = options.Count(o => node.Children.ContainsKey(o.Key));
                int allowed = picks ? 1 + (int)(_settings.Widening * Math.Sqrt(node.Visits))
                    : ourMain ? 1 + (int)(_settings.MainWidening * Math.Sqrt(node.Visits))
                    : int.MaxValue;
                var unexpanded = options.Where(o => !node.Children.ContainsKey(o.Key)).ToList();
                if (unexpanded.Count > 0 && (open < allowed || open == 0))
                {
                    // Our brain's own answer first; then the moves it didn't hold back; the held ones (rule-breaking) last.
                    Option pick;
                    if (player == 0 && node.PriorKey != null && unexpanded.FirstOrDefault(o => o.Key == node.PriorKey) is { } prior)
                        pick = prior;
                    else
                    {
                        var free = node.Holds == null ? unexpanded : unexpanded.Where(o => !node.Holds.ContainsKey(o.Key)).ToList();
                        var pool = free.Count > 0 ? free : unexpanded;
                        pick = pool[rng.Next(pool.Count)];
                    }
                    var fresh = new MctsNode { Label = pick.Label, Available = 1, Visits = 1, Total = loss, ParentVisitsAtCreation = node.Visits };
                    (node.ChildLabels ??= new Dictionary<string, string>())[pick.Key] = pick.Label;
                    node.Children[pick.Key] = fresh;
                    return (fresh, pick.Key, true, loss);
                }

                Option? best = null;
                double bestScore = double.NegativeInfinity;
                if (node == _start && _settings.FocusRoots is { Count: > 0 } focusKeys)
                {
                    MctsNode? focused = null;
                    Option? focusOption = null;
                    foreach (string key in focusKeys)
                        if (node.Children.TryGetValue(key, out var child) && child.Visits < _settings.FocusLines &&
                            (focused == null || child.Visits < focused.Visits) && options.FirstOrDefault(o => o.Key == key) is { } option)
                            (focused, focusOption) = (child, option);
                    best = focusOption;
                }
                // Our brain's own answer gets its LineValueVisits first, so a move with many follow-up choices (Armageddon Knight's
                // send) is read through a good follow-up instead of the average of every pick tried (user, 2026-11-24).
                if (best == null && player == 0 && node.PriorKey != null &&
                    node.Children.TryGetValue(node.PriorKey, out var priorChild) && priorChild.Visits < LineValueVisits &&
                    options.FirstOrDefault(o => o.Key == node.PriorKey) is { } priorOption)
                    best = priorOption;
                if (best == null)
                {
                    foreach (var option in options)
                    {
                        if (!node.Children.TryGetValue(option.Key, out var child) || settledRoots?.Contains(option.Key) == true)
                            continue;
                        double mean = player == 0 ? rootScores != null && rootScores.TryGetValue(option.Key, out double score0) ? score0 : child.Mean : 1 - child.Mean;
                        double score = mean + _settings.Exploration * Math.Sqrt(Math.Log(Math.Max(1, child.Available)) / Math.Max(1, child.Visits));
                        if (player == 0 && option.Key == node.PriorKey)
                            score += _settings.PriorBonus / Math.Sqrt(1 + child.Visits);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = option;
                        }
                    }
                }
                chosenKey = best!.Key;
                chosen = node.Children[chosenKey];
            }
            lock (chosen.Lock)
            {
                chosen.Visits++;
                chosen.Total += loss;
            }
            return (chosen, chosenKey, false, loss);
        }

        /// <summary>
        /// The root moves' scores (LineValue, as the verdict reads them), for choosing which root move the next line tries: by its lines
        /// average a move with many follow-ups looks as bad as its unexplored ones and gets starved (user, 2026-11-24: Reinforcement of
        /// the Army, lines average -1.14 against set Icarus Attack's +0.12, score -0.15 against +0.12). Each is worked out again once its
        /// lines have grown by a tenth (at least ScoreRefreshLines), outside every lock (the walk locks each node it visits). And the
        /// settled moves (RootSettled), set aside while any other move on offer isn't settled; null when none is set aside.
        /// </summary>
        private (Dictionary<string, double>? Scores, HashSet<string>? Settled) RootScores(List<Option> options)
        {
            var children = new List<(string Key, MctsNode Node)>();
            lock (_start.Lock)
                foreach (var option in options)
                    if (_start.Children.TryGetValue(option.Key, out var child))
                        children.Add((option.Key, child));
            var scores = new Dictionary<string, double>();
            HashSet<string>? settled = null;
            int unsettled = 0;
            foreach (var (key, child) in children)
            {
                int visits, cachedAt;
                lock (child.Lock)
                    (visits, cachedAt) = (child.Visits, child.ScoreCachedAt);
                if (cachedAt < 0 || visits >= cachedAt + Math.Max(ScoreRefreshLines, cachedAt / 10))
                {
                    double value = child.LineValue(LineValueVisits);
                    lock (child.Lock)
                        (child.CachedScore, child.ScoreCachedAt) = (value, visits);
                }
                double score, mean, err;
                lock (child.Lock)
                    (score, mean, err, visits) = (child.CachedScore, child.Mean, child.StdErr, child.Visits);
                scores[key] = score;
                if (RootSettled?.Invoke(visits, score, mean, err) == true)
                    (settled ??= new HashSet<string>()).Add(key);
                else
                    unsettled++;
            }
            return (scores, unsettled > 0 ? settled : null);
        }

        /// <summary>Lines a root move's score waits for before it's worked out again (or a tenth of its lines, whichever is more).</summary>
        private const int ScoreRefreshLines = 8;

        // If the proposed move was blocked, a legal phase exit avoids spending cards on a move the brain never chose.
        /// <summary>How many refused answers the rollout takes out and asks our brain again about before it falls back.</summary>
        private const int MaxBrainRetries = 8;

        /// <summary>
        /// Our brain's answer among the search's moves. A refused answer is taken out of the brain's options and it's asked again (user,
        /// 2026-11-24: "refused moves shouldn't go straight into fallback, they should just remove that option and try again"); seen: the
        /// brain's "Substitoad to defence", refused by the position-change timing rule, fell back to "end the turn" and Caius never attacked.
        /// Returns the matched move (null: the caller falls back), the brain's last answer and the refused ones (hex, for the trace).
        /// </summary>
        private static (Option? Proposed, byte[] Packet, List<string> Refused) AskAgainIfRefused(SearchBrain brain, Prompt prompt, List<Option> legal, byte[] packet)
        {
            var proposed = MatchOption(legal, packet, prompt.State);
            var refused = new List<string>();
            object? narrowed = prompt.Data;
            while (proposed == null && refused.Count < MaxBrainRetries && SearchActions.WithoutAnswer(narrowed, packet) is { } fewer)
            {
                refused.Add(Convert.ToHexString(packet));
                narrowed = fewer;
                if (brain.Decide(prompt, narrowed) is not { } again)
                    break;
                packet = again;
                proposed = MatchOption(legal, packet, prompt.State);
            }
            return (proposed, packet, refused);
        }

        /// <summary>A face-up monster's position change in a Main Phase: it opens the position window (SearchMoveRules.Allowed). So does
        /// Sirocco's boost in Main Phase 1: the last step before the battle (user, 2026-11-25).</summary>
        private static bool OpensPositionWindow(Prompt prompt, byte[] packet, DuelHost host)
        {
            if (prompt.Player != 0 || prompt.Data is not IdleCmdData idle || host.Phase is not ((int)DuelPhase.Main1 or (int)DuelPhase.Main2) || packet.Length < 4)
                return false;
            if (BitConverter.ToUInt16(packet, 0) == (ushort)MainAction.Activate && host.Phase == (int)DuelPhase.Main1)
            {
                int activation = BitConverter.ToUInt16(packet, 2);
                return (idle.ActivableCards ?? Array.Empty<DynamicCard>()).Any(card => SearchHost.Is(card.ID, BlackwingSiroccoTheDawn) &&
                    (card.ActionActivateIndex ?? Array.Empty<ActivationCardIndex>()).Any(a => a.ActionIndex == activation));
            }
            if (BitConverter.ToUInt16(packet, 0) != (ushort)MainAction.Repos)
                return false;
            int index = BitConverter.ToUInt16(packet, 2);
            return (idle.ReposableCards ?? Array.Empty<DynamicCard>()).Any(card => card.ActionIndex != null &&
                card.ActionIndex.Length > (int)MainAction.Repos && card.ActionIndex[(int)MainAction.Repos] == index && (card.Position & 0x5) != 0);
        }

        private static Option RolloutFallback(List<Option> options) =>
            options.Find(option => option.Key == "end") ??
            options.Find(option => option.Key == "battle") ??
            options.Find(option => option.Key == "pass") ??
            options[0];

        /// <summary>The option key our brain's answer (a bot packet) means, or null.</summary>
        private static Option? MatchOption(List<Option> options, byte[] packet, BotState state)
        {
            if (state is BotState.SelectCard or BotState.SelectTribute && packet.Length >= 1)
            {
                foreach (var option in options)
                    if (SamePickPacket(option.Packet, packet))
                        return option;
                return null;
            }
            foreach (var option in options)
                if (option.Packet.AsSpan().SequenceEqual(packet))
                    return option;
            return null;
        }

        // A multi-card selection is the same answer whichever order its card indexes were written in. Picks are tiny
        // (normally one to three cards), so counting in place is cheaper than allocating and sorting two arrays per option.
        private static bool SamePickPacket(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
                return false;
            for (int i = 1; i < left.Length; i++)
            {
                byte value = left[i];
                if (left[1..i].Contains(value))
                    continue;
                int leftCount = 0, rightCount = 0;
                for (int j = 1; j < left.Length; j++)
                {
                    if (left[j] == value) leftCount++;
                    if (right[j] == value) rightCount++;
                }
                if (leftCount != rightCount)
                    return false;
            }
            return true;
        }

        private static string? MatchKey(List<Option> options, byte[] packet, BotState state) =>
            MatchOption(options, packet, state)?.Key;

        /// <summary>
        /// At a card pick: records the cards a line found selectable at the root (MctsNode.Offered), as "card:" keys under the real
        /// decision's keys where only a zone differs. The cards, not the options: the search's own rules leave some options out on purpose
        /// (DiscardPicks, EffectPicks, no cancel on our cost picks). Always true (used inside a condition).
        /// </summary>
        private static bool NoteOffered(MctsNode root, Prompt prompt, IReadOnlyCollection<string>? live)
        {
            var cards = prompt.Data switch
            {
                SelectCardData d => d.SelectableCards,
                SelectTributeData d => d.SelectableCards,
                SelectUnselectCardData d => d.SelectableCards,
                SelectSumData d => d.SelectableCards,
                _ => null,
            };
            if (cards == null || cards.Length == 0)
                return true;
            var keys = cards.Select(c => $"card:{SearchActions.CardKey(c)}").Distinct().ToList();
            if (live != null)
            {
                var map = SearchActions.ToLiveKeys(keys, SearchActions.PickCardKeys(live).ToList());
                keys = keys.Select(k => map.TryGetValue(k, out var real) ? real : k).ToList();
            }
            lock (root.Lock)
                (root.Offered ??= new HashSet<string>()).UnionWith(keys);
            return true;
        }

        /// <summary>The root's options under the real decision's keys where only a zone differs (LiveRootKeys).</summary>
        private static List<Option> AsLiveKeys(List<Option> options, IReadOnlyCollection<string> live)
        {
            var map = SearchActions.ToLiveKeys(options.Select(o => o.Key).ToList(), live);
            return map.Count == 0 ? options : options.Select(o => map.TryGetValue(o.Key, out var real) ? o with { Key = real } : o).ToList();
        }

        // ---------------------------------------------------------------- reporting

        /// <summary>
        /// Fewest lines behind a step of a line (a pick, or a later move in the logged line): the same bar as the move the search
        /// plays (DeckBotBrain.MinPlayLines). Fewer: the line stops there (user, 2026-11-13: "so that we never go through
        /// lower-confidence sections"; a 3-line "end the turn" after Lyla's summon skipped her effect).
        /// </summary>
        [Tunable("Fewest lines behind a step of a line (a pick, or a later move in the logged line): the same bar as the move the search plays (DeckBotBrain.MinPlayLines). Fewer: the line stops there (user, 2026-11-13: \"so that we never go through lower-confidence sections\"; a 3-line \"end the turn\" after Lyla's summon skipped her effect).", Section = "Search")]
        private static readonly Tuned<int> MinPlanVisits = new(20);

        // Our step at a node: the best-scoring answer among those with MinPlanVisits+ lines (like the move the search plays),
        // or null. Their answers (walked past, never planned): the most-visited.
        /// <summary>
        /// This prompt is the searched decision: its kind, board and answers are the real ones (<paramref name="key"/>). A sum the engine asks
        /// at once counts as Omega's one-card-at-a-time pick of it (SelectUnselectCard), with the same cards on offer ("cancel" aside).
        /// </summary>
        private bool IsRootPrompt(Prompt p, DuelHost h, string? key, Random rng)
        {
            bool sumForPick = p.State == BotState.SelectSum && _rootState == BotState.SelectUnselectCard;
            if (key == null || p.State != _rootState && !sumForPick)
                return false;
            string mine = DecisionKey.Of(_rootState, h.Summary(0), h.Summary(1), ReplayOptionKeys(p, h, rng));
            if (FromTurnStart is { } replay && replay.SameCheckpointBoard(mine, key, replay.Checkpoints[^1].Phase))
                return true;
            return sumForPick ? WithoutCancel(mine) == WithoutCancel(key) : mine == key || WithoutGraveOrder(mine) == WithoutGraveOrder(key);
        }

        /// <summary>
        /// A decision key with its answers' graveyard places dropped ("card:7391468@1.16.10" to "card:7391468@1.16") and the answers sorted: a
        /// line's graveyard fills in its own order (live 2026-11-25, Machina #116: D.D. Crow's banish pick from their graveyard never matched
        /// the live one, the same 11 cards at other places, so no line reached it and the brain answered: "no recorded answer").
        /// </summary>
        private static string WithoutGraveOrder(string key)
        {
            int hash = key.LastIndexOf('#');
            if (hash < 0)
                return key;
            var answers = key[(hash + 1)..].Split(';')
                .Select(o => GravePlace.Replace(o, "@$1.16"))
                .Select(o => o.StartsWith("cards:") ? "cards:" + string.Join("+", o[6..].Split('+').OrderBy(c => c, StringComparer.Ordinal)) : o)
                .OrderBy(o => o, StringComparer.Ordinal);
            return key[..(hash + 1)] + string.Join(";", answers);
        }

        private static readonly System.Text.RegularExpressions.Regex GravePlace = new(@"@(\d)\.16\.\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private IEnumerable<string> ReplayOptionKeys(Prompt prompt, DuelHost host, Random rng) =>
            prompt.Data is SelectChainData chain ? SearchActions.ChainOptionKeys(chain)
                : YesNoLegalKeys(prompt) ?? RawPickKeys(prompt) ?? OptionsFor(prompt, true, rng, host)?.Select(option => option.Key) ?? Enumerable.Empty<string>();

        /// <summary>
        /// A card pick's every legal answer, as the live root key lists them (DeckBotBrain.TurnStartPickAsync), not the lines' rule-filtered
        /// ones (live 2026-11-25, Machina #116: D.D. Crow's allowed targets left the lines one card of the eleven on offer live, so the root
        /// never matched: "no recorded answer").
        /// </summary>
        private static IEnumerable<string>? RawPickKeys(Prompt prompt) => prompt.Data switch
        {
            SelectCardData select when (select.SelectableCards?.Length ?? 0) > 0 =>
                SearchActions.CardPicks(select.SelectableCards!, select.SelectionHint == (int)DHint.ATTACKTARGET ? Math.Max(1, select.Minimum) : select.Minimum,
                    select.Maximum, false).Select(p => p.Key).Distinct(),
            SelectTributeData tribute when (tribute.SelectableCards?.Length ?? 0) > 0 =>
                SearchActions.CardPicks(tribute.SelectableCards!, tribute.Minimum, tribute.Maximum, false).Select(p => p.Key).Distinct(),
            SelectUnselectCardData unselect when (unselect.SelectableCards?.Length ?? 0) > 0 =>
                SearchActions.CardPicks(unselect.SelectableCards!, Math.Min(unselect.Minimum, 1), 1, unselect.Minimum == 0 || unselect.Cancelable)
                    .Select(p => p.Key).Distinct(),
            _ => null,
        };

        /// <summary>
        /// A yes/no's legal answers for its decision key: both, even when a rule leaves the lines one (EffectAnswer). The live decision is
        /// keyed on both (live 2026-11-24: Machina Gearframe's search trigger, forced "yes" in the lines by ActivateOnSummon, never matched
        /// the live "yes;no", so no line reached it, the replay declined the trigger and missed: a PLAN GAP). Null for other prompts and
        /// "attack directly?" (one answer, never searched live).
        /// </summary>
        internal static IEnumerable<string>? YesNoLegalKeys(Prompt prompt) =>
            prompt.Data is SelectEffectYnData || prompt.Data is SelectYesNoData { Description: not 31 } ? YesNoKeys : null;

        private static readonly string[] YesNoKeys = { "yes", "no" };

        // A decision key without its "cancel" answer (the last '#' part lists the answers, ';'-separated).
        private static string WithoutCancel(string key) =>
            key.LastIndexOf('#') is int hash and >= 0
                ? key[..(hash + 1)] + string.Join(";", key[(hash + 1)..].Split(';').Where(o => o != "cancel"))
                : key;


        /// <summary>A real answer, not a sub-node for another kind of prompt ("@" key).</summary>
        private static bool IsMove(string key) => !key.StartsWith('@');

        private static KeyValuePair<string, MctsNode>? StepAt(MctsNode node, bool ours)
        {
            var tried = node.Children.Where(kv => IsMove(kv.Key) && kv.Value.Visits >= MinPlanVisits).ToList();
            if (tried.Count == 0)
                return null;
            return ours ? tried.OrderByDescending(kv => kv.Value.Mean).First() : tried.OrderByDescending(kv => kv.Value.Visits).First();
        }

        // Our steps below each root answer, up to our next main / battle decision.
        private Dictionary<string, List<PlanStep>> Plans(List<RootStat> stats)
        {
            var plans = new Dictionary<string, List<PlanStep>>();
            foreach (var stat in stats)
            {
                MctsNode? node;
                lock (_start.Lock)
                    node = _start.Children.GetValueOrDefault(stat.Key);
                var steps = new List<PlanStep>();
                // The set window: its set plan (the best one by StepAt, else the most tried) is the whole of it.
                if (stat.Key == SearchActions.SetWindowKey)
                {
                    if (node != null)
                        lock (node.Lock)
                            if ((StepAt(node, ours: true) ?? node.Children.Where(kv => IsMove(kv.Key)).OrderByDescending(kv => kv.Value.Visits)
                                    .Select(kv => (KeyValuePair<string, MctsNode>?)kv).FirstOrDefault()) is { } planStep)
                                steps.Add(new PlanStep(BotState.SelectIdleCmd, planStep.Key, node.LabelOf(planStep.Key, planStep.Value)));
                    plans[stat.Key] = steps;
                    continue;
                }
                var seen = new HashSet<MctsNode>(ReferenceEqualityComparer.Instance);
                while (node != null && seen.Add(node))
                {
                    KeyValuePair<string, MctsNode> next;
                    string planLabel = "";
                    BotState state;
                    lock (node.Lock)
                    {
                        if (node.Children.Count == 0 || node.ChildPlayer != 0 || node.ChildState is BotState.SelectIdleCmd or BotState.SelectBattleCmd)
                            break;
                        if (StepAt(node, ours: true) is not { } step)
                            break;
                        next = step;
                        state = node.ChildState;
                        planLabel = node.LabelOf(next.Key, next.Value);
                    }
                    steps.Add(new PlanStep(state, next.Key, planLabel));
                    node = next.Value;
                }
                plans[stat.Key] = steps;
            }
            return plans;
        }

        /// <summary>The line from the root through our turn (our decisions by StepAt; theirs are walked past), for the log.</summary>
        /// <param name="firstKey">The root choice the line starts with (null: the most-visited one).</param>
        private List<PlanStep> BestLine(string? firstKey = null)
        {
            var steps = new List<PlanStep>();
            var node = _start;
            for (int guard = 0; guard < 64; guard++)
            {
                KeyValuePair<string, MctsNode> next;
                string lineLabel = "";
                BotState state;
                int player;
                lock (node.Lock)
                {
                    if (node.Children.Count == 0)
                        break;
                    if (guard == 0 && firstKey != null)
                    {
                        if (!node.Children.TryGetValue(firstKey, out var first))
                            break;
                        next = new KeyValuePair<string, MctsNode>(firstKey, first);
                    }
                    else if (StepAt(node, ours: node.ChildPlayer == 0) is { } step)
                        next = step;
                    else
                        break;
                    state = node.ChildState;
                    player = node.ChildPlayer;
                    lineLabel = node.LabelOf(next.Key, next.Value);
                }
                // Our chains are in the line too (not passes): a real chain the chosen line didn't have is logged as unplanned.
                if (player == 0 && (state != BotState.SelectChain || next.Key != "pass"))
                    steps.Add(new PlanStep(state, next.Key, lineLabel));
                // Our turn ends where we pass to the End Phase.
                if (player == 0 && next.Key == "end")
                    break;
                node = next.Value;
            }
            return steps;
        }

        // The nodes along the most-visited line from this root move through our turn (stops at "end").
        private HashSet<MctsNode> LineNodes(string firstKey)
        {
            var seen = new HashSet<MctsNode>(ReferenceEqualityComparer.Instance);
            MctsNode? node;
            lock (_start.Lock)
                node = _start.Children.GetValueOrDefault(firstKey);
            for (int guard = 0; node != null && guard < 64; guard++)
            {
                if (!seen.Add(node))
                    break;
                KeyValuePair<string, MctsNode>? next;
                lock (node.Lock)
                    next = node.Children.Count(kv => IsMove(kv.Key)) == 0 ? null : node.Children.Where(kv => IsMove(kv.Key)).OrderByDescending(kv => kv.Value.Visits).First();
                if (next is not { } step || step.Key == "end")
                    break;
                node = step.Value;
            }
            return seen;
        }

        // Among the top root moves: pairs whose best lines meet in the same node (another order of the same play).
        private List<(string A, string B)> Transposed(List<RootStat> stats)
        {
            var top = stats.Take(4).Select(s => (s.Key, Nodes: LineNodes(s.Key))).ToList();
            var pairs = new List<(string, string)>();
            for (int i = 0; i < top.Count; i++)
                for (int j = i + 1; j < top.Count; j++)
                    if (top[i].Nodes.Overlaps(top[j].Nodes))
                        pairs.Add((top[i].Key, top[j].Key));
            return pairs;
        }

        private int _checked;
        private string? _mismatch;

        /// <summary>
        /// Once per search: does the rebuilt board match the bot's view (monsters and spells/traps on each side)? A rebuild
        /// that puts a card on the wrong side or loses it would make every line wrong.
        /// </summary>
        private void CheckPosition(DuelHost host)
        {
            if (Interlocked.Exchange(ref _checked, 1) != 0)
                return;
            var ours = host.Summary(0);
            var theirs = host.Summary(1);
            string seen = $"ours {ours.Monsters.Count} monsters / {ours.SpellCodes.Count} S/T, theirs {theirs.Monsters.Count} / {theirs.SpellCodes.Count}";
            string view = $"ours {_view.OwnMonsters.Count} / {_view.OwnSpells.Count}, theirs {_view.EnemyMonsters.Count} / {_view.EnemySpells.Count}";
            if (ours.Monsters.Count != _view.OwnMonsters.Count || ours.SpellCodes.Count != _view.OwnSpells.Count ||
                theirs.Monsters.Count != _view.EnemyMonsters.Count || theirs.SpellCodes.Count != _view.EnemySpells.Count)
                _mismatch = $"rebuilt board: {seen}; real board: {view}";
        }

        /// <summary>The "do nothing" answer to an optional prompt (no chain, don't use the effect), or null when it must be answered.</summary>
        /// <summary>
        /// The same kind of prompt for a replay: a card pick is one whichever pick prompt asks it (seen 2026-11-17: Substitoad's tribute was a
        /// SelectCard on Omega and a SelectUnselectCard in the search's engine, and every mid-chain replay after it missed).
        /// </summary>
        internal static bool SameKind(BotState a, BotState b) =>
            a == b || IsPick(a) && IsPick(b);

        internal static bool IsPick(BotState state) =>
            state is BotState.SelectCard or BotState.SelectUnselectCard or BotState.SelectTribute or BotState.SelectSum;

        internal static byte[]? Passive(Prompt prompt) => prompt.Data switch
        {
            SelectChainData { Forced: false } => BitConverter.GetBytes(-1),
            SelectEffectYnData or SelectYesNoData => new byte[] { 0 },
            _ => null,
        };

        internal static void Answer(DuelHost host, Prompt prompt, byte[] packet)
        {
            var response = DuelHost.TranslatePacket(prompt, packet) ?? prompt.Fallbacks.FirstOrDefault() ?? packet;
            host.Respond(prompt, response);
        }

        /// <summary>What playing a plan out in the engine showed: lines tried, lines where every step was on offer, lines that
        /// ended with their LP at 0 within our turn, and the first step that wasn't on offer (with its line count).</summary>
        internal sealed record LineCheck(int Lines, int Completed, int Lethal, string? Broke, int BrokeLines);

        /// <summary>
        /// Plays a plan out on the engine before it's adopted (user, 2026-11-12: "test it in engine before committing all the
        /// plays"): from this decision, our main / battle steps in order (the first one included), the picks between them by
        /// our brain, the rest of our turn by our brain; they pass every chance to respond (whether they can stop it is the
        /// push roll's question, not this one). Checks that each step is actually on offer when its turn comes (the rules
        /// engine knows what the brain's arithmetic doesn't: Lyla in defence after her effect can't attack) and, for a lethal
        /// plan, that their LP really reach 0. <paramref name="match"/> finds a step's live option among the offered keys.
        /// </summary>
        public LineCheck CheckLine(IReadOnlyList<PlanStep> steps, Func<PlanStep, IReadOnlyCollection<string>, string?> match, int lines, int seed)
        {
            using var stateScope = StateTuning.Use(State);
            SearchHost.Register(_view.AllKnownIds());
            var engine = EnginePool.TryTake(out var pooled) ? pooled : new PuzzleEngine(SearchHost.Cards, SearchHost.Scripts, seed);
            using var giveBack = new PoolReturn(engine);
            var brain = _brainFactory?.Invoke();
            int completed = 0, lethal = 0, tried = 0;
            var broke = new Dictionary<string, int>();
            for (int i = 0; i < lines; i++)
            {
                var rng = new Random(seed * 31 + i);
                var position = _view.Determinize(rng);
                var host = engine.Load(position.ToLua(_view.FirstTurnOfDuel ? DuelHost.PuzzleFlags & ~0x02UL : DuelHost.PuzzleFlags), out _);
                if (host == null)
                    continue;
                tried++;
                try
                {
                    AttachBrain(brain, host);
                    host.SetLifePoints(position.LifePoints[0], position.LifePoints[1]);
                    var line = new MctsPolicy.LineState();
                    bool rootReached = false, autoBattle = _view.InBattlePhase, setWindowChosen = false;
                    int positionWindowPhase = -1; // the turn and Main Phase (turn * 0x10000 + phase) in which the line opened the position window (SearchMoveRules), or -1
                    int next = 0;
                    string? stop = null;
                    int checkPrompts = 0;
                    Prompt? prompt;
                    // NextPrompt(n) stops when turn n *begins*: SearchTurn + 1 plays our whole turn and stops at theirs.
                    while ((prompt = host.NextPrompt(SearchTurn + 1)) != null && stop == null)
                    {
                        if (++checkPrompts > MaxPromptsPerLine) { stop = "over 3000 prompts (a loop)"; break; }
                        if (prompt.Attempts > 0)
                        {
                            int fallback = prompt.Attempts - 1;
                            if (fallback >= prompt.Fallbacks.Count) { stop = $"no valid answer to {prompt.State}"; break; }
                            host.Respond(prompt, prompt.Fallbacks[fallback]);
                            continue;
                        }
                        bool ours = prompt.Player == 0;
                        if (ours && !rootReached)
                        {
                            if (autoBattle && prompt.State == BotState.SelectIdleCmd && ((IdleCmdData)prompt.Data).CanBattlePhase)
                            {
                                autoBattle = false;
                                Answer(host, prompt, SearchActions.Packet((int)MainAction.ToBattlePhase, 0));
                                continue;
                            }
                            rootReached = prompt.State == _rootState;
                            if (rootReached)
                                host.HideUnseenDeckReveals();
                        }
                        if (!rootReached && Passive(prompt) is { } pass)
                        {
                            Answer(host, prompt, pass);
                            continue;
                        }
                        // Their side passes: no responses (see above).
                        if (!ours)
                        {
                            Answer(host, prompt, Passive(prompt) ?? MctsPolicy.Answer(prompt, rng, line, host));
                            continue;
                        }
                        // Our main / battle decisions: the plan's steps while there are any, then our brain.
                        if (rootReached && next < steps.Count && prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd)
                        {
                            var step = steps[next];
                            var options = OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, allowSetsBeforePlays: next == 0, brain: brain) ?? new List<Option>();
                            var key = step.State == prompt.State ? match(step, options.Select(o => o.Key).ToList()) : null;
                            if (key != null && !MoveRules.PlanStepAllowed(key, host))
                                key = null;
                            if (key == null)
                            {
                                stop = $"step {next + 1} ({step.Label}) not on offer";
                                break;
                            }
                            next++;
                            if (key.StartsWith("3:", StringComparison.Ordinal) || key.StartsWith("4:", StringComparison.Ordinal))
                                setWindowChosen = true;
                            var stepPacket = options.First(o => o.Key == key).Packet;
                            if (OpensPositionWindow(prompt, stepPacket, host))
                                positionWindowPhase = host.Turn * 0x10000 + host.Phase;
                            Answer(host, prompt, stepPacket);
                            continue;
                        }
                        var answer = MoveRules.ForcedSearchPick(prompt, host) is byte gardna
                            ? new byte[] { 1, gardna }
                            : brain?.Decide(prompt) ?? MctsPolicy.Answer(prompt, rng, line, host);
                        if (prompt.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd or BotState.SelectPosition &&
                            OptionsFor(prompt, true, rng, host, setWindowChosen, positionWindowPhase == host.Turn * 0x10000 + host.Phase, allowSetsBeforePlays: true, brain: brain)
                                is { Count: > 0 } legal)
                        {
                            var matched = brain != null ? AskAgainIfRefused(brain, prompt, legal, answer).Proposed : MatchOption(legal, answer, prompt.State);
                            answer = (matched ?? RolloutFallback(legal)).Packet;
                        }
                        if (prompt.Data is IdleCmdData && answer.Length >= 2 &&
                            (BitConverter.ToUInt16(answer, 0) is (ushort)MainAction.SetMonster or (ushort)MainAction.SetSpell))
                            setWindowChosen = true;
                        if (OpensPositionWindow(prompt, answer, host))
                            positionWindowPhase = host.Turn * 0x10000 + host.Phase;
                        Answer(host, prompt, answer);
                    }
                    if (stop == null && next < steps.Count)
                        stop = !rootReached ? "the position never reached our decision" : $"the turn ended before step {next + 1} ({steps[next].Label})";
                    if (stop != null)
                        broke[stop] = broke.GetValueOrDefault(stop) + 1;
                    else
                        completed++;
                    if (host.LifePoints(1) <= 0 || host.Result is { Winner: 0 })
                        lethal++;
                }
                catch (Exception ex)
                {
                    broke[$"failed: {ex.Message}"] = broke.GetValueOrDefault($"failed: {ex.Message}") + 1;
                }
                finally
                {
                    host.Dispose();
                }
            }
            var worst = broke.OrderByDescending(kv => kv.Value).FirstOrDefault();
            return new LineCheck(tried, completed, lethal, worst.Key, worst.Value);
        }

        // ---------------------------------------------------------------- tree answers


        /// <summary>Most answers to one multi-card selection listed at once (the rest are sampled afresh per line).</summary>
        [Tunable("Most answers to one multi-card selection listed at once (the rest are sampled afresh per line).", Section = "Search")]
        private static readonly Tuned<int> MaxPickOptions = new(64);

        /// <summary>Most cards a pick may offer and still be a decision of the tree (was 12; user, 2026-11-15: "this should be unlimited,
        /// or like 80 or fewer if a limit is truly needed"). Several-card picks are sampled (MaxPickOptions).</summary>
        [Tunable("Most cards a pick may offer and still be a decision of the tree (was 12; user, 2026-11-15: \"this should be unlimited, or like 80 or fewer if a limit is truly needed\"). Several-card picks are sampled (MaxPickOptions).", Section = "Search")]
        private static readonly Tuned<int> MaxTreePickCards = new(80);

        private static List<Option> Picks(List<SearchActions.Pick> picks) => picks.Select(p => new Option(p.Key, p.Label, p.Packet)).ToList();

        private SearchMoveRules? _moveRules;

        /// <summary>The card rules for this search's moves (Brains/SearchRules/SearchMoveRules): the search itself knows no card.</summary>
        private SearchMoveRules MoveRules => _moveRules ??= new SearchMoveRules(_view, State, GladiatorSetForRoutine,
            FromTurnStart == null, EffectUsed, RootTurn, _evaluator.BackrowDoomRule, _evaluator.MstOwnFutureFusion)
        {
            Evaluator = _evaluator, ClearingHandForAllure = ClearingHandForAllure,
        };

        /// <summary>Checking the brain's desperate Allure of Darkness routine: its sets may come before its other plays (SearchMoveRules).</summary>
        public bool ClearingHandForAllure { get; init; }

        private static Option ToOption(MoveOption o) => new(o.Key, o.Label, o.Packet, o.MandatoryReason);
        private static MoveOption ToMove(Option o) => new(o.Key, o.Label, o.Packet, o.MandatoryReason);

        /// <summary>
        /// The answers the search may give at a prompt: every answer on offer, then the card rules (SearchMoveRules) take away what
        /// they forbid and mark what they force. Generic: no card is named here.
        /// </summary>
        /// <param name="ourTurn">Our own decision in our turn: drop field effects we already used this turn, and moves the deck bans.</param>
        private List<Option>? OptionsFor(Prompt prompt, bool ourTurn, Random rng, DuelHost? host = null, bool setWindowChosen = false, bool positionWindowChosen = false,
            bool allowSetsBeforePlays = false, SearchBrain? brain = null, bool setPlanDone = false)
        {
            switch (prompt.Data)
            {
                case IdleCmdData idle:
                {
                    var rules = MoveRules.ForMain(prompt, idle, ourTurn, host, setWindowChosen, allowSetsBeforePlays && ourTurn, positionWindowChosen, setPlanDone);
                    var choices = SearchActions.Idle(idle, ourTurn && UsedEffects == null ? _usedThisTurn : null, rules.Allowed)
                        .Where(c => !ourTurn || UsedEffects == null || c.Response.Action != MainAction.Activate || !(c.Key.Contains("@0.4.") || c.Key.Contains("@0.8.")) ||
                                    !long.TryParse(c.Key[(c.Key.LastIndexOf(':') + 1)..], out long desc) || !EffectUsed(SearchActions.KeyCardId(c.Key), desc))
                        .Where(c => c.Response.Action != MainAction.ShuffleHand)
                        .Where(c => !ourTurn || MayChoose == null || c.Response.Action is MainAction.ToBattlePhase or MainAction.ToEndPhase ||
                                    MayChoose(SearchActions.KeyCardId(c.Key), c.Response.Action))
                        .ToList();
                    if (ourTurn && prompt.Player == 0 && brain != null)
                    {
                        var blocked = brain.BlockedIdleMoves(prompt, idle);
                        choices.RemoveAll(choice => blocked.Contains((choice.Response.Action, choice.Response.Index)));
                    }
                    return rules.Finish(choices)
                        .Select(x => new Option(x.Choice.Key, x.Choice.Label, SearchActions.Packet(x.Choice.Response), x.Reason)).ToList();
                }
                case BattleCmdData battle:
                    return SearchActions.Battle(battle)
                        .Where(c => SearchMoveRules.BattleAllowed(prompt, battle, c) && MoveRules.BattleActivationAllowed(prompt, c, host))
                        .Select(c => new Option(c.Key, c.Label, SearchActions.Packet(c.Response))).ToList();
                // Card picks, single or several (Brionac's discards and targets). No "cancel" for an attack target: whether to
                // attack is decided at the battle prompt (seen 2026-11-10: "attack, then cancel" cancelled the brain's attacks).
                case SelectCardData select when (select.SelectableCards?.Length ?? 0) is var n && n > 0 && n <= MaxTreePickCards:
                {
                    if (host != null && MoveRules.ForcedSearchPick(prompt, host) is byte forced)
                        return Picks(SearchActions.CardPicks(select.SelectableCards!, 1, 1, false)
                            .Where(pick => pick.Indexes[0] == forced).ToList());
                    bool attackTarget = select.SelectionHint == (int)DHint.ATTACKTARGET;
                    var picks = Picks(SearchActions.CardPicks(select.SelectableCards!, attackTarget ? Math.Max(1, select.Minimum) : select.Minimum, select.Maximum,
                        select.Cancelable && !attackTarget && prompt.Player != 0, rng, MaxPickOptions)).Select(ToMove).ToList();
                    picks = SearchMoveRules.DiscardPicks(prompt, select.SelectionHint, select.SelectableCards!, picks, host);
                    picks = SearchMoveRules.FutureFusionPicks(prompt, select.SelectionHint, select.SelectableCards!, picks);
                    picks = MoveRules.EffectPicks(prompt, select, picks, host, ourTurn);
                    return picks.Select(ToOption).ToList();
                }
                case SelectTributeData tribute when (tribute.SelectableCards?.Length ?? 0) is var n && n > 0 && n <= MaxTreePickCards:
                    return Picks(SearchActions.CardPicks(tribute.SelectableCards!, tribute.Minimum, tribute.Maximum, false, rng, MaxPickOptions));
                case SelectUnselectCardData unselect when (unselect.SelectableCards?.Length ?? 0) is var n && n > 0 && n <= MaxTreePickCards:
                    // Our own cost pick can't be cancelled in a line (seen 2026-11-18: "special summon Dark Grepher -> cancel" scored as ending the
                    // turn and was played; Omega offered no cancel, so Sirocco was discarded): "cancel" only where it finishes a selection.
                {
                    var picks = Picks(SearchActions.CardPicks(unselect.SelectableCards!, Math.Min(unselect.Minimum, 1), 1,
                        unselect.Minimum == 0 || unselect.Cancelable && prompt.Player != 0)).Select(ToMove).ToList();
                    picks = SearchMoveRules.FutureFusionPicks(prompt, unselect.SelectionHint, unselect.SelectableCards!, picks);
                    return SearchMoveRules.DiscardPicks(prompt, unselect.SelectionHint, unselect.SelectableCards!, picks, host)
                        .Select(ToOption).ToList();
                }
                // Our sum (Machina Fortress's discard, fusion materials): one option per card, keyed as Omega's one-card-at-a-time prompt keys
                // it (the live decision is a SelectUnselectCard), answered with a valid sum that includes that card (seen 2026-11-18 live: the
                // search could never stop at the discard).
                case SelectSumData sum when prompt.Player == 0 && (sum.SelectableCards?.Length ?? 0) > 0:
                {
                    var cards = sum.SelectableCards!;
                    static int Param(DynamicCard c) => c.OpParam1 | (c.OpParam2 << 16);
                    var must = (sum.MandatoryCards ?? Array.Empty<DynamicCard>()).Select(Param).ToList();
                    var all = cards.Select(Param).ToList();
                    var list = new List<Option>();
                    for (int i = 0; i < cards.Length; i++)
                        if (DuelSim.Engine.SumSolver.FindWith(must, all, i, sum.Sum, sum.Minimum, sum.Maximum, sum.MustBeExactValue) is { } picked)
                        {
                            var one = SearchActions.CardPicks(new[] { cards[i] }, 1, 1, false)[0];
                            list.Add(new Option(one.Key, one.Label, new[] { (byte)picked.Count }.Concat(picked.Select(x => (byte)x)).ToArray()));
                        }
                    return list.GroupBy(o => o.Key).Select(g => g.First()).ToList();
                }
                case SelectChainData chain:
                {
                    var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
                    var descs = chain.Descriptions ?? Array.Empty<int>();
                    var list = new List<MoveOption>();
                    if (!chain.Forced)
                        list.Add(new MoveOption("pass", "don't chain", BitConverter.GetBytes(-1)));
                    for (int i = 0; i < cards.Length; i++)
                    {
                        // A once-per-turn effect of ours already used this turn (the rebuilt card forgot): as at main decisions.
                        if (ourTurn && prompt.Player == 0 && !chain.Forced &&
                            (UsedEffects != null ? EffectUsed(cards[i].ID, i < descs.Length ? descs[i] : 0) : _usedThisTurn.Contains(cards[i].ID)) &&
                            cards[i].CurrentCoordinates.Location is (byte)CardLocation.MonsterZone or (byte)CardLocation.SpellZone)
                            continue;
                        list.Add(new MoveOption($"chain:{SearchActions.CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}",
                            $"chain {OmegaBotWrapper.Logging.CardNames.Get(cards[i].ID)}", BitConverter.GetBytes(i),
                            chain.Forced ? "the game requires a chain response" : null));
                    }
                    list = list.GroupBy(o => o.Key).Select(g => g.First()).ToList();
                    return MoveRules.ChainOptions(prompt, chain, list, host, ourTurn).Select(ToOption).ToList();
                }
                // "Attack directly?": Omega always attacks directly (and the simulator answers yes whatever is sent): one answer.
                case SelectYesNoData { Description: 31 }:
                    return new List<Option> { new("yes", "yes", new byte[] { 1 }) };
                case SelectEffectYnData effect:
                    if (MoveRules.EffectAnswer(prompt, effect, host) is { } forcedAnswer)
                        return new List<Option> { ToOption(forcedAnswer) };
                    return new List<Option> { new("yes", "yes", new byte[] { 1 }), new("no", "no", new byte[] { 0 }) };
                case SelectYesNoData:
                    return new List<Option> { new("yes", "yes", new byte[] { 1 }), new("no", "no", new byte[] { 0 }) };
                // A monster's position (plan step 5): a decision of the tree like any other. (Zones aren't: Omega places main monster zones
                // itself whatever the bot answers, and spell/trap zones change nothing.)
                case SelectPositionData position when (position.Positions?.Length ?? 0) > 1:
                    if (prompt.Player == 0 && CardRuleRegistry.RequiredSummonPosition(position.CardID, position.Positions) is CardPosition required)
                        return new List<Option> { new($"pos:{(byte)required}", "attack position", new[] { (byte)required }, "never summon in defence") };
                    return position.Positions!.Select(p => new Option($"pos:{p}", (CardPosition)p switch
                    {
                        CardPosition.FaceUpAttack => "attack position", CardPosition.FaceUpDefence => "defence position",
                        CardPosition.FaceDownDefence => "set (face-down defence)", _ => "face-down attack position",
                    }, new[] { p })).ToList();
                case SelectOptionData option:
                    if (SearchMoveRules.OptionIndex(prompt, option, host) is int index)
                        return new List<Option> { new($"option:{option.Options![index]}", $"option {index + 1}", BitConverter.GetBytes(index)) };
                    return Enumerable.Range(0, option.Options?.Length ?? 0)
                        .Select(i => new Option($"option:{option.Options![i]}", $"option {i + 1}", BitConverter.GetBytes(i))).ToList();
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// A main / battle decision of ours as a key: the board (our side in full, theirs as far as it's public), and the answers
    /// on offer, which carry what the board doesn't (Normal Summon used, once-per-turn effects spent, the Battle Phase still to
    /// come). Zones and card-id variants are left out (SearchActions.LooseKey), so the real game and a line of the search
    /// reaching the same decision give the same key, however the cards were placed.
    /// </summary>
    internal static class DecisionKey
    {
        private static int Canon(int code) => SearchHost.CanonicalId(code) is > 0 and var official ? official : code;
        private static string Ids(IEnumerable<int> ids) => string.Join(",", ids.Select(Canon).OrderBy(x => x));

        private static string Make(BotState state, int[] lp, IEnumerable<int> hand, IEnumerable<string> monsters, IEnumerable<string> spells,
            IEnumerable<int> grave, IEnumerable<string> theirMonsters, int theirSetMonsters, IEnumerable<int> theirSpells, int theirSetSpells,
            IEnumerable<int> theirGrave, IEnumerable<string> options) =>
            $"{state}|{lp[0]}|{lp[1]}|{Ids(hand)}|{string.Join(",", monsters.OrderBy(x => x, StringComparer.Ordinal))}|" +
            $"{string.Join(",", spells.OrderBy(x => x, StringComparer.Ordinal))}|{Ids(grave)}#" +
            $"{string.Join(",", theirMonsters.OrderBy(x => x, StringComparer.Ordinal))}+{theirSetMonsters}|{Ids(theirSpells)}+{theirSetSpells}|{Ids(theirGrave)}#" +
            string.Join(";", options.Select(SearchActions.DecisionOptionKey).OrderBy(x => x, StringComparer.Ordinal));

        private static string Monster(int code, bool faceUp, bool attack) => faceUp ? $"{Canon(code)}:{(attack ? "a" : "d")}" : "set";

        /// <summary>A decision reached in a line of the search.</summary>
        public static string Of(BotState state, SideSummary ours, SideSummary theirs, IEnumerable<string> options) =>
            Make(state, new[] { ours.LifePoints, theirs.LifePoints }, ours.HandCodes,
                ours.Monsters.Select(m => m.FaceUp ? Monster(m.Code, true, m.AttackPosition) : $"{Canon(m.Code)}:s"),
                ours.SpellCodes.Select(s => $"{Canon(s.Code)}:{(s.FaceUp ? "u" : "s")}"),
                ours.GraveCodes,
                theirs.Monsters.Where(m => m.FaceUp).Select(m => Monster(m.Code, true, m.AttackPosition)), theirs.Monsters.Count(m => !m.FaceUp),
                theirs.SpellCodes.Where(s => s.FaceUp).Select(s => s.Code), theirs.SpellCodes.Count(s => !s.FaceUp),
                theirs.GraveCodes, options);

        /// <summary>
        /// A decision of a line as a position for transpositions: the decision key plus what it leaves out (every monster's current
        /// ATK/DEF, both sides' hand and deck sizes), so only truly identical positions are merged (user, 2026-11-15: cautious).
        /// </summary>
        public static string Strict(BotState state, SideSummary ours, SideSummary theirs, IEnumerable<string> options)
        {
            static string Stats(SideSummary s) => string.Join(",", s.Monsters.Select(m => $"{Canon(m.Code)}:{m.Attack}/{m.Defense}:{(m.FaceUp ? 1 : 0)}{(m.AttackPosition ? "a" : "d")}")
                .OrderBy(x => x, StringComparer.Ordinal));
            return Of(state, ours, theirs, options) + $"#{Stats(ours)}#{Stats(theirs)}#{ours.Hand}/{ours.Deck}/{theirs.Hand}/{theirs.Deck}";
        }

        /// <summary>The real decision, from what the bot sees.</summary>
        public static string Of(BotState state, SearchView view, IEnumerable<string> options)
        {
            static bool FaceUp(uint position) => (position & 0x5) != 0;
            static bool Attack(uint position) => (position & 0x3) != 0;
            return Make(state, view.LifePoints, view.OwnHand,
                view.OwnMonsters.Select(m => FaceUp(m.Position) ? Monster(m.Code, true, Attack(m.Position)) : $"{Canon(m.Code)}:s"),
                view.OwnSpells.Select(s => $"{Canon(s.Code)}:{(FaceUp(s.Position) ? "u" : "s")}"),
                view.OwnGrave,
                view.EnemyMonsters.Where(m => m.Code > 0 && FaceUp(m.Position)).Select(m => Monster(m.Code, true, Attack(m.Position))),
                view.EnemyMonsters.Count(m => m.Code <= 0 || !FaceUp(m.Position)),
                view.EnemySpells.Where(s => s.Code > 0 && FaceUp(s.Position)).Select(s => s.Code),
                view.EnemySpells.Count(s => s.Code <= 0 || !FaceUp(s.Position)),
                view.EnemyGrave, options);
        }
    }
}
