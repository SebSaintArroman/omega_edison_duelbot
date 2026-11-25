using DuelSim.Data;
using DuelSim.Native;
using BotTest.Game.AI.Brains;
using OmegaBotWrapper.Enum;
using static BotTest.Game.AI.CardId;

namespace DuelSim.Engine;

/// <summary>
/// Search mode: a duel built from a position (a generated puzzle script) and driven one prompt at a time by the caller
/// instead of by bots over the emulated API. Used by the bots' play search (BotTest/Game/AI/Search) as their own
/// mini simulator: Omega only shows a bot the board, so the bot rebuilds it here and plays candidate lines forward.
/// </summary>
internal sealed partial class DuelHost
{
    /// <summary>
    /// Puzzle duel flags: Omega's (Edison) rules, and the first (rebuilt) turn may attack. Without DUEL_1ST_TURN_DRAW: the
    /// rebuilt position starts on engine turn 1, and with that flag the engine drew a card there, so every line played with one
    /// card more than the real hand and no tree node was ever found again (CSV S1, 2026-11-15). A projected next turn deals
    /// its own draw (SearchView.DrawsAtStart).
    /// </summary>
    public const ulong PuzzleFlags = (Ocg.DuelModeEdison & ~Ocg.Duel1stTurnDraw) | 0x02 /* DUEL_ATTACK_FIRST_TURN */;

    private bool _searchStarted;
    private bool _ownsDuel = true;

    private DuelHost(CardStore cards, OcgDuel duel, int seed)
    {
        _cards = cards;
        _rng = new Random(seed);
        _duel = duel;
        _ownsDuel = false;
        Decks = new[] { new DeckList("searcher", Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()),
                        new DeckList("opponent", Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()) };
    }

    /// <summary>A search duel on an engine duel the caller owns (PuzzleEngine).</summary>
    internal static DuelHost OnEngine(CardStore cards, OcgDuel duel, int seed) => new(cards, duel, seed);
}

/// <summary>
/// One engine duel reused for line after line of a search (one per search thread). Building an engine duel costs far
/// more than playing two turns in it (the base scripts, then every card script); a puzzle's Debug.ReloadFieldBegin
/// wipes the field, cards and effects but keeps the loaded scripts, so the next position loads on the same duel.
/// Once-per-duel script setup (aux.GlobalCheck) is re-armed before each position, and the duel is rebuilt from scratch
/// every <see cref="RebuildEvery"/> lines to cap anything a wipe leaves behind.
/// </summary>
internal sealed class PuzzleEngine : IDisposable
{
    /// <remarks>2000 since 2026-11-10 (no leak seen from wipes); the warm-up rebuilds engines past half of it in idle time.</remarks>
    public const int RebuildEvery = 2000;

    // Re-arms the card scripts' once-per-duel setup (s.global_check, see aux.GlobalCheck in utility.lua): each line is a new epoch, and the
    // helper runs a script's setup once per epoch. The old re-arm walked every global table each line (2026-11-19 profile: loading a line's
    // position was 14% of the search).
    private static readonly string RearmSetup =
        "_st_epoch=1\n" +
        "function Auxiliary.GlobalCheck(s,func) if s.global_check~=_st_epoch then s.global_check=_st_epoch func() end end\n" +
        BotTest.Game.AI.Search.SearchPosition.BlindRevealSetup +
        BotTest.Game.AI.Search.SearchPosition.RebuildHooksSetup;
    private const string Rearm = "_st_epoch=(_st_epoch or 0)+1\n";

    private readonly CardStore _cards;
    private readonly ScriptStore _scripts;
    private readonly Random _rng;
    private OcgDuel? _duel;
    private int _uses;
    private string? _engineError;

    public PuzzleEngine(CardStore cards, ScriptStore scripts, int seed)
    {
        _cards = cards;
        _scripts = scripts;
        _rng = new Random(seed);
    }

    /// <summary>
    /// A duel laid out by a puzzle script (Debug.ReloadFieldBegin ... Debug.ReloadFieldEnd), starting at turn 1 of
    /// player 0 with no opening draw. Null (with the reason) when the engine rejects the script. The previous line's
    /// DuelHost must be done with: this reuses its engine duel.
    /// </summary>
    public DuelHost? Load(string puzzle, out string? error)
    {
        error = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool fresh = false;
        if (_duel == null || _uses >= RebuildEvery)
        {
            _duel?.Dispose();
            var seeds = new ulong[4];
            for (int i = 0; i < 4; i++)
                seeds[i] = (ulong)_rng.NextInt64();
            _duel = OcgDuel.Create(_cards, _scripts, seeds, DuelHost.PuzzleFlags, startingHand: 0);
            _duel.EngineLog += text => { if (text.StartsWith("[error]")) _engineError ??= text; };
            _uses = 0;
            WarmedFor = 0; // a fresh duel has no scripts loaded yet
            fresh = true;
        }
        _uses++;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        SearchTimings.Add(ref SearchTimings.Create, t1 - t0);
        _engineError = null;
        if (!_duel.LoadScriptText("search-position.lua", (fresh ? RearmSetup : Rearm) + puzzle))
        {
            error = _engineError ?? "the engine rejected the position script";
            // Don't reuse a duel in an unknown state.
            _duel.Dispose();
            _duel = null;
            return null;
        }
        SearchTimings.Add(ref SearchTimings.Puzzle, System.Diagnostics.Stopwatch.GetTimestamp() - t1);
        if (SearchTimings.MeasurePuzzleParse)
        {
            // Lua compiles the whole chunk but skips its body at runtime. Run after the real load so
            // this probe does not warm Lua's string table for the position it measures.
            string probe = "if false then\n" + puzzle + "\nend\n";
            long parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_duel.LoadScriptText("search-position-parse-probe.lua", probe))
                SearchTimings.Add(ref SearchTimings.PuzzleParse, System.Diagnostics.Stopwatch.GetTimestamp() - parseStart);
            else
                Interlocked.Increment(ref SearchTimings.PuzzleParseFailures);
        }
        return DuelHost.OnEngine(_cards, _duel, _rng.Next());
    }

    /// <summary>Lines played on the current engine duel since it was built.</summary>
    public int Uses => _uses;

    /// <summary>Which card set this duel's scripts were warmed for (TurnSearch.WarmUp), 0 = none.</summary>
    public int WarmedFor { get; set; }

    /// <summary>Drops the engine duel (after a line that went wrong): the next Load builds a fresh one.</summary>
    public void Discard()
    {
        _duel?.Dispose();
        _duel = null;
        WarmedFor = 0;
    }

    public void Dispose() => Discard();
}

internal sealed partial class DuelHost
{

    /// <summary>
    /// Runs the duel to the next decision that needs an answer (answering the automatic ones itself). Null when the duel
    /// is over (Result), the engine is stuck, or turn <paramref name="stopAtTurn"/> has begun. A prompt the engine rejected
    /// comes back again with Attempts &gt; 0: answer it from its Fallbacks.
    /// </summary>
    public Prompt? NextPrompt(int stopAtTurn, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_searchStarted)
        {
            _searchStarted = true;
            _duel.Start();
        }
        while (Result == null)
        {
            ct.ThrowIfCancellationRequested();
            // The search views (board queries) are dropped when a batch carries a board message (HandleMessages -> BoardChanged), so a
            // batch with only the next prompt keeps them, and the brain keeps its board reads (SearchBrain, BoardVersion).
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int status = _duel.Process();
            var messages = _duel.GetSearchMessages();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var prompt = HandleMessages(messages.Array!, messages.Count);
            _searchEngineTicks += t1 - t0;
            _searchMessageTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            if (Result != null || Turn >= stopAtTurn)
                return null;
            if (prompt != null && ++_decisionsThisTurn > MaxDecisionsPerTurn)
            {
                Finish(-1, "stalled", error: "loop");
                return null;
            }
            if (prompt != null)
            {
                if (prompt.Auto != null && prompt.Attempts == 0)
                {
                    Respond(prompt, prompt.Auto);
                    continue;
                }
                CheckBoardVersion(prompt);
                return prompt;
            }
            if (status != OcgApi.StatusContinue)
            {
                Finish(-1, status == OcgApi.StatusEnd ? "engine ended the duel" : "engine waits for an unhandled prompt", error: "stuck");
                return null;
            }
        }
        return null;
    }

    private int _searchNormalSummonOrSetTurn = -1;
    internal bool SearchNormalSummonOrSetUsed => _searchNormalSummonOrSetTurn == Turn;
    // Per player (0 us, 1 them): the turn of their last Normal Summon or Set (user, 2026-11-25: "record it": whether a card in their hand
    // can still be Tribute Summoned this turn, CardRuleRegistry.TributableHandCorrection).
    private readonly int[] _normalSummonTurn = { -1, -1 };

    /// <summary>Whether this player has used their Normal Summon / Set this turn.</summary>
    internal bool NormalSummonUsed(int player) => _normalSummonTurn[player] == Turn;

    /// <summary>The real game's record when the line starts mid-turn (a direct rebuild after their Normal Summon).</summary>
    internal void MarkNormalSummonUsed(int player)
    {
        _normalSummonTurn[player] = Turn;
        BoardChanged();
    }

    /// <summary>Answers a prompt with an engine response.</summary>
    public void Respond(Prompt prompt, byte[] engineResponse)
    {
        if (prompt.State == BotState.SelectIdleCmd && engineResponse.Length >= 4 &&
            (BitConverter.ToInt32(engineResponse) & 0xffff) is (int)MainAction.Summon or (int)MainAction.SetMonster)
        {
            BoardChanged(); // the side summaries' Normal Summon flag
            if (prompt.Player == 0)
                _searchNormalSummonOrSetTurn = Turn;
            if (prompt.Player is 0 or 1)
                _normalSummonTurn[prompt.Player] = Turn;
        }
        prompt.Attempts++;
        _lastPrompt = prompt;
        _duel.SetResponse(engineResponse);
    }

    /// <summary>The engine response for a bot packet (the bot's answer format), or null when it can't be used.</summary>
    public static byte[]? TranslatePacket(Prompt prompt, byte[] packet)
    {
        try { return prompt.Translate(packet); }
        catch { return null; }
    }

    /// <summary>An attack is being declared or fought (a good moment for traps).</summary>
    public bool AttackPending => _attack != null;

    public int LifePoints(int player) => _lp[player];

    internal void HideUnseenDeckReveals()
    {
        if (!_duel.LoadScriptText("search-reveal-policy.lua", "_st_hide_new=true"))
            throw new InvalidOperationException("Could not enable the search's hidden-card policy.");
    }

    /// <summary>
    /// The whole position as text, for the search's transpositions (merged only on an exact match): both fields card by card with
    /// everything the engine reports (stats and base stats, level, type, race, attribute, status flags such as summoned or attacked
    /// this turn, equip target and targets by the card they point at, counters, overlays, owner), both hands, graveyards and banished
    /// piles, deck and Extra Deck sizes, life points. Zones are left out on purpose: two orders of summons put the same cards in
    /// different zones (no card in these decks cares about columns).
    /// </summary>
    /// <param name="statusMask">The status bits to keep (all by default; the stale-board check keeps only what the reads report).</param>
    public string Fingerprint(uint statusMask = uint.MaxValue)
    {
        const uint flags = Ocg.QueryCode | Ocg.QueryPosition | Ocg.QueryType | Ocg.QueryLevel | Ocg.QueryRank | Ocg.QueryAttribute | Ocg.QueryRace |
                           Ocg.QueryAttack | Ocg.QueryDefense | Ocg.QueryBaseAttack | Ocg.QueryBaseDefense | Ocg.QueryEquipCard | Ocg.QueryTargetCard |
                           Ocg.QueryOverlayCard | Ocg.QueryCounters | Ocg.QueryOwner | Ocg.QueryStatus;
        // Plain loops (line-time audit 2026-11-25: the LINQ version was ~2% of a line); the text is the same as before.
        // field[p * 2 + 0] = monster zones, [p * 2 + 1] = spell/trap zones.
        var field = new List<CardQuery?>[4];
        for (int p = 0; p < 2; p++)
        {
            field[p * 2] = QueryList(p, Ocg.LocMZone, flags);
            field[p * 2 + 1] = QueryList(p, Ocg.LocSZone, flags);
        }
        string Pointed(LocInfo at)
        {
            var list = at.Controller < 2 ? at.Location == Ocg.LocMZone ? field[at.Controller * 2] : at.Location == Ocg.LocSZone ? field[at.Controller * 2 + 1] : null : null;
            return list != null && at.Sequence < list.Count && list[(int)at.Sequence] is { } c ? $"{at.Controller}:{c.Code}" : $"{at.Controller}:{at.Location}?";
        }
        var cardText = new System.Text.StringBuilder(128);
        var parts = new List<string>(8);
        string Joined(List<string> items, char separator)
        {
            items.Sort(StringComparer.Ordinal);
            return string.Join(separator, items);
        }
        string Card(CardQuery c)
        {
            cardText.Clear();
            cardText.Append(c.Code).Append('/').Append(c.Position).Append('/').Append(c.Type).Append('/').Append(c.Level).Append('/').Append(c.Rank)
                .Append('/').Append(c.Attribute).Append('/').Append(c.Race).Append('/').Append(c.Attack).Append('/').Append(c.Defense)
                .Append('/').Append(c.BaseAttack).Append('/').Append(c.BaseDefense).Append('/').Append(c.Owner).Append('/').Append(c.Status & statusMask);
            cardText.Append("/e");
            if (c.EquipTarget is { } e)
                cardText.Append(Pointed(e));
            cardText.Append("/t");
            if (c.Targets.Length > 0)
            {
                var targets = new List<string>(c.Targets.Length);
                foreach (var t in c.Targets)
                    targets.Add(Pointed(t));
                cardText.Append(Joined(targets, '+'));
            }
            cardText.Append("/o");
            if (c.Overlays.Length > 0)
            {
                var overlays = (int[])c.Overlays.Clone();
                Array.Sort(overlays);
                cardText.Append(string.Join('+', overlays));
            }
            cardText.Append("/n");
            if (c.Counters.Length > 0)
            {
                var counters = new List<string>(c.Counters.Length);
                foreach (var k in c.Counters)
                    counters.Add($"{k.Type}x{k.Count}");
                cardText.Append(Joined(counters, '+'));
            }
            return cardText.ToString();
        }
        var sb = new System.Text.StringBuilder(512);
        for (int p = 0; p < 2; p++)
        {
            sb.Append('P').Append(p).Append(':').Append(_lp[p]);
            for (int zone = 0; zone < 2; zone++)
            {
                parts.Clear();
                foreach (var c in field[p * 2 + zone])
                    if (c != null)
                        parts.Add(Card(c));
                sb.Append('|').Append(Joined(parts, ','));
            }
            foreach (uint loc in PileLocations)
            {
                parts.Clear();
                foreach (var c in QueryList(p, loc, Ocg.QueryCode | Ocg.QueryPosition))
                    if (c != null)
                        parts.Add($"{c.Code}/{c.Position}");
                sb.Append('|').Append(Joined(parts, ','));
            }
            sb.Append('|').Append(CountIn(p, Ocg.LocDeck)).Append('/').Append(CountIn(p, Ocg.LocExtra)).Append('#');
        }
        return sb.ToString();
    }

    private static readonly uint[] PileLocations = { Ocg.LocHand, Ocg.LocGrave, Ocg.LocRemoved };

    /// <summary>How many cards this player has in a location (cheap: no card queries), e.g. their monsters, to spot a summon.</summary>
    public int CountIn(int player, uint location)
    {
        lock (_core)
            return (int)_duel.QueryCount(player, location);
    }

    internal int[] ExtraDeckCodes(int player) =>
        _searchExtraCodes[player] ??= QueryCodes(player, Ocg.LocExtra)
            .Where(code => code.HasValue).Select(code => code.GetValueOrDefault()).ToArray();

    internal int[] BanishedCodes(int player) => QueryCodes(player, Ocg.LocRemoved)
        .Where(code => code.HasValue).Select(code => code.GetValueOrDefault()).ToArray();

    /// <summary>Every card in a location with whether it's face-up, read fresh from the engine (no cache): the real duel's truth, for the
    /// position dataset's card counts.</summary>
    internal List<(int Code, bool FaceUp)> CardsIn(int player, uint location) =>
        QueryList(player, location, Ocg.QueryCode | Ocg.QueryPosition).Where(card => card != null && card.Code > 0)
            .Select(card => (card!.Code, (card.Position & Ocg.PosFaceUp) != 0)).ToList();

    /// <summary>A player's life points now.</summary>
    internal int LifePointsOf(int player) => _lp[player];

    internal StrategicCard[] StrategicCards(int player, bool spellZone) =>
        (spellZone
            ? QueryList(player, Ocg.LocSZone, Ocg.QueryCode | Ocg.QueryPosition | Ocg.QueryAttack | Ocg.QueryDefense | Ocg.QueryStatus)
            : SearchMonsters(player))
            .Select((card, zone) => (card, zone)).Where(entry => entry.card != null)
            .Select(entry =>
            {
                var card = entry.card!;
                return new StrategicCard(card.Code, entry.zone, card.Attack, card.Defense, (card.Position & Ocg.PosFaceUp) != 0,
                    (card.Position & Ocg.PosFaceUpAttack) != 0, (card.Status & Ocg.StatusDisabled) != 0);
            }).ToArray();

    /// <summary>The position's life points (the puzzle sets them in the engine without a message the host would see).</summary>
    public void SetLifePoints(int first, int second)
    {
        BoardChanged();
        _lp[0] = first;
        _lp[1] = second;
    }

    private readonly SideSummary?[] _searchSummaries = new SideSummary?[4];
    private readonly List<CardQuery?>?[] _searchMonsters = new List<CardQuery?>?[2];
    private readonly int[]?[] _searchExtraCodes = new int[]?[2];
    private Dictionary<(int Controller, uint Location, uint Sequence, uint Overlay), CardQuery?>? _searchCardQueries;
    private Dictionary<(int Controller, uint Location), List<int?>>? _searchCodeQueries;
    private long _searchEngineTicks, _searchMessageTicks;

    private static long s_boardVersions;

    /// <summary>
    /// The board's version: a new number (unique across all search duels) whenever the board may have changed, i.e. an engine batch
    /// carried anything but hints and the next prompt (HandleMessages), or the host changed what it reports (life points, a Normal
    /// Summon). Reads of the board (the search views here, the brain twin's field reads: SearchBrain) stay valid while it's the same.
    /// </summary>
    public long BoardVersion { get; private set; } = Interlocked.Increment(ref s_boardVersions);

    private void BoardChanged()
    {
        BoardVersion = Interlocked.Increment(ref s_boardVersions);
        InvalidateSearchViews();
    }

    // The stale-board check (user, 2026-11-25: flag it in the logs and raise it at comment reviews): a sample of the board versions
    // records the board as it was read; a later prompt on the same version must still see that board. A difference means the engine
    // changed the board without a message the host counts, so cached reads were stale (SearchTimings.StaleBoards).
    private const int StaleCheckEvery = 32; // ~1% of a line at 1 in 8 (two engine reads of the whole board per check)
    private long _checkedVersion = -1;
    private string? _checkedBoard;
    private BotState _checkedState;

    private void CheckBoardVersion(Prompt prompt)
    {
        if (BoardVersion != _checkedVersion)
        {
            _checkedVersion = BoardVersion;
            // Sampled by the version's number, not the duel's random numbers (the lines must play as without the check).
            _checkedBoard = BoardVersion % StaleCheckEvery == 0 ? BoardCheckKey() : null;
            _checkedState = prompt.State;
            return;
        }
        if (_checkedBoard == null)
            return;
        string now = BoardCheckKey();
        if (now == _checkedBoard)
            return;
        Interlocked.Increment(ref SearchTimings.StaleBoards);
        if (SearchTimings.FirstStaleBoard == null)
        {
            int at = 0;
            while (at < now.Length && at < _checkedBoard.Length && now[at] == _checkedBoard[at])
                at++;
            string Around(string s) => s.Substring(Math.Max(0, at - 60), Math.Min(s.Length - Math.Max(0, at - 60), 160));
            Interlocked.CompareExchange(ref SearchTimings.FirstStaleBoard,
                $"turn {Turn}, phase 0x{Phase:x}: the board changed between {_checkedState} and {prompt.State} with no board message; " +
                $"was ...{Around(_checkedBoard)}... now ...{Around(now)}...", null);
        }
        _checkedBoard = now; // one report per change
    }

    /// <summary>What the search's board reads report (the brain's views and the side summaries), read fresh from the engine.</summary>
    private string BoardCheckKey() =>
        Fingerprint(statusMask: Ocg.StatusDisabled) +
        // (Not the phase: no read reports it; the brain's header is read afresh at every decision.)
        $"|T{Turn}/{TurnPlayer}|a{(_attack is { } a ? $"{a.Attacker.Key}>{a.Target?.Key}" : "")}|at{string.Join(",", _attackedThisTurn.OrderBy(k => k))}" +
        $"|ct{_chainTargets.Count}|ns{_normalSummonTurn[0]},{_normalSummonTurn[1]}";

    private void InvalidateSearchViews()
    {
        Array.Clear(_searchSummaries);
        Array.Clear(_searchMonsters);
        Array.Clear(_searchExtraCodes);
        _searchCardQueries?.Clear();
        _searchCodeQueries?.Clear();
    }

    /// <summary>The codes of this player's monsters in zone order (as Summary(player).Monsters lists them), without reading the rest of the side.</summary>
    internal List<int> MonsterCodes(int player)
    {
        var codes = new List<int>(5);
        foreach (var card in SearchMonsters(player))
            if (card != null)
                codes.Add(card.Code);
        return codes;
    }

    private List<CardQuery?> SearchMonsters(int player) =>
        _searchMonsters[player] ??= QueryList(player, Ocg.LocMZone,
            Ocg.QueryCode | Ocg.QueryPosition | Ocg.QueryAttack | Ocg.QueryDefense | Ocg.QueryLevel |
            Ocg.QueryRank | Ocg.QueryType | Ocg.QueryStatus | Ocg.QueryOwner);

    /// <summary>What a position evaluation needs to know about one side, shared until the search duel advances.</summary>
    /// <param name="excludeForeign">Leave out monsters this player controls but the other player owns (taken: Brain Control, Goyo...).</param>
    public SideSummary Summary(int player, bool excludeForeign = false)
    {
        var summary = _searchSummaries[player] ??= ReadSummary(player);
        if (!excludeForeign || summary.Foreign == 0)
            return summary;
        return _searchSummaries[player + 2] ??= summary with
        {
            Monsters = ReadSearchMonsters(player, excludeForeign: true, out _)
        };
    }

    private List<(int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated)>
        ReadSearchMonsters(int player, bool excludeForeign, out int foreign)
    {
        var monsters = new List<(int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated)>();
        foreign = 0;
        foreach (var card in SearchMonsters(player))
        {
            if (card == null)
                continue;
            if (card.Owner != player)
            {
                foreign++;
                if (excludeForeign)
                    continue;
            }
            monsters.Add((card.Code, card.Attack, card.Defense, (card.Position & Ocg.PosFaceUp) != 0,
                (card.Position & (Ocg.PosFaceUpAttack | Ocg.PosFaceDownAttack)) != 0, Math.Max(card.Level, card.Rank), card.Type,
                (card.Status & Ocg.StatusDisabled) != 0));
        }
        return monsters;
    }

    private SideSummary ReadSummary(int player)
    {
        var monsters = ReadSearchMonsters(player, excludeForeign: false, out int foreign);
        int setSpells = 0, faceUpSpells = 0;
        var spells = new List<(int Code, bool FaceUp)>();
        foreach (var card in QueryList(player, Ocg.LocSZone))
        {
            if (card == null)
                continue;
            bool faceUp = (card.Position & Ocg.PosFaceDown) == 0;
            if (faceUp) faceUpSpells++; else setSpells++;
            spells.Add((card.Code, faceUp));
        }
        List<int> Codes(uint location) => QueryCodes(player, location)
            .Where(code => code.HasValue).Select(code => code.GetValueOrDefault()).ToList();
        var hand = Codes(Ocg.LocHand);
        var grave = Codes(Ocg.LocGrave);
        var deck = Codes(Ocg.LocDeck);
        int Count(uint location)
        {
            lock (_core)
                return (int)_duel.QueryCount(player, location);
        }
        return new SideSummary(_lp[player], monsters, setSpells, faceUpSpells, hand.Count, deck.Count, deck,
            grave.Count, Count(Ocg.LocExtra), spells, hand, grave, foreign)
        {
            ExtraCodes = player == 0 && grave.Contains(BlackwingVayuTheEmblemOfHonor) ? ExtraDeckCodes(player) : Array.Empty<int>(),
            // Their Normal Summon is spent only in their own turn after they used it; on the other turn, their next one comes first.
            NormalSummonAvailable = !(TurnPlayer == player && NormalSummonUsed(player)),
        };
    }
}

/// <summary>One side of a search duel, for evaluating where a line ends.</summary>
internal sealed record SideSummary(
    int LifePoints,
    /// <summary>Negated = its effects are negated (STATUS_DISABLED: e.g. a Synchro summoned by Vayu).</summary>
    List<(int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated)> Monsters,
    int SetSpells,
    int FaceUpSpells,
    int Hand,
    int Deck,
    List<int> DeckCodes,
    int Grave,
    int Extra,
    List<(int Code, bool FaceUp)> SpellCodes,
    List<int> HandCodes,
    List<int> GraveCodes,
    /// <summary>Monsters on this side the other player owns (taken), counted in Monsters unless left out.</summary>
    int Foreign = 0)
{
    public IReadOnlyList<int> ExtraCodes { get; init; } = Array.Empty<int>();
    /// <summary>This side can still Normal Summon (or Set) before the other side's next turn: not its own turn with it used (DuelHost.NormalSummonUsed).</summary>
    public bool NormalSummonAvailable { get; init; } = true;
}

internal sealed record SearchChainLink(int Player, int CardId, int[] TargetCardIds);

/// <summary>Where search time goes (Stopwatch ticks, summed over all threads). For the search-bench command.</summary>
internal static class SearchTimings
{
    public static long Create, Puzzle, Deal, GeneratePuzzle, ToRoot, Playout, Evaluate, Dispose, Lines, Prompts, Engine, Messages, Cards;
    /// <summary>Stale-board checks that failed (DuelHost.CheckBoardVersion), and the first one's description. Read and cleared by the brain.</summary>
    public static long StaleBoards;
    public static string? FirstStaleBoard;
    public static bool MeasurePuzzleParse { get; set; }
    public static long PuzzleParse, PuzzleParseFailures;

    public static void Add(ref long counter, long value) => Interlocked.Add(ref counter, value);

    public static void Reset()
    {
        Create = Puzzle = Deal = GeneratePuzzle = ToRoot = Playout = Evaluate = Dispose =
            Lines = Prompts = Engine = Messages = Cards = PuzzleParse = PuzzleParseFailures = StaleBoards = 0;
        FirstStaleBoard = null;
    }

    public static string Report()
    {
        double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, Lines);
        return $"per line: deal {Ms(Deal):0.00} ms, generate position Lua {Ms(GeneratePuzzle):0.00} ms, create duel {Ms(Create):0.00} ms, load position {Ms(Puzzle):0.00} ms, " +
               $"play to our decision {Ms(ToRoot):0.00} ms, play out {Ms(Playout):0.00} ms, score {Ms(Evaluate):0.00} ms, " +
               $"dispose {Ms(Dispose):0.00} ms; {(double)Prompts / Math.Max(1, Lines):0.0} prompts per line ({Lines} lines)\n" +
               $"  of which: engine resolving {Ms(Engine):0.00} ms, reading its messages into prompts {Ms(Messages):0.00} ms, " +
               $"our code (policy, tree) {Ms(ToRoot + Playout - Engine - Messages):0.00} ms; {(double)Cards / Math.Max(1, Lines):0} cards laid out per line" +
               $"\n  stale-board checks failed: {StaleBoards}" + (FirstStaleBoard is { } stale ? $" (first: {stale})" : "") +
               (MeasurePuzzleParse ? $"\n  parse-only probe (encoding and compilation, body skipped): {Ms(PuzzleParse):0.00} ms per line, " +
                   $"{PuzzleParseFailures} probe failures; probe time is included in lines/s" : "");
    }
}
