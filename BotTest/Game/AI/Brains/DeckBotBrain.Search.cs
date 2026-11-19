using BotTest.Game.AI.Search;
using DuelSim.Data;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The MCTS at our decisions (user, 2026-11-11): the brain decides; after it picks its main-phase or battle action, the
    /// MCTS (Search/Mcts.cs) searches the decision, continuing whatever tree dead time and earlier decisions grew for this
    /// board, and replaces the brain's pick only when its own move is clearly better ("Brain, MCTS only adds combos"). When
    /// it does, the picks along its line are followed too, so a combo comes out as found; otherwise every pick is the
    /// brain's. Off when the budget is 0 or the engine isn't available.
    /// </summary>
    public abstract partial class DeckBotBrain : IDecisionRefiner
    {
        /// <summary>Default thinking time per decision (ms): always on, 2000 (user, 2026-11-10: up to two seconds per action) unless DUELBOT_SEARCH_MS says otherwise (0 = off).</summary>
        public static int DefaultSearchBudgetMs { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("DUELBOT_SEARCH_MS"), out int ms) ? Math.Max(0, ms) : 2000;

        /// <summary>Search threads per decision, from DUELBOT_SEARCH_THREADS (default: all cores but two).</summary>
        public static int DefaultSearchThreads { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("DUELBOT_SEARCH_THREADS"), out int t) ? Math.Max(1, t) : Math.Max(1, Environment.ProcessorCount - 2);

        /// <summary>Turns each line plays (2 = rest of ours + their whole reply, user 2026-11-11), from DUELBOT_SEARCH_HORIZON.</summary>
        public static int DefaultSearchHorizon { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("DUELBOT_SEARCH_HORIZON"), out int h) ? Math.Clamp(h, 1, 5) : 2;

        /// <summary>The first decision of each of our turns gets this much extra (ms) by default; DuelSim's --dead-time sets it to stand in for their-turn thinking.</summary>
        // Extra on top of the usual per-decision budget (2 s), for 4 s in total on the turn's first decision (user, 2026-11-11:
        // "4 seconds in total, not on top of the 2 seconds it already uses").
        public static int DefaultTurnStartMs { get; set; } = 2000;

        /// <summary>This brain's thinking time per decision (ms); 0 = no search.</summary>
        public int SearchBudgetMs { get; set; } = DefaultSearchBudgetMs;

        /// <summary>How much better (position score, 0-1) another action must look before it replaces the brain's choice.</summary>
        [Tunable("How much better (position score, 0-1) another action must look before it replaces the brain's choice.", Section = "Search")]
        public double SearchOverrideMargin { get; set; } = 0.02;

        /// <summary>
        /// Standard errors of the search's best move the gap must clear on top of the margin (was 2). Lowered (user, 2026-11-13:
        /// "maybe we should lower the limit for the planner to overwrite? seems like the planner is doing relatively well while the
        /// brain keeps throwing up dumb things"): at 2 a 5-point preference needed 13-17 points at 2 s of lines and never got through.
        /// </summary>
        [Tunable("Standard errors of the search's best move the gap must clear on top of the margin (was 2). Lowered (user, 2026-11-13: \"maybe we should lower the limit for the planner to overwrite? seems like the planner is doing relatively well while the brain keeps throwing up dumb things\"): at 2 a 5-point preference needed 13-17 points at 2 s of lines and never got through.", Section = "Search")]
        public double OverrideNoiseFactor { get; set; } = 1.0;

        /// <summary>
        /// Deck-specific setups for the search's position score, on our side of where a line ends (added to the generic
        /// terms: card effects, graveyard cards that work from there, tribute fodder for a hand monster). In card units
        /// (a card in hand is about 0.5-1). Runs on search threads: read only the summary (and SearchHost card data).
        /// </summary>
        internal virtual double SearchSetupValue(DuelSim.Engine.SideSummary own) => 0;

        /// <summary>The deck's value for next turn at the end of a line, given the cards we activated this turn (Black Whirlwind's kept
        /// Normal Summon). Card units; 0 by default.</summary>
        internal virtual double SearchNextTurnValue(DuelSim.Engine.SideSummary own, IReadOnlyCollection<int> activatedThisTurn) => 0;

        /// <summary>The search plays Master Duel rules (Master Rule 5 field): Speed and Rush brains turn it off.</summary>
        protected virtual bool PlaySearchSupported => true;

        // Rules that fired while the brain made the current main-phase / battle decision (see Rule), in order: the last is
        // usually the one that picked the action. Reported with the search verdict, to show which rules the search overrides.
        private readonly List<string> _decisionRules = new();

        public void BeginDecision()
        {
            _decisionRules.Clear();
            _holds.Clear();
            _plan = null; // a plan covers the picks after one main/battle decision, up to the next
            _planAdoptedThisDecision = false;
            _materialsPicked.Clear();
        }

        // ------------------------------------------------------------------ holds: the brain's rules as search penalties

        /// <summary>
        /// A move the brain considered at this decision and held back by a rule: what a search line pays (card units) for
        /// making it anyway. Prefix: the option-key prefix it applies to ("{action}:" for one main action, "attack:" for the
        /// attack, null for any main action with the card).
        /// </summary>
        internal readonly record struct HeldMove(int CardId, string? Prefix, string Rule, double Cost);

        private readonly List<HeldMove> _holds = new();

        /// <summary>The moves held back at the current decision (see Hold), for the search.</summary>
        internal IReadOnlyList<HeldMove> Holds => _holds;

        /// <summary>
        /// What a search line pays for going against a hold of this rule. The brain's rules are strong suggestions, not bans
        /// (user, 2026-11-11: "use the brain's rules as penalties in the searches"): a line that breaks one must gain more than
        /// this to win. Per rule: the tier declared on its constant ([HoldCost(HoldCosts.Strong)]), else Standard (1.5), or a
        /// value tuning set on this brain (Rules.SetCost).
        /// </summary>
        protected virtual double HoldCost(string rule) =>
            // Sets held for Main Phase 2 (spell/trap and monster) cost their own tier: the only charge for a Main Phase 1 set (user, 2026-11-18:
            // "give it a cost. give it 0.5"; "we should only have one": LineTally's set-then-battle charge is gone).
            Rules.Cost(rule);

        // Rules whose summon holds the search now checks on the actual line (LineTally, user 2026-11-13): a monster summoned
        // that never attacks, uses an effect or leaves the field; Lyla's effect, a union equip, or a set in Main Phase 1 before
        // the Battle Phase; Miracle Fusion with no Battle Phase after it. Their holds cost nothing in the search, so nothing is
        // charged twice; the brain's own play still follows them.
        private static readonly HashSet<string> CheckedOnTheLine = new()
        {
            DeckRules.LylaAfterBattle, Bots.MasterDuel.Machina.MachinaRules.UnionEquipAfterBattle,
            Bots.MasterDuel.HeroFrog.HeroFrogRules.FusionWaitsForBattle, Bots.MasterDuel.Blackwing.BlackwingRules.BodyNeedsPurpose,
        };
        // Rules that hold both summons and sets back: only their summons are checked on the line.
        private static readonly HashSet<string> SummonsCheckedOnTheLine = new()
        {
            DeckRules.NoBlockers, Bots.MasterDuel.Blackwing.BlackwingRules.NoPointlessSummon,
        };

        private double HoldCost(string rule, MainAction? action) =>
            CheckedOnTheLine.Contains(rule) || action is MainAction.Summon or MainAction.SpSummon && SummonsCheckedOnTheLine.Contains(rule)
                ? 0 : HoldCost(rule);

        /// <summary>
        /// Records that the brain considered this move and held it back by this rule. Called where a rule rejects a move, so
        /// the search charges lines that make it anyway (at this decision in the real game, and at every decision in its lines,
        /// where the brain twin records the same holds).
        /// </summary>
        protected void Hold(int cardId, MainAction? action, string rule)
        {
            if (cardId <= 0)
                return;
            // A hold that came from a former roll pays its cost in proportion to that chance.
            double scale = _rollChance is double c ? Math.Clamp(c, 0, 1) : 1;
            _rollChance = null;
            _holds.Add(new HeldMove(cardId, action is { } a ? $"{(int)a}:" : null, rule, HoldCost(rule, action) * scale));
            Interlocked.Increment(ref HoldsRecorded);
            HoldsByRule.AddOrUpdate(rule, 1, (_, n) => n + 1);
        }

        /// <summary>A card the brain wouldn't pick at this card pick (a search, a target), held back by this rule: lines that pick it pay the rule's cost.</summary>
        protected void HoldPick(int cardId, string rule)
        {
            if (cardId <= 0)
                return;
            _holds.Add(new HeldMove(cardId, "card:", rule, HoldCost(rule)));
            Interlocked.Increment(ref HoldsRecorded);
            HoldsByRule.AddOrUpdate(rule, 1, (_, n) => n + 1);
        }

        /// <summary>
        /// A hold whose cost is the rule's cost times a chance (0-1): what used to be a dice roll ("hold it 60% of the time") is now a
        /// cost every line pays in proportion (user, 2026-11-17: "get rid of all rolls, we're already including the probabilistic nature
        /// in our unknown card predictions"). The brain itself holds the move when the chance is at least an even one.
        /// </summary>
        protected void HoldScaled(int cardId, MainAction? action, string rule, double chance)
        {
            if (cardId <= 0 || chance <= 0)
                return;
            _holds.Add(new HeldMove(cardId, action is { } a ? $"{(int)a}:" : null, rule, HoldCost(rule, action) * Math.Clamp(chance, 0, 1)));
            Interlocked.Increment(ref HoldsRecorded);
            HoldsByRule.AddOrUpdate(rule, 1, (_, n) => n + 1);
        }

        /// <summary>
        /// What every former dice roll now "rolls": just under HoldChanceForBrain, so "roll &lt; chance" holds exactly when the chance is at
        /// least an even one (user, 2026-11-17: no rolls).
        /// </summary>
        protected static double RollAt => HoldChanceForBrain - 1e-9;

        // The chance a former roll gave the hold the brain is about to record (Hold scales its cost by it, then clears it).
        private double? _rollChance;

        /// <summary>What used to be a roll: the brain's own choice is to hold when the chance is at least this.</summary>
        [Tunable("A former dice roll: the brain holds the move itself when the chance is at least this (the search pays the cost in proportion either way).")]
        public static double HoldChanceForBrain = 0.5;

        /// <summary>A hold on answers whose key starts with this prefix ("chain:" for a chain window's answers).</summary>
        protected void HoldKey(int cardId, string prefix, string rule)
        {
            if (cardId <= 0)
                return;
            _holds.Add(new HeldMove(cardId, prefix, rule, HoldCost(rule)));
            Interlocked.Increment(ref HoldsRecorded);
            HoldsByRule.AddOrUpdate(rule, 1, (_, n) => n + 1);
        }

        /// <summary>Holds recorded by any brain (the real one and the search twins), for the bench.</summary>
        public static long HoldsRecorded;
        /// <summary>Holds recorded, by rule (for the bench).</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> HoldsByRule = new();

        /// <summary>
        /// Whatever the brain chose, sets wait for Main Phase 2 when a battle worth entering is ahead (deck.sets-after-battle):
        /// recorded for every deck after its decision, so a line that sets now pays even where the brain's flow picked its play
        /// before it ever considered sets (seen 2026-11-11: Icarus Attack set before Goyo's synchro, Raigeki Break before
        /// Indomitable, both unpenalised). Not when the brain set something itself, or its flow already recorded the hold.
        /// </summary>
        /// <summary>Torrential chance (the brain's backrow read) from which the test-for-Torrential rule applies.</summary>
        [Tunable("Whatever the brain chose, sets wait for Main Phase 2 when a battle worth entering is ahead (deck.sets-after-battle): recorded for every deck after its decision, so a line that sets now pays even where the brain's flow picked its play before it ever considered sets (seen 2026-11-11: Icarus Attack set before Goyo's synchro, Raigeki Break before Indomitable, both unpenalised). Not when the brain s...")]
        public double TorrentialTestChance { get; set; } = 0.1;

        /// <summary>
        /// Test for Torrential (DeckRules.TestForTorrential): with Torrential suspected and more than one monster we could summon now,
        /// the least important one that can attack this turn (into their weakest monster, or directly) goes first; the more important
        /// ones are held, so a line that opens with one of them pays. Importance: the card's worth on the field (EffectEvaluator).
        /// </summary>
        private async Task HoldForTorrentialTestAsync(DecisionContext<IdleCmdData> context)
        {
            // Pressing (the risk tolerance above its base) relaxes it: the threshold rises up to 3x (user, 2026-11-16).
            await RiskToleranceAsync();
            if (TorrentialChance < TorrentialTestChance * (1 + 2 * RiskPressNow))
                return;
            var data = context.StateData;
            var candidates = new List<(DynamicCard Card, MainAction Action)>();
            foreach (var card in data.SummonableCards ?? Array.Empty<DynamicCard>())
                candidates.Add((card, MainAction.Summon));
            foreach (var card in data.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                candidates.Add((card, MainAction.SpSummon));
            candidates = candidates.Where(c => c.Card.ID > 0).GroupBy(c => c.Card.ID).Select(g => g.First()).ToList();
            if (candidates.Count < 2)
                return;
            // Can it attack this turn: a Battle Phase still ahead, and their field empty or a monster of theirs it beats.
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && data.CanBattlePhase;
            var theirs = (await GetEnemyMonsterCardsAsync()).Where(c => c.ID > 0 || c.CurrentCoordinates.Location != 0).ToList();
            int weakest = theirs.Count == 0 ? -1
                : theirs.Select(c => !c.IsFaceup() ? int.MaxValue : c.IsAttack() ? c.Attack : c.Defense).Min();
            bool Pressures(DynamicCard card) => battleAhead && card.Attack > 0 && (weakest < 0 || card.Attack > weakest);
            var grave = (await CurrentField.GetBotField()).OwnSide.Grave ?? Array.Empty<int>();
            var worth = new Dictionary<int, double>();
            foreach (var (card, _) in candidates)
                worth[card.ID] = (await EffectEvaluator.ScoreOnFieldAsync(card.ID, grave)).Item1;
            var tester = candidates.Where(c => Pressures(c.Card)).OrderBy(c => worth[c.Card.ID]).FirstOrDefault();
            if (tester.Card.ID <= 0)
                return;
            foreach (var (card, action) in candidates)
                if (worth[card.ID] > worth[tester.Card.ID] + TorrentialTestMargin)
                    Hold(card.ID, action, DeckRules.TestForTorrential);
        }

        /// <summary>A deck's own holds at a main-phase decision (before the search reads them). Default: none.</summary>
        protected virtual Task DeckHoldsAsync(DecisionContext<IdleCmdData> context) => Task.CompletedTask;

        /// <summary>
        /// Monsters whose effect works from the hand (Battle Fader, Honest, Kalut) aren't set: set, they lose it (user, 2026-11-18: "NEVER set
        /// battle fader - its effect in hand stops more attacks than it would while set").
        /// </summary>
        /// <summary>
        /// Trap Dustshoot is set whenever they hold 3 or more cards (user, 2026-11-18: "it should always do this if my hand has 3 or more cards
        /// (all decks should), and then use it as soon as I draw"; the chain rule fires at 4+). Ending the turn with it still in hand is held,
        /// so a line that doesn't set it pays for it (in the lines their hand is blank cards, so Dustshoot itself never shows its worth there).
        /// </summary>
        private async Task HoldTrapDustshootUnsetAsync(DecisionContext<IdleCmdData> context)
        {
            if (!(context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(TrapDustshootId)) || !context.StateData.CanEndPhase)
                return;
            int theirHand = ((await CurrentField.GetBotField()).EnemySide.Hand ?? Array.Empty<int>()).Length;
            if (theirHand >= DustshootSetAtHand && Rule(DeckRules.SetTrapDustshoot))
                HoldPhaseMove("end", DeckRules.SetTrapDustshoot);
        }

        [Tunable("Trap Dustshoot is set (ending the turn without it is held) when their hand has this many cards or more.")]
        public static int DustshootSetAtHand = 3;

        private void HoldHandTrapSets(DecisionContext<IdleCmdData> context)
        {
            foreach (var card in context.StateData.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                if ((card.IsCode(BattleFaderId) || card.IsCode(HonestId) || card.IsCode(KalutId)) && Rule(DeckRules.NoHandTrapSet))
                    Hold(card.ID, MainAction.SetMonster, DeckRules.NoHandTrapSet);
        }

        private const int BurialFromADifferentDimensionId = 48976825;

        /// <summary>Setting Burial from a Different Dimension with none of our monsters banished: held (see DeckRules.NoEmptyBurialSet).</summary>
        private async Task HoldEmptyBurialSetAsync(DecisionContext<IdleCmdData> context)
        {
            if (!(context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(BurialFromADifferentDimensionId)))
                return;
            foreach (int id in (await CurrentField.GetBotField()).OwnSide.Banish ?? Array.Empty<int>())
                if (id > 0 && await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & 0x1) != 0)
                    return; // a banished monster of ours: Burial can be bait or a play
            Hold(BurialFromADifferentDimensionId, MainAction.SetSpell, DeckRules.NoEmptyBurialSet);
        }

        private async Task HoldSetsBeforeBattleAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse brainChoice)
        {
            try
            {
                if (brainChoice.Action is MainAction.SetSpell or MainAction.SetMonster)
                    return;
                var data = context.StateData;
                var spells = data.SpellSetableCards ?? Array.Empty<DynamicCard>();
                var monsters = data.MonsterSetableCards ?? Array.Empty<DynamicCard>();
                if (spells.Length + monsters.Length == 0 || _holds.Any(h => h.Rule is DeckRules.HoldSetsForBattle or DeckRules.HoldMonsterSetsForBattle))
                    return;
                if (!await SetsWaitForMainPhase2Async(context))
                    return;
                foreach (var card in spells)
                    Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForBattle);
                foreach (var card in monsters)
                    Hold(card.ID, MainAction.SetMonster, DeckRules.HoldMonsterSetsForBattle);
            }
            catch
            {
                // A hold is only a penalty for the search; it must never cost the bot its answer.
            }
        }

        /// <summary>Records that the brain held back attacking with this monster (the first attacker in zone order).</summary>
        protected void HoldAttack(int cardId, string rule)
        {
            if (cardId > 0)
                _holds.Add(new HeldMove(cardId, "attack:", rule, HoldCost(rule)));
        }

        /// <summary>Records that the brain holds back leaving the Battle Phase (Main Phase 2 / end the turn): it has an attack to make.</summary>
        protected void HoldBattleExit(string rule)
        {
            HoldPhaseMove("main2", rule);
            HoldPhaseMove("end", rule);
        }

        /// <summary>Records that the brain holds back a card-less move ("battle", "main2", "end") by this rule.</summary>
        protected void HoldPhaseMove(string key, string rule)
        {
            if (!_holds.Any(h => h.CardId == 0 && h.Prefix == "=" + key))
                _holds.Add(new HeldMove(0, "=" + key, rule, HoldCost(rule)));
        }

        /// <summary>A search option key in words ("set Icarus Attack"), for the log.</summary>
        private static string DescribeKey(string key)
        {
            string verb = key.Split(':')[0] switch
            {
                "attack" => "attack with",
                var n when int.TryParse(n, out int a) => (MainAction)a switch
                {
                    MainAction.Summon => "summon", MainAction.SpSummon => "special summon", MainAction.Repos => "change position of",
                    MainAction.SetMonster or MainAction.SetSpell => "set", MainAction.Activate => "activate", _ => n
                },
                var other => other
            };
            return $"{verb} {OmegaBotWrapper.Logging.CardNames.Get(SearchActions.KeyCardId(key))}";
        }

        /// <summary>The held moves among these option keys: key -> (cost, rule). Empty when none.</summary>
        internal Dictionary<string, (double Cost, string Rule)> HoldsFor(IEnumerable<string> keys)
        {
            var result = new Dictionary<string, (double, string)>();
            if (_holds.Count == 0)
                return result;
            static int Canon(int id) => SearchHost.CanonicalId(id) is > 0 and var official ? official : id;
            foreach (string key in keys)
            {
                // Card-less holds (leaving the Battle Phase) match their key exactly.
                var exact = _holds.FirstOrDefault(h => h.CardId == 0 && h.Prefix == "=" + key);
                if (exact.Rule != null)
                {
                    result[key] = (exact.Cost, exact.Rule);
                    continue;
                }
                int id = SearchActions.KeyCardId(key);
                if (id <= 0)
                    continue;
                int canon = Canon(id);
                foreach (var hold in _holds)
                {
                    bool prefixOk = hold.Prefix == null ? !key.StartsWith("attack:") && !key.StartsWith("bact:") : key.StartsWith(hold.Prefix);
                    if (prefixOk && Canon(hold.CardId) == canon)
                    {
                        result[key] = (hold.Cost, hold.Rule);
                        break;
                    }
                }
            }
            return result;
        }

        // The MCTS's picks for the move it chose over the brain's (see FollowPlan), and how far along we are.
        private (int Turn, List<PlanStep> Steps, int Next)? _plan;

        /// <summary>
        /// The picks after a move: when the MCTS replaced the brain's move, the picks along its most-visited line (a combo
        /// needs its exact search, cost and target); otherwise the brain's, and the MCTS's line is only logged (user,
        /// 2026-11-10, after sampled picks kept beating the brain's: "this keeps happening, please stop it happening").
        /// </summary>
        private void FollowPlan(DuelBotWrapper bot, SearchOutcome outcome, string chosenKey, int turn, bool overriding)
        {
            _plan = null;
            if (outcome.Plans?.GetValueOrDefault(chosenKey) is not { Count: > 0 } steps)
                return;
            if (overriding)
            {
                _plan = (turn, steps, 0);
                bot.LogNote($"search: following its line: {string.Join(" -> ", steps.Select(s => s.Label))}");
            }
        }

        /// <summary>
        /// The search replaced the brain's move. Its later main / battle steps are not committed to: every decision searches
        /// again, continuing the same tree (every line played so far still counts), so no step is taken on a few lines
        /// (user, 2026-11-13: "every step of the line keep producing a full search of the future steps ... so that we never go
        /// through lower-confidence sections"). Routines, the brain's own sequences, are still followed whole.
        /// </summary>
        private void CommitLineIfOverriding(DuelBotWrapper bot, SearchOutcome outcome, string chosenKey, int turn, bool overriding)
        {
            if (IsSearchTwin)
                return;
            // The line behind the move played (ours or the brain's): our chains in our turn are checked against it.
            _expectedLine = (turn, outcome.RootLines?.GetValueOrDefault(chosenKey) ?? new List<PlanStep>());
            // The move played and the lines behind it: a later decision this turn's tree can't find is a desync only when the
            // tree had explored past this move (DESYNC), not when it barely tried it.
            _lastPlayed = (turn, outcome.Stat(chosenKey)?.Label ?? DescribeKey(chosenKey), outcome.Stat(chosenKey)?.Visits ?? 0);
            if (overriding)
                bot.LogNote("search: the next decision searches again from there (continuing this tree)");
        }

        // The line behind the last move played this turn (its steps, our chains included), for spotting unplanned chains.
        private (int Turn, List<PlanStep> Steps)? _expectedLine;

        // Where our last searched decision sat in its tree: the way from there to the next decision shows their choices.
        private (int Turn, MctsTree Tree, MctsNode Start)? _lastSearchStart;
        private bool _keyMismatchLogged;

        /// <summary>
        /// On the way from our last searched decision this turn to this one (by its decision key), an outcome the plan couldn't
        /// know: their pick among more than one genuinely different card (Fortress destroying one of ours, a revive with
        /// different targets, one from their unknown hand), or chance (a coin, a die, a random selection). Then the memory is
        /// wiped (true). A pick with a single distinct answer was played out by the lines: the plan continues.
        /// </summary>
        private bool OpenChoiceSinceLastSearch(DuelBotWrapper bot, string key)
        {
            if (IsSearchTwin || _wipeTrees || _lastSearchStart is not { } prev || prev.Turn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return false;
            var (reached, open) = prev.Tree.TheirChoicesOnTheWay(prev.Start, key);
            if (!reached || open <= 1)
                return false;
            WipeTurnRecord(bot, $"an outcome the plan couldn't know since the last decision (their pick among {open} different cards, or chance)");
            return true;
        }

        /// <summary>The same check before a routine's next step (no search runs there): a wipe ends the routine first.</summary>
        internal async Task CheckOpenChoiceBeforeStepAsync(DuelBotWrapper bot, BotState state, IEnumerable<string> offered)
        {
            if (IsSearchTwin || _wipeTrees || _lastSearchStart is not { } prev || prev.Turn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return;
            try
            {
                var view = await BuildSearchViewAsync(bot);
                OpenChoiceSinceLastSearch(bot, DecisionKey.Of(state, view, offered));
            }
            catch (Exception ex)
            {
                bot.LogNote($"search: couldn't check the way since the last decision ({ex.Message})");
            }
        }

        // The last searched move played (its label and the lines behind it), for telling a desync from a barely-explored move.
        private (int Turn, string Label, int Lines)? _lastPlayed;
        // The previous decision's, kept while this decision searches (the desync check reads it).
        private (int Turn, string Label, int Lines)? _prevPlayed;

        // The next planned step when it answers this kind of prompt (this turn), else null.
        private PlanStep? NextStep(BotState state) =>
            _plan is { } plan && plan.Turn == _currentHeader.CurrentTurn && plan.Next < plan.Steps.Count && SamePrompt(plan.Steps[plan.Next].State, state)
                ? plan.Steps[plan.Next] : null;

        // A tribute is a SelectTribute prompt in the simulator but often a SelectCard with the release hint on Omega (seen
        // 2026-11-11: the line's "pick Vayu" for Icarus Attack never matched, and the default tributed Armor Master).
        private static bool SamePrompt(BotState planned, BotState live) =>
            planned == live || planned is BotState.SelectCard or BotState.SelectTribute && live is BotState.SelectCard or BotState.SelectTribute;

        private void StepDone(PlanStep step, bool followed)
        {
            if (_plan is not { } plan)
                return;
            if (followed)
            {
                _plan = plan with { Next = plan.Next + 1 };
                _trackedBot?.LogNote($"search: next step of its line: {step.Label}");
            }
            else
            {
                _plan = null;
                _trackedBot?.LogNote($"search: its line stops ({step.Label} isn't on offer); the brain picks");
            }
        }

        public IList<byte>? PlannedCards(BotState state, DynamicCard[] cards, int minimum, int maximum, bool cancelable)
        {
            // The plan's own pick at its node (a search's card, a target, a cost): the plan decides, not the brain. Never its "cancel".
            var offeredPicks = SearchActions.CardPicks(cards, minimum, maximum, cancelable);
            if (PlanAnswer(state, offeredPicks.Select(p => p.Key).ToList(), "pick") is string planPick && planPick != "cancel")
            {
                var chosen = offeredPicks.First(p => p.Key == planPick);
                _trackedBot?.LogNote($"search: {chosen.Label} (the plan)");
                return chosen.Indexes.Select(i => (byte)i).ToList();
            }
            if (NextStep(state) is not { } step)
                return null;
            // A planned "cancel" is never followed: cancelling an attack target or a cost the brain chose to pay throws
            // the play away (seen 2026-11-10: "plan: cancel" sent nothing and the validator attacked Gorz instead).
            if (step.Key == "cancel")
            {
                StepDone(step, false);
                return null;
            }
            // The brain chose this target itself when it decided the effect (PlanTarget: Sirocco's boost on Bora, a removal
            // target): its pick stands (seen 2026-11-10). The plan goes on.
            if (_plannedTarget is { } own && own.Turn == _currentHeader.CurrentTurn &&
                cards.Any(card => card.CurrentCoordinates.Location == own.Location && card.CurrentCoordinates.Sequence == own.Sequence &&
                                  card.CurrentCoordinates.Controller == own.Controller))
            {
                _plan = _plan is { } p ? p with { Next = p.Next + 1 } : null;
                _trackedBot?.LogNote($"search: its line's step {step.Label} skipped, the brain's own target stands");
                return null;
            }
            // A tribute the brain already planned stands.
            if (_tributePlan is { } tributes && tributes.Turn == _currentHeader.CurrentTurn)
            {
                _plan = _plan is { } skip ? skip with { Next = skip.Next + 1 } : null;
                _trackedBot?.LogNote($"search: its line's step {step.Label} left to the brain (its own tribute plan)");
                return null;
            }
            var picks = SearchActions.CardPicks(cards, minimum, maximum, cancelable);
            // The same pick with a card in another zone counts (a line from dead time; see SearchActions.MatchKey).
            var liveKey = SearchActions.MatchKey(step.Key, picks.Select(p => p.Key).ToList());
            var pick = liveKey == null ? null : picks.First(p => p.Key == liveKey);
            StepDone(step, pick != null);
            return pick?.Indexes.Select(i => (byte)i).ToList();
        }

        public bool? PlannedEffectYn(SelectEffectYnData data)
        {
            var planned = PlannedYesNo(BotState.SelectEffectYn);
            // A trigger the deck always uses beats a planned "no" (seen 2026-11-10: Black Whirlwind declined, the search lost).
            if (planned == false && AlwaysTriggers(data.EffectCard))
            {
                _trackedBot?.LogNote($"planned 'no' ignored: {CardNames.Get(data.EffectCard.ID)} always triggers");
                return true;
            }
            return planned;
        }

        /// <summary>The last main-phase move sent: turn, action, and the card's id (0 if none).</summary>
        protected (int Turn, MainAction Action, int CardId) LastMainMove { get; set; }

        /// <summary>The turn number of the decision being made.</summary>
        protected int CurrentTurnNumber => _currentHeader.CurrentTurn;

        public bool AlwaysUses(SelectEffectYnData data) => data.EffectCard is { } card && AlwaysTriggers(card);

        /// <summary>Optional effects the deck always uses when offered (no plan may decline them). Default: none.</summary>
        protected virtual bool AlwaysTriggers(DynamicCard card) => false;

        public bool? PlannedYesNo(BotState state)
        {
            if (PlanAnswer(state, new[] { "yes", "no" }, "yes/no") is string planYn)
                return planYn == "yes";
            if (NextStep(state) is not { } step)
                return null;
            StepDone(step, step.Key is "yes" or "no");
            return step.Key switch { "yes" => true, "no" => false, _ => null };
        }

        /// <summary>The plan's choice at a keyed prompt (a position, plan step 5), or null when no plan covers it.</summary>
        public string? PlannedChoice(BotState state, IReadOnlyList<string> options)
        {
            if (PlanAnswer(state, options, state == BotState.SelectPosition ? "position" : state.ToString()) is string planned)
                return planned;
            if (NextStep(state) is not { } step)
                return null;
            bool offered = options.Contains(step.Key);
            StepDone(step, offered);
            return offered ? step.Key : null;
        }

        public int? PlannedOption(int[] options)
        {
            if (PlanAnswer(BotState.SelectOption, options.Select(o => $"option:{o}").ToList(), "option") is string planOption)
                return Array.FindIndex(options, o => $"option:{o}" == planOption) is var planIndex and >= 0 ? planIndex : null;
            if (NextStep(BotState.SelectOption) is not { } step)
                return null;
            int index = Array.FindIndex(options, o => $"option:{o}" == step.Key);
            StepDone(step, index >= 0);
            return index >= 0 ? index : null;
        }

        private static bool _searchProblemLogged;
        private DeckList? _ownDeckList;
        private int _searchSeed = Environment.TickCount;
        // Whether our Normal Summon looked used at this turn's last main-phase decision (for battle decisions).
        private (int Turn, bool Used) _normalSummonSeen = (-1, false);
        // This brain's own tree for the turn when there's no dead-time search (DuelSim): decisions of the turn still share it.
        private (int Turn, MctsTree Tree)? _turnTree;

        public async Task<IdleCmdResponse> RefineIdleAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse brainChoice, CancellationToken ct = default)
        {
            if (!IsSearchTwin)
                _expectedLine = null; // only this decision's own search says what the chains after it should be
            if (!IsSearchTwin)
                { _prevPlayed = _lastPlayed; _lastPlayed = null; }
            var chosen = await RefineIdleCoreAsync(context, brainChoice, ct);
            // The move is final: commitments made for it take effect, the rest are dropped; tracking (DeckBotBrain.TurnPlan).
            await AfterMainMoveAsync(context, chosen, brainChoice);
            return chosen;
        }

        private async Task<IdleCmdResponse> RefineIdleCoreAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse brainChoice, CancellationToken ct)
        {
            // Pot of Greed at the first chance, always: a fixed rule ahead of the deck's own choice and the search (user,
            // 2026-11-12). Its two cards change the hand so much that the next decision is thought through afresh, like the
            // first decision of a turn: a new tree and the turn-start budget (4 s in total).
            if (TryGetActivable(context.StateData, Search.Mcts.PotOfGreedId, out var pot))
            {
                context.Bot.LogNote("deck brain: activate Pot of Greed: draw 2 (fixed rule: Pot of Greed at the first chance); " +
                                    "the next decision is searched afresh like a turn start");
                if (!IsSearchTwin)
                {
                    _turnStartTurn = -1;
                    _turnStartWarm = (-1, 0);
                    _turnTree = null;
                }
                return pot.MainActivateFromIndex();
            }
            try
            {
                // Round-robin 43: whichever rule chose the Battle Phase, spare attackers go to defence first against a likely
                // Mirror Force (the next decision comes back here for the next one, then the Battle Phase).
                // A monster first in zone order with nothing to attack blocks the ones behind it: to defence before the battle
                // (free), rather than a chip crash to let the next one through (seen 2026-11-13: two Armageddon Knights took 600
                // each into Dupe Frog so Fortress could follow; both could have switched to defence).
                // Ending the turn over the hand limit (more than 6: the End Phase discards down to 6): set spells/traps first, traps
                // before spells, rather than discard them for nothing; then the deck's own last resort (Machina: a Fortress
                // summon). User, 2026-11-13: "if you have more than 6 cards in hand you could always set spell and traps
                // (traps first) until you're down to 6 cards".
                if (brainChoice.Action == MainAction.ToEndPhase && !IsSearchTwin &&
                    ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2 ||
                     (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && !context.StateData.CanBattlePhase) &&
                    await HandLimitPlayAsync(context) is IdleCmdResponse handLimit)
                    brainChoice = handLimit;
                var opponentsNow = await GetEnemyMonsterCardsAsync();
                if (brainChoice.Action == MainAction.ToBattlePhase)
                    await RefreshRewardOnDestroyAsync(opponentsNow); // our monsters and their effect flags, as BlocksAttackOrder reads them
                var unblock = brainChoice.Action == MainAction.ToBattlePhase && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1
                    ? (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>()).Where(card => card.IsFaceup() && card.IsAttack())
                        .Select(card => (Card: card, Why: BlocksAttackOrder(card, opponentsNow))).FirstOrDefault(x => x.Why != null)
                    : default;
                if (brainChoice.Action == MainAction.ToBattlePhase && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 &&
                    await GorzAttackOrderAsync(context.StateData, opponentsNow) is { } gorzOrder && Rule(DeckRules.GorzAttackOrder))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(gorzOrder.Card.ID)} to defence before the Battle Phase: {gorzOrder.Why}");
                    brainChoice = gorzOrder.Card.Reposition();
                }
                else if (brainChoice.Action == MainAction.ToBattlePhase && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 &&
                         await BattleRemovalOrderAsync(context.StateData, opponentsNow) is { } removalOrder && Rule(DeckRules.AvoidBattleRemoval))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(removalOrder.Card.ID)} to defence before the Battle Phase: {removalOrder.Why}");
                    brainChoice = removalOrder.Card.Reposition();
                }
                else if (unblock.Why != null && Rule(DeckRules.UnblockAttackOrder))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(unblock.Card.ID)} to defence before the Battle Phase: {unblock.Why}");
                    brainChoice = unblock.Card.Reposition();
                }
                else if (brainChoice.Action == MainAction.ToBattlePhase && await ShelterBeforeBattleAsync(context) is IdleCmdResponse shelter)
                    brainChoice = shelter;
                // Round-robin 9: our set MST at a fresh set card before a key monster attacks.
                else if (brainChoice.Action == MainAction.ToBattlePhase && await MstBeforeKeyAttackAsync(context) is IdleCmdResponse mstFirst)
                    brainChoice = mstFirst;
                // Round-robin 11: a non-expendable summon into a likely Ryko waits for next turn (a medium-weight roll): the brain
                // is asked again without it.
                if (brainChoice.Action is MainAction.Summon or MainAction.SpSummon)
                {
                    var list = (brainChoice.Action == MainAction.Summon ? context.StateData.SummonableCards : context.StateData.SpecialSummonableCards) ?? Array.Empty<DynamicCard>();
                    var summon = list.FirstOrDefault(c => c.ActionIndex != null && c.ActionIndex.Length > (int)brainChoice.Action &&
                                                          c.ActionIndex[(int)brainChoice.Action] == brainChoice.Index);
                    if (await RykoBeforeSummonAsync(context, summon) is string rykoHold && Rule(DeckRules.RykoHoldsBigSummon))
                    {
                        context.Bot.LogNote($"deck brain: {rykoHold}: the brain picks again without it");
                        Hold(summon.ID, brainChoice.Action, DeckRules.RykoHoldsBigSummon);
                        if (_planAdoptedThisDecision)
                            DropPlan(context.Bot, $"its first move ({CardNames.Get(summon.ID)}) waits for next turn (likely Ryko)");
                        var without = context.StateData;
                        without.SummonableCards = (without.SummonableCards ?? Array.Empty<DynamicCard>()).Where(c => !c.IsCode(summon.ID)).ToArray();
                        without.SpecialSummonableCards = (without.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Where(c => !c.IsCode(summon.ID)).ToArray();
                        context = context with { StateData = without };
                        brainChoice = await OnIdleCmdAsync(context, ct);
                    }
                }
                await HoldSetsBeforeBattleAsync(context, brainChoice);
                await HoldEmptyBurialSetAsync(context);
                await HoldForTorrentialTestAsync(context);
                HoldHandTrapSets(context);
                await HoldTrapDustshootUnsetAsync(context);
                await DeckHoldsAsync(context);
                return await SearchIdleAsync(context, brainChoice, ct);
            }
            catch (Exception ex)
            {
                // The search must never cost the bot its answer.
                context.Bot.LogNote($"search failed, keeping the brain's choice: {ex.Message}");
                return brainChoice;
            }
        }

        private async Task<IdleCmdResponse> SearchIdleAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse brainChoice, CancellationToken ct)
        {
            var data = context.StateData;
            bool summonUsed = (data.SummonableCards?.Length ?? 0) == 0 && (data.MonsterSetableCards?.Length ?? 0) == 0;
            _normalSummonSeen = (context.Header.CurrentTurn, summonUsed);
            // The line the search committed to when it replaced the brain's move: its next step, while it's on offer.
            var idleChoices = SearchActions.Idle(data);
            if (!IsSearchTwin && NextSearchLineKey(context.Bot, BotState.SelectIdleCmd, idleChoices.Select(c => c.Key).ToList()) is string lineKey)
                return idleChoices.First(c => c.Key == lineKey).Response;
            if (!SearchWanted(context.Bot))
                return brainChoice;
            // Options the deck rules out entirely (e.g. Blackwing's Blizzard without a revive target) are left out of what the search may pick.
            var all = SearchActions.Idle(data);
            var choices = SearchActions.Idle(WithoutRuledOut(data));
            var brainKey = choices.FirstOrDefault(c => c.Response.Action == brainChoice.Action && c.Response.Index == brainChoice.Index)?.Key;
            if (choices.Count < 2 || brainKey == null)
                return brainChoice;
            var keys = choices.Select(c => c.Key).ToHashSet();
            int turn = context.Header.CurrentTurn;
            var phase = (DuelPhase)context.Header.CurrentPhase;
            var outcome = await SearchUntilSureAsync(context.Bot, BotState.SelectIdleCmd, brainKey, all.Select(c => c.Key).ToList(), keys, summonUsed,
                battleDone: phase == DuelPhase.Main2, inBattle: false, ct);
            if (outcome == null)
                return brainChoice;
            var better = Better(context.Bot, outcome, brainKey, keys, "main", turn);
            FollowPlan(context.Bot, outcome, better?.Key ?? brainKey, turn, overriding: better != null);
            CommitLineIfOverriding(context.Bot, outcome, better?.Key ?? brainKey, turn, overriding: better != null);
            return better == null ? brainChoice : choices.First(c => c.Key == better.Key).Response;
        }

        public async Task<BattleCmdResponse> RefineBattleAsync(DecisionContext<BattleCmdData> context, BattleCmdResponse brainChoice, CancellationToken ct = default)
        {
            try
            {
                // No "end the turn" from the Battle Phase: Main Phase 2 comes first, as the rules say (CSV S10; the game passes through it anyway).
                if (brainChoice.Action == BattleAction.ToEndPhase && context.StateData.CanMainPhaseTwo)
                    brainChoice = BattleAction.ToMainPhaseTwo;
                if (!IsSearchTwin)
                {
                    _expectedLine = null;
                    _prevPlayed = _lastPlayed; _lastPlayed = null;
                }
                var chosen = await SearchBattleAsync(context, brainChoice, ct);
                if (chosen.Action == BattleAction.ToEndPhase && !IsSearchTwin)
                    await RetargetDeadTimeAsync(context.Bot, context.Header, await CurrentField.GetBotField(), endingOurTurn: true);
                return chosen;
            }
            catch (Exception ex)
            {
                context.Bot.LogNote($"search failed, keeping the brain's choice: {ex.Message}");
                return brainChoice;
            }
        }

        private async Task<BattleCmdResponse> SearchBattleAsync(DecisionContext<BattleCmdData> context, BattleCmdResponse brainChoice, CancellationToken ct)
        {
            var choices = SearchActions.Battle(context.StateData).Where(c => !(c.Key == "end" && context.StateData.CanMainPhaseTwo)).ToList(); // CSV S10
            // The line the search committed to when it replaced the brain's move: its next step, while it's on offer.
            if (!IsSearchTwin && NextSearchLineKey(context.Bot, BotState.SelectBattleCmd, choices.Select(c => c.Key).ToList()) is string lineKey)
            {
                var step = choices.First(c => c.Key == lineKey);
                if (lineKey.StartsWith("attack:"))
                    ClearAim(); // its target comes from the line's own pick (set by NextSearchLineKey)
                return step.Response;
            }
            if (!SearchWanted(context.Bot))
                return brainChoice;
            // An attack is always the first attacker's (server attack order), whatever index the brain sent.
            var brainKey = brainChoice.Action == BattleAction.Attack
                ? choices.FirstOrDefault(c => c.Key.StartsWith("attack:"))?.Key
                : choices.FirstOrDefault(c => c.Response.Action == brainChoice.Action && c.Response.Index == brainChoice.Index)?.Key;
            if (choices.Count < 2 || brainKey == null)
                return brainChoice;

            bool summonUsed = _normalSummonSeen.Turn == context.Header.CurrentTurn && _normalSummonSeen.Used;
            var outcome = await SearchUntilSureAsync(context.Bot, BotState.SelectBattleCmd, brainKey, choices.Select(c => c.Key).ToList(),
                choices.Select(c => c.Key).ToHashSet(), summonUsed, battleDone: false, inBattle: true, ct);
            if (outcome == null)
                return brainChoice;
            var better = Better(context.Bot, outcome, brainKey, choices.Select(c => c.Key).ToHashSet(), "battle", context.Header.CurrentTurn);
            var chosen = better?.Key ?? brainKey;
            FollowPlan(context.Bot, outcome, chosen, context.Header.CurrentTurn, overriding: better != null);
            CommitLineIfOverriding(context.Bot, outcome, chosen, context.Header.CurrentTurn, overriding: better != null);
            // An attack's target is the aimed one (the brain's, or the MCTS's best by its statistics when it replaced the
            // attacker), not the line's pick.
            if (chosen.StartsWith("attack:") && _plan is { } attackPlan && attackPlan.Next < attackPlan.Steps.Count &&
                attackPlan.Steps[attackPlan.Next].State == BotState.SelectCard)
                _plan = attackPlan with { Next = attackPlan.Next + 1 };
            if (chosen.StartsWith("attack:") && (better != null || brainChoice.Action == BattleAction.Attack))
                AimAttack(context.Bot, outcome, chosen, overriding: better != null);
            return better == null ? brainChoice : choices.First(c => c.Key == better.Key).Response;
        }

        private int _warmedTurn = -1;
        // The warm-up time spent at the start of this turn (ours), deducted from the turn-start step's extra thinking.
        private (int Turn, long Ms) _turnStartWarm = (-1, 0);

        /// <summary>
        /// The warm-up at the first prompt of every turn, both players' (user, 2026-11-10): scores our deck's cards and the
        /// cards of the lists they're likely on, and loads their scripts into the search's engine duels, so searches spend
        /// their budget on lines. On their turn it runs in the background (our answers aren't delayed); on ours it runs
        /// right away, before our first decision.
        /// </summary>
        private void WarmUpAtTurnStart(DuelBotWrapper bot, BotStateHeader header)
        {
            if (header.CurrentTurn == _warmedTurn || SearchBudgetMs <= 0 || !PlaySearchSupported || !SearchHost.Available)
                return;
            _warmedTurn = header.CurrentTurn;
            try
            {
                var ids = SearchCardIds(bot);
                if (header.CurrentPlayer == 1)
                {
                    Mcts.WarmUpInBackground(ids, DefaultSearchThreads);
                    return;
                }
                long ms = Mcts.WarmUp(ids, DefaultSearchThreads);
                _turnStartWarm = (header.CurrentTurn, ms);
                // (warm-up time is not logged: user, 2026-11-12)
            }
            catch
            {
                // Warming is only an optimisation.
            }
        }

        /// <summary>The cards our searches can meet: our deck list, and the cards of the lists they're likely on.</summary>
        private HashSet<int> SearchCardIds(DuelBotWrapper bot)
        {
            _ownDeckList ??= string.IsNullOrEmpty(bot.DeckHash) ? null : DeckCode.Decode(bot.DeckName ?? "own", bot.DeckHash, SearchHost.Cards, SearchHost.Scripts);
            var ids = new HashSet<int>();
            if (_ownDeckList != null)
                ids.UnionWith(_ownDeckList.Main.Concat(_ownDeckList.Extra));
            lock (OpponentDeck)
                ids.UnionWith(OpponentDeck.LikelyCards());
            return ids;
        }

        /// <summary>
        /// The turn-start step (user, 2026-11-10: up to ~3 s at the start of a turn is fine if the turn plays better): the
        /// warm-up at the turn's first prompt, then the rest of this time as extra thinking for our first decision of the
        /// turn, which usually sets the turn's plan.
        /// </summary>
        public int TurnStartMs { get; set; } = DefaultTurnStartMs;

        private int _turnStartTurn = -1;

        /// <summary>Extra budget (ms) for this search: the turn-start step's remainder on our first search of the turn, else 0.</summary>
        private int TurnStartStep(DuelBotWrapper bot)
        {
            int turn = _currentHeader.CurrentTurn;
            if (TurnStartMs <= 0 || _turnStartTurn == turn)
                return 0;
            _turnStartTurn = turn;
            long warmMs = _turnStartWarm.Turn == turn ? _turnStartWarm.Ms : 0;
            int extra = (int)Math.Max(0, TurnStartMs - warmMs);
            bot.LogNote($"search: first decision of the turn, thinking {(SearchBudgetMs + extra) / 1000.0:0.#} s");
            return extra;
        }

        /// <summary>A brain playing our side of the search's imagined lines: it skips real-duel bookkeeping (deck read, summon tracking, matchups, warm-ups).</summary>
        public bool IsSearchTwin { get; private set; }

        /// <summary>
        /// A fresh brain of the same deck for playing our side of the search's lines (one per search thread): built like
        /// DeckCatalog builds it, with its own search off (no search inside the search) and the same rule probabilities and list.
        /// </summary>
        internal DeckBotBrain CreateSearchTwin()
        {
            var twin = (DeckBotBrain)Activator.CreateInstance(GetType(),
                new BotTest.Game.AI.Heuristics.WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator(), null)!;
            twin.SearchBudgetMs = 0;
            twin.IsSearchTwin = true;
            twin.TurnStartMs = 0;
            twin.Variant = Variant;
            twin.OpponentDeck.Frozen = true; // a search copy observes nothing: its deck read's chances can be cached
            foreach (var rule in RuleGates.Declared(GetType()))
            {
                twin.Rules.Set(rule, Rules.Probability(rule));
                twin.Rules.SetCost(rule, Rules.Cost(rule));
            }
            return twin;
        }

        /// <summary>Whether our side of the search's lines is played by our own brain (else the quick policy). From DUELBOT_SEARCH_BRAIN (default on).</summary>
        public static bool PlaySearchWithBrain { get; set; } = Environment.GetEnvironmentVariable("DUELBOT_SEARCH_BRAIN") != "0";

        /// <summary>Fewest lines behind the MCTS's best move before it may replace the brain's.</summary>
        [Tunable("Fewest lines behind the MCTS's best move before it may replace the brain's.", Section = "Search")]
        public int MinOverrideLines { get; set; } = 50;

        /// <summary>
        /// Fewest lines behind the brain's own pick. Lower than MinOverrideLines: the tree is biased toward the brain's pick,
        /// so a pick it then barely explores is one it found bad (seen 2026-11-11: Goyo 85 over 712 lines vs the brain's
        /// end the turn 52 over 17 lines, kept). The 2-sigma noise term still covers the small sample.
        /// </summary>
        [Tunable("Fewest lines behind the brain's own pick. Lower than MinOverrideLines: the tree is biased toward the brain's pick, so a pick it then barely explores is one it found bad (seen 2026-11-11: Goyo 85 over 712 lines vs the brain's end the turn 52 over 17 lines, kept). The 2-sigma noise term still covers the small sample.", Section = "Search")]
        public int MinBrainPickLines { get; set; } = 15;

        /// <summary>
        /// Whether the brain's choice, made by these rules, is a deck engine play the MCTS must not replace. Default: none.
        /// </summary>
        protected virtual bool SearchMustKeep(IReadOnlyList<string> decisionRules) => false;

        /// <summary>Whether the MCTS may pick this action with this card at the decision (a deck rules some out entirely). Default: yes.</summary>
        protected virtual bool SearchMayChoose(DynamicCard card, MainAction action) => PlanMayChoose(card.ID, action);

        /// <summary>
        /// Moves no line may contain, anywhere in the tree. For very specific cases only (user, 2026-11-10: "i dont like hard
        /// rules ... only when absolutely necessary"). Default: none.
        /// </summary>
        protected internal virtual bool PlanMayChoose(int code, MainAction action)
        {
            return true;
        }

        /// <summary>The cost (card units) of a line going against a strong suggestion.</summary>
        [Tunable("The cost (card units) of a line going against a strong suggestion.")]
        protected static double SuggestionCost = 1.5;

        /// <summary>
        /// The deck's strong suggestions (user, 2026-11-10: "just make them strong suggestions"): the cost (SuggestionCost)
        /// when our step at this index of an MCTS line goes against one, given the keys on offer there and their field. The
        /// line's score pays it, so such a line must be that much better. The brain's own holds (Hold / HoldAttack: sets
        /// before battle, spends it wouldn't justify, pointless summons...) are charged separately, at every decision of a
        /// line; this is for suggestions about picks and combinations that no single hold expresses. Default: none.
        /// </summary>
        internal virtual double PlanSuggestions(IReadOnlyList<PlanStep> steps, int index, IReadOnlyList<string> offered, DuelSim.Engine.SideSummary theirs) => 0;

        /// <summary>The card id in a search key ("{action}:{id}@..." or "card:{id}@..."), official id, or 0.</summary>
        protected static int KeyCardId(string key) => SearchActions.KeyCardId(key);

        private IdleCmdData WithoutRuledOut(IdleCmdData data)
        {
            DynamicCard[] Keep(DynamicCard[]? cards, MainAction action) =>
                (cards ?? Array.Empty<DynamicCard>()).Where(card => SearchMayChoose(card, action)).ToArray();
            var kept = data;
            kept.SummonableCards = Keep(data.SummonableCards, MainAction.Summon);
            kept.SpecialSummonableCards = Keep(data.SpecialSummonableCards, MainAction.SpSummon);
            kept.MonsterSetableCards = Keep(data.MonsterSetableCards, MainAction.SetMonster);
            kept.SpellSetableCards = Keep(data.SpellSetableCards, MainAction.SetSpell);
            kept.ActivableCards = Keep(data.ActivableCards, MainAction.Activate);
            return kept;
        }

        private bool SearchWanted(DuelBotWrapper bot)
        {
            if (SearchBudgetMs <= 0 || !PlaySearchSupported)
                return false;
            if (SearchHost.Available)
                return true;
            if (!_searchProblemLogged)
            {
                _searchProblemLogged = true;
                bot.LogNote($"search off: {SearchHost.Problem}");
            }
            return false;
        }

        /// <summary>
        /// Lines the search's best move needs before it may replace the brain's, by the size of the gap (round-robin 51a, user
        /// 2026-11-13: "judge confidence on best move's lines alone"): a big gap needs fewer. 50 below 15 points, 35 from 15,
        /// 20 from 30.
        /// </summary>
        private int OverrideLinesNeeded(double gap) => gap >= OverrideBigGap ? OverrideBigGapLines : gap >= OverrideMidGap ? OverrideMidGapLines : MinOverrideLines;
        [Tunable("A score gap (0-1) this big counts as clear: the search needs only OverrideBigGapLines lines to be sure.", Section = "Search")]
        public static double OverrideBigGap = 0.30;
        [Tunable("Lines needed to be sure of a clear (OverrideBigGap) lead.", Section = "Search")]
        public static int OverrideBigGapLines = 20;
        [Tunable("A score gap (0-1) this big counts as fair: OverrideMidGapLines lines to be sure.", Section = "Search")]
        public static double OverrideMidGap = 0.15;
        [Tunable("Lines needed to be sure of a fair (OverrideMidGap) lead.", Section = "Search")]
        public static int OverrideMidGapLines = 35;

        /// <summary>The noise in the best move's mean, from its own lines only: few lines on the brain's move mean the search
        /// found it worse and stopped playing it (seen 2026-11-13: a winning Raiza line, 94 over 1730 lines, refused because the
        /// brain's move had 6), so they don't hold the override back.</summary>
        private static double OverrideNoise(RootStat best) => Math.Sqrt(Math.Max(0.01, best.Mean * (1 - best.Mean)) / Math.Max(1, best.Visits));

        /// <summary>Whether <paramref name="best"/> beats <paramref name="mine"/> clearly: enough lines for the gap, and the gap above
        /// the margin plus two standard errors of the best move's mean.</summary>
        private bool Confident(RootStat best, RootStat mine)
        {
            double gap = best.Mean - mine.Mean;
            return best.Visits >= OverrideLinesNeeded(gap) && gap >= SearchOverrideMargin + OverrideNoiseFactor * OverrideNoise(best);
        }

        /// <summary>
        /// Round-robin 51b (user, 2026-11-13: "if the bot is facing a tricky decision I don't mind it thinking for longer ... up
        /// to 10 seconds occasionally"): the search prefers another move by more than the margin but isn't yet confident. The
        /// caller searches on (the same tree) in ExtraThinkStepMs steps, up to MaxExtraThinkMs more.
        /// </summary>
        private bool Unsure(SearchOutcome outcome, string brainKey, IReadOnlySet<string> legal)
        {
            // User, 2026-11-13: "keep searching until it gets significantly better though, just as a way of finding the stopping
            // condition. if the planner can't find a significantly better line it keeps trying": think on while neither the
            // best other move nor the brain's is significantly ahead (Confident: the old override bar, now only the stop).
            var mine = outcome.Stat(brainKey);
            var other = outcome.Root.Where(s => s.Key != brainKey && legal.Contains(s.Key)).OrderByDescending(s => s.Mean).FirstOrDefault();
            // Two orders of the same play (a transposition) aren't a doubt worth thinking on.
            bool sameplay = mine != null && other != null && (outcome.Transposed ?? Array.Empty<(string, string)>())
                .Any(p => p.A == mine.Key && p.B == other.Key || p.A == other.Key && p.B == mine.Key);
            return mine != null && other != null && !sameplay && !SearchMustKeep(_decisionRules) &&
                   !Confident(other, mine) && !Confident(mine, other);
        }

        /// <summary>
        /// The move the search plays: the best score among the legal moves with at least MinPlayLines lines behind them (no
        /// bias to the brain's move: user, 2026-11-13, "a planned line that is better than the brain's gets played ... when it
        /// runs out of time the best line is played between planner or brain, but with no bias"). The brain's own move counts
        /// whatever its lines.
        /// </summary>
        /// <summary>
        /// Whether a move has the lines behind it to count (user, 2026-11-17: "make sure we never get actions on stale/thin scores"): the line
        /// count (MinPlayLines), or a score settled under SettledError after the ScreenLines fair look. One rule for stopping, for the move
        /// played and for replacing the brain's (seen 2026-11-17: the search stopped on settled scores, then wouldn't count the better move
        /// for "too few lines", and the brain's move went through on a stale score).
        /// </summary>
        private bool HasItsLines(RootStat s) => s.Visits >= MinPlayLines || s.Visits >= ScreenLines && s.StdErr <= SettledError;

        private RootStat? BestByScore(SearchOutcome outcome, string brainKey, IReadOnlySet<string> legal) =>
            outcome.Root.Where(s => legal.Contains(s.Key) && (HasItsLines(s) || s.Key == brainKey))
                .OrderByDescending(s => s.Mean).FirstOrDefault();

        /// <summary>Fewest lines behind a move before its score can make it the move played (so a lucky few lines can't). Tune from logs.</summary>
        public int MinPlayLines { get; set; } = DefaultMinPlayLines; // was 20 (user, 2026-11-15: more lines behind a move, a truer score)

        /// <summary>The starting line requirement: 200 (user, 2026-11-15), or DUELBOT_MIN_PLAY_LINES. It tapers with time (ThinkUntilSureAsync).</summary>
        [Tunable("Lines the best move needs at the start of a decision (both moves of a replacement need them); tapers to TaperFloorLines with time.", Section = "Search")]
        public static int DefaultMinPlayLines { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("DUELBOT_MIN_PLAY_LINES"), out int minLines) && minLines > 0 ? minLines : 200;

        /// <summary>Extra thinking per step when the search is unsure, and the most added to one decision (ms).</summary>
        [Tunable("Extra thinking per step when the search is unsure, and the most added to one decision (ms).", Section = "Search")]
        public int ExtraThinkStepMs { get; set; } = 2000;
        [Tunable("", Section = "Search")]
        public int MaxExtraThinkMs { get; set; } = 6000;

        /// <summary>The search, then more of it while it's unsure (see Unsure).</summary>
        private async Task<SearchOutcome?> SearchUntilSureAsync(DuelBotWrapper bot, BotState rootState, string brainKey, IReadOnlyList<string> offered,
            IReadOnlySet<string> legal, bool summonUsed, bool battleDone, bool inBattle, CancellationToken ct)
        {
            return await ThinkUntilSureAsync(bot, async budget =>
            {
                _extraBudgetMs = budget;
                try { return await SearchAsync(bot, rootState, brainKey, offered, summonUsed, battleDone, inBattle, ct); }
                finally { _extraBudgetMs = null; }
            }, brainKey, legal);
        }

        // The decision being searched (its key, turn, tree and node): its extra runs continue it.
        private (string Key, int Turn, MctsTree Tree, MctsNode Start)? _thisDecision;

        // Set while an extra run fills the lines of the two moves being compared (see ThinkUntilSureAsync).
        private (List<string> Keys, int Lines)? _focusRoot;

        // The setup of the decision being searched (its board view, evaluator and root holds), reused by its extra runs.
        private (int Turn, BotState RootState, SearchView View, MctsEvaluator Evaluator, Dictionary<string, (double Cost, string Rule)> RootHolds)? _prepared;

        /// <summary>The budget of each extra run while the search thinks longer (ms). The taper itself counts elapsed time, not runs.</summary>
        [Tunable("The budget of each extra run while the search thinks longer (ms). The taper itself counts elapsed time, not runs.", Section = "Search")]
        public static int ExtraRunMs = 2000;

        // Set while the search thinks longer: the budget of that extra run (instead of the per-decision budget).
        private int? _extraBudgetMs;

        /// <summary>Lines the best move needs at the start of a decision (DUELBOT_MIN_PLAY_LINES overrides; user, 2026-11-15: 200).</summary>
        public static int StartLines => DefaultMinPlayLines;
        /// <summary>From this point on the line requirement tapers, and "significantly ahead" is no longer required.</summary>
        [Tunable("From this point on the line requirement tapers, and \"significantly ahead\" is no longer required.", Section = "Search")]
        public static int TaperAfterMs = 6000;
        /// <summary>The requirement drops by TaperLines every TaperStepMs (user, 2026-11-15: "after 6s drop it by 10 every half a second").</summary>
        [Tunable("The requirement drops by TaperLines every TaperStepMs (user, 2026-11-15: \"after 6s drop it by 10 every half a second\").", Section = "Search")]
        public static int TaperStepMs = 500, TaperLines = 10;
        /// <summary>The progress note's interval while a decision takes longer than that (S9: "log every 5s").</summary>
        [Tunable("The progress note's interval while a decision takes longer than that (S9: \"log every 5s\").", Section = "Search")]
        public static int ProgressNoteMs = 5000;

        /// <summary>Lines the best move needs after thinking this long: StartLines, then down by TaperLines every TaperStepMs after TaperAfterMs.</summary>
        private static int RequiredLines(long elapsedMs) =>
            elapsedMs <= TaperAfterMs ? StartLines : Math.Max(Math.Min(TaperFloorLines, StartLines), StartLines - TaperLines * (int)((elapsedMs - TaperAfterMs) / TaperStepMs));

        /// <summary>The taper never goes below this (user, 2026-11-16: "lets even keep it up at 100 lines"; a slow good bot over a fast bad one).</summary>
        [Tunable("The taper never goes below this (user, 2026-11-16: \"lets even keep it up at 100 lines\"; a slow good bot over a fast bad one).", Section = "Search")]
        public static int TaperFloorLines = 100;

        /// <summary>
        /// The search, then more of it until its best-scoring move has enough lines behind it (user, 2026-11-15: no maximum time; the
        /// requirement tapers instead). Before TaperAfterMs the best move also has to be significantly ahead (not Unsure); after it,
        /// the tapering line count alone decides, so even a true tie ends (by about TaperAfterMs + 10 s at 200 lines). Each extra run
        /// continues the same tree. <paramref name="run"/> runs one search with the given budget (null: the decision's own budget).
        /// While it takes longer than ProgressNoteMs, a small progress note every ProgressNoteMs.
        /// </summary>
        private async Task<SearchOutcome?> ThinkUntilSureAsync(DuelBotWrapper bot, Func<int?, Task<SearchOutcome?>> run, string brainKey, IReadOnlySet<string> legal)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            MinPlayLines = StartLines;
            _noProgressRuns = 0;
            string decidedBy = "first run";
            var outcome = await run(null);
            long nextNote = ProgressNoteMs;
            while (outcome != null && !IsSearchTwin && outcome.Root.Count > 0)
            {
                long elapsed = clock.ElapsedMilliseconds;
                int need = RequiredLines(elapsed);
                MinPlayLines = need;
                var top = outcome.Root.Where(s => legal.Contains(s.Key)).OrderByDescending(s => s.Mean).FirstOrDefault();
                if (top == null)
                    break;
                // Both sides of a replacement need the lines (user, 2026-11-16: "every next action should require the same amount of
                // justification"): the move it would replace too, not only the best one.
                var replaced = top.Key != brainKey && legal.Contains(brainKey) ? outcome.Stat(brainKey) : null;
                // Every move gets a fair look first (user, 2026-11-17: the bot ended its turn with Sirocco in hand; summon Sirocco had a
                // handful of lines while "end the turn" and the brain's set reached 100 each, and a 4000-line bench put Sirocco 27 points
                // ahead): no decision until each legal move has ScreenLines lines.
                var unscreened = outcome.Root.Where(s => legal.Contains(s.Key) && s.Visits < ScreenLines).Select(s => s.Key).ToList();
                // A move has its lines when it has the count, or when its score has settled (its margin of error is under SettledError):
                // more lines wouldn't move it, so they're better spent elsewhere (user, 2026-11-17: "end the turn" lines all come out
                // alike; a settled move meets the requirement even below the count).
                bool Met(RootStat s) => HasItsLines(s);
                // Moves within error of the best (the close-call margin: 2 x sqrt(se1^2 + se2^2)) need their lines too, and get a share of the
                // focus until they have them (user, 2026-11-19: "if one line is within error of being the best line it should get a share of
                // which lines are being checked"; seen 2026-11-18: Bora to attack 1.0 behind on 230 lines, the best move on 4523). Two orders
                // of the same play aren't a doubt.
                var transposed = outcome.Transposed ?? Array.Empty<(string A, string B)>();
                var challengers = outcome.Root.Where(s => legal.Contains(s.Key) && s.Key != top.Key && (replaced == null || s.Key != replaced.Key) &&
                        top.Mean - s.Mean <= 2 * Math.Sqrt(top.StdErr * top.StdErr + s.StdErr * s.StdErr) &&
                        !transposed.Any(p => p.A == top.Key && p.B == s.Key || p.A == s.Key && p.B == top.Key))
                    .ToList();
                bool enough = Met(top) && (replaced == null || Met(replaced)) && unscreened.Count == 0 && challengers.All(Met);
                // The time limit (user, 2026-11-16: "if it gets to 30s, just take the best move available whatever it is and flag it really
                // loudly in the logs ... give a full breakdown as to what went wrong").
                if (!enough && elapsed >= MaxThinkMs)
                {
                    bot.LogNote($"search: !!! TIME LIMIT ({MaxThinkMs / 1000} s) - deciding on what it has, NOT sure !!! {outcome.TreeVisits} lines in the tree; " +
                                $"needed {need} lines on the best move and on the brain's, and {ScreenLines} on every move");
                    foreach (var s in outcome.Root.Where(s => legal.Contains(s.Key)).OrderByDescending(s => s.Mean))
                        bot.LogNote($"search: !!! {s.Label}: score {s.Mean * 100:0.0} over {s.Visits} lines" +
                                    (double.IsNaN(s.Average) ? "" : $" (lines average {s.Average * 100:0.0})") +
                                    (s.Key == brainKey ? " [the brain's move]" : "") + (s.Visits < ScreenLines ? " [never got a fair look]" : ""));
                    decidedBy = "time limit";
                    break;
                }
                if (enough && (elapsed >= TaperAfterMs || !Unsure(outcome, brainKey, legal)))
                {
                    decidedBy = top.Visits >= need && (replaced == null || replaced.Visits >= need) ? "line count" : "settled score";
                    break;
                }
                if (elapsed >= nextNote)
                {
                    nextNote += ProgressNoteMs;
                    string progress = $"search: still thinking ({elapsed / 1000.0:0} s): {outcome.TreeVisits} lines; best so far {top.Label} " +
                                      $"(score {top.Mean * 100:0.0} over {top.Visits} lines)" +
                                      (replaced != null ? $", the brain's {replaced.Label} {replaced.Mean * 100:0.0} over {replaced.Visits}" : "") +
                                      $"; each needs {need} lines" + (unscreened.Count > 0 ? $", {unscreened.Count} move(s) still under {ScreenLines}" : "") +
                                      (challengers.Count(s => !Met(s)) is > 0 and var close ? $", {close} move(s) within error of the best still short" : "");
                    bot.LogNote(progress);
                    // Live too (user, 2026-11-16: "I would still like to see more information about how the search is progressing
                    // here while I'm waiting"): notes reach the log only when the decision is made.
                    Console.WriteLine($"[thinking] {progress}");
                }
                // The two moves being compared are short of lines: the next run sends its lines to them (the fewer first) until each has
                // them, instead of spreading them over every root move (seen 2026-11-16: 44 s for two moves to reach 100 lines each).
                var short_ = new List<string>();
                if (!Met(top)) short_.Add(top.Key);
                if (replaced != null && !Met(replaced)) short_.Add(replaced.Key);
                short_.AddRange(challengers.Where(s => !Met(s)).Select(s => s.Key));
                // The unscreened moves first, up to ScreenLines each; then the two being compared, up to the requirement.
                _focusRoot = unscreened.Count > 0 ? (unscreened, ScreenLines) : short_.Count > 0 ? (short_, need) : null;
                SearchOutcome? more;
                // Longer runs than the taper step: each run rebuilds the view and the holds (about a second), so 0.5 s runs spent two
                // thirds of the time outside the search (seen 2026-11-16: a run every 1.5 s).
                try { more = await run(ExtraRunMs); }
                finally { _focusRoot = null; }
                if (more == null)
                    break;
                // No new lines reach this decision (a chain window the replayed lines miss: seen 2026-11-17, "907 lines didn't reach this
                // window", and the bot thought until the server gave up): thinking longer can't help, so it decides on what it has.
                int linesBefore = outcome.Root.Where(s => legal.Contains(s.Key)).Sum(s => s.Visits);
                int linesAfter = more.Root.Where(s => legal.Contains(s.Key)).Sum(s => s.Visits);
                if (linesAfter <= linesBefore)
                {
                    if (++_noProgressRuns >= NoProgressRunsToStop)
                    {
                        bot.LogNote($"search: no new lines reach this decision ({more.Iterations} played, {more.Failures} failed); deciding on the " +
                                    $"{linesAfter} it has");
                        outcome = more;
                        break;
                    }
                }
                else
                    _noProgressRuns = 0;
                outcome = more with
                {
                    // The decision's totals, not the last extra run's (manual game 2026-11-16 #136: the log said the brain's attack had
                    // "no lines this search" when only the last 0.5 s run had none; the move had hundreds from this decision's runs).
                    Iterations = outcome.Iterations + more.Iterations,
                    Failures = outcome.Failures + more.Failures,
                    Elapsed = outcome.Elapsed + more.Elapsed,
                    Details = MergeDetails(outcome.Details, more.Details),
                };
            }
            _prepared = null; // the next decision reads its own board
            // How settled each move's score was (user, 2026-11-17: "get an idea of how settled these lines become"): one line per decision.
            if (outcome != null && !IsSearchTwin)
                bot.LogNote($"search: settle stats: decided by {decidedBy} after {clock.ElapsedMilliseconds / 1000.0:0.0} s; " +
                            string.Join("; ", outcome.Root.Where(s => legal.Contains(s.Key)).OrderByDescending(s => s.Mean)
                                .Select(s => $"{s.Label}{(s.Key == brainKey ? " [brain]" : "")} {s.Mean * 100:0.0} n={s.Visits} +/-{s.StdErr * 100:0.00}")));
            if (clock.ElapsedMilliseconds > SearchBudgetMs + 1000)
                bot.LogNote($"search: thought {clock.ElapsedMilliseconds / 1000.0:0.#} s in all (best move needed {MinPlayLines} lines)");
            return outcome;
        }

        [Tunable("Every legal move needs this many lines before a decision is made (a fair look at each), however far ahead another is.", Section = "Search")]
        public static int ScreenLines = 40;

        [Tunable("A move's score counts as settled when its margin of error (standard error, 0-1 scale: 0.01 = 1 point) is at most this; a settled move meets the line requirement below the count.", Section = "Search")]
        public static double SettledError = 0.01;

        [Tunable("The most a decision thinks (ms): past it the best move so far is played and the log flags it loudly with every move's lines and scores.", Section = "Search")]
        public static int MaxThinkMs = 30000;

        // Extra runs in a row that added no lines to this decision's moves.
        private int _noProgressRuns;

        [Tunable("Extra runs in a row that add no lines to a decision before the search stops thinking and decides on what it has.", Section = "Search")]
        public static int NoProgressRunsToStop = 2;

        /// <summary>Cards that take a monster of theirs until the End Phase: Brain Control, Mind Control, Change of Heart.</summary>
        private static readonly int[] TemporaryControlIds = { BrainControlId, 37520316, 4031928 };

        /// <summary>Two runs' line totals per move, combined (weighted by lines).</summary>
        private static IReadOnlyDictionary<string, RootDetail>? MergeDetails(IReadOnlyDictionary<string, RootDetail>? a, IReadOnlyDictionary<string, RootDetail>? b)
        {
            if (a == null || b == null)
                return a ?? b;
            var merged = new Dictionary<string, RootDetail>(a);
            foreach (var (key, y) in b)
            {
                if (!merged.TryGetValue(key, out var x))
                {
                    merged[key] = y;
                    continue;
                }
                int n = x.Lines + y.Lines;
                double W(double p, double q) => n == 0 ? 0 : (p * x.Lines + q * y.Lines) / n;
                merged[key] = new RootDetail(n, W(x.WinRate, y.WinRate), W(x.LossRate, y.LossRate), W(x.OurLp, y.OurLp), W(x.TheirLp, y.TheirLp),
                    W(x.OurBoard, y.OurBoard), W(x.TheirBoard, y.TheirBoard), W(x.Penalty, y.Penalty));
            }
            return merged;
        }

        /// <summary>
        /// The MCTS's move when it beats the brain's by the margin plus two standard errors of the difference, with enough lines
        /// behind both, else null (the brain decides; user 2026-11-11). Only answers the real prompt offers count.
        /// </summary>
        private RootStat? Better(DuelBotWrapper bot, SearchOutcome outcome, string brainKey, IReadOnlySet<string> legal, string phase, int turn)
        {
            var mine = outcome.Stat(brainKey);
            var best = BestByScore(outcome, brainKey, legal) ?? outcome.Root.FirstOrDefault(s => legal.Contains(s.Key));
            var holds = HoldsFor(legal);
            // What a move's lines ended in this search (user, 2026-11-13: "the log should show more"; later, familiar board
            // states found this way become routines).
            string Detail(RootStat s) => outcome.Details?.TryGetValue(s.Key, out var d) == true ? d.Describe() : "no lines this search";
            string summary = $"{outcome.Iterations} lines in {outcome.Elapsed.TotalMilliseconds:0} ms, {outcome.TreeVisits} in the tree" +
                             (outcome.Failures > 0 ? $", {outcome.Failures} failed ({outcome.FirstFailure})" : "");
            if (outcome.PositionMismatch is string mismatch)
                bot.LogNote($"search WARNING: the rebuilt position differs from the real one ({mismatch})");
            if (best == null || mine == null)
            {
                bot.LogNote($"search: no verdict ({summary})");
                return null;
            }
            foreach (var (a, b) in outcome.Transposed ?? Array.Empty<(string, string)>())
                if (legal.Contains(a) && legal.Contains(b))
                    bot.LogNote($"search: {outcome.Stat(a)?.Label ?? a} and {outcome.Stat(b)?.Label ?? b} reach the same position (another order of the same play)");
            bool protectedChoice = SearchMustKeep(_decisionRules);
            // Any better score wins (no bar): the extra thinking above already ran until one side was significantly ahead or time ran out.
            bool overrides = best.Key != brainKey && !protectedChoice && best.Mean > mine.Mean;
            var rules = _decisionRules.Distinct().ToList();

            // What the user reads (2026-11-12: "make them clearer, more relevant to me: what was the best line it didn't use"):
            // the verdict, the best move the brain didn't play with its line, and why it wasn't played. Scores are the
            // search's 0-100 yardstick for the position the line ends in, centred on the decision's own position (50 = no better or worse than now; since 2026-11-18), not a win chance.
            // The score is the move's line value (its best well-explored follow-ups); the plain average of its lines is shown when it differs.
            double Score(RootStat s) => Math.Round(s.Mean * 100, 1);
            string Avg(RootStat s) => !double.IsNaN(s.Average) && Math.Abs(s.Average - s.Mean) >= 0.005 ? $", lines average {s.Average * 100:0.0}" : "";
            string Line(RootStat s) => outcome.RootLines?.TryGetValue(s.Key, out var steps) == true && steps.Count > 1
                ? string.Join(" -> ", steps.Select(step => step.Label))
                : s.Label;
            string Held(RootStat s) => holds.TryGetValue(s.Key, out var h) ? $", against the brain's rule {h.Rule}, which cost it {h.Cost:0.#} card(s) in the scoring" : "";
            string effort = $"{outcome.Elapsed.TotalSeconds:0.0} s, {outcome.Iterations} lines played out";
            if (overrides)
            {
                bot.LogNote($"search: REPLACED the brain's {mine.Label} (score {Score(mine)} over {mine.Visits} lines{Avg(mine)}) with {best.Label} (score {Score(best)} over {best.Visits} lines{Avg(best)}, " +
                            $"{Score(best) - Score(mine):0.0} better{Held(best)}); {effort}");
                bot.LogNote($"search: its line: {Line(best)}");
                bot.LogNote($"search: its lines: {Detail(best)}");
                bot.LogNote($"search: the brain's {mine.Label}: {Detail(mine)}; its line: {Line(mine)}");
                // Within the scores' uncertainty (two standard errors of the difference): played anyway, and flagged for the user's review
                // (user, 2026-11-18: "whenever it happens, can we flag it and you raise it to me for review ... they might be a good source
                // of finding issues with scoring").
                double margin = 2 * Math.Sqrt(best.StdErr * best.StdErr + mine.StdErr * mine.StdErr);
                if (best.Mean - mine.Mean <= margin)
                    FlagForReview(bot, "close-calls.jsonl",
                        $"search: CLOSE CALL: {best.Label} ({Score(best)} over {best.Visits} lines) replaced the brain's {mine.Label} ({Score(mine)} over {mine.Visits} lines): " +
                        $"{Score(best) - Score(mine):0.0} better, inside the uncertainty (+/-{margin * 100:0.0}). Its lines: {Detail(best)} | the brain's: {Detail(mine)}; " +
                        $"its line: {Line(best)} | the brain's: {Line(mine)}");
                if (rules.Count > 0)
                    bot.LogNote($"search: the brain's rules it went past: {string.Join(", ", rules)}");
            }
            else
            {
                // The best move other than the brain's: the search's own favourite, or (when that is the brain's) the runner-up.
                var other = best.Key != brainKey ? best
                    : outcome.Root.Where(s => s.Key != brainKey && legal.Contains(s.Key)).OrderByDescending(s => s.Mean).FirstOrDefault();
                bot.LogNote($"search: kept the brain's {mine.Label} (score {Score(mine)} over {mine.Visits} lines{Avg(mine)}); {effort}");
                if (other != null)
                {
                    string why = other.Mean <= mine.Mean ? $"it scored {Score(mine) - Score(other):0.0} lower"
                        : protectedChoice ? $"it scored {Score(other) - Score(mine):0.0} higher, but the brain's move is a protected engine play ({string.Join(", ", rules)})"
                        : $"it scored {Score(other) - Score(mine):0.0} higher, but on too few lines to count ({other.Visits}; {MinPlayLines} needed)";
                    bot.LogNote($"search: best move not played: {other.Label} (score {Score(other)} over {other.Visits} lines{Avg(other)}{Held(other)}): {why}");
                    bot.LogNote($"search: its line would have been: {Line(other)}");
                    bot.LogNote($"search: its lines: {Detail(other)}");
                    bot.LogNote($"search: the brain's {mine.Label}: {Detail(mine)}; its line: {Line(mine)}");
                }
            }
            // kind|turn|phase|rules (comma-separated, the last usually decided)|brain label|brain mean|brain lines|
            // search label|search mean|search lines|total lines
            static string Clean(string s) => s.Replace('|', '/').Replace('\t', ' ');
            var alternative = best.Key == brainKey ? outcome.Root.FirstOrDefault(s => s.Key != brainKey && legal.Contains(s.Key)) ?? best : best;
            string evt = string.Join('|', overrides ? "override" : "keep", turn, phase,
                rules.Count == 0 ? "(none)" : string.Join(',', rules),
                Clean(mine.Label), mine.Mean.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), mine.Visits,
                Clean(alternative.Label), alternative.Mean.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), alternative.Visits,
                outcome.Iterations);
            SearchEventSink.Report(bot, evt);
            return overrides ? best : null;
        }

        /// <summary>A target pick key ("card:&lt;id&gt;@1.4.&lt;zone&gt;") as a card of theirs, or null.</summary>
        private static DynamicCard? TargetFromKey(string? key)
        {
            if (key == null || !key.StartsWith("card:"))
                return null;
            var parts = key.Substring(5).Split('@');
            var zone = parts.Length == 2 ? parts[1].Split('.') : Array.Empty<string>();
            if (zone.Length != 3 || !int.TryParse(parts[0], out int id) || !byte.TryParse(zone[0], out byte controller) ||
                !byte.TryParse(zone[1], out byte location) || !byte.TryParse(zone[2], out byte sequence) || controller == 0)
                return null;
            return new DynamicCard
            {
                ID = id,
                CurrentCoordinates = new CardCoordinate { Controller = controller, Location = location, Sequence = sequence },
            };
        }

        /// <summary>
        /// Points the coming attack-target selection, for the attack move actually played (<paramref name="chosenKey"/>):
        ///  - the search replaced the attacker: its target;
        ///  - the brain's target was committed or chosen for a reason of its own: it stands (user, 2026-11-10: Gearframe sent
        ///    into Gol'gar by the search's pick);
        ///  - the brain's target only came from the battle logic's step order (a fallback): the search's target replaces it when
        ///    its lines say so clearly, by the same bar as a whole move (user, 2026-11-12, round-robin 1: the search wanted
        ///    Fortress into Dark Armed Dragon, and the fallback free kill of Lyla stood).
        /// </summary>
        private void AimAttack(DuelBotWrapper bot, SearchOutcome outcome, string chosenKey, bool overriding)
        {
            var picks = outcome.FollowUps?.GetValueOrDefault(chosenKey);
            var best = picks?.FirstOrDefault(p => TargetFromKey(p.Key) != null);
            string? bestKey = best?.Key ?? (overriding || chosenKey == outcome.Root.FirstOrDefault()?.Key ? outcome.FollowUpKey : null);
            if (TargetFromKey(bestKey) is not DynamicCard target)
            {
                if (overriding)
                    ClearAim();
                return;
            }
            byte sequence = target.CurrentCoordinates.Sequence;
            string name = target.ID > 0 ? CardNames.Get(target.ID) : "the face-down monster";
            if (_selectedDefenderCard is DynamicCard current && current.CurrentCoordinates.Sequence == sequence)
                return;
            if (overriding || _selectedDefenderCard is not DynamicCard brainTarget)
            {
                AimAt(bot, target, AimKind.Search, $"attack target {name} in zone {sequence + 1}: the search's line");
                return;
            }
            string brainZone = $"zone {brainTarget.CurrentCoordinates.Sequence + 1}";
            if (_aimKind != AimKind.Fallback)
            {
                bot.LogNote($"search: it would attack {name} in zone {sequence + 1}; the brain's target in {brainZone} stands ({_aimKind.ToString().ToLowerInvariant()})");
                return;
            }
            var mine = picks?.FirstOrDefault(p => TargetFromKey(p.Key) is DynamicCard t && t.CurrentCoordinates.Sequence == brainTarget.CurrentCoordinates.Sequence);
            if (best == null || mine == null)
            {
                bot.LogNote($"search: it would attack {name} in zone {sequence + 1}; the brain's target in {brainZone} stands (no lines behind it to compare)");
                return;
            }
            // The same rule as a whole move: a better score with enough lines behind it wins, no bias to the brain's target.
            if (best.Mean > mine.Mean && HasItsLines(best))
            {
                AimAt(bot, target, AimKind.Search, $"attack target {name} in zone {sequence + 1}: the search's lines beat the brain's fallback target in {brainZone} " +
                                                   $"(score {best.Mean * 100:0.0} over {best.Visits} lines vs {mine.Mean * 100:0.0} over {mine.Visits})");
                return;
            }
            bot.LogNote($"search: it would attack {name} in zone {sequence + 1} (score {best.Mean * 100:0.0}, {best.Visits} lines); the brain's target in {brainZone} " +
                        $"(score {mine.Mean * 100:0.0}, {mine.Visits} lines) stands: " +
                        (best.Mean <= mine.Mean ? "not better" : $"too few lines to count ({best.Visits}; {MinPlayLines} needed)"));
        }

        private async Task<SearchOutcome?> SearchAsync(DuelBotWrapper bot, BotState rootState, string brainKey, IReadOnlyList<string> offered,
            bool summonUsed, bool battleDone, bool inBattle, CancellationToken ct)
        {
            // The decision has the cores: the dead-time search waits.
            var session = Session;
            session?.Pause();
            try
            {
                return await SearchCoreAsync(bot, session, rootState, brainKey, offered, summonUsed, battleDone, inBattle, ct);
            }
            finally
            {
                session?.Resume();
            }
        }

        private async Task<SearchOutcome?> SearchCoreAsync(DuelBotWrapper bot, MctsSession? session, BotState rootState, string brainKey,
            IReadOnlyList<string> offered, bool summonUsed, bool battleDone, bool inBattle, CancellationToken ct)
        {
            SearchView view;
            int turn = _currentHeader.CurrentTurn;
            // An extra run of the same decision (ThinkUntilSureAsync) plays the same board: the view, the evaluator and the holds
            // built for its first run are reused (user, 2026-11-16: "why not just keep the same setup? you're running the same
            // scenario"); reading the board again from the server cost about a second per run.
            bool reused = _extraBudgetMs != null && _prepared is { } prep && prep.Turn == turn && prep.RootState == rootState;
            if (reused)
                view = _prepared!.Value.View;
            else
            {
                try
                {
                    view = await BuildSearchViewAsync(bot);
                }
                catch (Exception ex)
                {
                    bot.LogNote($"search: couldn't read the board ({ex.Message})");
                    return null;
                }
                view.NormalSummonUsed = summonUsed;
                view.BattlePhaseDone = battleDone;
                view.InBattlePhase = inBattle;
                view.FirstTurnOfDuel = turn == 1;
                view.RealTurn = turn;
                view.OnlyAttackerZone = _siroccoTarget.Turn == turn ? _siroccoTarget.Zone : -1;
                if (_main2Summons.Turn == turn)
                    view.Main2Summoned.AddRange(_main2Summons.Codes);
                view.ActivatedThisTurn.AddRange(_activatedThisTurn);
                view.Record = await BuildTurnRecordAsync();
                // The board a mid-chain replay rebuilds (DeckBotBrain.MidChain): this decision, and our answers from here on.
                if (IsOwnTurn(_currentHeader))
                    SetMidChainAnchor(turn, view, rootState);
            }

            // This decision's node in the tree grown so far (dead time, earlier decisions this turn), found by board and answers.
            string key = DecisionKey.Of(rootState, view, offered);
            MctsTree tree;
            MctsNode start;
            string origin;
            // A memory wipe (their activation, our draw: DeckBotBrain.TurnRecord) throws the reused trees away: their lines were
            // played on outdated information and counted commitments that no longer hold.
            // Since our last searched decision this turn, did they pick among more than one genuinely different card (Fortress
            // destroying one of our cards, a revive with different targets)? Then what they chose was unknown to the plan: wipe.
            // A pick with a single distinct answer was played out by its lines: the plan continues (the tree reaching this
            // decision through it is the check that it did).
            if (!reused && OpenChoiceSinceLastSearch(bot, key))
                view.Record = await BuildTurnRecordAsync();
            bool wiped = _wipeTrees;
            _wipeTrees = false;
            if (wiped)
            {
                _turnTree = null;
                _deadTree = null;
            }
            // The same decision searching on (the taper's extra runs): the tree and node it started with, whatever the key says
            // (seen 2026-11-15: a decision whose key the lines filed differently started a fresh tree every 0.5 s run).
            if (!wiped && _thisDecision is { } same && same.Key == key && same.Turn == turn)
                (tree, start, origin) = (same.Tree, same.Start, "continuing this decision's own search");
            else if (!wiped && session?.Tree is { } grown && grown.Find(key) is { } found)
                (tree, start, origin) = (grown, found, $"continuing {grown.Label}'s tree");
            else if (_deadTree is { } dead && dead.Turn == turn && dead.Tree.Find(key) is { } deadFound)
                (tree, start, origin) = (dead.Tree, deadFound, $"continuing the dead-time tree ({dead.Tree.Label})");
            else if (_turnTree is { } own && own.Turn == turn && own.Tree.Find(key) is { } ownFound)
                (tree, start, origin) = (own.Tree, ownFound, "continuing this turn's tree");
            else
            {
                tree = new MctsTree { Label = "this turn" };
                start = tree.Root;
                origin = session?.Tree != null ? $"new tree (the dead-time tree, {session.Tree.Label}, never reached this decision)" : "new tree";
                // De-synced (user, 2026-11-13: "track very carefully if these lines ever become de-synced"): this turn's tree
                // was kept (no memory wipe), yet none of its lines reached this board with these options. Something happened that
                // its lines never played out, so its commitments were built on a game we're not in.
                if (!wiped && _turnTree is { } kept0 && kept0.Turn == turn)
                {
                    var last = _prevPlayed is { } lp && lp.Turn == turn ? lp : ((int, string, int)?)null;
                    string after = last is { } l ? $"{l.Item2} ({l.Item3} lines behind it)" : "a move that wasn't searched";
                    if (last is { } explored && explored.Item3 >= MinPlayLines)
                    {
                        bot.LogNote($"search: DESYNC: this turn's tree has no node for this decision (board and options), though no memory " +
                                    $"wipe was called and its lines explored the last move, {after}: something happened that they never played out. Starting a new tree");
                        // What the lines reached instead: the nearest board they filed, and how it differs from the real one.
                        if (kept0.Tree.Closest(key) is { } near)
                            bot.LogNote($"search: DESYNC detail: the nearest board the lines reached differs in {near.Differences}");
                    }
                    else
                        bot.LogNote($"search: new tree: this turn's tree never reached this decision (the last move, {after}, was barely explored); not a desync");
                }
            }
            _turnTree = (turn, tree);
            _thisDecision = (key, turn, tree, start);
            _lastSearchStart = (turn, tree, start);
            if (!IsSearchTwin && IsOwnTurn(_currentHeader))
                SetPlanStart(turn, tree, start);
            int kept;
            lock (start.Lock)
                kept = start.Visits;

            var used = _activatedThisTurn.ToArray();
            int budget = _extraBudgetMs ?? SearchBudgetMs + TurnStartStep(bot);
            var settings = new SearchSettings(BudgetMs: budget, Threads: DefaultSearchThreads, Horizon: DefaultSearchHorizon,
                FocusRoots: _focusRoot?.Keys, FocusLines: _focusRoot?.Lines ?? 0);
            MctsEvaluator evaluator;
            Dictionary<string, (double Cost, string Rule)> rootHolds;
            if (reused)
                (evaluator, rootHolds) = (_prepared!.Value.Evaluator, _prepared.Value.RootHolds);
            else
            {
                await RefreshStopOddsAsync();
                evaluator = MakeEvaluator(view);
                // The brain's holds at this decision: what its lines pay for the moves it held back (the twin records the same at
                // every later decision of a line).
                rootHolds = HoldsFor(offered);
                if (rootHolds.Count > 0)
                    bot.LogNote($"search: moves the brain's rules hold back here (a line that makes one pays the cost in the scoring): {string.Join(", ", rootHolds.Select(kv => $"{DescribeKey(kv.Key)} ({kv.Value.Rule}, {kv.Value.Cost:0.#})"))}");
                _prepared = (turn, rootState, view, evaluator, rootHolds);
            }
            var search = new Mcts(view, tree, start, rootState, brainKey, used, settings, evaluator, SearchBrains(bot), rootHolds)
            {
                MayChoose = PlanMayChoose, Suggestions = PlanSuggestions, UsedEffects = _usedEffects.ToArray(),
                // A routine the brain's move starts here: lines through that move follow it, so the routine is scored on the engine.
                Routine = _planAdoptedThisDecision && _searchLine is { } routineLine && _planInfo?.Source.StartsWith("routine") == true
                    ? (brainKey, routineLine.Steps.Skip(routineLine.Next).Where(IsMainOrBattle).ToList(), RoutineMatch)
                    : null,
            };
            int seed = Interlocked.Increment(ref _searchSeed);
            try
            {
                var outcome = await Task.Run(() => search.Run(seed, ct), ct);
                // Diagnostic (2026-11-14): the decision's own node must be filed under the key the bot looks it up by, or no tree is
                // ever reused (the next search of this very decision started a new tree). Logged once per duel when they differ.
                if (!IsSearchTwin && origin.StartsWith("new tree") && start.Key is string filed && filed != key && !_keyMismatchLogged)
                {
                    _keyMismatchLogged = true;
                    bot.LogNote($"search: KEY MISMATCH: the bot looks up [{key}] but the tree filed [{filed}]");
                }
                // (The search's own statistics, threads and tree reuse, are left out of the log: user, 2026-11-12. Failures stay.)
                if (outcome.Failures > 0)
                    bot.LogNote($"search: {outcome.Failures} of its lines failed ({outcome.FirstFailure})");
                // While we wait for the server (and their chains), the dead-time search keeps growing this decision.
                session?.SetTarget(MakeTarget(bot, view, tree, start, rootState, $"{key}@{turn}", tree.Label));
                return outcome;
            }
            catch (Exception ex)
            {
                bot.LogNote($"search failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether their zone holds a card. Live Omega reports an empty zone as id 0 too (and a face-down card as id 0 or a
        /// negative id): only the card query tells them apart, by its position (as the duel logger does). Reading every
        /// empty zone as a face-down card filled the search's board with phantom set monsters (seen 2026-11-10, live).
        /// </summary>
        private async Task<bool> ZoneOccupiedAsync(int[] ids, CardLocation location, int zone)
        {
            if (ids.Length == 0)
                return false;
            if (ids.Any(id => id > 0))
                return true;
            try
            {
                var card = await CurrentField.GetBotFieldCard(1, (int)location, zone);
                return card.Position != 0 || card.ID != 0;
            }
            catch
            {
                return false; // no card there
            }
        }

        /// <summary>What we know right now, for the search: the board as Omega shows it, our deck list, and the deck read.</summary>
        private async Task<SearchView> BuildSearchViewAsync(DuelBotWrapper bot)
        {
            var field = await CurrentField.GetBotField();
            var own = field.OwnSide;
            var enemy = field.EnemySide;
            var view = new SearchView();
            view.LifePoints[0] = own.LifePoints;
            view.LifePoints[1] = enemy.LifePoints;

            static (int, int, int)? Coord(CardCoordinate c) =>
                c.Location == 0 ? null : (c.Controller, c.Location, c.Sequence);
            static ViewCard ToView(DynamicCard card, int code) => new(
                code, card.CurrentCoordinates.Sequence, card.Position == 0 ? SearchPosition.PosFaceUpAttack : card.Position, card.Owner, card.Attacked,
                Coord(card.EquipTarget),
                (card.TargetCards ?? Array.Empty<CardCoordinate>()).Select(Coord).Where(c => c != null).Select(c => c!.Value).ToList(),
                (card.Counters ?? Array.Empty<ushort[]>()).Where(c => c.Length >= 2).Select(c => ((int)c[0], (int)c[1])).ToList(),
                BaseAttack: card.ID > 0 && (card.Position & 0x5) != 0 ? card.BaseAttack : -1, BaseDefense: card.ID > 0 && (card.Position & 0x5) != 0 ? card.BaseDefense : -1,
                Attack: card.ID > 0 && (card.Position & 0x5) != 0 ? card.Attack : -1, Defense: card.ID > 0 && (card.Position & 0x5) != 0 ? card.Defense : -1);

            foreach (var card in await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, true))
                view.OwnMonsters.Add(ToView(card, card.ID) with { Improper = Summons.ImproperOnField(0, card.CurrentCoordinates.Sequence), ThisTurn = OwnCardThisTurn(true, card), NoAttack = await CannotAttackAfterOwnEffectAsync(card),
                    // Theirs, taken until the End Phase (Brain Control / Mind Control / Change of Heart activated this turn): it goes back
                    // in the rebuilt turn too and doesn't count toward our exposure (C1, user 2026-11-16). Taken for good (Goyo): ours.
                    Borrowed = card.Owner == 1 && _activatedThisTurn.Overlaps(TemporaryControlIds),
                    DestroyAtOwnersEnd = IsReturnerJinzo(card), PositionLockEnds = PositionLockEnds(card) });
            foreach (var card in await CurrentField.GetBotCardsInZone(CardLocation.SpellZone, true))
                view.OwnSpells.Add(ToView(card, card.ID) with { ThisTurn = OwnCardThisTurn(false, card) });

            // Their side: face-up cards in full; face-down ones by zone, named only when we remember them.
            var faceUpMonsters = (await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, false)).ToDictionary(c => (int)c.CurrentCoordinates.Sequence);
            var monsterZones = enemy.MonsterZones ?? Array.Empty<CardArray>();
            for (int z = 0; z < monsterZones.Length; z++)
            {
                var ids = monsterZones[z].Cards ?? Array.Empty<int>();
                if (!faceUpMonsters.ContainsKey(z) && !await ZoneOccupiedAsync(ids, CardLocation.MonsterZone, z))
                    continue;
                if (faceUpMonsters.TryGetValue(z, out var up) && up.ID > 0)
                {
                    view.EnemyMonsters.Add(ToView(up, up.ID) with { Improper = Summons.ImproperOnField(1, z), Battled = _battledThisTurn.Contains((_currentHeader.CurrentTurn, z, up.ID)),
                        PositionLockEnds = PositionLockEnds(up) });
                    continue;
                }
                var faceDown = new DynamicCard { CurrentCoordinates = new CardCoordinate { Controller = 1, Location = (byte)CardLocation.MonsterZone, Sequence = (byte)z } };
                int known = OpponentMemory.TryRecall(faceDown, out var recalled) ? recalled.Id : OpponentDeck.KnownSet(true, z) ?? 0;
                view.EnemyMonsters.Add(new ViewCard(known, z, SearchPosition.PosFaceDownDefense, 1, false, null, new(), new()));
                // Unknown: each line deals one from the deck read's likely sets for this zone (B1, user 2026-11-16), not one fixed body.
                if (known <= 0)
                    view.EnemySetMonsterOdds[z] = OpponentDeck.LikelyMonsterZone(z);
            }
            var faceUpSpells = (await CurrentField.GetBotCardsInZone(CardLocation.SpellZone, false)).ToDictionary(c => (int)c.CurrentCoordinates.Sequence);
            var spellZones = enemy.SpellZones ?? Array.Empty<CardArray>();
            for (int z = 0; z < spellZones.Length; z++)
            {
                var ids = spellZones[z].Cards ?? Array.Empty<int>();
                if (!faceUpSpells.ContainsKey(z) && !await ZoneOccupiedAsync(ids, CardLocation.SpellZone, z))
                    continue;
                if (faceUpSpells.TryGetValue(z, out var up) && up.ID > 0)
                    view.EnemySpells.Add(ToView(up, up.ID));
                else
                    view.EnemySpells.Add(new ViewCard(OpponentDeck.KnownSet(false, z) ?? 0, z, SearchPosition.PosFaceDown, 1, false, null, new(), new()));
            }

            view.OwnHand.AddRange((own.Hand ?? Array.Empty<int>()).Where(id => id > 0));
            view.OwnGrave.AddRange((own.Grave ?? Array.Empty<int>()).Where(id => id > 0));
            view.OwnBanished.AddRange((own.Banish ?? Array.Empty<int>()).Where(id => id > 0));
            view.OwnExtra.AddRange((own.Extra ?? Array.Empty<int>()).Where(id => id > 0));
            view.EnemyGrave.AddRange((enemy.Grave ?? Array.Empty<int>()).Where(id => id > 0));
            view.EnemyBanished.AddRange((enemy.Banish ?? Array.Empty<int>()).Where(id => id > 0));
            foreach (int id in view.OwnGrave.Distinct())
                if (Summons.ImproperInGrave(0, id) is > 0 and var n)
                    view.OwnGraveImproper[id] = n;
            foreach (int id in view.EnemyGrave.Distinct())
                if (Summons.ImproperInGrave(1, id) is > 0 and var n)
                    view.EnemyGraveImproper[id] = n;
            view.EnemyHandCount = enemy.Hand?.Length ?? 0;
            view.EnemyExtraCount = enemy.Extra?.Length ?? 0;

            // Our deck: the list minus every main-deck card of ours we can see.
            _ownDeckList ??= string.IsNullOrEmpty(bot.DeckHash) ? null : DeckCode.Decode(bot.DeckName ?? "own", bot.DeckHash, SearchHost.Cards, SearchHost.Scripts);
            if (_ownDeckList != null)
            {
                var left = _ownDeckList.Main.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                var seen = view.OwnHand.Concat(view.OwnGrave).Concat(view.OwnBanished)
                    .Concat(view.OwnMonsters.Where(c => c.Owner == 0).Select(c => c.Code))
                    .Concat(view.OwnSpells.Where(c => c.Owner == 0).Select(c => c.Code));
                foreach (int id in seen)
                    if (left.TryGetValue(id, out int n) && n > 0)
                        left[id] = n - 1;
                var deck = left.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
                int deckCount = own.Main?.Length ?? deck.Count;
                var rng = new Random(_searchSeed);
                view.OwnDeck.AddRange(deck.OrderBy(_ => rng.Next()).Take(Math.Max(0, deckCount)));
            }

            // Their hidden cards: no guessing (user, 2026-11-11: "play the opponent's turn with known cards only"). The cards we
            // know are in their hand are dealt; face-down ones we remember are named above (OpponentMemory: a Des Lacooda
            // flipped back down, a Book of Moon target); the rest are inert stand-ins, and what they might do is the evaluator's
            // threat term (their number and how explosive their deck is).
            view.EnemyKnownHand.AddRange(KnownEnemyHand().Take(view.EnemyHandCount));
            FillArrivedThisTurn(view);
            return view;
        }
    }
}
