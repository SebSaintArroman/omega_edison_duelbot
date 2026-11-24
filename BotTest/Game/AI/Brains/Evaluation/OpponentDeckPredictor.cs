using System.Reflection;
using System.Text.Json;
using OmegaBotWrapper;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Reads the opponent's deck from real Edison deck lists (Data/edison-decks.json: the best-placing lists of each main
    /// Edison deck type, from Format Library; built with DuelSim's edison-data command). Every card they show narrows
    /// the lists down: a list that can't hold what we've seen (more copies than it runs) loses weight sharply. From the
    /// lists still in the running:
    ///  - which deck type they're on;
    ///  - what each face-down spell/trap is likely to be: each list's spells/traps minus the ones already seen (in the
    ///    graveyard, banished, face-up: those can't be the set card) and minus the ones known to be in their hand. A card
    ///    set after we saw their hand is most likely one of the spells/traps we saw there, unless they've drawn since;
    ///  - the chance a given card is in their hand.
    /// So against Frogs (no Dimensional Prison in any list) a set card is almost surely not D-Prison, but maybe a Raigeki
    /// Break. Only public information is used, plus hand cards revealed to us.
    /// </summary>
    public sealed class OpponentDeckPredictor
    {
        [Tunable("Deck read: how likely a quick-play spell is to be the card they set, against a trap (1).")]
        public static readonly Tuned<double> SetWeightQuickPlay = new(0.5);
        [Tunable("Deck read: how likely any other spell is to be the card they set, against a trap (1).")]
        public static readonly Tuned<double> SetWeightSpell = new(0.1);
        [Tunable("Deck read (no per-card data): how likely a plain monster is to be the one they set, against a flip monster (1).")]
        public static readonly Tuned<double> SetWeightPlainMonster = new(0.25);
        [Tunable("Deck read: the explosiveness of a deck type neither DeckExplosiveness nor the deck data rates.")]
        public static readonly Tuned<double> DefaultTypeExplosiveness = new(0.5);

        /// <summary>Forgets everything about the opponent (new duel).</summary>
        public void Clear() => ClearLists();

        private sealed record ListDeck(string Type, Dictionary<int, int> Main, Dictionary<int, int> Extra, int MainSize);

        private static readonly Lazy<(List<ListDeck> Decks, Dictionary<int, char> Kinds)> Data = new(LoadData);

        /// <summary>Weight of a list per copy it can't account for (we saw more copies than it runs). Small, not zero: lists tech.</summary>
        [Tunable("Weight of a list per copy it can't account for (we saw more copies than it runs). Small, not zero: lists tech.")]
        public static readonly Tuned<double> MismatchPenalty = new(0.05);

        /// <summary>Whether the deck-list data is loaded (otherwise callers use their flat defaults).</summary>
        public static bool HasListData => Data.Value.Decks.Count > 0;

        private static (List<ListDeck>, Dictionary<int, char>) LoadData()
        {
            var decks = new List<ListDeck>();
            var kinds = new Dictionary<int, char>();
            try
            {
                var assembly = typeof(OpponentDeckPredictor).Assembly;
                string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("edison-decks.json", StringComparison.OrdinalIgnoreCase));
                if (name == null)
                    return (decks, kinds);
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var doc = JsonDocument.Parse(stream);
                foreach (var card in doc.RootElement.GetProperty("cards").EnumerateObject())
                    kinds[int.Parse(card.Name)] = card.Value.GetString() is { Length: > 0 } k ? k[0] : '?';
                if (doc.RootElement.TryGetProperty("explosiveness", out var explosive))
                    foreach (var type in explosive.EnumerateObject())
                        ExplosivenessByType[type.Name] = type.Value.GetDouble();
                if (doc.RootElement.TryGetProperty("monsterSetWeights", out var setWeights))
                    foreach (var monster in setWeights.EnumerateObject())
                        MonsterSetWeights[int.Parse(monster.Name)] = monster.Value.GetDouble();
                if (doc.RootElement.TryGetProperty("stoppers", out var stoppers))
                    foreach (var stopper in stoppers.EnumerateArray())
                        StopperIds.Add(stopper.GetInt32());
                if (doc.RootElement.TryGetProperty("names", out var names))
                    foreach (var card in names.EnumerateObject())
                        if (card.Value.GetString() is { Length: > 0 } cardName)
                            CardNames.Set(int.Parse(card.Name), cardName);
                foreach (var deck in doc.RootElement.GetProperty("decks").EnumerateArray())
                {
                    Dictionary<int, int> Counts(string section) =>
                        deck.GetProperty(section).EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
                    var main = Counts("main");
                    decks.Add(new ListDeck(deck.GetProperty("type").GetString() ?? "?", main, Counts("extra"), main.Values.Sum()));
                }
            }
            catch
            {
                decks.Clear();
            }
            return (decks, kinds);
        }

        /// <summary>Spells/traps that can stop a push (remove, flip, bounce or neutralise a monster, stop attacks or the damage).</summary>
        private static readonly HashSet<int> StopperIds = new();

        /// <summary>
        /// How likely each main-deck monster is to be the one set face-down (from its level, stats and text): Level 5+
        /// never; flips, floaters / recruiters, graveyard-effect monsters the usual sets; "when Normal Summoned" monsters and
        /// beaters almost never. Built by DuelSim's edison-data.
        /// </summary>
        private static readonly Dictionary<int, double> MonsterSetWeights = new();

        /// <summary>How explosive each deck type is (0-1: Combo / Ramp high, Control low), from Format Library's categories.</summary>
        private static readonly Dictionary<string, double> ExplosivenessByType = new();

        /// <summary>
        /// How explosive their deck is (0-1): each deck type's explosiveness weighted by how likely they're on it. The more
        /// explosive, the likelier a big turn that blows us out the longer the game goes. Null without list data.
        /// </summary>
        public double? Explosiveness() =>
            HasListData ? LikelyTypes(int.MaxValue).Sum(t => t.Chance *
                (DeckExplosiveness.ByType.TryGetValue(t.Type, out double tuned) ? tuned : ExplosivenessByType.GetValueOrDefault(t.Type, DefaultTypeExplosiveness))) : null;

        private static bool IsSpellTrap(int id) => Data.Value.Kinds.TryGetValue(id, out char k) && k is 'S' or 'Q' or 'T';

        /// <summary>
        /// How likely a card of this kind is to be the one set face-down, relative to a trap: traps are set to be used,
        /// quick-play spells (MST) often, other spells rarely (a bluff at most). Tune from logs.
        /// </summary>
        private static double SetWeight(int id) =>
            Data.Value.Kinds.TryGetValue(id, out char k) ? k switch { 'T' => 1.0, 'Q' => SetWeightQuickPlay, 'S' => SetWeightSpell, _ => 0 } : 0;
        private static bool IsExtra(int id) => Data.Value.Kinds.TryGetValue(id, out char k) && k == 'X';

        /// <summary>
        /// The id the lists use for a card: the server's own ids (pre-errata copies, official id + 20) and alt arts map
        /// to the official one.
        /// </summary>
        public static int Normalize(int id)
        {
            var kinds = Data.Value.Kinds;
            if (id <= 0 || kinds.ContainsKey(id))
                return id;
            if (CardDatabase.TryGetCached(id, out var info) && info.Alias > 0 && kinds.ContainsKey(info.Alias))
                return info.Alias;
            if (kinds.ContainsKey(id - 20))
                return id - 20;
            return id;
        }

        // Most copies of each card seen at once in their public zones (field face-up, graveyard, banished).
        private readonly Dictionary<int, int> _seen = new();
        // Cards revealed in their hand that we haven't seen leave it yet.
        private readonly Dictionary<int, int> _knownHand = new();
        private int _stamp;
        private int _revealStamp = -1;
        private int _drawsSinceReveal;
        private int _lastHandCount = -1;
        // Their public cards (face-up field, graveyard, banished) by id at the last read, their hand size and face-down count:
        // to see a card go back to their hand.
        private Dictionary<int, int>? _lastPublic, _lastField;
        private int _lastHand, _lastFaceDown;
        // Their face-down cards we know for certain (set from a fully known hand with only one candidate), by zone.
        private readonly Dictionary<(bool Monster, int Zone), int> _knownSets = new();

        /// <summary>Their face-down card in this zone when we know it for certain, else null.</summary>
        public int? KnownSet(bool monster, int zone) => _knownSets.TryGetValue((monster, zone), out int id) ? id : null;

        // One new set zone, and the known hand holds only one kind of card that fits it (spell/trap or monster): that card.
        private void TryPinSet(List<int> newZones, bool monster)
        {
            if (newZones.Count != 1)
                return;
            bool Fits(int id) => Data.Value.Kinds.TryGetValue(id, out char k) && (monster ? k is 'M' or 'F' or 'f' : k is 'S' or 'Q' or 'T');
            var candidates = _knownHand.Where(kv => kv.Value > 0 && Fits(kv.Key)).Select(kv => kv.Key).ToList();
            if (candidates.Count != 1)
                return;
            int id = candidates[0];
            _knownSets[(monster, newZones[0])] = id;
            if (_knownHand[id] > 1) _knownHand[id]--; else _knownHand.Remove(id);
        }
        // When each of their face-down spell/trap zones became set (stamp), to tell sets made after a reveal.
        private readonly Dictionary<int, int> _setZoneStamp = new();
        // ...and each face-down monster zone.
        private readonly Dictionary<int, int> _monsterSetStamp = new();
        private Dictionary<ListDeck, double>? _weights;
        private string? _leadingType;

        private void ClearLists()
        {
            _seen.Clear();
            _knownHand.Clear();
            _stamp = 0;
            _revealStamp = -1;
            _drawsSinceReveal = 0;
            _lastHandCount = -1;
            _lastPublic = null;
            _lastField = null;
            _knownSets.Clear();
            _setZoneStamp.Clear();
            _monsterSetStamp.Clear();
            _weights = null;
            _leadingType = null;
            _searched = null;
            _searchStamp = -1;
        }

        /// <summary>
        /// Updates what we've seen of their deck from a field read. Returns a note when the deck type we think they're on
        /// changes (for the log), else null.
        /// </summary>
        public string? ObserveField(CardInfo enemySide)
        {
            if (!HasListData)
                return null;
            _stamp++;
            var now = new Dictionary<int, int>();
            void Count(int id)
            {
                if (id <= 0)
                    return;
                id = Normalize(id);
                now[id] = now.GetValueOrDefault(id) + 1;
            }
            var field = new Dictionary<int, int>(); // face-up field cards by id
            var monsterZones = enemySide.MonsterZones ?? Array.Empty<CardArray>();
            for (int z = 0; z < monsterZones.Length; z++)
            {
                var ids = monsterZones[z].Cards ?? Array.Empty<int>();
                if (ids.Length > 0 && ids[0] > 0)
                {
                    Count(ids[0]);
                    field[Normalize(ids[0])] = field.GetValueOrDefault(Normalize(ids[0])) + 1;
                    _monsterSetStamp.Remove(z);
                }
                else if (ids.Length > 0)
                {
                    if (!_monsterSetStamp.ContainsKey(z))
                        _monsterSetStamp[z] = _stamp;
                }
                else
                    _monsterSetStamp.Remove(z);
            }
            var spellZones = enemySide.SpellZones ?? Array.Empty<CardArray>();
            for (int z = 0; z < spellZones.Length; z++)
            {
                var ids = spellZones[z].Cards ?? Array.Empty<int>();
                if (ids.Length > 0 && ids[0] > 0)
                {
                    Count(ids[0]);
                    field[Normalize(ids[0])] = field.GetValueOrDefault(Normalize(ids[0])) + 1;
                    _setZoneStamp.Remove(z);
                }
                else if (ids.Length > 0)
                {
                    if (!_setZoneStamp.ContainsKey(z))
                        _setZoneStamp[z] = _stamp;
                }
                else
                    _setZoneStamp.Remove(z);
            }
            foreach (int id in enemySide.Grave ?? Array.Empty<int>())
                Count(id);
            foreach (int id in enemySide.Banish ?? Array.Empty<int>())
                Count(id);

            bool changed = false;
            int hand = enemySide.Hand?.Length ?? 0;
            int faceDown = monsterZones.Count(z => (z.Cards ?? Array.Empty<int>()) is { Length: > 0 } ids && ids[0] <= 0) +
                           spellZones.Count(z => (z.Cards ?? Array.Empty<int>()) is { Length: > 0 } ids && ids[0] <= 0);
            if (_lastPublic != null)
            {
                foreach (var (id, count) in now)
                {
                    int gained = count - _lastPublic.GetValueOrDefault(id);
                    // One of the unseen search's candidates out in the open: most likely the searched card itself.
                    if (gained > 0 && _searched != null && _searched.Contains(id))
                        _searched = null;
                    // A copy out in the open again: a known hand card of that name is the likeliest source.
                    if (gained > 0 && _knownHand.TryGetValue(id, out int inHand))
                    {
                        int used = Math.Min(inHand, gained);
                        if (inHand - used > 0) _knownHand[id] = inHand - used; else _knownHand.Remove(id);
                    }
                }
                // Cards that left their public zones altogether while their hand grew went back to it (Grand Mole's own bounce,
                // Compulsory Evacuation Device, Brionac, a card taken back from the graveyard: user, 2026-11-11): they stay
                // known in hand until they show up again. Not Extra Deck monsters (they go back to the Extra Deck). (Not offset by
                // zones turning to id 0: live Omega shows an empty zone as id 0 like a face-down card, so a card bounced to the
                // hand looked like a Book of Moon and was missed: seen 2026-11-11, Honest.)
                // The hand's change is offset by what they played from it in the same step: a face-up card new to their field that
                // didn't come from their graveyard / banished (seen 2026-11-11: Honest bounced itself while Harvest Angel was
                // summoned and Valhalla played, so the hand shrank and the bounce was missed).
                int fromHand = 0;
                if (_lastField != null)
                    foreach (var (id, count) in field)
                    {
                        int arrived = count - _lastField.GetValueOrDefault(id);
                        int offPile = _lastPublic.GetValueOrDefault(id) - _lastField.GetValueOrDefault(id) -
                                      (now.GetValueOrDefault(id) - count); // fewer in graveyard / banished than before
                        if (arrived > 0)
                            fromHand += Math.Max(0, arrived - Math.Max(0, offPile));
                    }
                int grew = hand - _lastHand + fromHand;
                if (grew > 0)
                    foreach (var (id, count) in _lastPublic)
                    {
                        int lost = count - now.GetValueOrDefault(id);
                        if (lost <= 0 || Data.Value.Kinds.TryGetValue(id, out char kind) && kind == 'X')
                            continue;
                        int back = Math.Min(lost, grew);
                        _knownHand[id] = _knownHand.GetValueOrDefault(id) + back;
                        grew -= back;
                        if (grew <= 0)
                            break;
                    }
                // A card set from a hand we know in full, when only one card in it could be that set: it's known on the field
                // (user, 2026-11-11: "if you've seen my hand and there aren't any unknowns and it's the only card I could have
                // played then it should remain known on the field"). It leaves the known hand.
                int knownCount = _knownHand.Values.Sum();
                if (knownCount >= _lastHand && _lastHand > hand)
                {
                    TryPinSet(_setZoneStamp.Where(kv => kv.Value == _stamp).Select(kv => kv.Key).ToList(), monster: false);
                    TryPinSet(_monsterSetStamp.Where(kv => kv.Value == _stamp).Select(kv => kv.Key).ToList(), monster: true);
                }
            }
            // A pinned set card that's gone from its zone (flipped, destroyed) is forgotten there.
            foreach (var key in _knownSets.Keys.Where(k => !(k.Monster ? _monsterSetStamp : _setZoneStamp).ContainsKey(k.Zone)).ToList())
                _knownSets.Remove(key);
            _lastPublic = now;
            _lastField = field;
            _lastHand = hand;
            _lastFaceDown = faceDown;
            foreach (var (id, count) in now)
            {
                if (count <= _seen.GetValueOrDefault(id))
                    continue;
                _seen[id] = count;
                changed = true;
            }

            if (_revealStamp >= 0 && _lastHandCount >= 0 && hand > _lastHandCount)
                _drawsSinceReveal += hand - _lastHandCount;
            _lastHandCount = hand;

            if (changed)
                _weights = null;
            return LeadingTypeNote();
        }

        // A search we saw them make without seeing the card (Reinforcement of the Army): one card in their hand is one of these
        // (their lists' cards the search can fetch), each as likely as its unseen copies. Until one of them shows up.
        private HashSet<int>? _searched;
        private int _searchStamp = -1;
        private string _searchSource = "";

        /// <summary>
        /// They searched a card we didn't see (user, 2026-11-13: "their likely deck should be checked for all warrior-type
        /// cards and the potential targets should all be considered high probability to be in their hand"): one of
        /// <paramref name="candidates"/> is now in their hand, and a monster they set after it is likely that card.
        /// </summary>
        public void ObserveSearch(IEnumerable<int> candidates, string source)
        {
            if (!HasListData)
                return;
            _searched = candidates.Select(Normalize).ToHashSet();
            if (_searched.Count == 0)
                _searched = null;
            _searchStamp = _stamp;
            _searchSource = source;
        }

        /// <summary>The searched card's candidates (for the log), or null when no unseen search is pending.</summary>
        public string? DescribeSearch(int top = 4)
        {
            if (_searched == null)
                return null;
            var shares = SearchShares();
            return $"{_searchSource} fetched one of: " + string.Join(", ", shares.OrderByDescending(kv => kv.Value).Take(top).Select(kv => $"{CardNames.Get(kv.Key)} {kv.Value:P0}"));
        }

        // Each candidate's chance to be the searched card: its unseen copies over all candidates' unseen copies, per list.
        private Dictionary<int, double> SearchShares()
        {
            var shares = new Dictionary<int, double>();
            if (_searched == null)
                return shares;
            foreach (var (deck, weight) in Weights())
            {
                if (weight < 1e-4)
                    continue;
                double total = _searched.Sum(id => (double)Remaining(deck, id));
                if (total <= 0)
                    continue;
                foreach (int id in _searched)
                    if (Remaining(deck, id) > 0)
                        shares[id] = shares.GetValueOrDefault(id) + weight * Remaining(deck, id) / total;
            }
            return shares;
        }

        /// <summary>The cards we know are in their hand now (revealed, and not seen leaving it since), one entry per copy.</summary>
        public List<int> KnownHand() => _knownHand.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();

        /// <summary>
        /// A card we just sent from their hand elsewhere (Trap Dustshoot to the deck): it leaves the known hand, and the hand
        /// count we compare against drops by one, so the next read doesn't mistake it for a card played (seen 2026-11-11:
        /// Freed stayed "known in their hand" after Dustshoot shuffled it away).
        /// </summary>
        public void CardLeftHand(int id)
        {
            id = Normalize(id);
            if (_knownHand.TryGetValue(id, out int n))
            {
                if (n > 1) _knownHand[id] = n - 1; else _knownHand.Remove(id);
            }
            if (_lastHand > 0)
                _lastHand--;
            if (_lastHandCount > 0)
                _lastHandCount--;
        }

        /// <summary>Cards revealed from their hand (Trap Dustshoot and the like): what their hand holds right now.</summary>
        public string? ObserveRevealedHand(IEnumerable<int> ids)
        {
            if (!HasListData)
                return null;
            _knownHand.Clear();
            _searched = null; // the whole hand seen: the search is no longer a guess
            foreach (int raw in ids.Where(id => id > 0))
            {
                int id = Normalize(raw);
                _knownHand[id] = _knownHand.GetValueOrDefault(id) + 1;
            }
            _revealStamp = ++_stamp;
            _drawsSinceReveal = 0;
            _weights = null;
            return LeadingTypeNote();
        }

        // Copies of a card we know they have (seen out in the open, or known in hand).
        private int Known(int id) => _seen.GetValueOrDefault(id) + _knownHand.GetValueOrDefault(id);

        // Per list, its unseen settable cards (weighted), valid while the read is unchanged (built with the weights): describing
        // a zone asks for it once per candidate card, so recomputing it each time made a decision take ~100 ms (2026-11-10).
        private readonly Dictionary<(ListDeck Deck, bool Monster), double> _settableCache = new();

        private double RemainingSettable(ListDeck deck, bool monster, Func<int, double> weightOf)
        {
            if (!_settableCache.TryGetValue((deck, monster), out double value))
                _settableCache[(deck, monster)] = value = deck.Main.Keys.Sum(x => weightOf(x) * Remaining(deck, x));
            return value;
        }

        private Dictionary<ListDeck, double> Weights()
        {
            if (_weights != null)
                return _weights;
            _settableCache.Clear(); // the read changed: recount
            var weights = new Dictionary<ListDeck, double>();
            var known = _seen.Keys.Concat(_knownHand.Keys).Distinct().Where(id => Data.Value.Kinds.ContainsKey(id)).ToList();
            foreach (var deck in Data.Value.Decks)
            {
                int missing = 0;
                foreach (int id in known)
                {
                    int runs = IsExtra(id) ? deck.Extra.GetValueOrDefault(id) : deck.Main.GetValueOrDefault(id);
                    missing += Math.Max(0, Known(id) - runs);
                }
                weights[deck] = Math.Pow(MismatchPenalty, missing);
            }
            double total = weights.Values.Sum();
            foreach (var deck in weights.Keys.ToList())
                weights[deck] = total > 0 ? weights[deck] / total : 1.0 / weights.Count;
            return _weights = weights;
        }

        /// <summary>The deck types they're likely on, most likely first.</summary>
        public List<(string Type, double Chance)> LikelyTypes(int top = 3) =>
            Weights().GroupBy(kv => kv.Key.Type).Select(g => (Type: g.Key, Chance: g.Sum(kv => kv.Value)))
                .OrderByDescending(x => x.Chance).Take(top).ToList();

        /// <summary>The chance of the deck type we think they're most likely on (0-1): how sure the deck read is. 0 without list data.</summary>
        /// <summary>The chance their list runs this main-deck card (the weight of the lists they're likely on that have it), or null without list data.</summary>
        public double? RunsChance(int cardId)
        {
            if (!HasListData)
                return null;
            int id = Normalize(cardId);
            return Weights().Where(kv => kv.Key.Main.GetValueOrDefault(id) > 0).Sum(kv => kv.Value);
        }

        /// <summary>The chance they're on a deck type whose name contains this word (e.g. "Fairy"), or 0 without list data.</summary>
        public double TypeChance(string word) =>
            HasListData ? Weights().Where(kv => kv.Key.Type.Contains(word, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value) : 0;

        public double TopTypeChance() => HasListData && LikelyTypes(1) is { Count: > 0 } types ? types[0].Chance : 0;

        /// <summary>From this top deck-type chance, we have an idea of what deck they're on (also the log's "unclear" line).</summary>
        [Tunable("From this top deck-type chance, we have an idea of what deck they're on (also the log's \"unclear\" line).")]
        public static readonly Tuned<double> KnownTypeChance = new(0.35);

        private string? LeadingTypeNote()
        {
            if (_seen.Count == 0 && _knownHand.Count == 0)
                return null;
            var types = LikelyTypes();
            if (types.Count == 0)
                return null;
            string leading = types[0].Chance >= KnownTypeChance ? types[0].Type : "unclear";
            if (leading == _leadingType)
                return null;
            _leadingType = leading;
            return $"opponent deck from Edison lists: {string.Join(", ", types.Select(t => $"{t.Type} {t.Chance:P0}"))}";
        }

        // Unseen copies of a card in one list (in their deck, hand or set), not counting known hand cards.
        private int Remaining(ListDeck deck, int id) =>
            Math.Max(0, deck.Main.GetValueOrDefault(id) - _seen.GetValueOrDefault(id) - _knownHand.GetValueOrDefault(id));

        /// <summary>
        /// Chance their face-down spell/trap in this zone is the given card, or null without list data. Per list: its
        /// unseen copies over its unseen spells/traps; a card set after we saw their hand is most likely one of the
        /// spells/traps we saw there (unless they've drawn since: each draw may have brought a new one).
        /// </summary>
        public double? SetZoneChance(int zone, int cardId) => FaceDownChance(monster: false, zone, cardId);

        /// <summary>
        /// Chance their face-down monster in this monster zone is the given card, or null without list data. Like set
        /// spells/traps, weighted by how likely each monster is to be set: flip monsters (Ryko, Hamster...) are set to be
        /// flipped; other monsters only sometimes (as a blocker). Decks without Ryko make it not Ryko.
        /// </summary>
        public double? MonsterZoneChance(int zone, int cardId) => FaceDownChance(monster: true, zone, cardId);

        /// <summary>
        /// How likely a monster is to be the one set face-down, relative to a flip monster. Tune from logs.
        /// </summary>
        private static double MonsterSetWeight(int id) =>
            MonsterSetWeights.Count > 0 ? MonsterSetWeights.GetValueOrDefault(id)
            : Data.Value.Kinds.TryGetValue(id, out char k) ? k switch { 'F' or 'f' => 1.0, 'M' => SetWeightPlainMonster, _ => 0 } : 0;

        /// <summary>
        /// Traps can't be activated right now (a face-up Jinzo, either side's, or Royal Decree): a set trap is no threat,
        /// so only their quick-play spells can answer (none at all if their lists run none). Set by the brain with each
        /// backrow read (user, 2026-11-10).
        /// </summary>
        public bool TrapsNegated { get; set; }

        private static bool IsTrap(int id) => Data.Value.Kinds.TryGetValue(id, out char k) && k == 'T';

        /// <summary>
        /// A deck read that never changes (a search copy's: it observes nothing): its chances are cached (2026-11-17: with face-downs no
        /// longer leaked, the search copies worked them out from every deck list at every decision of every line, and the search crawled
        /// at 10 lines a second).
        /// </summary>
        public bool Frozen { get; set; }
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(bool Monster, int Zone, int Id), double?> _frozenChances = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, double?> _frozenFlipChances = new();

        private double? FaceDownChance(bool monster, int zone, int cardId) =>
            Frozen ? _frozenChances.GetOrAdd((monster, zone, cardId), k => FaceDownChanceCore(k.Monster, k.Zone, k.Id)) : FaceDownChanceCore(monster, zone, cardId);

        private double? FaceDownChanceCore(bool monster, int zone, int cardId)
        {
            if (!HasListData)
                return null;
            // A negated trap still takes up the zone (it can be the set card) but it can't be activated: as a threat it's 0.
            if (!monster && TrapsNegated && IsTrap(Normalize(cardId)))
                return 0;
            // Known for certain (set from a fully known hand): that card and no other.
            if (KnownSet(monster, zone) is int pinned)
                return Normalize(cardId) == pinned ? 1 : 0;
            Func<int, double> weightOf = monster ? MonsterSetWeight : SetWeight;
            int id = Normalize(cardId);
            var stamps = monster ? _monsterSetStamp : _setZoneStamp;
            bool afterReveal = _revealStamp >= 0 && stamps.TryGetValue(zone, out int setAt) && setAt > _revealStamp;
            // Known hand cards, weighted by how likely each is to be set here.
            double knownSettable = _knownHand.Sum(kv => weightOf(kv.Key) * kv.Value);
            double chance = 0;
            foreach (var (deck, weight) in Weights())
            {
                if (weight <= 0)
                    continue;
                double remainingSettable = RemainingSettable(deck, monster, weightOf);
                double fromDeck = remainingSettable > 0 ? weightOf(id) * Remaining(deck, id) / remainingSettable : 0;
                double p = fromDeck;
                if (afterReveal && knownSettable > 0)
                {
                    int unknownPool = Math.Max(1, deck.MainSize - _seen.Where(kv => !IsExtra(kv.Key)).Sum(kv => kv.Value) - _knownHand.Values.Sum());
                    double drawnSettable = _drawsSinceReveal * remainingSettable / unknownPool;
                    double fromHand = knownSettable / (knownSettable + drawnSettable);
                    p = fromHand * weightOf(id) * _knownHand.GetValueOrDefault(id) / knownSettable + (1 - fromHand) * fromDeck;
                }
                chance += weight * p;
            }
            // A monster set after an unseen search (Reinforcement of the Army): likely the searched card (user, 2026-11-13: "DD
            // Warrior Lady is also commonly a card that gets set"). The searched card against the rest of the hand they set
            // from, each weighted by how likely it is to be set.
            if (monster && _searched != null && stamps.TryGetValue(zone, out int setStamp) && setStamp > _searchStamp)
            {
                var shares = SearchShares();
                double searchSettable = shares.Sum(kv => kv.Value * weightOf(kv.Key));
                if (searchSettable > 0)
                {
                    // The other cards in the hand they set from, each about as settable as an average unseen card.
                    double perCard = 0;
                    foreach (var (deck, weight) in Weights())
                        if (weight >= 1e-4)
                        {
                            int unknownPool = Math.Max(1, deck.MainSize - _seen.Where(kv => !IsExtra(kv.Key)).Sum(kv => kv.Value) - _knownHand.Values.Sum());
                            perCard += weight * RemainingSettable(deck, monster, weightOf) / unknownPool;
                        }
                    double others = Math.Max(0, _lastHand) * perCard; // the hand they set from, less the set card itself
                    double fromSearch = searchSettable / (searchSettable + others);
                    double inSearch = shares.GetValueOrDefault(id) * weightOf(id) / searchSettable;
                    chance = fromSearch * inSearch + (1 - fromSearch) * chance;
                }
            }
            return chance;
        }

        /// <summary>
        /// Chance their face-down monster in this zone is a flip monster that destroys a card when flipped (Ryko, Night
        /// Assailant), or null without list data.
        /// </summary>
        public double? FlipDestroyChance(int zone) =>
            Frozen ? _frozenFlipChances.GetOrAdd(zone, FlipDestroyChanceCore) : FlipDestroyChanceCore(zone);

        private double? FlipDestroyChanceCore(int zone) =>
            HasListData ? Data.Value.Kinds.Where(kv => kv.Value == 'F').Sum(kv => MonsterZoneChance(zone, kv.Key) ?? 0) : null;

        /// <summary>
        /// Chance their set spell/trap in this zone can stop a push (Mirror Force, Torrential, D-Prison, Bottomless,
        /// Compulsory, Sakuretsu, Raigeki Break, Book of Moon, Waboku...), or null without list data.
        /// </summary>
        public double? StopChance(int zone) =>
            HasListData ? StopperIds.Sum(id => SetZoneChance(zone, id) ?? 0) : null;

        /// <summary>The same, stoppers that aren't traps only (Book of Moon...): what still works under Jinzo / Royal Decree.</summary>
        public double? QuickPlayStopChance(int zone) =>
            HasListData ? StopperIds.Where(id => (BotTest.Game.AI.Search.SearchHost.Type(id) & 0x4) == 0).Sum(id => SetZoneChance(zone, id) ?? 0) : null;

        /// <summary>The likeliest identities of their set card in this spell/trap zone (for the log).</summary>
        public string DescribeSetZone(int zone, int top = 3) => Describe(monster: false, zone, top);

        /// <summary>
        /// What their face-down monster in this zone likely is, with each card's chance (1% and up), for the play search to deal one
        /// per line (user, 2026-11-16: "weigh by likely sets only from deck read"). Empty without list data.
        /// </summary>
        public List<(int Id, double Chance)> LikelyMonsterZone(int zone, int top = 12)
        {
            if (!HasListData)
                return new List<(int, double)>();
            return Data.Value.Kinds.Where(kv => kv.Value is 'M' or 'F' or 'f').Select(kv => (Id: kv.Key, Chance: FaceDownChance(true, zone, kv.Key) ?? 0))
                .Where(x => x.Chance >= 0.01).OrderByDescending(x => x.Chance).Take(top).ToList();
        }

        /// <summary>The likeliest identities of their face-down monster in this zone (for the log).</summary>
        public string DescribeMonsterZone(int zone, int top = 3) => Describe(monster: true, zone, top);

        private string Describe(bool monster, int zone, int top)
        {
            var candidates = Data.Value.Kinds.Where(kv => monster ? kv.Value is 'M' or 'F' or 'f' : kv.Value is 'S' or 'Q' or 'T').Select(kv => kv.Key);
            var likely = candidates.Select(id => (Id: id, Chance: FaceDownChance(monster, zone, id) ?? 0))
                .Where(x => x.Chance >= 0.01).OrderByDescending(x => x.Chance).Take(top).ToList();
            return likely.Count == 0 ? "unknown" : string.Join(", ", likely.Select(x => $"{CardNames.Get(x.Id)} {x.Chance:P0}"));
        }

        /// <summary>
        /// The main-deck monsters (not flips: those get set) most likely in their hand of this size, with the chance each is:
        /// what they may well summon in their turn, for the turn planner's "if they summon X" boards. Empty without list data.
        /// </summary>
        public List<(int Id, double Chance)> LikelyMonsters(int handCount, int top)
        {
            if (!HasListData || handCount <= 0)
                return new List<(int, double)>();
            // Only the cards of the lists they're likely on (thousands of cards across all lists otherwise).
            return LikelyCards().Where(id => Data.Value.Kinds.TryGetValue(id, out char k) && k == 'M')
                .Select(id => (Id: id, Chance: HandChance(id, handCount) ?? 0))
                .Where(x => x.Chance >= 0.05)
                .OrderByDescending(x => x.Chance).Take(top).ToList();
        }

        /// <summary>Every card in the lists they're likely on (each list at least <paramref name="minWeight"/>), for warming up the play search.</summary>
        public List<int> LikelyCards(double minWeight = 0.02)
        {
            if (!HasListData)
                return new List<int>();
            return Weights().Where(kv => kv.Value >= minWeight)
                .SelectMany(kv => kv.Key.Main.Keys.Concat(kv.Key.Extra.Keys)).Distinct().ToList();
        }

        /// <summary>Their hidden cards, one guess (for the play search). Lists in the order asked for.</summary>
        public sealed record HiddenSample(List<int> Hand, List<int> SetSpells, List<int> SetMonsters, List<int> Deck, List<int> Extra);

        /// <summary>
        /// One guess at everything of theirs we can't see, consistent with what we've seen: pick a list by how well it
        /// fits, then deal its unseen cards. Revealed hand cards stay in hand; set spells/traps and set monsters are dealt
        /// by how likely each card is to be set (traps over spells, flips over beaters). Null without list data.
        /// </summary>
        public HiddenSample? SampleHidden(Random rng, int handCount, int setSpells, int setMonsters, int extraCount)
        {
            if (!HasListData)
                return null;
            var weights = Weights();
            if (weights.Count == 0)
                return null;
            double roll = rng.NextDouble() * weights.Values.Sum();
            var deck = weights.Keys.First();
            foreach (var (candidate, weight) in weights)
            {
                deck = candidate;
                if ((roll -= weight) <= 0)
                    break;
            }

            var pool = new List<int>();
            foreach (var id in deck.Main.Keys)
                for (int i = Remaining(deck, id); i > 0; i--)
                    pool.Add(id);

            int TakeWeighted(Func<int, double> weightOf)
            {
                double total = pool.Sum(weightOf);
                if (total <= 0)
                    return 0;
                double r = rng.NextDouble() * total;
                for (int i = 0; i < pool.Count; i++)
                {
                    if ((r -= weightOf(pool[i])) <= 0)
                    {
                        int id = pool[i];
                        pool.RemoveAt(i);
                        return id;
                    }
                }
                return 0;
            }

            var spells = new List<int>();
            for (int i = 0; i < setSpells; i++)
                spells.Add(TakeWeighted(SetWeight));
            var monsters = new List<int>();
            for (int i = 0; i < setMonsters; i++)
                monsters.Add(TakeWeighted(MonsterSetWeight));
            var hand = _knownHand.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).Take(handCount).ToList();
            while (hand.Count < handCount && pool.Count > 0)
                hand.Add(TakeWeighted(_ => 1));
            var rest = pool.OrderBy(_ => rng.Next()).ToList();

            var extra = new List<int>();
            foreach (var (id, count) in deck.Extra)
                for (int i = count - _seen.GetValueOrDefault(id); i > 0; i--)
                    extra.Add(id);
            extra = extra.OrderBy(_ => rng.Next()).Take(extraCount).ToList();
            return new HiddenSample(hand, spells, monsters, rest, extra);
        }

        /// <summary>
        /// Chance their Extra Deck holds this card (the lists their cards fit that run it), or null without list data.
        /// E.g. Chimeratech Fortress Dragon: no point playing around it against a deck that doesn't run it.
        /// </summary>
        public double? ExtraDeckChance(int cardId)
        {
            if (!HasListData)
                return null;
            int id = Normalize(cardId);
            return Weights().Where(kv => kv.Key.Extra.GetValueOrDefault(id) > _seen.GetValueOrDefault(id) || _seen.GetValueOrDefault(id) > 0)
                .Sum(kv => kv.Value);
        }

        /// <summary>
        /// For each ATK threshold, the chance at least one of their <paramref name="unknownCards"/> unknown cards (hand plus
        /// draws) is a main-deck monster they can normal summon (Level 4 or lower) with at least that ATK: what they can
        /// attack with next turn. Per list they're likely on, hypergeometric over the cards not yet seen. Null without list data.
        /// </summary>
        /// <summary>
        /// Their next attacker as the sitting-duck check assumes it (user, 2026-11-18): the biggest Level 4-or-lower monster still in the
        /// deck they're most likely on (they summon it and attack), and the biggest Level 5-6 one (summoned instead when they have a monster
        /// to tribute). 0 when none; null without list data.
        /// </summary>
        public (int Normal, int Tribute)? BiggestAttackers(Func<int, (int Attack, int Level)?> stats)
        {
            if (!HasListData)
                return null;
            var top = Weights().OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Key == null)
                return null;
            int normal = 0, tribute = 0;
            foreach (var id in top.Key.Main.Keys)
            {
                if (!Data.Value.Kinds.TryGetValue(id, out char k) || k is not ('M' or 'F' or 'f') || Remaining(top.Key, id) <= 0 || stats(id) is not { } s)
                    continue;
                if (s.Level <= 4)
                    normal = Math.Max(normal, s.Attack);
                else if (s.Level <= 6)
                    tribute = Math.Max(tribute, s.Attack);
            }
            return (normal, tribute);
        }

        public double[]? AttackerChances(int[] thresholds, int unknownCards, Func<int, (int Attack, int Level)?> stats, int maxLevel = 4) =>
            // A frozen read (a search copy's) asks the same question at every decision of every line (2026-11-17 profile: a sixth of
            // the brain's time in the lines). The thresholds are a fixed array, so its identity is part of the key.
            Frozen
                ? _frozenAttackers.GetOrAdd((thresholds, unknownCards, maxLevel), _ => AttackerChancesCore(thresholds, unknownCards, stats, maxLevel))?.ToArray()
                : AttackerChancesCore(thresholds, unknownCards, stats, maxLevel);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int[] Thresholds, int Unknown, int MaxLevel), double[]?> _frozenAttackers = new();

        /// <summary>
        /// As <see cref="AttackerChances"/>, but a monster that's a threat on its effect (<paramref name="effectThreat"/>: a bouncer, a
        /// strong effect) counts at every threshold whatever its ATK: the chance their unknown cards hold a threat (user, 2026-11-24).
        /// A tagged threat (<paramref name="taggedThreat"/>: the Threat tag, Machina Fortress, Archlord Kristya) counts at any level too:
        /// those are special summoned (user, 2026-11-24: "main deck threat-tag monsters should count").
        /// </summary>
        public double[]? ThreatChances(int[] thresholds, int unknownCards, Func<int, (int Attack, int Level)?> stats, Func<int, bool> effectThreat,
            Func<int, bool> taggedThreat) =>
            AttackerChancesCore(thresholds, unknownCards, stats, 4, effectThreat, taggedThreat);

        private double[]? AttackerChancesCore(int[] thresholds, int unknownCards, Func<int, (int Attack, int Level)?> stats, int maxLevel,
            Func<int, bool>? effectThreat = null, Func<int, bool>? taggedThreat = null)
        {
            if (!HasListData || unknownCards <= 0)
                return null;
            var result = new double[thresholds.Length];
            int seen = _seen.Where(kv => !IsExtra(kv.Key)).Sum(kv => kv.Value) + _knownHand.Values.Sum();
            double included = 0;
            foreach (var (deck, weight) in Weights())
            {
                if (weight < 1e-4)
                    continue; // lists their cards don't fit: thousands of them, next to nothing between them
                included += weight;
                int pool = Math.Max(1, deck.MainSize - seen);
                // The list's unseen normal-summonable monsters by ATK.
                var attackers = new List<(int Attack, int Count)>();
                foreach (var id in deck.Main.Keys)
                {
                    if (!Data.Value.Kinds.TryGetValue(id, out char k) || k is not ('M' or 'F' or 'f'))
                        continue;
                    if (stats(id) is not { } s)
                        continue;
                    bool tagged = taggedThreat != null && taggedThreat(id);
                    if (s.Level > maxLevel && !tagged)
                        continue;
                    int left = Remaining(deck, id);
                    if (left > 0)
                        attackers.Add((tagged || effectThreat != null && effectThreat(id) ? int.MaxValue : s.Attack, left));
                }
                for (int t = 0; t < thresholds.Length; t++)
                {
                    int r = attackers.Where(a => a.Attack >= thresholds[t]).Sum(a => a.Count);
                    double none = 1;
                    for (int i = 0; i < Math.Min(unknownCards, pool); i++)
                        none *= Math.Max(0, (double)(pool - r - i) / (pool - i));
                    result[t] += weight * (1 - none);
                }
            }
            if (included > 0)
                for (int t = 0; t < result.Length; t++)
                    result[t] /= included;
            return result;
        }

        /// <summary>
        /// Chance the given card is in their hand of this size, or null without list data (hypergeometric per list over
        /// their unknown cards; certain if we saw it there and it hasn't come out).
        /// </summary>
        public double? HandChance(int cardId, int handCount)
        {
            if (!HasListData)
                return null;
            int id = Normalize(cardId);
            if (_knownHand.GetValueOrDefault(id) > 0)
                return 1;
            int unknownHand = Math.Max(0, handCount - _knownHand.Values.Sum());
            double chance = 0;
            foreach (var (deck, weight) in Weights())
            {
                int pool = Math.Max(1, deck.MainSize - _seen.Where(kv => !IsExtra(kv.Key)).Sum(kv => kv.Value) - _knownHand.Values.Sum());
                int r = Remaining(deck, id);
                double none = 1;
                for (int i = 0; i < Math.Min(unknownHand, pool); i++)
                    none *= Math.Max(0, (double)(pool - r - i) / (pool - i));
                chance += weight * (1 - none);
            }
            // The unseen search: this card is the one fetched with its share.
            if (_searched != null && _searched.Contains(id) && SearchShares().TryGetValue(id, out double share))
                chance = 1 - (1 - chance) * (1 - share);
            return chance;
        }
    }
}
