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
        /// RULE deck.no-set-into-stealer
        /// Against Goyo Guardian (a monster that takes what it destroys by battle) a set monster it can beat is just taken: it
        /// destroys it and summons it to their field. True: don't set it (held).
        /// </summary>
        private bool ValueSetIntoStealer(SummonSituation s, DynamicCard valueSet)
        {
            var stealer = s.EnemyMonsters.FirstOrDefault(card => StealsBattleVictim(card) && card.IsAttack());
            if (stealer.ID <= 0 || stealer.Attack <= valueSet.Defense || !Rule(DeckRules.NoSetIntoStealer))
                return false;
            StapleNote(s.Context, $"don't set {CardNames.Get(valueSet.ID)}: {CardNames.Get(stealer.ID)} would destroy it by battle and take it");
            Hold(valueSet.ID, MainAction.SetMonster, DeckRules.NoSetIntoStealer);
            return true;
        }
    }
}
