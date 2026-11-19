using System.Text.Json;
using BotTest.Game.AI.Search;
using DuelSim.Data;

namespace DuelSim.Match;

/// <summary>
/// A position for search-bench --scenario, written by hand from a duel log, to see what the play search makes of it:
/// <code>
/// { "deck": "MachinaControl", "opponent": "Vayu", "lp": [7900, 600], "turn": 16, "phase": "main1",
///   "decision": "idle", "normalSummonUsed": true,
///   "ours":   { "monsters": [ { "id": 33420078, "zone": 2 }, { "id": 85087012, "zone": 3, "pos": "def" } ],
///               "spells": [ { "id": 41420027, "zone": 2, "set": true } ], "hand": [ 44330098, 5556499 ], "grave": [ ... ] },
///   "theirs": { "monsters": [ { "id": 26593852, "zone": 1 } ], "spells": [ { "zone": 0, "set": true } ], "handCount": 3, "grave": [ ... ] } }
/// </code>
/// Zones count from 0. A card without "id" (or id 0) is face-down and unknown: the search deals it from the opponent
/// deck's list like the deck read would. Our deck is our list minus every card of ours shown.
/// </summary>
internal static class SearchScenario
{
    private sealed class Card { public int Id { get; set; } public int Zone { get; set; } public string? Pos { get; set; } public bool Set { get; set; } public bool Attacked { get; set; } public bool Improper { get; set; } public int? EquippedTo { get; set; }
        /// <summary>Their unknown face-down monster: the deck read's likely cards, [[id, chance], ...] (as live EnemySetMonsterOdds).</summary>
        public List<double[]>? Likely { get; set; } }
    private sealed class Side
    {
        public List<Card> Monsters { get; set; } = new();
        public List<Card> Spells { get; set; } = new();
        public List<int> Hand { get; set; } = new();
        public int HandCount { get; set; }
        public List<int> Grave { get; set; } = new();
        public List<int> Banished { get; set; } = new();
    }
    private sealed class File
    {
        public string Deck { get; set; } = "";
        public string Opponent { get; set; } = "";
        public int[] Lp { get; set; } = { 8000, 8000 };
        public int Turn { get; set; } = 3;
        public string Phase { get; set; } = "main1";
        public string Decision { get; set; } = "idle";
        public bool NormalSummonUsed { get; set; }
        public Side Ours { get; set; } = new();
        public Side Theirs { get; set; } = new();
    }

    public sealed record Loaded(SearchView View, OmegaBotWrapper.Enum.BotState Root, string Deck, string Opponent);

    public static Loaded Load(string path, CardStore cards, ScriptStore scripts)
    {
        var file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new InvalidDataException($"{path}: empty scenario");
        var mine = DeckCode.Decode(file.Deck, MatchRunner.FindDeck("MasterDuel", file.Deck).Factory().DeckHash, cards, scripts);
        var theirs = DeckCode.Decode(file.Opponent, MatchRunner.FindDeck("MasterDuel", file.Opponent).Factory().DeckHash, cards, scripts);

        static uint Position(Card c, bool monster) =>
            monster ? c.Pos?.ToLowerInvariant() switch { "def" => SearchPosition.PosFaceUpDefense, "set" or "facedown" => SearchPosition.PosFaceDownDefense, _ => SearchPosition.PosFaceUpAttack }
                    : c.Set || c.Id == 0 ? SearchPosition.PosFaceDown : SearchPosition.PosFaceUp;
        static ViewCard ToView(Card c, bool monster, int owner) =>
            new(c.Id, c.Zone, Position(c, monster), owner, c.Attacked,
                // "equippedTo": the zone of the monster on the same side it's equipped to (a union, an Equip Spell).
                c.EquippedTo is int zone ? (owner, (int)SearchPosition.LocMZone, zone) : null, new(), new(), c.Improper);

        var view = new SearchView();
        view.LifePoints[0] = file.Lp[0];
        view.LifePoints[1] = file.Lp[1];
        view.NormalSummonUsed = file.NormalSummonUsed;
        view.BattlePhaseDone = file.Phase.Equals("main2", StringComparison.OrdinalIgnoreCase);
        view.InBattlePhase = file.Decision.Equals("battle", StringComparison.OrdinalIgnoreCase);
        view.FirstTurnOfDuel = file.Turn == 1;
        view.RealTurn = file.Turn;
        view.OwnMonsters.AddRange(file.Ours.Monsters.Select(c => ToView(c, true, 0)));
        view.OwnSpells.AddRange(file.Ours.Spells.Select(c => ToView(c, false, 0)));
        view.OwnHand.AddRange(file.Ours.Hand);
        view.OwnGrave.AddRange(file.Ours.Grave);
        view.OwnBanished.AddRange(file.Ours.Banished);
        view.EnemyMonsters.AddRange(file.Theirs.Monsters.Select(c => ToView(c, true, 1)));
        foreach (var c in file.Theirs.Monsters.Where(c => c.Id == 0 && c.Likely is { Count: > 0 }))
            view.EnemySetMonsterOdds[c.Zone] = c.Likely!.Select(p => ((int)p[0], p[1])).ToList();
        view.EnemySpells.AddRange(file.Theirs.Spells.Select(c => ToView(c, false, 1)));
        view.EnemyGrave.AddRange(file.Theirs.Grave);
        view.EnemyBanished.AddRange(file.Theirs.Banished);
        view.EnemyHandCount = Math.Max(file.Theirs.HandCount, file.Theirs.Hand.Count);
        view.EnemyExtraCount = theirs.Extra.Count;

        // Our deck: the list minus what's shown; our Extra Deck: its list minus synchros shown on the field or in the GY.
        var shown = file.Ours.Hand.Concat(file.Ours.Grave).Concat(file.Ours.Banished)
            .Concat(file.Ours.Monsters.Select(c => c.Id)).Concat(file.Ours.Spells.Select(c => c.Id)).ToList();
        List<int> Minus(IEnumerable<int> list)
        {
            var left = list.ToList();
            foreach (int id in shown)
                left.Remove(id);
            return left;
        }
        view.OwnDeck.AddRange(Minus(mine.Main));
        view.OwnExtra.AddRange(Minus(mine.Extra));

        // Their hidden cards: no guessing (as live, 2026-11-11): the hand cards the scenario names are known; the rest are
        // inert stand-ins and the evaluator's threat term.
        view.EnemyKnownHand.AddRange(file.Theirs.Hand);
        var root = view.InBattlePhase ? OmegaBotWrapper.Enum.BotState.SelectBattleCmd : OmegaBotWrapper.Enum.BotState.SelectIdleCmd;
        return new Loaded(view, root, file.Deck, file.Opponent);
    }
}
