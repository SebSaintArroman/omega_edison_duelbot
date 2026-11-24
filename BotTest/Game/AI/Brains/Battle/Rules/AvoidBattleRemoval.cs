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
        /// RULE deck.avoid-battle-removal
        /// A face-down that likely banishes (or bounces) what battles it (D.D. Warrior Lady, e.g. set after Reinforcement of
        /// the Army): a valuable attacker isn't worth the trade (user, 2026-11-13: "expected to violate the 'dont banish
        /// fortress' rule (can be fine sometimes...)"). Weighed: the face-down destroyed (1 card) when it isn't, against our
        /// strongest attacker lost when it is. Leaves the Battle Phase when the loss outweighs it.
        /// </summary>
        private async Task<BattleCmdResponse?> AvoidBattleRemovalAsync(BattleSituation battle, DynamicCard faceDown)
        {
            var first = battle.Attackers[0];
            if (IsExpendable(first))
                return null;
            var (removeChance, removers) = await FaceDownRemovesAttackerChanceAsync(faceDown);
            if (removeChance <= 0)
                return null;
            double lose = removeChance * Math.Max(1, BoardEvaluator.AtkValue(first.Attack));
            double gain = 1 - removeChance;
            if (lose <= gain || !Rule(DeckRules.AvoidBattleRemoval))
                return null;
            battle.Context.Bot.LogNote($"deck brain: don't attack the face-down with {CardNames.Get(first.ID)}: it may well be {removers} " +
                                       $"({removeChance:P0}), which removes what battles it: losing it ({lose:0.#}) outweighs destroying the face-down ({gain:0.#})");
            HoldAttack(first.ID, DeckRules.AvoidBattleRemoval);
            ClearAim();
            return LeaveBattle(battle.Context);
        }
    }
}
