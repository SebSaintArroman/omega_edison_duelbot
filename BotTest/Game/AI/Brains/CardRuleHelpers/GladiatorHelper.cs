using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Helpers for card rules: Gladiator Beasts.</summary>
    internal static partial class CardRules
    {
        internal static bool IsGladiatorBeast(int id) =>
            (Search.SearchHost.Type(id) & 0x1) != 0 &&
            Search.SearchHost.Cards.Get(id)?.SetCodes().Any(code => (code & 0x0fff) == 0x19) == true;

        internal static bool GladiatorWouldBeDestroyed(DynamicCard own, DynamicCard opponent)
        {
            if (!IsGladiatorBeast(own.ID) || (opponent.Position & (byte)CardPosition.FaceUpAttack) == 0)
                return false;
            return (own.Position & (byte)CardPosition.FaceUpAttack) != 0
                ? opponent.Attack >= own.Attack
                : opponent.Attack > own.Defense;
        }

        /// <summary>
        /// The AlwaysSet exception for Skill Successor and War Chariot (user, 2026-11-26): we have Raigeki Break (in hand or set) and no other
        /// GoodDiscard card in hand than this one, so it stays in hand as the discard.
        /// </summary>
        internal static bool KeptForRaigeki(CardRuleRegistry.Context context, int self)
        {
            bool raigeki = context.OwnHand.Any(id => Search.SearchHost.Is(id, CardId.RaigekiBreak)) ||
                           context.OwnSpellTrapIds?.Any(id => Search.SearchHost.Is(id, CardId.RaigekiBreak)) == true;
            if (!raigeki)
                return false;
            bool skippedSelf = false;
            foreach (int id in context.OwnHand)
            {
                if (!skippedSelf && Search.SearchHost.Is(id, self))
                {
                    skippedSelf = true;
                    continue;
                }
                if (CardRuleRegistry.AnyCardWithTag(new[] { id }, TagRules.Tag.GoodDiscard))
                    return false;
            }
            return true;
        }
    }
}
