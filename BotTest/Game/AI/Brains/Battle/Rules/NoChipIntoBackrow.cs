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
        /// RULE deck.no-chip-into-backrow
        /// Chip damage that isn't a clock only exposes the monster to their backrow (Mirror Force, Sakuretsu...). With set
        /// cards up, attack directly only when our board is a real clock (better than AlmostNone) or the hits are lethal.
        /// True: don't attack.
        /// </summary>
        private bool NoChipIntoBackrow(BattleSituation battle, DynamicCard attacker)
        {
            if (_clockSpeed is not (ClockSpeed.AlmostNone or ClockSpeed.None) ||
                battle.Attackers.Where(card => card.CanDirectAttack).Sum(card => card.Attack) >= _oppLp || !Rule(DeckRules.NoChipIntoBackrow))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't attack directly with {CardNames.Get(attacker.ID)}: " +
                                       $"our board is only a {_clockSpeed} clock, not worth exposing it to the backrow");
            return true;
        }
    }
}
