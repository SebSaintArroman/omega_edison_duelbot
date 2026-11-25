using BotTest.Game.AI.Brains;
using BotTest.Game.AI.Search;
using DuelSim.Engine;

namespace DuelSim.Match;

/// <summary>
/// DuelSim replay-check FILE|DIR [--lines N] [--ms MS] [--seed N]: re-runs a live turn's replay saved by the bot when its lines missed
/// (BotTest/duel-logs/replays/*.json, ReplayDump; user, 2026-11-25). A searched decision runs the same search as live (from the turn's
/// start to that decision, with the deck's brain) and reports how many lines reached it and every reason the others didn't; an end-of-turn
/// audit replays the turn N times. A directory checks every dump in it, one line each.
/// </summary>
internal static class ReplayCheck
{
    public static int Run(string[] args, Action<string> write)
    {
        if (args.Length < 2)
        {
            write("Usage: DuelSim replay-check FILE.json|DIR [--lines N] [--ms MS] [--seed N] [--verbose]");
            return 1;
        }
        if (!SearchHost.Available)
        {
            write($"search unavailable: {SearchHost.Problem}");
            return 1;
        }
        string target = args[1];
        int lines = int.Parse(Option(args, "--lines", "200"));
        int ms = int.Parse(Option(args, "--ms", "0"));
        int seed = int.Parse(Option(args, "--seed", "1"));
        bool verbose = args.Contains("--verbose") || !Directory.Exists(target);
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string> { target };
        int clean = 0;
        foreach (string file in files)
        {
            try
            {
                bool ok = CheckOne(file, lines, ms, seed, verbose, write);
                if (ok)
                    clean++;
            }
            catch (Exception ex)
            {
                write($"{Path.GetFileName(file)}: FAILED to check ({ex.Message})");
            }
        }
        if (files.Count > 1)
            write($"{clean}/{files.Count} dump(s) replay with no miss");
        return clean == files.Count ? 0 : 2;
    }

    private static bool CheckOne(string file, int lines, int ms, int seed, bool verbose, Action<string> write)
    {
        var dump = ReplayDump.Load(file);
        // A mid-chain replan: the anchor decision's board, our answers since and their reveals, to the chain window.
        var midChain = dump.MidChain is { } m ? ReplayDump.MidChainReplay(m) : ((SearchView View, ReplayScript Script)?)null;
        var replay = midChain == null ? ReplayDump.Replay(dump) : null;
        if (replay != null)
            SearchHost.Register(replay.Start.AllKnownIds().Concat(replay.Checkpoints.SelectMany(c => c.View.AllKnownIds())));
        else
            SearchHost.Register(midChain!.Value.View.AllKnownIds());
        string head = $"{Path.GetFileName(file)}: {dump.Deck}, turn {dump.Turn} ({(dump.Ours ? "ours" : "theirs")}), " +
                      (replay != null ? $"{replay.Checkpoints.Count} checkpoint(s)" : $"mid-chain, {dump.MidChain!.Ours.Count} answer(s) since the anchor");
        if (verbose)
        {
            write(head);
            write($"  live: {dump.Note}");
            if (replay != null)
                write($"  {replay.DescribeStart()}");
        }

        // An end-of-turn audit: the turn replayed whole, as the bot's audit does.
        if (dump.RootState is not { } rootState)
        {
            if (replay == null)
                throw new InvalidDataException("a mid-chain dump without its decision");
            var runs = new List<ReplayRun>();
            using (var engine = new PuzzleEngine(SearchHost.Cards, SearchHost.Scripts, seed * 7919))
            {
                var rng = new Random(seed);
                for (int i = 0; i < lines; i++)
                    runs.Add(replay.Play(engine, rng));
            }
            int all = runs.Count(r => r.Miss == null);
            write($"{(verbose ? "  " : head + ": ")}audit: {all}/{runs.Count} line(s) reached all {replay.Checkpoints.Count} checkpoints");
            foreach (var g in runs.Where(r => r.Miss != null).GroupBy(r => (r.MissAt, r.Miss)).OrderByDescending(g => g.Count()))
                write($"    {g.Count()}x at checkpoint {g.Key.MissAt + 1}: {g.Key.Miss}");
            return all == runs.Count;
        }

        // A searched decision: the live search from the turn's start, with the deck's brain in the lines.
        var deckDef = MatchRunner.FindDeck("MasterDuel", dump.Deck);
        var deckBrain = Tuning.BrainParams.FindBrain(deckDef.Factory()) as DeckBotBrain;
        Func<SearchBrain>? brains = deckBrain == null ? null
            : () => new SearchBrain(deckBrain, deckDef.DeckName, deckDef.Factory().DeckHash, true);
        var view = replay?.Start ?? midChain!.Value.View;
        var evaluator = new BoardEvaluator(new CardValues(view.OwnGrave, view.EnemyGrave), deckBrain == null ? null : deckBrain.SearchCardBonus,
            Math.Max(0, view.EnemyHandCount - view.EnemyKnownHand.Count) + view.EnemySpells.Count(c => c.Code <= 0), 0.5)
        {
            OwnDeck = view.OwnDeck, OwnHand = view.OwnHand, OwnGrave = view.OwnGrave, OwnBanished = view.OwnBanished,
            OwnMonsters = view.OwnMonsters.Select(c => c.Code).Where(c => c > 0).ToList(),
            TheirStartLp = view.LifePoints[1], OurStartLp = view.LifePoints[0],
        };
        var settings = new SearchSettings(BudgetMs: ms > 0 ? ms : 0, Threads: 1, MaxIterations: ms > 0 ? int.MaxValue : lines, Horizon: 1);
        var tree = new MctsTree { Label = "replay-check" };
        string brainKey = dump.MidChain?.BrainKey is { Length: > 0 } planned ? planned : dump.Options.FirstOrDefault() ?? "";
        var search = replay != null
            ? new Mcts(view, tree, tree.Root, rootState, brainKey, Array.Empty<int>(), settings, evaluator, brains)
            {
                MayChoose = deckBrain == null ? null : deckBrain.PlanMayChoose,
                Suggestions = deckBrain == null ? null : deckBrain.PlanSuggestions,
                FromTurnStart = replay, RootKey = dump.RootKey, LiveRootKeys = dump.Options,
                CollectReplayMisses = true,
            }
            : new Mcts(view, tree, tree.Root, rootState, brainKey, dump.MidChain!.Activated.ToArray(), settings, evaluator, brains)
            {
                MayChoose = deckBrain == null ? null : deckBrain.PlanMayChoose,
                Suggestions = deckBrain == null ? null : deckBrain.PlanSuggestions,
                Replay = midChain!.Value.Script, UsedEffects = dump.MidChain.UsedEffects, LiveRootKeys = dump.Options,
                CollectReplayMisses = true,
            };
        var outcome = search.Run(seed);
        int reached = Math.Max(0, outcome.Iterations - search.ReplayMisses);
        write($"{(verbose ? "  " : head + ": ")}search at {rootState}: {reached}/{outcome.Iterations} line(s) reached the decision, {search.ReplayMisses} missed" +
              $"{(outcome.Failures > 0 ? $", {outcome.Failures} failed ({outcome.FirstFailure})" : "")}");
        foreach (var g in search.AllReplayMisses.GroupBy(m => m).OrderByDescending(g => g.Count()).Take(verbose ? 12 : 3))
            write($"    {g.Count()}x {g.Key}");
        if (verbose)
            foreach (var stat in outcome.Root.Take(8))
                write($"    {stat.Label,-50} {BoardEvaluator.CuPm(stat.Mean, stat.StdErr)} card units ({stat.Visits} lines)");
        return search.ReplayMisses == 0;
    }

    private static string Option(string[] args, string name, string fallback)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    }
}
