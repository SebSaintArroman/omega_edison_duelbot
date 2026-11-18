using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Temporary ATK boosts that end with the turn (Honest, Blackwing - Kalut the Moon Shadow): left out of every decision in
    /// Main Phase 2 and the End Phase (user, 2026-11-13: "temporary attack boosts like from honest or kalut shouldn't be counted
    /// when working out if you should flip ryko or otherwise use removal in mp2 (but could them during battle phase and mp1)";
    /// "mp2 decisions should ignore kalut and honest's attack boosts"). A boost is noted when one of those cards reaches a
    /// graveyard and a monster's ATK rises at the same moment; it lasts until the turn ends.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private static readonly int[] TempBoosters = { HonestId, 85215458 /* Blackwing - Kalut the Moon Shadow */ };

        private int _boostTurn = -1;
        private int _boostersInGraves;
        private readonly Dictionary<(int Controller, int Zone, int Id), int> _tempBoosts = new();
        private List<(int Controller, int Zone, int Id, int Attack)> _boostSnapshot = new();

        /// <summary>At every real decision: notes a boost that just came from Honest / Kalut.</summary>
        private async Task ObserveTempBoostsAsync(BotStateHeader header, FieldInfo field)
        {
            if (header.CurrentTurn != _boostTurn)
            {
                _boostTurn = header.CurrentTurn;
                _tempBoosts.Clear();
                _boostSnapshot = new();
                _boostersInGraves = -1;
            }
            static bool Booster(int id) => TempBoosters.Contains(id) || TempBoosters.Contains(Search.SearchHost.CanonicalId(id));
            int boosters = (field.OwnSide.Grave ?? Array.Empty<int>()).Count(Booster) + (field.EnemySide.Grave ?? Array.Empty<int>()).Count(Booster);
            var now = (await CurrentField.GetBotMonsterCards(false)).Concat(await CurrentField.GetEnemyMonsterCards(false))
                .Where(card => card.ID > 0 && card.IsFaceup())
                .Select(card => ((int)card.CurrentCoordinates.Controller, (int)card.CurrentCoordinates.Sequence, card.ID, card.Attack))
                .ToList();
            if (_boostersInGraves >= 0 && boosters > _boostersInGraves)
                foreach (var (controller, zone, id, attack) in now)
                    if (_boostSnapshot.FirstOrDefault(s => s.Controller == controller && s.Zone == zone && s.Id == id) is { Id: > 0 } before &&
                        attack > before.Attack)
                    {
                        var key = (controller, zone, id);
                        _tempBoosts[key] = _tempBoosts.GetValueOrDefault(key) + attack - before.Attack;
                    }
            _boostersInGraves = boosters;
            _boostSnapshot = now;
        }

        /// <summary>In Main Phase 2 and the End Phase: these monsters at their ATK without this turn's Honest / Kalut boosts.</summary>
        private List<DynamicCard> WithoutTempBoosts(List<DynamicCard> cards)
        {
            if (_tempBoosts.Count == 0 || _boostTurn != _currentHeader.CurrentTurn ||
                (DuelPhase)_currentHeader.CurrentPhase is not (DuelPhase.Main2 or DuelPhase.End))
                return cards;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (_tempBoosts.TryGetValue((card.CurrentCoordinates.Controller, card.CurrentCoordinates.Sequence, card.ID), out int boost))
                {
                    card.Attack = Math.Max(0, card.Attack - boost);
                    cards[i] = card;
                }
            }
            return cards;
        }
    }
}
