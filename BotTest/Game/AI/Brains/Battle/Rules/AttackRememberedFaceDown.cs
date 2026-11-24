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
        /// RULE deck.attack-remembered-face-down
        /// A face-down monster we saw face-up earlier (OpponentMemory): attack it with the strongest monster that beats its
        /// remembered DEF.
        /// </summary>
        private BattleCmdResponse? AttackRememberedFaceDown(BattleSituation battle)
        {
            foreach (var remembered in battle.FaceDownTargets)
            {
                if (!OpponentMemory.TryRecall(remembered, out var known))
                    continue;
                var attacker = battle.Attackers.FirstOrDefault(card => card.Attack > known.BaseDefense);
                if (attacker.ID > 0 && Rule(DeckRules.AttackRememberedFaceDown))
                {
                    AimAt(battle.Context.Bot, remembered, AimKind.Deliberate, $"attack face-down {CardNames.Get(known.Id)} (remembered, {known.BaseDefense} DEF) with {CardNames.Get(attacker.ID)}");
                    return attacker.DeclareAttack();
                }
            }
            return null;
        }
    }
}
