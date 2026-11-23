using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Generic lookup and tag resolution for the per-card definitions in CardRules.cs.</summary>
    internal static class CardRuleRegistry
    {
        internal sealed record Context(IReadOnlyCollection<int> OwnDeck, IReadOnlyCollection<int> OwnHand, int OpponentHandCount,
            int OwnLifePoints, int OpponentLifePoints, bool NoOtherAnswer,
            int ThreatCardId = 0, bool ThreatWasAlreadyOnFieldWhenTrapWasSet = false,
            bool ThreatAttackPosition = false, bool OpponentHasStardustDragon = false,
            bool ThreatTriggersWhenNormalSummoned = false,
            int ActiveChainPlayer = -1, int ActiveChainCardId = 0, IReadOnlyCollection<int>? ActiveChainTargetIds = null,
            IReadOnlyCollection<int>? TargetedOpponentGraveCards = null, IReadOnlyCollection<int>? OwnGrave = null,
            IReadOnlyCollection<int>? OpponentGrave = null, IReadOnlyCollection<int>? OpponentMonsterIds = null,
            IReadOnlyCollection<int>? OpponentHand = null, bool UnknownOpponentHandCards = false,
            bool IsOurTurn = false, int OurFaceUpAttackers = 0, bool CanWinThisTurn = false,
            IReadOnlyCollection<int>? OwnMonsterIds = null, bool OpponentIsAttacking = false,
            bool IsDoomed = false, bool InDamageStep = false, bool GladiatorWouldBeDestroyed = false,
            bool DrawTrapException = false, bool PlaguespreaderMayRevive = false,
            TagRules.BotStrategicState StrategicState = TagRules.BotStrategicState.Base,
            bool NormalSummonUsed = true, bool ReinforcementHasSingleTarget = false, bool DarkGrepherAvailable = false);

        internal sealed record Rule(int[] Ids, TagRules.Tag Tags,
            Func<Context, TagRules.Tag>? TagsToDisableWhen = null,
            Func<Context, IReadOnlyCollection<int>, IReadOnlyCollection<int>>? AllowedTargets = null,
            Func<Context, int, int?>? SelectOptionIndex = null,
            Func<bool, DHint, DynamicCard, int>? TargetPriority = null);

        internal sealed record CategoryRule(Func<int, bool> Matches, TagRules.Tag Tags);

        private static readonly Dictionary<int, (Rule Rule, int Order)> ById = IndexRules();

        private static Dictionary<int, (Rule Rule, int Order)> IndexRules()
        {
            var byId = new Dictionary<int, (Rule Rule, int Order)>();
            for (int i = 0; i < CardRules.Definitions.Length; i++)
            {
                var rule = CardRules.Definitions[i];
                foreach (int id in rule.Ids)
                    byId.TryAdd(id, (rule, i));
            }
            return byId;
        }

        private static Rule? ForCard(int id)
        {
            int canonical = Search.SearchHost.CanonicalId(id);
            bool hasDirect = ById.TryGetValue(id, out var direct);
            // The first definition wins even when the raw id and its alias have separate entries.
            if (canonical > 0 && ById.TryGetValue(canonical, out var alias) && (!hasDirect || alias.Order < direct.Order))
                return alias.Rule;
            return direct.Rule;
        }

        public static TagRules.Tag TagsFor(int id, Context context)
        {
            var rule = ForCard(id);
            TagRules.Tag cardTags = rule?.Tags ?? TagRules.Tag.None;
            TagRules.Tag disabledTags = rule?.TagsToDisableWhen?.Invoke(context) ?? TagRules.Tag.None;
            TagRules.Tag categoryTags = TagRules.Tag.None;
            foreach (var category in CardRules.Categories)
                if (category.Matches(id))
                    categoryTags |= category.Tags;
            return (cardTags & ~disabledTags) | categoryTags;
        }

        public static IReadOnlyCollection<int> CardsWithTag(TagRules.Tag tag) => CardRules.Definitions
            .Where(rule => (rule.Tags & tag) != 0)
            .SelectMany(rule => rule.Ids).Distinct().ToArray();

        public static bool AnyCardWithTag(IEnumerable<int> ids, TagRules.Tag tag) =>
            ids.Any(id => (ForCard(id)?.Tags & tag) != 0);

        public static IReadOnlyCollection<int> AllowedTargets(int id, Context context, IReadOnlyCollection<int> candidates) =>
            ForCard(id)?.AllowedTargets?.Invoke(context, candidates) ?? candidates;

        public static int? OptionIndexFor(int id, Context context, int optionCount) =>
            ForCard(id)?.SelectOptionIndex?.Invoke(context, optionCount);

        public static Func<bool, DHint, DynamicCard, int>? TargetPriorityFor(int id) => ForCard(id)?.TargetPriority;
    }
}
