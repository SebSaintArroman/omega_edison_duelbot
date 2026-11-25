using BotTest.Game.AI.Dataset;
using DuelSim.Data;
using DuelSim.Engine;
using DuelSim.Native;

namespace DuelSim.Match;

/// <summary>
/// One duel's share of the position dataset (user, 2026-11-25: "build up a database over thousands of games to see how well they predict
/// the winner before we start training"): at every turn start, the engine's real position (both players: life points, deck sizes, every
/// card's count by zone; never shown to the bots) and each seat's own scores (DeckBotBrain.RecordPosition, from what that seat can see),
/// written to dataset/positions.db with the result when the duel ends (PositionStore: the sim_* tables). The database lives outside out/,
/// so superseding runs never moves it; each duel names its run, brain versions and tuning files, so stale rows can be filtered.
/// </summary>
internal sealed class PositionDataset
{
    private readonly object _lock = new();
    private readonly Dictionary<(int Turn, int Seat), PositionScores> _views = new();
    private readonly List<(int Turn, int TurnPlayer, TruthSide[] Sides)> _truth = new();

    /// <summary>A seat's own scores at a turn start (from PositionSink).</summary>
    public void View(int seat, int turn, PositionScores scores)
    {
        lock (_lock)
            _views.TryAdd((turn, seat), scores);
    }

    /// <summary>The engine's real position as a turn starts (DuelHost.TurnStarted: the engine holds there until both bots have seen it).</summary>
    public void Truth(DuelHost host, int turn, int turnPlayer)
    {
        try
        {
            var sides = new[] { Side(host, 0), Side(host, 1) };
            lock (_lock)
                _truth.Add((turn, turnPlayer, sides));
        }
        catch { /* the dataset must never cost the duel */ }
    }

    private static TruthSide Side(DuelHost host, int player)
    {
        var cards = new List<(int Zone, int Code, int Count)>();
        void Add(int zone, IEnumerable<int> codes) => cards.AddRange(codes.GroupBy(c => c).Select(g => (zone, g.Key, g.Count())));
        var monsters = host.CardsIn(player, Ocg.LocMZone);
        var spells = host.CardsIn(player, Ocg.LocSZone);
        Add(PositionStore.Hand, host.CardsIn(player, Ocg.LocHand).Select(c => c.Code));
        Add(PositionStore.Monsters, monsters.Where(c => c.FaceUp).Select(c => c.Code));
        Add(PositionStore.SetMonsters, monsters.Where(c => !c.FaceUp).Select(c => c.Code));
        Add(PositionStore.Spells, spells.Where(c => c.FaceUp).Select(c => c.Code));
        Add(PositionStore.SetSpells, spells.Where(c => !c.FaceUp).Select(c => c.Code));
        Add(PositionStore.Grave, host.CardsIn(player, Ocg.LocGrave).Select(c => c.Code));
        Add(PositionStore.Banished, host.CardsIn(player, Ocg.LocRemoved).Select(c => c.Code));
        return new TruthSide(host.LifePointsOf(player), host.CountIn(player, Ocg.LocDeck), host.CountIn(player, Ocg.LocExtra), cards);
    }

    /// <summary>The duel's rows, written to the database with its result (seat 0 went first).</summary>
    public void Write(SimPaths paths, CardStore cards, string runFolder, int duel, string[] decks, int[] versions, string?[] tunings, DuelResult result)
    {
        SimDuel record;
        lock (_lock)
            record = new SimDuel(Path.GetFileName(runFolder), duel, decks, versions, tunings, result.IsDraw ? -1 : result.Winner, result.Reason,
                result.Turns, result.Error != null, _truth.ToList(), new Dictionary<(int, int), PositionScores>(_views));
        if (record.Truth.Count == 0)
            return;
        PositionStore.DatabasePath = Path.Combine(paths.Dataset, "positions.db");
        PositionStore.WriteSimDuel(record, code => cards.Get(code)?.Name);
    }
}
