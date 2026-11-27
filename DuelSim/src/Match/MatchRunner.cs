using System.Reflection;
using System.Text;
using BotTest.Web;
using DuelSim.Bridge;
using DuelSim.Data;
using DuelSim.Engine;
using OmegaBotWrapper;
using OmegaBotWrapper.Logging;

namespace DuelSim.Match;

internal sealed class UnknownDeckException(string message) : ArgumentException(message);

public sealed record MatchOptions(
    string DeckA,
    string DeckB,
    int Duels,
    int Seed,
    string Format = "MasterDuel",
    bool BotLogs = true,
    TimeSpan? DuelTimeout = null,
    string? OutputRoot = null,
    string? ResultFile = null,
    /// <summary>Play search thinking time per decision (ms) for deck A and deck B; null = the brain default (on), 0 = off.</summary>
    int? SearchMsA = null,
    int? SearchMsB = null,
    /// <summary>Deck B answers this much later (ms), like a human thinking: deck A's dead time (--think-b).</summary>
    int ThinkMsB = 0,
    /// <summary>Deck A's / deck B's own tuning values (--tuning-a / --tuning-b: a file in the bot-tuning.txt format listing only the numbers
    /// that side changes); null = the tuning file's for that side.</summary>
    BotTest.Game.AI.Brains.TuningProfile? TuningA = null,
    BotTest.Game.AI.Brains.TuningProfile? TuningB = null);

public sealed record DuelOutcome(int Number, string FirstDeck, string SecondDeck, DuelResult Result, string Folder, int Warnings);

/// <summary>Plays a series of duels between two of BotTest's deck bots and writes the results under out/.</summary>
public sealed class MatchRunner
{
    private readonly SimPaths _paths;
    private readonly CardStore _cards;
    private readonly ScriptStore _scripts;
    private readonly OmegaEmulator _emulator;
    private readonly TextWriter _console;

    public MatchRunner(SimPaths paths, CardStore cards, ScriptStore scripts, TextWriter console)
    {
        _paths = paths;
        _cards = cards;
        _scripts = scripts;
        _console = console;
        _emulator = new OmegaEmulator(cards);
        _emulator.Install();
    }

    /// <summary>A deck by id or name, or a tuned variant (tuning/<name>.json): its base deck playing the variant's parameters.</summary>
    public DeckDefinition ResolveDeck(string format, string deck)
    {
        if (Tuning.Variant.TryLoad(_paths, deck) is not { } variant)
            return FindDeck(format, deck);
        var baseDeck = FindDeck(format, variant.Base);
        return baseDeck with
        {
            DeckId = deck,
            DeckName = deck,
            Factory = () =>
            {
                var bot = baseDeck.Factory();
                var brain = Tuning.BrainParams.FindBrain(bot) ?? throw new InvalidOperationException($"{deck}: couldn't find the brain inside the {variant.Base} bot");
                Tuning.BrainParams.Apply(brain, variant.Params, msg => _console.WriteLine($"{deck}: {msg}"));
                return bot;
            }
        };
    }

    public static DeckDefinition FindDeck(string format, string deck) =>
        DeckCatalog.Definitions.FirstOrDefault(d => d.FormatId.Equals(format, StringComparison.OrdinalIgnoreCase) &&
                                                    (d.DeckId.Equals(deck, StringComparison.OrdinalIgnoreCase) ||
                                                     d.DeckName.Equals(deck, StringComparison.OrdinalIgnoreCase) ||
                                                     // A DuelSim bot's name has the deck name without spaces ("MiracleVayu"): replay-check reads it back.
                                                     d.DeckName.Replace(" ", "").Equals(deck, StringComparison.OrdinalIgnoreCase)))
        ?? throw new UnknownDeckException($"No {format} deck called '{deck}'. Try: {string.Join(", ", DeckCatalog.Definitions.Where(d => d.FormatId.Equals(format, StringComparison.OrdinalIgnoreCase)).Select(d => d.DeckId))}");

    public async Task<List<DuelOutcome>> RunAsync(MatchOptions options, CancellationToken ct)
    {
        var deckA = ResolveDeck(options.Format, options.DeckA);
        var deckB = ResolveDeck(options.Format, options.DeckB);
        var listA = DeckCode.Decode(deckA.DeckId, deckA.Factory().DeckHash, _cards, _scripts, _console.WriteLine);
        var listB = DeckCode.Decode(deckB.DeckId, deckB.Factory().DeckHash, _cards, _scripts, _console.WriteLine);
        CheckDeck(listA);
        CheckDeck(listB);

        var runFolder = Path.Combine(options.OutputRoot ?? _paths.Output, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{deckA.DeckId}-vs-{deckB.DeckId}");
        Directory.CreateDirectory(runFolder);
        _console.WriteLine($"Run folder: {runFolder}");
        _console.WriteLine($"{deckA.DeckId}: {listA.Main.Count} main / {listA.Extra.Count} extra; {deckB.DeckId}: {listB.Main.Count} main / {listB.Extra.Count} extra");

        var outcomes = new List<DuelOutcome>();
        for (int n = 1; n <= options.Duels && !ct.IsCancellationRequested; n++)
        {
            // Alternate who goes first.
            bool aFirst = n % 2 == 1;
            var first = aFirst ? (Def: deckA, List: listA) : (Def: deckB, List: listB);
            var second = aFirst ? (Def: deckB, List: listB) : (Def: deckA, List: listA);
            var outcome = await RunDuelAsync(n, first, second, options, runFolder, ct, outcomes);
            outcomes.Add(outcome);
            var r = outcome.Result;
            string winner = r.IsDraw ? "draw" : r.Winner == 0 ? $"{first.Def.DeckId} (went first)" : $"{second.Def.DeckId} (went second)";
            _console.WriteLine($"Duel {n,3}: {winner,-24} turn {r.Turns,3}  LP {r.LifePoints[0],5}/{r.LifePoints[1],-5} {r.Reason}" +
                               $"{(outcome.Warnings > 0 ? $"  [{outcome.Warnings} warnings]" : "")}{(r.Error != null ? "  ERROR" : "")}  ({r.Duration.TotalSeconds:0}s)");
        }

        WriteSummary(runFolder, options, outcomes);
        // Every search override of the run, listed for reading (overrides.md).
        SearchRecord.WriteOverrides(runFolder, _console);
        if (options.ResultFile != null)
            WriteResult(options.ResultFile, options, outcomes);
        return outcomes;
    }

    private void CheckDeck(DeckList deck)
    {
        var missing = deck.Main.Concat(deck.Extra).Where(id => _cards.Get(id) == null).Distinct().ToList();
        if (missing.Count > 0)
            _console.WriteLine($"Warning: {deck.Name} has cards missing from the card database: {string.Join(", ", missing)}");
        var noScript = deck.Main.Concat(deck.Extra).Distinct()
            .Where(id => _cards.Get(id) is { } c && (c.Type & 0x10) == 0 && _scripts.Read($"c{id}.lua") == null).ToList();
        if (noScript.Count > 0)
            _console.WriteLine($"Warning: {deck.Name} has cards with no script (they will do nothing): {string.Join(", ", noScript.Select(id => _cards.Get(id)?.Name ?? id.ToString()))}");
    }

    private async Task<DuelOutcome> RunDuelAsync(int number, (DeckDefinition Def, DeckList List) first, (DeckDefinition Def, DeckList List) second,
        MatchOptions options, string runFolder, CancellationToken ct, List<DuelOutcome> done)
    {
        var folder = Path.Combine(runFolder, $"duel-{number:000}");
        Directory.CreateDirectory(folder);
        int warnings = 0;
        using var transcript = new StreamWriter(Path.Combine(folder, "transcript.txt"), false, Encoding.UTF8) { AutoFlush = true };
        using var botConsole = new StreamWriter(Path.Combine(folder, "bot-console.txt"), false, Encoding.UTF8) { AutoFlush = false };
        transcript.WriteLine($"Duel {number}: player 0 = {first.Def.DeckId} (goes first), player 1 = {second.Def.DeckId}");

        using var host = new DuelHost(_cards, _scripts, first.List, second.List, options.Seed + number);
        var sync = new object();
        host.Event += line => { lock (sync) transcript.WriteLine(line); };
        host.Warning += line =>
        {
            lock (sync)
            {
                warnings++;
                transcript.WriteLine($"  !! {line}");
            }
        };

        // Progress: a live console line, and progress.txt in the run folder (open it to check on a long run).
        var started = DateTime.Now;
        var progressFile = Path.Combine(runFolder, "progress.txt");
        void Progress(string status)
        {
            string line = $"duel {number}/{options.Duels} | {status} | {(DateTime.Now - started).TotalSeconds:0}s";
            _console.Write($"\r{line,-100}");
            var finished = string.Join(Environment.NewLine, done.Select(o =>
                $"  duel {o.Number:000}: {(o.Result.IsDraw ? "draw" : o.Result.Winner == 0 ? o.FirstDeck + " (1st)" : o.SecondDeck + " (2nd)")} - {o.Result.Reason} - turn {o.Result.Turns}"));
            try
            {
                File.WriteAllText(progressFile,
                    $"Updated {DateTime.Now:HH:mm:ss}{Environment.NewLine}Now: {line}{Environment.NewLine}Current duel folder: {folder}{Environment.NewLine}" +
                    $"Finished ({done.Count}):{Environment.NewLine}{finished}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
        // The position dataset: the engine's real position at each turn start, and each seat's own scores (PositionSink), joined at the end.
        var dataset = new PositionDataset();
        host.TurnStarted += (turn, player, lp0, lp1) =>
        {
            dataset.Truth(host, turn, player);
            Progress($"turn {turn} ({(player == 0 ? first.Def.DeckId + " P0" : second.Def.DeckId + " P1")}) | LP {lp0} / {lp1}");
        };
        Progress("starting");

        DuelLogOptions.Enabled = options.BotLogs;
        DuelLogOptions.Directory = folder;
        DuelLogOptions.IdleCheckInterval = TimeSpan.FromSeconds(1);
        // No sleep between polls: the emulator holds each poll until the bot has a decision (long-poll).
        DuelBotWrapper.HeartbeatDelayMs = 0;
        var previousOut = Console.Out;
        Console.SetOut(TextWriter.Synchronized(botConsole));

        _emulator.Attach(host);
        using var duelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        duelCts.CancelAfter(options.DuelTimeout ?? TimeSpan.FromMinutes(20));
        var bots = new[] { CreateBot(first.Def, 0, second.Def), CreateBot(second.Def, 1, first.Def) };
        // Play search per deck (A goes first in odd duels).
        bool aFirst = number % 2 == 1;
        for (int seat = 0; seat < 2; seat++)
        {
            if (Tuning.BrainParams.FindBrain(bots[seat]) is BotTest.Game.AI.Brains.DeckBotBrain searcher)
            {
                searcher.SearchBudgetMs = ((seat == 0) == aFirst ? options.SearchMsA : options.SearchMsB) ?? searcher.SearchBudgetMs;
                searcher.ReplayAudit = true; // each turn replayed from its start in the engine, hits and misses logged (TurnReplay)
            }
            DuelSim.Bridge.OmegaEmulator.ThinkMs[seat] = (seat == 0) == aFirst ? 0 : options.ThinkMsB;
        }
        // Each seat's brain version (DeckBotBrain.BrainVersion; 0 = unversioned), recorded with the result.
        var versions = bots.Select(bot => Tuning.BrainParams.FindBrain(bot) is BotTest.Game.AI.Brains.DeckBotBrain b ? b.BrainVersion : 0).ToArray();
        // Play search verdicts (kept or overrode the brain, and the rules behind its choice), per seat.
        var searchEvents = new[] { new List<string>(), new List<string>() };
        BotTest.Game.AI.Brains.SearchEventSink.OnSearchEvent = (bot, evt) =>
        {
            int seat = Array.IndexOf(bots, bot);
            if (seat >= 0)
                lock (searchEvents[seat])
                    searchEvents[seat].Add(evt);
        };
        BotTest.Game.AI.Brains.PositionSink.OnPosition = (bot, turn, row) =>
        {
            int seat = Array.IndexOf(bots, bot);
            if (seat >= 0)
                dataset.View(seat, turn, row);
        };
        var botTasks = bots.Select((bot, seat) => Task.Run(async () =>
        {
            SimContext.Seat.Value = seat;
            // This side's own tuning values: its brain, search twins and search threads all run from this flow.
            BotTest.Game.AI.Brains.TuningProfile.Use((seat == 0) == aFirst ? options.TuningA : options.TuningB);
            try
            {
                if (!await bot.Connect(ct: duelCts.Token))
                {
                    lock (sync)
                        transcript.WriteLine($"  !! player {seat}: bot failed to connect");
                }
                else
                    await bot.Start(duelCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (sync)
                    transcript.WriteLine($"  !! player {seat} bot crashed: {ex}");
            }
        })).ToArray();

        DuelResult result;
        try
        {
            result = await host.RunAsync(duelCts.Token);
        }
        finally
        {
            // Give the bots a moment to see the end of the duel (their logs record the result), then stop them.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            duelCts.Cancel();
            await Task.WhenAny(Task.WhenAll(botTasks), Task.Delay(TimeSpan.FromSeconds(10)));
            _emulator.Detach();
            Console.Out.Flush();
            Console.SetOut(previousOut);
            _console.Write("\r" + new string(' ', 100) + "\r");
        }

        if (result.Error != null)
            transcript.WriteLine($"SIMULATOR ERROR: {result.Error}");
        transcript.WriteLine($"Result: {(result.IsDraw ? "draw" : $"player {result.Winner}")} - {result.Reason} - turn {result.Turns} - LP {result.LifePoints[0]}/{result.LifePoints[1]}");
        // For the analyze command: who sat where and who won (player 0 went first).
        File.WriteAllText(Path.Combine(folder, "duel-result.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            number, first = first.Def.DeckId, second = second.Def.DeckId, winner = result.Winner, reason = result.Reason,
            turns = result.Turns, error = result.Error, firstVersion = versions[0], secondVersion = versions[1],
        }));
        BotTest.Game.AI.Brains.SearchEventSink.OnSearchEvent = null;
        BotTest.Game.AI.Brains.PositionSink.OnPosition = null;
        var tunings = new[] { aFirst ? options.TuningA?.Name : options.TuningB?.Name, aFirst ? options.TuningB?.Name : options.TuningA?.Name };
        try
        {
            dataset.Write(_paths, _cards, runFolder, number, new[] { first.Def.DeckId, second.Def.DeckId }, versions, tunings, result);
        }
        catch (Exception ex)
        {
            transcript.WriteLine($"  !! position dataset not written: {ex.Message}");
        }
        SearchRecord.AppendDuel(runFolder, number, first.Def.DeckId, second.Def.DeckId, result.Error != null ? -1 : result.Winner, searchEvents, versions);
        return new DuelOutcome(number, first.Def.DeckId, second.Def.DeckId, result, folder, warnings);
    }

    private static DuelBotWrapper CreateBot(DeckDefinition definition, int seat, DeckDefinition opponent)
    {
        var bot = definition.Factory();
        // Flag bot-vs-bot duels in the bot logs (file name and header), and give each seat its own name so mirror
        // matches don't share a file: e.g. "2026-11-07_081728_BOTvBOT_Vayu-P0_vs_Vayu.log".
        typeof(DuelBotWrapper).GetProperty(nameof(DuelBotWrapper.DeckName))!
            .SetValue(bot, $"BOTvBOT_{bot.DeckName.Replace(" ", "")}-P{seat}_vs_{opponent.DeckName.Replace(" ", "")}");
        return bot;
    }

    /// <summary>Machine-readable result of the run (used by evolve to combine parallel workers).</summary>
    public sealed record RunResult(string DeckA, string DeckB, int Duels, int WinsA, int WinsB, int Draws, int Errors, int DeckOuts, double AverageTurns);

    private static void WriteResult(string file, MatchOptions options, List<DuelOutcome> outcomes)
    {
        int Wins(string deck) => outcomes.Count(o => !o.Result.IsDraw &&
                                                     (o.Result.Winner == 0 ? o.FirstDeck : o.SecondDeck).Equals(deck, StringComparison.OrdinalIgnoreCase));
        var result = new RunResult(options.DeckA, options.DeckB, outcomes.Count, Wins(options.DeckA), Wins(options.DeckB),
            outcomes.Count(o => o.Result.IsDraw), outcomes.Count(o => o.Result.Error != null),
            outcomes.Count(o => o.Result.Reason.Contains("deck out", StringComparison.OrdinalIgnoreCase)),
            outcomes.Count > 0 ? outcomes.Average(o => o.Result.Turns) : 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
    }

    private void WriteSummary(string runFolder, MatchOptions options, List<DuelOutcome> outcomes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{options.DeckA} vs {options.DeckB} ({options.Format}), {outcomes.Count} duels, seed {options.Seed}");
        foreach (var deck in new[] { options.DeckA, options.DeckB }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int wins = outcomes.Count(o => !o.Result.IsDraw && (o.Result.Winner == 0 ? o.FirstDeck : o.SecondDeck).Equals(deck, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"  {deck}: {wins} wins");
        }
        int firstWins = outcomes.Count(o => o.Result.Winner == 0);
        int secondWins = outcomes.Count(o => o.Result.Winner == 1);
        sb.AppendLine($"  going first won {firstWins}, going second won {secondWins}, draws/aborted {outcomes.Count(o => o.Result.IsDraw)}");
        sb.AppendLine($"  errors: {outcomes.Count(o => o.Result.Error != null)}, duels with warnings: {outcomes.Count(o => o.Warnings > 0)}");
        if (outcomes.Count > 0)
            sb.AppendLine($"  average length: {outcomes.Average(o => o.Result.Turns):0.0} turns, {outcomes.Average(o => o.Result.Duration.TotalSeconds):0}s");
        sb.AppendLine();
        foreach (var o in outcomes)
            sb.AppendLine($"duel {o.Number:000}: {o.FirstDeck} (1st) vs {o.SecondDeck} (2nd) -> {(o.Result.IsDraw ? "draw" : o.Result.Winner == 0 ? o.FirstDeck : o.SecondDeck)} | {o.Result.Reason} | turn {o.Result.Turns} | warnings {o.Warnings}{(o.Result.Error != null ? " | ERROR" : "")}");
        File.WriteAllText(Path.Combine(runFolder, "summary.txt"), sb.ToString());
        _console.WriteLine();
        _console.Write(sb.ToString());
    }
}
