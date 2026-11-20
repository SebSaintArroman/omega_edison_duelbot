using BotTest.Game.AI.Brains;
using DuelSim.Engine;

namespace BotTest.Game.AI.Search
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
    internal sealed class MctsEvaluator
    {
        /// <summary>Weight of a card's effect points (EffectEvaluator: a searcher or negate ~4) next to its body.</summary>
        [Tunable("Weight of a card's effect points (EffectEvaluator: a searcher or negate ~4) next to its body.")]
        private static double FieldEffectWeight = 0.25, HeldEffectWeight = 0.1;

        /// <summary>A monster in the graveyard without a graveyard effect: this share of its field worth (user, 2026-11-18: 5%).</summary>
        [Tunable("A monster in the graveyard without a graveyard effect (or an override): this share of its field worth (face-up, printed ATK, field effects). Spells/traps without one: 0. User, 2026-11-18.")]
        public static double GraveMonsterShare = 0.025; // user, 2026-11-18: 2.5% (5% "feels a little high")

        /// <summary>Effect points are linear up to this, then taper (see Taper).</summary>
        [Tunable("Effect points are linear up to this, then taper (see Taper).")]
        public static double TaperStart = 2;
        /// <summary>How slowly the taper bends: 1 puts Absolute Zero (5.25 points with the "all" factor) at 3.45, Stardust (9) at 4.1, Shura (10) at 4.2.</summary>
        [Tunable("How slowly the taper bends: 1 puts Absolute Zero (5.25 points with the \"all\" factor) at 3.45, Stardust (9) at 4.1, Shura (10) at 4.2.")]
        public static double TaperScale = 1;

        /// <summary>
        /// Effect points after tapering (user, 2026-11-13: instead of a raw cap, "a log style tapering ... mostly linear up until
        /// 2, then starts tapering down"): x up to TaperStart, then TaperStart + TaperScale * ln(1 + (x - TaperStart) / TaperScale).
        /// </summary>
        public static double Taper(double points) =>
            points <= TaperStart ? Math.Max(0, points) : TaperStart + TaperScale * Math.Log(1 + (points - TaperStart) / TaperScale);

        /// <summary>How many card units of difference move the score from 0.5 to about 0.73 (the logistic's scale).</summary>
        [Tunable("How many card units of difference move the score from 0.5 to about 0.73 (the logistic's scale).")]
        public static double Scale = 3;

        private readonly SearchCardValues _values;
        private readonly Func<SideSummary, double>? _setup;
        private readonly Func<int, CardPlace, double>? _cardBonus;
        private readonly int _unknownCards;
        private readonly double _explosiveness;

        /// <param name="setup">The deck brain's setups on our side (DeckBotBrain.SearchSetupValue), in card units.</param>
        /// <param name="cardBonus">The deck brain's own value of one of its cards where it stands (DeckBotBrain.SearchCardBonus).</param>
        /// <param name="unknownCards">Their hand cards we know nothing about: not played in
        /// the lines (no guessing), counted by the threat term instead.</param>
        /// <param name="explosiveness">How likely their deck is to have a big turn (0-1, the deck read; 0.5 when unknown).</param>
        public MctsEvaluator(SearchCardValues values, Func<SideSummary, double>? setup, Func<int, CardPlace, double>? cardBonus,
            int unknownCards = 0, double explosiveness = 0.5)
        {
            _values = values;
            _setup = setup;
            _cardBonus = cardBonus;
            _unknownCards = unknownCards;
            _explosiveness = explosiveness;
        }

        /// <summary>Each unknown card's share of the risk to our board (the explosiveness scales it). Tune from logs.</summary>
        [Tunable("Each unknown card's share of the risk to our board (the explosiveness scales it). Tune from logs.")]
        public static double UnknownCardRisk = 0.08;

        [Tunable("Their unknown cards' risk scales with this plus their deck's explosiveness (0.5 + explosiveness).")]
        public static double ExplosivenessBase = 0.5;

        /// <summary>How much we have to press: the risk tolerance above its base, 0-1 (DeckBotBrain.RiskPressNow, CSV S13). Fear of their
        /// cards drops by up to 60% (RiskDiscount) and damage to them counts up to double.</summary>
        public double RiskPress { get; init; }

        /// <summary>Their life points as the search began: gains above this count for nothing (CSV S6). 0: not set.</summary>
        public int TheirStartLp { get; init; }
        /// <summary>The deck brain's next-turn value of our side at the end of a line, given the cards we activated in our turn
        /// (DeckBotBrain.SearchNextTurnValue).</summary>
        public Func<SideSummary, IReadOnlyCollection<int>, double>? NextTurnValue { get; init; }
        /// <summary>Our life points at the decision: the life points we lose by the end of a line count against it (OwnDamageShare).</summary>
        public int OurStartLp { get; init; }

        [Tunable("Our life points lost by the end of a line count against it at this share of DamageBonusPer1000 (continuous: 0.5 x 0.1 per 1000 = 0.05 per 1000 lost). User, 2026-11-18: \"make it count 50% of the opponent's one\".")]
        public static double OwnDamageShare = 0.8333; // user, 2026-11-19: taking damage doubled to 0.10 per 1000 (0.8333 x 0.12); was 0.5 x 0.1

        /// <summary>Per card we draw in our turn, on top of the card itself: a drawn card counts as 1.01 cards (CSV S6, so free draws aren't
        /// skipped for a tie).</summary>
        [Tunable("Per card we draw in our turn, on top of the card itself: a drawn card counts as 1.01 cards (CSV S6, so free draws aren't skipped for a tie).")]
        public static double DrawBonus = 0.01;

        /// <summary>
        /// Per card of advantage won in our turn (card units, on top of the cards' own worth): a +1 (a card gained without losing one:
        /// Black Whirlwind's search, Pot of Greed) or a card of theirs taken for nothing (DAD's effect). A 1-for-1 trade is 0; a card
        /// spent for nothing (Kalut into a battle that changes nothing) is -1. User, 2026-11-16: "a very large bonus (per card)".
        /// </summary>
        [Tunable("Per card of advantage won in our turn (card units, on top of the cards' own worth): a +1 (a card gained without losing one: Black Whirlwind's search, Pot of Greed) or a card of theirs taken for nothing (DAD's effect). A 1-for-1 trade is 0; a card spent for nothing (Kalut into a battle that changes nothing) is -1. User, 2026-11-16: \"a very large bonus (per card)\".")]
        public static double PlusOneBonus = 1.0;

        /// <summary>Our cards minus theirs: hand and field (monsters and spells/traps), tokens aside.</summary>
        public static int CardAdvantage(SideSummary ours, SideSummary theirs)
        {
            static int Cards(SideSummary s) => s.Hand + s.Monsters.Count(m => (m.Type & 0x4000) == 0) + s.SpellCodes.Count;
            return Cards(ours) - Cards(theirs);
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
        public static double DamageBonusPer1000 = 0.12; // user, 2026-11-19: +20% (was 0.1)

        [Tunable("A side's clock on the other, as part of the board (card units), by its length: entry 1 for a 1-turn clock, 2 for 2 turns, and so on; the last entry for that many turns or more (no clock = 0). The turns are the brain's own clock (BoardAssessment.TurnsToWin). User, 2026-11-18: 1.5 / 1 / 0.5 / 0.25 (4-5) / 0.1 (6-7) / 0.1 (8+, its own bracket).")]
        public static double[] ClockByTurns = { 1.5, 1, 0.5, 0.25, 0.25, 0.1, 0.1, 0.1 };
        [Tunable("Their clock on us counts this share of ClockByTurns against a line (ours on them counts in full). User, 2026-11-18: \"tone down the penalty for being on a clock ... leave the bonus for the bot putting me on a clock as is\".")]
        public static double TheirClockShare = 0.5;

        /// <summary>
        /// A player's clock as part of the board (user, 2026-11-17: "make a player's clock a part of the board state evaluation, following the
        /// existing clock rules that the brain uses ... give it a bonus the lower the clock is at the end"): the brain's clock
        /// (BoardAssessment.TurnsToWin, the same function its push / hold / spend rules use) for this side's face-up attack-position monsters
        /// against the other side's monsters and life points, as ClockByTurns by its length. Attacking lowers their life points, more monsters and
        /// monsters kept alive add ATK: all of them shorten it.
        /// </summary>
        internal static double ClockValue(SideSummary attacker, SideSummary defender, int extraAttack = 0)
        {
            int turns = ClockTurns(attacker, defender, extraAttack);
            if (turns == BoardAssessment.NoClock || ClockByTurns.Length == 0)
                return 0;
            return ClockByTurns[Math.Clamp(turns, 1, ClockByTurns.Length) - 1];
        }

        /// <summary>
        /// The brain's clock (turns to win) of this side on the other, from a search position: every face-up monster of the attacker (one in
        /// defence can switch to attack next turn; user, 2026-11-18), plus <paramref name="extraAttack"/> (their next summon, TheirNextAttack).
        /// </summary>
        internal static int ClockTurns(SideSummary attacker, SideSummary defender, int extraAttack = 0) =>
            BoardAssessment.TurnsToWin(
                attacker.Monsters.Where(m => m.FaceUp).Select(m => m.Attack).Concat(extraAttack > 0 ? new[] { extraAttack } : Array.Empty<int>()),
                defender.Monsters.Select(m => new BoardAssessment.Defender(
                    m.FaceUp ? (m.AttackPosition ? m.Attack : m.Defense) : m.Defense, m.FaceUp && m.AttackPosition, !m.FaceUp, m.Code)),
                defender.LifePoints);

        [Tunable("Gorz in our hand and our field empty at the end of a line: this small bonus (card units): Gorz can come out on their direct hit.")]
        public static double GorzReadyBonus = 0.3;
        private const int GorzCode = 44330098;
        private const int LylaCode = 22624373;

        [Tunable("A line that wins: this many card units on top of its position, times the chance the win really works (WinChance). Large.")]
        public static double WinBonus = 10;
        [Tunable("A won line: each card of ours it used up costs this much (card units) in the win's part of its score; the board it leaves doesn't count there (user, 2026-11-18).")]
        public static double WinCardCost = 1.0;
        [Tunable("A won line's bonus when the win carries any risk (WinBonus is only for a sure win). User, 2026-11-18: risk is risky, an overspent win that fails loses.")]
        public static double RiskyWinBonus = 5;
        [Tunable("A line that activates our Trap Dustshoot gains this (card units): it removes a card from their hand almost always, but the lines' unseen hand cards are blanks (user, 2026-11-18: \"1 point if it's activated\").")]
        public static double DustshootBonus = 1.0;

        /// <summary>
        /// The chance a win found in a line really happens: none of their unknown set cards is a stopper that would have fired (the lines never
        /// fire them): Mirror Force (if we attacked), Torrential Tribute, and any other stopper (Bottomless, D-Prison...), each at the deck
        /// read's chance. Their hand traps (Honest, Gorz) aren't counted here yet.
        /// </summary>
        public double WinChance(bool attacked, SideSummary? theirsAtEnd = null)
        {
            static double Not(double p) => 1 - Math.Clamp(p, 0, 1);
            double chance = (attacked ? Not(MirrorForceChance) : 1) * Not(TorrentialChance) * Not(OtherStopChance);
            // Monsters that work from their hand (user, 2026-11-17): Honest and Kalut only with an attack-position LIGHT / Blackwing monster
            // of theirs out (set in HonestChance / KalutChance), Battle Fader against any attack, Gorz when the win's direct hit came into a
            // field with no cards of theirs.
            if (attacked)
            {
                chance *= Not(HonestChance) * Not(KalutChance) * Not(BattleFaderChance);
                if (theirsAtEnd is { } t && t.Monsters.Count == 0 && t.SpellCodes.Count == 0)
                    chance *= Not(GorzChance);
            }
            return chance;
        }

        /// <summary>Their hand monsters that stop a lethal battle, each the deck read's chance it's in their hand (0 when it can't act here:
        /// Honest needs an attack-position LIGHT monster of theirs, Kalut a Blackwing).</summary>
        public double HonestChance { get; init; }
        public double KalutChance { get; init; }
        public double GorzChance { get; init; }
        public double BattleFaderChance { get; init; }

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
            var ours = host.Summary(0);
            var theirs = host.Summary(1);
            // With a breakdown: each side's categories as sub-rows of its part (their side negated, as it counts against us).
            double ourSide = parts == null ? SideScore(ours, 0, theirs) : SideScore(ours, 0, theirs, (c, v) => Part(OurCardsPart + SubPart + c, v));
            double theirSide = parts == null ? SideScore(theirs, 1, ours) : SideScore(theirs, 1, ours, (c, v) => Part(TheirCardsPart + SubPart + c, -v));
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
                Part(OurCardsPart, ourSide - Root(OurCardsPart));
                Part(TheirCardsPart, -theirSide - Root(TheirCardsPart));
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
                // It replaces the old 1500+ "big hit" bonus (Bora's pierce). Damage is mostly the clock's business (ClockValue).
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
                double ourClock = ClockValue(WithoutMonsters(ours, noClock), theirs), theirClock = TheirClockShare * ClockValue(theirs, ours, TheirNextAttack(theirs));
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
            // Gorz in our hand with our field empty: it can come out when they hit us directly (user, 2026-11-17: "if you have a gorz in hand,
            // you should get a small bonus for ending on an empty field because it'll let you use gorz").
            if (!won && ours.Monsters.Count == 0 && ours.SpellCodes.Count == 0 && ours.HandCodes.Any(c => SearchHost.Is(c, GorzCode)))
            {
                diff += GorzReadyBonus;
                Part("Gorz ready (empty field)", GorzReadyBonus);
            }
            before = diff;
            // A won line: what it spent, not the board it leaves (user, 2026-11-18: "yes, on that change to winning lines": a superfluous Bora
            // summoned on the way to a win scored higher for the extra board). As an expected value, so a risky win compares like for like
            // with lines that don't win: if the win works (its chance), the decision's own position plus the win bonus, less WinCardCost per
            // card of ours the line used up; if it doesn't, the board the line actually ends with.
            if (won)
            {
                double chance = WinChance(ourTurnEnd?.Attacked ?? true, theirs);
                // The win's bonus by its risk (user, 2026-11-18: "non-risk wins are worth 10, other wins just get a +5"; "a sliver of risk halving the
                // bonus doesn't feel wrong to me - it's like a black swan effect - lots of those winning lines will cause you to lose if they go
                // wrong because you overspend"): WinBonus only for a sure win, RiskyWinBonus for any other.
                double bonus = chance >= 1 ? WinBonus : RiskyWinBonus;
                diff = chance * (center + bonus - WinCardCost * Math.Max(0, cardsSpent) - penalty) + (1 - chance) * diff; // the rules it broke cost in both
                Part("Win (by its chance, in place of the board)", diff - before);
            }
            // A position still alive sits clearly between a loss and a win: far behind, the squashed score is ~0 and a line that
            // survives (the Dupe lock at 300 LP) looked no better than one that dies (seen 2026-11-11: every option scored 0-2
            // and the bot summoned Thestalos into a lost board instead of locking).
            // Centred on the decision's own position (center: PositionDiff at the root; user, 2026-11-18): a line scores by how much better or
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
        internal const string OurCardsPart = "Our cards (hand, field, graveyard)";
        internal const string TheirCardsPart = "Their cards";
        internal const string DominancePart = "Board dominance";
        internal const string ThreatPart = "Their threat to our board";
        internal const string OurClockPart = "Our clock on them";
        internal const string TheirClockPart = "Their clock on us";
        /// <summary>Not a card-unit part: the chance they kill us from the board we left, which scales the line's score down.</summary>
        internal const string LethalPart = "~lethal";

        /// <summary>PositionDiff's terms by name (the breakdown's starting point): the top-level ones add up to PositionDiff; the names with
        /// SubPart in them are the two sides' categories, a split of their cards' parts.</summary>
        public Dictionary<string, double> PositionParts(DuelHost host)
        {
            var ours = host.Summary(0);
            var theirs = host.Summary(1);
            // Each side's categories too, as sub-rows (theirs negated): not added into PositionDiff, only the breakdown's yardstick.
            var subRows = new Dictionary<string, double>();
            void Sub(string name, double v) => subRows[name] = (subRows.TryGetValue(name, out var had) ? had : 0) + v;
            double ourSide = SideScore(ours, 0, theirs, (c, v) => Sub(OurCardsPart + SubPart + c, v));
            double theirSide = SideScore(theirs, 1, ours, (c, v) => Sub(TheirCardsPart + SubPart + c, -v));
            var result = new Dictionary<string, double>
            {
                [OurCardsPart] = ourSide,
                [TheirCardsPart] = -theirSide,
                [DominancePart] = Dominance(ours, theirs, 0) - Dominance(theirs, ours, 1),
                [ThreatPart] = -Threat(ours),
                [OurClockPart] = ClockValue(ours, theirs),
                [TheirClockPart] = -TheirClockShare * ClockValue(theirs, ours, TheirNextAttack(theirs)),
            };
            foreach (var (name, v) in subRows)
                result[name] = v;
            return result;
        }

        /// <summary>
        /// The board difference of a position as Evaluate measures it (both sides' cards, dominance, their threat, both clocks), without a
        /// line's own costs: the decision's position, the centre its lines are scored around (Evaluate's center).
        /// </summary>
        public double PositionDiff(DuelHost host)
        {
            var ours = host.Summary(0);
            var theirs = host.Summary(1);
            return SideScore(ours, 0, theirs) - SideScore(theirs, 1, ours) + Dominance(ours, theirs, 0) - Dominance(theirs, ours, 1) - Threat(ours) +
                   ClockValue(ours, theirs) - TheirClockShare * ClockValue(theirs, ours, TheirNextAttack(theirs));
        }

        /// <summary>ATK steps of <see cref="AttackerOdds"/>: index i is "an attacker of at least i × 100 ATK".</summary>
        [Tunable("ATK steps of AttackerOdds: index i is \"an attacker of at least i × 100 ATK\".")]
        public static int AttackerOddsStep = 100;
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
        public static double AtkScale = 1.15;

        /// <summary>A monster's battle size: only the stat of the position it's in, ATK in attack, DEF (DefenceShare of it) in defence (user,
        /// 2026-11-18: "only give them the position they're in in that calc"; a defence-position Bora counted its 1700 ATK, so switching it to
        /// attack gained nothing).</summary>
        private static double Power((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m) =>
            m.AttackPosition ? m.Attack : m.Defense * DefenceShare;

        /// <summary>Bonus for the single largest face-up monster on the whole field, to its side (user, 2026-11-13: "move the
        /// dominance score down to just 0.1 for now, and only apply it if its the largest on both sides of the field ... nominal -
        /// a lever we can pull later"; the ATK table does most of the work).</summary>
        [Tunable("Bonus for the single largest face-up monster on the whole field, to its side (user, 2026-11-13: \"move the dominance score down to just 0.1 for now, and only apply it if its the largest on both sides of the field ... nominal - a lever we can pull later\"; the ATK table does most of the work).")]
        public static double DominanceBonus = 0.1;
        /// <summary>Extra when that monster is the one that dominated when the search started. 0 for now: a lever for later.</summary>
        [Tunable("Extra when that monster is the one that dominated when the search started. 0 for now: a lever for later.")]
        public static double KeptDominanceBonus = 0;

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
        public double SittingDuckExposure(SideSummary ours, SideSummary theirs)
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
                int power = m.AttackPosition ? m.Attack : m.Defense;
                bool destroyed = TheirHonestKnown || (m.AttackPosition ? power <= attack : power < attack);
                if (destroyed)
                    worst = Math.Max(worst, MonsterLoss(m, 0));
            }
            return SittingDuckWeight * worst;
        }

        [Tunable("Our face-up monsters summoned in Main Phase 2 (or on the duel's first turn) count this share of their board value; monster sets count in full. User, 2026-11-18.")]
        public static double Main2BoardShare = 0.25; // user, 2026-11-19: "strengthen the penalty for summoning something in mp2" (was 0.5)

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
        public static int NoListNormalAttack = 1800;

        [Tunable("Sitting duck without deck-list data: their assumed tribute attacker's ATK (user, 2026-11-18: 2400).")]
        public static int NoListTributeAttack = 2400;

        [Tunable("The sitting-duck loss counts this share (the assumed attacker is taken as certain: 1 = the whole monster).")]
        public static double SittingDuckWeight = 0.5; // 2026-11-18: assumed attacker taken as certain; half weight so monsters still get used (user)

        // ------------------------------------------------------------------ what each card is worth where it is (card units)

        [Tunable("A face-up monster's base worth, before its ATK (the ATK table) and its effects.")]
        public static double MonsterBase = 0.3; // user, 2026-11-19 (was 0.6), with AtkScale 1.15
        [Tunable("A monster in defence position counts this share of its DEF as its battle size (its ATK doesn't count there). User, 2026-11-18: 100%.")]
        public static double DefenceShare = 1.0;
        [Tunable("A face-down monster that gains from being set (a flip, a floater, a graveyard effect): its base worth.")]
        public static double SetFlipBase = 0.9;
        // A set flip monster's effect is on the field already, ready to go: weighed like a field effect, so using it costs what it's worth
        // (user, 2026-11-18: "using a one-time effect like ryko's should have cost the value of that effect, while attacking should have had no
        // cost": at the held weight a set Ryko's destroy was ~0.3 card units, and Ryko was flipped for a kill an attack would have made).
        [Tunable("Weight of a set flip / floater monster's effect points (its effect still to be used), like FieldEffectWeight: using it costs that much.")]
        public static double SetFlipEffectWeight = 0.25;
        [Tunable("Any other face-down monster: just a wall (walls are worth little).")]
        public static double WallValue = 0.3;
        [Tunable("A face-up spell/trap's base worth, before its lasting effect.")]
        public static double FaceUpSpellBase = 0.3;
        // Set cards by kind (user, 2026-11-18: "shouldn't there be separate ones for spells and traps (and a third for quick-play spells)?
        // happy with raising it a bit in line with the new hand scores"): each 0.3 up with a hand card (0.5 -> 0.8), the old set-trap /
        // set-quick-play bonuses (0.25 / 0.1) folded in, so setting a trap still beats holding it by 0.25.
        [Tunable("A set normal / continuous / field / equip spell's base worth, before what it will do: 0.5 below a hand card, so setting one costs that.")]
        public static double SetSpellBase = 0.8; // user, 2026-11-19: back to 0.8, with LineTally.SetNormalSpellCost 0.5 for setting a normal spell (was 0.3 on 2026-11-18)
        [Tunable("A set quick-play spell's base worth, before what it will do.")]
        public static double SetQuickPlayBase = 0.9;
        [Tunable("A set trap's base worth, before what it will do.")]
        public static double SetTrapBase = 1.25; // user, 2026-11-19: "no traps are being set - increase the setting traps bonus" (was 1.05)
        [Tunable("A card in hand: its base worth, before its effects and ATK.")]
        public static double HandCardBase = 0.8; // user, 2026-11-18 (was 0.5)
        [Tunable("A monster in hand: its ATK counts as effect points at ATK / this.")]
        public static double HandAtkScale = 2000;
        [Tunable("A Level 5+ monster in hand with the tributes for it on the field: its base worth as a setup.")]
        public static double TributeSetupBase = 0.3;
        [Tunable("Life points: the first LpLowBand of them are worth this many card units in all (each point matters more when low).")]
        public static double LpLowWorth = 0; // user, 2026-11-18: "we should just use the clock" (was 2.5)
        [Tunable("Life points: the band that counts at LpLowWorth.")]
        public static int LpLowBand = 2000;
        [Tunable("Life points: everything from LpLowBand to 8000 is worth this many card units in all.")]
        public static double LpHighWorth = 0; // user, 2026-11-18: the clock instead (was 1.0)
        [Tunable("With our deck under this many cards, each missing card costs LowDeckCostPerCard (deck-out risk).")]
        public static int LowDeckFrom = 5;
        [Tunable("The deck-out cost per card under LowDeckFrom (card units).")]
        public static double LowDeckCostPerCard = 0.5;

        // ------------------------------------------------------------------ their unknown cards, risk, wins

        [Tunable("Each of their unknown cards (hand, set) counts as this much of a card of theirs.")]
        public static double UnknownCardWorth = 0.5;
        [Tunable("The most of our board their unknown cards are assumed to answer (a chance, 0-1).")]
        public static double ThreatRiskMax = 0.9;
        [Tunable("Their unknown cards' threat: the share of our best face-up monster at risk.")]
        public static double ThreatFirstShare = 0.6;
        [Tunable("Their unknown cards' threat: the share of each other face-up monster at risk.")]
        public static double ThreatRestShare = 0.2;
        [Tunable("How much the fear of their cards drops at full pressure (risk tolerance at its top): 0.6 = to 40%.")]
        public static double RiskDiscountMax = 0.6;

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
        public static double AliveFloor = 0.05;

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
            double grave = token ? 0 : (card.Grave > 0 ? card.Grave : GraveMonsterShare * (MonsterBase + AtkValue(Math.Max(0, SearchHost.Cards.Get(m.Code)?.Attack ?? 0)) + FieldEffectWeight * Taper(card.Field))) + Bonus(m.Code, CardPlace.Grave, side);
            return Math.Max(0, field - grave);
        }

        /// <summary>Torrential Tribute springing now (on our summon): what we'd lose, net of what they'd lose, by its chance now.</summary>
        public double TorrentialExposure(SideSummary ours, SideSummary theirs) =>
            ChanceWith(TorrentialChance, UnknownSetsIn(theirs)) *
            Math.Max(0, ours.Monsters.Sum(m => MonsterLoss(m, 0)) - theirs.Monsters.Sum(m => MonsterLoss(m, 1)));

        /// <summary>Mirror Force springing now (on our attack): our attack-position monsters, by its chance now.</summary>
        public double MirrorExposure(SideSummary ours, SideSummary theirs) =>
            ChanceWith(MirrorForceChance, UnknownSetsIn(theirs)) * ours.Monsters.Where(m => m.FaceUp && m.AttackPosition).Sum(m => MonsterLoss(m, 0));

        /// <summary>Single-card removal and Icarus Attack count at this share of what Mirror Force / Torrential count (user, 2026-11-15: "~25%").</summary>
        [Tunable("Single-card removal and Icarus Attack count at this share of what Mirror Force / Torrential count (user, 2026-11-15: \"~25%\").")]
        public static double LesserExposureWeight = 0.25;

        /// <summary>The chance one of their unknown set cards is Icarus Attack (Blackwing lists).</summary>
        public double IcarusChance { get; init; }

        /// <summary>Held monsters count at this share of their field worth: safe from the removal, but they accomplish nothing this turn
        /// (user, 2026-11-15: Fortress and Gearframe both out must beat Fortress alone with Gearframe held).</summary>
        [Tunable("Held monsters count at this share of their field worth: safe from the removal, but they accomplish nothing this turn (user, 2026-11-15: Fortress and Gearframe both out must beat Fortress alone with Gearframe held).")]
        public static double ReserveWeight = 0.5;

        /// <summary>The monsters in our hand, by what they'd be worth on the field: what we still have if their removal hits the field.</summary>
        private double ReserveWorth(SideSummary ours) => ReserveWeight *
            ours.HandCodes.Where(c => c != SearchView.InertCard).Sum(code =>
                SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0 && !r.IsExtraDeckMonster
                    ? MonsterBase + AtkValue(r.Attack) + FieldEffectWeight * Taper(_values.Get(code, 0).Field) : 0);

        // What one of our spells/traps is worth where it stands (lost to a wipe or Icarus).
        private double SpellLoss((int Code, bool FaceUp) s) =>
            s.FaceUp ? FaceUpSpellBase + FieldEffectWeight * Taper(_values.Get(s.Code, 0).Field) + Bonus(s.Code, CardPlace.Field, 0)
                     : SetBase(s.Code) + HeldEffectWeight * Taper(_values.Get(s.Code, 0).Held) + Bonus(s.Code, CardPlace.Set, 0);

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
            return total <= 0 ? 0 : LesserExposureWeight * ChanceWith(Math.Min(1, OtherStopChance), UnknownSetsIn(theirs)) * best * (best / total);
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
            return total <= 0 ? 0 : LesserExposureWeight * ChanceWith(IcarusChance, UnknownSetsIn(theirs)) * two * (two / total);
        }

        /// <summary>Heavy Storm on their turn: our set spells/traps (each by what it would do), by its chance.</summary>
        public double HeavyExposure(SideSummary ours)
        {
            if (HeavyStormChance <= 0)
                return 0;
            // Bait (a set card worth chaining in response: Jar of Greed, Dust Tornado, MST...) isn't lost to it (CSV S2).
            var set = ours.SpellCodes.Where(c => !c.FaceUp && c.Code != SearchView.InertCard && !BaitCodes.Contains(c.Code))
                .Select(c => SetBase(c.Code) + HeldEffectWeight * Taper(_values.Get(c.Code, 0).Held) + Bonus(c.Code, CardPlace.Set, 0)).ToList();
            // Each extra set card makes the wipe worse than one more card's worth (CSV S2: "gradually stops setting them").
            return HeavyStormChance * set.Sum() * Math.Max(1, set.Count * OversetCurve);
        }

        /// <summary>The Heavy Storm charge grows with the number of our set cards: x max(1, n x this) (CSV S2).</summary>
        [Tunable("The Heavy Storm charge grows with the number of our set cards: x max(1, n x this) (CSV S2).")]
        public static double OversetCurve = 0.5;

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
        private double SetCardSituational(int code, CardValue card, double protectNeed, double lpNeed, List<int> ourSets, SideSummary? other)
        {
            double extra = 0;
            // Only a card that can act on their turn earns these: a trap or a quick-play spell. A set normal spell (Brain Control) can't
            // protect or remove anything until our next turn, so it only pays for being set (SetSpellBase is 0.5 below a hand card) (user, 2026-11-17: "there
            // should've been a penalty for setting a normal spell"; it was earning removal credit instead).
            if (SearchHost.Cards.Get(code) is not { } setCard || !((setCard.Type & 0x4) != 0 || (setCard.Type & 0x2) != 0 && (setCard.Type & 0x10000) != 0))
                return 0;
            // Protects a monster worth protecting; with our LP low, any card that stops or removes an attacker.
            extra += ProtectionWeight * card.Protects * Math.Min(protectNeed, ProtectNeedCap);
            extra += LowLpRemovalWeight * lpNeed * Math.Max(card.Protects, (card.Removes & Removes.Monster) != 0 ? 1 : 0);
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
                extra += RemoveThreatWeight * best;
            }
            // Bait (Jar of Greed, Dust Tornado...): their MST, Lyla or Ryko picking among our sets may take it instead of a better one.
            if (BaitCodes.Contains(code) && ourSets.Count > 1)
            {
                double others = ourSets.Where(c => !BaitCodes.Contains(c)).Sum(c => SetBase(c) + HeldEffectWeight * Taper(_values.Get(c, 0).Held));
                extra += BaitWeight * others / ourSets.Count;
            }
            return extra;
        }

        /// <summary>A set card that protects (CardValue.Protects) gains this times what our most-at-risk monster needs (ProtectionNeed).
        /// Card units; adjust slowly.</summary>
        [Tunable("A set card that protects (CardValue.Protects) gains this times what our most-at-risk monster needs (ProtectionNeed). Card units; adjust slowly.")]
        public static double ProtectionWeight = 0.5;
        /// <summary>The most a monster's protection need counts for (card units).</summary>
        [Tunable("The most a monster's protection need counts for (card units).")]
        public static double ProtectNeedCap = 2;
        /// <summary>How much of a monster's body counts toward "how good it is" for protection (its effects count in full).</summary>
        [Tunable("How much of a monster's body counts toward \"how good it is\" for protection (its effects count in full).")]
        public static double ProtectBodyShare = 0.5;
        /// <summary>With our LP at LowLpFullAt or below, a set card that stops or removes an attacker gains this much (card units).</summary>
        [Tunable("With our LP at LowLpFullAt or below, a set card that stops or removes an attacker gains this much (card units); less as LP rise (LowLpScale).")]
        public static double LowLpRemovalWeight = 0.6;
        [Tunable("Our LP at or below which a stopping/removing set card gets the full LowLpRemovalWeight.")]
        public static int LowLpFullAt = 100;
        [Tunable("How fast that value falls as our LP rise above LowLpFullAt: exp(-(LP - LowLpFullAt) / this). 1500: about 55% at 1000 LP, 28% at 2000, 7% at 4000, 0.5% at 8000.")]
        public static double LowLpScale = 1500;
        /// <summary>A set card that can remove a card of theirs already on the field gains this times that card's worth (card units).</summary>
        [Tunable("A set card that can remove a card of theirs already on the field gains this times that card's worth (card units).")]
        public static double RemoveThreatWeight = 0.3;
        /// <summary>The worth of an unknown set card of theirs, as a removal target.</summary>
        [Tunable("The worth of an unknown set card of theirs, as a removal target.")]
        public static double UnknownSetWorth = 0.5;
        /// <summary>A set bait card gains this times our other sets' worth over the number of our sets (the share of a random pick).</summary>
        [Tunable("A set bait card gains this times our other sets' worth over the number of our sets (the share of a random pick).")]
        public static double BaitWeight = 0.5;

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
            var bodies = ours.Monsters.Where(m => m.FaceUp)
                .Select(m => MonsterBase + AtkValue(Power(m)))
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

        private const int SolemnJudgmentCode = 41420027;

        [Tunable("A set Solemn Judgment gains this times the worth of our strongest card still in hand (capped at SolemnGuardCap): it protects that play next turn.")]
        public static double SolemnGuardWeight = 0.3;
        [Tunable("The most a hand card's worth counts for in the Solemn bonus (card units).")]
        public static double SolemnGuardCap = 2;
        [Tunable("A hand card counts as strong for the Solemn bonus from this worth (effect points plus ATK value).")]
        public static double SolemnGuardMinWorth = 2;

        /// <summary>
        /// A set Solemn Judgment protects the strong cards we still hold for next turn (user, 2026-11-18: "solemn should get a bonus if we have
        /// any strong cards in hand that we aren't using that turn, that we may be able to use next turn, as well as its other bonuses"):
        /// SolemnGuardWeight times our strongest hand card's worth (its effect points in hand plus its ATK value), if it's strong enough.
        /// </summary>
        private double SolemnGuardsHand(SideSummary ours)
        {
            double best = 0;
            foreach (int code in ours.HandCodes)
            {
                var card = _values.Get(code, 0);
                double atk = SearchHost.Available && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0 ? AtkValue(r.Attack) : 0;
                best = Math.Max(best, card.Held + atk);
            }
            return best >= SolemnGuardMinWorth ? SolemnGuardWeight * Math.Min(SolemnGuardCap, best) : 0;
        }

        /// <summary>
        /// A side's worth in card units. <paramref name="into"/> (the log's breakdown; user, 2026-11-20: "I don't quite understand the
        /// 'our cards' and 'their cards' - please add a breakdown for how these are generated") gets each category's share by name; they
        /// add up to the returned value.
        /// </summary>
        public double SideScore(SideSummary s, int side, SideSummary? other = null, Action<string, double>? into = null)
        {
            double Effect(double points, double weight) => weight * Taper(points);
            double value = 0;
            void Add(string category, double v)
            {
                value += v;
                if (into != null && v != 0)
                    into(category, v);
            }
            double lp = Math.Max(0, s.LifePoints);
            // Their life point gains count for nothing (CSV S6, user: "it's fine if they gain life points, who cares"): only damage counts.
            if (side == 1 && TheirStartLp > 0)
                lp = Math.Min(lp, TheirStartLp);
            Add(SideLifePoints, (LpLowWorth * Math.Min(lp, LpLowBand) / LpLowBand + LpHighWorth * Math.Max(0, Math.Min(lp, 8000) - LpLowBand) / (8000.0 - LpLowBand)) *
                                (side == 1 ? 1 + Math.Clamp(RiskPress, 0, 1) : 1)); // their LP count for more when we must press (S13)

            // Monsters: body (current ATK, or DEF in defence) plus what their effects can still do on the field (nothing when
            // negated: a Synchro summoned by Vayu is only its body).
            foreach (var m in s.Monsters)
            {
                var card = _values.Get(m.Code, side);
                // Lyla's effect destroys a spell/trap: with none on the other side it's worth nothing (user, 2026-11-18).
                bool idle = SearchHost.Is(m.Code, LylaCode) && other != null && other.SpellCodes.Count == 0;
                if (m.FaceUp)
                {
                    Add(SideMonsterBodies, MonsterBase + AtkValue(Power(m)));
                    Add(SideMonsterEffects, m.Negated || idle ? 0 : Effect(card.Field, FieldEffectWeight));
                }
                else
                    Add(SideSetMonsters, card.LikesSet
                        ? SetFlipBase + Effect(card.Held, SetFlipEffectWeight) // a flip or a floater, still to be used
                        : WallValue); // just a wall: walls are worth little
                Add(SideOverrides, Bonus(m.Code, m.FaceUp ? CardPlace.Field : CardPlace.Set, side));
            }
            // Spells/traps: set ones by what they'll do (a set Mirror Force beats a bluff), face-up ones by their lasting effect.
            // Our set cards' situational worth (user, 2026-11-16): protecting a monster worth protecting, removing a monster when our LP
            // are low, removing a card of theirs already on the field, and bait that draws their spell/trap removal off the others.
            var (protectNeed, lpNeed) = side == 0 && other != null ? ProtectionNeed(s, other) : (0, 0);
            var ourSets = side == 0 ? s.SpellCodes.Where(c => !c.FaceUp && c.Code != SearchView.InertCard).Select(c => c.Code).ToList() : new List<int>();
            foreach (var (code, faceUp) in s.SpellCodes.Where(c => c.Code != SearchView.InertCard)) // stand-ins count in Threat
            {
                var card = _values.Get(code, side);
                if (faceUp)
                    Add(SideFaceUpSpells, FaceUpSpellBase + Effect(card.Field, FieldEffectWeight));
                else
                    Add(SideSetSpells, SetBase(code) + Effect(card.Held, HeldEffectWeight));
                if (!faceUp && side == 0)
                    Add(SideSetSituational, SetCardSituational(code, card, protectNeed, lpNeed, ourSets, other) + (SearchHost.Is(code, SolemnJudgmentCode) ? SolemnGuardsHand(s) : 0));
                Add(SideOverrides, Bonus(code, faceUp ? CardPlace.Field : CardPlace.Set, side));
            }
            // Hand: every card is an option; strong effects and big bodies more so.
            foreach (int code in s.HandCodes.Where(c => c != SearchView.InertCard))
            {
                var card = _values.Get(code, side);
                int atk = SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0 ? r.Attack : 0;
                Add(SideHand, HandCardBase + Effect(card.Held + atk / HandAtkScale, HeldEffectWeight));
                Add(SideOverrides, Bonus(code, CardPlace.Hand, side));
            }
            // Graveyard: setups (cards that work from there: Vayu, Plaguespreader, Treeborn, Necro Gardna).
            // Graveyard: the grave scorer's value as it is (user, 2026-11-18: "graveyard weights should be their weight according to the grave
            // scorer, not 15% of anything"). A card without a graveyard effect (or an override, Bonus): a spell/trap 0, a monster 5% of
            // its field worth (face-up in attack, its printed ATK and field effects).
            foreach (int code in s.GraveCodes)
            {
                var card = _values.Get(code, side);
                double grave = card.Grave;
                if (grave <= 0 && SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0)
                    grave = GraveMonsterShare * (MonsterBase + AtkValue(Math.Max(0, r.Attack)) + Effect(card.Field, FieldEffectWeight));
                Add(SideGraveyard, grave);
                Add(SideOverrides, Bonus(code, CardPlace.Grave, side));
            }
            Add(SideTributeSetup, TributeSetup(s, side));
            if (side == 0 && _setup != null)
            {
                double goal = 0;
                try { goal = _setup(s); }
                catch { /* a deck goal that fails counts nothing */ }
                Add(SideDeckGoals, goal);
            }

            if (s.Deck < LowDeckFrom)
                Add(SideLowDeck, -(LowDeckFrom - s.Deck) * LowDeckCostPerCard);
            return value;
        }

        // SideScore's categories (the breakdown's sub-rows of "Our cards" / "Their cards").
        internal const string SideLifePoints = "Life points";
        internal const string SideMonsterBodies = "Face-up monsters: bodies (ATK/DEF)";
        internal const string SideMonsterEffects = "Face-up monsters: effects";
        internal const string SideSetMonsters = "Set monsters";
        internal const string SideFaceUpSpells = "Face-up spells/traps";
        internal const string SideSetSpells = "Set spells/traps";
        internal const string SideSetSituational = "Set cards' situational worth";
        internal const string SideOverrides = "Card value overrides";
        internal const string SideHand = "Hand";
        internal const string SideGraveyard = "Graveyard";
        internal const string SideTributeSetup = "Tribute summon ready";
        internal const string SideDeckGoals = "Deck goals (setups)";
        internal const string SideLowDeck = "Low deck";
        /// <summary>Joins a part and its sub-row in the breakdown's names: "Our cards (hand, field, graveyard) :: Hand".</summary>
        internal const string SubPart = " :: ";

        /// <summary>
        /// A tribute monster in hand (a Monarch) with enough monsters on the field to tribute for it: it can come out
        /// next turn. The best one counts, by its effects.
        /// </summary>
        private double TributeSetup(SideSummary s, int side)
        {
            double best = 0;
            foreach (int code in s.HandCodes)
            {
                if (SearchHost.Cards.Get(code) is not { } r || (r.Type & 0x1) == 0 || r.IsExtraDeckMonster || r.Level < 5)
                    continue;
                int needed = r.Level >= 7 ? 2 : 1;
                if (s.Monsters.Count >= needed)
                    best = Math.Max(best, TributeSetupBase + HeldEffectWeight * Taper(_values.Get(code, side).Held));
            }
            return best;
        }
    }
}
