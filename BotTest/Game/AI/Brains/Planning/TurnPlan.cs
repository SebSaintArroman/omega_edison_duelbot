using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using BotTest.Game.AI.Search;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The turn plan (user, 2026-11-12): a whole sequence of our moves and picks, chosen once and then followed. It comes
    /// from the search (its line, when it replaced the brain's move) or from one of the brain's routines (combined lethal,
    /// the Hero Frog combos, ...). The brain's rules have their say when the plan is chosen (the search scores its lines
    /// with the rules' holds; a routine is a rule), not again at every step: that's the point of planning, finding plays a
    /// one-step-at-a-time choice would reject. So while a plan stands, its next step is taken before the brain is asked.
    ///
    /// A plan ends on anything it didn't expect, and the next decision plans afresh from the real board:
    ///  - its next step isn't on offer;
    ///  - they respond during our turn (a chain of theirs, or a card from their hand: Gorz, Kalut, Honest...);
    ///  - a step drew us cards (Pot of Greed, Allure...): a new hand is new information.
    /// The search doesn't second-guess a plan halfway: it already weighed the whole turn when the plan was chosen.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Where the plan being followed came from, and what it's for (for the log).
        private (string Source, string Goal)? _planInfo;
        // Their hand size at our last plan step (a smaller hand: they used a card from it).
        // A step that drew us cards was just taken: the plan ends at the next decision.
        private bool _planDrew;

        private static readonly System.Text.RegularExpressions.Regex DrawText = new(
            @"\bdraw (\d|one|two|three) cards?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // ------------------------------------------------------------------ routines

        /// <summary>
        /// One step of a routine: a main-phase action on a card ("activate Dark Armed Dragon", "summon Lyla"), "battle" /
        /// "end", and what the step does for the brain's own bookkeeping when it's taken (OnTake). The live option is the first
        /// one offered with that action and card (a routine doesn't know zones or effect indexes).
        /// </summary>
        protected sealed record RoutineStep(MainAction Action, int CardId, string Label, Action? OnTake = null, CardLocation? Where = null, int[]? AnyOf = null,
            bool IsAttack = false)
        {
            public static RoutineStep Battle => new(MainAction.ToBattlePhase, 0, "go to the Battle Phase");

            /// <summary>A Battle Phase step: attack with this monster (its target set by <paramref name="onTake"/>, e.g. AimAt).</summary>
            public static RoutineStep AttackWith(int cardId, string label, Action? onTake = null) => new(MainAction.ToBattlePhase, cardId, label, onTake, IsAttack: true);
        }

        // Steps' bookkeeping, run when the plan takes them.
        private readonly Dictionary<PlanStep, Action> _stepActions = new();

        /// <summary>A routine step's key ("r:{action}:{card}"), for the plan.</summary>
        private static PlanStep ToPlanStep(RoutineStep step) => step.IsAttack ? new(BotState.SelectBattleCmd, $"ra:{step.CardId}", step.Label) : new(BotState.SelectIdleCmd,
            step.Action == MainAction.ToBattlePhase ? "battle" : step.Action == MainAction.ToEndPhase ? "end"
                : $"r:{(int)step.Action}:{(step.AnyOf is { Length: > 0 } any ? string.Join("|", any) : step.CardId.ToString())}:{(int)(step.Where ?? 0)}", step.Label);

        /// <summary>A plan step's offered option: a routine step, else the search key (matched loosely), else the exact key.</summary>
        private static string? RoutineMatch(PlanStep step, IReadOnlyCollection<string> offered) =>
            RoutineStepKey(step, offered) ?? SearchActions.MatchKey(step.Key, offered.ToList()) ?? (offered.Contains(step.Key) ? step.Key : null);

        /// <summary>The offered option a routine step means: the first with its action and card. Null for other steps.</summary>
        private static string? RoutineStepKey(PlanStep step, IReadOnlyCollection<string> offered)
        {
            // An attack step: the attack with that monster.
            if (step.Key.StartsWith("ra:") && int.TryParse(step.Key.AsSpan(3), out int attacker))
            {
                int canonical = SearchHost.CanonicalId(attacker) is > 0 and var c0 ? c0 : attacker;
                return offered.FirstOrDefault(k => k.StartsWith("attack:") && SearchActions.KeyCardId(k) == canonical);
            }
            if (!step.Key.StartsWith("r:"))
                return null;
            var parts = step.Key.Split(':');
            if (parts.Length != 4 || !int.TryParse(parts[1], out int action) || !int.TryParse(parts[3], out int where))
                return null;
            // One card, or any of several ("12|34|56": any Monarch in hand).
            var ids = parts[2].Split('|').Select(s => int.TryParse(s, out int id) ? (SearchHost.CanonicalId(id) is > 0 and var c ? c : id) : 0).ToList();
            // Where the card is, when the step says (Skill Successor from the graveyard vs set): "@hand" or "@{controller}.{location}.".
            bool At(string k) => where == 0 || (where == (int)CardLocation.Hand ? k.Contains("@hand") : k.Contains($".{where}."));
            // In the listed order (the routine's preference).
            return ids.Select(id => offered.FirstOrDefault(k => k.StartsWith($"{action}:") && SearchActions.KeyCardId(k) == id && At(k)))
                .FirstOrDefault(k => k != null);
        }

        /// <summary>
        /// A routine hands over its whole sequence (user, 2026-11-12: "the whole plan should be handed over at once and then
        /// followed"). The first step is made now (its response is returned: the caller sends it as its own answer, so its
        /// bookkeeping runs as usual); the rest become the turn plan. Null when the first step isn't on offer.
        /// </summary>
        protected async Task<IdleCmdResponse?> StartRoutineAsync(DecisionContext<IdleCmdData> context, string name, string goal, IReadOnlyList<RoutineStep> steps,
            bool lethal = false, int gladiatorSet = 0, bool desperateAllure = false)
        {
            if (steps.Count == 0)
                return null;
            var choices = SearchActions.Idle(context.StateData);
            var first = ToPlanStep(steps[0]);
            string? key = RoutineStepKey(first, choices.Select(c => c.Key).ToList()) ??
                          (first.Key is "battle" or "end" && choices.Any(c => c.Key == first.Key) ? first.Key : null);
            if (key == null)
                return null;
            if (!await RoutineHoldsUpAsync(context, name, steps.Select(ToPlanStep).ToList(), lethal, gladiatorSet, desperateAllure))
                return null;
            steps[0].OnTake?.Invoke();
            var rest = steps.Skip(1).Select(ToPlanStep).ToList();
            foreach (var (step, plan) in steps.Skip(1).Zip(rest))
                if (step.OnTake != null)
                    _stepActions[plan] = step.OnTake;
            context.Bot.LogNote($"routine {name}: {goal}: {string.Join(" -> ", steps.Select(s => s.Label))}");
            if (rest.Count > 0)
                AdoptPlan(context.Bot, $"routine {name}", goal, rest);
            return choices.First(c => c.Key == key).Response;
        }

        /// <summary>
        /// A routine whose first step the caller has already made (and returns as its answer): the rest becomes the turn plan.
        /// If the search then replaces that first move, the routine's plan is dropped (AfterMainMoveAsync).
        /// </summary>
        /// <returns>False when the engine check rejected the routine: the caller doesn't make its first move.</returns>
        protected async Task<bool> ContinueRoutineAsync(DecisionContext<IdleCmdData> context, string name, string goal, RoutineStep first,
            IReadOnlyList<RoutineStep> rest, bool lethal = false)
        {
            var steps = rest.Select(ToPlanStep).ToList();
            if (!await RoutineHoldsUpAsync(context, name, steps.Prepend(ToPlanStep(first)).ToList(), lethal))
                return false;
            foreach (var (step, plan) in rest.Zip(steps))
                if (step.OnTake != null)
                    _stepActions[plan] = step.OnTake;
            context.Bot.LogNote($"routine {name}: {goal}: {first.Label}{string.Concat(rest.Select(s => " -> " + s.Label))}");
            if (steps.Count > 0)
                AdoptPlan(context.Bot, $"routine {name}", goal, steps);
            _routineStartedTurn = _currentHeader.CurrentTurn;
            return true;
        }

        /// <summary>Engine play-outs per routine check (different hidden cards each).</summary>
        [Tunable("Engine play-outs per routine check (different hidden cards each).", Section = "Search")]
        public int RoutineCheckLines { get; set; } = 4;

        /// <summary>
        /// The routine played out on the engine before it's adopted (Mcts.CheckLine; user, 2026-11-12: "test it in engine before
        /// committing all the plays"). Rejected when in most play-outs a step isn't on offer when its turn comes, or, for a
        /// lethal routine, their LP don't reach 0 in at least half of them. Logged either way. Accepted untested when the
        /// engine isn't available (and in the search's own brain twin: the check is the real bot's).
        /// </summary>
        private async Task<bool> RoutineHoldsUpAsync(DecisionContext<IdleCmdData> context, string name, List<PlanStep> steps, bool lethal,
            int gladiatorSet = 0, bool desperateAllure = false)
        {
            if (IsSearchTwin || !SearchHost.Available || !desperateAllure && (SearchBudgetMs <= 0 || !PlaySearchSupported))
                return true;
            try
            {
                var view = await BuildSearchViewAsync(context.Bot);
                var data = context.StateData;
                view.NormalSummonUsed = (data.SummonableCards?.Length ?? 0) == 0 && (data.MonsterSetableCards?.Length ?? 0) == 0;
                view.BattlePhaseDone = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2;
                view.InBattlePhase = false;
                view.FirstTurnOfDuel = context.Header.CurrentTurn == 1;
                view.RealTurn = context.Header.CurrentTurn;
                var tree = new MctsTree { Label = $"check {name}" };
                var settings = new SearchSettings(BudgetMs: 0, Threads: 1, Horizon: 1);
                var check = new Mcts(view, tree, tree.Root, BotState.SelectIdleCmd, null, _activatedThisTurn.ToArray(), settings,
                    MakeEvaluator(view), SearchBrains(context.Bot), new Dictionary<string, (double, string)>())
                {
                    GladiatorSetForRoutine = gladiatorSet,
                    State = desperateAllure ? TagRules.BotStrategicState.Desperate : StrategicState
                };
                int seed = Interlocked.Increment(ref _searchSeed);
                var result = await Task.Run(() => check.CheckLine(steps, RoutineMatch, RoutineCheckLines, seed));
                if (result.Lines == 0)
                    return !desperateAllure;
                string tally = $"{result.Completed}/{result.Lines} play-outs made every step" + (lethal ? $", {result.Lethal}/{result.Lines} lethal" : "");
                bool stepsHold = result.Completed * 2 > result.Lines;
                bool lethalHolds = !lethal || result.Lethal * 2 >= result.Lines;
                if (stepsHold && lethalHolds)
                {
                    context.Bot.LogNote($"routine {name}: checked on the engine: {tally}");
                    return true;
                }
                context.Bot.LogNote($"routine {name} rejected by the engine check: {tally}" +
                                    (result.Broke != null ? $"; {result.Broke} in {result.BrokeLines}" : "") + "; the brain decides without it");
                return false;
            }
            catch (Exception ex)
            {
                if (desperateAllure)
                {
                    context.Bot.LogNote($"routine {name}: engine could not verify the sequence ({ex.Message})");
                    return false;
                }
                context.Bot.LogNote($"routine {name}: engine check failed ({ex.Message}); adopted unchecked");
                return true;
            }
        }

        // A routine started at this decision (its first move is the brain's: if the search replaces it, the routine is off).
        private int _routineStartedTurn = -1;

        /// <summary>Whether a turn plan is being followed this turn.</summary>
        protected bool FollowingPlan => _searchLine is { } line && line.Turn == _currentHeader.CurrentTurn;

        /// <summary>
        /// Adopt a turn plan: its steps from the next decision on (main / battle moves, each followed by its picks). Replaces
        /// any plan in hand. <paramref name="source"/> says where it came from ("search", or a routine's name).
        /// </summary>
        /// <summary>
        /// The plan was adopted during the current decision (a routine's first move is this decision's answer): its next step
        /// waits for the next decision. Without this the search refiner, running after the brain on the same decision, read
        /// step 2 against step 1's options (seen 2026-11-13: Monarch routines dropped before One for One resolved, and Glads
        /// went to the Battle Phase instead of the routine's Indomitable).
        /// </summary>
        private bool _planAdoptedThisDecision;

        private protected void AdoptPlan(DuelBotWrapper bot, string source, string goal, List<PlanStep> steps)
        {
            if (steps.Count == 0)
                return;
            _searchLine = (_currentHeader.CurrentTurn, steps, 0);
            _planAdoptedThisDecision = true;
            _planInfo = (source, goal);
            _planDrew = false;
            bot.LogNote($"plan ({source}): {goal}: {string.Join(" -> ", steps.Select(s => s.Label))}");
        }

        /// <summary>The plan ends (logged with why); the next decision plans afresh.</summary>
        protected void DropPlan(DuelBotWrapper? bot, string why)
        {
            if (_searchLine == null && _plan == null)
                return;
            bot?.LogNote($"plan ({_planInfo?.Source ?? "?"}) dropped: {why}; planning afresh from the board as it is");
            _searchLine = null;
            _plan = null;
            _planInfo = null;
        }

        /// <summary>Anything the plan didn't expect since our last step: then it's dropped (and true).</summary>
        private async Task<bool> PlanInterruptedAsync(DuelBotWrapper bot, BotStateHeader header)
        {
            if (_searchLine is not { } line)
                return false;
            if (line.Turn != header.CurrentTurn)
            {
                _searchLine = null;
                _planInfo = null;
                return true;
            }
            if (_planDrew)
            {
                _planDrew = false;
                DropPlan(bot, "we drew cards: a new hand");
                return true;
            }
            // (A chain of ours doesn't end the plan, nor does their effect that uses only known cards, which the plan could
            // simulate: user, 2026-11-13. Hidden information coming into play does: the memory wipe (DeckBotBrain.TurnRecord)
            // drops the plan, checked on the board read before this step.)
            await Task.CompletedTask;
            return false;
        }

        /// <summary>
        /// Whether the main-phase move actually made this decision was Vayu's graveyard activation. A Vayu pick planned when the
        /// brain chose Vayu applies only then: if another move was made (the search's), it would otherwise land on the next
        /// banish prompt (seen 2026-11-13: Dark Armed Dragon's cost banished Armed Wing "as planned when deciding to use Vayu").
        /// </summary>
        protected bool VayuWasTheMove(BotStateHeader header) =>
            LastMainMove.Turn == header.CurrentTurn && LastMainMove.Action == MainAction.Activate &&
            (LastMainMove.CardId == BlackwingVayuTheEmblemOfHonor || Search.SearchHost.CanonicalId(LastMainMove.CardId) == BlackwingVayuTheEmblemOfHonor);

        /// <summary>
        /// Our chain answer was sent. In our turn: the card's effect counts as used (TurnRecord); a routine whose next step is
        /// this effect takes that step; a chain that neither the routine nor the line behind the last move expected is logged
        /// as UNPLANNED (red in the log: user, 2026-11-13, "I think they should all be accounted for in the plans so want to
        /// know if we're missing anything"). Our own chains never wipe the plan.
        /// </summary>
        public void ChainAnswered(DecisionContext<SelectChainData> context, int index)
        {
            if (index < 0 || IsSearchTwin || !IsOwnTurn(context.Header))
                return;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            var chainedCard = index < cards.Length ? cards[index] : default;
            var chainDescs = context.StateData.Descriptions ?? Array.Empty<int>();
            RecordEffectUsed(context.Header, chainedCard.ID, index < chainDescs.Length ? chainDescs[index] : 0);
            // A routine's next step is this card's effect (Dark Armed Dragon's routine with its summon): using it in the chain
            // window takes that step.
            if (_searchLine is { } line && line.Turn == context.Header.CurrentTurn && chainedCard.ID > 0 && line.Next < line.Steps.Count &&
                (line.Steps[line.Next].Key == ToPlanStep(new RoutineStep(MainAction.Activate, chainedCard.ID, "")).Key ||
                 chainedCard.IsCode(DarkArmedDragon) && line.Steps[line.Next].Key == ToPlanStep(new RoutineStep(MainAction.Activate, DarkArmedDragon, "")).Key))
            {
                _searchLine = line.Next + 1 < line.Steps.Count ? line with { Next = line.Next + 1 } : null;
                return;
            }
            int code = Search.SearchHost.CanonicalId(chainedCard.ID) is > 0 and var official ? official : chainedCard.ID;
            if (_expectedLine is not { } expected || expected.Turn != context.Header.CurrentTurn ||
                expected.Steps.Any(step => step.State == BotState.SelectChain && SearchActions.KeyCardId(step.Key) == code))
                return;
            // A line that stops at the move itself (its next steps had too few lines to count) says nothing about what came
            // after it: only a line that goes on to a later main / battle move without this chain makes it unplanned (seen
            // 2026-11-13: Dark Grepher's and DAD's own summon chains flagged against the one-step lines of their summons).
            if (!expected.Steps.Skip(1).Any(IsMainOrBattle))
                return;
            string name = chainedCard.ID > 0 ? CardNames.Get(chainedCard.ID) : "a card";
            string line2 = _expectedLine is { } e && e.Turn == context.Header.CurrentTurn && e.Steps.Count > 0
                ? string.Join(" -> ", e.Steps.Select(s => s.Label)) : "(no line this turn)";
            context.Bot.LogNote($"search: UNPLANNED chain: {name}, not in the line behind the last move ({line2}); no memory wipe (our own chain)");
        }

        public async Task<IdleCmdResponse?> PlannedIdleAsync(DecisionContext<IdleCmdData> context)
        {
            _currentHeader = context.Header;
            bool followingDesperateAllure = _searchLine?.Turn == context.Header.CurrentTurn &&
                _planInfo?.Source == $"routine {DesperateAllureRoutine}";
            if (!followingDesperateAllure && await TryDesperateAllureAsync(context) is IdleCmdResponse desperate)
            {
                var action = SearchActions.Idle(context.StateData).FirstOrDefault(choice =>
                    choice.Response.Action == desperate.Action && choice.Response.Index == desperate.Index);
                context.Bot.LogNote($"mandatory action: {action?.Label ?? desperate.Action.ToString()} ({DesperateAllureRoutine})");
                await AfterMainMoveAsync(context, desperate, desperate);
                return desperate;
            }
            if (_searchLine != null)
                await PrepareContextAsync(context); // the board read runs the memory-wipe check (TurnRecord) before a routine step
            if (_searchLine != null && !followingDesperateAllure)
                await CheckOpenChoiceBeforeStepAsync(context.Bot, BotState.SelectIdleCmd, SearchActions.Idle(context.StateData).Select(c => c.Key).ToList());
            if (_searchLine == null || await PlanInterruptedAsync(context.Bot, context.Header))
                return null;
            if (await MandatoryAllureAsync(context.StateData) != null)
                return null;
            var choices = SearchActions.Idle(context.StateData);
            if (NextSearchLineKey(context.Bot, BotState.SelectIdleCmd, choices.Select(c => c.Key).ToList()) is not string key)
                return null;
            var chosen = choices.First(c => c.Key == key).Response;
            if (followingDesperateAllure && chosen.Action == MainAction.Activate &&
                !await AllureIsOnlyCardInHandAsync())
            {
                DropPlan(context.Bot, "Allure is no longer the only card in hand");
                return null;
            }
            if (!await SharedIdleMoveAllowedAsync(context, chosen))
            {
                DropPlan(context.Bot, "the next move fails a shared card rule");
                return null;
            }
            var required = await SetDustshootBeforeEndingAsync(context, chosen);
            if (required.Action != chosen.Action)
            {
                DropPlan(context.Bot, "set Trap Dustshoot before ending the turn");
                chosen = required;
            }
            else if (followingDesperateAllure)
                context.Bot.LogNote($"mandatory action: {choices.First(c => c.Key == key).Label} ({DesperateAllureRoutine})");
            await AfterMainMoveAsync(context, chosen, chosen);
            return chosen;
        }

        public async Task<BattleCmdResponse?> PlannedBattleAsync(DecisionContext<BattleCmdData> context)
        {
            _currentHeader = context.Header;
            if (_searchLine != null)
                await PrepareContextAsync(context);
            if (_searchLine != null)
                await CheckOpenChoiceBeforeStepAsync(context.Bot, BotState.SelectBattleCmd, SearchActions.Battle(context.StateData).Select(c => c.Key).ToList());
            if (_searchLine == null || await PlanInterruptedAsync(context.Bot, context.Header))
                return null;
            var choices = SearchActions.Battle(context.StateData);
            if (NextSearchLineKey(context.Bot, BotState.SelectBattleCmd, choices.Select(c => c.Key).ToList()) is not string key)
                return null;
            var step = choices.First(c => c.Key == key);
            if (key.StartsWith("attack:"))
            {
                // The target is the plan's own pick: the search line's next pick, or the one a routine's step aimed (its OnTake).
                if (_planInfo?.Source.StartsWith("routine") != true)
                    ClearAim();
            }
            await RecordBattleChoiceAsync(context, step.Response);
            return step.Response;
        }

        /// <summary>
        /// Bookkeeping once a main-phase move is final, whoever chose it (the brain, the search, the plan): commitments made
        /// for it take effect, Lyla / DAD use tracking, the last move, a draw ending the plan, dead time after ending the turn.
        /// </summary>
        private async Task AfterMainMoveAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse chosen, IdleCmdResponse brainChoice)
        {
            int movedCard = SearchActions.Idle(context.StateData)
                .FirstOrDefault(c => c.Response.Action == chosen.Action && c.Response.Index == chosen.Index)?.Key is string movedKey
                ? SearchActions.KeyCardId(movedKey) : 0;
            LastMainMove = (context.Header.CurrentTurn, chosen.Action, movedCard);
            await RecordMainMoveAsync(context, chosen, movedCard);
            if (chosen.Action is MainAction.Summon or MainAction.SetMonster)
                _normalSummonMadeTurn = context.Header.CurrentTurn;
            else if (_normalSummonMadeTurn > context.Header.CurrentTurn)
                _normalSummonMadeTurn = -1; // a new duel
            // Dark Armed Dragon's summon is a routine with its effect (user, 2026-11-13: "dad is always going to use its effect
            // chaining on summon if the opponent has any cards and you have expendable darks in graveyard, treat that as a
            // routine"): a line being followed gets the effect as its next step, so using it (Omega's chain window right after
            // the summon, or a main-phase activation) is part of the line, not a departure from it.
            if (chosen.Action == MainAction.SpSummon && (movedCard == DarkArmedDragon || Search.SearchHost.CanonicalId(movedCard) == DarkArmedDragon) &&
                _searchLine is { } dadLine && dadLine.Turn == context.Header.CurrentTurn)
            {
                var enemy = await GetEnemyMonsterCardsAsync();
                bool theyHaveCards = enemy.Any(card => card.ID != 0 || !card.IsFaceup()) || (await GetEnemySpellCardsAsync()).Count > 0;
                var effectStep = ToPlanStep(new RoutineStep(MainAction.Activate, DarkArmedDragon, "Dark Armed Dragon's effect (routine with its summon)"));
                bool already = dadLine.Next < dadLine.Steps.Count && dadLine.Steps[dadLine.Next].Key == effectStep.Key;
                if (theyHaveCards && !already && await DadSpareDarksAsync() > 0)
                {
                    dadLine.Steps.Insert(dadLine.Next, effectStep);
                    context.Bot.LogNote("plan: Dark Armed Dragon's effect added after its summon (a routine: they have cards, and we have spare DARKs)");
                }
            }
            bool brainMoveKept = chosen.Action == brainChoice.Action && chosen.Index == brainChoice.Index;
            // A routine started at this decision whose first move the search replaced: the routine is off.
            if (_routineStartedTurn == context.Header.CurrentTurn && !brainMoveKept && _planInfo?.Source.StartsWith("routine") == true)
                DropPlan(context.Bot, "the search replaced the routine's first move");
            _routineStartedTurn = -1;
            // Lyla's effect turns her to defence: from now until she can switch back (our next turn) she's semi-expendable.
            if (chosen.Action == MainAction.Activate && movedCard == LylaLightswornSorceress)
                LylaEffectTurn = context.Header.CurrentTurn;
            if (chosen.Action == MainAction.Activate && movedCard == DarkArmedDragon)
                NoteDadActivation(context.Header.CurrentTurn);
            // A move that draws us cards: whatever plan we're on ends after it (the new hand is new information).
            if (chosen.Action == MainAction.Activate && movedCard > 0 && FollowingPlan &&
                await CardDatabase.GetAsync(movedCard) is DBCardInfo info && DrawText.IsMatch(info.CardText ?? ""))
                _planDrew = true;
            var ourIds = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0).Select(card => card.ID).ToList();
            SettleCommitments(context.Bot, context.Header.CurrentTurn, movedCard, chosen.Action is MainAction.Summon or MainAction.SpSummon,
                brainMoveKept: chosen.Action == brainChoice.Action && chosen.Index == brainChoice.Index, ourIds);
            // Ending our turn: the dead time starts now, so search our next turn from here.
            if (chosen.Action == MainAction.ToEndPhase && !IsSearchTwin)
                await RetargetDeadTimeAsync(context.Bot, context.Header, await CurrentField.GetBotField(), endingOurTurn: true);
        }
    }
}
