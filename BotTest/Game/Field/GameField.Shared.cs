using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.Field
{
    public partial class GameField
    {
        private enum FieldPerspective
        {
            Bot,
            Player
        }

        private async Task<FieldInfo> RefreshFieldAsync(FieldPerspective perspective)
        {
            _fieldInfo = perspective switch
            {
                FieldPerspective.Bot => await _botWrapper.GetBotFieldInfo(),
                FieldPerspective.Player => await DuelBotWrapper.GetPlayerFieldInfo(),
                _ => throw new ArgumentOutOfRangeException(nameof(perspective))
            };

            return _fieldInfo;
        }

        private Task<DynamicCard> GetFieldCardAsync(
            FieldPerspective perspective,
            int controller,
            CardLocation location,
            int sequence)
        {
            return perspective switch
            {
                FieldPerspective.Bot => _botWrapper.GetBotFieldCardInfo(controller, (int)location, sequence),
                FieldPerspective.Player => DuelBotWrapper.GetPlayerFieldCardInfo(controller, (int)location, sequence),
                _ => throw new ArgumentOutOfRangeException(nameof(perspective))
            };
        }

        /// <summary>
        /// The opponent's view ("Player") shows their own hidden cards (face-down monsters and spells/traps, hand, deck) in full: the bot
        /// must not see them (2026-11-17: every "their face-down is Ryko 100%" note was a real Ryko, and "0%" a real Mega Hamster). Their
        /// hidden card keeps its place and position (a face-down is still there to attack) but loses its identity and stats.
        /// </summary>
        private static DynamicCard MaskHidden(DynamicCard card, FieldPerspective perspective, int controller, CardLocation location)
        {
            if (perspective != FieldPerspective.Player || controller != 0)
                return card;
            bool hidden = location is CardLocation.Hand or CardLocation.Deck or CardLocation.Extra ||
                          location is CardLocation.MonsterZone or CardLocation.SpellZone or CardLocation.PendulumZone or CardLocation.FieldZone &&
                          (card.Position & 0xA) != 0; // face-down (attack or defence)
            if (!hidden)
                return card;
            card.ID = 0;
            card.Alias = 0;
            card.Type = 0;
            card.Level = 0;
            card.Rank = 0;
            card.Attribute = 0;
            card.Race = 0;
            card.Attack = card.Defense = card.BaseAttack = card.BaseDefense = 0;
            return card;
        }

        private static CardInfo GetSide(FieldInfo field, bool isOwnSide)
        {
            return isOwnSide ? field.OwnSide : field.EnemySide;
        }

        private static bool IsLinearLocation(CardLocation location)
        {
            return location is CardLocation.Hand
                or CardLocation.Grave
                or CardLocation.Removed
                or CardLocation.Deck
                or CardLocation.Extra;
        }

        private static int[] GetLinearZoneCards(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.Hand => side.Hand,
                CardLocation.Grave => side.Grave,
                CardLocation.Removed => side.Banish,
                CardLocation.Deck => side.Main,
                CardLocation.Extra => side.Extra,
                _ => Array.Empty<int>()
            };
        }

        private static CardArray[] GetStructuredZones(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.MonsterZone => side.MonsterZones,
                CardLocation.SpellZone => side.SpellZones,
                CardLocation.PendulumZone => side.SpellZones,
                CardLocation.FieldZone => side.SpellZones,
                _ => Array.Empty<CardArray>()
            };
        }

        private static bool IsMissingCardException(Exception ex)
        {
            return ex.Message.Contains("404") || ex.Message.Contains("No card found");
        }

        // Each card list built once per decision (2026-11-17 profile: the brains ask for "their monsters", "our hand", ... many times
        // in one decision, and each ask re-read the field and every card in it). A new decision (the wrapper's field generation) starts
        // afresh; callers get their own copy of the list, so changing it can't touch the cached one.
        private readonly Dictionary<(FieldPerspective Perspective, CardLocation Location, int Controller), List<DynamicCard>> _listCache = new();
        private int _listCacheGeneration = -1;
        private readonly object _listCacheLock = new();

        private async Task<List<DynamicCard>> GetCardsInLocationAsync(
            FieldPerspective perspective,
            CardLocation location,
            int controller = 0)
        {
            int generation = _botWrapper.FieldGeneration;
            var key = (perspective, location, controller);
            lock (_listCacheLock)
            {
                if (_listCacheGeneration != generation)
                {
                    _listCache.Clear();
                    _listCacheGeneration = generation;
                }
                if (_listCache.TryGetValue(key, out var cached))
                    return new List<DynamicCard>(cached);
            }
            var cards = await ReadCardsInLocationAsync(perspective, location, controller);
            lock (_listCacheLock)
                if (_listCacheGeneration == generation)
                    _listCache[key] = new List<DynamicCard>(cards);
            return cards;
        }

        private async Task<List<DynamicCard>> ReadCardsInLocationAsync(
            FieldPerspective perspective,
            CardLocation location,
            int controller)
        {
            var field = await RefreshFieldAsync(perspective);
            var side = controller == 0 ? field.OwnSide : field.EnemySide;
            var cards = new List<DynamicCard>();

            if (IsLinearLocation(location))
            {
                var cardIds = GetLinearZoneCards(side, location);

                for (int sequence = 0; sequence < cardIds.Length; sequence++)
                {
                    if (cardIds[sequence] <= 0)
                    {
                        continue;
                    }

                    try
                    {
                        var card = await GetFieldCardAsync(perspective, controller, location, sequence);
                        if (card.ID > 0)
                        {
                            cards.Add(MaskHidden(card, perspective, controller, location));
                        }
                    }
                    catch (Exception ex) when (IsMissingCardException(ex))
                    {
                        continue;
                    }
                }

                return cards;
            }

            var zones = GetStructuredZones(side, location);
            for (int zoneIndex = 0; zoneIndex < zones.Length; zoneIndex++)
            {
                if (!zones[zoneIndex].Cards.Any(cardId => cardId > 0))
                {
                    continue;
                }

                try
                {
                    var card = await GetFieldCardAsync(perspective, controller, location, zoneIndex);
                    if (card.ID > 0)
                    {
                        cards.Add(MaskHidden(card, perspective, controller, location));
                    }
                }
                catch (Exception ex) when (IsMissingCardException(ex))
                {
                    continue;
                }
            }

            if (zones.Length == 0)
            {
                throw new ArgumentException($"Unsupported location: {location}");
            }

            return cards;
        }

        private async Task<List<DynamicCard>> GetCardsInZoneAsync(
            FieldPerspective perspective,
            CardLocation location,
            bool isOwnSide = true)
        {
            var controller = isOwnSide ? 0 : 1;
            return await GetCardsInLocationAsync(perspective, location, controller);
        }

        private static int GetZoneCardCount(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.MonsterZone => side.MonsterZones.Sum(zone => zone.Cards.Length),
                CardLocation.SpellZone => side.SpellZones.Sum(zone => zone.Cards.Length),
                CardLocation.Hand => side.Hand.Length,
                CardLocation.Grave => side.Grave.Length,
                CardLocation.Removed => side.Banish.Length,
                CardLocation.Deck => side.Main.Length,
                CardLocation.Extra => side.Extra.Length,
                _ => 0
            };
        }

        private async Task<int> GetLifePointsAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            return GetSide(field, isOwnSide).LifePoints;
        }

        private async Task<bool> IsUnderAttackAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            return GetSide(field, isOwnSide).UnderAttack;
        }

        private async Task<DynamicCard> GetBattlingMonsterAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            var side = GetSide(field, isOwnSide);
            return side.HasBattlingMonster ? side.BattlingMonster : default;
        }
    }
}
