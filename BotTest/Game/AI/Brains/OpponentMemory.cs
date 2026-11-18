using OmegaBotWrapper;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Remembers opponent monsters the bot has seen face-up, by zone, so a card flipped face-down
    /// (e.g. by Book of Moon) is still known. Only face-up information is ever recorded: the bot must not
    /// use the real identity of cards it has never seen.
    ///
    /// Limitation: the bot only looks at the board when it makes a decision. If a card leaves a zone and a new
    /// face-down card is set there before the bot looks again, the new card is wrongly assumed to be the old one.
    /// </summary>
    public sealed class OpponentMemory
    {
        /// <summary>How many Honest we know are in their hand (set by the brain at each decision; see BoardAssessment.Defenders).</summary>
        public int KnownHonest { get; set; }
        /// <param name="OutsideCount">How many copies were in the opponent's graveyard/banished pile when this was recorded.</param>
        public readonly record struct KnownCard(int Id, int BaseAttack, int BaseDefense, int OutsideCount = 0);

        private readonly Dictionary<int, KnownCard> _monsterZones = new();
        // Base stats of every opponent card seen face-up, so a card known only by id can still be evaluated.
        private readonly Dictionary<int, KnownCard> _statsById = new();

        public void Clear()
        {
            _monsterZones.Clear();
            _statsById.Clear();
        }

        /// <summary>
        /// Cheap observation from the bot's field summary: face-up opponent cards report their id, face-down ones report 0.
        /// Records face-up ids by zone and forgets zones that are empty.
        /// </summary>
        public void ObserveZoneIds(CardInfo enemySide)
        {
            var monsterZones = enemySide.MonsterZones;
            if (monsterZones == null)
                return;

            // Ids in public piles: if a remembered card shows up there more often than when we recorded it, it left the field.
            var outside = (enemySide.Grave ?? Array.Empty<int>()).Concat(enemySide.Banish ?? Array.Empty<int>()).ToList();

            for (int zone = 0; zone < monsterZones.Length; zone++)
            {
                int[] ids = monsterZones[zone].Cards ?? Array.Empty<int>();
                int id = ids.FirstOrDefault(cardId => cardId > 0);
                if (id > 0)
                {
                    var stats = _statsById.TryGetValue(id, out var s) ? s : new KnownCard(id, -1, -1);
                    _monsterZones[zone] = stats with { OutsideCount = outside.Count(x => x == id) };
                }
                else if (_monsterZones.TryGetValue(zone, out var known) &&
                         (ids.Length == 0 || outside.Count(x => x == known.Id) > known.OutsideCount))
                {
                    // Empty, or the remembered card went to the graveyard / banished: a face-down card here is something new.
                    _monsterZones.Remove(zone);
                }
                // Otherwise ids == [0] means empty or face-down; keep what we know.
            }
        }

        /// <summary>Records face-up monsters and forgets zones that are now empty.</summary>
        public void Observe(IEnumerable<DynamicCard> enemyMonsters)
        {
            var occupied = new HashSet<int>();
            foreach (var card in enemyMonsters)
            {
                int zone = card.CurrentCoordinates.Sequence;
                occupied.Add(zone);
                if (card.IsFaceup() && card.ID > 0)
                {
                    var known = new KnownCard(card.ID, card.BaseAttack, card.BaseDefense);
                    _statsById[card.ID] = known;
                    // Keep the graveyard/banished count recorded by ObserveZoneIds for the same card.
                    if (_monsterZones.TryGetValue(zone, out var previous) && previous.Id == card.ID)
                        known = known with { OutsideCount = previous.OutsideCount };
                    _monsterZones[zone] = known;
                }
            }

            foreach (int zone in _monsterZones.Keys.Where(zone => !occupied.Contains(zone)).ToList())
                _monsterZones.Remove(zone);
        }

        /// <summary>
        /// Per monster zone, the DEF their face-down monster likely has: the deck read's likely sets weighted by chance (user, 2026-11-16:
        /// "weigh by likely sets only from deck read ... for anything involving face down monsters"). Set by the brain each decision.
        /// </summary>
        public Dictionary<int, int> ExpectedSetDefense { get; } = new();

        /// <summary>A face-down monster's DEF for the battle maths: the card remembered in its zone, else the deck read's estimate, else
        /// BoardAssessment.AssumedFaceDownDefense.</summary>
        public int FaceDownDefense(DynamicCard faceDown) =>
            TryRecall(faceDown, out var known) ? known.BaseDefense
            : ExpectedSetDefense.TryGetValue(faceDown.CurrentCoordinates.Sequence, out int expected) ? expected
            : BoardAssessment.AssumedFaceDownDefense;

        /// <summary>The lowest DEF among their face-down monsters (FaceDownDefense), or AssumedFaceDownDefense with none.</summary>
        public int LowestFaceDownDefense(IEnumerable<DynamicCard> enemyMonsters) =>
            enemyMonsters.Where(card => !card.IsFaceup() && (card.ID > 0 || card.Position != 0)).Select(FaceDownDefense)
                .DefaultIfEmpty(BoardAssessment.AssumedFaceDownDefense).Min();

        /// <summary>The card last seen face-up in this face-down card's zone, if the zone hasn't been seen empty since.</summary>
        public bool TryRecall(DynamicCard faceDown, out KnownCard known)
        {
            if (!_monsterZones.TryGetValue(faceDown.CurrentCoordinates.Sequence, out known))
                return false;
            if (known.BaseDefense < 0 && _statsById.TryGetValue(known.Id, out var stats))
                known = stats;
            // Identity known but stats never captured: not useful for battle maths, fall back to the default assumption.
            return known.BaseDefense >= 0;
        }
    }
}
