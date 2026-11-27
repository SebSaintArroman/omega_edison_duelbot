using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Helpers for card rules: Reinforcement of the Army and Dark Grepher's discards.</summary>
    internal static partial class CardRules
    {
        internal static bool ReinforcementCanSearch(int type, long race, int level) =>
            (type & (int)CardType.Monster) != 0 && (race & (int)CardRace.Warrior) != 0 && level is > 0 and <= 4;

        /// <summary>A DARK monster with the GoodDiscard tag: what Dark Grepher's effect can discard and is happy to.</summary>
        internal static bool IsGoodDarkDiscard(int id) =>
            CardRuleRegistry.AnyCardWithTag(new[] { id }, TagRules.Tag.GoodDiscard) &&
            (Search.SearchHost.Type(id) & 0x1) != 0 && (Search.SearchHost.Attribute(id) & 0x20) != 0;

        internal static bool ReinforcementNeedsGardna(CardRuleRegistry.Context context) =>
            context.DarkGrepherAvailable && context.OwnDeck.Any(id => Search.SearchHost.Is(id, NecroGardna)) &&
            !context.OwnHand.Any(IsGoodDarkDiscard);
    }
}
