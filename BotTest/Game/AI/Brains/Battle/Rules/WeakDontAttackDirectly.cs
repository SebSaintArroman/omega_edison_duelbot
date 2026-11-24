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
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// RULE deck.weak-dont-attack-directly
        /// A weak monster only attacks directly when its damage changes the clock (e.g. it's the difference between game and
        /// not), or it's an expendable hitter or a beater; otherwise the hit isn't worth exposing it.
        /// Returns the monsters that can attack directly and are worth the hit.
        /// </summary>
        private List<DynamicCard> DirectAttackersWorthTheHit(BattleSituation battle)
        {
            var directAttackers = battle.Attackers.Where(card => card.CanDirectAttack).ToList();
            var board = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).ToList();
            var worthwhile = directAttackers.Where(card => !IsWeak(card) || ChangesClock(card, board) || ExpendableHitter(card) || IsBeater(card) ||
                                                           !Rule(DeckRules.WeakDontAttackDirectly)).ToList();
            foreach (var skipped in directAttackers.Where(card => !worthwhile.Any(w => SameZone(w, card))))
                battle.Context.Bot.LogNote($"deck brain: {CardNames.Get(skipped.ID)} doesn't attack: weak, and its damage doesn't change the clock");
            return worthwhile;
        }
    }
}
