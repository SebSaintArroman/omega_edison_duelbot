using BotTest.Game.AI.Brains;
using DuelSim.Data;
using DuelSim.Native;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;

namespace DuelSim.Engine;

public sealed record DuelResult(int Winner, string Reason, int Turns, int[] LifePoints, TimeSpan Duration, string? Error = null)
{
    /// <summary>-1 = draw/aborted, otherwise the winning team (0 = went first).</summary>
    public bool IsDraw => Winner < 0;
}

/// <summary>
/// Runs one duel in the engine and exposes it the way Omega exposes a duel to its bots: per-player pending decisions
/// (Prompt), header info, and field queries from either player's point of view (see DuelHost.Views.cs).
/// Team 0 goes first. All engine access goes through _core.
/// </summary>
internal sealed partial class DuelHost : IDisposable
{
    public const int MaxTurns = 150;
    /// <summary>Loop guard: the same player activating the same card this many times in a row, with its deck the same size each time, cancels
    /// the duel. (A deck that shrinks is progress, not a loop: Substitoad tributing into a Frog from the deck again and again ends when the
    /// Frogs run out; user, 2026-11-24: "using substitoad again and again though is very legitimate and should stay in as an option".)</summary>
    public const int MaxSameActivationsInARow = 5;
    /// <summary>Loop guard backstop: decisions (including automatic ones) allowed in a single turn.</summary>
    public const int MaxDecisionsPerTurn = 400;

    private (int Player, int Code, ulong Desc, uint Deck) _lastActivation = (-1, 0, 0, 0);
    private int _sameActivations;
    private int _decisionsThisTurn;

    /// <summary>Raised at the start of every turn: (turn, turn player, LP of player 0, LP of player 1).</summary>
    public event Action<int, int, int, int>? TurnStarted;

    private readonly object _core = new();
    private readonly OcgDuel _duel;
    private readonly CardStore _cards;
    private readonly Random _rng;
    private readonly Prompt?[] _pending = new Prompt?[2];
    // Timeouts in a row per player: a bot that stopped answering (crashed) ends the duel instead of being waited on for
    // every prompt (seen 2026-11-12: a crashed bot left two duels running for 12 hours).
    private readonly int[] _timeoutsInARow = new int[2];
    private const int MaxTimeoutsInARow = 3;
    private readonly ulong[] _selectHint = new ulong[2];
    private readonly int[] _lp = { 8000, 8000 };
    private readonly HashSet<(byte, byte, uint)> _attackedThisTurn = new();
    private readonly HashSet<(byte, byte, uint)> _summonedThisTurn = new();
    private bool _summonedAndAttackedWithThreat;
    internal bool PreviousTurnSummonedAndAttackedWithThreat { get; private set; }
    // Chain link card -> what it targets; cleared when the chain ends.
    private readonly Dictionary<(byte, byte, uint), List<LocInfo>> _chainTargets = new();
    private LocInfo? _lastChainCard;
    private LocInfo? _previousChainCard;
    private (LocInfo Attacker, LocInfo? Target)? _attack;
    internal int AttackDeclarations { get; private set; }
    internal int ChainLinksStarted { get; private set; }
    internal bool IsDirectAttack => _attack is { Target: null };

    /// <summary>This turn's first battle damage from a direct attack: whether it took the defender to 0 (null: no direct hit yet). Gorz
    /// only comes out after a direct hit they survive.</summary>
    internal bool? FirstDirectHitLethal { get; private set; }
    private readonly List<(int Player, int Code, int Location)> _pendingChainEffects = new();
    internal IReadOnlyList<(int Player, int Code, int Location)> PendingChainEffects => _pendingChainEffects;
    private int _resolvingChainIndex = -1;
    internal (int Player, int Code, int Location)? ResolvingChainEffect =>
        _resolvingChainIndex >= 0 && _resolvingChainIndex < _pendingChainEffects.Count
            ? _pendingChainEffects[_resolvingChainIndex] : null;
    internal bool InDamageStep { get; private set; }
    private readonly List<(int Player, int Code)> _survivingAttackers = new();
    internal IReadOnlyList<(int Player, int Code)> SurvivingAttackers => _survivingAttackers;
    private int _lastChainPlayer = -1;
    private int _lastSummonPlayer = -1;
    private int _lastSummonedCardId;
    private bool _summonWindow;
    private bool _summonIdleSeen;
    private readonly bool[] _wipeInChain = new bool[2];

    /// <summary>The card at this place is about to go: a chain link of the other player targets it, or wipes the spells/traps.</summary>
    public bool Doomed(int controller, int location, int sequence)
    {
        lock (_core)
        {
            if (location == Ocg.LocSZone && _wipeInChain[1 - controller])
                return true;
            foreach (var list in _chainTargets.Values)
                if (list.Any(t => t.Controller == controller && t.Location == location && t.Sequence == sequence))
                    return true;
            return false;
        }
    }

    /// <summary>A chain window opened by nothing: no chain to answer, no attack, no summon just made (their Draw Phase, an open
    /// priority pass). A trap fired here could have waited (user, 2026-11-19: "a general 'last possible moment' rule for all traps").</summary>
    public bool Attacking { get { lock (_core) return _attack != null; } }

    internal bool IsOpponentAttacking(int viewer)
    {
        lock (_core)
            return _attack is { } attack && attack.Attacker.Controller != viewer;
    }

    internal bool IsTargetedByOpponent(int controller, int location, int sequence)
    {
        lock (_core)
            return _chainTargets.Any(link => link.Key.Item1 == 1 - controller &&
                link.Value.Any(target => target.Controller == controller &&
                    target.Location == location && target.Sequence == sequence));
    }

    public bool EventlessWindow { get { lock (_core) return _lastChainPlayer < 0 && _attack == null && !_summonWindow; } }

    internal bool IsSummonWindow(int viewer, int cardId)
    {
        lock (_core)
        {
            bool SameCard(int a, int b) => a == b || _cards.Get(a)?.Alias == b || _cards.Get(b)?.Alias == a;
            return _summonWindow && Relative(viewer, _lastSummonPlayer) == 0 && SameCard(cardId, _lastSummonedCardId);
        }
    }
    private Prompt? _lastPrompt;
    private readonly DateTime _started = DateTime.UtcNow;

    public DeckList[] Decks { get; }
    public int Turn { get; private set; }

    // The cards each player activated, by turn (a card's code per activation; Black Whirlwind's trigger for the search's next-turn value).
    private readonly Dictionary<int, HashSet<int>>[] _activatedByTurn = { new(), new() };

    /// <summary>The cards this player activated (effects, spells, traps) in that turn.</summary>
    public IReadOnlyCollection<int> ActivatedInTurn(int player, int turn) =>
        _activatedByTurn[player & 1].TryGetValue(turn, out var codes) ? codes : (IReadOnlyCollection<int>)Array.Empty<int>();

    // The same, Main Phase activations only: a card's Main Phase effect apart from its End Phase one (Lyla's destroy, not her mill).
    private readonly Dictionary<int, HashSet<int>>[] _mainActivatedByTurn = { new(), new() };

    /// <summary>The cards this player activated in a Main Phase of that turn.</summary>
    public IReadOnlyCollection<int> ActivatedInMainPhase(int player, int turn) =>
        _mainActivatedByTurn[player & 1].TryGetValue(turn, out var codes) ? codes : (IReadOnlyCollection<int>)Array.Empty<int>();
    public int TurnPlayer { get; private set; }
    public int Phase { get; private set; } = (int)DuelPhase.Draw;
    public DuelResult? Result { get; private set; }
    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Readable play-by-play (moves, chains, attacks, LP), for the duel transcript.</summary>
    public event Action<string>? Event;

    /// <summary>Random outcomes so far: coin tosses, dice rolls, random card selections.</summary>
    public int RandomEvents { get; private set; }
    internal int OurCardsDrawn { get; private set; }
    internal int OurCardsDrawnAtTurnStart { get; private set; }
    private readonly List<int> _opponentNormalDraws = new();
    internal IReadOnlyList<int> OpponentNormalDraws => _opponentNormalDraws;
    private int _opponentNormalDrawTurn = -1;
    /// <summary>Problems worth surfacing: engine script errors, rejected bot answers, fallbacks.</summary>
    public event Action<string>? Warning;

    public DuelHost(CardStore cards, ScriptStore scripts, DeckList first, DeckList second, int seed)
    {
        _cards = cards;
        _rng = new Random(seed);
        Decks = new[] { first, second };
        var seeds = new ulong[4];
        for (int i = 0; i < 4; i++)
            seeds[i] = (ulong)_rng.NextInt64();
        _duel = OcgDuel.Create(cards, scripts, seeds, Ocg.DuelModeEdison);
        _duel.EngineLog += text => Warning?.Invoke($"engine {text}");

        for (int team = 0; team < 2; team++)
        {
            var deck = Decks[team];
            foreach (var code in deck.Main.OrderBy(_ => _rng.Next()))
                _duel.AddCard(team, code, Ocg.LocDeck);
            foreach (var code in deck.Extra)
                _duel.AddCard(team, code, Ocg.LocExtra);
        }
    }

    public string CardName(int code) => _cards.Get(code)?.Name is { Length: > 0 } name ? name : code.ToString();

    // ---------------------------------------------------------------- main loop

    public async Task<DuelResult> RunAsync(CancellationToken ct)
    {
        try
        {
            lock (_core)
                _duel.Start();
            while (Result == null)
            {
                ct.ThrowIfCancellationRequested();
                int status;
                byte[] messages;
                lock (_core)
                {
                    status = _duel.Process();
                    messages = _duel.GetMessages();
                }
                var prompt = HandleMessages(messages, messages.Length);
                if (_turnHold)
                    await TurnHoldAsync(ct);
                if (Result != null)
                    break;
                if (Turn > MaxTurns)
                {
                    Finish(-1, $"turn limit ({MaxTurns}) reached");
                    break;
                }
                if (prompt != null && ++_decisionsThisTurn > MaxDecisionsPerTurn)
                {
                    Finish(-1, $"stalled: over {MaxDecisionsPerTurn} decisions in turn {Turn} (probably a loop)", error: "loop");
                    break;
                }
                if (prompt != null)
                {
                    var response = await ResolveAsync(prompt, ct);
                    lock (_core)
                        _duel.SetResponse(response);
                }
                else if (status == OcgApi.StatusEnd)
                {
                    Finish(-1, "engine ended the duel without a winner");
                }
                else if (status == OcgApi.StatusAwaiting)
                {
                    Finish(-1, "engine is waiting for a response the simulator did not recognise", error: "unhandled prompt");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Finish(-1, "cancelled");
        }
        catch (Exception ex)
        {
            Finish(-1, "simulator error", error: ex.ToString());
        }
        finally
        {
            for (int p = 0; p < 2; p++)
                _pending[p]?.Response.TrySetCanceled();
        }
        return Result!;
    }

    private void Finish(int winner, string reason, string? error = null)
    {
        Result ??= new DuelResult(winner, reason, Turn, (int[])_lp.Clone(), DateTime.UtcNow - _started, error);
        Event?.Invoke(winner < 0 ? $"DUEL OVER: draw ({reason})" : $"DUEL OVER: player {winner} ({Decks[winner].Name}) wins ({reason})");
    }

    private async Task<byte[]> ResolveAsync(Prompt prompt, CancellationToken ct)
    {
        _lastPrompt = prompt;
        prompt.Attempts++;
        if (prompt.Auto != null && prompt.Attempts == 1)
            return prompt.Auto;

        // A rejected answer (MSG_RETRY): ask the bot once more, then fall back to safe answers in turn.
        if (prompt.Attempts > 2 || prompt.Auto != null)
        {
            int fallback = prompt.Auto != null ? prompt.Attempts - 2 : prompt.Attempts - 3;
            if (fallback < prompt.Fallbacks.Count)
            {
                Warning?.Invoke($"player {prompt.Player}: using fallback #{fallback + 1} for {prompt.State} ({prompt.Summary})");
                return prompt.Fallbacks[fallback];
            }
            throw new InvalidOperationException($"No valid answer for {prompt.State} ({prompt.Summary}) after {prompt.Attempts - 1} tries.");
        }

        var fresh = new Prompt
        {
            Player = prompt.Player, State = prompt.State, MessageId = prompt.MessageId, Data = prompt.Data,
            Translate = prompt.Translate, Fallbacks = prompt.Fallbacks, Summary = prompt.Summary,
        };
        fresh.Attempts = prompt.Attempts;
        _lastPrompt = fresh;
        lock (_core)
            _pending[prompt.Player] = fresh;
        _promptSignal[prompt.Player].Release(); // wake that bot's long-poll (see WaitForPromptAsync)

        byte[] packet;
        try
        {
            packet = await fresh.Response.Task.WaitAsync(PromptTimeout, ct);
        }
        catch (TimeoutException)
        {
            lock (_core)
                _pending[prompt.Player] = null;
            Warning?.Invoke($"player {prompt.Player}: no answer to {prompt.State} within {PromptTimeout.TotalSeconds:0}s");
            if (++_timeoutsInARow[prompt.Player] >= MaxTimeoutsInARow)
                throw new InvalidOperationException($"player {prompt.Player} stopped answering ({MaxTimeoutsInARow} prompts in a row timed out).");
            return prompt.Fallbacks.FirstOrDefault() ?? throw new InvalidOperationException($"{prompt.State} timed out with no fallback.");
        }

        _timeoutsInARow[prompt.Player] = 0;
        byte[]? translated = null;
        try
        {
            translated = prompt.Translate(packet);
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"player {prompt.Player}: could not translate the answer to {prompt.State}: {ex.Message}");
        }
        if (translated == null)
        {
            Warning?.Invoke($"player {prompt.Player}: unusable answer to {prompt.State} ({Convert.ToHexString(packet)}); using a fallback");
            return prompt.Fallbacks.FirstOrDefault() ?? throw new InvalidOperationException($"{prompt.State}: unusable answer and no fallback.");
        }
        return translated;
    }

    // ---------------------------------------------------------------- bot-facing API (called from the HTTP emulation)

    public BotStateHeader Header(int player)
    {
        lock (_core)
        {
            var state = _pending[player]?.State ?? BotState.None;
            return new BotStateHeader
            {
                CurrentStateCode = (byte)state,
                CurrentStateName = state.ToString(),
                CurrentTurn = Turn,
                CurrentPhase = Phase,
                LastChainPlayer = Relative(player, _lastChainPlayer),
                LastSummonPlayer = Relative(player, _lastSummonPlayer),
                CurrentPlayer = Relative(player, TurnPlayer),
                IsFirst = player == 0,
            };
        }
    }

    /// <summary>Visible details of the latest chain link in a search duel.</summary>
    internal SearchChainLink CurrentSearchChain(int viewer)
    {
        lock (_core)
            return SearchChainAt(viewer, _lastChainCard, _lastChainPlayer);
    }

    internal SearchChainLink PreviousSearchChain(int viewer)
    {
        lock (_core)
            return SearchChainAt(viewer, _previousChainCard, _previousChainCard is { } link ? link.Controller : -1);
    }

    private SearchChainLink SearchChainAt(int viewer, LocInfo? location, int player)
    {
        if (location is not { } link)
            return new SearchChainLink(-1, 0, Array.Empty<int>());
        int code = QueryCard(link.Controller, link.Location, link.Sequence)?.Code ?? 0;
        int[] targets = _chainTargets.TryGetValue(link.Key, out var locations)
            ? locations.Select(target => QueryCard(target.Controller, target.Location, target.Sequence)?.Code ?? 0)
                .Where(target => target > 0).ToArray()
            : Array.Empty<int>();
        return new SearchChainLink(Relative(viewer, player), code, targets);
    }

    // Released whenever a player gets a new prompt: the emulator holds a bot's state poll until then (long-poll).
    private readonly SemaphoreSlim[] _promptSignal = { new(0), new(0) };

    /// <summary>
    /// Waits until this player has a prompt pending (or <paramref name="max"/> passes). Lets a bot poll without sleeping:
    /// the poll returns the moment there is a decision to make.
    /// </summary>
    // A new turn: the engine waits until each bot has polled twice since (the first poll sees the new turn and the bot snapshots the board
    // before its next poll), so a bot's turn-start snapshot is the board as the turn began, as in a live game where it's polled every 50 ms
    // while the human hasn't acted yet (plan "the search makes every decision", step 1).
    private readonly object _turnHoldLock = new();
    private bool _turnHold;
    private readonly int[] _turnPolls = new int[2];
    private static readonly TimeSpan TurnHoldMax = TimeSpan.FromSeconds(3);

    private async Task TurnHoldAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + TurnHoldMax;
        while (DateTime.UtcNow < until)
        {
            lock (_turnHoldLock)
                if (_turnPolls[0] >= 2 && _turnPolls[1] >= 2)
                    break;
            await Task.Delay(5, ct);
        }
        lock (_turnHoldLock)
            _turnHold = false;
    }

    /// <summary>A bot asked for its state (the emulator's header route): counts toward releasing a new-turn hold.</summary>
    public void NotePoll(int player)
    {
        lock (_turnHoldLock)
            if (_turnHold)
                _turnPolls[player]++;
    }

    public async Task WaitForPromptAsync(int player, TimeSpan max, CancellationToken ct)
    {
        if (_turnHold)
            return; // answer at once while a new turn waits for the bots' snapshots
        if (Pending(player) != null)
            return;
        try { await _promptSignal[player].WaitAsync(max, ct); }
        catch (OperationCanceledException) { }
    }

    public Prompt? Pending(int player)
    {
        lock (_core)
            return _pending[player];
    }

    /// <summary>Takes a bot's response packet. False when that bot has nothing pending.</summary>
    public bool Submit(int player, byte[] packet)
    {
        Prompt? prompt;
        lock (_core)
        {
            prompt = _pending[player];
            _pending[player] = null;
        }
        if (prompt == null)
            return false;
        prompt.Response.TrySetResult(packet);
        return true;
    }

    private static int Relative(int viewer, int player) => player < 0 ? -1 : player == viewer ? 0 : 1;
    private static int Absolute(int viewer, int relative) => relative == 0 ? viewer : 1 - viewer;

    // ---------------------------------------------------------------- message stream

    /// <summary>Applies every message to the tracked state; returns the decision the engine now waits on, if any.</summary>
    private Prompt? HandleMessages(byte[] buffer, int count)
    {
        Prompt? prompt = null;
        int offset = 0;
        while (count - offset >= 4)
        {
            int length = BitConverter.ToInt32(buffer, offset);
            offset += 4;
            if (length <= 0 || length > count - offset)
                break;
            var reader = new MessageReader(buffer, offset, length);
            offset += length;
            byte id = reader.U8();
            try
            {
                var p = HandleMessage(id, reader);
                if (p != null)
                    prompt = p;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                Warning?.Invoke($"could not read engine message {id}: {ex.Message}");
            }
        }
        return prompt;
    }

    private Prompt? HandleMessage(byte id, MessageReader r)
    {
        switch (id)
        {
            case Msg.Retry:
                Warning?.Invoke($"engine rejected player {_lastPrompt?.Player}'s answer to {_lastPrompt?.State} ({_lastPrompt?.Summary})");
                return _lastPrompt;
            case Msg.Hint:
            {
                byte type = r.U8();
                byte player = r.U8();
                ulong data = r.U64();
                if (type == Msg.HintSelectMsg && player < 2)
                    _selectHint[player] = data;
                return null;
            }
            case Msg.Win:
            {
                byte winner = r.U8();
                byte reason = r.U8();
                Finish(winner > 1 ? -1 : winner, WinReason(reason));
                return null;
            }
            case Msg.NewTurn:
            {
                OurCardsDrawnAtTurnStart = OurCardsDrawn;
                TurnPlayer = r.U8() & 1;
                Turn++;
                _attackedThisTurn.Clear();
                _summonedThisTurn.Clear();
                FirstDirectHitLethal = null;
                PreviousTurnSummonedAndAttackedWithThreat = _summonedAndAttackedWithThreat;
                _summonedAndAttackedWithThreat = false;
                _decisionsThisTurn = 0;
                _lastActivation = (-1, 0, 0, 0);
                _sameActivations = 0;
                Event?.Invoke($"--- Turn {Turn}: player {TurnPlayer} ({Decks[TurnPlayer].Name}) | LP {_lp[0]} / {_lp[1]}");
                TurnStarted?.Invoke(Turn, TurnPlayer, _lp[0], _lp[1]);
                // Hold the engine here until both bots have seen the new turn and taken their turn-start snapshots (see TurnHoldAsync).
                lock (_turnHoldLock)
                {
                    _turnHold = true;
                    _turnPolls[0] = _turnPolls[1] = 0;
                }
                _promptSignal[0].Release();
                _promptSignal[1].Release();
                return null;
            }
            case Msg.NewPhase:
                Phase = r.U16();
                InDamageStep = false;
                _summonWindow = false;
                _lastSummonedCardId = 0;
                _attack = null;
                return null;
            case Msg.Chaining:
            {
                ChainLinksStarted++;
                int code = r.I32();
                var card = r.Loc();
                byte triggerController = r.U8();
                byte triggerLocation = r.U8();
                r.U32();
                ulong desc = r.U64();
                uint chainCount = r.U32();
                _pendingChainEffects.Add((triggerController, code, triggerLocation));
                _lastChainPlayer = triggerController;
                if (triggerController < 2 && _cards.Get(code)?.Text is { } wipeText &&
                    System.Text.RegularExpressions.Regex.IsMatch(wipeText, @"destroy all (Spells?|Spell(/| and )Trap)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    _wipeInChain[triggerController] = true;
                _previousChainCard = _lastChainCard;
                _lastChainCard = card;
                if (triggerController < 2)
                {
                    if (!_activatedByTurn[triggerController].TryGetValue(Turn, out var activated))
                        _activatedByTurn[triggerController][Turn] = activated = new HashSet<int>();
                    activated.Add(code);
                    if (Phase is (int)DuelPhase.Main1 or (int)DuelPhase.Main2)
                    {
                        if (!_mainActivatedByTurn[triggerController].TryGetValue(Turn, out var inMain))
                            _mainActivatedByTurn[triggerController][Turn] = inMain = new HashSet<int>();
                        inMain.Add(code);
                    }
                }
                Event?.Invoke($"  chain {chainCount}: player {triggerController} activates {CardName(code)}");
                // Loop guard: same player, same card, same effect, N activations with no other activation in between. (By effect:
                // Indomitable Gladiator Beast from the hand, then its recycle from the GY, alternating, is a plan, not a loop;
                // round-robin 13 was ended as a draw by this guard.)
                uint deck = triggerController < 2 ? _duel.QueryCount((byte)triggerController, Ocg.LocDeck) : 0;
                _sameActivations = _lastActivation == (triggerController, code, desc, deck) ? _sameActivations + 1 : 1;
                _lastActivation = (triggerController, code, desc, deck);
                if (_sameActivations >= MaxSameActivationsInARow)
                    Finish(-1, $"stalled: player {triggerController} activated {CardName(code)} {MaxSameActivationsInARow} times in a row in turn {Turn} (probably a loop)", error: "loop");
                return null;
            }
            case Msg.BecomeTarget:
            {
                uint count = r.U32();
                var targets = new List<LocInfo>();
                for (int i = 0; i < count; i++)
                    targets.Add(r.Loc());
                if (_lastChainCard is { } chainCard)
                {
                    if (!_chainTargets.TryGetValue(chainCard.Key, out var list))
                        _chainTargets[chainCard.Key] = list = new List<LocInfo>();
                    list.AddRange(targets);
                }
                return null;
            }
            case Msg.ChainSolving:
                _resolvingChainIndex = r.U8() - 1;
                return null;
            case Msg.ChainSolved:
                _resolvingChainIndex = -1;
                return null;
            case Msg.ChainEnd:
                _resolvingChainIndex = -1;
                _pendingChainEffects.Clear();
                _chainTargets.Clear();
                _wipeInChain[0] = _wipeInChain[1] = false;
                _lastChainCard = null;
                _previousChainCard = null;
                _lastChainPlayer = -1;
                return null;
            case Msg.Summoning:
            case Msg.SpSummoning:
            case Msg.FlipSummoning:
            {
                int code = r.I32();
                var at = r.Loc();
                _lastSummonPlayer = at.Controller;
                _lastSummonedCardId = code;
                if (at.Controller == TurnPlayer)
                    _summonedThisTurn.Add(at.Key);
                _summonWindow = true;
                _summonIdleSeen = false;
                string kind = id == Msg.Summoning ? "summons" : id == Msg.SpSummoning ? "special summons" : "flip summons";
                Event?.Invoke($"  player {at.Controller} {kind} {CardName(code)}");
                return null;
            }
            case Msg.Attack:
            {
                AttackDeclarations++;
                var attacker = r.Loc();
                var target = r.Loc();
                _attack = (attacker, target.Location == 0 ? null : target);
                _attackedThisTurn.Add(attacker.Key);
                if (!_summonedAndAttackedWithThreat && _summonedThisTurn.Contains(attacker.Key) &&
                    QueryCard(attacker.Controller, attacker.Location, attacker.Sequence) is { } summonedAttacker)
                {
                    int bestDefense = QueryList(1 - attacker.Controller, Ocg.LocMZone, Ocg.QueryPosition | Ocg.QueryAttack)
                        .Where(card => card != null && (card.Position & Ocg.PosFaceUpAttack) != 0)
                        .Select(card => card!.Attack).DefaultIfEmpty(0).Max();
                    _summonedAndAttackedWithThreat = summonedAttacker.Attack > bestDefense;
                }
                Event?.Invoke($"  attack: {DescribeAt(attacker)} -> {(target.Location == 0 ? "direct" : DescribeAt(target))}");
                return null;
            }
            case Msg.AttackDisabled:
                _attack = null;
                return null;
            case Msg.DamageStepStart:
                InDamageStep = true;
                return null;
            case Msg.DamageStepEnd:
                InDamageStep = false;
                if (_attack is { } battle && QueryCard(battle.Attacker.Controller, battle.Attacker.Location,
                    battle.Attacker.Sequence) is { Code: > 0 } survivor)
                    _survivingAttackers.Add((battle.Attacker.Controller, survivor.Code));
                _attack = null;
                return null;
            case Msg.Move:
            {
                int code = r.I32();
                var from = r.Loc();
                var to = r.Loc();
                uint reason = r.U32();
                if (_attack is { } currentBattle && currentBattle.Attacker.Key == from.Key && to.Location != Ocg.LocMZone)
                    _attack = null;
                if (_attackedThisTurn.Remove(from.Key) && to.Location == Ocg.LocMZone)
                    _attackedThisTurn.Add(to.Key);
                if (from.Key != to.Key && _summonedThisTurn.Remove(from.Key) && to.Location == Ocg.LocMZone)
                    _summonedThisTurn.Add(to.Key);
                bool draw = from.Location == Ocg.LocDeck && to.Location == Ocg.LocHand;
                if (code != 0 && from.Location != to.Location && !draw && to.Location != Ocg.LocOverlay)
                    Event?.Invoke($"  {CardName(code)}: {LocationName(from)} -> {LocationName(to)}");
                return null;
            }
            case Msg.Damage:
            case Msg.PayLpCost:
            {
                byte player = r.U8();
                uint amount = r.U32();
                if (player < 2)
                {
                    _lp[player] = Math.Max(0, _lp[player] - (int)amount);
                    if (id == Msg.Damage && InDamageStep && FirstDirectHitLethal == null &&
                        _attack is { Target: null } direct && direct.Attacker.Controller != player)
                        FirstDirectHitLethal = _lp[player] == 0;
                    Event?.Invoke($"  player {player} {(id == Msg.Damage ? "takes" : "pays")} {amount} (LP {_lp[player]})");
                }
                return null;
            }
            case Msg.Recover:
            {
                byte player = r.U8();
                uint amount = r.U32();
                if (player < 2)
                    _lp[player] += (int)amount;
                return null;
            }
            case Msg.LpUpdate:
            {
                byte player = r.U8();
                uint lp = r.U32();
                if (player < 2)
                    _lp[player] = (int)lp;
                return null;
            }
            case Msg.Draw:
            {
                int player = r.U8();
                int count = (int)r.U32();
                if (player == 0)
                    OurCardsDrawn += count;
                else if (!_ownsDuel && player == 1 && TurnPlayer == 1 && Phase == (int)DuelPhase.Draw &&
                    count > 0 && _opponentNormalDrawTurn != Turn && ResolvingChainEffect == null)
                {
                    // One baseline draw per turn. Extra draws caused by effects still count.
                    _opponentNormalDrawTurn = Turn;
                    _opponentNormalDraws.Add(r.I32());
                }
                return null;
            }
            // Chance: a coin toss, a dice roll, a card picked at random. Counted for the play search (a random outcome the plan
            // can't know: its decisions after one are re-planned, Mcts / DeckBotBrain.TurnRecord).
            case Msg.TossCoin:
            case Msg.TossDice:
            case Msg.RandomSelected:
                RandomEvents++;
                return null;
            default:
                return BuildPrompt(id, r);
        }
    }

    private string DescribeAt(LocInfo at)
    {
        var card = QueryCard(at.Controller, at.Location, at.Sequence);
        return card == null ? LocationName(at) : $"{CardName(card.Code)} (player {at.Controller})";
    }

    private static string LocationName(LocInfo at)
    {
        string where = at.Location switch
        {
            (byte)Ocg.LocDeck => "deck",
            (byte)Ocg.LocHand => "hand",
            (byte)Ocg.LocMZone => $"M{at.Sequence + 1}",
            (byte)Ocg.LocSZone => $"S{at.Sequence + 1}",
            (byte)Ocg.LocGrave => "GY",
            (byte)Ocg.LocRemoved => "banished",
            (byte)Ocg.LocExtra => "extra",
            _ => $"loc {at.Location}",
        };
        return $"p{at.Controller} {where}";
    }

    private static string WinReason(byte reason) => reason switch
    {
        // processor.cpp: 1 = LP, 2 = couldn't draw; 0x10+ = card wins (WIN_REASON_* in constant.lua).
        0 => "surrender",
        1 => "life points reached 0",
        2 => "deck out",
        0x10 => "Exodia",
        _ => $"card win (reason 0x{reason:x})",
    };

    public void Dispose()
    {
        // A search duel belongs to its PuzzleEngine, which reuses it for the next line.
        if (!_ownsDuel)
        {
            // Publish each line's timings once instead of updating shared counters every engine batch.
            if (_searchEngineTicks != 0)
                SearchTimings.Add(ref SearchTimings.Engine, _searchEngineTicks);
            if (_searchMessageTicks != 0)
                SearchTimings.Add(ref SearchTimings.Messages, _searchMessageTicks);
            _searchEngineTicks = _searchMessageTicks = 0;
            return;
        }
        lock (_core)
            _duel.Dispose();
    }
}
