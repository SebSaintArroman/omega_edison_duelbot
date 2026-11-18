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
        private static double FieldEffectWeight = 0.25, HeldEffectWeight = 0.1, GraveEffectWeight = 0.15;

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

        /// <summary>How much we have to press: the risk tolerance above its base, 0-1 (DeckBotBrain.RiskPressNow, CSV S13). Fear of their
        /// cards drops by up to 60% (RiskDiscount) and damage to them counts up to double.</summary>
        public double RiskPress { get; init; }

        /// <summary>Their life points as the search began: gains above this count for nothing (CSV S6). 0: not set.</summary>
        public int TheirStartLp { get; init; }

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
        public static double DamageBonusPer1000 = 0.1;

        [Tunable("A side's clock on the other, as part of the board: worth ClockBonus / turns (card units) for a clock of that many turns (1 turn = the full bonus; no clock = 0). The turns are the brain's own clock (BoardAssessment.TurnsToWin).")]
        public static double ClockBonus = 1.5;
        [Tunable("Their clock on us counts this share of ClockBonus against a line (ours on them counts in full). User, 2026-11-18: \"tone down the penalty for being on a clock ... leave the bonus for the bot putting me on a clock as is\".")]
        public static double TheirClockShare = 0.5;

        /// <summary>
        /// A player's clock as part of the board (user, 2026-11-17: "make a player's clock a part of the board state evaluation, following the
        /// existing clock rules that the brain uses ... give it a bonus the lower the clock is at the end"): the brain's clock
        /// (BoardAssessment.TurnsToWin, the same function its push / hold / spend rules use) for this side's face-up attack-position monsters
        /// against the other side's monsters and life points, as ClockBonus / turns. Attacking lowers their life points, more monsters and
        /// monsters kept alive add ATK: all of them shorten it.
        /// </summary>
        internal static double ClockValue(SideSummary attacker, SideSummary defender)
        {
            int turns = ClockTurns(attacker, defender);
            return turns == BoardAssessment.NoClock ? 0 : ClockBonus / Math.Max(1, turns);
        }

        /// <summary>The brain's clock (turns to win) of this side on the other, from a search position.</summary>
        internal static int ClockTurns(SideSummary attacker, SideSummary defender) =>
            BoardAssessment.TurnsToWin(
                attacker.Monsters.Where(m => m.FaceUp && m.AttackPosition).Select(m => m.Attack),
                defender.Monsters.Select(m => new BoardAssessment.Defender(
                    m.FaceUp ? (m.AttackPosition ? m.Attack : m.Defense) : m.Defense, m.FaceUp && m.AttackPosition, !m.FaceUp, m.Code)),
                defender.LifePoints);

        [Tunable("Gorz in our hand and our field empty at the end of a line: this small bonus (card units): Gorz can come out on their direct hit.")]
        public static double GorzReadyBonus = 0.3;
        private const int GorzCode = 44330098;

        [Tunable("A line that wins: this many card units on top of its position, times the chance the win really works (WinChance). Large.")]
        public static double WinBonus = 10;

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
            double winRisk = 0, int cardsSpent = 0, int length = 0)
        {
            // A loss is 0. A win in the line is scored like any other line, plus a large bonus scaled down by the chance it really works: the
            // lines never fire their unknown cards, so a "win" through their set cards is only as sure as those cards being blanks (user,
            // 2026-11-17: "if the win isn't a sure thing, it shouldn't be counted as a win for the line. why not keep the win as a large line
            // bonus just get a partial amount of that, scaled down by the chance of it working?").
            bool won = host.Result is { Winner: 0 };
            if (host.Result is { Winner: >= 0 } && !won)
                return 0;
            var ours = host.Summary(0);
            var theirs = host.Summary(1);
            double diff = SideScore(ours, 0, theirs) - SideScore(theirs, 1) + Dominance(ours, theirs, 0) - Dominance(theirs, ours, 1) - Threat(ours) - penalty -
                          (ourTurnEnd is { } end ? Backrow(end.Ours, end.Theirs, end.Attacked) : 0);
            if (ourTurnEnd is { Attacked: true } attackedEnd)
            {
                // Damage dealt in our turn: a little extra, more for more (user, 2026-11-16: "a small bonus for dealing direct damage"; 2026-11-17:
                // "can we scale it by amount of damage dealt? ... I'd like this to only ever be small, but more attack is better than less").
                // It replaces the old 1500+ "big hit" bonus (Bora's pierce). Damage is mostly the clock's business (ClockValue).
                if (TheirStartLp > 0 && attackedEnd.Theirs.LifePoints < TheirStartLp)
                    diff += DamageBonusPer1000 * (TheirStartLp - attackedEnd.Theirs.LifePoints) / 1000.0;
            }
            // Each side's clock on the other at the end of the line, as part of the board: ours on them counts for us, theirs on us against.
            if (!won)
                diff += ClockValue(ours, theirs) - TheirClockShare * ClockValue(theirs, ours);
            // Gorz in our hand with our field empty: it can come out when they hit us directly (user, 2026-11-17: "if you have a gorz in hand,
            // you should get a small bonus for ending on an empty field because it'll let you use gorz").
            if (!won && ours.Monsters.Count == 0 && ours.SpellCodes.Count == 0 && ours.HandCodes.Any(c => SearchHost.Is(c, GorzCode)))
                diff += GorzReadyBonus;
            if (won)
                diff += WinBonus * WinChance(ourTurnEnd?.Attacked ?? true, theirs);
            // A position still alive sits clearly between a loss and a win: far behind, the squashed score is ~0 and a line that
            // survives (the Dupe lock at 300 LP) looked no better than one that dies (seen 2026-11-11: every option scored 0-2
            // and the bot summoned Thestalos into a lost board instead of locking).
            double score = AliveFloor + (1 - 2 * AliveFloor) / (1 + Math.Exp(-diff / Scale));
            // The lines' unknown cards (their hand, their next draw) are inert, so a position that dies to any monster they
            // draw (300 LP, a 400-ATK Plaguespreader stuck in attack position: seen 2026-11-12, they drew Card Trooper) scored
            // as safe. Weigh the line by the chance they kill us from what we left as our turn ended.
            if (ourTurnEnd is { } turnEnd && !won)
                score *= 1 - LethalExposure(turnEnd.Ours);
            return score;
        }

        /// <summary>ATK steps of <see cref="AttackerOdds"/>: index i is "an attacker of at least i × 100 ATK".</summary>
        [Tunable("ATK steps of AttackerOdds: index i is \"an attacker of at least i × 100 ATK\".")]
        public static int AttackerOddsStep = 100;
        public static readonly int[] AttackerThresholds = Enumerable.Range(0, 41).Select(i => i * AttackerOddsStep).ToArray();

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
        public static double AtkValue(double power) => AtkTable[Math.Clamp((int)(Math.Max(0, power) / 50), 0, AtkTable.Length - 1)];

        /// <summary>A monster's battle size: ATK, or DEF at 60% in defence.</summary>
        private static double Power((int Code, int Attack, int Defense, bool FaceUp, bool AttackPosition, int Level, int Type, bool Negated) m) =>
            Math.Max(m.Attack, m.AttackPosition ? 0 : m.Defense * DefenceShare);

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
                double perCard = atk <= 1400 ? 0.35 : atk <= 1600 ? 0.28 : atk <= 1800 ? 0.2 : atk <= 1900 ? 0.14 : atk <= 2100 ? 0.05 : atk <= 2400 ? 0.02 : 0;
                return 1 - Math.Pow(1 - perCard, Math.Max(0, unknownCards));
            }).ToArray();

        /// <summary>
        /// The chance their unknown cards (hand plus the next draw) kill us as our turn ended: an attacker through our weakest
        /// attack-position monster, or directly into an empty field, for at least our life points (it needs our life points
        /// more ATK than that monster, or our life points direct). 0 when nothing they're likely to draw is enough.
        /// </summary>
        /// <summary>
        /// Sitting duck (C2, user 2026-11-16): the lines' unknown cards of theirs are inert, so a monster of ours a new attacker of theirs
        /// would run over looked safe. As our turn ends: the worst single loss, the chance their unknown cards hold a monster bigger
        /// than ours (ATK in attack position, DEF in defence) times that monster's body value, times SittingDuckWeight. Face-down
        /// monsters are left out (they can't see what they'd hit). Small: adjust slowly.
        /// </summary>
        public double SittingDuckExposure(SideSummary ours)
        {
            if (AttackerOdds == null)
                return 0;
            double worst = 0;
            foreach (var m in ours.Monsters.Where(m => m.FaceUp))
            {
                int power = m.AttackPosition ? m.Attack : m.Defense;
                int index = (power + AttackerOddsStep) / AttackerOddsStep; // an attacker above it
                double chance = index >= AttackerOdds.Length ? 0 : AttackerOdds[index];
                worst = Math.Max(worst, chance * (MonsterBase + AtkValue(Math.Max(0, m.Attack))));
            }
            return SittingDuckWeight * worst;
        }

        [Tunable("")]
        public static double SittingDuckWeight = 0.3;

        // ------------------------------------------------------------------ what each card is worth where it is (card units)

        [Tunable("A face-up monster's base worth, before its ATK (the ATK table) and its effects.")]
        public static double MonsterBase = 0.6;
        [Tunable("A monster in defence position counts this share of its DEF as its battle size.")]
        public static double DefenceShare = 0.6;
        [Tunable("A face-down monster that gains from being set (a flip, a floater, a graveyard effect): its base worth.")]
        public static double SetFlipBase = 0.9;
        [Tunable("Any other face-down monster: just a wall (walls are worth little).")]
        public static double WallValue = 0.3;
        [Tunable("A face-up spell/trap's base worth, before its lasting effect.")]
        public static double FaceUpSpellBase = 0.3;
        [Tunable("A set spell/trap's base worth, before what it will do.")]
        public static double SetSpellBase = 0.5;
        [Tunable("A card in hand: its base worth, before its effects and ATK.")]
        public static double HandCardBase = 0.5;
        [Tunable("A monster in hand: its ATK counts as effect points at ATK / this.")]
        public static double HandAtkScale = 2000;
        [Tunable("A Level 5+ monster in hand with the tributes for it on the field: its base worth as a setup.")]
        public static double TributeSetupBase = 0.3;
        [Tunable("Life points: the first LpLowBand of them are worth this many card units in all (each point matters more when low).")]
        public static double LpLowWorth = 2.5;
        [Tunable("Life points: the band that counts at LpLowWorth.")]
        public static int LpLowBand = 2000;
        [Tunable("Life points: everything from LpLowBand to 8000 is worth this many card units in all.")]
        public static double LpHighWorth = 1.0;
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
                : card.LikesSet ? SetFlipBase + HeldEffectWeight * Taper(card.Held) : WallValue;
            field += Bonus(m.Code, m.FaceUp ? CardPlace.Field : CardPlace.Set, side);
            bool token = (m.Type & 0x4000) != 0;
            double grave = token ? 0 : GraveEffectWeight * Taper(card.Grave) + Bonus(m.Code, CardPlace.Grave, side);
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
                     : SetSpellBase + HeldEffectWeight * Taper(_values.Get(s.Code, 0).Held) + Bonus(s.Code, CardPlace.Set, 0);

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
                .Select(c => SetSpellBase + HeldEffectWeight * Taper(_values.Get(c.Code, 0).Held) + Bonus(c.Code, CardPlace.Set, 0) + SetBonus(c.Code)).ToList();
            // Each extra set card makes the wipe worse than one more card's worth (CSV S2: "gradually stops setting them").
            return HeavyStormChance * set.Sum() * Math.Max(1, set.Count * OversetCurve);
        }

        /// <summary>The bonus of a set trap over the same trap in hand (it can act on their turn), and a much smaller one for a set quick-play
        /// spell (CSV S2/S3). Card units; numbers to confirm with the user.</summary>
        [Tunable("The bonus of a set trap over the same trap in hand (it can act on their turn), and a much smaller one for a set quick-play spell (CSV S2/S3). Card units; numbers to confirm with the user.")]
        public static double SetTrapBonus = 0.25, SetQuickPlayBonus = 0.1;
        /// <summary>A normal / continuous / field / equip spell is worth this much more in hand than set (CSV S3: setting Miracle Dig was a bad play).</summary>
        [Tunable("A normal / continuous / field / equip spell is worth this much more in hand than set (CSV S3: setting Miracle Dig was a bad play).")]
        public static double SpellInHandBonus0 = 0.5; // user, 2026-11-17: Brain Control set again: "increase the penalty for setting a non-chainable card" (was 0.1)
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
            // protect or remove anything until our next turn, so it only pays SpellInHandBonus0 for being set (user, 2026-11-17: "there
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
                double others = ourSets.Where(c => !BaitCodes.Contains(c)).Sum(c => SetSpellBase + HeldEffectWeight * Taper(_values.Get(c, 0).Held));
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

        private static double SetBonus(int code) =>
            SearchHost.Cards.Get(code) is not { } r ? 0 : (r.Type & 0x4) != 0 ? SetTrapBonus : (r.Type & 0x2) != 0 && (r.Type & 0x10000) != 0 ? SetQuickPlayBonus : 0;

        private static double SpellInHandBonus(int code) =>
            SearchHost.Cards.Get(code) is { } r && (r.Type & 0x2) != 0 && (r.Type & 0x10000) == 0 ? SpellInHandBonus0 : 0;

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
            double risk = Math.Min(ThreatRiskMax, UnknownCardRisk * _unknownCards * (0.5 + _explosiveness));
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

        public double SideScore(SideSummary s, int side, SideSummary? other = null)
        {
            double Effect(double points, double weight) => weight * Taper(points);
            double lp = Math.Max(0, s.LifePoints);
            // Their life point gains count for nothing (CSV S6, user: "it's fine if they gain life points, who cares"): only damage counts.
            if (side == 1 && TheirStartLp > 0)
                lp = Math.Min(lp, TheirStartLp);
            double value = (LpLowWorth * Math.Min(lp, LpLowBand) / LpLowBand + LpHighWorth * Math.Max(0, Math.Min(lp, 8000) - LpLowBand) / (8000.0 - LpLowBand)) *
                           (side == 1 ? 1 + Math.Clamp(RiskPress, 0, 1) : 1); // their LP count for more when we must press (S13)

            // Monsters: body (current ATK, or DEF in defence) plus what their effects can still do on the field (nothing when
            // negated: a Synchro summoned by Vayu is only its body).
            foreach (var m in s.Monsters)
            {
                var card = _values.Get(m.Code, side);
                value += m.FaceUp
                    ? MonsterBase + AtkValue(Power(m)) + (m.Negated ? 0 : Effect(card.Field, FieldEffectWeight))
                    : card.LikesSet
                        ? SetFlipBase + Effect(card.Held, HeldEffectWeight) // a flip or a floater, still to be used
                        : WallValue; // just a wall: walls are worth little
                value += Bonus(m.Code, m.FaceUp ? CardPlace.Field : CardPlace.Set, side);
            }
            // Spells/traps: set ones by what they'll do (a set Mirror Force beats a bluff), face-up ones by their lasting effect.
            // Our set cards' situational worth (user, 2026-11-16): protecting a monster worth protecting, removing a monster when our LP
            // are low, removing a card of theirs already on the field, and bait that draws their spell/trap removal off the others.
            var (protectNeed, lpNeed) = side == 0 && other != null ? ProtectionNeed(s, other) : (0, 0);
            var ourSets = side == 0 ? s.SpellCodes.Where(c => !c.FaceUp && c.Code != SearchView.InertCard).Select(c => c.Code).ToList() : new List<int>();
            foreach (var (code, faceUp) in s.SpellCodes.Where(c => c.Code != SearchView.InertCard)) // stand-ins count in Threat
            {
                var card = _values.Get(code, side);
                value += faceUp ? FaceUpSpellBase + Effect(card.Field, FieldEffectWeight) : SetSpellBase + Effect(card.Held, HeldEffectWeight) + (side == 0 ? SetBonus(code) : 0);
                if (!faceUp && side == 0)
                    value += SetCardSituational(code, card, protectNeed, lpNeed, ourSets, other) + (SearchHost.Is(code, SolemnJudgmentCode) ? SolemnGuardsHand(s) : 0);
                value += Bonus(code, faceUp ? CardPlace.Field : CardPlace.Set, side);
            }
            // Hand: every card is an option; strong effects and big bodies more so.
            foreach (int code in s.HandCodes.Where(c => c != SearchView.InertCard))
            {
                var card = _values.Get(code, side);
                int atk = SearchHost.Cards.Get(code) is { } r && (r.Type & 0x1) != 0 ? r.Attack : 0;
                value += HandCardBase + Effect(card.Held + atk / HandAtkScale, HeldEffectWeight) + Bonus(code, CardPlace.Hand, side) + (side == 0 ? SpellInHandBonus(code) : 0);
            }
            // Graveyard: setups (cards that work from there: Vayu, Plaguespreader, Treeborn, Necro Gardna).
            foreach (int code in s.GraveCodes)
                value += Effect(_values.Get(code, side).Grave, GraveEffectWeight) + Bonus(code, CardPlace.Grave, side);
            value += TributeSetup(s, side);
            if (side == 0 && _setup != null)
            {
                try { value += _setup(s); }
                catch { /* a deck goal that fails counts nothing */ }
            }

            if (s.Deck < LowDeckFrom)
                value -= (LowDeckFrom - s.Deck) * LowDeckCostPerCard;
            return value;
        }

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
