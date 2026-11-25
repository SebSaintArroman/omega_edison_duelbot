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
            bool NormalSummonUsed = true, bool ReinforcementHasSingleTarget = false, bool DarkGrepherAvailable = false,
            int PlannedSetCardId = 0,
            // Their spells/traps worth removing (set, or face-up Continuous / Field / Equip); -1 when the caller doesn't know.
            int OpponentRemovableSpellTraps = -1,
            // Our own face-up Future Fusion in the Draw Phase (Machina Jinzo): the one card of ours MST may target.
            bool OwnFutureFusionInDrawPhase = false,
            // An attack on us: fewer set answers (AnswersAnyThreat) than attackers left, and a direct hit with all their
            // attacks together short of our LP.
            bool AttackAnswersScarce = false, bool NonLethalDirectAttack = false,
            // Our face-up attack-position monsters (id, current ATK), and whether our Battle Phase is still ahead this turn (our Main Phase 1
            // with a Battle Phase on offer). Null / false when the caller doesn't know.
            IReadOnlyList<(int Id, int Attack)>? OwnFaceUpAttackers = null, bool BattleAhead = false,
            // Their monsters (id, ATK, DEF, face-up, attack position), their face-down spells/traps (-1 unknown), and the DEF a face-down
            // monster of theirs is assumed to have against a pierce (-1 unknown). Sirocco's lethal check (user, 2026-11-25).
            IReadOnlyList<(int Id, int Attack, int Defense, bool FaceUp, bool AttackPosition)>? OpponentMonsters = null,
            int OpponentSetSpellTraps = -1, int OpponentLikelySetDefense = -1,
            // Honest known or suspected in their hand (DeckBotBrain.HonestSuspected: the deck read's chance at HonestSuspectChance or more).
            bool HonestSuspected = false);

        internal sealed record Rule(int[] Ids, TagRules.Tag Tags,
            Func<Context, TagRules.Tag>? TagsToDisableWhen = null,
            Func<Context, IReadOnlyCollection<int>, IReadOnlyCollection<int>>? AllowedTargets = null,
            Func<Context, int, int?>? SelectOptionIndex = null,
            Func<bool, DHint, DynamicCard, int>? TargetPriority = null,
            CardCorrection? Correction = null,
            TagRules.Tag DiscardPickTag = TagRules.Tag.None,
            Func<Context, IReadOnlyList<int>, int?>? ForcedSearchPick = null,
            bool SendGoodDiscardFromDeck = false);

        public static bool RequiresGoodDiscardDeckSend(int id) => ForCard(id)?.SendGoodDiscardFromDeck == true;

        /// <summary>
        /// A per-card correction to a card's effect points (EffectEvaluator), added before the taper (BoardEvaluator.Taper): a rough
        /// last-resort lever for a card the scorer gets wrong (user, 2026-11-13: "don't give any cards this without my explicit
        /// approval"). General: wherever the card is scored; Hand: only while it's in a hand. Cards without one: 0.
        /// </summary>
        internal sealed record CardCorrection(double General = 0, double Hand = 0);

        /// <summary>The card's correction where it is: General, plus Hand while it's in a hand (0 without one).</summary>
        public static double CorrectionFor(int id, bool inHand = false) =>
            ForCard(id)?.Correction is { } c ? c.General + (inHand ? c.Hand : 0) : 0;

        /// <summary>Tags for every card in a category; TagsToDisableWhen (card id, context) switches some off.</summary>
        internal sealed record CategoryRule(Func<int, bool> Matches, TagRules.Tag Tags,
            Func<int, Context, TagRules.Tag>? TagsToDisableWhen = null);

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
                    categoryTags |= category.Tags & ~(category.TagsToDisableWhen?.Invoke(id, context) ?? TagRules.Tag.None);
            var tags = (cardTags & ~disabledTags) | categoryTags;
            if (rule?.SendGoodDiscardFromDeck == true &&
                context.OwnDeck.Count > 0 && !context.OwnDeck.Any(CardRules.IsGoodDarkDiscard))
                tags |= TagRules.Tag.DoNotActivate;
            return tags;
        }

        public static IReadOnlyCollection<int> CardsWithTag(TagRules.Tag tag) => CardRules.Definitions
            .Where(rule => (rule.Tags & tag) != 0)
            .SelectMany(rule => rule.Ids).Distinct().ToArray();

        private static bool HasUnconditionalTag(int id, TagRules.Tag tag) =>
            ((ForCard(id)?.Tags ?? TagRules.Tag.None) & tag) != 0 ||
            CardRules.Categories.Any(category => category.TagsToDisableWhen == null &&
                (category.Tags & tag) != 0 && category.Matches(id));

        public static bool AnyCardWithTag(IEnumerable<int> ids, TagRules.Tag tag) =>
            ids.Any(id => HasUnconditionalTag(id, tag));

        public static bool NeverSets(int id) => HasUnconditionalTag(id, TagRules.Tag.NeverSet);

        public static bool NeverSets(DynamicCard card) =>
            (card.Type & (int)CardType.Monster) != 0 && card.Level >= 5 || NeverSets(card.ID);

        public static CardPosition? RequiredSummonPosition(int id, IReadOnlyCollection<byte>? positions) =>
            positions?.Contains((byte)CardPosition.FaceUpAttack) == true &&
            HasUnconditionalTag(id, TagRules.Tag.NeverSummonInDefence) ? CardPosition.FaceUpAttack : null;

        public static IReadOnlyCollection<int> AllowedTargets(int id, Context context, IReadOnlyCollection<int> candidates) =>
            ForCard(id)?.AllowedTargets?.Invoke(context, candidates) ?? candidates;

        public static int? OptionIndexFor(int id, Context context, int optionCount) =>
            ForCard(id)?.SelectOptionIndex?.Invoke(context, optionCount);

        public static Func<bool, DHint, DynamicCard, int>? TargetPriorityFor(int id) => ForCard(id)?.TargetPriority;

        /// <summary>While this card is on our field, our discards are limited to cards with this tag when any is offered.</summary>
        public static TagRules.Tag DiscardPickTagFor(int id) => ForCard(id)?.DiscardPickTag ?? TagRules.Tag.None;

        /// <summary>The card this card's search must take (its single pick from the deck), or null for a free choice.</summary>
        public static int? ForcedSearchPickFor(int id, Context context, IReadOnlyList<int> offered) =>
            ForCard(id)?.ForcedSearchPick?.Invoke(context, offered);
    }
}
