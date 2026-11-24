using BotTest.Game.Field;
using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Which monsters a Vayu summon negated (Vayu's synchro comes out with its effects negated): tracked from the field.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ Vayu-negated monsters

        // Blackwing - Vayu the Emblem of Honor: banish it + 1 non-Tuner "Blackwing" from the graveyard to Special Summon
        // 1 "Blackwing" Synchro whose Level equals their total Levels, with its effects negated.
        private const int BlackwingSetCode = 0x33;
        private readonly List<int>?[] _lastBanished = { null, null };
        // Per side: the Vayu summon on its way (turn, Level of the synchro it will summon), until that synchro arrives.
        private readonly (int Turn, int Level)?[] _vayuPending = { null, null };
        private readonly int[][] _monsterZoneIds = { new int[7], new int[7] };
        private readonly HashSet<(int Side, int Sequence, int Id)> _vayuNegated = new();

        /// <summary>
        /// Whether this monster's effects are negated. Omega never reports it (Status is always 0), so a Vayu-summoned
        /// synchro is tracked from its summon: when a Vayu is banished together with a partner, the synchro it summons
        /// is the "Blackwing" Synchro of their total Level arriving on that side (this turn).
        /// </summary>
        protected bool IsEffectNegated(DynamicCard card) =>
            card.IsDisabled() || _vayuNegated.Contains((card.CurrentCoordinates.Controller, card.CurrentCoordinates.Sequence, card.ID));

        private static bool IsBlackwing(DBCardInfo info)
        {
            for (long set = info.SetCode; set != 0; set >>= 16)
                if ((set & 0xFFF) == BlackwingSetCode)
                    return true;
            return false;
        }

        private async Task TrackVayuNegationAsync(FieldInfo field)
        {
            // A negated monster turned face-down (Book of Moon) gets its effects back for good, even once flipped up
            // again, so forget it the moment it's seen face-down.
            // (Read fresh, and only while something is marked: the cached monster lists can be a decision old.)
            if (_vayuNegated.Count > 0)
            {
                var sidesNow = new[] { await GetBotMonsterCardsAsync(), await GetEnemyMonsterCardsAsync() };
                for (int side = 0; side < 2; side++)
                    foreach (var card in sidesNow[side].Where(card => !card.IsFaceup()))
                        _vayuNegated.RemoveWhere(x => x.Side == side && x.Sequence == card.CurrentCoordinates.Sequence);
            }
            var sides = new[] { field.OwnSide, field.EnemySide };
            int turn = CurrentHeader.CurrentTurn;
            for (int side = 0; side < 2; side++)
            {
                // Newly banished cards on this side. Vayu's cost banishes it with its partner first; the synchro arrives
                // a few decisions later (its selection, zone and position prompts come in between).
                var banished = (sides[side].Banish ?? Array.Empty<int>()).ToList();
                if (_lastBanished[side] is List<int> before)
                {
                    var added = new List<int>(banished);
                    foreach (int id in before)
                        added.Remove(id);
                    if (added.Remove(BlackwingVayuTheEmblemOfHonor))
                    {
                        int level = 1; // Vayu
                        foreach (int partner in added)
                            if (await CardDatabase.GetAsync(partner) is DBCardInfo p && (p.Type & (int)CardType.Monster) != 0)
                                level += p.Level;
                        if (added.Count == 1)
                            _vayuPending[side] = (turn, level);
                    }
                }
                _lastBanished[side] = banished;

                var zones = sides[side].MonsterZones ?? Array.Empty<CardArray>();
                for (int seq = 0; seq < _monsterZoneIds[side].Length; seq++)
                {
                    int id = seq < zones.Length && zones[seq].Cards is { Length: > 0 } cards ? cards[0] : 0;
                    if (id == _monsterZoneIds[side][seq])
                        continue;
                    // The zone changed: whatever was negated there has left.
                    _vayuNegated.RemoveWhere(x => x.Side == side && x.Sequence == seq);
                    _monsterZoneIds[side][seq] = id;
                    // The synchro this Vayu summons: a "Blackwing" Synchro of the expected Level, this turn.
                    if (_vayuPending[side] is (int pendingTurn, int pendingLevel) && pendingTurn == turn && id > 0 &&
                        await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Synchro) != 0 &&
                        info.Level == pendingLevel && IsBlackwing(info))
                    {
                        _vayuNegated.Add((side, seq, id));
                        _vayuPending[side] = null;
                    }
                }
                if (_vayuPending[side] is (int t, _) && t != turn)
                    _vayuPending[side] = null;
            }
        }
    }
}
