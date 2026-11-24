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
        /// RULE deck.one-hit-lethal-first
        /// A direct hit that is lethal on its own is taken first (the smallest such attacker): a hand trap like Gorz can't stop
        /// it. Returns that attacker, or null.
        /// </summary>
        private DynamicCard? OneHitLethalFirst(BattleSituation battle, List<DynamicCard> worthwhile)
        {
            var oneShot = worthwhile.Where(card => card.Attack >= _oppLp).OrderBy(card => card.Attack).FirstOrDefault();
            if (oneShot.ID <= 0 || !Rule(DeckRules.OneHitLethalFirst))
                return null;
            battle.Context.Bot.LogNote($"deck brain: {CardNames.Get(oneShot.ID)} is lethal in one hit: attack with it first");
            return oneShot;
        }
    }
}
