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
        /// RULE deck.no-attack-without-gain
        /// No attack when no attack gains anything (no kill, no safe damage, no payoff): leave the Battle Phase. Every
        /// attacker is held, so a search line that attacks anyway pays for it.
        /// </summary>
        private BattleCmdResponse NoAttackWithoutGain(BattleSituation battle)
        {
            ConsoleUtils.PrintColored("No advantageous attack targets found.");
            foreach (var attacker in battle.Offered)
                HoldAttack(attacker.ID, DeckRules.NoAttackWithoutGain);
            ClearAim();
            return LeaveBattle(battle.Context);
        }
    }
}
