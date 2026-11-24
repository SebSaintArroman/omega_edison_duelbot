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
        /// RULE deck.strongest-into-face-down
        /// Our strongest monster attacks the unknown face-down first (it's assumed to be Ryko: if the flip destroys our
        /// strongest, it has already attacked). Exception: when the strongest is the only monster that beats one of their
        /// face-up monsters, it attacks that instead.
        /// </summary>
        private BattleCmdResponse? StrongestIntoFaceDown(BattleSituation battle, DynamicCard faceDown)
        {
            var strongest = battle.Attackers[0];
            bool strongestUniquelyNeeded = battle.FaceUpTargets.Any(target =>
                IsWorthAttacking(strongest, target) &&
                !battle.Attackers.Skip(1).Any(other => IsWorthAttacking(other, target)));
            if (strongestUniquelyNeeded || strongest.Attack <= OpponentMemory.FaceDownDefense(faceDown) || !Rule(DeckRules.StrongestIntoFaceDown))
                return null;
            AimAt(battle.Context.Bot, faceDown, AimKind.Deliberate, $"attack the unknown face-down (Ryko chance {FaceDownRykoChance(faceDown):P0}: {FaceDownRykoWhy(faceDown)}) first with the strongest monster {CardNames.Get(strongest.ID)}");
            return strongest.DeclareAttack();
        }
    }
}
