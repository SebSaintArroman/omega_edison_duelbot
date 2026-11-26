using BotTest.Game.AI.Brains;
using BotTest.Web;
using DuelSim.Data;
using DuelSim.Engine;
using DuelSim.Match;

namespace DuelSim;

public static class Program
{
    private const string Usage = """
        DuelSim - plays BotTest's deck bots against each other offline, on the EDOPro rules engine.

        Usage:
          DuelSim run <deckA> <deckB> [--duels N] [--seed N] [--format MasterDuel] [--no-bot-logs] [--timeout-min N]
          DuelSim run ... [--out DIR] [--result FILE.json]   (also: a deck can be a tuned variant from tuning/, e.g. vayu-test-1)
          DuelSim run ... [--search MS] [--search-a MS] [--search-b MS] [--search-horizon N]   [--dead-time MS]   (MCTS: ms per decision, turns ahead; optional simulated dead-time search, off by default)
          DuelSim replay-check FILE.json|DIR [--lines N] [--ms MS] [--seed N] [--verbose]   (re-run a live turn's replay the bot saved on a miss: BotTest/duel-logs/replays)
          DuelSim search-bench <deckA> <deckB> | --scenario FILE.json  [--ms MS] [--threads N] [--horizon N] [--repeat N] [--trace KEY] [--profile-puzzle-parse]   (MCTS on one position: best line, lines/s)
          DuelSim evolve [--base Vayu] [--name vayu-test] [--generations N] [--duels 100] [--workers N]
                         [--mutations 8] [--strength 0.3] [--accept 0.55] [--baseline-every 5] [--baseline-duels 100]
                         [--seed N] [--keep-duels]
                                       tune the base deck's parameters: <name>-1 = champion, <name>-2 = challenger
          DuelSim run ... [--tuning-a FILE] [--tuning-b FILE]   (each side's own tuning values: bot-tuning.txt format, only the numbers it changes)
          DuelSim tuning [--list [FILE.json]]   tidy bot-tuning*.txt; --list: every tunable with its range and step (default tuning/params.json)
          DuelSim params <variant>     what a tuned variant changes vs its base deck
          DuelSim list                 list the decks that have bots
          DuelSim check                load the engine, card database and scripts, and report versions
          DuelSim decode <deck code>   dump the bytes of an Omega deck code

        Example:
          DuelSim run Vayu Vayu --duels 10

        Output goes to DuelSim/out/<run>/: summary.txt, and per duel a transcript plus both bots' duel logs.
        """;

    /// <summary>Lists every tunable: the static ones, each deck brain's own (each brain tuned once), every rule's hold cost.</summary>
    private static void RegisterAllTunables()
    {
        BotTest.Game.AI.Brains.Tuning.EnsureLoaded();
        foreach (var d in DeckCatalog.Definitions.Where(d => d.FormatId == "MasterDuel"))
            if (Tuning.BrainParams.FindBrain(d.Factory()) is { } brain)
                if (brain is BotTest.Game.AI.Brains.DeckBotBrain deckBrain) deckBrain.ApplyTuning(); else BotTest.Game.AI.Brains.Tuning.ApplyTo(brain);
        BotTest.Game.AI.Brains.RuleGates.DefaultCost("deck.heavy-storm"); // lists every rule's hold cost
        _ = BotTest.Game.AI.Brains.DeckExplosiveness.ByType.Count; // and every deck type's explosiveness
    }

    /// <summary>
    /// search-bench: the opponent's attacker odds from their real list (what the live deck read approximates): their unknown
    /// hand plus the draw holding a Level 4 or lower monster of at least each threshold's ATK, over their unseen cards.
    /// </summary>
    private static double[] BenchAttackerOdds(string opponent, BotTest.Game.AI.Search.SearchView view, CardStore cards, ScriptStore scripts)
    {
        int unknown = Math.Max(0, view.EnemyHandCount - view.EnemyKnownHand.Count) + 1;
        var list = DeckCode.Decode(opponent, MatchRunner.FindDeck("MasterDuel", opponent).Factory().DeckHash, cards, scripts);
        var unseen = list.Main.ToList();
        foreach (int id in view.EnemyGrave.Concat(view.EnemyBanished).Concat(view.EnemyKnownHand)
                     .Concat(view.EnemyMonsters.Select(c => c.Code)).Concat(view.EnemySpells.Select(c => c.Code)))
            unseen.Remove(id);
        int pool = Math.Max(1, unseen.Count);
        return BoardEvaluator.AttackerThresholds.Select(atk =>
        {
            int r = unseen.Count(id => cards.Get(id) is { } c && !c.IsExtraDeckMonster && (c.Type & 0x1) != 0 && c.Level <= 4 && c.Attack >= atk);
            double none = 1;
            for (int i = 0; i < Math.Min(unknown, pool); i++)
                none *= Math.Max(0, (double)(pool - r - i) / (pool - i));
            return 1 - none;
        }).ToArray();
    }

    private static string Option2(string[] args, string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (UnknownDeckException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var paths = SimPaths.Discover();
        var console = Console.Out;
        BotTest.Game.AI.Brains.Tuning.EnsureLoaded(); // bot-tuning.txt: every weight and threshold, before any bot or search reads one
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            console.WriteLine(Usage);
            return 0;
        }
        // No dead-time search (user, 2026-11-11: given up on; it never matched the board the turn started from): the turn-start
        // search (4 s in total on the first decision of each turn) prepares the turn, in DuelSim as live.
        // --dead-time MS brings back a simulated once-per-turn dead-time search for testing.
        BotTest.Game.AI.Brains.DeckBotBrain.DeadTimePlanning = false;
        BotTest.Game.AI.Brains.DeckBotBrain.SimulatedDeadTimeMs =
            int.TryParse(Option2(args, "--dead-time", ""), out int deadMs) && deadMs >= 0 ? deadMs : 0;

        switch (args[0])
        {
            case "list":
                foreach (var d in DeckCatalog.Definitions)
                    console.WriteLine($"{d.FormatId,-12} {d.DeckId,-24} {d.DeckName}");
                return 0;

            case "decode":
                foreach (var code in args.Skip(1))
                {
                    var bytes = DeckCode.Inflate(code);
                    console.WriteLine($"{bytes.Length} bytes, header {bytes[0]} {bytes[1]}: " +
                                      string.Join(", ", Enumerable.Range(0, (bytes.Length - 2) / 4).Select(i => BitConverter.ToInt32(bytes, 2 + i * 4))));
                }
                return 0;

            case "deck-edit":
            {
                // DuelSim deck-edit <code> [+id ...] [-id ...]: the deck code with those cards added / removed (one copy per
                // argument), then the new code decoded back as a check.
                var add = args.Skip(2).Where(a => a.StartsWith('+')).Select(a => int.Parse(a[1..])).ToList();
                var remove = args.Skip(2).Where(a => a.StartsWith('-')).Select(a => int.Parse(a[1..])).ToList();
                string edited = DeckCode.Edit(args[1], add, remove);
                var check = DeckCode.Inflate(edited);
                console.WriteLine(edited);
                console.WriteLine($"check: header {check[0]} {check[1]}: " +
                                  string.Join(", ", Enumerable.Range(0, (check.Length - 2) / 4).Select(i => BitConverter.ToInt32(check, 2 + i * 4))));
                return 0;
            }

            case "edison-data":
            {
                // Turns downloaded deck lists (Format Library, see README) into the bot's opponent-deck data: official
                // ids (alt arts / server ids resolved), counts per list, and each card's kind (M/S/T/X), so the bot needs
                // no card lookups mid-duel.
                var cards = CardStore.Load(paths.CardDb);
                using var input = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
                int Official(int id) => cards.Get(id) is { Alias: > 0 } c && cards.Get(c.Alias) != null ? c.Alias : id;
                var kinds = new SortedDictionary<int, string>();
                // M monster, F flip monster whose flip destroys (Ryko), f other flip monster, X Extra Deck monster, T trap,
                // Q quick-play spell, S other spell (how likely it is to be set).
                var flipDestroy = new System.Text.RegularExpressions.Regex(@"FLIP:[\s\S]*destroy", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string Kind(int id) => cards.Get(id) is not { } c ? "?"
                    : c.IsExtraDeckMonster ? "X"
                    : (c.Type & 0x1) != 0 ? ((c.Type & 0x200000) != 0 ? (flipDestroy.IsMatch(c.Text) ? "F" : "f") : "M")
                    : (c.Type & 0x4) != 0 ? "T" : (c.Type & 0x10000) != 0 ? "Q" : "S";
                // Spells/traps that can stop a push (our attack or summon): they remove, flip, bounce or neutralise a monster,
                // stop attacks, or stop the damage. Curated by name (card text matching was too noisy).
                var stopperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Mirror Force", "Torrential Tribute", "Dimensional Prison", "Bottomless Trap Hole", "Compulsory Evacuation Device",
                    "Sakuretsu Armor", "Raigeki Break", "Magic Cylinder", "Negate Attack", "Solemn Judgment", "Horn of Heaven",
                    "Book of Moon", "Enemy Controller", "Treacherous Trap Hole", "Karma Cut", "Phoenix Wing Wind Blast", "Waboku",
                    "Threatening Roar", "Scapegoat", "Gemini Spark", "Deck Devastation Virus", "Fiendish Chain", "Icarus Attack",
                    "Shrink", "Royal Oppression", "Hero Blast", "Offerings to the Doomed", "Telekinetic Power Well", "Dark Illusion",
                    "Gladiator Beast War Chariot", "Trap Hole", "Widespread Ruin", "Sakuretsu Armor", "Draining Shield",
                    "Spellbinding Circle", "Magical Arm Shield", "Interdimensional Matter Transporter",
                };
                bool Stopper(int id) => cards.Get(id) is { } c && (c.Type & 0x1) == 0 && stopperNames.Contains(c.Name);

                // How likely a main-deck monster is to be the one set face-down: Level 5+ never (needs a tribute); flip
                // monsters, floaters / recruiters and graveyard-effect monsters are the usual sets; a monster with a
                // "when Normal Summoned" effect or a beater (good ATK, low DEF) almost never; a wall sometimes.
                var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                var floaterText = new System.Text.RegularExpressions.Regex(
                    @"(destroyed|sent (from the field )?to the (gy|graveyard))[^.]*?:[^.]*?(special summon|add|draw)", rx);
                var graveText = new System.Text.RegularExpressions.Regex(
                    @"this card is in (your|the) (gy|graveyard)|banish this card[^.]*?from (your|the) (gy|graveyard)", rx);
                var normalSummonText = new System.Text.RegularExpressions.Regex(@"(when|if) this card is normal summoned", rx);
                // Used from the hand (Honest, Kuriboh, Battle Fader, D.D. Crow): kept in hand, not set.
                var handEffectText = new System.Text.RegularExpressions.Regex(
                    @"(discard|send) this card (from your hand )?to the (gy|graveyard)|special summon this card from your hand|this card in your hand", rx);
                double SetWeight(int id)
                {
                    if (cards.Get(id) is not { } c || (c.Type & 0x1) == 0 || c.IsExtraDeckMonster || c.Level >= 5)
                        return 0;
                    if ((c.Type & 0x200000) != 0) return 1.0;                 // flip
                    if (floaterText.IsMatch(c.Text)) return 1.0;              // floater / recruiter / replaces itself
                    if (graveText.IsMatch(c.Text)) return 0.8;                // works from the graveyard
                    if (handEffectText.IsMatch(c.Text)) return 0.02;          // used from the hand
                    if (normalSummonText.IsMatch(c.Text)) return 0.02;        // wants to be Normal Summoned
                    if (c.Attack >= 1600 && c.RealDefense < c.Attack) return 0.05; // beater
                    if (c.RealDefense >= 1500 && c.RealDefense > c.Attack) return 0.4; // wall
                    return 0.1;
                }
                // Input: the downloaded lists (an array; main / extra as id arrays), or the bot's data file itself (an object
                // with "decks"; main / extra as {id: count}), so the data can be rebuilt and extended without the download.
                // --with-bots adds every Master Duel bot list (user, 2026-11-10: "add all the bot lists to the central list of
                // decks ... don't remove the existing ones": their lists aren't standard, and the bots should meet many lists).
                var sources = new List<(string Type, int Id, int Placement, List<int> Main, List<int> Extra)>();
                static List<int> Ids(System.Text.Json.JsonElement deck, string section)
                {
                    var ids = new List<int>();
                    if (!deck.TryGetProperty(section, out var list))
                        return ids;
                    if (list.ValueKind == System.Text.Json.JsonValueKind.Array)
                        ids.AddRange(list.EnumerateArray().Select(item => item.GetInt32()));
                    else if (list.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var entry in list.EnumerateObject())
                            ids.AddRange(Enumerable.Repeat(int.Parse(entry.Name), entry.Value.GetInt32()));
                    return ids;
                }
                bool dataFile = input.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
                var inputDecks = dataFile ? input.RootElement.GetProperty("decks") : input.RootElement;
                foreach (var deck in inputDecks.EnumerateArray())
                    sources.Add((deck.GetProperty("type").GetString() ?? "?", deck.GetProperty("id").GetInt32(),
                        deck.TryGetProperty("placement", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt32() : 0,
                        Ids(deck, "main"), Ids(deck, "extra")));
                if (args.Contains("--with-bots"))
                {
                    // The bot decks under their Edison deck type (a variant counts as its type). Ids 900000+: not Format Library's.
                    var typeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Vayu"] = "Vayu Turbo", ["MachinaControl"] = "Machina Control", ["MachinaJinzo"] = "Machina Jinzo",
                        ["HeroFrog"] = "Hero Frog", ["Blackwing"] = "Blackwing", ["GladiatorControl"] = "Gladiator Beast",
                        // A Blackwing variant leaning into Vayu (user, 2026-11-10: "basically it's blackwings"): predicted as Blackwing.
                        ["BlackwingHybrid"] = "Blackwing",
                        // Not Machina Control: a Jinzo / Dark Armed Dragon push deck (user, 2026-11-10), its own type for the predictor.
                    };
                    var scriptStore = new ScriptStore(paths.Scripts, paths.CustomScripts);
                    int botId = 900000;
                    sources.RemoveAll(s => s.Id >= 900000); // rebuilt from the current bot lists
                    foreach (var def in DeckCatalog.Definitions.Where(d => d.FormatId == "MasterDuel"))
                    {
                        var list = DeckCode.Decode(def.DeckId, def.Factory().DeckHash, cards, scriptStore);
                        string type = typeOf.TryGetValue(def.DeckId, out var t) ? t : def.DeckId;
                        sources.Add((type, ++botId, 1, list.Main.ToList(), list.Extra.ToList()));
                        console.WriteLine($"bot list {def.DeckId} as {type}: {list.Main.Count} main, {list.Extra.Count} extra");
                    }
                }
                var decks = new List<object>();
                foreach (var source in sources)
                {
                    Dictionary<string, int> Counts(List<int> list)
                    {
                        var counts = new Dictionary<string, int>();
                        foreach (int raw in list)
                        {
                            int id = Official(raw);
                            kinds[id] = Kind(id);
                            counts[id.ToString()] = counts.GetValueOrDefault(id.ToString()) + 1;
                        }
                        return counts;
                    }
                    decks.Add(new
                    {
                        type = source.Type,
                        id = source.Id,
                        placement = source.Placement,
                        main = Counts(source.Main),
                        extra = Counts(source.Extra),
                    });
                }
                // Explosiveness per deck type (how likely a big turn that blows us out is, the longer the game goes): from
                // Format Library's deck-type category (optional third argument: {type: category}), with the "Multiple" ones
                // judged by hand. Used to push harder (take more risks) against explosive decks.
                var categories = args.Length > 3 && File.Exists(args[3])
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[3])) ?? new()
                    : new Dictionary<string, string>();
                var byCategory = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Combo"] = 0.85, ["Ramp"] = 0.75, ["Aggro"] = 0.6, ["Midrange"] = 0.45, ["Control"] = 0.3,
                };
                var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Vayu Turbo"] = 0.75, ["Machina Jinzo"] = 0.6, ["Hero Frog"] = 0.4, ["Diva Hero"] = 0.5, ["Diva Zombie"] = 0.55, ["Diva Hero Zombie"] = 0.55,
                };
                var explosiveness = decks.Select(d => (string)d.GetType().GetProperty("type")!.GetValue(d)!).Distinct()
                    .ToDictionary(type => type, type => byType.TryGetValue(type, out double v) ? v
                        : categories.TryGetValue(type, out var cat) && byCategory.TryGetValue(cat, out double c) ? c : 0.5);
                // Rebuilding from the data file: its values (from the categories file of the original download) stay.
                if (dataFile && input.RootElement.TryGetProperty("explosiveness", out var kept))
                    foreach (var type in kept.EnumerateObject())
                        explosiveness[type.Name] = type.Value.GetDouble();
                var output = new
                {
                    source = "formatlibrary.com (Edison), best-placing lists per main deck type",
                    explosiveness,
                    decks,
                    cards = kinds.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    names = kinds.Keys.ToDictionary(id => id.ToString(), id => cards.Get(id)?.Name ?? ""),
                    stoppers = kinds.Keys.Where(Stopper).ToList(),
                    monsterSetWeights = kinds.Keys.Where(id => SetWeight(id) > 0).ToDictionary(id => id.ToString(), SetWeight),
                };
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(output));
                console.WriteLine($"{decks.Count} decks, {kinds.Count} distinct cards ({kinds.Count(kv => kv.Value == "?")} unknown) -> {args[2]}");
                return 0;
            }

            case "gy-plays":
            {
                // Which cards in the Edison data count as graveyard fuel for the big-turn read (BotTest EffectEvaluator).
                var cards = CardStore.Load(paths.CardDb);
                using var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
                foreach (var card in data.RootElement.GetProperty("cards").EnumerateObject())
                    if (cards.Get(int.Parse(card.Name)) is { } c && BotTest.Game.AI.Brains.EffectEvaluator.HasGraveyardPlay(c.Text))
                        console.WriteLine(args.Contains("--text") ? $"{c.Name}: {c.Text.Replace("\r", "").Replace("\n", " ")}" : c.Name);
                return 0;
            }

            case "cards":
            {
                // A deck code's cards with their stats and text (for writing a new deck's bot).
                var cards = CardStore.Load(paths.CardDb);
                var scripts = new ScriptStore(paths.Scripts, paths.CustomScripts);
                var list = DeckCode.Decode("deck", args[1], cards, scripts, console.WriteLine);
                foreach (var (section, ids) in new[] { ("MAIN", list.Main), ("EXTRA", list.Extra), ("SIDE", list.Side) })
                {
                    console.WriteLine($"=== {section} ({ids.Count}) ===");
                    foreach (var group in ids.GroupBy(id => id))
                    {
                        var c = cards.Get(group.Key);
                        console.WriteLine($"{group.Count()}x {group.Key} {c?.Name ?? "?"} [type 0x{c?.Type:x} L{c?.Level} {c?.Attack}/{c?.RealDefense} attr {c?.Attribute} race {c?.Race}]");
                        console.WriteLine($"    {c?.Text.Replace("\r", "").Replace("\n", " ")}");
                    }
                }
                return 0;
            }

            case "effect-scores":
            {
                // DuelSim effect-scores <deck> [<deck>...]: each card's effect points (EffectEvaluator) and what the search's
                // scoring adds for them in card units, with the reasons: to check the effect values make sense.
                if (args.Length < 2 || !BotTest.Game.AI.Search.SearchHost.Available)
                {
                    console.WriteLine(args.Length < 2 ? "Usage: DuelSim effect-scores <deck> [<deck>...]" : $"search unavailable: {BotTest.Game.AI.Search.SearchHost.Problem}");
                    return 1;
                }
                var sc = BotTest.Game.AI.Search.SearchHost.Cards;
                var ss = BotTest.Game.AI.Search.SearchHost.Scripts;
                var values = new CardValues(Array.Empty<int>(), Array.Empty<int>());
                foreach (var name in args.Skip(1).Where(a => !a.StartsWith("--")))
                {
                    // A card id instead of a deck name: just that card.
                    var ids = int.TryParse(name, out int single)
                        ? new[] { single }
                        : DeckCode.Decode(name, MatchRunner.FindDeck("MasterDuel", name).Factory().DeckHash, sc, ss) is var list
                            ? list.Main.Concat(list.Extra).ToArray() : Array.Empty<int>();
                    console.WriteLine($"=== {name} ===");
                    console.WriteLine($"{"card",-42} {"ATK",5} {"held",5} {"field",5} {"grave",5}  {"tapered",7} {"corr",4} {"on field adds",13}  why (on the field)");
                    foreach (int id in ids.Distinct())
                    {
                        var v = values.Get(id, 0);
                        var record = sc.Get(id);
                        var (_, why) = BotTest.Game.AI.Brains.EffectEvaluator.ScoreOnFieldAsync(id, Array.Empty<int>()).GetAwaiter().GetResult();
                        double tapered = BoardEvaluator.Taper(v.Field);
                        double adds = 0.25 * tapered;
                        double corr = BotTest.Game.AI.Brains.CardRuleRegistry.CorrectionFor(id);
                        console.WriteLine($"{record?.Name ?? id.ToString(),-42} {record?.Attack,5} {v.Held,5:0.0} {v.Field,5:0.0} {v.Grave,5:0.0}  {tapered,7:0.00} {corr,4:0.#} {adds,13:0.00}  " +
                                          (v.Protects > 0 ? $"protects {v.Protects:0.0}; " : "") + why);
                        if (args.Contains("--grave") && v.Grave > 0)
                        {
                            var (graveScore, graveWhy) = BotTest.Game.AI.Brains.EffectEvaluator.ScoreInGraveAsync(id, Array.Empty<int>()).GetAwaiter().GetResult();
                            console.WriteLine($"    in the GY: {graveScore:0.0} ({graveWhy})");
                        }
                        if (args.Contains("--text") && record != null)
                            console.WriteLine($"    {record.Text.Replace("\r", "").Replace("\n", " ")}");
                    }
                }
                return 0;
            }

            case "replay-check":
                return DuelSim.Match.ReplayCheck.Run(args, console.WriteLine);

            case "search-bench":
            {
                // DuelSim search-bench <deckA> <deckB> [--lines N] [--threads N] [--seed N] [--horizon N] [--tree-turns N]: times the play search on an
                // opening position (A to play, 5 cards each) and shows where the time goes.
                bool scenario = args.Contains("--scenario");
                if (args.Length < 3 && !scenario)
                {
                    console.WriteLine("Usage: DuelSim search-bench <deckA> <deckB> [--lines N] [--threads N] [--seed N]  |  search-bench --scenario FILE.json [...]");
                    return 1;
                }
                if (!BotTest.Game.AI.Search.SearchHost.Available)
                {
                    console.WriteLine($"search unavailable: {BotTest.Game.AI.Search.SearchHost.Problem}");
                    return 1;
                }
                int lines = int.Parse(Option2(args, "--lines", "200"));
                int threads = int.Parse(Option2(args, "--threads", "1"));
                int seed = int.Parse(Option2(args, "--seed", "1"));
                var sc = BotTest.Game.AI.Search.SearchHost.Cards;
                var ss = BotTest.Game.AI.Search.SearchHost.Scripts;
                var root = OmegaBotWrapper.Enum.BotState.SelectIdleCmd;
                string benchDeck = scenario ? "" : args[1];
                string benchOpponent = scenario ? "" : args[2];
                IReadOnlyList<string>? usedEffects = null;
                BotTest.Game.AI.Search.SearchView view;
                if (scenario)
                {
                    var loaded = DuelSim.Match.SearchScenario.Load(Option2(args, "--scenario", ""), sc, ss);
                    view = loaded.View;
                    root = loaded.Root;
                    benchDeck = loaded.Deck;
                    benchOpponent = loaded.Opponent;
                    usedEffects = loaded.UsedEffects;
                    console.WriteLine($"scenario: {loaded.Deck} vs {loaded.Opponent}, {root}");
                    // --dump-lua FILE: the rebuilt position's puzzle script, to check what the rebuild carries over.
                    if (args.Contains("--dump-lua"))
                        File.WriteAllText(Option2(args, "--dump-lua", "position.lua"), view.Determinize(new Random(seed)).ToLua(DuelHost.PuzzleFlags));
                }
                else
                {
                var a = DeckCode.Decode(args[1], MatchRunner.FindDeck("MasterDuel", args[1]).Factory().DeckHash, sc, ss);
                var b = DeckCode.Decode(args[2], MatchRunner.FindDeck("MasterDuel", args[2]).Factory().DeckHash, sc, ss);
                var deal = new Random(seed);
                var mine = a.Main.OrderBy(_ => deal.Next()).ToList();
                view = new BotTest.Game.AI.Search.SearchView();
                view.LifePoints[0] = view.LifePoints[1] = 8000;
                view.OwnHand.AddRange(mine.Take(5));
                view.OwnDeck.AddRange(mine.Skip(5));
                view.OwnExtra.AddRange(a.Extra);
                view.EnemyHandCount = 5;
                view.EnemyExtraCount = b.Extra.Count;
                console.WriteLine($"hand: {string.Join(", ", view.OwnHand.Select(id => sc.Get(id)?.Name ?? id.ToString()))}");
                }
                int budgetMs = int.Parse(Option2(args, "--ms", "0"));
                var settings = new BotTest.Game.AI.Search.SearchSettings(BudgetMs: budgetMs > 0 ? budgetMs : 0, Threads: threads, MaxIterations: budgetMs > 0 ? int.MaxValue : lines,
                    Horizon: int.Parse(Option2(args, "--horizon", "2")),
                    TraceRoot: args.Contains("--trace") ? Option2(args, "--trace", "") : null, TraceLines: int.Parse(Option2(args, "--trace-lines", args.Contains("--trace") ? "3" : "0")),
                    // --focus KEY[,KEY] --focus-lines N: send lines to these root moves first, as the live stopping loop does for moves short of lines.
                    FocusRoots: args.Contains("--focus") ? Option2(args, "--focus", "").Split(',') : null,
                    FocusLines: int.Parse(Option2(args, "--focus-lines", "200")));
                // The deck brain's own goals and card values, as in a real search.
                var deckBrain = Tuning.BrainParams.FindBrain(MatchRunner.FindDeck("MasterDuel", benchDeck).Factory()) as BotTest.Game.AI.Brains.DeckBotBrain;
                var benchDef = MatchRunner.FindDeck("MasterDuel", benchDeck);
                Func<BotTest.Game.AI.Search.SearchBrain>? brains = deckBrain == null ? null
                    : () => new BotTest.Game.AI.Search.SearchBrain(deckBrain, benchDef.DeckName, benchDef.Factory().DeckHash, !args.Contains("--no-brain"));
                var evaluator = new BoardEvaluator(new CardValues(view.OwnGrave, view.EnemyGrave),
                    deckBrain == null ? null : deckBrain.SearchCardBonus,
                    Math.Max(0, view.EnemyHandCount - view.EnemyKnownHand.Count) + view.EnemySpells.Count(c => c.Code <= 0), 0.5)
                    {
                        AttackerOdds = BenchAttackerOdds(benchOpponent, view, sc, ss),
                        OwnDeck = view.OwnDeck, OwnHand = view.OwnHand, OwnGrave = view.OwnGrave, OwnBanished = view.OwnBanished, OwnMonsters = view.OwnMonsters.Select(c => c.Code).Where(c => c > 0).ToList(),
                        GraveSetup = deckBrain?.SearchGraveSetup,
                        RewardGladiatorBattles = deckBrain?.RewardGladiatorBattles == true,
                        BackrowDoomRule = deckBrain?.SearchBackrowDoomRule,
                        NextTurnValue = deckBrain == null ? null : deckBrain.SearchNextTurnValue,
                        // As live (DeadTimeSearch): damage dealt / taken counts from the life points at the decision.
                        TheirStartLp = view.LifePoints[1],
                        OurStartLp = view.LifePoints[0],
                        UnknownSets = view.EnemySpells.Count(c => c.Code <= 0),
                        // Their unknown set cards' trap odds (live: the deck read's): --mirror P --torrential P --other-stop P --icarus P.
                        MirrorForceChance = double.Parse(Option2(args, "--mirror", "0"), System.Globalization.CultureInfo.InvariantCulture),
                        TorrentialChance = double.Parse(Option2(args, "--torrential", "0"), System.Globalization.CultureInfo.InvariantCulture),
                        OtherStopChance = double.Parse(Option2(args, "--other-stop", "0"), System.Globalization.CultureInfo.InvariantCulture),
                        IcarusChance = double.Parse(Option2(args, "--icarus", "0"), System.Globalization.CultureInfo.InvariantCulture)
                    };
                // --hand-values: each of our cards' hand value at this position (card units), with its target effects' part.
                if (args.Contains("--hand-values"))
                {
                    console.WriteLine($"{"card",-42} {"hand",6} {"targets",8}");
                    foreach (int id in view.OwnHand.Concat(view.OwnDeck).Distinct())
                    {
                        var rec = sc.Get(id);
                        bool st = rec != null && (rec.Type & 0x1) == 0;
                        console.WriteLine($"{rec?.Name ?? id.ToString(),-42} {evaluator.HandCardValue(id, 0),6:0.00} {evaluator.TargetEffectValue(id, st),8:0.00}");
                    }
                    return 0;
                }
                var tree = new BotTest.Game.AI.Search.MctsTree { Label = "bench" };
                BotTest.Game.AI.Search.Mcts NewSearch() => new(view, tree, tree.Root, root, null, Array.Empty<int>(), settings, evaluator, brains)
                    { Suggestions = deckBrain == null ? null : deckBrain.PlanSuggestions, UsedEffects = usedEffects };
                int repeat = int.Parse(Option2(args, "--repeat", "1"));
                for (int r = 1; r < repeat; r++)
                {
                    // Each repeat grows the same tree further (as dead time does before a decision).
                    var warm = NewSearch().Run(seed + r);
                    console.WriteLine($"run {r}: {warm.Iterations} lines in {warm.Elapsed.TotalMilliseconds:0} ms, {warm.TreeVisits} in the tree, depth {warm.Depth}");
                }
                DuelSim.Engine.SearchTimings.MeasurePuzzleParse = args.Contains("--profile-puzzle-parse");
                DuelSim.Engine.SearchTimings.Reset();
                var outcome = NewSearch().Run(seed);
                console.WriteLine($"{outcome.Iterations} lines in {outcome.Elapsed.TotalMilliseconds:0} ms " +
                                  $"({outcome.Iterations / Math.Max(0.001, outcome.Elapsed.TotalSeconds):0} lines/s, {threads} thread(s)), " +
                                  $"{outcome.TreeVisits} in the tree, depth {outcome.Depth}, {tree.Decisions} decisions indexed, " +
                                  $"{outcome.Failures} failed{(outcome.FirstFailure != null ? $" ({outcome.FirstFailure})" : "")}");
                console.WriteLine(DuelSim.Engine.SearchTimings.Report());
                console.WriteLine($"  transpositions merged: {BotTest.Game.AI.Search.Mcts.Transpositions}, damaged nodes: {BotTest.Game.AI.Search.Mcts.DamagedNodes}");
                if (BotTest.Game.AI.Search.SearchBrain.Decisions + BotTest.Game.AI.Search.SearchBrain.Failures > 0)
                    console.WriteLine($"  our brain in the lines: {BotTest.Game.AI.Search.SearchBrain.Decisions} decisions, " +
                        $"{BotTest.Game.AI.Search.SearchBrain.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, BotTest.Game.AI.Search.SearchBrain.Decisions):0.00} ms each, " +
                        $"{BotTest.Game.AI.Search.SearchBrain.Failures} failed, board reads kept {BotTest.Game.AI.Search.SearchBrain.BoardReadsKept} / " +
                        $"fresh {BotTest.Game.AI.Search.SearchBrain.BoardReadsFresh}" +
                        (BotTest.Game.AI.Search.SearchBrain.FirstFailure is string firstFailure ? $" (first: {firstFailure})" : ""));
                console.WriteLine($"  the brain's holds: {BotTest.Game.AI.Brains.DeckBotBrain.HoldsRecorded} recorded in its decisions, " +
                                  $"{BotTest.Game.AI.Search.Mcts.HoldsMatched} matched an answer at a tree node, {BotTest.Game.AI.Search.Mcts.HoldsCharged} charged to our lines' steps");
                if (BotTest.Game.AI.Brains.DeckBotBrain.HoldsByRule.Count > 0)
                    console.WriteLine($"  by rule: {string.Join(", ", BotTest.Game.AI.Brains.DeckBotBrain.HoldsByRule.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"))}");
                if (outcome.RootPrior is string rootPrior)
                    console.WriteLine($"  the brain's own pick at the root: {outcome.Stat(rootPrior)?.Label ?? rootPrior}");
                if (outcome.RootHolds is { Count: > 0 } rootHolds)
                    console.WriteLine($"  held at the root: {string.Join(", ", rootHolds.Select(kv => $"{kv.Key} ({kv.Value.Rule}, {kv.Value.Cost:0.#})"))}");
                if (outcome.PositionMismatch != null)
                    console.WriteLine($"WARNING: {outcome.PositionMismatch}");
                foreach (var line in outcome.Traces ?? new List<string>())
                    console.WriteLine($"--- traced line ---\n{line}");
                if (outcome.BestLine is { Count: > 0 } best)
                    console.WriteLine($"best line: {string.Join(" -> ", best.Select(s => s.Label))}");
                foreach (var stat in outcome.Root.Take(10))
                {
                    var plan = outcome.Plans?.GetValueOrDefault(stat.Key);
                    console.WriteLine($"  [{stat.Key}]");
                    console.WriteLine($"  {stat.Label,-50} score {BoardEvaluator.CuPm(stat.Mean, stat.StdErr)} card units  ({stat.Visits} lines" +
                                      (double.IsNaN(stat.Average) ? "" : $", lines average {BoardEvaluator.Cu(stat.Average)}") +
                                      (double.IsNaN(stat.Unexplored) ? "" : $", best unexplored follow-up {BoardEvaluator.Cu(stat.Unexplored)}") + ")" +
                                      (plan is { Count: > 0 } ? $"  then: {string.Join(" -> ", plan.Select(p => p.Label))}" : "") +
                                      outcome.OfferNote(stat.Key));
                }
                // Moves of the decision no line ever had in its own list (so no line through them at all).
                foreach (var key in (outcome.RootMissing?.Keys ?? Enumerable.Empty<string>()).Where(k => outcome.Root.All(s => s.Key != k)))
                    console.WriteLine($"  [{key}] never in the tree{outcome.OfferNote(key)}");
                // --dump-tree [MINLINES]: every tree node with at least MINLINES lines (default 30), to depth --dump-depth (default 8):
                // its lines' average, its score (MctsNode.LineValue: the best explored follow-up at our decisions) and how often it was on offer.
                if (args.Contains("--dump-tree"))
                {
                    int at = Array.IndexOf(args, "--dump-tree");
                    int minLines = at + 1 < args.Length && int.TryParse(args[at + 1], out int ml) ? ml : 30;
                    int maxDepth = int.Parse(Option2(args, "--dump-depth", "8"));
                    int lvv = BotTest.Game.AI.Search.Mcts.LineValueVisits;
                    void Dump(BotTest.Game.AI.Search.MctsNode node, int depth)
                    {
                        if (depth > maxDepth)
                            return;
                        List<(string Key, BotTest.Game.AI.Search.MctsNode Child, string Label)> kids;
                        lock (node.Lock)
                            kids = node.Children.Select(kv => (kv.Key, kv.Value, node.LabelOf(kv.Key, kv.Value))).ToList();
                        foreach (var (key, child, label) in kids.Where(k => k.Child.Visits >= minLines).OrderByDescending(k => k.Child.Visits))
                        {
                            double share = child.Available / (double)Math.Max(1, node.Visits - child.ParentVisitsAtCreation);
                            string who = node.ChildPlayer == 0 ? "us" : node.ChildPlayer == 1 ? "them" : "?";
                            console.WriteLine($"{new string(' ', depth * 2)}- [{who}] {label}  n={child.Visits}  avg {BoardEvaluator.Cu(child.Mean)}  " +
                                              $"score {BoardEvaluator.Cu(child.LineValue(lvv))}  on offer {share:0.00}");
                            Dump(child, depth + 1);
                        }
                    }
                    console.WriteLine("--- tree ---");
                    Dump(tree.Root, 0);
                }
                return 0;
            }

            case "tuning":
            {
                // DuelSim tuning: lists every tunable number in bot-tuning.txt (the static ones, each deck brain's own, every rule's
                // hold cost); numbers already in the file keep their values. Run it after adding numbers to the code.
                RegisterAllTunables();
                BotTest.Game.AI.Brains.Tuning.Tidy();
                console.WriteLine($"tuning file: {BotTest.Game.AI.Brains.Tuning.FilePath}");
                // --list FILE: every tunable number for self-play tuning (key, kind, value, range, step), as JSON.
                if (args.Contains("--list"))
                {
                    int at = Array.IndexOf(args, "--list");
                    string file = at + 1 < args.Length ? args[at + 1] : Path.Combine(paths.Tuning, "params.json");
                    var entries = BotTest.Game.AI.Brains.Tuning.Entries();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                    File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(entries, Newtonsoft.Json.Formatting.Indented,
                        new Newtonsoft.Json.Converters.StringEnumConverter()));
                    console.WriteLine($"{entries.Count} tunable numbers ({string.Join(", ", entries.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}"))}) -> {file}");
                }
                return 0;
            }

            case "check":
            {
                var (major, minor) = OcgDuel.Version();
                console.WriteLine($"ocgcore API {major}.{minor}");
                var cards = CardStore.Load(paths.CardDb);
                console.WriteLine($"cards: {cards.Count}");
                var scripts = new ScriptStore(paths.Scripts, paths.CustomScripts);
                console.WriteLine($"constant.lua: {(scripts.Read("constant.lua")?.Length ?? 0)} bytes");
                foreach (var d in DeckCatalog.Definitions.Where(d => d.FormatId == "MasterDuel"))
                {
                    var list = DeckCode.Decode(d.DeckId, d.Factory().DeckHash, cards, scripts, console.WriteLine);
                    console.WriteLine($"{d.DeckId}: {list.Main.Count} main, {list.Extra.Count} extra, {list.Side.Count} side");
                }
                return 0;
            }

            case "run":
            {
                if (args.Length < 3)
                {
                    console.WriteLine(Usage);
                    return 1;
                }
                string Option(string name, string fallback)
                {
                    int i = Array.IndexOf(args, name);
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
                }
                var options = new MatchOptions(
                    DeckA: args[1],
                    DeckB: args[2],
                    Duels: int.Parse(Option("--duels", "1")),
                    Seed: int.Parse(Option("--seed", Environment.TickCount.ToString())),
                    Format: Option("--format", "MasterDuel"),
                    BotLogs: !args.Contains("--no-bot-logs"),
                    DuelTimeout: TimeSpan.FromMinutes(double.Parse(Option("--timeout-min", "20"))),
                    OutputRoot: args.Contains("--out") ? Option("--out", "") : null,
                    ResultFile: args.Contains("--result") ? Option("--result", "") : null,
                    // Play search is on by default (DeckBotBrain.DefaultSearchBudgetMs). --search MS sets it for both decks (0 = off),
                    // --search-a / --search-b MS for one of them.
                    SearchMsA: int.TryParse(Option("--search-a", Option("--search", "")), out int sa) ? sa : null,
                    SearchMsB: int.TryParse(Option("--search-b", Option("--search", "")), out int sb) ? sb : null,
                    ThinkMsB: int.Parse(Option("--think-b", "0")),
                    TuningA: args.Contains("--tuning-a") ? BotTest.Game.AI.Brains.TuningProfile.Load(Option("--tuning-a", ""), console.WriteLine) : null,
                    TuningB: args.Contains("--tuning-b") ? BotTest.Game.AI.Brains.TuningProfile.Load(Option("--tuning-b", ""), console.WriteLine) : null);
                if (options.TuningA != null || options.TuningB != null)
                {
                    // Every brain's numbers and rule costs registered, so a misspelt key in a profile is caught before the duels.
                    RegisterAllTunables();
                    foreach (var (side, profile) in new[] { ("A", options.TuningA), ("B", options.TuningB) })
                        if (profile != null)
                        {
                            console.WriteLine($"deck {side} plays with {profile.Name}: {profile.Values.Count} value(s) of its own");
                            foreach (string unknown in profile.UnknownKeys())
                                console.WriteLine($"  !! {profile.Name}: no tunable '{unknown}' (renamed or removed?) - ignored");
                        }
                }
                if (args.Contains("--search-horizon"))
                    BotTest.Game.AI.Brains.DeckBotBrain.DefaultSearchHorizon = int.Parse(Option("--search-horizon", "2"));

                var cards = CardStore.Load(paths.CardDb);
                var scripts = new ScriptStore(paths.Scripts, paths.CustomScripts);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                var runner = new MatchRunner(paths, cards, scripts, console);
                var outcomes = await runner.RunAsync(options, cts.Token);
                return outcomes.Any(o => o.Result.Error != null) ? 2 : 0;
            }

            case "evolve":
            {
                string Opt(string name, string fallback)
                {
                    int i = Array.IndexOf(args, name);
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
                }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var evolve = new Tuning.EvolveOptions(
                    BaseDeck: Opt("--base", "Vayu"),
                    Name: Opt("--name", "vayu-test"),
                    Generations: int.Parse(Opt("--generations", "0")),
                    Duels: int.Parse(Opt("--duels", "100")),
                    Workers: int.Parse(Opt("--workers", Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)).ToString())),
                    Mutations: int.Parse(Opt("--mutations", "8")),
                    Strength: double.Parse(Opt("--strength", "0.3"), inv),
                    Accept: double.Parse(Opt("--accept", "0.55"), inv),
                    BaselineEvery: int.Parse(Opt("--baseline-every", "5")),
                    BaselineDuels: int.Parse(Opt("--baseline-duels", "100")),
                    Seed: int.Parse(Opt("--seed", "1")),
                    KeepDuels: args.Contains("--keep-duels"),
                    Format: Opt("--format", "MasterDuel"));
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); console.WriteLine("stopping after the current match..."); };
                return await new Tuning.Evolver(paths, evolve, console).RunAsync(cts.Token);
            }

            case "params":
            {
                // params <variant>: what it changes vs its base deck's defaults.
                if (args.Length < 2 || Tuning.Variant.TryLoad(paths, args[1]) is not { } variant)
                {
                    console.WriteLine($"No variant '{(args.Length > 1 ? args[1] : "")}' in {paths.Tuning}");
                    return 1;
                }
                var deck = MatchRunner.FindDeck(Option2(args, "--format", "MasterDuel"), variant.Base);
                var brain = Tuning.BrainParams.FindBrain(deck.Factory())!;
                var defaults = Tuning.BrainParams.Read(brain);
                console.WriteLine($"{args[1]} (based on {variant.Base}): {variant.Note}");
                var diff = Tuning.BrainParams.Diff(defaults, variant.Params);
                console.WriteLine(diff.Count == 0 ? "  same as the defaults" : "  " + string.Join(Environment.NewLine + "  ", diff));
                return 0;
            }

            default:
                console.WriteLine(Usage);
                return 1;
        }
    }
}
