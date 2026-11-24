using BotTest.Game.AI.Brains.Matchups;
using OmegaBotWrapper;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Matchups: once the opponent's cards show which deck they're on (an X-Saber in their graveyard, say), that deck's
    /// rules apply for the rest of the duel (see Brains/Matchups).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>The opponent deck we've recognised this duel, or null.</summary>
        protected Matchup? ActiveMatchup { get; private set; }

        private void ResetMatchup() => ActiveMatchup = null;

        private void ObserveMatchup(FieldInfo field)
        {
            if (ActiveMatchup != null)
                return;
            var enemy = field.EnemySide;
            var ids = (enemy.MonsterZones ?? Array.Empty<CardArray>()).Concat(enemy.SpellZones ?? Array.Empty<CardArray>())
                .SelectMany(zone => zone.Cards ?? Array.Empty<int>())
                .Concat(enemy.Grave ?? Array.Empty<int>()).Concat(enemy.Banish ?? Array.Empty<int>())
                .Where(id => id > 0);
            foreach (int id in ids)
            {
                var matchup = Matchup.All.FirstOrDefault(m => m.Recognises(id));
                if (matchup == null)
                    continue;
                ActiveMatchup = matchup;
                _trackedBot?.LogNote($"matchup: playing against {matchup.Deck} (seen {OmegaBotWrapper.Logging.CardNames.Get(id)})");
                return;
            }
        }

        /// <summary>The active matchup's reason not to set another spell/trap now, or null.</summary>
        private async Task<string?> MatchupHoldSetAsync(int ourExposedSets)
        {
            if (ActiveMatchup == null)
                return null;
            var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).ToList();
            return ActiveMatchup.HoldSet(theirs, ourExposedSets) is string why && Rule(MatchupRules.XSaberHyunleiBackrow)
                ? $"against {ActiveMatchup.Deck}: {why}"
                : null;
        }
    }
}
