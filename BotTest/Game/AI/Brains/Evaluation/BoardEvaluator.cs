using BotTest.Game.AI.Search;
using DuelSim.Engine;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>Where a card of ours stands, for the deck brain's own card values (DeckBotBrain.SearchCardBonus).</summary>
    internal enum CardPlace { Hand, Field, Set, Grave }

    /// <summary>
    /// The MCTS position score where a line ends (0-1, 0.5 = even): a won or lost duel counts fully; otherwise each side's
    /// cards, life points and setups, ours against theirs, squashed into 0-1. Not a measured win chance: a yardstick to
    /// compare lines. Cards count by their effects and current ATK together (EffectEvaluator, never printed ATK), plus what
    /// our deck brain says its own cards are worth where they stand (a Treeborn Frog in the graveyard, Kalut in hand: user,
    /// 2026-11-11, "scored by the brain's rules about what cards are good"). Life points mostly at the low end.
    /// Runs on search threads: reads only card data and the line's end position.
    /// </summary>
    internal sealed class BoardEvaluator
    {
        internal bool RewardGladiatorBattles { get; init; }
        internal Func<bool, OmegaBotWrapper.Enum.DuelPhase, SideSummary, bool>? BackrowDoomRule { get; init; }
        /// <summary>Our deck is Machina Jinzo: MST may target our own Future Fusion in the Draw Phase (DeckBotBrain.SearchMstOwnFutureFusion).</summary>
        internal bool MstOwnFutureFusion { get; init; }
        /// <summary>Weight of a card's effect points (EffectEvaluator: a searcher or negate ~4) next to its body.</summary>
        [Tunable("Weight of a card's effect points (EffectEvaluator: a searcher or negate ~4) next to its body.")]
        private static readonly Tuned<double> FieldEffectWeight = new(0.25), HeldEffectWeight = new(0.1);

        /// <summary>A monster in the graveyard without a graveyard effect: this share of its field worth (user, 2026-11-18: 5%).</summary>
        [Tunable("A monster in the graveyard without a graveyard effect (or an override): this share of its field worth (face-up, printed ATK, field effects). Spells/traps without one: 0. User, 2026-11-18.")]
        public static readonly Tuned<double> GraveMonsterShare = new(0.025); // user, 2026-11-18: 2.5% (5% "feels a little high")

        /// <summary>Effect points are linear up to this, then taper (see Taper).</summary>
        [Tunable("Effect points are linear up to this, then taper (see Taper).")]
        public static readonly Tuned<double> TaperStart = new(2);
        /// <summary>How slowly the taper bends: 1 puts Absolute Zero (5.25 points with the "all" factor) at 3.45, Stardust (9) at 4.1, Shura (10) at 4.2.</summary>
        [Tunable("How slowly the taper bends: 1 puts Absolute Zero (5.25 points with the \"all\" factor) at 3.45, Stardust (9) at 4.1, Shura (10) at 4.2.")]
        public static readonly Tuned<double> TaperScale = new(1);

        /// <summary>
        /// Effect points after tapering (user, 2026-11-13: instead of a raw cap, "a log style tapering ... mostly linear up until
        /// 2, then starts tapering down"): x up to TaperStart, then TaperStart + TaperScale * ln(1 + (x - TaperStart) / TaperScale).
        /// </summary>
        public static double Taper(double points) =>
            points <= TaperStart ? Math.Max(0, points) : TaperStart + TaperScale * Math.Log(1 + (points - TaperStart) / TaperScale);

        /// <summary>How many card units of difference move the score from 0.5 to about 0.73 (the logistic's scale).</summary>
        [Tunable("How many card units of difference move the score from 0.5 to about 0.73 (the logistic's scale).")]
        public static readonly Tuned<double> Scale = new(3);

        private readonly CardValues _values;
        private readonly Func<int, CardPlace, double>? _cardBonus;
        private readonly int _unknownCards;
        private readonly double _explosiveness;

        /// <param name="cardBonus">The deck brain's own value of one of its cards where it stands (DeckBotBrain.SearchCardBonus).</param>
        /// <param name="unknownCards">Their hand cards we know nothing about: not played in
        /// the lines (no guessing), counted by the threat term instead.</param>
        /// <param name="explosiveness">How likely their deck is to have a big turn (0-1, the deck read; 0.5 when unknown).</param>
        public BoardEvaluator(CardValues values, Func<int, CardPlace, double>? cardBonus,
            int unknownCards = 0, double explosiveness = 0.5)
        {
            _values = values;
            _cardBonus = cardBonus;
            _unknownCards = unknownCards;
            _explosiveness = explosiveness;
        }

        /// <summary>Each unknown card's share of the risk to our board (the explosiveness scales it). Tune from logs.</summary>
        [Tunable("Each unknown card's share of the risk to our board (the explosiveness scales it). Tune from logs.")]
        public static readonly Tuned<double> UnknownCardRisk = new(0.08);

        [Tunable("Their unknown cards' risk scales with this plus their deck's explosiveness (0.5 + explosiveness).")]
        public static readonly Tuned<double> ExplosivenessBase = new(0.5);

        /// <summary>How much we have to press: the risk tolerance above its base, 0-1 (DeckBotBrain.RiskPressNow, CSV S13). Fear of their
        /// cards drops by up to 60% (RiskDiscount) and damage to them counts up to double.</summary>
        public double RiskPress { get; init; }

        /// <summary>Their life points as the search began: gains above this count for nothing (CSV S6). 0: not set.</summary>
        public int TheirStartLp { get; init; }
        /// <summary>The deck brain's next-turn value of our side at the end of a line, given the cards we activated in our turn
        /// (DeckBotBrain.SearchNextTurnValue).</summary>
        public Func<SideSummary, IReadOnlyCollection<int>, double>? NextTurnValue { get; init; }
        /// <summary>The deck brain's cards on our field lost later regardless as our turn ends (Future Fusion's Overdragon), given the cards
        /// we activated in our turn: which codes are lost, or null for none (DeckBotBrain.SearchFieldLostAfterTurn).</summary>
        public Func<SideSummary, IReadOnlyCollection<int>, Func<int, bool>?>? FieldLostAfterTurn { get; init; }
        /// <summary>Our life points at the decision: the life points we lose by the end of a line count against it (OwnDamageShare).</summary>
        public int OurStartLp { get; init; }

        [Tunable("Our life points lost by the end of a line count against it at this share of DamageBonusPer1000 (continuous: 0.5 x 0.1 per 1000 = 0.05 per 1000 lost). User, 2026-11-18: \"make it count 50% of the opponent's one\".")]
        public static readonly Tuned<double> OwnDamageShare = new(0.8333); // user, 2026-11-19: taking damage doubled to 0.10 per 1000 (0.8333 x 0.12); was 0.5 x 0.1

        /// <summary>Bonus in card units for each card we draw after the searched decision.</summary>
        [Tunable("Bonus in card units for each card we draw after the searched decision.")]
        public static readonly Tuned<double> DrawBonus = new(0.5);

        /// <summary>Scale for the change in counted field cards, hand value, and Graveyard value.</summary>
        [Tunable("Scale for CardAdvantageDelta. At 1, known hand cards and set traps use their hand value, and other field cards count as one.")]
        public static readonly Tuned<double> PlusOneBonus = new(1.0);

        [Tunable("The base value of a known hand card in CardAdvantageDelta, before effects and printed ATK.")]
        public static readonly Tuned<double> HandCardBase = new(0.8);

        [Tunable("Printed monster ATK is divided by this before adding it to held effect points.")]
        public static readonly Tuned<double> HandAtkScale = new(2000);

        /// <summary>Our cards minus theirs, with standalone Graveyard values and live Vayu pairs scored separately.</summary>
        internal readonly record struct AdvantageCounts(int OurHand, int TheirHand, int OurMonsters, int TheirMonsters,
            int OurSpells, int TheirSpells, int GravePairs, double OurHandAdjustment, double TheirHandAdjustment,
            double OurSetTrapAdjustment, double TheirSetTrapAdjustment,
            double OurGraveValue, double TheirGraveValue, double PressuringHandValue, double PressuringGraveValue)
        {
            public double Net => OurHand - TheirHand + MonsterCardValue * (OurMonsters - TheirMonsters) + OurSpells - TheirSpells +
                GravePairs + OurGraveValue - TheirGraveValue;
        }

        public double CardAdvantage(SideSummary ours, SideSummary theirs) => CountAdvantage(ours, theirs).Net;

        /// <summary>
        /// Each side's whole card advantage score, as CardAdvantageDelta counts it (Mcts.Score): hand count and hand values, non-token
        /// monsters at MonsterCardValue, spells/traps on the field and set trap values, graveyard values and live Vayu pairs, scaled by
        /// PlusOneBonus. For the strategic state (StandingState; user, 2026-11-24: "it should be the total card advantage score").
        /// </summary>
        public (double Ours, double Theirs) CardAdvantageScores(SideSummary ours, SideSummary theirs)
        {
            var c = CountAdvantage(ours, theirs);
            double our = c.OurHand + c.OurHandAdjustment + MonsterCardValue * c.OurMonsters + c.OurSpells + c.OurSetTrapAdjustment +
                         c.GravePairs + c.OurGraveValue;
            double their = c.TheirHand + c.TheirHandAdjustment + MonsterCardValue * c.TheirMonsters + c.TheirSpells + c.TheirSetTrapAdjustment +
                           c.TheirGraveValue;
            return (our * PlusOneBonus, their * PlusOneBonus);
        }

        internal AdvantageCounts CountAdvantage(SideSummary ours, SideSummary theirs, bool pressuring = false)
        {
            ours = ScoringSide(ours);
            static int Monsters(SideSummary s) => s.Monsters.Count(m => (m.Type & 0x4000) == 0);
            double ourGraveValue = ours.GraveCodes.Where(code => code > 0).Sum(code => GraveValue(code, 0));
            double theirGraveValue = theirs.GraveCodes.Where(code => code > 0).Sum(code => GraveValue(code, 1));
            // A live Vayu pair has its own score, so remove both cards' standalone values.
            int gravePairs = GraveSetup != null && ours.ExtraCodes.Count > 0
                ? GraveSetup(ours, code => ourGraveValue -= GraveValue(code, 0))
                : 0;
            return new(ours.Hand, theirs.Hand, Monsters(ours), Monsters(theirs), ours.SpellCodes.Count, theirs.SpellCodes.Count,
                gravePairs, HandAdjustment(ours, 0), HandAdjustment(theirs, 1),
                SetTrapAdjustment(ours, 0), SetTrapAdjustment(theirs, 1),
                ourGraveValue, theirGraveValue,
                pressuring ? ours.HandCodes.Where(IsMonster).Sum(code => HandCardValue(code, 0)) : 0,
                pressuring ? ourGraveValue + gravePairs : 0);
        }

        internal double HandCardValue(int code, int side)
        {
            // Unknown opponent cards already count as one each. Keep their value at one until we know the card.
            if (code <= 0 || code == SearchView.InertCard || SearchHost.Cards.Get(code) is not { } printed)
                return 1;
            int atk = (printed.Type & 0x1) != 0 ? printed.Attack : 0;
            var value = _values.Get(code, side);
            // Our cards with searches / Special Summons / sends from a zone: valued by what they can fetch now (TargetEffects), not keyword
            // points. A spell or trap of this kind has no base value: it is worth what it turns into (user, 2026-11-24).
            // (Their cards: we don't know their deck, so keyword points as before. Re-entered while valuing its own targets: the same.)
            if (side == 0 && value.HasTargetEffects && OwnDeck != null && (_valuing ??= new()).Add(code))
            {
                try
                {
                    bool spellOrTrap = (printed.Type & 0x1) == 0;
                    return (spellOrTrap ? 0 : HandCardBase) + HeldEffectWeight * Taper(value.HeldUntargeted + atk / HandAtkScale) +
                        TargetEffectWeight * TargetEffectValue(code, spellOrTrap) + Bonus(code, CardPlace.Hand, side);
                }
                finally
                {
                    _valuing.Remove(code);
                }
            }
            return HandCardBase + HeldEffectWeight * Taper(value.Held + atk / HandAtkScale) + Bonus(code, CardPlace.Hand, side);
        }

        /// <summary>Our zones at the decision (the search's view): what a held search / Special Summon / send can fetch "at that moment".</summary>
        public IReadOnlyCollection<int>? OwnDeck { get; init; }
        public IReadOnlyCollection<int>? OwnHand { get; init; }
        public IReadOnlyCollection<int>? OwnGrave { get; init; }
        public IReadOnlyCollection<int>? OwnBanished { get; init; }
        /// <summary>Our monsters at the decision (codes): what can pay a Tribute cost.</summary>
        public IReadOnlyCollection<int>? OwnMonsters { get; init; }

        [Tunable("Scale on the value of a held card's searches / Special Summons / sends from a zone (TargetEffects), in card units. User, 2026-11-24: a monster is worth its best target, a spell or trap its average target (no base value); a discard cost only has to be payable.")]
        public static readonly Tuned<double> TargetEffectWeight = new(1.0);

        // Cards whose hand value is being worked out on this thread: valuing its own targets or costs comes back to them (Dark Grepher's
        // discard cost is another Dark Grepher), and those count by their keyword points instead.
        [ThreadStatic] private static HashSet<int>? _valuing;
        // Worked out once per card per search (one evaluator per search, its zones fixed at the decision).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> _targetValue = new();

        /// <summary>
        /// What a held card's searches / Special Summons from a zone / sends from the deck are worth now (card units): per effect, the best
        /// target for a monster or the average target for a spell or trap (user, 2026-11-24: there's no other inherent incentive to use
        /// them), when its discard cost can be paid. A searched card counts at its hand value, a summoned monster at its worth on the field (a card
        /// on the field plus its body and field effects) less what it was worth where it came from, a sent card at its GY value.
        /// Return from the Different Dimension: its five best banished monsters together.
        /// </summary>
        internal double TargetEffectValue(int code, bool spellOrTrap) =>
            _targetValue.GetOrAdd(code, c => ComputeTargetValue(c, spellOrTrap));

        private double ComputeTargetValue(int code, bool spellOrTrap)
        {
            if (SearchHost.Cards.Get(code)?.Text is not { } text)
                return 0;
            bool Fits(string filter, int id) => SearchHost.Cards.Get(id) is { } c && TargetEffects.Fits(filter, c);
            double total = 0;
            foreach (var effect in TargetEffects.Effects(text))
            {
                // A discard cost only has to be payable: the card paid leaves the hand when the effect is used, and the score loses it then.
                // Taking it off here too charged it twice and made Dark Grepher cheap to discard (user, 2026-11-24).
                if (effect.CostFilter is { } costFilter && !Without(OwnHand, code).Any(id => Fits(costFilter, id)))
                    continue;
                // A Tribute cost is taken off (user, 2026-11-24: "discarding from hand maybe doesn't justify the reduction, but tributing does"):
                // the cheapest monster that pays it, what it's lost on the field less what it's worth in the GY (Treeborn: almost free).
                // Our monsters at the decision, and the card itself once it's out unless the text excludes it.
                double tributeCost = 0;
                if (effect.TributeFilter is { } tributeFilter)
                {
                    var payers = (OwnMonsters ?? Array.Empty<int>()).Where(id => Fits(tributeFilter, id)).ToList();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(tributeFilter, @"\bother\b|except this card", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                        Fits(tributeFilter, code))
                        payers.Add(code);
                    if (payers.Count == 0)
                        continue;
                    tributeCost = payers.Min(id => Math.Max(0, FieldWorth(id) - GraveValue(id, 0)));
                }
                var values = new List<double>();
                void From(IEnumerable<int>? zone, Func<int, double> worth)
                {
                    if (zone == null)
                        return;
                    foreach (int id in zone.Distinct())
                        if (Fits(effect.Filter, id))
                            values.Add(worth(id));
                }
                switch (effect.Kind)
                {
                    case TargetEffects.Kind.Search:
                        From(OwnDeck, id => HandCardValue(id, 0));
                        break;
                    case TargetEffects.Kind.Send:
                        From(OwnDeck, id => GraveValue(id, 0));
                        break;
                    case TargetEffects.Kind.ReturnBanished:
                        From(OwnBanished?.Where(id => SearchHost.Cards.Get(id) is { } c && (c.Type & 0x1) != 0), FieldWorth);
                        break;
                    default:
                        if ((effect.From & TargetEffects.Zone.Deck) != 0)
                            From(OwnDeck, FieldWorth);
                        if ((effect.From & TargetEffects.Zone.Hand) != 0)
                            From(Without(OwnHand, code), id => FieldWorth(id) - HandCardValue(id, 0));
                        if ((effect.From & TargetEffects.Zone.Grave) != 0)
                            From(OwnGrave, id => FieldWorth(id) - GraveValue(id, 0));
                        break;
                }
                if (values.Count == 0)
                    continue;
                double target = effect.Kind == TargetEffects.Kind.ReturnBanished ? values.OrderByDescending(v => v).Take(5).Sum()
                    : spellOrTrap ? values.Average() : values.Max();
                total += Math.Max(0, target - tributeCost);
            }
            return total;
        }

        // A monster brought out: a card on the field (CardAdvantageDelta) plus its body and field effects (BoardScore).
        private double FieldWorth(int id)
        {
            if (SearchHost.Cards.Get(id) is not { } card || (card.Type & 0x1) == 0)
                return 0;
            return PlusOneBonus * MonsterCardValue + MonsterBase + AtkValue(card.Attack) + FieldEffectWeight * Taper(_values.Get(id, 0).Field) + Bonus(id, CardPlace.Field, 0);
        }

        // A zone without one copy of this card (it can't fetch or pay with itself).
        private static List<int> Without(IReadOnlyCollection<int>? zone, int code)
        {
            var left = zone?.ToList() ?? new List<int>();
            left.Remove(code);
            return left;
        }


        private double HandAdjustment(SideSummary side, int player) =>
            side.HandCodes.Sum(code => HandCardValue(code, player) - 1);

        private double SetTrapAdjustment(SideSummary side, int player) =>
            side.SpellCodes.Where(card => !card.FaceUp && IsKnownTrap(card.Code))
                .Sum(card => HandCardValue(card.Code, player) - 1) +
            (player == 1 ? UnknownSetsIn(side) * UnknownSetFutureValue : 0);

        /// <summary>
        /// Each unknown set card of theirs, on top of counting as a card: what it still threatens if it stays set, weighted as a trap
        /// sprung on a bait attacker is (WipeTrapCost / SingleTrapCost: Mirror Force 2, D-Prison 1.5 with the card). At least single
        /// removal's weight, more by the chance this one set card is a wipe (the backrow read). So destroying one (Lyla, MST) gains
        /// 1.5 to 2. User, 2026-11-24: "destroying a card should have just factored into the score ... it should be the same weighting
        /// as the bait attacker"; "it should average over all things, there's no way it could be less than 1.5".
        /// </summary>
        private double UnknownSetFutureValue =>
            SingleTrapFutureValue + Math.Min(1, PerSetChance(MirrorForceChance) + PerSetChance(TorrentialChance)) *
            Math.Max(0, WipeTrapFutureValue - SingleTrapFutureValue);

        /// <summary>The chance one unknown set card of theirs is X, from "any of their unknown set cards is X" (as ChanceWith).</summary>
        private double PerSetChance(double anyChance) =>
            anyChance <= 0 || UnknownSets <= 0 ? 0 : 1 - Math.Pow(1 - Math.Min(anyChance, 0.999), 1.0 / UnknownSets);

        private static bool IsMonster(int code) =>
            code > 0 && code != SearchView.InertCard && SearchHost.Cards.Get(code) is { } card && (card.Type & 0x1) != 0;

        private static bool IsKnownTrap(int code) =>
            code > 0 && code != SearchView.InertCard && SearchHost.Cards.Get(code) is { } card && (card.Type & 0x4) != 0;

        private static SideSummary ScoringSide(SideSummary side)
        {
            if (!side.HandCodes.Contains(SearchView.InertCard) && !side.GraveCodes.Contains(SearchView.InertCard))
                return side;
            var hand = side.HandCodes.Where(code => code != SearchView.InertCard).ToList();
            var grave = side.GraveCodes.Where(code => code != SearchView.InertCard).ToList();
            return side with { Hand = hand.Count, HandCodes = hand, Grave = grave.Count, GraveCodes = grave };
        }

        public double RiskDiscount => 1 - RiskDiscountMax * Math.Clamp(RiskPress, 0, 1);

        /// <summary>The chances their unknown set cards hold these (the brain's backrow read; 0 under Jinzo / Royal Decree).</summary>
        public double MirrorForceChance { get; init; }
        /// <summary>Their unknown set spells/traps: each a card of theirs (their risk to our board is Backrow's, not Threat's).</summary>
        public int UnknownSets { get; init; }
        public double TorrentialChance { get; init; }
        /// <summary>The chance one of them is any other stopper (Bottomless, D-Prison, Sakuretsu...), beyond the two above.</summary>
        public double OtherStopChance { get; init; }

        [Tunable("A line that attacked gets this much extra (card units) per 1000 damage dealt in our turn, in proportion (no cap). Small: damage is mostly the clock's business.")]
        public static readonly Tuned<double> DamageBonusPer1000 = new(0.12); // user, 2026-11-19: +20% (was 0.1)

        [Tunable("A line that wins: this many card units on top of its position, times the chance the win really works (WinChance). Large.")]
        public static readonly Tuned<double> WinBonus = new(10);
        [Tunable("A won line: each card of ours it used up costs this much (card units) in the win's part of its score; the board it leaves doesn't count there (user, 2026-11-18).")]
        public static readonly Tuned<double> WinCardCost = new(1.0);

        /// <summary>
        /// The chance a win found in a line really happens: none of their unknown set cards is a stopper that would have fired (the lines never
        /// fire them): Mirror Force (if we attacked), Torrential Tribute, and any other stopper (Bottomless, D-Prison...), each at the deck
        /// read's chance. Their hand traps (Honest, Gorz) aren't counted here yet.
        /// </summary>
        public double WinChance(bool attacked, SideSummary? theirsAtEnd = null, SideSummary? oursAtEnd = null, bool? firstDirectHitLethal = null,
            bool usedMonsterEffect = false)
        {
            static double Not(double p) => 1 - Math.Clamp(p, 0, 1);
            // Every set card they had as the decision began counts, even one the line destroyed: they can chain it on the way (user,
            // 2026-11-24). While a face-up Jinzo / Royal Decree negates traps (user, 2026-11-24: "all traps were negated so there was no
            // risk"), only the stoppers that aren't traps (Book of Moon...) can.
            bool trapsOff = theirsAtEnd is { } th && oursAtEnd is { } ou && TrapsNegatedIn(ou, th);
            double mirror = trapsOff ? 0 : MirrorForceChance;
            double torrential = trapsOff ? 0 : TorrentialChance;
            double other = trapsOff ? QuickPlayStopChance : OtherStopChance;
            double chance = (attacked ? Not(mirror) : 1) * Not(torrential) * Not(other);
            // Herald of Orange Light (user, 2026-11-24): a monster effect of ours on the way to the win could be negated.
            if (usedMonsterEffect)
                chance *= Not(OrangeLightChance);
            // Monsters that work from their hand (user, 2026-11-17): Honest and Kalut only with an attack-position LIGHT / Blackwing monster
            // of theirs out (set in HonestChance / KalutChance), Battle Fader against any attack, Gorz when the win's direct hit came into a
            // field with no cards of theirs.
            if (attacked)
            {
                chance *= Not(HonestChance) * Not(KalutChance) * Not(BattleFaderChance);
                // Gorz comes out only after a direct hit they survive: not when the turn's first direct hit (whichever monster made it in
                // the attack order, after any attacks into their monsters) was lethal (user, 2026-11-24: DAD's 2800 into 2600 LP).
                if (theirsAtEnd is { } t && t.Monsters.Count == 0 && t.SpellCodes.Count == 0 && firstDirectHitLethal != true)
                    chance *= Not(GorzChance);
            }
            return chance;
        }

        /// <summary>No trap can be activated: a face-up, unnegated Jinzo on either side, or a face-up Royal Decree.</summary>
        public static bool TrapsNegatedIn(SideSummary ours, SideSummary theirs) =>
            ours.Monsters.Concat(theirs.Monsters).Any(m => m.FaceUp && !m.Negated && SearchHost.Is(m.Code, Jinzo)) ||
            ours.SpellCodes.Concat(theirs.SpellCodes).Any(s => s.FaceUp && SearchHost.Is(s.Code, RoyalDecree));

        /// <summary>The chance one of their unknown sets is a stopper that isn't a trap (Book of Moon...): what's left under Jinzo / Royal Decree.</summary>
        public double QuickPlayStopChance { get; init; }

        /// <summary>Their hand monsters that stop a lethal battle, each the deck read's chance it's in their hand (0 when it can't act here:
        /// Honest needs an attack-position LIGHT monster of theirs, Kalut a Blackwing).</summary>
        public double HonestChance { get; init; }
        public double KalutChance { get; init; }
        public double GorzChance { get; init; }
        public double BattleFaderChance { get; init; }
        /// <summary>Herald of Orange Light in their hand (with another Fairy): negates a monster effect of ours on the way to the win.</summary>
        public double OrangeLightChance { get; init; }

        /// <param name="penalty">Strong suggestions the line went against (card units, see DeckBotBrain.PlanSuggestions).</param>
        /// <param name="ourTurnEnd">Our side and theirs as our turn ended (null if the line didn't get there), and whether we attacked.</param>
        /// <param name="winRisk">For a won line: the risk it ran (its exposure, card units), ranked first.</param>
        /// <param name="cardsSpent">For a won line: our cards it used up, ranked second.</param>
        /// <param name="length">For a won line: its length (prompts), ranked last.</param>
        public double Evaluate(DuelHost host, double penalty = 0, (SideSummary Ours, SideSummary Theirs, bool Attacked)? ourTurnEnd = null,
            double winRisk = 0, int cardsSpent = 0, int length = 0, double center = 0, IReadOnlyList<int>? noClock = null, IReadOnlyCollection<int>? activatedThisTurn = null,
            IDictionary<string, double>? parts = null, IReadOnlyDictionary<string, double>? centerParts = null)
        {
            // The score breakdown (parts, card units): each term of the line's board difference by name, the position terms against the
            // decision's own (centerParts), so the parts add up to the line's difference from the position now (penalty aside: the caller
            // records its own parts of it). Lethal exposure scales the squashed score and isn't a card-unit part: Lethal holds it.
            void Part(string name, double value)
            {
                if (parts != null && value != 0)
                    parts[name] = (parts.TryGetValue(name, out var had) ? had : 0) + value;
            }
            // A loss is 0. A win in the line is scored like any other line, plus a large bonus scaled down by the chance it really works: the
            // lines never fire their unknown cards, so a "win" through their set cards is only as sure as those cards being blanks (user,
            // 2026-11-17: "if the win isn't a sure thing, it shouldn't be counted as a win for the line. why not keep the win as a large line
            // bonus just get a partial amount of that, scaled down by the chance of it working?").
            bool won = host.Result is { Winner: 0 };
            if (host.Result is { Winner: >= 0 } && !won)
                return 0;
            var ours = ScoringSide(host.Summary(0));
            var theirs = host.Summary(1);
            // With a breakdown: each side's categories as sub-rows of its part (their side negated, as it counts against us).
            double ourSide = parts == null ? BoardScore(ours, 0, theirs) : BoardScore(ours, 0, theirs, (c, v) => Part(OurBoardDelta + SubPart + c, v));
            double theirSide = parts == null ? BoardScore(theirs, 1, ours) : BoardScore(theirs, 1, ours, (c, v) => Part(TheirBoardDelta + SubPart + c, -v));
            double dominance = Dominance(ours, theirs, 0) - Dominance(theirs, ours, 1), threat = Threat(ours);
            double backrow = ourTurnEnd is { } end ? Backrow(end.Ours, end.Theirs, end.Attacked) : 0;
            double diff = ourSide - theirSide + dominance - threat - penalty - backrow;
            if (parts != null)
            {
                double Root(string name) => centerParts != null && centerParts.TryGetValue(name, out var v) ? v : 0;
                // The sub-rows against the decision's own (a category there and gone at the line's end shows as its loss).
                if (centerParts != null)
                    foreach (var (name, v) in centerParts)
                        if (name.Contains(SubPart))
                            Part(name, -v);
                parts[OurBoardDelta] = ourSide - Root(OurBoardDelta);
                parts[TheirBoardDelta] = -theirSide - Root(TheirBoardDelta);
                Part(DominancePart, dominance - Root(DominancePart));
                Part(ThreatPart, -threat - Root(ThreatPart));
                // The clocks as the decision stands: a line adds its own below (a won line none).
                Part(OurClockPart, -Root(OurClockPart));
                Part(TheirClockPart, -Root(TheirClockPart));
                Part("Their backrow on our board", -backrow);
            }
            double before = diff;
            if (ourTurnEnd is { Attacked: true } attackedEnd)
            {
                // Damage dealt in our turn: a little extra, more for more (user, 2026-11-16: "a small bonus for dealing direct damage"; 2026-11-17:
                // "can we scale it by amount of damage dealt? ... I'd like this to only ever be small, but more attack is better than less").
                // It replaces the old 1500+ "big hit" bonus (Bora's pierce). Damage is mostly the clock's business (BoardAssessment.ClockValue).
                if (TheirStartLp > 0 && attackedEnd.Theirs.LifePoints < TheirStartLp)
                    diff += DamageBonusPer1000 * (TheirStartLp - attackedEnd.Theirs.LifePoints) / 1000.0;
            }
            Part("Damage dealt", diff - before);
            before = diff;
            // Our own life points lost by the end of the line, at half the damage bonus (user, 2026-11-18: Super-Nimble Mega Hamster switched to
            // attack position took 1600 more damage from Gorz for nothing, the loss inside the same clock count costing nothing).
            if (!won && OurStartLp > 0 && ours.LifePoints < OurStartLp)
                diff -= OwnDamageShare * DamageBonusPer1000 * (OurStartLp - ours.LifePoints) / 1000.0;
            Part("Our life points lost", diff - before);
            // Each side's clock on the other at the end of the line, as part of the board: ours on them counts for us, theirs on us against.
            if (!won)
            {
                double ourClock = BoardAssessment.ClockValue(WithoutMonsters(ours, noClock), theirs), theirClock = BoardAssessment.TheirClockShare * BoardAssessment.ClockValue(theirs, ours, TheirNextAttack(theirs));
                diff += ourClock - theirClock;
                Part(OurClockPart, ourClock);
                Part(TheirClockPart, -theirClock);
            }
            // Our face-up monsters summoned in Main Phase 2 (or on the duel's first turn, no battle possible) count only Main2BoardShare of
            // their board value (user, 2026-11-18: "yes, but mp2 sets get full value"): they can't attack this turn and stay exposed.
            double main2 = Main2BodyDiscount(ours, noClock);
            diff -= main2;
            Part("Monsters summoned in Main Phase 2", -main2);
            before = diff;
            // The deck's next-turn setups that depend on what we activated this turn (Black Whirlwind triggered: a good Normal Summon kept for
            // next turn).
            if (!won && NextTurnValue != null)
            {
                try { diff += NextTurnValue(ours, activatedThisTurn ?? Array.Empty<int>()); }
                catch { /* a deck goal that fails counts nothing */ }
            }
            Part("Deck's next-turn setup", diff - before);
            before = diff;
            // The deck's cards lost later anyway, read from our side as our turn ended (Future Fusion: Overdragon wipes our field), each at
            // what the board scorer says losing it costs (MonsterLoss / SpellLoss, as the wipe exposures count it).
            if (!won && FieldLostAfterTurn != null)
            {
                try
                {
                    var atEnd = ourTurnEnd?.Ours ?? ours;
                    if (FieldLostAfterTurn(atEnd, activatedThisTurn ?? Array.Empty<int>()) is { } lost)
                        diff -= atEnd.Monsters.Where(m => lost(m.Code)).Sum(m => MonsterLoss(m, 0)) +
                                atEnd.SpellCodes.Where(s => s.Code != SearchView.InertCard && lost(s.Code)).Sum(SpellLoss);
                }
                catch { /* a deck cost that fails counts nothing */ }
            }
            Part("Deck's cards lost after our turn", diff - before);
            // Gorz in our hand with our field empty: it can come out when they hit us directly (user, 2026-11-17: "if you have a gorz in hand,
            // you should get a small bonus for ending on an empty field because it'll let you use gorz").
            if (!won && ours.Monsters.Count == 0 && ours.SpellCodes.Count == 0 && ours.HandCodes.Any(c => SearchHost.Is(c, GorzTheEmissaryOfDarkness)))
            {
                diff += LineRules.GorzReadyBonus;
                Part("Gorz ready (empty field)", LineRules.GorzReadyBonus);
            }
            before = diff;
            // A won line: what it spent, not the board it leaves (user, 2026-11-18: "yes, on that change to winning lines": a superfluous Bora
            // summoned on the way to a win scored higher for the extra board). As an expected value, so a risky win compares like for like
            // with lines that don't win: if the win works (its chance), the decision's own position plus the win bonus, less WinCardCost per
            // card of ours the line used up; if it doesn't, the board the line actually ends with.
            if (won)
            {
                bool usedMonsterEffect = host.TurnPlayer == 0 &&
                    host.ActivatedInTurn(0, host.Turn).Any(code => (SearchHost.Type(code) & 0x1) != 0);
                double chance = WinChance(ourTurnEnd?.Attacked ?? true, theirs, ours, host.FirstDirectHitLethal, usedMonsterEffect);
                // An actually sure win (user, 2026-11-24: "a sure win (*actually* sure), then it doesn't matter how many cards it uses"): the
                // win chance alone decides ("the only cards in their hand that could matter" are Gorz, Battle Fader, Kalut, Honest and Herald
                // of Orange Light, all in WinChance). It scores above every line still alive (1, past the alive ceiling), whatever it spent.
                if (chance >= 1)
                {
                    Part("Sure win (above every line still alive)", center + WinBonus - before);
                    return 1.0;
                }
                // The bonus by the win's chance (user, 2026-11-24: "give the win bonus as 10*percentage chance of win"; 50% gets +5): the
                // expected value below already weighs it by the chance, so the bonus itself is the full WinBonus. Cards still costed.
                double bonus = WinBonus;
                diff = chance * (center + bonus - WinCardCost * Math.Max(0, cardsSpent) - penalty) + (1 - chance) * diff; // the rules it broke cost in both
                // Never below the line without its win (user, 2026-11-24: "dont let the winning bonus go negative, just cap it down at 0"):
                // a won line far ahead scored under the boards of lines that don't win, so Dark Armed Dragon went to defence to win less.
                diff = Math.Max(diff, before);
                Part("Win (by its chance, in place of the board)", diff - before);
            }
            // A position still alive sits clearly between a loss and a win: far behind, the squashed score is ~0 and a line that
            // survives (the Dupe lock at 300 LP) looked no better than one that dies (seen 2026-11-11: every option scored 0-2
            // and the bot summoned Thestalos into a lost board instead of locking).
            // Centred on the decision's own position (center: the root position; user, 2026-11-18): a line scores by how much better or
            // worse it ends than the position it started from, so one card counts the same when far behind or far ahead as when even (the
            // uncentred curve flattened differences there: Heavy Storm set at 10%, "0.0 better").
            double score = AliveFloor + (1 - 2 * AliveFloor) / (1 + Math.Exp(-(diff - center) / Scale));
            // The lines' unknown cards (their hand, their next draw) are inert, so a position that dies to any monster they
            // draw (300 LP, a 400-ATK Plaguespreader stuck in attack position: seen 2026-11-12, they drew Card Trooper) scored
            // as safe. Weigh the line by the chance they kill us from what we left as our turn ended.
            if (ourTurnEnd is { } turnEnd && !won)
            {
                double lethal = LethalExposure(turnEnd.Ours);
                score *= 1 - lethal;
                if (parts != null && lethal > 0)
                    parts[LethalPart] = lethal;
            }
            return score;
        }

        // The breakdown's position parts (named alike at the decision and at a line's end).
        internal const string OurBoardDelta = "BoardScoreDelta (ours)";
        internal const string TheirBoardDelta = "BoardScoreDelta (opponent)";
        internal const string OurAdvantageDelta = "CardAdvantageDelta (ours)";
        internal const string TheirAdvantageDelta = "CardAdvantageDelta (opponent)";
        internal const string DominancePart = "Board dominance";
        internal const string ThreatPart = "Their threat to our board";
        internal const string OurClockPart = "Our clock on them";
        internal const string TheirClockPart = "Their clock on us";
        /// <summary>Not a card-unit part: the chance they kill us from the board we left, which scales the line's score down.</summary>
        internal const string LethalPart = "~lethal";

        /// <summary>The root position by scoring term. Sub-rows break down each player's field value.</summary>
        public Dictionary<string, double> PositionParts(SideSummary ours, SideSummary theirs)
        {
            ours = ScoringSide(ours);
            // Each side's categories too, as sub-rows (theirs negated): used only in the breakdown.
            var subRows = new Dictionary<string, double>();
            void Sub(string name, double v) => subRows[name] = (subRows.TryGetValue(name, out var had) ? had : 0) + v;
            double ourSide = BoardScore(ours, 0, theirs, (c, v) => Sub(OurBoardDelta + SubPart + c, v));
            double theirSide = BoardScore(theirs, 1, ours, (c, v) => Sub(TheirBoardDelta + SubPart + c, -v));
            var result = new Dictionary<string, double>
            {
                [OurBoardDelta] = ourSide,
                [TheirBoardDelta] = -theirSide,
                [DominancePart] = Dominance(ours, theirs, 0) - Dominance(theirs, ours, 1),
                [ThreatPart] = -Threat(ours),
                [OurClockPart] = BoardAssessment.ClockValue(ours, theirs),
                [TheirClockPart] = -BoardAssessment.TheirClockShare * BoardAssessment.ClockValue(theirs, ours, TheirNextAttack(theirs)),
            };
            foreach (var (name, v) in subRows)
                result[name] = v;
            return result;
        }

        /// <summary>ATK steps of <see cref="AttackerOdds"/>: index i is "an attacker of at least i × 100 ATK".</summary>
        [Tunable("ATK steps of AttackerOdds: index i is \"an attacker of at least i × 100 ATK\".")]
        public static readonly Tuned<int> AttackerOddsStep = new(100);
        // Follows AttackerOddsStep as the tuning file sets it (a readonly field was built before the file loaded), but one array per step: the
        // deck read caches its attacker odds by this array's identity (seen 2026-11-19 profile: a new array per call missed the cache every
        // time, and the odds were recomputed at every brain decision of every line, 14% of the search).
        public static int[] AttackerThresholds
        {
            get
            {
                var cached = _thresholds;
                if (cached.Step == AttackerOddsStep && cached.Values != null)
                    return cached.Values;
                var built = (AttackerOddsStep, Enumerable.Range(0, 41).Select(i => i * AttackerOddsStep).ToArray());
                _thresholds = built;
                return built.Item2;
            }
        }
        private static (int Step, int[]? Values) _thresholds;

        /// <summary>
        /// The chance their unknown cards (hand plus the next draw) hold a monster they can attack with of at least each
        /// threshold's ATK (<see cref="AttackerThresholds"/>): from their likely deck lists (OpponentDeckPredictor), else
        /// <see cref="GenericAttackerOdds"/>. Null: no lethal-exposure check.
        /// </summary>
        public double[]? AttackerOdds { get; init; }
        public Func<SideSummary, Action<int>?, int>? GraveSetup { get; init; }

        // ------------------------------------------------------------------ monster size and dominance

        /// <summary>
        /// A monster's battle size in card units, by 50-ATK steps (user, 2026-11-13: "do increments of 50 atk, and do it as a
        /// table ... we'll start linear, but this isn't a linear scale": 1500 is better than 1400, 1600 hardly better than
        /// 1500, depending on the meta). One entry per 50 ATK, 0 to 5000; above 5000 scores as 5000. Linear for now (0.04 a
        /// step: 0.8 per 1000): edit the entries to reshape it.
        /// </summary>
        [Tunable("A monster's battle size in card units by ATK, one entry per 50 ATK from 0 to 5000 (above 5000 counts as 5000). Not linear: reshape it here.")]
        public static double[] AtkTable =
        {
            0.00, // 0 ATK
            0.01, // 50 ATK
            0.011, // 100 ATK
            0.012, // 150 ATK
            0.013, // 200 ATK
            0.014, // 250 ATK
            0.015, // 300 ATK
            0.02, // 350 ATK
            0.021, // 400 ATK
            0.022, // 450 ATK
            0.03, // 500 ATK
            0.031, // 550 ATK
            0.04, // 600 ATK
            0.041, // 650 ATK
            0.3, // 700 ATK
            0.31, // 750 ATK
            0.4, // 800 ATK
            0.41, // 850 ATK
            0.5, // 900 ATK
            0.51, // 950 ATK
            1, // 1000 ATK
            1.01, // 1050 ATK
            1.1, // 1100 ATK
            1.11, // 1150 ATK
            1.2, // 1200 ATK
            1.21, // 1250 ATK
            1.25, // 1300 ATK
            1.26, // 1350 ATK
            1.4, // 1400 ATK
            1.45, // 1450 ATK
            1.5, // 1500 ATK
            1.55, // 1550 ATK
            1.6, // 1600 ATK
            1.65, // 1650 ATK
            1.7, // 1700 ATK
            1.75, // 1750 ATK
            1.8, // 1800 ATK
            1.85, // 1850 ATK
            1.9, // 1900 ATK
            1.95, // 1950 ATK
            2, // 2000 ATK
            2.01, // 2050 ATK
            2.1, // 2100 ATK
            2.11, // 2150 ATK
            2.2, // 2200 ATK
            2.21, // 2250 ATK
            2.25, // 2300 ATK
            2.26, // 2350 ATK
            2.4, // 2400 ATK
            2.49, // 2450 ATK
            2.5, // 2500 ATK
            2.55, // 2550 ATK
            2.6, // 2600 ATK
            2.61, // 2650 ATK
            2.62, // 2700 ATK
            2.63, // 2750 ATK
            2.7, // 2800 ATK
            2.701, // 2850 ATK
            2.702, // 2900 ATK
            2.703, // 2950 ATK
            2.8, // 3000 ATK
            2.801, // 3050 ATK
            2.802, // 3100 ATK
            2.803, // 3150 ATK
            2.804, // 3200 ATK
            2.805, // 3250 ATK
            2.806, // 3300 ATK
            2.807, // 3350 ATK
            2.808, // 3400 ATK
            2.809, // 3450 ATK
            2.810, // 3500 ATK
            2.811, // 3550 ATK
            2.812, // 3600 ATK
            2.813, // 3650 ATK
            2.814, // 3700 ATK
            2.815, // 3750 ATK
            2.816, // 3800 ATK
            2.817, // 3850 ATK
            2.818, // 3900 ATK
            2.819, // 3950 ATK
            2.820, // 4000 ATK
            2.821, // 4050 ATK
            2.822, // 4100 ATK
            2.823, // 4150 ATK
            2.824, // 4200 ATK
            2.825, // 4250 ATK
            2.826, // 4300 ATK
            2.827, // 4350 ATK
            2.828, // 4400 ATK
            2.829, // 4450 ATK
            2.83, // 4500 ATK
            2.831, // 4550 ATK
            2.832, // 4600 ATK
            2.833, // 4650 ATK
            2.834, // 4700 ATK
            2.835, // 4750 ATK
            2.836, // 4800 ATK
            2.837, // 4850 ATK
            2.838, // 4900 ATK
            2.839, // 4950 ATK
            2.84, // 5000 ATK
        };

        /// <summary>The table value for this ATK (rounded down to its 50 step; beyond 5000, the last).</summary>
        public static double AtkValue(double power) => AtkScale * AtkTable[Math.Clamp((int)(Math.Max(0, power) / 50), 0, AtkTable.Length - 1)];

        [Tunable("Every ATK table value is multiplied by this. User, 2026-11-19: MonsterBase down to 0.3 (a 200-ATK Ryko was worth 0.61), stats scaled up to compensate: 1.15 keeps 1800-2000 attackers where they were.")]
        public static readonly Tuned<double> AtkScale = new(1.15);

        /// <summary>A monster's battle size: only the stat of the position it's in, ATK in attack, DEF (DefenceShare of it) in defence (user,
        /// 2026-11-18: "only give them the position they're in in that calc"; a defence-position Bora counted its 1700 ATK, so switching it to
        /// attack gained nothing).</summary>
        private static double Power((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m) =>
            m.AttackPosition ? m.Attack : m.Defense * DefenceShare;

        /// <summary>Bonus for the single largest face-up monster on the whole field, to its side (user, 2026-11-13: "move the
        /// dominance score down to just 0.1 for now, and only apply it if its the largest on both sides of the field ... nominal -
        /// a lever we can pull later"; the ATK table does most of the work).</summary>
        [Tunable("Bonus for the single largest face-up monster on the whole field, to its side (user, 2026-11-13: \"move the dominance score down to just 0.1 for now, and only apply it if its the largest on both sides of the field ... nominal - a lever we can pull later\"; the ATK table does most of the work).")]
        public static readonly Tuned<double> DominanceBonus = new(0.1);
        /// <summary>Extra when that monster is the one that dominated when the search started. 0 for now: a lever for later.</summary>
        [Tunable("Extra when that monster is the one that dominated when the search started. 0 for now: a lever for later.")]
        public static readonly Tuned<double> KeptDominanceBonus = new(0);

        /// <summary>Card code of each side's dominant monster at the search's start (0: none), for KeptDominanceBonus.</summary>
        public (int Ours, int Theirs) DominantAtStart { get; init; }

        public double Dominance(SideSummary side, SideSummary other, int sideIndex)
        {
            var top = side.Monsters.Where(m => m.FaceUp).OrderByDescending(m => m.Attack).FirstOrDefault();
            if (top.Code == 0 || top.Attack <= 0)
                return 0;
            // The largest on both sides: above every other face-up monster, theirs and ours.
            double best = other.Monsters.Where(m => m.FaceUp).Select(m => m.Attack).Concat(side.Monsters.Where(m => m.FaceUp && !m.Equals(top)).Select(m => m.Attack))
                .DefaultIfEmpty(0).Max();
            if (top.Attack <= best)
                return 0;
            int kept = sideIndex == 0 ? DominantAtStart.Ours : DominantAtStart.Theirs;
            return DominanceBonus + (kept != 0 && kept == top.Code ? KeptDominanceBonus : 0);
        }


        /// <summary>
        /// Without a deck read: the chance one unknown card is a Level 4 or lower monster of at least this ATK, by a typical
        /// deck (user, 2026-11-12: "1400 attackers are very common"). Then over all their unknown cards.
        /// </summary>
        public static double[] GenericAttackerOdds(int unknownCards) =>
            AttackerThresholds.Select(atk =>
            {
                int band = Array.FindIndex(GenericAttackerAtk, cap => atk <= cap);
                double perCard = band >= 0 && band < GenericAttackerChance.Length ? GenericAttackerChance[band] : 0;
                return 1 - Math.Pow(1 - perCard, Math.Max(0, unknownCards));
            }).ToArray();

        [Tunable("Without a deck read: the ATK bands of a typical deck's Level 4-or-lower attackers (paired with GenericAttackerChance; above the last band: 0).")]
        public static int[] GenericAttackerAtk = { 1400, 1600, 1800, 1900, 2100, 2400 };

        [Tunable("Without a deck read: the chance one unknown card is an attacker of at least that band's ATK (one per GenericAttackerAtk band).")]
        public static double[] GenericAttackerChance = { 0.35, 0.28, 0.2, 0.14, 0.05, 0.02 };

        /// <summary>
        /// The chance their unknown cards (hand plus the next draw) kill us as our turn ended: an attacker through our weakest
        /// attack-position monster, or directly into an empty field, for at least our life points (it needs our life points
        /// more ATK than that monster, or our life points direct). 0 when nothing they're likely to draw is enough.
        /// </summary>
        /// <summary>
        /// Sitting duck (C2, user 2026-11-16; reworked 2026-11-18): the lines' unknown cards of theirs are inert, so a monster of ours their
        /// next attacker would run over looked safe. Their next attacker as the user's rule assumes it: they summon and attack with the
        /// biggest Level 4-or-lower monster left in their deck, or, with a monster of theirs on the field to tribute, the biggest Level 5-6
        /// one (TheirNormalAttack, TheirTributeAttack); their face-up attack-position monsters attack too; known hand cards that boost an
        /// attacker are used (TheirKnownBoost: Kalut), and a known Honest wins any battle. As our turn ends: the most valuable face-up
        /// monster of ours that attacker would destroy (ATK in attack position, DEF in defence), at SittingDuckWeight.
        /// </summary>
        public double SittingDuckExposure(SideSummary ours, SideSummary theirs, bool lylaEffectUsed = false)
        {
            int attack = TheirNormalAttack;
            if (theirs.Monsters.Count > 0)
                attack = Math.Max(attack, TheirTributeAttack);
            foreach (var m in theirs.Monsters.Where(m => m.FaceUp && m.AttackPosition))
                attack = Math.Max(attack, m.Attack);
            if (attack <= 0)
                return 0;
            attack += TheirKnownBoost;
            double worst = 0;
            foreach (var m in ours.Monsters.Where(m => m.FaceUp))
            {
                if (ExpendableUntilOurNextTurn(m, lylaEffectUsed))
                    continue;
                int power = m.AttackPosition ? m.Attack : m.Defense;
                bool destroyed = TheirHonestKnown || (m.AttackPosition ? power <= attack : power < attack);
                if (destroyed)
                    // Losing a monster in DEF also loses the attacker it could be next turn.
                    worst = Math.Max(worst, MonsterLoss(Power(m) < m.Attack ? m with { AttackPosition = true } : m, 0));
            }
            return SittingDuckWeight * worst;
        }

        /// <summary>
        /// A monster of ours that's expendable as our turn ends, so the sitting duck leaves it out: a Lyla that used her destroy this turn
        /// (face-up in defence; her Main Phase effect, not her End Phase mill, which she does every turn she's face-up). User, 2026-11-24:
        /// "once she's used her effect she should be marked as expendable for one turn only. expendable cards shouldn't have sitting duck
        /// applied to them"; "lyla in defence at the start of our turn should lose her expendable tag": one spent on an earlier turn isn't.
        /// </summary>
        private static bool ExpendableUntilOurNextTurn((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m,
            bool lylaEffectUsed) =>
            lylaEffectUsed && m.FaceUp && !m.AttackPosition && IsLyla(m.Code);

        private static bool IsLyla(int code) =>
            SearchHost.Is(code, LylaLightswornSorceress) || SearchHost.Is(code, LylaLightswornSorceressPreErrata);

        [Tunable("Our face-up monsters summoned in Main Phase 2 (or on the duel's first turn) count this share of their board value; monster sets count in full. User, 2026-11-18.")]
        public static readonly Tuned<double> Main2BoardShare = new(0.25); // user, 2026-11-19: "strengthen the penalty for summoning something in mp2" (was 0.5)

        /// <summary>What our face-up Main Phase 2 summons (by card) lose of their board value: (1 - Main2BoardShare) of each one's worth.</summary>
        private double Main2BodyDiscount(SideSummary ours, IReadOnlyList<int>? codes)
        {
            if (codes == null || codes.Count == 0 || Main2BoardShare >= 1)
                return 0;
            var left = codes.ToList();
            double discount = 0;
            foreach (var m in ours.Monsters.Where(m => m.FaceUp))
                if (left.Remove(m.Code))
                {
                    var card = _values.Get(m.Code, 0);
                    discount += (1 - Main2BoardShare) * (MonsterBase + AtkValue(Power(m)) + (m.Negated ? 0 : FieldEffectWeight * Taper(card.Field)));
                }
            return discount;
        }

        /// <summary>This side without one monster per listed card (our Main Phase 2 summons, left out of our clock).</summary>
        private static SideSummary WithoutMonsters(SideSummary side, IReadOnlyList<int>? codes)
        {
            if (codes == null || codes.Count == 0)
                return side;
            var left = codes.ToList();
            var kept = side.Monsters.Where(m => !left.Remove(m.Code)).ToList();
            return side with { Monsters = kept };
        }

        /// <summary>
        /// The attacker they're assumed to summon next (as for the sitting duck): the biggest Level 4-or-lower left in their deck, or with a monster
        /// of theirs to tribute the biggest Level 5-6, plus a known hand boost (Kalut). Part of their clock on us (user, 2026-11-18: "assume they'll
        /// attack our weakest one for damage purposes"): a 100-ATK D.D. Crow left in attack position makes their clock faster.
        /// </summary>
        public int TheirNextAttack(SideSummary theirs)
        {
            int attack = Math.Max(TheirNormalAttack, theirs.Monsters.Count > 0 ? TheirTributeAttack : 0);
            return attack > 0 ? attack + TheirKnownBoost : 0;
        }

        /// <summary>Their next summon's ATK (biggest Level 4-or-lower left in their deck) and, with a monster to tribute, the biggest Level 5-6.</summary>
        public int TheirNormalAttack { get; init; }
        public int TheirTributeAttack { get; init; }
        /// <summary>ATK a known hand card of theirs adds to their attacker (Kalut: 1400), and whether they're known to hold Honest.</summary>
        public int TheirKnownBoost { get; init; }
        public bool TheirHonestKnown { get; init; }

        [Tunable("Sitting duck without deck-list data: their assumed Level 4-or-lower attacker's ATK.")]
        public static readonly Tuned<int> NoListNormalAttack = new(1800);

        [Tunable("Sitting duck without deck-list data: their assumed tribute attacker's ATK (user, 2026-11-18: 2400).")]
        public static readonly Tuned<int> NoListTributeAttack = new(2400);

        [Tunable("The sitting-duck loss counts this share (the assumed attacker is taken as certain: 1 = the whole monster).")]
        public static readonly Tuned<double> SittingDuckWeight = new(0.5); // 2026-11-18: assumed attacker taken as certain; half weight so monsters still get used (user)

        // ------------------------------------------------------------------ what each card is worth where it is (card units)

        [Tunable("A face-up monster's base worth, before its ATK (the ATK table) and its effects. User, 2026-11-24: 0 (was 0.3): a monster already counts as a card (MonsterCardValue) and gets its stats.")]
        public static readonly Tuned<double> MonsterBase = new(0); // user, 2026-11-24: "lets get rid of this base 0.3 all monsters get"
        [Tunable("A non-token monster on the field counts as this many cards in CardAdvantageDelta (other cards count 1). User, 2026-11-24: \"because monsters also get their stats, lower their base value for just being a card down to 0.7 on the field\".")]
        public static readonly Tuned<double> MonsterCardValue = new(0.7);

        // A trap they spring is a card spent (user, 2026-11-24: "count traps used as a card cost to the opponent"): the card itself plus how
        // dangerous it would still be later (a wipe left set threatens next turn's attackers too). The exposures below count our loss net of it.
        [Tunable("What a wipe trap (Mirror Force, Torrential Tribute) still threatens if it isn't used now, on top of the card itself (card units), in the exposures' trap cost. User, 2026-11-24: 1.0 to start.")]
        public static readonly Tuned<double> WipeTrapFutureValue = new(1.0);
        [Tunable("What single removal (D-Prison, Bottomless, Icarus Attack...) still threatens if it isn't used now, on top of the card itself (card units). User, 2026-11-24: 0.5 to start.")]
        public static readonly Tuned<double> SingleTrapFutureValue = new(0.5);
        private static double WipeTrapCost => PlusOneBonus * 1 + WipeTrapFutureValue;
        private static double SingleTrapCost => PlusOneBonus * 1 + SingleTrapFutureValue;
        [Tunable("A monster in defence position counts this share of its DEF as its battle size (its ATK doesn't count there). User, 2026-11-18: 100%.")]
        public static readonly Tuned<double> DefenceShare = new(1.0);
        [Tunable("A face-down monster that gains from being set (a flip, a floater, a graveyard effect): its base worth.")]
        public static readonly Tuned<double> SetFlipBase = new(0.9);
        // A set flip monster's effect is on the field already, ready to go: weighed like a field effect, so using it costs what it's worth
        // (user, 2026-11-18: "using a one-time effect like ryko's should have cost the value of that effect, while attacking should have had no
        // cost": at the held weight a set Ryko's destroy was ~0.3 card units, and Ryko was flipped for a kill an attack would have made).
        [Tunable("Weight of a set flip / floater monster's effect points (its effect still to be used), like FieldEffectWeight: using it costs that much.")]
        public static readonly Tuned<double> SetFlipEffectWeight = new(0.25);
        [Tunable("Any other face-down monster: just a wall (walls are worth little).")]
        public static readonly Tuned<double> WallValue = new(0.3);
        [Tunable("A face-up spell/trap's base worth, before its lasting effect.")]
        public static readonly Tuned<double> FaceUpSpellBase = new(0.3);
        [Tunable("A set normal / continuous / field / equip spell's base worth, before what it will do: 0.5 below a hand card, so setting one costs that.")]
        public static readonly Tuned<double> SetSpellBase = new(0.8); // user, 2026-11-19: back to 0.8, with LineRules.SetNormalSpellCost 0.5 for setting a normal spell (was 0.3 on 2026-11-18)
        [Tunable("A set quick-play spell's base worth, before what it will do.")]
        public static readonly Tuned<double> SetQuickPlayBase = new(0.9);
        [Tunable("Nominal BoardScore bonus for setting a trap. Its intrinsic hand value is kept in CardAdvantageDelta.")]
        public static readonly Tuned<double> SetTrapBase = new(0.01);
        // ------------------------------------------------------------------ their unknown cards, risk, wins

        [Tunable("Each of their unknown cards (hand, set) counts as this much of a card of theirs.")]
        public static readonly Tuned<double> UnknownCardWorth = new(0.5);
        [Tunable("The most of our board their unknown cards are assumed to answer (a chance, 0-1).")]
        public static readonly Tuned<double> ThreatRiskMax = new(0.9);
        [Tunable("Their unknown cards' threat: the share of our most valuable monster at risk, including face-down monsters.")]
        public static readonly Tuned<double> ThreatFirstShare = new(0.6);
        [Tunable("Their unknown cards' threat: the share of each other monster at risk, including face-down monsters.")]
        public static readonly Tuned<double> ThreatRestShare = new(0.2);
        [Tunable("How much the fear of their cards drops at full pressure (risk tolerance at its top): 0.6 = to 40%.")]
        public static readonly Tuned<double> RiskDiscountMax = new(0.6);

        /// <summary>The chance they bring out their biggest Extra Deck attacker next turn, in the attacker odds behind LethalExposure (user,
        /// 2026-11-25: "yes, add" — Gearframe left in attack position at 900 LP, in reach of XX-Saber Gottoms' 3100).</summary>
        [Tunable("The chance they bring out their biggest Extra Deck attacker next turn, in the attacker odds behind the lethal exposure (user, 2026-11-25).")]
        public static readonly Tuned<double> ExtraDeckAttackerChance = new(0.3);

        public double LethalExposure(SideSummary ours)
        {
            int lp = ours.LifePoints;
            if (lp <= 0 || AttackerOdds == null)
                return 0;
            var attackPosition = ours.Monsters.Where(m => m.FaceUp && m.AttackPosition).Select(m => m.Attack).ToList();
            int needed = ours.Monsters.Count == 0 ? lp
                : attackPosition.Count > 0 ? attackPosition.Min() + lp
                : int.MaxValue; // only defence / face-down monsters: no damage
            int index = (needed + AttackerOddsStep - 1) / AttackerOddsStep;
            return needed == int.MaxValue || index >= AttackerOdds.Length ? 0 : AttackerOdds[index];
        }

        /// <summary>
        /// A won line's score (CSV S15): wins aren't all equal. Lowest risk first (what could still have stopped it), then the fewest cards
        /// spent, then the shortest. Always above any position still alive (1 - AliveFloor at most), so a win beats every non-win.
        /// </summary>
        /// <summary>The least a position still alive at the horizon scores (and 1 minus the most): survival over a loss.</summary>
        [Tunable("The least a position still alive at the horizon scores (and 1 minus the most): survival over a loss.")]
        public static readonly Tuned<double> AliveFloor = new(0.05);

        /// <summary>A search score (0-1, squashed) back in card units against the decision's own position: what the user reads
        /// (user, 2026-11-19: "everywhere it reaches me ... report these scores in raw card units"). Exact for one line; for an
        /// average of lines it's the card units of that average score. A won line reads very high.</summary>
        public static double CardUnits(double score)
        {
            double lo = AliveFloor, hi = 1 - AliveFloor, s = Math.Clamp(score, lo + 1e-6, hi - 1e-6);
            return Scale * Math.Log((s - lo) / (hi - s));
        }

        /// <summary>A score's standard error (or margin) in card units, at that score.</summary>
        public static double CardUnitsErr(double score, double err)
        {
            double lo = AliveFloor, hi = 1 - AliveFloor, s = Math.Clamp(score, lo + 1e-3, hi - 1e-3);
            return err * (hi - lo) * Scale / ((s - lo) * (hi - s));
        }

        public static string Cu(double score) => CardUnits(score).ToString("+0.00;-0.00;0.00");
        public static string CuErr(double score, double err) => CardUnitsErr(score, err).ToString("0.00");
        /// <summary>A score with its uncertainty, in card units ("+1.25 +/-0.30"): an average over lines shows how sure it is.</summary>
        public static string CuPm(double score, double err) => $"{Cu(score)} +/-{CuErr(score, err)}";

        // A monster's worth as a body (current ATK, or DEF in defence), as SideScore counts it.
        private static double Body((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m) =>
            MonsterBase + AtkValue(Power(m));

        /// <summary>
        /// What their unknown set cards would have cost the line as our turn ended, since in the lines they never fire (no
        /// guessing; user, 2026-11-11: "lines that would overplay into those should be penalised somewhat if there's a chance
        /// of them"). Mirror Force: our attack-position monsters, if we attacked. Torrential Tribute: our monsters against
        /// theirs (they'd spring it only when it hurts us more). Any other stopper: our best monster. Each by its chance.
        /// </summary>
        public double Backrow(SideSummary ours, SideSummary theirs, bool attacked)
        {
            // Mirror Force, Torrential, single-card removal and Icarus Attack are all charged along the line now (the exposure).
            return 0;
        }

        // ------------------------------------------------------------------ overexposure (user, 2026-11-15)

        /// <summary>The chance they have Heavy Storm (or another spell/trap wipe) for our set cards on their turn (the brain's read).</summary>
        public double HeavyStormChance { get; init; }

        /// <summary>
        /// "Any of their unknown set cards is X" scaled to the set cards they still have face-down at this point of the line: a set
        /// card of theirs destroyed first (Lyla, MST) lowers it (user, 2026-11-15: Lyla before a push lowers the exposure).
        /// </summary>
        public double ChanceWith(double anyChance, int setsNow)
        {
            if (anyChance <= 0 || setsNow <= 0 || UnknownSets <= 0)
                return 0;
            if (setsNow >= UnknownSets)
                return anyChance;
            double perCard = 1 - Math.Pow(1 - Math.Min(anyChance, 0.999), 1.0 / UnknownSets);
            return 1 - Math.Pow(1 - perCard, setsNow);
        }

        /// <summary>Their face-down spells/traps we know nothing about at this point of a line (the inert stand-ins).</summary>
        public static int UnknownSetsIn(SideSummary theirs) => theirs.SpellCodes.Count(c => !c.FaceUp && c.Code == SearchView.InertCard);

        /// <summary>
        /// What losing this monster to a wipe costs, in card units: its worth on the field minus what it's still worth in the
        /// graveyard (a floater, a graveyard effect, a Treeborn: little; a token: everything, though tokens are usually worth little).
        /// </summary>
        public double MonsterLoss((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m, int side)
        {
            var card = _values.Get(m.Code, side);
            double field = m.FaceUp
                ? MonsterBase + AtkValue(Power(m)) + (m.Negated ? 0 : FieldEffectWeight * Taper(card.Field))
                : card.LikesSet ? SetFlipBase + SetFlipEffectWeight * Taper(card.Held) : WallValue;
            field += Bonus(m.Code, m.FaceUp ? CardPlace.Field : CardPlace.Set, side);
            bool token = (m.Type & 0x4000) != 0;
            // The card itself too (CardAdvantageDelta's non-token monsters), so an exposure is the change the board and card counts would show.
            if (!token)
                field += PlusOneBonus * MonsterCardValue;
            double grave = token ? 0 : GraveValue(m.Code, side);
            return Math.Max(0, field - grave);
        }

        private double GraveValue(int code, int side)
        {
            var card = _values.Get(code, side);
            double value = card.Grave;
            if (value <= 0 && SearchHost.Cards.Get(code) is { } printed && (printed.Type & 0x1) != 0)
                value = GraveMonsterShare * (MonsterBase + AtkValue(printed.Attack) + FieldEffectWeight * Taper(card.Field));
            return value + Bonus(code, CardPlace.Grave, side);
        }

        /// <summary>
        /// A trap exposure's net value (our loss less the trap they spend): never below 0, since we don't play on them wasting a trap (user,
        /// 2026-11-24: "we shouldn't be doing things because we assume the opponent is going to waste their traps") - unless our board
        /// threatens lethal: then they must spend it, and the trap's cost counts ("if the attack threatens lethal then we should be allowed
        /// to count on them wasting a trap on it").
        /// </summary>
        private static double Floor(double net, SideSummary ours, SideSummary theirs) => ThreatensLethal(ours, theirs) ? net : Math.Max(0, net);

        /// <summary>Our board threatens lethal: their monster zone is empty and our face-up attack-position monsters' ATK covers their LP.</summary>
        private static bool ThreatensLethal(SideSummary ours, SideSummary theirs) =>
            theirs.Monsters.Count == 0 && theirs.LifePoints > 0 &&
            ours.Monsters.Where(m => m.FaceUp && m.AttackPosition).Sum(m => m.Attack) >= theirs.LifePoints;

        /// <summary>Torrential Tribute springing now (on our summon): what we'd lose, net of what they'd lose (their monsters and the trap
        /// itself), by its chance now (Floor: never below 0 unless we threaten lethal).</summary>
        public double TorrentialExposure(SideSummary ours, SideSummary theirs) =>
            ChanceWith(TorrentialChance, UnknownSetsIn(theirs)) *
            Floor(ours.Monsters.Sum(m => MonsterLoss(m, 0)) - theirs.Monsters.Sum(m => MonsterLoss(m, 1)) - WipeTrapCost, ours, theirs);

        /// <summary>Mirror Force springing now (on our attack): our attack-position monsters net of the trap they spend, by its chance now.
        /// Floor: never below 0 (a cheap attacker just risks little) unless we threaten lethal.</summary>
        public double MirrorExposure(SideSummary ours, SideSummary theirs) =>
            ChanceWith(MirrorForceChance, UnknownSetsIn(theirs)) *
            Floor(ours.Monsters.Where(m => m.FaceUp && m.AttackPosition).Sum(m => MonsterLoss(m, 0)) - WipeTrapCost, ours, theirs);

        /// <summary>Single-card removal and Icarus Attack count at this share of what Mirror Force / Torrential count (user, 2026-11-15: "~25%").</summary>
        [Tunable("Single-card removal and Icarus Attack count at this share of what Mirror Force / Torrential count (user, 2026-11-15: \"~25%\").")]
        public static readonly Tuned<double> LesserExposureWeight = new(0.25);

        /// <summary>The chance one of their unknown set cards is Icarus Attack (Blackwing lists).</summary>
        public double IcarusChance { get; init; }

        /// <summary>Held monsters count at this share of their field worth: safe from the removal, but they accomplish nothing this turn
        /// (user, 2026-11-15: Fortress and Gearframe both out must beat Fortress alone with Gearframe held).</summary>
        [Tunable("Held monsters count at this share of their field worth: safe from the removal, but they accomplish nothing this turn (user, 2026-11-15: Fortress and Gearframe both out must beat Fortress alone with Gearframe held).")]
        public static readonly Tuned<double> ReserveWeight = new(0.5);

        /// <summary>The monsters in our hand, by what they'd be worth on the field: what we still have if their removal hits the field.</summary>
        private double ReserveWorth(SideSummary ours) => ReserveWeight *
            ours.HandCodes.Where(c => c != SearchView.InertCard).Sum(code =>
                SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0 && !r.IsExtraDeckMonster
                    ? MonsterBase + AtkValue(r.Attack) + FieldEffectWeight * Taper(_values.Get(code, 0).Field) : 0);

        private double SetCardWorth(int code) => IsKnownTrap(code)
            ? SetTrapBase + PlusOneBonus * HandCardValue(code, 0)
            : SetBase(code) + HeldEffectWeight * Taper(_values.Get(code, 0).Held);

        // What one of our spells/traps is worth where it stands (lost to a wipe or Icarus).
        private double SpellLoss((int Code, bool FaceUp) s) =>
            s.FaceUp ? FaceUpSpellBase + FieldEffectWeight * Taper(_values.Get(s.Code, 0).Field) + Bonus(s.Code, CardPlace.Field, 0)
                     : SetCardWorth(s.Code) + (IsKnownTrap(s.Code) ? 0 : Bonus(s.Code, CardPlace.Set, 0));

        /// <summary>
        /// Single-card removal now (D-Prison, Bottomless, Raigeki Break, Compulsory...: OtherStopChance): our best monster, at a quarter
        /// of a mass wipe's weight. Still worth tracking when Mirror Force and Torrential are gone, so lines don't tie (user, 2026-11-15).
        /// </summary>
        public double SpotExposure(SideSummary ours, SideSummary theirs)
        {
            // Not "how much can their removal take" but "how much do we still accomplish if they have it" (user, 2026-11-15): what our
            // best monster is worth, times its share of our whole board's worth. One monster: all of it; two equally good: half (the
            // other one still does its job), so more good cards make this better, not worse.
            if (OtherStopChance <= 0 || ours.Monsters.Count == 0)
                return 0;
            // The monsters we hold back in hand count too: their removal can't touch them, so baiting it with the worse card (Gearframe
            // attacking, Fortress kept) beats committing both (user, 2026-11-15).
            var worth = ours.Monsters.Select(m => MonsterLoss(m, 0)).ToList();
            double total = worth.Sum() + ReserveWorth(ours), best = worth.Max();
            // Net of the trap they spend (user, 2026-11-24).
            return total <= 0 ? 0 : LesserExposureWeight * ChanceWith(Math.Min(1, OtherStopChance), UnknownSetsIn(theirs)) * Floor(best * (best / total) - SingleTrapCost, ours, theirs);
        }

        /// <summary>
        /// Icarus Attack now (against Blackwings): it destroys two cards, so with one card of ours on the field it costs them more than
        /// it takes (0); from a second card on, our two most valuable cards. Only while they have a Winged Beast (a face-down monster
        /// may be one) to tribute and a set card it could be (user, 2026-11-15). At a quarter of a mass wipe's weight.
        /// </summary>
        public double IcarusExposure(SideSummary ours, SideSummary theirs)
        {
            if (IcarusChance <= 0 || UnknownSetsIn(theirs) == 0)
                return 0;
            bool tribute = theirs.Monsters.Any(m => !m.FaceUp || SearchHost.Cards.Get(m.Code) is { } r && ((long)r.Race & 0x200) != 0);
            if (!tribute)
                return 0;
            var losses = ours.Monsters.Select(m => MonsterLoss(m, 0))
                .Concat(ours.SpellCodes.Where(s => s.Code != SearchView.InertCard).Select(SpellLoss))
                .OrderByDescending(v => v).ToList();
            if (losses.Count < 2)
                return 0;
            // As for single removal: what we still accomplish if they have it (user, 2026-11-15) - our two best cards' worth times their
            // share of our whole board, so more good cards beyond those two soften it.
            double two = losses[0] + losses[1], total = losses.Sum() + ReserveWorth(ours);
            // Net of the trap they spend (user, 2026-11-24).
            return total <= 0 ? 0 : LesserExposureWeight * ChanceWith(IcarusChance, UnknownSetsIn(theirs)) * Floor(two * (two / total) - SingleTrapCost, ours, theirs);
        }

        /// <summary>Heavy Storm on their turn: our set spells/traps (each by what it would do), by its chance.</summary>
        public double HeavyExposure(SideSummary ours, SideSummary theirs)
        {
            if (HeavyStormChance <= 0)
                return 0;
            // Bait (a set card worth chaining in response: Jar of Greed, Dust Tornado, MST...) isn't lost to it (CSV S2).
            var set = ours.SpellCodes.Where(c => !c.FaceUp && c.Code != SearchView.InertCard && !BaitCodes.Contains(c.Code))
                .Select(SpellLoss).ToList();
            // Each extra set card makes the wipe worse than one more card's worth (CSV S2: "gradually stops setting them").
            return HeavyStormChance * set.Sum() * Math.Max(1, set.Count * OversetCurve) + OverHalfTrapsExposure(ours, theirs);
        }

        /// <summary>
        /// Setting more than half our trap power (set + in hand) with no good reason (user, 2026-11-24): exposure isn't a fixed number of
        /// sets - losing two set traps hurts more when they're our only two than when five more wait in hand. By trap power, not count
        /// (user, 2026-11-24: "setting a d-prison first turn when you're unthreatened is preferable over setting mirror force"): a trap's
        /// power is its card value (HandCardValue). Our known traps (not bait, not storm-proof) count; set power beyond half the total
        /// costs OverHalfTrapWeight x the excess. Ignored with only one trap (user, 2026-11-24). Good reasons: their big face-up monsters
        /// at least matching our set traps, the risk tolerance past OversetPressFrom (one more trap: our strongest set one's power
        /// allowed on top), their Heavy Storm gone, or a hand of 7+ (it'd be discarded anyway).
        /// </summary>
        private double OverHalfTrapsExposure(SideSummary ours, SideSummary theirs)
        {
            if (HeavyStormChance < StormGoneChance || ours.HandCodes.Count > 6)
                return 0;
            bool Counted(int code) => IsKnownTrap(code) && !BaitCodes.Contains(code) && !StormProofCodes.Contains(code);
            double Power(int code) => Math.Max(0, HandCardValue(code, 0));
            var setPowers = ours.SpellCodes.Where(c => !c.FaceUp && Counted(c.Code)).Select(c => Power(c.Code)).ToList();
            var handPowers = ours.HandCodes.Where(Counted).Select(Power).ToList();
            if (setPowers.Count == 0 || setPowers.Count + handPowers.Count <= 1)
                return 0;
            int bigThreats = theirs.Monsters.Count(m => m.FaceUp && m.Attack >= StrongMonsterAttack);
            if (bigThreats >= setPowers.Count)
                return 0;
            double setPower = setPowers.Sum();
            double allowed = (setPower + handPowers.Sum()) / 2 + (RiskPress >= OversetPressFrom ? setPowers.Max() : 0);
            return OverHalfTrapWeight * Math.Max(0, setPower - allowed);
        }

        /// <summary>Set trap power beyond half our trap power costs this times the excess (OverHalfTrapsExposure). User, 2026-11-24.</summary>
        [Tunable("Set trap power (card value) beyond half our trap power (set + in hand) costs this times the excess, unless there's a good reason (their big threats, high risk tolerance, their Heavy Storm gone, a 7+ hand); ignored with only one trap. User, 2026-11-24.")]
        public static readonly Tuned<double> OverHalfTrapWeight = new(0.5);

        /// <summary>Storm-proof set cards (Jar of Greed, Legacy of Yata-Garasu, Trap Dustshoot...: DeckBotBrain.StormProofSets): not counted as traps to keep back.</summary>
        public IReadOnlyCollection<int> StormProofCodes { get; init; } = Array.Empty<int>();
        /// <summary>Below this Heavy Storm chance their Storm counts as gone (DeckBotBrain.StormGoneChance).</summary>
        public double StormGoneChance { get; init; }
        /// <summary>Risk tolerance from which one more set trap is fine (DeckBotBrain.OversetPressFrom).</summary>
        public double OversetPressFrom { get; init; } = 1;
        /// <summary>A face-up monster of theirs at least this big is a threat worth a trap (DeckBotBrain.StrongMonsterAttack).</summary>
        public int StrongMonsterAttack { get; init; } = 2000;

        /// <summary>The Heavy Storm charge grows with the number of our set cards: x max(1, n x this) (CSV S2).</summary>
        [Tunable("The Heavy Storm charge grows with the number of our set cards: x max(1, n x this) (CSV S2).")]
        public static readonly Tuned<double> OversetCurve = new(0.5);

        /// <summary>Our set cards that are bait (worth chaining when they're removed): not lost to a wipe (DeckBotBrain.BaitCardIds).</summary>
        public IReadOnlyCollection<int> BaitCodes { get; init; } = Array.Empty<int>();

        /// <summary>
        /// What a monster of ours needs protecting, in card units (user, 2026-11-16: "scaled by how good the monster you have to protect
        /// is: a monster with low survivability but a powerful effect gives traps a high protection score, lower for monsters with
        /// higher survivability or less powerful effects"): for each face-up monster, how good it is (its effects in full, its body at
        /// ProtectBodyShare) times how likely it is to be lost (a bigger attacker of theirs on the field: certain; in their unknown
        /// cards: AttackerOdds); the most of these. And how low our life points are, 0-1 (LowLpFullAt, LowLpScale).
        /// </summary>
        private (double Monster, double Lp) ProtectionNeed(SideSummary ours, SideSummary theirs)
        {
            int theirBest = theirs.Monsters.Where(m => m.FaceUp && m.AttackPosition).Select(m => m.Attack).DefaultIfEmpty(0).Max();
            double monster = 0;
            foreach (var m in ours.Monsters.Where(m => m.FaceUp))
            {
                var card = _values.Get(m.Code, 0);
                double effect = m.Negated ? 0 : FieldEffectWeight * Taper(card.Field) + Bonus(m.Code, CardPlace.Field, 0);
                double good = effect + ProtectBodyShare * (MonsterBase + AtkValue(Power(m)));
                int power = m.AttackPosition ? m.Attack : m.Defense;
                double lost = theirBest > power ? 1
                    : AttackerOdds is { } odds && (power + AttackerOddsStep) / AttackerOddsStep is var i && i < odds.Length ? odds[i] : 0;
                monster = Math.Max(monster, good * lost);
            }
            // Never quite 0, rising faster the lower our LP get (user, 2026-11-17: "taper exponentially - from near-0 around 8k to 4k and
            // then going up to the full value of 1 when life points are at 100 (or lower)").
            double lp = ours.LifePoints <= LowLpFullAt ? 1 : Math.Exp(-(ours.LifePoints - LowLpFullAt) / Math.Max(1, LowLpScale));
            return (monster, lp);
        }

        /// <summary>A set card's situational worth beyond its own effect value (card units): see SideScore.</summary>
        private double SetCardSituational(int code, CardValue card, double protectNeed, double lpNeed, List<int> ourSets, SideSummary ours, SideSummary? other, Action<string, double>? into)
        {
            double extra = 0;
            void Add(string rule, double value)
            {
                extra += value;
                if (value != 0)
                    into?.Invoke(rule, value);
            }
            // Only a card that can act on their turn earns these: a trap or a quick-play spell. A set normal spell (Brain Control) can't
            // protect or remove anything until our next turn, so it only pays for being set (SetSpellBase is 0.5 below a hand card) (user, 2026-11-17: "there
            // should've been a penalty for setting a normal spell"; it was earning removal credit instead).
            if (SearchHost.Cards.Get(code) is not { } setCard || !((setCard.Type & 0x4) != 0 || (setCard.Type & 0x2) != 0 && (setCard.Type & 0x10000) != 0))
                return 0;
            // Protects a monster worth protecting; with our LP low, any card that stops or removes an attacker.
            Add("Set cards protecting monsters", ProtectionWeight * card.Protects * Math.Min(protectNeed, ProtectNeedCap));
            Add("Set cards protecting low LP", LowLpRemovalWeight * lpNeed * Math.Max(card.Protects, (card.Removes & Removes.Monster) != 0 ? 1 : 0));
            // A trap that answers a monster (on its summon or attack, or at any time) waits for their next one. Their unknown hand is inert
            // in the lines, so the lines alone rate such a trap a dead card (seen 2026-11-24: Bottomless not set, only Sangan's searches,
            // all 1500 or less, were ever summoned; then X-Saber Airbellum hit us). User, 2026-11-24: for all traps, threats only, as the
            // brain counts them (DeckBotBrain.IsBigThreatAsync): the chance their unknown cards (hand + draw) hold one it can hit.
            if ((setCard.Type & 0x4) != 0 && (card.Protects > 0 || (card.Removes & Removes.Monster) != 0) && ThreatOdds is { } odds)
            {
                int minAttack = TrapMinAttack.GetOrAdd(code, _ =>
                    System.Text.RegularExpressions.Regex.Match(setCard.Text ?? "", @"(\d+) or more ATK", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0);
                // 1. ATK over the bar: our biggest Normal Summon in our deck, or our biggest face-up monster as the line ends if higher.
                int bar = OwnLargestNormalAttack ?? StrongMonsterAttack - 1;
                foreach (var mon in ours.Monsters.Where(mon => mon.FaceUp))
                    bar = Math.Max(bar, mon.Attack);
                int threat = bar + 1;
                // 2. Or it beats a monster of ours that isn't expendable (here: not a token; the brain's full expendability isn't in the lines).
                foreach (var mon in ours.Monsters.Where(mon => mon.FaceUp && (mon.Type & 0x4000) == 0))
                    threat = Math.Min(threat, mon.AttackPosition ? mon.Attack : mon.Defense);
                // (3. Bouncers and strong effects count at any ATK: in ThreatOdds.) The trap's own floor ("1500 or more ATK") still applies.
                threat = Math.Max(threat, minAttack);
                int index = Math.Min(odds.Length - 1, (threat + AttackerOddsStep - 1) / AttackerOddsStep);
                // 4. Or an Extra Deck monster: by how explosive their deck is.
                double extraThreat = Math.Clamp(ExtraThreatPerExplosiveness * _explosiveness, 0, 1);
                double chance = 1 - (1 - odds[index]) * (1 - extraThreat);
                Add("Set traps: their next threat to answer", TrapCatchWeight * chance);
            }
            // Removes a card of theirs already on the field: by the best one it can hit.
            if (card.Removes != Removes.None && other is { } theirs)
            {
                double best = 0;
                if ((card.Removes & Removes.Monster) != 0)
                    foreach (var m in theirs.Monsters)
                        best = Math.Max(best, MonsterLoss(m, 1));
                if ((card.Removes & Removes.SpellTrap) != 0)
                    foreach (var (c, up) in theirs.SpellCodes)
                        best = Math.Max(best, c == SearchView.InertCard || !up ? UnknownSetWorth : FaceUpSpellBase + FieldEffectWeight * Taper(_values.Get(c, 1).Field));
                Add("Set cards with a removal target", RemoveThreatWeight * best);
            }
            // Bait (Jar of Greed, Dust Tornado...): their MST, Lyla or Ryko picking among our sets may take it instead of a better one.
            if ((setCard.Type & 0x4) == 0 && BaitCodes.Contains(code) && ourSets.Count > 1)
            {
                double others = ourSets.Where(c => !BaitCodes.Contains(c)).Sum(SetCardWorth);
                Add("Set cards baiting backrow removal", BaitWeight * others / ourSets.Count);
            }
            return extra;
        }

        /// <summary>A set card that protects (CardValue.Protects) gains this times what our most-at-risk monster needs (ProtectionNeed).
        /// Card units; adjust slowly.</summary>
        [Tunable("A set card that protects (CardValue.Protects) gains this times what our most-at-risk monster needs (ProtectionNeed). Card units; adjust slowly.")]
        public static readonly Tuned<double> ProtectionWeight = new(0.5);
        /// <summary>The most a monster's protection need counts for (card units).</summary>
        [Tunable("The most a monster's protection need counts for (card units).")]
        public static readonly Tuned<double> ProtectNeedCap = new(2);
        /// <summary>How much of a monster's body counts toward "how good it is" for protection (its effects count in full).</summary>
        [Tunable("How much of a monster's body counts toward \"how good it is\" for protection (its effects count in full).")]
        public static readonly Tuned<double> ProtectBodyShare = new(0.5);
        /// <summary>With our LP at LowLpFullAt or below, a set card that stops or removes an attacker gains this much (card units).</summary>
        [Tunable("With our LP at LowLpFullAt or below, a set card that stops or removes an attacker gains this much (card units); less as LP rise (LowLpScale).")]
        // User, 2026-11-24: set traps protect our life points too, more the lower they are; raised from 0.6 / 1500 (it was ~0.05 a card at
        // 4000 LP), the low end higher still: per card about 0.11 at 8000, 0.41 at 4000, 0.80 at 2000, 1.11 at 1000.
        public static readonly Tuned<double> LowLpRemovalWeight = new(1.5);
        [Tunable("Our LP at or below which a stopping/removing set card gets the full LowLpRemovalWeight.")]
        public static readonly Tuned<int> LowLpFullAt = new(100);
        [Tunable("How fast that value falls as our LP rise above LowLpFullAt: exp(-(LP - LowLpFullAt) / this). 3000: about 74% at 1000 LP, 53% at 2000, 27% at 4000, 7% at 8000.")]
        public static readonly Tuned<double> LowLpScale = new(3000);
        /// <summary>Our set trap that answers a monster gains this times the chance their unknown cards (hand + draw) hold one it can hit
        /// (threats as the brain counts them: ThreatOdds, ExtraThreatPerExplosiveness). Card units.</summary>
        [Tunable("Our set trap that answers a monster (on its summon / attack, or any time) gains this times the chance their unknown cards (hand + draw) hold a threat it can hit, as the brain counts threats (over our biggest Normal Summon / face-up monster, beats our non-token monster, bouncer / strong effect, Extra Deck), the trap's \"N or more ATK\" still applying; from every list they're likely on. Card units. User, 2026-11-24: for all traps.")]
        public static readonly Tuned<double> TrapCatchWeight = new(0.15); // user, 2026-11-24: a small change (was 0.5)
        [Tunable("The chance their next turn brings an Extra Deck monster (a threat) is this times their deck's explosiveness (the deck read's, 0-1). For set traps' TrapCatchWeight. User, 2026-11-24.")]
        public static readonly Tuned<double> ExtraThreatPerExplosiveness = new(0.5);

        /// <summary>The chance their unknown cards (hand + draw) hold a threat, by ATK threshold as AttackerOdds; bouncers and strong
        /// effects count at every threshold (OpponentDeckPredictor.ThreatChances; without a deck read, AttackerOdds). Null: no trap term.</summary>
        public double[]? ThreatOdds { get; init; }
        /// <summary>Our biggest Normal Summon in our deck list (DeckBotBrain.OwnLargestNormalAttack): the threat bar. Null: StrongMonsterAttack.</summary>
        public int? OwnLargestNormalAttack { get; init; }

        // A trap's ATK floor from its text ("1500 or more ATK": Bottomless), 0 for any monster; per card code.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> TrapMinAttack = new();
        /// <summary>A set card that can remove a card of theirs already on the field gains this times that card's worth (card units).</summary>
        [Tunable("A set card that can remove a card of theirs already on the field gains this times that card's worth (card units).")]
        public static readonly Tuned<double> RemoveThreatWeight = new(0.3);
        /// <summary>The worth of an unknown set card of theirs, as a removal target.</summary>
        [Tunable("The worth of an unknown set card of theirs, as a removal target.")]
        public static readonly Tuned<double> UnknownSetWorth = new(0.5);
        /// <summary>A set bait card gains this times our other sets' worth over the number of our sets (the share of a random pick).</summary>
        [Tunable("A set bait card gains this times our other sets' worth over the number of our sets (the share of a random pick).")]
        public static readonly Tuned<double> BaitWeight = new(0.5);

        /// <summary>A set card's base worth by its kind: trap, quick-play spell, other spell (and an unknown card).</summary>
        internal static double SetBase(int code) =>
            // (An unknown set card, the stand-in: UnknownSetWorth, as before the split; not the set-spell base.)
            code == SearchView.InertCard || SearchHost.Cards.Get(code) is not { } r ? UnknownSetWorth : (r.Type & 0x4) != 0 ? SetTrapBase : (r.Type & 0x2) != 0 && (r.Type & 0x10000) != 0 ? SetQuickPlayBase : SetSpellBase;


        /// <summary>
        /// What their unknown cards might do, since the lines don't guess at them (user, 2026-11-11: "a simple score for the
        /// threat level of unknown cards based on the number of them and the explosivity of the deck"): each is a card of
        /// theirs (0.5), and together they put our board at risk. The more unknown cards and the more explosive their deck, the
        /// likelier they answer our best monster (and some of the rest), so a line that commits everything to the field pays more.
        /// </summary>
        public double Threat(SideSummary ours)
        {
            if (_unknownCards <= 0 && UnknownSets <= 0)
                return 0;
            double risk = Math.Min(ThreatRiskMax, UnknownCardRisk * _unknownCards * (ExplosivenessBase + _explosiveness));
            var bodies = ours.Monsters
                .Select(Body)
                .OrderByDescending(v => v).ToList();
            double exposed = bodies.Count == 0 ? 0 : ThreatFirstShare * bodies[0] + ThreatRestShare * bodies.Skip(1).Sum();
            return UnknownCardWorth * (_unknownCards + UnknownSets) + risk * exposed * RiskDiscount;
        }

        private double Bonus(int code, CardPlace place, int side)
        {
            if (side != 0 || _cardBonus == null)
                return 0;
            try { return _cardBonus(code, place); }
            catch { return 0; }
        }



        /// <summary>One player?s field value in card units, optionally broken down by rule.</summary>
        public double BoardScore(SideSummary s, int side, SideSummary? other = null, Action<string, double>? into = null)
        {
            double Effect(double points, double weight) => weight * Taper(points);
            double value = 0;
            void Add(string category, double v)
            {
                value += v;
                if (into != null && v != 0)
                    into(category, v);
            }
            void AddPair(string firstRule, double first, string secondRule, double second)
            {
                value += first + second;
                if (first != 0)
                    into?.Invoke(firstRule, first);
                if (second != 0)
                    into?.Invoke(secondRule, second);
            }

            // Monsters: body (current ATK, or DEF in defence) plus what their effects can still do on the field (nothing when
            // negated: a Synchro summoned by Vayu is only its body).
            foreach (var m in s.Monsters)
            {
                var card = _values.Get(m.Code, side);
                // Lyla's effect destroys a spell/trap: with none on the other side it's worth nothing (user, 2026-11-18).
                bool idle = SearchHost.Is(m.Code, LylaLightswornSorceress) && other != null && other.SpellCodes.Count == 0;
                if (m.FaceUp)
                {
                    AddPair("Face-up monsters: base value", MonsterBase, "Face-up monsters: ATK/DEF value", AtkValue(Power(m)));
                    Add(SideMonsterEffects, m.Negated || idle ? 0 : Effect(card.Field, FieldEffectWeight));
                }
                else if (card.LikesSet)
                    AddPair("Set monsters with effects: base value", SetFlipBase,
                        "Set monsters: unused effects", Effect(card.Held, SetFlipEffectWeight));
                else
                    Add("Set monsters: walls", WallValue);
                Add(SideOverrides, Bonus(m.Code, m.FaceUp ? CardPlace.Field : CardPlace.Set, side));
            }
            // Set traps retain their hand value in CardAdvantageDelta. BoardScore measures the benefit of deploying them.
            // Face-up spells/traps are valued for their lasting effects.
            var (protectNeed, lpNeed) = side == 0 && other != null ? ProtectionNeed(s, other) : (0, 0);
            var ourSets = side == 0 ? s.SpellCodes.Where(c => !c.FaceUp && c.Code != SearchView.InertCard).Select(c => c.Code).ToList() : new List<int>();
            foreach (var (code, faceUp) in s.SpellCodes.Where(c => c.Code != SearchView.InertCard)) // stand-ins count in Threat
            {
                var card = _values.Get(code, side);
                bool setTrap = !faceUp && IsKnownTrap(code);
                if (faceUp)
                    AddPair("Face-up spells/traps: base value", FaceUpSpellBase, "Face-up spells/traps: effects", Effect(card.Field, FieldEffectWeight));
                else if (setTrap)
                    Add("Set traps: nominal bonus", SetTrapBase);
                else
                    AddPair("Set spells: base value", SetBase(code), "Set spells: effects", Effect(card.Held, HeldEffectWeight));
                if (!faceUp && side == 0)
                    value += SetCardSituational(code, card, protectNeed, lpNeed, ourSets, s, other, into);
                if (!setTrap)
                    Add(SideOverrides, Bonus(code, faceUp ? CardPlace.Field : CardPlace.Set, side));
            }
            return value;
        }

        internal const string SideMonsterEffects = "Face-up monsters: effects";
        internal const string SideOverrides = "Card value overrides";
        /// <summary>Separates a score name from its rule label in the breakdown.</summary>
        internal const string SubPart = " :: ";
    }
}
