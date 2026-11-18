using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Which Extra Deck monsters were not properly summoned (Starlight Road's Stardust Dragon, a Synchro brought out by
    /// Vayu's effect): such a monster can't be revived from the graveyard or taken by Goyo Guardian. Omega doesn't report
    /// how a card was summoned (every card's Reason is 0), so this is worked out from board changes: an Extra Deck
    /// monster that leaves the Extra Deck while fewer than two monsters reach the graveyard (no Synchro / Fusion
    /// materials) wasn't properly summoned; a monster coming back from the graveyard or banishment keeps its status.
    /// Index 0 = our side, 1 = theirs. Reset per duel.
    /// </summary>
    public sealed class SummonTracker
    {
        // Per side: the monster id in each zone at the last update, and whether it wasn't properly summoned.
        private readonly int[][] _zoneIds = { new int[7], new int[7] };
        private readonly bool[][] _improperZone = { new bool[7], new bool[7] };
        // Per side: improper copies sitting in that side's graveyard (owner's) and banished zone, by id.
        private readonly Dictionary<int, int>[] _improperGrave = { new(), new() };
        private readonly int[] _extraCount = { -1, -1 };
        private readonly List<int>[] _grave = { new(), new() };
        private readonly List<int>[] _banished = { new(), new() };

        public void Clear()
        {
            for (int s = 0; s < 2; s++)
            {
                Array.Clear(_zoneIds[s]);
                Array.Clear(_improperZone[s]);
                _improperGrave[s].Clear();
                _extraCount[s] = -1;
                _grave[s].Clear();
                _banished[s].Clear();
            }
        }

        /// <summary>Whether the monster in this zone wasn't properly summoned (it can't come back once it leaves).</summary>
        public bool ImproperOnField(int side, int zone) => zone is >= 0 and < 7 && _improperZone[side][zone];

        /// <summary>How many copies of this card in the side's graveyard weren't properly summoned (can't be revived).</summary>
        public int ImproperInGrave(int side, int id) => _improperGrave[side].GetValueOrDefault(id);

        private static bool IsExtraDeckMonster(int id) =>
            CardDatabase.TryGetCached(id, out var info) &&
            (info.Type & ((int)CardType.Synchro | (int)CardType.Fusion | (int)CardType.Xyz | (int)CardType.Link)) != 0;

        private static bool IsMonster(int id) => CardDatabase.TryGetCached(id, out var info) && (info.Type & (int)CardType.Monster) != 0;

        /// <summary>Updates from a board read (call at every decision).</summary>
        public void Observe(FieldInfo field)
        {
            var sides = new[] { field.OwnSide, field.EnemySide };
            var now = new int[2][];
            var newGrave = new List<int>[2];
            var newBanished = new List<int>[2];
            var gainedGrave = new List<int>[2];
            var leftGrave = new List<int>[2];
            var leftBanished = new List<int>[2];
            for (int s = 0; s < 2; s++)
            {
                var zones = sides[s].MonsterZones ?? Array.Empty<CardArray>();
                now[s] = new int[7];
                for (int z = 0; z < Math.Min(7, zones.Length); z++)
                    now[s][z] = (zones[z].Cards ?? Array.Empty<int>()).FirstOrDefault(id => id > 0);
                newGrave[s] = (sides[s].Grave ?? Array.Empty<int>()).Where(id => id > 0).ToList();
                newBanished[s] = (sides[s].Banish ?? Array.Empty<int>()).Where(id => id > 0).ToList();
                gainedGrave[s] = Minus(newGrave[s], _grave[s]);
                leftGrave[s] = Minus(_grave[s], newGrave[s]);
                leftBanished[s] = Minus(_banished[s], newBanished[s]);
            }

            // Monsters that left a zone: an improper one going to a graveyard stays improper there.
            var improperLeaving = new List<int>();
            for (int s = 0; s < 2; s++)
                for (int z = 0; z < 7; z++)
                    if (_zoneIds[s][z] > 0 && now[s][z] != _zoneIds[s][z] && _improperZone[s][z])
                        improperLeaving.Add(_zoneIds[s][z]);
            foreach (int id in improperLeaving)
                for (int s = 0; s < 2; s++)
                    if (gainedGrave[s].Remove(id))
                    {
                        _improperGrave[s][id] = _improperGrave[s].GetValueOrDefault(id) + 1;
                        break;
                    }

            // Monsters that arrived in a zone.
            for (int s = 0; s < 2; s++)
            {
                int extra = sides[s].Extra?.Length ?? 0;
                bool fromExtra = _extraCount[s] >= 0 && extra < _extraCount[s];
                int materials = (sides[s].Grave ?? Array.Empty<int>()).Count(id => id > 0 && IsMonster(id)) -
                                _grave[s].Count(IsMonster);
                for (int z = 0; z < 7; z++)
                {
                    int id = now[s][z];
                    if (id <= 0 || id == _zoneIds[s][z])
                    {
                        if (id <= 0)
                            _improperZone[s][z] = false;
                        continue;
                    }
                    bool improper = false;
                    if (IsExtraDeckMonster(id))
                    {
                        bool fromGraveOrBanished = leftGrave.Any(g => g.Contains(id)) || leftBanished.Any(b => b.Contains(id));
                        bool stolen = improperLeaving.Contains(id) && !fromGraveOrBanished;
                        if (fromGraveOrBanished)
                        {
                            // Revived: it keeps the status it had (improper copies can't be revived, so normally proper).
                            improper = false;
                        }
                        else if (stolen)
                            improper = true; // changed control: still the same improper monster
                        else if (fromExtra && materials < 2 && !IsXyz(id))
                            improper = true; // out of the Extra Deck with no Synchro / Fusion materials
                    }
                    _improperZone[s][z] = improper;
                }
                _extraCount[s] = extra;
            }

            // Improper copies that left a graveyard (banished, returned...) are no longer there.
            for (int s = 0; s < 2; s++)
            {
                foreach (int id in leftGrave[s])
                    if (_improperGrave[s].TryGetValue(id, out int n) && newGrave[s].Count(g => g == id) < n)
                        _improperGrave[s][id] = Math.Max(0, newGrave[s].Count(g => g == id));
                Array.Copy(now[s], _zoneIds[s], 7);
                _grave[s] = newGrave[s];
                _banished[s] = newBanished[s];
            }
        }

        private static bool IsXyz(int id) => CardDatabase.TryGetCached(id, out var info) && (info.Type & (int)CardType.Xyz) != 0;

        private static List<int> Minus(List<int> a, List<int> b)
        {
            var left = new List<int>(a);
            foreach (int id in b)
                left.Remove(id);
            return left;
        }
    }
}
