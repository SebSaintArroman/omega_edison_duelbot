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
    /// <summary>
    /// Reading the field and the prompt: our and their cards by zone, and what a prompt offers.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected virtual async Task<List<DynamicCard>> GetPlayerCardsInZoneAsync(CardLocation location, bool isOwnSide = true) =>
            (await CurrentField.GetPlayerCardsInZone(location, isOwnSide)).ToList();

        protected virtual Task<int> GetPlayerSpellZoneCountAsync(CardLocation location = CardLocation.SpellZone) =>
            CurrentField.GetPlayerSpellZoneCount(location);

        protected virtual Task<bool> HasCardInBotMonsterZoneAsync(int cardId) =>
            CurrentField.HasCardInBotMonsterZone(cardId);

        protected virtual Task<bool> HasCardInBotSpellZoneAsync(int cardId) =>
            CurrentField.HasCardInBotSpellZone(cardId);

        protected virtual Task<List<DynamicCard>> GetBotHandCardsAsync() =>
            CurrentField.GetBotHandCards();

        protected virtual Task<List<DynamicCard>> GetBotGraveyardCardsAsync() =>
            CurrentField.GetBotGraveyardCards();

        protected virtual async Task<List<DynamicCard>> GetBotMonsterCardsAsync(bool onlyFaceUp = false) =>
            WithoutTempBoosts(await CurrentField.GetBotMonsterCards(onlyFaceUp));

        protected virtual async Task<List<DynamicCard>> GetEnemyMonsterCardsAsync(bool onlyFaceUp = false) =>
            WithoutTempBoosts(await CurrentField.GetEnemyMonsterCards(onlyFaceUp));

        protected virtual Task<List<DynamicCard>> GetEnemySpellCardsAsync(bool onlyFaceUp = false) =>
            CurrentField.GetEnemySpellCards(onlyFaceUp);

        protected static bool TryGetSummonable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpecialSummon(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpellSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetMonsterSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetActivable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ActivableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetReposable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ReposableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected virtual async Task<DynamicCard> GetBestEnemyMonster(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyMonster(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemySpell(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemySpell(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemyCard(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyCard(onlyFaceUp);

        protected static bool CanBuildLevel8Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(SwordsoulGrandmasterChixiao, BaxiaBrightnessOfTheYangZing, YaziEvilOfTheYangZing));
        }

        protected static bool CanBuildLevel10Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(SwordsoulSupremeSovereignChengying, BaronneDeFleur, SwordsoulSinisterSovereignQixingLongyuan));
        }
    }
}
