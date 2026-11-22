using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Logging;

namespace BotTest.Web;

public sealed class BotControlService
{
    private const int MaxLogEntries = 16;

    private readonly object _sync = new();
    private readonly IReadOnlyList<FormatOption> _formatOptions;

    private string _selectedFormat;
    private string _selectedDeckId;
    private string _selectedDeckName;
    private bool _isBusy;
    private string _status = "Ready";
    private string _statusDetail = "Choose a format and a deck to start the duel.";
    private DateTimeOffset _lastUpdated = DateTimeOffset.Now;
    private readonly List<LogEntry> _logs = new();
    private Task? _runningTask;
    private CancellationTokenSource? _cts;

    /// <summary>The deck option that picks one of the real decks at random when the duel starts.</summary>
    public const string RandomDeckId = "random";
    private const string RandomDeckName = "Random deck";

    // The duel running now (for comments: its log and decision number), and the last duel's log once it's over.
    private DuelBotWrapper? _currentBot;
    private string? _lastLogPath;
    private int _lastDecision;
    private DeckSelection? _lastPlayed;
    private readonly Random _random = new();

    private static string CommentsPath => Path.Combine(DuelLogOptions.Directory, "comments.jsonl");

    public BotControlService()
    {
        RegisterCardNamesForLogs();
        // Master Duel only (user, 2026-11-11), with "Random deck" first.
        _formatOptions = DeckCatalog.GetFormatOptions()
            .Where(format => format.Id == "MasterDuel")
            .Select(format => format with { Decks = format.Decks.Prepend(new DeckOptionDto(RandomDeckId, RandomDeckName)).ToArray() })
            .ToArray();

        var defaultFormat = _formatOptions.First();
        var defaultDeck = defaultFormat.Decks.First();

        _selectedFormat = defaultFormat.Id;
        _selectedDeckId = defaultDeck.Id;
        _selectedDeckName = defaultDeck.Name;

        AddLog("Panel initialized. Auto-start is on: the bot fills the next Omega bot slot that waits for a deck.", "system");
        _ = Task.Run(AutoLoopAsync);
    }

    // ---------------------------------------------------------------- auto-start (user, 2026-11-12: "can it just always be on?")

    /// <summary>Whether the bot fills Omega's waiting bot slots by itself (on by default; the page's button pauses it).</summary>
    private volatile bool _autoEnabled = true;

    /// <summary>How often the auto-start looks for a waiting bot slot (ms).</summary>
    private const int AutoPollMs = 3000;

    public CommandResult SetAuto(bool enabled)
    {
        _autoEnabled = enabled;
        AddLog(enabled ? "Auto-start resumed." : "Auto-start paused: the bot won't take new bot slots (a duel underway plays on).", "system");
        lock (_sync)
            if (!_isBusy)
            {
                _status = enabled ? "Waiting" : "Paused";
                _statusDetail = enabled ? string.Empty : "Auto-start is paused. Press Resume to let the bot take the next bot slot.";
                _lastUpdated = DateTimeOffset.Now;
            }
        return CommandResult.Ok(enabled ? "Auto-start on." : "Auto-start paused.");
    }

    /// <summary>
    /// Every few seconds: when nothing runs and auto-start is on, try to fill a waiting bot slot with the selected deck
    /// (Random draws a deck each duel). A duel over, the loop picks up the next slot by itself.
    /// </summary>
    private async Task AutoLoopAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(AutoPollMs);
                if (!_autoEnabled)
                    continue;
                Task run;
                lock (_sync)
                {
                    if (_isBusy)
                        continue;
                    var selection = Resolve(new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName));
                    _isBusy = true;
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    run = _runningTask = Task.Run(() => ExecuteDuelAsync(selection, requestRematch: false, token, auto: true));
                }
                await run;
            }
            catch
            {
                // The loop must never stop: a failed attempt waits for the next poll.
            }
        }
    }

    public OptionsResponse GetOptions() => new(_formatOptions, _selectedFormat, _selectedDeckId);

    public BotStateResponse GetState()
    {
        lock (_sync)
        {
            return new BotStateResponse(
                _selectedFormat,
                _selectedDeckId,
                _selectedDeckName,
                _isBusy,
                _status,
                _statusDetail,
                _lastUpdated,
                _logs.ToArray(),
                _autoEnabled);
        }
    }

    public CommandResult UpdateSelection(string format, string deckId)
    {
        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Wait for the current match to finish before changing your selection.");
            }

            var formatOption = _formatOptions.FirstOrDefault(option => option.Id.Equals(format, StringComparison.OrdinalIgnoreCase));
            if (formatOption is null)
            {
                return CommandResult.Fail("Invalid format.");
            }

            var deckOption = formatOption.Decks.FirstOrDefault(option => option.Id.Equals(deckId, StringComparison.OrdinalIgnoreCase));
            if (deckOption is null)
            {
                return CommandResult.Fail("Invalid deck for the selected format.");
            }

            _selectedFormat = formatOption.Id;
            _selectedDeckId = deckOption.Id;
            _selectedDeckName = deckOption.Name;
            _status = "Ready";
            _statusDetail = $"Selection ready: {deckOption.Name} in {formatOption.Name}.";
            _lastUpdated = DateTimeOffset.Now;

            AddLog($"Selected deck: {deckOption.Name} ({formatOption.Name}).", "system");

            return CommandResult.Ok("Updated selection.");
        }
    }

    public CommandResult StartDuel()
    {
        DeckSelection selection;

        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("A duel is already underway.");
            }

            selection = Resolve(new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName));
            _isBusy = true;
            _cts = new CancellationTokenSource();
            _status = "Connecting";
            _statusDetail = $"Connecting the bot to the deck {selection.DeckName}...";
            _lastUpdated = DateTimeOffset.Now;
            AddLog($"Starting the duel with {selection.DeckName}.", "system");
            var token = _cts.Token;
            _runningTask = Task.Run(() => ExecuteDuelAsync(selection, requestRematch: false, token));
        }

        return CommandResult.Ok("Duel started.");
    }

    public CommandResult RequestRematch()
    {
        DeckSelection selection;

        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Wait for the current flow to finish before requesting a rematch.");
            }

            // A rematch keeps the deck just played, unless "Random deck" is the selection: then each rematch draws again too (user,
            // 2026-11-13: "it seems to pick machina control a lot" - five Machina Control duels in a row were rematches of one draw).
            selection = _selectedDeckId == RandomDeckId
                ? Resolve(new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName))
                : _lastPlayed ?? Resolve(new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName));
            _isBusy = true;
            _cts = new CancellationTokenSource();
            _status = "Rematch";
            _statusDetail = "Requesting rematch and preparing a new connection...";
            _lastUpdated = DateTimeOffset.Now;
            AddLog("Requesting rematch.", "system");
            var rematchToken = _cts.Token;
            _runningTask = Task.Run(() => ExecuteDuelAsync(selection, requestRematch: true, rematchToken));
        }

        return CommandResult.Ok("Rematch requested.");
    }

    public CommandResult ResetSelection()
    {
        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Cannot reset while a duel is in progress.");
            }

            _status = "Ready";
            _statusDetail = "Selection kept. You can switch decks or start a new duel.";
            _lastUpdated = DateTimeOffset.Now;
            AddLog("Panel ready for a new duel.", "system");

            return CommandResult.Ok("Panel reset.");
        }
    }

    public CommandResult ClearRecentEvents()
    {
        lock (_sync)
        {
            _logs.Clear();
            TerminalCapture.Clear();
            _lastUpdated = DateTimeOffset.Now;
            return CommandResult.Ok("Recent events cleared.");
        }
    }

    // "Random deck" becomes one of the real decks (chosen when the duel starts).
    private DeckSelection Resolve(DeckSelection selection)
    {
        if (selection.DeckId != RandomDeckId)
            return selection;
        var decks = _formatOptions.First(f => f.Id == selection.FormatId).Decks.Where(d => d.Id != RandomDeckId).ToList();
        var deck = decks[_random.Next(decks.Count)];
        Console.WriteLine($"[Random deck] drew {deck.Name}"); // engine chatter on the page (user, 2026-11-17)
        return new DeckSelection(selection.FormatId, deck.Id, deck.Name);
    }

    /// <summary>
    /// Saves a bug report / comment with where it points: the current duel's log (or the last one), the decision number
    /// written last ("#NNN" in the .log) and the .log line count, so it can be found later.
    /// </summary>
    public CommandResult AddComment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CommandResult.Fail("Write a comment first.");
        string? basePath;
        int decision;
        string deck;
        lock (_sync)
        {
            basePath = _currentBot?.DuelLog?.CurrentLogPath ?? _lastLogPath;
            decision = _currentBot?.DuelLog?.DecisionCount ?? _lastDecision;
            deck = _lastPlayed?.DeckName ?? _selectedDeckName;
        }
        int? line = null;
        string? logFile = basePath == null ? null : basePath + ".log";
        try
        {
            if (logFile != null && File.Exists(logFile))
            {
                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                int count = 0;
                while (reader.ReadLine() != null)
                    count++;
                line = count;
            }
        }
        catch (IOException)
        {
        }
        var entry = new CommentEntry(DateTimeOffset.Now, text.Trim(), deck,
            logFile == null ? null : Path.GetFileName(logFile), decision > 0 ? decision : null, line);
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(DuelLogOptions.Directory);
            File.AppendAllText(CommentsPath, System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        AddLog($"Comment saved{(entry.Decision is int d ? $" at decision #{d:000}" : "")}{(entry.LogFile != null ? $" in {entry.LogFile}" : "")}.", "system");
        return CommandResult.Ok("Comment saved.");
    }

    /// <summary>The saved comments, newest first.</summary>
    public IReadOnlyList<CommentEntry> GetComments(int max = 50)
    {
        lock (_sync)
        {
            if (!File.Exists(CommentsPath))
                return Array.Empty<CommentEntry>();
            return File.ReadAllLines(CommentsPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => { try { return System.Text.Json.JsonSerializer.Deserialize<CommentEntry>(l); } catch { return null; } })
                .Where(c => c != null).Select(c => c!)
                .Reverse().Take(max).ToList();
        }
    }

    /// <summary>
    /// Gives the duel logger readable card names: deck *CardId constants first, then the card-list enums.
    /// </summary>
    private static void RegisterCardNamesForLogs()
    {
        var assembly = typeof(BotControlService).Assembly;
        CardNames.RegisterConstantsFromAssembly(assembly, "CardId");
        foreach (var enumType in assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == "BotTest.Game.AI.Enums"))
            CardNames.RegisterEnum(enumType);
    }

    /// <param name="auto">Started by the auto-start loop: no free bot slot is the normal wait, not an error (nothing logged).</param>
    private async Task ExecuteDuelAsync(DeckSelection selection, bool requestRematch, CancellationToken ct = default, bool auto = false)
    {
        DuelBotWrapper? bot = null;

        void OnMessageLogged(string message)
        {
            AddLog(message, "engine");
            bot?.LogNote(message);
        }

        // Each decision with its reasons, printed to the terminal (and the panel log).
        void OnDecisionNarrated(string summary)
        {
            var original = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(summary);
            Console.ForegroundColor = original;
            AddLog(summary, "decision");
        }

        ConsoleUtils.MessageLogged += OnMessageLogged;
        DuelLogger.DecisionNarrated += OnDecisionNarrated;

        try
        {
            if (requestRematch)
            {
                UpdateStatus("Rematch", "Sending the rematch request to Omega...");
                await DuelBotWrapper.RematchDuel();
                AddLog("Rematch request sent.", "system");
                await Task.Delay(500, ct);
            }

            bot = DeckCatalog.CreateBot(selection.FormatId, selection.DeckId);
            lock (_sync)
            {
                _currentBot = bot;
                _lastPlayed = selection;
            }

            if (!auto)
                UpdateStatus("Connecting", $"Trying to connect the bot using {selection.DeckName}...");
            var connected = await bot.Connect(ct: ct);

            if (!connected && bot.LastConnectProblem is string refused)
            {
                // Not "no slot yet": Omega refused this deck. Shown every time, so a deck that never plays is visible.
                AddLog($"{refused}. Check its deck code in DeckManager.", "error");
                Console.WriteLine($"[Connect] {refused}");
                if (auto)
                    return; // the next poll draws again
            }
            if (!connected && auto)
            {
                    UpdateStatus("Waiting", string.Empty);
                return;
            }
            if (auto)
                AddLog($"Bot slot found: starting the duel with {selection.DeckName}.", "system");
            if (!connected)
            {
                UpdateStatus("Connection failed", "No bot slot is available or Omega did not respond as expected.");
                AddLog("Failed to connect to the bot.", "error");
                return;
            }

            UpdateStatus("Dueling", $"Bot is dueling with {selection.DeckName}. Follow the match in Omega.");
            AddLog("Bot connected successfully.", "system");
            AddLog($"Duel logs folder: {DuelLogOptions.Directory}", "system");

            await bot.Start(ct);

            UpdateStatus("Finished", _autoEnabled ? "The duel is over. Auto-start takes the next bot slot by itself." : "The duel is over. Auto-start is paused.");
            AddLog("Duel finished.", "system");
            if (bot.DuelLog?.CurrentLogPath is string logPath)
                AddLog($"Duel log saved: {logPath}.log", "system");
        }
        catch (Exception ex)
        {
            UpdateStatus("Error", $"An error occurred during execution: {ex.Message}");
            AddLog($"Error: {ex.Message}", "error");
        }
        finally
        {
            ConsoleUtils.MessageLogged -= OnMessageLogged;
            DuelLogger.DecisionNarrated -= OnDecisionNarrated;

            lock (_sync)
            {
                if (bot?.DuelLog is { } duelLog)
                {
                    _lastLogPath = duelLog.CurrentLogPath ?? _lastLogPath;
                    _lastDecision = duelLog.DecisionCount;
                }
                _currentBot = null;
                _isBusy = false;
                _runningTask = null;
                _cts?.Dispose();
                _cts = null;
                _lastUpdated = DateTimeOffset.Now;
            }
        }
    }

    public CommandResult StopDuel()
    {
        lock (_sync)
        {
            if (!_isBusy || _cts == null)
                return CommandResult.Fail("No duel is currently running.");
            _cts.Cancel();
            AddLog("Stop requested.", "system");
            return CommandResult.Ok("Stop signal sent to the bot.");
        }
    }

    private void UpdateStatus(string status, string detail)
    {
        lock (_sync)
        {
            _status = status;
            _statusDetail = detail;
            _lastUpdated = DateTimeOffset.Now;
        }
    }

    private void AddLog(string message, string kind = "info")
    {
        lock (_sync)
        {
            _logs.Insert(0, new LogEntry(DateTimeOffset.Now, message, kind));
            if (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveRange(MaxLogEntries, _logs.Count - MaxLogEntries);
            }
        }
    }
}

public sealed record SelectionRequest(string Format, string DeckId);

public sealed record CommentRequest(string Text);

/// <summary>A saved comment: what was written, when, and where in which duel log it points (decision "#NNN", .log line).</summary>
public sealed record CommentEntry(DateTimeOffset Timestamp, string Text, string Deck, string? LogFile, int? Decision, int? Line);

public sealed record CommandResult(bool Success, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);

    public static CommandResult Fail(string message) => new(false, message);
}

public sealed record OptionsResponse(
    IReadOnlyList<FormatOption> Formats,
    string SelectedFormat,
    string SelectedDeckId);

public sealed record BotStateResponse(
    string SelectedFormat,
    string SelectedDeckId,
    string SelectedDeckName,
    bool IsBusy,
    string Status,
    string StatusDetail,
    DateTimeOffset LastUpdated,
    IReadOnlyList<LogEntry> Logs,
    bool AutoEnabled = true);

public sealed record FormatOption(string Id, string Name, IReadOnlyList<DeckOptionDto> Decks);

public sealed record DeckOptionDto(string Id, string Name, string? AssetDeckId = null);

public sealed record LogEntry(DateTimeOffset Timestamp, string Message, string Kind);

internal sealed record DeckSelection(string FormatId, string DeckId, string DeckName);
