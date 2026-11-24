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
        /// RULE deck.cheap-into-face-down
        /// A cheap monster (Necro Gardna, an expendable body) attacks a likely Ryko first when everything we have out is cheap:
        /// if it's Ryko, its flip has nothing better to destroy (Necro even works from the graveyard). With a good card out,
        /// Ryko just destroys that instead, so the usual order applies.
        /// The cheap monster must beat the face-down's assumed DEF: a 100-ATK Substitoad kills nothing and only hands a
        /// flipped Ryko its target (seen 2026-11-11).
        /// </summary>
        private BattleCmdResponse? CheapIntoFaceDown(BattleSituation battle, DynamicCard faceDown)
        {
            var cheap = CheapAttackDodgesRyko() && LikelyRyko(faceDown)
                ? battle.Attackers.Where(card => IsCheapAttacker(card) && card.Attack > OpponentMemory.FaceDownDefense(faceDown))
                    .OrderBy(card => card.Attack).FirstOrDefault()
                : default;
            if (cheap.ID <= 0 || !Rule(DeckRules.CheapIntoFaceDown))
                return null;
            AimAt(battle.Context.Bot, faceDown, AimKind.Deliberate, $"attack the unknown face-down (Ryko chance {FaceDownRykoChance(faceDown):P0}: {FaceDownRykoWhy(faceDown)}) with a cheap monster, {CardNames.Get(cheap.ID)}: " +
                                                                  "if it's Ryko, its flip is wasted on it");
            return cheap.DeclareAttack();
        }
    }
}
