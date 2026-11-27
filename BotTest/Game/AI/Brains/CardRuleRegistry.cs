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
            bool HonestSuspected = false,
            // Our face-up monsters, either position (War Chariot's set rule). Null when the caller doesn't know.
            IReadOnlyCollection<int>? OwnFaceUpMonsterIds = null,
            // Our spells/traps on the field, set or face-up (a set Raigeki Break's discard). Null when the caller doesn't know.
            IReadOnlyCollection<int>? OwnSpellTrapIds = null,
            // The phase (DuelPhase as int; -1 unknown) and how many of their face-down spells/traps were set this turn (-1 unknown): MST's
            // End Phase snipe (user, 2026-11-26).
            int Phase = -1, int OpponentSetThisTurn = -1)
        {
            /// <summary>
            /// Stardust Dragon on their field: the flag a caller set, or read from their monsters when the caller listed them (user,
            /// 2026-11-25: Stardust's rules must hold wherever the card rules are asked, not only where a threat context set the flag).
            /// </summary>
            public bool StardustUp => OpponentHasStardustDragon ||
                OpponentMonsters?.Any(m => m.FaceUp && Search.SearchHost.Is(m.Id, CardId.StardustDragon)) == true ||
                OpponentMonsterIds?.Any(id => id > 0 && Search.SearchHost.Is(id, CardId.StardustDragon)) == true;
        }

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
        /// <param name="HandUnlessTributable">The Hand part applies only while its holder can't Tribute Summon it (normal summon available
        /// and enough monsters on their field to tribute); then it keeps its full hand value. Applied where the holder's field is known
        /// (TributableHandCorrection), not in the card's static score.</param>
        /// <param name="Grave">Added to the card's graveyard value (CardValues), in card units: a card the effect reader can't see working from
        /// the GY (Versago's fusion substitute; user, 2026-11-26).</param>
        internal sealed record CardCorrection(double General = 0, double Hand = 0, bool HandUnlessTributable = false, double Grave = 0);

        /// <summary>The graveyard part of the card's correction (0 without one).</summary>
        public static double GraveCorrectionFor(int id) => ForCard(id)?.Correction?.Grave ?? 0;

        /// <summary>The card's correction where it is: General, plus Hand while it's in a hand (0 without one). A conditional Hand part
        /// (HandUnlessTributable) isn't included: see TributableHandCorrection.</summary>
        public static double CorrectionFor(int id, bool inHand = false) =>
            ForCard(id)?.Correction is { } c ? c.General + (inHand && !c.HandUnlessTributable ? c.Hand : 0) : 0;

        /// <summary>
        /// A conditional hand correction (HandUnlessTributable): 0 when its holder can Tribute Summon it now (normal summon still
        /// available, and as many monsters on their field as its tributes: 1 for Level 5-6, 2 for Level 7+), else the Hand part
        /// (user, 2026-11-25: "tytannial should retain its full hand value if the normal summon is still available and there are two
        /// monsters on the field that could be tributed for a tribute summon").
        /// </summary>
        public static double TributableHandCorrection(int id, int holderMonsters, bool normalSummonAvailable)
        {
            if (ForCard(id)?.Correction is not { HandUnlessTributable: true } c)
                return 0;
            int level = Search.SearchHost.Available ? Search.SearchHost.Cards.Get(id)?.Level ?? 0 : 0;
            int tributes = level >= 7 ? 2 : level >= 5 ? 1 : 0;
            return normalSummonAvailable && holderMonsters >= tributes ? 0 : c.Hand;
        }

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
            // Stardust Dragon negates destruction: with it on their field, an answer whose text destroys isn't an answer (user,
            // 2026-11-25: "if stardust is on the field all cards with answersanythreat that 'destroy' (not remove from field but
            // specifically use the word destroy) should lose their tag").
            if ((tags & TagRules.Tag.AnswersAnyThreat) != 0 && context.StardustUp && DestroysByText(id))
                tags &= ~TagRules.Tag.AnswersAnyThreat;
            if (rule?.SendGoodDiscardFromDeck == true &&
                context.OwnDeck.Count > 0 && !context.OwnDeck.Any(CardRules.IsGoodDarkDiscard))
                tags |= TagRules.Tag.DoNotActivate;
            return tags;
        }

        /// <summary>The card's text uses the word "destroy" (Stardust Dragon negates those).</summary>
        private static bool DestroysByText(int id) =>
            Search.SearchHost.Available && Search.SearchHost.Cards.Get(id)?.Text is string text &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"\bdestroy", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
