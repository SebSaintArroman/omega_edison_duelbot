using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using BotTest.Game.AI.Search;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    public abstract partial class DeckBotBrain
    {
        private const string DesperateAllureRoutine = "desperate Allure of Darkness";
        private TagRules.BotStrategicState? _strategicStateOverride;
        private TagRules.BotStrategicState _assessedStrategicState;
        internal TagRules.BotStrategicState StrategicState
        {
            get => _strategicStateOverride ?? _assessedStrategicState;
            init => _strategicStateOverride = value;
        }

        private async Task<bool> AllureIsOnlyCardInHandAsync()
        {
            var hand = await GetBotHandCardsAsync();
            return hand.Count == 1 && hand[0].IsCode(AllureOfDarkness);
        }

        private async Task<IdleCmdResponse?> TryDesperateAllureAsync(DecisionContext<IdleCmdData> context)
        {
            if (StrategicState != TagRules.BotStrategicState.Desperate)
                return null;

            var state = context.StateData;
            var allure = (state.ActivableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card =>
                card.IsCode(AllureOfDarkness) && card.CurrentCoordinates.Location == (byte)CardLocation.Hand &&
                card.ActionActivateIndex is { Length: > 0 });
            if (allure.ID <= 0)
                return null;
            var hand = await GetBotHandCardsAsync();
            if (hand.Count(card => card.IsCode(AllureOfDarkness)) != 1 ||
                hand.Any(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0))
                return null;
            if (hand.Count == 1)
                return allure.MainActivateFromIndex();

            var specials = state.SpecialSummonableCards ?? Array.Empty<DynamicCard>();
            var summons = state.SummonableCards ?? Array.Empty<DynamicCard>();
            var monsterSets = state.MonsterSetableCards ?? Array.Empty<DynamicCard>();
            var spellSets = state.SpellSetableCards ?? Array.Empty<DynamicCard>();
            var specialSteps = new List<RoutineStep>();
            var setSteps = new List<RoutineStep>();
            RoutineStep? normalStep = null;
            foreach (var card in hand.Where(card => !card.IsCode(AllureOfDarkness)))
            {
                string name = CardNames.Get(card.ID);
                if (specials.Any(candidate => candidate.IsCode(card.ID) &&
                    candidate.CurrentCoordinates.Location == (byte)CardLocation.Hand))
                {
                    if (!SearchHost.Available)
                        return null;
                    specialSteps.Add(new(MainAction.SpSummon, card.ID, $"special summon {name}"));
                }
                else if (normalStep == null && monsterSets.Any(candidate => candidate.IsCode(card.ID)))
                    normalStep = new(MainAction.SetMonster, card.ID, $"set {name}");
                else if (normalStep == null && summons.Any(candidate => candidate.IsCode(card.ID)))
                    normalStep = new(MainAction.Summon, card.ID, $"summon {name}");
                else if (spellSets.Any(candidate => candidate.IsCode(card.ID)) && await SharedSpellSetAllowedAsync(card))
                    setSteps.Add(new(MainAction.SetSpell, card.ID, $"set {name}"));
                else
                    return null;
            }

            int openSpellZones = 5 - (await GetPlayerCardsInZoneAsync(CardLocation.SpellZone)).Count(card =>
                card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && card.CurrentCoordinates.Sequence < 5);
            // Allure needs a free Spell/Trap Zone to activate from the hand.
            if (setSteps.Count >= openSpellZones)
                return null;

            var steps = new List<RoutineStep>(specialSteps);
            if (normalStep != null)
                steps.Add(normalStep);
            steps.AddRange(setSteps);
            steps.Add(new(MainAction.Activate, AllureOfDarkness, "activate Allure of Darkness", Where: CardLocation.Hand));
            return await StartRoutineAsync(context, DesperateAllureRoutine, "clear the hand before drawing", steps,
                desperateAllure: true);
        }

    }
}
