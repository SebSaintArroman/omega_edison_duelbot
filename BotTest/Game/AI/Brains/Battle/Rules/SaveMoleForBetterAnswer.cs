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
        /// RULE deck.save-mole-for-better-answer
        /// No attack into Grand Mole when the bounce gains us nothing (no on-summon effect to reuse) and we have a better
        /// answer to it in hand or set (BetterAnswerToBouncerAsync) - unless removing both is lethal. True: skip this attack.
        /// </summary>
        private bool SaveMoleForBetterAnswer(BattleSituation battle, DynamicCard attacker, DynamicCard target, string? betterMoleAnswer)
        {
            if (!BouncesAttacker(target) || betterMoleAnswer == null || GainsFromBounce(attacker) || !Rule(DeckRules.SaveMoleForBetterAnswer) ||
                LethalIfBothRemoved(attacker, target))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(target.ID)} with {CardNames.Get(attacker.ID)}: " +
                                       $"the bounce gains us nothing and we have a better answer to it ({betterMoleAnswer})");
            return true;
        }
    }
}
