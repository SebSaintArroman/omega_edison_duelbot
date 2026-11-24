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
        /// RULE deck.no-set-into-bouncer
        /// Against Grand Mole a face-down monster does nothing (it's attacked and returned to our hand): no monster sets
        /// (SetIntoBouncerHold, Knowledge/GrandMole.cs). Removes the monster sets from the options.
        /// </summary>
        private DecisionContext<IdleCmdData> NoMonsterSetsIntoBouncer(DecisionContext<IdleCmdData> context)
        {
            if ((context.StateData.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Length == 0 ||
                SetIntoBouncerHold(context) is not string bouncerWhy || !Rule(DeckRules.NoSetIntoBouncer))
                return context;
            context.Bot.LogNote($"deck brain: don't set monsters: {bouncerWhy}");
            foreach (var card in context.StateData.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                Hold(card.ID, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
            var noMonsterSets = context.StateData;
            noMonsterSets.MonsterSetableCards = Array.Empty<DynamicCard>();
            return context with { StateData = noMonsterSets };
        }

        /// <summary>
        /// RULE deck.no-set-into-bouncer (value-set side)
        /// Against Grand Mole a set monster does nothing: Mole attacks it and returns it to our hand (Ryko included).
        /// True: don't set it (held).
        /// </summary>
        private bool ValueSetIntoBouncer(SummonSituation s, DynamicCard valueSet, string? bouncerHold)
        {
            if (bouncerHold == null || !Rule(DeckRules.NoSetIntoBouncer))
                return false;
            StapleNote(s.Context, $"don't set {CardNames.Get(valueSet.ID)}: {bouncerHold}");
            Hold(valueSet.ID, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
            return true;
        }
    }
}
