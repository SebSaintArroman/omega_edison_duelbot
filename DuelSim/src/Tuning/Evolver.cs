using System.Diagnostics;
using System.Globalization;
using System.Text;
using BotTest.Web;
using DuelSim.Match;
using Newtonsoft.Json;

namespace DuelSim.Tuning;

public sealed record EvolveOptions(
    string BaseDeck = "Vayu",
    string Name = "vayu-test",
    int Generations = 0,          // 0 = until stopped (Ctrl+C)
    int Duels = 100,
    int Workers = 6,
    int Mutations = 8,
    double Strength = 0.3,
    double Accept = 0.55,
    int BaselineEvery = 5,
    int BaselineDuels = 100,
    int Seed = 1,
    bool KeepDuels = false,
    string Format = "MasterDuel");

/// <summary>
/// Hill-climbs a deck bot's tunable parameters. &lt;name&gt;-1 is the current champion, &lt;name&gt;-2 the challenger:
/// each generation the challenger is a mutation of the champion (a few parameters changed); they play N duels and the
/// challenger replaces the champion only if it wins at least the --accept share (so noise rarely wins). Every few
/// generations the champion also plays the untouched original deck, to catch overfitting to its own mirror.
/// Everything is written to tuning/ (variants, history.csv, evolve.log), so a stopped run carries on where it left off.
/// </summary>
public sealed class Evolver
{
    private readonly SimPaths _paths;
    private readonly EvolveOptions _o;
    private readonly TextWriter _console;
    private readonly List<Process> _running = new();

    public Evolver(SimPaths paths, EvolveOptions options, TextWriter console)
    {
        _paths = paths;
        _o = options;
        _console = console;
    }

    private string Champion => $"{_o.Name}-1";
    private string Challenger => $"{_o.Name}-2";
    private string HistoryFile => Path.Combine(_paths.Tuning, $"{_o.Name}-history.csv");
    private string LogFile => Path.Combine(_paths.Tuning, $"{_o.Name}-evolve.log");

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.Tuning);
        var (defaults, types) = Defaults();
        var champion = Variant.TryLoad(_paths, Champion);
        if (champion == null)
        {
            champion = new Variant { Base = _o.BaseDeck, Note = "generation 0: the original's defaults", Params = defaults };
            champion.Save(_paths, Champion);
            Log($"created {Champion} from {_o.BaseDeck}'s defaults ({defaults.Count} parameters)");
        }
        // Parameters added to the brain since the champion was made start at their defaults.
        foreach (var (name, value) in defaults)
            champion.Params.TryAdd(name, value);

        int generation = LastGeneration() + 1;
        var rng = new Random(_o.Seed * 7919 + generation);
        if (!File.Exists(HistoryFile))
            File.WriteAllText(HistoryFile, "generation,time,kind,duels,champion_wins,challenger_wins,draws,errors,deck_outs,avg_turns,accepted,changes" + Environment.NewLine);

        Log($"evolving {_o.Name} from {_o.BaseDeck}: {_o.Duels} duels/generation on {_o.Workers} workers, {_o.Mutations} mutations " +
            $"(strength {_o.Strength}), accept at {_o.Accept:P0}, baseline every {_o.BaselineEvery} generation(s); starting at generation {generation}");

        for (int done = 0; (_o.Generations == 0 || done < _o.Generations) && !ct.IsCancellationRequested; done++, generation++)
        {
            // Challenger: a mutation of the champion.
            var (mutated, changes) = BrainParams.Mutate(champion.Params, types, _o.Mutations, _o.Strength, rng);
            if (changes.Count == 0)
            {
                done--;
                generation--;
                continue;
            }
            var challenger = new Variant { Base = _o.BaseDeck, Note = $"generation {generation} challenger: {string.Join("; ", changes)}", Params = mutated };
            challenger.Save(_paths, Challenger);
            Log($"gen {generation}: challenger = {string.Join("; ", changes)}");

            var r = await PlayAsync(generation, "match", Champion, Challenger, _o.Duels, ct);
            if (ct.IsCancellationRequested)
                break;
            int decided = r.WinsA + r.WinsB;
            double share = decided == 0 ? 0 : (double)r.WinsB / decided;
            bool accepted = decided > 0 && share >= _o.Accept && r.Errors == 0;
            Log($"gen {generation}: {Champion} {r.WinsA} - {r.WinsB} {Challenger} (challenger {share:P0}; draws {r.Draws}, errors {r.Errors}, deck-outs {r.DeckOuts}) " +
                (accepted ? "-> ACCEPTED, the challenger is the new champion" : "-> rejected"));
            History(generation, "match", r, accepted, changes);
            if (accepted)
            {
                champion = new Variant
                {
                    Base = _o.BaseDeck,
                    Note = $"champion since generation {generation} (beat the previous one {r.WinsB}-{r.WinsA}); changes vs {_o.BaseDeck}: " +
                           string.Join("; ", BrainParams.Diff(defaults, mutated)),
                    Params = mutated
                };
                champion.Save(_paths, Champion);
            }

            // Overfitting check: the champion against the untouched original.
            if (_o.BaselineEvery > 0 && generation % _o.BaselineEvery == 0 && !ct.IsCancellationRequested)
            {
                var b = await PlayAsync(generation, "baseline", Champion, _o.BaseDeck, _o.BaselineDuels, ct);
                int bd = b.WinsA + b.WinsB;
                double vsOriginal = bd == 0 ? 0 : (double)b.WinsA / bd;
                string diff = string.Join("; ", BrainParams.Diff(defaults, champion.Params));
                Log($"gen {generation}: baseline {Champion} {b.WinsA} - {b.WinsB} {_o.BaseDeck} (champion {vsOriginal:P0} vs the original); " +
                    $"champion's changes vs the original: {(diff.Length > 0 ? diff : "none")}");
                History(generation, "baseline", b, false, new List<string>());
            }
        }
        Log("stopped");
        return 0;
    }

    /// <summary>The base deck's parameter defaults (a fresh bot) and their types.</summary>
    private (SortedDictionary<string, double> Values, Dictionary<string, Type> Types) Defaults()
    {
        var deck = MatchRunner.FindDeck(_o.Format, _o.BaseDeck);
        var brain = BrainParams.FindBrain(deck.Factory()) ?? throw new InvalidOperationException($"no brain found in the {_o.BaseDeck} bot");
        return (BrainParams.Read(brain), BrainParams.Types(brain));
    }

    /// <summary>Plays A vs B over the workers (separate DuelSim processes, each its own share of the duels), combined.</summary>
    private async Task<MatchRunner.RunResult> PlayAsync(int generation, string kind, string a, string b, int duels, CancellationToken ct)
    {
        var folder = Path.Combine(_paths.Output, "evolve", _o.Name, $"gen-{generation:0000}-{kind}");
        Directory.CreateDirectory(folder);
        int workers = Math.Max(1, Math.Min(_o.Workers, duels / 2));
        // Even shares, so each worker alternates who goes first fairly.
        var shares = Enumerable.Range(0, workers).Select(i => duels / 2 / workers * 2).ToArray();
        for (int left = duels - shares.Sum(), i = 0; left > 0; left -= 2, i = (i + 1) % workers)
            shares[i] += Math.Min(2, left);

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("can't find the DuelSim executable");
        var started = DateTime.Now;
        var tasks = new List<Task<MatchRunner.RunResult?>>();
        for (int i = 0; i < workers; i++)
        {
            if (shares[i] == 0)
                continue;
            int seed = _o.Seed * 1_000_000 + generation * 1000 + (kind == "baseline" ? 500 : 0) + i * 50;
            var result = Path.Combine(folder, $"worker-{i}.json");
            var args = $"run \"{a}\" \"{b}\" --duels {shares[i]} --seed {seed} --format {_o.Format} --no-bot-logs " +
                       $"--timeout-min 10 --out \"{Path.Combine(folder, $"worker-{i}")}\" --result \"{result}\"";
            tasks.Add(RunWorkerAsync(exe, args, Path.Combine(folder, $"worker-{i}.log"), result, ct));
        }
        _console.Write($"  gen {generation} {kind}: {a} vs {b}, {duels} duels on {tasks.Count} workers...");
        var results = (await Task.WhenAll(tasks)).Where(x => x != null).Select(x => x!).ToList();
        _console.WriteLine($" done in {(DateTime.Now - started).TotalMinutes:0.0} min");

        if (!_o.KeepDuels)
            foreach (var dir in Directory.GetDirectories(folder))
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        return new MatchRunner.RunResult(a, b, results.Sum(x => x.Duels), results.Sum(x => x.WinsA), results.Sum(x => x.WinsB),
            results.Sum(x => x.Draws), results.Sum(x => x.Errors) + (tasks.Count - results.Count), results.Sum(x => x.DeckOuts),
            results.Count > 0 ? results.Sum(x => x.AverageTurns * x.Duels) / Math.Max(1, results.Sum(x => x.Duels)) : 0);
    }

    private async Task<MatchRunner.RunResult?> RunWorkerAsync(string exe, string args, string logFile, string resultFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _paths.Root
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start a worker");
        lock (_running) _running.Add(process);
        await using var log = new StreamWriter(logFile, false, Encoding.UTF8);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        finally
        {
            lock (_running) _running.Remove(process);
        }
        await log.WriteAsync((await stdout).Replace("\r", "\n"));
        await log.WriteAsync(await stderr);
        return File.Exists(resultFile) ? JsonConvert.DeserializeObject<MatchRunner.RunResult>(await File.ReadAllTextAsync(resultFile)) : null;
    }

    private int LastGeneration()
    {
        if (!File.Exists(HistoryFile))
            return 0;
        return File.ReadLines(HistoryFile).Skip(1)
            .Select(line => int.TryParse(line.Split(',')[0], out int g) ? g : 0)
            .DefaultIfEmpty(0).Max();
    }

    private void History(int generation, string kind, MatchRunner.RunResult r, bool accepted, List<string> changes) =>
        File.AppendAllText(HistoryFile, string.Join(",", generation, DateTime.Now.ToString("s", CultureInfo.InvariantCulture), kind, r.Duels,
            r.WinsA, r.WinsB, r.Draws, r.Errors, r.DeckOuts, r.AverageTurns.ToString("0.0", CultureInfo.InvariantCulture),
            accepted ? "yes" : "no", $"\"{string.Join("; ", changes)}\"") + Environment.NewLine);

    private void Log(string line)
    {
        var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
        _console.WriteLine(stamped);
        File.AppendAllText(LogFile, stamped + Environment.NewLine);
    }
}
