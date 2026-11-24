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
        /// RULE deck.develop-when-bleeding
        /// Round-robin 34c: damage taken on an empty field two of our turns running: holding every monster is wrong, develop the
        /// best one (the strongest that isn't weak; set the best defence when nothing can be summoned).
        /// </summary>
        private IdleCmdResponse? DevelopWhenBleeding(SummonSituation s)
        {
            if (BleedingTurns < 2 || !Rule(DeckRules.DevelopWhenBleeding))
                return null;
            var best = s.Summonable.Where(card => !s.Weak(card)).OrderByDescending(card => card.Attack).FirstOrDefault();
            if (best.ID > 0)
            {
                StapleNote(s.Context, $"normal summon {CardNames.Get(best.ID)}: we've taken damage on an empty field {BleedingTurns} turns running, holding isn't working");
                return best.NormalSummon();
            }
            var set = s.Settable.OrderByDescending(card => card.Defense).FirstOrDefault();
            if (set.ID > 0)
            {
                StapleNote(s.Context, $"set {CardNames.Get(set.ID)}: we've taken damage on an empty field {BleedingTurns} turns running, holding isn't working");
                return set.SetMonster();
            }
            return null;
        }
    }
}
