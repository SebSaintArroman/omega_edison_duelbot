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
        /// RULE deck.mole-lock
        /// No attack into Grand Mole again while the Mole lock holds (MoleLockStopsAttack, Knowledge/GrandMole.cs).
        /// True: skip this attack.
        /// </summary>
        private bool MoleLockStopsThisAttack(BattleSituation battle, DynamicCard attacker, DynamicCard target)
        {
            if (!BouncesAttacker(target) || !MoleLockStopsAttack(battle.Attackers.Count, out string lockWhy) || !Rule(DeckRules.MoleLock))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(target.ID)} again: {lockWhy}");
            return true;
        }
    }
}
