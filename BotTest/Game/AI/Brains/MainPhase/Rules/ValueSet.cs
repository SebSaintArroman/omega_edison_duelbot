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
        /// RULE deck.value-set
        /// Monsters that gain value when set and destroyed / flipped are worth setting anyway (not a straight -1): the deck's
        /// first ValueSetIds monster on offer. Not into Grand Mole (NoSetIntoBouncer) nor Goyo (NoSetIntoStealer).
        /// </summary>
        private IdleCmdResponse? ValueSet(SummonSituation s)
        {
            var valueSet = s.Settable
                .Where(card => card.IsCode(ValueSetIds))
                .OrderBy(card => StaplePriorityIndex(ValueSetIds, card))
                .FirstOrDefault();
            string? bouncerHold = s.Settable.Count > 0 ? SetIntoBouncerHold(s.Context) : null;
            if (valueSet.ID <= 0 || ValueSetIntoBouncer(s, valueSet, bouncerHold) || ValueSetIntoStealer(s, valueSet) || !Rule(DeckRules.ValueSet))
                return null;
            StapleNote(s.Context, $"set {CardNames.Get(valueSet.ID)} face-down: it gains value when flipped/destroyed (or works from the graveyard)");
            return valueSet.SetMonster();
        }
    }
}
