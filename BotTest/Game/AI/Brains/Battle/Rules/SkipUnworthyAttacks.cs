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
        /// RULE deck.skip-unworthy-attacks
        /// No attack on a monster that isn't worth destroying (IsWorthAttacking): a bouncer that would cost us a token or
        /// Extra Deck monster, a recruiter that isn't a threat and doesn't win the game this turn, or one whose destruction
        /// gets them a card while our clock is slow. True: skip this attack.
        /// </summary>
        private bool SkipUnworthyAttack(BattleSituation battle, DynamicCard attacker, DynamicCard target)
        {
            if (IsWorthAttacking(attacker, target) || !Rule(DeckRules.SkipUnworthyAttacks))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(target.ID)} with {CardNames.Get(attacker.ID)}: " +
                                       (BouncesAttacker(target)
                                           ? "it returns its attacker to the hand, and a token / Extra Deck monster would be lost"
                                           : IsRecruiter(target)
                                               ? "it's a recruiter (it replaces itself when destroyed), it isn't a real threat to us, and it doesn't win the game this turn"
                                               : $"destroying it gets them a card, it isn't a threat, and our clock is only {_clockSpeed}"));
            return true;
        }

        /// <summary>
        /// RULE deck.skip-unworthy-attacks (Treeborn Frog)
        /// No attack on their Treeborn Frog when it's pointless (TreebornAttackPointless: it comes back anyway).
        /// True: skip this attack.
        /// </summary>
        private bool SkipPointlessTreebornAttack(BattleSituation battle, DynamicCard attacker, DynamicCard target)
        {
            if (TreebornWhy(battle, attacker, target) is not string treeborn || !Rule(DeckRules.SkipUnworthyAttacks))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(target.ID)} with {CardNames.Get(attacker.ID)}: {treeborn}");
            return true;
        }
    }
}
