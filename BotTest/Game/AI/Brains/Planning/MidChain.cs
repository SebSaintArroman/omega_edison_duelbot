using BotTest.Game.AI.Search;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Mid-chain planning in our turn (user, 2026-11-15: "mid-chain planning is very important, as you can see its been my answer
    /// to several of these things"; "search every time a new card is revealed in the chain window before deciding whether to
    /// chain anything"). The search can't load an open chain, so at a chain window of ours each line rebuilds the last searched
    /// decision of the turn (the anchor), plays the real game's answers since then with the cards revealed since then where they
    /// really were (Card Trooper's milled cards on top of our deck, the trap they flipped in its zone, the card they played from
    /// their hand), checks that it reached this very window, and searches from there through the rest of the turn and their reply.
    /// Their turn comes later (user: "later i will want to do mid-chain planning for every decision where new information is
    /// revealed").
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // The last searched decision of our turn: the board a replay rebuilds, the prompt it was, and our answers since.
        private (int Turn, SearchView View, BotState State)? _anchor;
        private readonly List<ReplayStep> _sinceAnchor = new();
        // Their cards seen coming into play since the anchor, each stamped with how many of our answers came before it.
        private readonly List<ReplayReveal> _revealsSinceAnchor = new();
        // What the last search (anchor or mid-chain) knew: reveals and our deck size then. More than that is new information.
        private int _revealsSearched, _deckSearched = -1;
        // Cards we picked out of our own deck since the last search (a search: Charge's add, Reinforcement): known, not new.
        private int _deckPickedSinceSearch;

        private void SetMidChainAnchor(int turn, SearchView view, BotState state)
        {
            if (IsSearchTwin)
                return;
            _anchor = (turn, view.Clone(), state);
            _sinceAnchor.Clear();
            _revealsSinceAnchor.Clear();
            _revealsSearched = 0;
            _deckSearched = view.OwnDeck.Count;
            _deckPickedSinceSearch = 0;
        }

        /// <summary>Every answer the real bot sent: in our turn, after the anchor, it's a step of the replay.</summary>
        public virtual void ResponseSent(BotState state, object? data, byte[] response)
        {
            RecordCheckpointAnswer(state, data, response); // every turn, ours or theirs (DeckBotBrain.TurnLog)
            if (IsSearchTwin || !IsOwnTurn(_currentHeader))
                return;
            // Our picks out of our own deck count for the turn record whatever the anchor (a known card, not a mill).
            var picked = ReplayKeys.Of(state, data, response);
            if (picked != null && picked.Key.StartsWith("pick:"))
                _recDeckPicked += picked.Key[5..].Split('+').Count(k => k.EndsWith(".deck"));
            if (_anchor is not { } anchor || anchor.Turn != _currentHeader.CurrentTurn)
                return;
            // The monster of theirs our attack targets battles it (plan picks answer attack targets before the brain's own record).
            if (data is SelectCardData { SelectionHint: (int)DHint.ATTACKTARGET } target && response.Length >= 2 &&
                target.SelectableCards is { } targets && response[1] < targets.Length)
            {
                var defender = targets[response[1]];
                _battledThisTurn.Add((_currentHeader.CurrentTurn, defender.CurrentCoordinates.Sequence, defender.ID));
            }
            var step = ReplayKeys.Of(state, data, response);
            if (step != null)
                _sinceAnchor.Add(step);
            // Our own pick from our deck (a search): we know what it is, so it isn't new information (user, 2026-11-15: "it knows what
            // card was added because that's the one it picked").
            if (step != null && step.Key.StartsWith("pick:"))
                _deckPickedSinceSearch += step.Key[5..].Split('+').Count(k => k.EndsWith(".deck"));
        }

        /// <summary>
        /// At every board read of our turn after the anchor: their cards that came into play since, stamped with the number of
        /// our answers before them (the replay plays each at their first chain window after that many of ours).
        /// </summary>
        private void ObserveReveals(FieldInfo field)
        {
            if (!IsSearchTwin)
                _deckCountNow = field.OwnSide.Main?.Length ?? -1;
            if (IsSearchTwin || _anchor is not { } anchor || anchor.Turn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return;
            var enemy = field.EnemySide;
            void Zones(CardArray[]? zones, List<ViewCard> atAnchor, bool monster)
            {
                if (zones == null)
                    return;
                foreach (var card in atAnchor.Where(c => c.Code <= 0))
                {
                    if (card.Sequence >= zones.Length || _revealsSinceAnchor.Any(r => !r.FromHand && r.Monster == monster && r.Zone == card.Sequence))
                        continue;
                    if ((zones[card.Sequence].Cards ?? Array.Empty<int>()).FirstOrDefault(id => id > 0) is int code and > 0)
                        _revealsSinceAnchor.Add(new ReplayReveal(code, false, monster, card.Sequence, _sinceAnchor.Count));
                }
            }
            Zones(enemy.SpellZones, anchor.View.EnemySpells, monster: false);
            Zones(enemy.MonsterZones, anchor.View.EnemyMonsters, monster: true);

            // A face-down spell/trap of theirs destroyed before it was ever face-up (our MST on their set Waboku, 2026-11-16): its zone is
            // empty now and a new spell/trap is in their graveyard - that's what it was, so the replay places it there.
            var spellZones = enemy.SpellZones ?? Array.Empty<CardArray>();
            var vanished = anchor.View.EnemySpells
                .Where(c => c.Code <= 0 && c.Sequence < spellZones.Length && !(spellZones[c.Sequence].Cards ?? Array.Empty<int>()).Any(id => id != 0) &&
                            !_revealsSinceAnchor.Any(r => !r.FromHand && !r.Monster && r.Zone == c.Sequence))
                .Select(c => c.Sequence).ToList();
            if (vanished.Count > 0)
            {
                var graveBefore = anchor.View.EnemyGrave.Concat(_revealsSinceAnchor.Select(r => r.Code))
                    .Concat(anchor.View.EnemySpells.Where(c => c.Code > 0).Select(c => c.Code)).Concat(anchor.View.EnemyMonsters.Where(c => c.Code > 0).Select(c => c.Code)).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                foreach (int id in enemy.Grave ?? Array.Empty<int>())
                {
                    if (id <= 0)
                        continue;
                    if (graveBefore.TryGetValue(id, out int n) && n > 0)
                    {
                        graveBefore[id] = n - 1;
                        continue;
                    }
                    if (vanished.Count == 0)
                        break;
                    if (Search.SearchHost.Cards.Get(id) is { } r && (r.Type & 0x6) != 0)
                    {
                        _revealsSinceAnchor.Add(new ReplayReveal(id, false, false, vanished[0], _sinceAnchor.Count));
                        vanished.RemoveAt(0);
                    }
                }
            }

            // Cards they played from their hand: their hand is smaller than at the anchor by more than we've accounted for, and
            // the cards that appeared on their side (graveyard, banished, a zone empty at the anchor) are those cards.
            int handDrop = anchor.View.EnemyHandCount - (enemy.Hand?.Length ?? 0);
            int fromHand = _revealsSinceAnchor.Count(r => r.FromHand);
            if (handDrop <= fromHand)
                return;
            var before = anchor.View.EnemyGrave.Concat(anchor.View.EnemyBanished)
                .Concat(anchor.View.EnemyMonsters.Select(c => c.Code)).Concat(anchor.View.EnemySpells.Select(c => c.Code))
                .Concat(_revealsSinceAnchor.Select(r => r.Code)).Where(id => id > 0)
                .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var now = (enemy.Grave ?? Array.Empty<int>()).Concat(enemy.Banish ?? Array.Empty<int>())
                .Concat((enemy.MonsterZones ?? Array.Empty<CardArray>()).SelectMany(z => z.Cards ?? Array.Empty<int>()))
                .Concat((enemy.SpellZones ?? Array.Empty<CardArray>()).SelectMany(z => z.Cards ?? Array.Empty<int>()))
                .Where(id => id > 0);
            foreach (int id in now)
            {
                if (before.TryGetValue(id, out int n) && n > 0)
                {
                    before[id] = n - 1;
                    continue;
                }
                if (fromHand >= handDrop)
                    break;
                _revealsSinceAnchor.Add(new ReplayReveal(id, true, false, -1, _sinceAnchor.Count));
                fromHand++;
            }
        }

        private static List<string> ChainOptionKeys(SelectChainData chain) => SearchActions.ChainOptionKeys(chain);

        private static int ChainIndexOf(SelectChainData chain, string key)
        {
            if (key == "pass")
                return -1;
            var cards = chain.SelectableCards ?? Array.Empty<DynamicCard>();
            var descs = chain.Descriptions ?? Array.Empty<int>();
            for (int i = 0; i < cards.Length; i++)
                if ($"chain:{SearchActions.CardKey(cards[i])}:{(i < descs.Length ? descs[i] : 0)}" == key)
                    return i;
            return -2;
        }

        /// <summary>
        /// A chain window of ours in our turn: when a card was revealed since the last search (their activation, our milled or
        /// drawn cards) or the line behind our last move says nothing about this window, the search replans from here before
        /// we chain anything; otherwise the line's own chain step is played. Null: the brain's answer stands.
        /// </summary>
        public async Task<int?> RefineChainAsync(DecisionContext<SelectChainData> context, int brainIndex, CancellationToken ct = default)
        {
            // Their turn (or ours before any searched decision): the line behind the last chain answer searched this turn is followed while
            // nothing new was revealed; its step stands in for the brain's answer, and the shared chain rules still check it.
            bool turnStartWindow = !IsSearchTwin && _ownTurnParity >= 0 && SearchWanted(context.Bot) &&
                                   (!IsOwnTurn(context.Header) || _anchor is not { } anchored || anchored.Turn != context.Header.CurrentTurn);
            var lineStep = turnStartWindow && !context.StateData.Forced && ChainOptionKeys(context.StateData) is { Count: >= 2 } lineOptions
                ? TheirTurnLineStep(context.Bot, lineOptions) : null;
            int lineIndex = lineStep is { } ls ? ChainIndexOf(context.StateData, ls.Key) : -2;
            if (lineStep != null && lineIndex < -1)
            {
                _theirTurnLine = null;
                lineStep = null;
            }
            int answerIn = lineStep != null ? lineIndex : brainIndex;
            var offeredCards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            string Describe(int i) => i >= 0 && i < offeredCards.Length ? OmegaBotWrapper.Logging.CardNames.Get(offeredCards[i].ID) : "pass";
            if (lineStep is { } followed)
                context.Bot.LogNote($"search: chain window: nothing new revealed, playing the line behind the last chain answer: {Describe(lineIndex)} " +
                                    $"(score {BoardEvaluator.CuPm(followed.Mean, followed.StdErr)} over {followed.Visits} lines; the brain's answer: {Describe(brainIndex)})");

            if (await ApplySharedChainRulesAsync(context, answerIn) is int sharedAnswer)
            {
                // Logged when it changes the answer (seen 2026-11-24: the shared attack-boost rule silently turned the Blackwing brain's
                // Kalut into a pass; the search had passed Mirror Force counting on that Kalut, and Shura died).
                if (sharedAnswer != answerIn && !IsSearchTwin)
                    context.Bot.LogNote(lineStep != null
                        ? $"search: COMMITMENT BROKEN: the shared chain rules changed the line's {Describe(answerIn)} to {Describe(sharedAnswer)}"
                        : $"shared chain rules: changed the brain's {Describe(brainIndex)} to {Describe(sharedAnswer)}");
                if (lineStep is { } kept && sharedAnswer == answerIn)
                    TheirTurnLineStepped(kept.Child);
                else
                    _theirTurnLine = null;
                return sharedAnswer;
            }
            if (lineStep is { } step)
            {
                TheirTurnLineStepped(step.Child);
                return answerIn;
            }

            // The holds for this window, after the deck brain's answer (real bot and search twin alike).
            try { await RecordChainHoldsAsync(context, brainIndex); }
            catch { /* holds are guidance only */ }
            // Trap Stun (CSV S8): the last chance for our traps, nothing after it resolves, ours chained to their first push-stopper.
            try
            {
                if (await TrapStunAnswerAsync(context, brainIndex) is int stunAnswer)
                    return stunAnswer == brainIndex ? null : stunAnswer;
            }
            catch { /* the brain's answer stands */ }
            // Their turn, or ours before any searched decision (our Draw and Standby Phases): the search from the turn's start
            // (DeckBotBrain.TurnLog; plan steps 3 and 4).
            if (!IsSearchTwin && _ownTurnParity >= 0 && SearchWanted(context.Bot) &&
                (!IsOwnTurn(context.Header) || _anchor is not { } searched || searched.Turn != context.Header.CurrentTurn))
            {
                try { return await TurnStartChainAsync(context, brainIndex, ct); }
                catch (Exception ex)
                {
                    context.Bot.LogNote($"search: chain search from the turn's start failed, keeping the brain's answer: {ex.Message}");
                    return null;
                }
            }
            if (IsSearchTwin || !IsOwnTurn(context.Header) || !SearchWanted(context.Bot) ||
                _anchor is not { } anchor || anchor.Turn != context.Header.CurrentTurn)
                return null;
            var chain = context.StateData;
            var options = ChainOptionKeys(chain);
            if (options.Count < 2)
                return null;
            try
            {
                var view = await BuildSearchViewAsync(context.Bot);
                // What their revealed cards target, while they're on the chain (the replay makes their pick the same).
                for (int r = 0; r < _revealsSinceAnchor.Count; r++)
                {
                    var reveal = _revealsSinceAnchor[r];
                    if (reveal.FromHand || reveal.Targets is { Count: > 0 })
                        continue;
                    var theirs = await CurrentField.GetBotCardsInZone(reveal.Monster ? CardLocation.MonsterZone : CardLocation.SpellZone, false);
                    if (theirs.FirstOrDefault(c => c.CurrentCoordinates.Sequence == reveal.Zone && c.ID == reveal.Code) is { ID: > 0 } onChain)
                    {
                        var coords = (onChain.CurrentChainTargets is { Length: > 0 } chainTargets ? chainTargets : onChain.TargetCards ?? Array.Empty<CardCoordinate>())
                            .Where(t => t.Location != 0).Select(t => ((int)t.Controller, (int)t.Location, (int)t.Sequence)).ToList();
                        if (coords.Count > 0)
                            _revealsSinceAnchor[r] = reveal with { Targets = coords };
                    }
                }
                // New since the last search: their revealed cards, or cards that left our deck (a mill, a draw, a search).
                var news = new List<string>();
                foreach (var r in _revealsSinceAnchor.Skip(_revealsSearched))
                    news.Add($"{CardNames.Get(r.Code)} {(r.FromHand ? "from their hand" : r.Monster ? "flipped" : "activated from their set zone")}");
                int deckNow = view.OwnDeck.Count;
                int unseenLeft = _deckSearched - deckNow - _deckPickedSinceSearch;
                if (_deckSearched >= 0 && unseenLeft > 0)
                    news.Add($"{unseenLeft} card(s) left our deck unseen (milled or drawn)");
                string brainKey = brainIndex >= 0 && brainIndex < (chain.SelectableCards?.Length ?? 0)
                    ? $"chain:{SearchActions.CardKey(chain.SelectableCards![brainIndex])}:{(brainIndex < (chain.Descriptions?.Length ?? 0) ? chain.Descriptions![brainIndex] : 0)}"
                    : chain.Forced ? options[0] : "pass";

                if (news.Count == 0 && NextStep(BotState.SelectChain) is { } carried &&
                    SearchActions.MatchKey(carried.Key, options) is { } carriedKey &&
                    ChainIndexOf(chain, carriedKey) is var carriedIndex and >= -1)
                {
                    StepDone(carried, true);
                    return carriedIndex;
                }

                // Nothing new revealed (user, 2026-11-15: "no new information should have been revealed by that point"): no replan. The
                // plan already holds this window: the tree of the last searched decision, walked along the real moves to its node
                // here, keeps being searched (its lines so far still count). Something new: a fresh node, on what we know now.
                // Nothing new: the plan's own step here is played, no new search (user, 2026-11-15: "chains should be known about in
                // advance if they're only using your own cards").
                (MctsTree Tree, MctsNode Node)? planned = news.Count == 0 ? PlannedNode(BotState.SelectChain) : null;
                var planStep = planned is { } p ? PlanNodeStep(p.Node, options) : null;
                if (planStep is { } sure && sure.Visits >= StartLines)
                {
                    var planStep2 = sure;
                    context.Bot.LogNote($"search: chain window: nothing new revealed, playing the plan's step: {DescribeChainKey(chain, planStep2.Key)} " +
                                        $"(score {BoardEvaluator.CuPm(planStep2.Mean, planStep2.StdErr)} over {planStep2.Visits} lines)");
                    return ChainIndexOf(chain, planStep2.Key) is var planIndex and >= -1 ? planIndex : null;
                }
                // The plan's node is thin (its best answer on fewer than MinPlayLines lines: seen 2026-11-15, Skill Successor on Bestiari
                // at the end of the Battle Phase on 8 lines): the search carries on from that node (same tree, same information).
                if (news.Count == 0 && planned is { } thin)
                    context.Bot.LogNote($"search: chain window: nothing new revealed, but the plan's node is thin ({planStep?.Visits ?? 0} lines behind its best answer); searching on from it");
                else if (news.Count > 0)
                    context.Bot.LogNote($"search: MID-CHAIN replan (new since the last search: {string.Join("; ", news)}); replaying {_sinceAnchor.Count} answer(s) since the last searched decision: " +
                                        (_sinceAnchor.Count == 0 ? "(none)" : string.Join(" -> ", _sinceAnchor.Select(s => s.Label))));
                else
                    context.Bot.LogNote("search: chain window: nothing new revealed, but the plan never reached this window (PLAN GAP: its lines didn't get here); planning it now");

                var outcome = await MidChainSearchAsync(context.Bot, anchor, view, options, brainKey, planned, ct);
                _revealsSearched = _revealsSinceAnchor.Count;
                _deckSearched = deckNow;
                _deckPickedSinceSearch = 0;
                // No line reached the window from the last searched decision (a REPLAY MISS): from the turn's start instead (plan step 4).
                if (outcome == null)
                {
                    context.Bot.LogNote("search: no verdict from the last searched decision; searching from the turn's start");
                    return await TurnStartChainAsync(context, brainIndex, ct);
                }
                var legal = options.ToHashSet();
                var better = Better(context.Bot, outcome, brainKey, legal, "chain", context.Header.CurrentTurn);
                string chosen = better?.Key ?? brainKey;
                FollowPlan(context.Bot, outcome, chosen, context.Header.CurrentTurn, overriding: better != null);
                CommitLineIfOverriding(context.Bot, outcome, chosen, context.Header.CurrentTurn, overriding: better != null);
                return better == null ? null : ChainIndexOf(chain, chosen) is var index and >= -1 ? index : null;
            }
            catch (Exception ex)
            {
                context.Bot.LogNote($"search: mid-chain replan failed, keeping the brain's answer: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// This chain window's node in the tree of the last searched decision, walked down along our real answers since (theirs:
        /// passes, since nothing of theirs was revealed), or null when the tree never got here. Prompts that aren't tree decisions
        /// (zone, position, single answers) are stepped over.
        /// </summary>
        private (MctsTree Tree, MctsNode Node)? PlannedNode(BotState state)
        {
            if (_planStart is not { } last || last.Turn != _currentHeader.CurrentTurn)
                return null;
            var node = last.Start;
            var seen = new HashSet<MctsNode>(ReferenceEqualityComparer.Instance);
            bool FollowPasses()
            {
                while (true)
                {
                    MctsNode? passed;
                    lock (node.Lock)
                    {
                        if (node.ChildPlayer != 1)
                            return true;
                        passed = node.Children.GetValueOrDefault("pass");
                    }
                    if (passed == null || !seen.Add(node))
                        return false;
                    node = passed;
                }
            }
            foreach (var step in _sinceAnchor.Skip(last.From))
            {
                if (!FollowPasses())
                    return null;
                lock (node.Lock)
                {
                    if (node.ChildPlayer < 0)
                        return null; // the lines never went past here
                    if (node.ChildPlayer != 0 || !SamePrompt(node.ChildState, step.State))
                        continue; // not a decision of the tree (one answer, a zone, a position)
                    var child = node.Children.FirstOrDefault(kv => NodeLoose(kv.Key) == NodeLoose(step.Key)).Value;
                    if (child == null)
                        return null;
                    node = child;
                }
            }
            if (!FollowPasses())
                return null;
            lock (node.Lock)
                return node.ChildPlayer == 0 && SamePrompt(node.ChildState, state) ? (last.Tree, node) : null;
        }

        private int _deckCountNow = -1;

        /// <summary>Something new since the plan was searched: their revealed cards, or cards that left our deck unseen.</summary>
        private bool NewsSincePlan() =>
            _revealsSinceAnchor.Count > _revealsSearched ||
            _deckSearched >= 0 && _deckCountNow >= 0 && _deckSearched - _deckCountNow - _deckPickedSinceSearch > 0;

        /// <summary>
        /// The plan's answer to a choice inside an effect (a search's card, a target, a cost, yes/no, an option) in our turn: the
        /// step at its node (user, 2026-11-15: "cards searched from reinforcement *are* part of the plan ... the brain shouldn't ever
        /// be involved"). Null when there's no plan for it: then the brain answers, and the log says why.
        /// </summary>
        private string? PlanAnswer(BotState state, IReadOnlyList<string> offered, string what)
        {
            if (IsSearchTwin || !IsOwnTurn(_currentHeader) || _planStart is not { } start || start.Turn != _currentHeader.CurrentTurn || offered.Count < 2)
                return null;
            // Something new since the plan (a pick re-plans first, RefinePickAsync): the original plan's choice still stands while on offer
            // (user, 2026-11-15: "the backup should be to continue to do the thing that the original plan wanted to do").
            if (NewsSincePlan())
                _trackedBot?.LogNote($"search: {what}: something new was revealed since the plan; keeping to the original plan's choice");
            if (PlannedNode(state) is not { } planned || PlanNodeStep(planned.Node, offered) is not { } step)
            {
                _trackedBot?.LogNote($"search: {what}: the plan's lines never reached this choice (PLAN GAP)");
                return null;
            }
            _trackedBot?.LogNote($"search: {what}: the plan's choice (score {BoardEvaluator.CuPm(step.Mean, step.StdErr)} over {step.Visits} lines)");
            if (NextStep(state) is { } carried)
                StepDone(carried, SearchActions.MatchKey(carried.Key, offered) == step.Key);
            return step.Key;
        }

        // The plan's root: the last search of our turn (a main / battle decision, or a mid-chain one), and how many of our
        // answers since the anchor came before it.
        private (int Turn, MctsTree Tree, MctsNode Start, int From)? _planStart;

        private void SetPlanStart(int turn, MctsTree tree, MctsNode start)
        {
            if (!IsSearchTwin)
                _planStart = (turn, tree, start, _sinceAnchor.Count);
        }

        /// <summary>A tree key or a replay key, in one comparable form (zones dropped, picks as their cards).</summary>
        private static string NodeLoose(string key)
        {
            if (key is "yes" or "raw:01")
                return "yes";
            if (key is "no" or "raw:00")
                return "no";
            if (key.StartsWith("pick:"))
                return key;
            if (key.StartsWith("card:") || key.StartsWith("cards:"))
                return "pick:" + string.Join('+', key[(key.IndexOf(':') + 1)..].Split('+')
                    .Select(k => SearchActions.LooseKey("card:" + k)[5..]).OrderBy(k => k, StringComparer.Ordinal));
            return key == "cancel" ? "pick:" : SearchActions.LooseKey(key);
        }

        /// <summary>
        /// The plan's answer at its node among the real answers on offer: the best score among those with MinPlayLines+ lines (as at
        /// a main decision), else the most-visited. Null when none of its answers is on offer.
        /// </summary>
        private (string Key, double Mean, int Visits, double StdErr)? PlanNodeStep(MctsNode node, IEnumerable<string> offered)
        {
            var byLoose = offered.GroupBy(NodeLoose).ToDictionary(g => g.Key, g => g.First());
            List<(string Key, MctsNode Node)> children;
            lock (node.Lock)
                children = node.Children.Select(kv => (Key: NodeLoose(kv.Key), Node: kv.Value))
                    .Where(kv => kv.Node.Visits > 0 && byLoose.ContainsKey(kv.Key)).ToList();
            var tried = children.Select(c => (Key: byLoose[c.Key], Mean: c.Node.LineValue(Mcts.LineValueVisits), c.Node.Visits, c.Node.StdErr)).ToList();
            if (tried.Count == 0)
                return null;
            var sure = tried.Where(t => t.Visits >= StartLines).OrderByDescending(t => t.Mean).ToList();
            return sure.Count > 0 ? sure[0] : tried.OrderByDescending(t => t.Visits).First();
        }

        /// <summary>
        /// A pick of ours inside an effect in our turn (a search's card, a target, a cost) when something new was revealed since the plan
        /// and no chain window came to re-plan in: the replay search runs at the pick itself, before it's submitted (user, 2026-11-15:
        /// "surely in that point it can do the planning before submitting its selection"). Without a verdict, the original plan's pick
        /// stands while it's on offer. Null: no re-plan needed here (the plan's own pick, or the brain, answers).
        /// </summary>
        public async Task<IList<byte>?> RefinePickAsync(BotState state, object data, CancellationToken ct = default)
        {
            if (data is SelectCardData { SelectionHint: (int)DHint.ATOHAND, Minimum: 1, Maximum: 1, SelectableCards: { } offered } &&
                LastMainMove.Turn == _currentHeader.CurrentTurn && LastMainMove.Action == MainAction.Activate &&
                SearchHost.Is(LastMainMove.CardId, ReinforcementOfTheArmy) &&
                Array.FindIndex(offered, card => card.IsCode(NecroGardna)) is int gardna && gardna >= 0 &&
                CardRules.ReinforcementNeedsGardna(await OwnDeckContextAsync(_currentHeader,
                    offered: offered.Select(card => card.ID).ToArray())))
            {
                _trackedBot?.LogNote("Reinforcement of the Army: search Necro Gardna to give Dark Grepher a discard");
                return new[] { (byte)gardna };
            }
            // The next card of a sum Omega asks one card at a time (Machina Fortress's discard): the search chose the sum at its first card (the
            // engine asks it whole, Mcts.OptionsFor); the rest is completed by the brain, no new search.
            if (!IsSearchTwin && state == BotState.SelectUnselectCard && _checkpoints.Count >= 2 && _checkpoints[^1].Answer == null &&
                _checkpoints[^2] is { State: BotState.SelectUnselectCard, Answer: { Key: var prev } } && prev.StartsWith("pick:") && prev != "pick:")
            {
                _trackedBot?.LogNote("search: the next card of a sum the search chose at its first card: the brain completes it");
                return null;
            }
            // Their turn, or ours before any searched decision: the search from the turn's start (DeckBotBrain.TurnLog).
            if (!IsSearchTwin && _trackedBot != null && _ownTurnParity >= 0 && SearchWanted(_trackedBot) &&
                (!IsOwnTurn(_currentHeader) || _anchor is not { } searched || searched.Turn != _currentHeader.CurrentTurn))
            {
                try { return await TurnStartPickAsync(_trackedBot, state, data, ct); }
                catch (Exception ex)
                {
                    _trackedBot.LogNote($"search: their-turn pick search failed, the brain picks: {ex.Message}");
                    return null;
                }
            }
            if (IsSearchTwin || _trackedBot == null || !IsOwnTurn(_currentHeader) || !SearchWanted(_trackedBot) || _anchor is not { } anchor ||
                anchor.Turn != _currentHeader.CurrentTurn)
                return null;
            // A pick can come straight after a draw or their activation, with no board read since: read it now.
            try { ObserveReveals(await CurrentField.GetBotField()); }
            catch { /* the last read stands */ }
            bool news = NewsSincePlan();
            List<SearchActions.Pick> picks;
            switch (data)
            {
                case SelectCardData select when (select.SelectableCards?.Length ?? 0) > 0:
                    bool attackTarget = select.SelectionHint == (int)DHint.ATTACKTARGET;
                    picks = SearchActions.CardPicks(select.SelectableCards!, attackTarget ? Math.Max(1, select.Minimum) : select.Minimum, select.Maximum, false);
                    break;
                case SelectTributeData tribute when (tribute.SelectableCards?.Length ?? 0) > 0:
                    picks = SearchActions.CardPicks(tribute.SelectableCards!, tribute.Minimum, tribute.Maximum, false);
                    break;
                case SelectUnselectCardData unselect when (unselect.SelectableCards?.Length ?? 0) > 0:
                    picks = SearchActions.CardPicks(unselect.SelectableCards!, Math.Min(unselect.Minimum, 1), 1, unselect.Minimum == 0 || unselect.Cancelable);
                    break;
                default:
                    return null;
            }
            var options = picks.Select(p => p.Key).Distinct().ToList();
            if (options.Count < 2)
                return null;
            var bot = _trackedBot!;
            try
            {
                // The original plan's pick (made before the new card showed up): the fallback, and the move the search is measured against.
                var planNode = PlannedNode(state);
                var planStep = planNode is { } plan ? PlanNodeStep(plan.Node, options) : null;
                string? original = planStep?.Key;
                // Every pick is searched, the plan's node continued (user, 2026-11-18: "I thought every decision got a search? this is a decision.
                // it should have a search. otherwise we run into thin lines again"): the plan's pick is only the fallback.
                var view = await BuildSearchViewAsync(bot);
                bot.LogNote((news ? "search: MID-CHAIN replan at a pick (something new since the plan, no chain window to re-plan in)"
                                  : planNode != null ? $"search: pick: the plan's node is thin ({planStep?.Visits ?? 0} lines behind its best pick); searching on from it"
                                  : "search: pick: the plan never reached this choice (PLAN GAP); planning it now") + $"; replaying {_sinceAnchor.Count} answer(s)" +
                            (original != null ? $"; the original plan's pick: {picks.First(p => p.Key == original).Label}" : ""));
                var outcome = await MidChainSearchAsync(bot, anchor, view, options, original ?? options[0], news ? null : planNode, ct, state);
                _revealsSearched = _revealsSinceAnchor.Count;
                _deckSearched = view.OwnDeck.Count;
                _deckPickedSinceSearch = 0;
                var best = outcome == null ? null : BestByScore(outcome, original ?? options[0], options.ToHashSet());
                // An option the replayed lines never met: no verdict here; the plan's pick, or else the search from the turn's start.
                if (outcome != null && PickNotFullySearched(bot, outcome, picks,
                        original != null ? "the plan's pick kept" : "searching from the turn's start"))
                    best = null;
                // A re-search that beats the plan's pick only inside the scores' uncertainty keeps the plan's pick, and is flagged for review
                // (user, 2026-11-19: Plaguespreader's cost re-searched, Sirocco over the plan's Jar of Greed by 0.10 +/-0.6, and the plan's
                // Grepher discard of Sirocco was lost; "keeping a plan's choice *if* the re-search is within uncertainty ... flagging these as close calls").
                if (!news && original != null && best != null && best.Key != original && outcome!.Stat(original) is { } planned)
                {
                    double margin = 2 * Math.Sqrt(best.StdErr * best.StdErr + planned.StdErr * planned.StdErr);
                    if (best.Mean - planned.Mean <= margin)
                    {
                        string label(RootStat s) => picks.FirstOrDefault(p => p.Key == s.Key)?.Label ?? s.Label;
                        string note = $"search: CLOSE CALL at a pick: {label(best)} ({BoardEvaluator.CuPm(best.Mean, best.StdErr)} over {best.Visits} lines) " +
                                      $"over the plan's {label(planned)} ({BoardEvaluator.CuPm(planned.Mean, planned.StdErr)} over {planned.Visits} lines): " +
                                      $"{BoardEvaluator.CardUnits(best.Mean) - BoardEvaluator.CardUnits(planned.Mean):0.00} card units better, " +
                                      $"inside the uncertainty (+/-{BoardEvaluator.CuErr(best.Mean, margin)}): the plan's pick kept";
                        bot.LogNote(note);
                        // Two copies of the same card (two Fluff Tokens) read the same: nothing to review (user, 2026-11-25: "yes" to not flagging them).
                        // Face-down cards (id 0) aren't known copies, so they're still flagged.
                        static string? CopyOf(string key) => key.StartsWith("card:") && key.Split('@')[0] is var id && id != "card:0" && !id.Contains('+') ? id : null;
                        if (CopyOf(best.Key) is not { } copy || copy != CopyOf(planned.Key))
                            FlagForReview(bot, "close-calls.jsonl", note);
                        best = planned;
                    }
                }
                string? chosen = best?.Key ?? original;
                if (chosen == null || chosen == "cancel")
                {
                    // No verdict and no plan (a REPLAY MISS or a PLAN GAP): from the turn's start instead (plan step 4).
                    bot.LogNote("search: no verdict at the pick and no plan for it; searching from the turn's start");
                    return await TurnStartPickAsync(bot, state, data, ct);
                }
                var pick = picks.First(p => p.Key == chosen);
                bot.LogNote($"search: {pick.Label} " + (best != null
                    ? $"(score {BoardEvaluator.CuPm(best.Mean, best.StdErr)} over {best.Visits} lines{(original != null && original != chosen ? ", over the original plan's pick" : "")})"
                    : "(the original plan's pick: the search had no verdict)"));
                return pick.Indexes.Select(i => (byte)i).ToList();
            }
            catch (Exception ex)
            {
                bot.LogNote($"search: re-plan at the pick failed ({ex.Message})");
                return null;
            }
        }

        private static string DescribeChainKey(SelectChainData chain, string key)
        {
            int i = ChainIndexOf(chain, key);
            return i == -1 ? "don't chain" : i >= 0 && chain.SelectableCards![i].ID > 0 ? $"chain {CardNames.Get(chain.SelectableCards[i].ID)}" : key;
        }

        /// <summary>The replay search at a chain window, thinking on while it's unsure (as at a main decision).</summary>
        private async Task<SearchOutcome?> MidChainSearchAsync(DuelBotWrapper bot, (int Turn, SearchView View, BotState State) anchor, SearchView now,
            List<string> options, string brainKey, (MctsTree Tree, MctsNode Node)? planned, CancellationToken ct, BotState rootState = BotState.SelectChain)
        {
            // The anchor's board with what we've learnt since: the cards that left our deck on top of it (milled first, in the
            // order they lie in our graveyard, then banished, then drawn or searched), their revealed cards where they were.
            var view = anchor.View.Clone();
            view.Record = await BuildTurnRecordAsync();
            var left = anchor.View.OwnDeck.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            foreach (int id in now.OwnDeck)
                if (left.TryGetValue(id, out int n) && n > 0)
                    left[id] = n - 1;
            var top = new List<int>();
            foreach (int id in now.OwnGrave.AsEnumerable().Reverse().Concat(now.OwnBanished).Concat(now.OwnHand)
                         .Concat(now.OwnMonsters.Select(c => c.Code)).Concat(now.OwnSpells.Select(c => c.Code)))
                if (left.TryGetValue(id, out int n) && n > 0)
                {
                    left[id] = n - 1;
                    top.Add(id);
                }
            view.KnownTop.Clear();
            view.KnownTop.AddRange(top);
            // Cards that came from our deck to our hand by a draw (not one we picked out of the deck ourselves): the replay's draws
            // take them even after a shuffle (Pot of Avarice).
            var searched = _sinceAnchor.Where(s => s.Key.StartsWith("pick:")).SelectMany(s => s.Key[5..].Split('+'))
                .Where(k => k.EndsWith(".deck") && int.TryParse(k.Split('@')[0], out _)).Select(k => int.Parse(k.Split('@')[0])).ToList();
            view.ForcedDraws.Clear();
            var fromDeck = anchor.View.OwnDeck.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            foreach (int id in now.OwnDeck)
                if (fromDeck.TryGetValue(id, out int n) && n > 0)
                    fromDeck[id] = n - 1;
            static int CanonOf(int id) => SearchHost.CanonicalId(id) is > 0 and var c ? c : id;
            bool drewSinceAnchor = _sinceAnchor.Any(s => !s.Key.StartsWith("pick:") && !s.Key.StartsWith("card") &&
                                                         SearchActions.KeyCardId(s.Key) is > 0 and var played && LineTally.Draws(played));
            // Our graveyard cards at the anchor that have left it since.
            var graveLeft = anchor.View.OwnGrave.GroupBy(CanonOf).ToDictionary(g => g.Key, g => g.Count());
            foreach (int id in now.OwnGrave)
                if (graveLeft.TryGetValue(CanonOf(id), out int stillThere) && stillThere > 0)
                    graveLeft[CanonOf(id)] = stillThere - 1;
            var handBefore = anchor.View.OwnHand.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            foreach (int id in now.OwnHand)
            {
                if (handBefore.TryGetValue(id, out int had) && had > 0)
                {
                    handBefore[id] = had - 1;
                    continue;
                }
                int canon = SearchHost.CanonicalId(id) is > 0 and var official ? official : id;
                if (searched.Remove(id) || searched.Remove(canon))
                    continue;
                if (fromDeck.TryGetValue(id, out int deckLeft) && deckLeft > 0)
                {
                    fromDeck[id] = deckLeft - 1;
                    view.ForcedDraws.Add(id);
                }
                // A draw card that shuffles its own cards in first (Pot of Avarice: five monsters from the graveyard, then two drawn): a card
                // that was in our graveyard at the anchor (live 2026-11-25, Machina #98: Avarice drew Ryko and Card Trooper out of the five it
                // returned; the lines drew at random and none reached the window).
                else if (drewSinceAnchor && graveLeft.TryGetValue(canon, out int inGrave) && inGrave > 0)
                {
                    graveLeft[canon] = inGrave - 1;
                    view.ForcedDraws.Add(id);
                }
            }
            foreach (var reveal in _revealsSinceAnchor)
            {
                if (reveal.FromHand)
                {
                    view.EnemyKnownHand.Add(reveal.Code);
                    continue;
                }
                var zone = reveal.Monster ? view.EnemyMonsters : view.EnemySpells;
                int at = zone.FindIndex(c => c.Sequence == reveal.Zone);
                if (at >= 0)
                    zone[at] = zone[at] with { Code = reveal.Code };
            }

            string target = DecisionKey.Of(rootState, now, options);
            var script = new ReplayScript(anchor.State, _sinceAnchor.ToList(), _revealsSinceAnchor.ToList(), target)
            {
                OurHandNow = now.OwnHand.ToList(),
                OurFieldNow = now.OwnMonsters.Select(c => ((int)CardLocation.MonsterZone, c.Sequence, c.Code))
                    .Concat(now.OwnSpells.Select(c => ((int)CardLocation.SpellZone, c.Sequence, c.Code))).ToHashSet(),
            };
            var tree = planned?.Tree ?? (_turnTree is { } own && own.Turn == anchor.Turn ? own.Tree : new MctsTree { Label = "this turn" });
            _turnTree = (anchor.Turn, tree);
            var start = planned?.Node ?? new MctsNode();
            SetPlanStart(anchor.Turn, tree, start); // the plan from here on is this search's
            var holds = HoldsFor(options);
            await RefreshStopOddsAsync();
            var evaluator = MakeEvaluator(now);
            var used = _activatedThisTurn.ToArray();

            SearchOutcome? outcome = null;
            int extra = 0;
            int misses = 0;
            string? firstMiss = null;
            var session = Session;
            session?.Pause();
            try
            {
                outcome = await ThinkUntilSureAsync(bot, async budgetOrNull =>
                {
                    int budget = budgetOrNull ?? SearchBudgetMs;
                    var settings = new SearchSettings(BudgetMs: budget, Threads: DefaultSearchThreads, Horizon: DefaultSearchHorizon,
                        FocusRoots: _focusRoot?.Keys, FocusLines: _focusRoot?.Lines ?? 0);
                    var search = new Mcts(view, tree, start, rootState, brainKey, used, settings, evaluator, SearchBrains(bot), holds)
                    {
                        MayChoose = PlanMayChoose, Suggestions = PlanSuggestions, Replay = script, UsedEffects = _usedEffects.ToArray(),
                        LiveRootKeys = options,
                    };
                    int seed = Interlocked.Increment(ref _searchSeed);
                    var more = await Task.Run(() => search.Run(seed, ct), ct);
                    misses += search.ReplayMisses;
                    firstMiss ??= search.FirstReplayMiss;
                    return more;
                }, brainKey, options.ToHashSet(), rootState);
            }
            finally
            {
                session?.Resume();
            }
            if (extra > 0)
                bot.LogNote($"search: unsure, thought {extra / 1000.0:0.#} s longer");
            if (misses > 0)
            {
                string missNote = $"search: REPLAY MISS: {misses} line(s) didn't reach this chain window from the last searched decision " +
                                  $"(first: {firstMiss}){(outcome?.Root.Count == 0 ? "; no line reached it, the brain answers" : "")}";
                // The replan's input, for DuelSim replay-check (user, 2026-11-25: "yes save mid chain replans too").
                string? saved = SaveMidChainDump(bot, missNote, anchor.Turn, rootState, options,
                    ReplayDump.MidChainOf(view, script, used, _usedEffects, brainKey));
                FlagReplayIssue(bot, missNote + (saved != null ? $" [replay saved: {saved}]" : ""));
            }
            return outcome is { Root.Count: > 0 } ? outcome with { NeverOffered = NeverOffered(start, options) } : null;
        }

        // Our zones as our turn began (zone -> code, position): a card that's different now arrived or changed this turn.
        private int _ownZonesTurn = -1;
        private readonly Dictionary<(bool Monster, int Zone), (int Code, uint Position)> _ownZonesAtTurnStart = new();
        // Their face-up monsters (code, position) at the same moment: a monster borrowed from them this turn, unmoved, isn't "this turn" (OwnCardThisTurn).
        private readonly HashSet<(int Code, uint Position)> _theirMonstersAtTurnStart = new();
        // Both graveyards and banished piles as our turn began (ours, theirs).
        private (List<int> Ours, List<int> Theirs) _pilesAtTurnStart = (new(), new());

        /// <summary>Cards in these piles now that weren't there as our turn began (by id): they got there this turn.</summary>
        private void FillArrivedThisTurn(SearchView view)
        {
            if (_ownZonesTurn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return;
            static void Fill(Dictionary<int, int> into, IEnumerable<int> now, List<int> before)
            {
                var old = before.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                foreach (int id in now)
                {
                    if (old.TryGetValue(id, out int n) && n > 0)
                        old[id] = n - 1;
                    else
                        into[id] = into.GetValueOrDefault(id) + 1;
                }
            }
            Fill(view.ArrivedThisTurn, view.OwnGrave.Concat(view.OwnBanished), _pilesAtTurnStart.Ours);
            Fill(view.EnemyArrivedThisTurn, view.EnemyGrave.Concat(view.EnemyBanished), _pilesAtTurnStart.Theirs);
        }

        private static readonly System.Text.RegularExpressions.Regex NoAttackAfterEffect = new(
            @"cannot attack (during )?the turn (you activate|this effect)|cannot attack this turn", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Our monster used its own effect this turn and that effect stops it attacking (Chaos Sorcerer: "This card cannot
        /// attack the turn you activate this effect"): the rebuilt turn must not let it attack (user, 2026-11-15).</summary>
        private async Task<bool> CannotAttackAfterOwnEffectAsync(DynamicCard card) =>
            card.ID > 0 && _activatedThisTurn.Contains(card.ID) && await CardDatabase.GetAsync(card.ID) is DBCardInfo info &&
            NoAttackAfterEffect.IsMatch(info.CardText ?? "");

        /// <summary>At the first board read of our turn: our monster and spell/trap zones, for ViewCard.ThisTurn.</summary>
        private async Task SnapshotOwnZonesAsync(BotStateHeader header)
        {
            if (IsSearchTwin || !IsOwnTurn(header) || _ownZonesTurn == header.CurrentTurn)
                return;
            _ownZonesTurn = header.CurrentTurn;
            _ownZonesAtTurnStart.Clear();
            foreach (var card in await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, true))
                _ownZonesAtTurnStart[(true, card.CurrentCoordinates.Sequence)] = (card.ID, card.Position);
            foreach (var card in await CurrentField.GetBotCardsInZone(CardLocation.SpellZone, true))
                _ownZonesAtTurnStart[(false, card.CurrentCoordinates.Sequence)] = (card.ID, card.Position);
            _theirMonstersAtTurnStart.Clear();
            foreach (var card in await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, false))
                if (card.ID > 0 && card.IsFaceup())
                    _theirMonstersAtTurnStart.Add((card.ID, card.Position));
            var field = await CurrentField.GetBotField();
            _pilesAtTurnStart = (
                (field.OwnSide.Grave ?? Array.Empty<int>()).Concat(field.OwnSide.Banish ?? Array.Empty<int>()).Where(id => id > 0).ToList(),
                (field.EnemySide.Grave ?? Array.Empty<int>()).Concat(field.EnemySide.Banish ?? Array.Empty<int>()).Where(id => id > 0).ToList());
        }

        /// <summary>Whether our card in this zone arrived, or (a monster) changed position, since our turn began.</summary>
        private bool OwnCardThisTurn(bool monster, DynamicCard card)
        {
            if (_ownZonesTurn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return false;
            if (!_ownZonesAtTurnStart.TryGetValue((monster, card.CurrentCoordinates.Sequence), out var before) || before.Code != card.ID)
                // Theirs, borrowed this turn (Brain Control and the like) and still in the position it had on their field: taking control
                // isn't a summon, so it may change position, and the engine keeps its old turn id (it moves between zones without being added
                // again). Marked "this turn", the rebuild locked its position (user, 2026-11-26: Brain Control's Lyla and Sirocco, offered by
                // the real game in no line, held two decisions to the 30 s limit).
                return !(monster && card.Owner == 1 && _activatedThisTurn.Overlaps(TemporaryControlIds) &&
                         _theirMonstersAtTurnStart.Contains((card.ID, card.Position)));
            return monster && before.Position != card.Position;
        }
    }
}
