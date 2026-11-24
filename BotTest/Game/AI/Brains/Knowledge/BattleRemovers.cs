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
    /// Monsters that banish or bounce what battles them (D.D. Warrior Lady), and our face-down risk against them.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Attackers that banish or bounce the monster they battle (D.D. Warrior Lady, Grand Mole): refreshed per battle decision.
        private HashSet<int> _removesWhatItBattles = new();

        // Their face-up monsters that banish / return whatever battles them (D.D. Warrior Lady), learnt from their text.
        private readonly HashSet<int> _targetRemovesAttacker = new();

        private static readonly System.Text.RegularExpressions.Regex RemovesBattledText = new(
            @"(battles?|damage calculation)[^.]{0,120}?\b(banish|return)[^.]{0,60}?(that|the opponent's|opponent's|both)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Their likely monsters that banish / bounce what battles them (from their lists' card texts), built once per duel read.
        private List<int>? _theirBattleRemovers;
        private int _theirBattleRemoversTurn = int.MaxValue;

        /// <summary>
        /// The chance their face-down is a monster that removes what battles it (D.D. Warrior Lady), from the deck read
        /// (an unseen Reinforcement of the Army search counts: see ObserveUnseenSearchesAsync), and the likeliest of them.
        /// </summary>
        protected async Task<(double Chance, string Names)> FaceDownRemovesAttackerChanceAsync(DynamicCard faceDown)
        {
            int turn = _currentHeader.CurrentTurn;
            if (_theirBattleRemovers == null || turn < _theirBattleRemoversTurn || _theirBattleRemovers.Count == 0 && turn != _theirBattleRemoversTurn)
            {
                var list = new List<int>();
                foreach (int id in OpponentDeck.LikelyCards(minWeight: 1e-4))
                    if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) != 0 &&
                        RemovesBattledText.IsMatch(info.CardText ?? ""))
                        list.Add(id);
                _theirBattleRemovers = list;
            }
            _theirBattleRemoversTurn = turn;
            int zone = faceDown.CurrentCoordinates.Sequence;
            var chances = _theirBattleRemovers.Select(id => (Id: id, Chance: OpponentDeck.MonsterZoneChance(zone, id) ?? 0))
                .Where(x => x.Chance > 0).OrderByDescending(x => x.Chance).ToList();
            double none = chances.Aggregate(1.0, (acc, x) => acc * (1 - x.Chance));
            return (1 - none, string.Join(" / ", chances.Take(2).Select(x => CardNames.Get(x.Id))));
        }

        private async Task RefreshRemovesWhatItBattlesAsync(IEnumerable<DynamicCard> attackers)
        {
            foreach (var card in attackers.Where(card => card.ID > 0 && !_removesWhatItBattles.Contains(card.ID)))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && RemovesBattledText.IsMatch(info.CardText ?? ""))
                    _removesWhatItBattles.Add(card.ID);
        }
    }
}
