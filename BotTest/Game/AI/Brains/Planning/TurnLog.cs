using BotTest.Game.AI.Search;
using DuelSim.Engine;
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
    /// The turn as it really happened, for the search to replay from its start (plan "the search makes every decision", step 1;
    /// user, 2026-11-18: "it should rebuild from the start of my turn and play my turn through exactly as it was to the current point"):
    /// the board as the turn began (taken on the first poll that shows the new turn, before anything else is answered), then one
    /// checkpoint per prompt of ours this turn, whoever's turn it is: the prompt, the board as we saw it, and the answer we sent.
    /// Step 1 only records and logs them (to check they're complete); the replay comes next.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>The board as a turn began. BeforeDraw: taken before the turn player drew (the header still showed the last turn's
        /// End Phase, or no phase on turn 1); otherwise Phase = the phase it was taken in, and the draw may be in the hand already.</summary>
        internal sealed record TurnStartSnapshot(int Turn, bool BeforeDraw, int Phase, SearchView View);

        /// <summary>Whose turn: from the parity our main-phase prompts set (unknown on turn 1 until we get one, or it's theirs).</summary>
        private string TurnOwnerWord(int turn) => _ownTurnParity < 0 ? "whose: not known yet" : turn % 2 == _ownTurnParity ? "ours" : "theirs";

        /// <summary>One prompt of ours this turn: what it was, the board then, and the answer sent (null until it's sent).</summary>
        internal sealed class TurnCheckpoint
        {
            public required BotState State { get; init; }
            public required int Phase { get; init; }
            public required SearchView View { get; init; }
            public required int Generation { get; init; }
            public ReplayStep? Answer { get; set; }
        }

        private TurnStartSnapshot? _turnStart;
        private TagRules.BotStrategicState? _lastLoggedStrategicState;
        private readonly ThreatenedState _threatenedState = new();
        private readonly List<TurnCheckpoint> _checkpoints = new();

        /// <summary>The wrapper's new-turn callback (IDecisionRefiner.TurnStartedAsync, from the adapter as soon as the bot exists, so
        /// turn 1 is seen too): logs the last turn's record, then takes this turn's snapshot.</summary>
        public async Task TurnStartedAsync(DuelBotWrapper bot, BotStateHeader header)
        {
            if (IsSearchTwin)
                return;
            if (_trackedBot != bot || _gameField == null)
                AttachToBot(bot);
            if (_turnStart is { } last && last.Turn < header.CurrentTurn)
            {
                bot.LogNote($"turn log: turn {last.Turn} ({TurnOwnerWord(last.Turn)}): snapshot taken {SnapshotWhen(last)}, " +
                            $"{_checkpoints.Count} prompt(s) of ours recorded ({string.Join(", ", _checkpoints.GroupBy(c => c.State).Select(g => $"{g.Count()} {g.Key}"))}), " +
                            $"{_checkpoints.Count(c => c.Answer != null)} with the answer we sent");
                if (ReplayAudit)
                    AuditTurn(bot, last, _checkpoints.ToList());
            }
            _currentHeader = header;
            // Our hand / their hand size as last seen (the last snapshot or prompt): what this turn's draw is measured against (UndoDraw).
            var lastHand = _lastSeenOwnHand?.ToList();
            int lastTheirHand = _lastSeenTheirHand;
            _checkpoints.Clear();
            SearchView view;
            try { view = await BuildSearchViewAsync(bot); }
            catch (Exception ex)
            {
                _turnStart = null;
                bot.LogNote($"turn log: no snapshot for turn {header.CurrentTurn} ({ex.Message})");
                return;
            }
            // The first poll of a new turn still shows the last turn's End Phase (or no phase on turn 1): nothing drawn yet.
            // Turn 1 has no draw at all, so its snapshot is always a "before the draw" one.
            bool beforeDraw = header.CurrentTurn == 1 || header.CurrentPhase == 0 || (DuelPhase)header.CurrentPhase == DuelPhase.End;
            // Live Omega's first view of a turn is usually its Draw Phase, the card already drawn (seen 2026-11-18: 117 live decisions had no
            // turn-start replay). The draw is taken back out (the plan's late snapshot), so the rebuilt turn draws it again.
            if (!beforeDraw && (DuelPhase)header.CurrentPhase == DuelPhase.Draw)
            {
                bool oursNow = header.CurrentPlayer == 0;
                if (_ownTurnParity < 0)
                    _ownTurnParity = oursNow ? header.CurrentTurn % 2 : 1 - header.CurrentTurn % 2;
                (_lastSeenOwnHand, _lastSeenTheirHand) = (view.OwnHand.ToList(), view.EnemyHandCount);
                bot.LogNote($"turn log: turn {header.CurrentTurn}: snapshot in the Draw Phase; {UndoDraw(view, oursNow, lastHand, lastTheirHand)}");
                beforeDraw = true;
            }
            if ((DuelPhase)header.CurrentPhase != DuelPhase.Draw)
                (_lastSeenOwnHand, _lastSeenTheirHand) = (view.OwnHand.ToList(), view.EnemyHandCount);
            _turnStart = new TurnStartSnapshot(header.CurrentTurn, beforeDraw, header.CurrentPhase, view);
            bot.LogNote($"turn log: turn {header.CurrentTurn} ({TurnOwnerWord(header.CurrentTurn)}) starts; snapshot {SnapshotWhen(_turnStart)}: " +
                        $"our hand {view.OwnHand.Count}, field {view.OwnMonsters.Count}+{view.OwnSpells.Count}; " +
                        $"their hand {view.EnemyHandCount}, field {view.EnemyMonsters.Count}+{view.EnemySpells.Count}; LP {view.LifePoints[0]} / {view.LifePoints[1]}");
        }

        /// <summary>
        /// A Draw Phase snapshot made into one from before the draw: their hand one card smaller on their turn; on ours, the card our hand
        /// gained since our last prompt (else the newest hand card) back into our deck, where the replay finds it as the turn's draw.
        /// </summary>
        private static string UndoDraw(SearchView view, bool ours, List<int>? lastHand, int lastTheirHand)
        {
            // Omega's first view of the Draw Phase can come before the draw itself (seen 2026-11-18 live: the snapshot still had 5 cards, a card
            // not drawn was taken back, and every replay of the turn was a card short): only a hand that grew since we last saw it has drawn.
            if (!ours)
            {
                if (lastTheirHand >= 0 && view.EnemyHandCount <= lastTheirHand)
                    return "their hand hasn't grown since we last saw it: not drawn yet";
                view.EnemyHandCount = Math.Max(0, view.EnemyHandCount - 1);
                return "their draw taken back (their hand one smaller)";
            }
            if (view.OwnHand.Count == 0)
                return "our hand is empty: nothing to take back";
            int drawn = view.OwnHand[^1];
            if (lastHand != null)
            {
                var left = lastHand.ToList();
                var gained = view.OwnHand.Where(id => !left.Remove(id)).ToList();
                if (gained.Count == 0)
                    return "our hand hasn't grown since we last saw it: not drawn yet";
                if (gained.Count == 1)
                    drawn = gained[0];
            }
            view.OwnHand.Remove(drawn);
            view.OwnDeck.Add(drawn);
            return $"our draw ({OmegaBotWrapper.Logging.CardNames.Get(drawn)}) taken back into the deck";
        }

        /// <summary>
        /// At every prompt of ours, before anything answers it (the wrapper's PromptArrived: the brain, a plan's step, a replay alike; seen
        /// 2026-11-18: Gearframe's trigger and search, answered by "following its line", were missing from the record): a checkpoint, once
        /// per prompt (the wrapper's field generation; a prompt still unanswered isn't recorded twice).
        /// </summary>
        public async Task PromptArrivedAsync(DuelBotWrapper bot, BotStateHeader header, BotState state)
        {
            if (!IsSearchTwin)
            {
                if (_trackedBot != bot || _gameField == null)
                    AttachToBot(bot);
                (int Threats, int Answers)? coverage = null;
                try { coverage = await AssessStrategicStateAsync(bot); }
                catch (Exception ex) { bot.LogNote($"strategic state assessment failed: {ex.Message}"); }
                var strategicState = StrategicState;
                string detail = coverage is { } counts ? $" ({counts.Answers}/{counts.Threats} threats covered)" : "";
                bot.LogNote($"strategic state: {TagRules.DescribeStrategicState(strategicState, _lastLoggedStrategicState)} at {state}{detail}");
                _lastLoggedStrategicState = strategicState;

                try { await TrackTemporaryMonstersAsync(); }
                catch { /* tracking is best effort */ }
                try { await TrackTheirAttackersAsync(header); }
                catch { /* tracking is best effort */ }
                try { await TrackLylasAsync(header.CurrentTurn); }
                catch { /* tracking is best effort */ }
                try { await TrackMain2SummonsAsync(header); }
                catch { /* tracking is best effort */ }
            }
            if (IsSearchTwin || _turnStart is not { } start || start.Turn != header.CurrentTurn)
                return;
            int generation = bot.FieldGeneration;
            if (_checkpoints.Count > 0 && (_checkpoints[^1].Generation == generation || _checkpoints[^1].Answer == null && _checkpoints[^1].State == state))
                return;
            _currentHeader = header;
            SearchView view;
            try { view = await BuildSearchViewAsync(bot); }
            catch { return; }
            _checkpoints.Add(new TurnCheckpoint { State = state, Phase = header.CurrentPhase, View = view, Generation = generation });
            (_lastSeenOwnHand, _lastSeenTheirHand) = (view.OwnHand.ToList(), view.EnemyHandCount);
        }

        private async Task<(int Threats, int Answers)> AssessStrategicStateAsync(DuelBotWrapper bot)
        {
            var field = await CurrentField.GetBotField();
            var own = field.OwnSide;
            var hand = own.Hand ?? Array.Empty<int>();
            var monsters = await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, true);
            var spells = await CurrentField.GetBotCardsInZone(CardLocation.SpellZone, true);
            var enemy = await CurrentField.GetBotCardsInZone(CardLocation.MonsterZone, false);
            var seen = hand.Concat(own.Grave ?? Array.Empty<int>()).Concat(own.Banish ?? Array.Empty<int>())
                .Concat(monsters.Concat(spells).Where(card => card.Owner == 0).Select(card => card.ID));
            var deck = SearchHost.Available && enemy.Count > 0 ? RemainingOwnDeck(bot, seen) : new List<int>();
            StrategicCard Convert(DynamicCard card) => new(card.ID, card.CurrentCoordinates.Sequence,
                card.Attack, card.Defense, card.IsFaceup(), card.IsAttack(), card.IsDisabled());
            var result = _threatenedState.Evaluate(deck, hand, monsters.Select(Convert).ToArray(),
                spells.Select(Convert).ToArray(), enemy.Select(Convert).ToArray(),
                field.EnemySide.Hand?.Length ?? 0, own.LifePoints, field.EnemySide.LifePoints);
            _assessedStrategicState = result.State;
            return (result.Threats.Count, result.Answers);
        }

        // ------------------------------------------------------------------ temporary monsters (user, 2026-11-18)

        // Sirocco the Dawn activated this turn (its target pick still to come), and its target: our monster zone that alone may attack.
        private int _siroccoPending = -1;
        private (int Turn, int Zone) _siroccoTarget = (-1, -1);
        // Our monster zones holding a Jinzo revived by Jinzo - Returner (destroyed in our next End Phase by Returner's effect).
        private readonly HashSet<int> _returnerJinzoZones = new();
        // Returner was just used: our Jinzo zones before it resolved (the new one is Returner's).
        private HashSet<int>? _jinzoZonesBeforeReturner;

        /// <summary>A Jinzo our Jinzo - Returner revived: it dies in our next End Phase whatever happens (a temporary monster).</summary>
        protected bool IsReturnerJinzo(DynamicCard card) =>
            card.CurrentCoordinates.Controller == 0 && card.IsCode(Jinzo) && _returnerJinzoZones.Contains(card.CurrentCoordinates.Sequence);

        /// <summary>
        /// A monster of ours that leaves soon whatever we do (user, 2026-11-18: "laquari was summoned from darius. that should put it in the
        /// category of 'temporary' monster like the jinzo's from returner ... don't try and protect those kinds of monsters"): Returner's Jinzo,
        /// and the monster our face-up Darius revived (its card target: back to the deck when Darius leaves). Counted as expendable.
        /// </summary>
        protected bool IsTemporaryMonster(DynamicCard card) =>
            IsReturnerJinzo(card) ||
            // Theirs, borrowed until the End Phase (Brain Control, Change of Heart, Mind Control): expendable (user, 2026-11-18: "give all cards
            // borrowed from me the expendable tag (if they're borrowed, not if they're taken for good like goyo guardian)").
            card.CurrentCoordinates.Controller == 0 && card.Owner == 1 && _activatedThisTurn.Overlaps(TemporaryControlIds) ||
            _ownMonsters.Any(darius => darius.IsCode(CardId.GladiatorBeastDarius) && darius.IsFaceup() &&
                                       (darius.TargetCards ?? Array.Empty<CardCoordinate>()).Any(t => t.Location == (byte)CardLocation.MonsterZone &&
                                           t.Controller == 0 && t.Sequence == card.CurrentCoordinates.Sequence));

        // Our hand and their hand size as last seen (a snapshot or a prompt of ours; -1 / null: not yet): UndoDraw's evidence of a draw.
        private List<int>? _lastSeenOwnHand;
        private int _lastSeenTheirHand = -1;

        // Face-up Lylas (controller, zone): in attack position at the last prompt, and the real turn whose End Phase ends her position lock
        // (0: none). Her effect turns her to defence, then "this card cannot change its battle position until the end of your next turn".
        private readonly Dictionary<(int Controller, int Zone), (bool Attack, int LockEnds)> _lylas = new();
        private static readonly int[] LylaIds = { LylaLightswornSorceress, LylaLightswornSorceressPreErrata }; // official, pre-errata

        /// <summary>At each prompt: a Lyla gone from attack to defence used her effect (locked until the end of her controller's next turn).</summary>
        private async Task TrackLylasAsync(int turn)
        {
            var seen = new HashSet<(int, int)>();
            var cards = (await GetBotMonsterCardsAsync(onlyFaceUp: true)).Concat(await GetEnemyMonsterCardsAsync(onlyFaceUp: true));
            foreach (var card in cards.Where(c => c.IsFaceup() && c.IsCode(LylaIds)))
            {
                var key = ((int)card.CurrentCoordinates.Controller, (int)card.CurrentCoordinates.Sequence);
                seen.Add(key);
                bool attack = card.IsAttack();
                int lockEnds = 0;
                if (_lylas.TryGetValue(key, out var was))
                {
                    lockEnds = was.Attack && !attack ? turn + 2 : was.LockEnds;
                    if (lockEnds > 0 && turn > lockEnds)
                        lockEnds = 0;
                }
                _lylas[key] = (attack, lockEnds);
            }
            foreach (var gone in _lylas.Keys.Where(k => !seen.Contains(k)).ToList())
                _lylas.Remove(gone);
        }

        // Our monsters at the last prompt (by card), and those that turned up in this turn's Main Phase 2 (SearchView.Main2Summoned).
        private List<int>? _ownMonsterCodes;
        private (int Turn, List<int> Codes) _main2Summons = (-1, new List<int>());

        /// <summary>At each prompt of our turn: a monster of ours that turned up since the last prompt, in Main Phase 2, is a Main Phase 2 summon.</summary>
        private async Task TrackMain2SummonsAsync(BotStateHeader header)
        {
            if (!IsOwnTurn(header))
            {
                _ownMonsterCodes = null;
                return;
            }
            var now = (await GetBotMonsterCardsAsync(onlyFaceUp: true)).Where(c => c.ID > 0).Select(c => c.ID).ToList();
            if (_main2Summons.Turn != header.CurrentTurn)
                _main2Summons = (header.CurrentTurn, new List<int>());
            // (The duel's first turn counts too: no battle possible; user, 2026-11-18.)
            if (((DuelPhase)header.CurrentPhase == DuelPhase.Main2 || header.CurrentTurn == 1 && (DuelPhase)header.CurrentPhase == DuelPhase.Main1) &&
                _ownMonsterCodes != null)
            {
                var before = _ownMonsterCodes.ToList();
                _main2Summons.Codes.AddRange(now.Where(c => !before.Remove(c)));
            }
            _ownMonsterCodes = now;
        }

        /// <summary>The real turn whose End Phase ends this monster's position lock (Lyla's), or 0.</summary>
        protected int PositionLockEnds(DynamicCard card) =>
            _lylas.TryGetValue(((int)card.CurrentCoordinates.Controller, (int)card.CurrentCoordinates.Sequence), out var l) && card.IsCode(LylaIds) ? l.LockEnds : 0;

        /// <summary>At each prompt: Returner's Jinzo found after Returner was used, and zones no longer holding a Jinzo forgotten.</summary>
        private async Task TrackTemporaryMonstersAsync()
        {
            if (_returnerJinzoZones.Count == 0 && _jinzoZonesBeforeReturner == null)
                return;
            var jinzos = (await GetBotMonsterCardsAsync(onlyFaceUp: true)).Where(m => m.IsCode(Jinzo)).Select(m => (int)m.CurrentCoordinates.Sequence).ToHashSet();
            _returnerJinzoZones.IntersectWith(jinzos);
            if (_jinzoZonesBeforeReturner is { } before && jinzos.Except(before).ToList() is { Count: > 0 } revived)
            {
                _returnerJinzoZones.UnionWith(revived);
                _jinzoZonesBeforeReturner = null;
            }
        }

        /// <summary>The answer we sent at the current prompt (from ResponseSent): onto its checkpoint.</summary>
        private void RecordCheckpointAnswer(BotState state, object? data, byte[] response)
        {
            // Jinzo - Returner used: the Jinzo that turns up next is its temporary one.
            if (!IsSearchTwin && ReplayKeys.Of(state, data, response) is { } step && step.Key.Contains(JinzoReturner.ToString()) &&
                !step.Key.StartsWith("pick:"))
                _jinzoZonesBeforeReturner = _ownMonsters.Where(m => m.IsCode(Jinzo) && m.IsFaceup()).Select(m => (int)m.CurrentCoordinates.Sequence).ToHashSet();
            // Sirocco the Dawn used: its target (our next pick of our own monster) alone may attack this turn (SearchView.OnlyAttackerZone).
            if (!IsSearchTwin && _siroccoPending == _currentHeader.CurrentTurn && data is SelectCardData siroccoPick && response.Length >= 2 &&
                siroccoPick.SelectableCards is { } offered && response[1] < offered.Length &&
                offered[response[1]].CurrentCoordinates is { Controller: 0, Location: (byte)CardLocation.MonsterZone } at)
            {
                _siroccoTarget = (_currentHeader.CurrentTurn, at.Sequence);
                _siroccoPending = -1;
            }
            if (!IsSearchTwin && ReplayKeys.Of(state, data, response) is { } siroccoUse && SearchActions.KeyCardId(siroccoUse.Key) == BlackwingSiroccoTheDawn &&
                (siroccoUse.Key.StartsWith($"{(int)MainAction.Activate}:") || siroccoUse.Key.StartsWith("chain:")))
                _siroccoPending = _currentHeader.CurrentTurn;
            // Every activation we send counts as activated this turn, whoever chose it (the search's too: Brain Control's borrow check).
            if (!IsSearchTwin && ReplayKeys.Of(state, data, response) is { } sent &&
                (sent.Key.StartsWith($"{(int)MainAction.Activate}:") || sent.Key.StartsWith("chain:")) && SearchActions.KeyCardId(sent.Key) is > 0 and var activated)
                MarkCardActivated(activated);
            if (IsSearchTwin || _checkpoints.Count == 0 || _checkpoints[^1].Answer != null || _checkpoints[^1].State != state)
                return;
            _checkpoints[^1].Answer = ReplayKeys.Of(state, data, response);
        }

        /// <summary>
        /// The replay audit (plan step 2, DuelSim only: MatchRunner turns it on): as each turn ends, that turn is played again from its
        /// snapshot in the engine (TurnReplay), several lines with their hidden cards dealt afresh, and the log says how many reached every
        /// checkpoint and where the others left the real game. The search's replays are built on this, so each miss is a bug to fix.
        /// </summary>
        public bool ReplayAudit { get; set; }

        /// <summary>Lines each turn's replay audit plays.</summary>
        public int ReplayAuditLines { get; set; } = 6;

        private void AuditTurn(DuelBotWrapper bot, TurnStartSnapshot start, List<TurnCheckpoint> checkpoints)
        {
            if (checkpoints.Count == 0 || !SearchHost.Available)
                return;
            if (!start.BeforeDraw)
            {
                bot.LogNote($"replay audit: turn {start.Turn} not replayed (the snapshot was taken after the draw)");
                return;
            }
            if (_ownTurnParity < 0)
                return;
            bool ours = start.Turn % 2 == _ownTurnParity;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var replay = TurnReplay.Prepare(start.View, ours, start.Turn,
                    checkpoints.Select(c => new ReplayCheckpoint(c.State, c.Phase, c.View, c.Answer)).ToList());
                SearchHost.Register(replay.Start.AllKnownIds().Concat(checkpoints.SelectMany(c => c.View.AllKnownIds())));
                var runs = new List<ReplayRun>();
                using (var engine = new PuzzleEngine(SearchHost.Cards, SearchHost.Scripts, start.Turn * 7919))
                {
                    var rng = new Random(start.Turn);
                    for (int i = 0; i < ReplayAuditLines; i++)
                        runs.Add(replay.Play(engine, rng));
                }
                int all = runs.Count(r => r.Miss == null);
                string misses = string.Join("; ", runs.Where(r => r.Miss != null).GroupBy(r => (r.MissAt, r.Miss))
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()}x at checkpoint {g.Key.MissAt + 1}/{checkpoints.Count} ({checkpoints[Math.Min(g.Key.MissAt, checkpoints.Count - 1)].State}): {g.Key.Miss}"));
                string note = $"replay audit: turn {start.Turn} ({(ours ? "ours" : "theirs")}, rebuilt as engine turn {replay.RebuiltTurn}): " +
                              $"{all}/{runs.Count} line(s) reached all {checkpoints.Count} checkpoints in {clock.ElapsedMilliseconds} ms" +
                              (misses.Length > 0 ? $"; misses: {misses}; {replay.DescribeStart()}" : "");
                if (all < runs.Count)
                    FlagReplayIssue(bot, note.Replace("replay audit:", "replay audit: REPLAY MISS:"));
                else
                    bot.LogNote(note);
            }
            catch (Exception ex)
            {
                FlagReplayIssue(bot, $"replay audit: REPLAY MISS: turn {start.Turn} failed ({ex.Message})");
            }
        }

        /// <summary>
        /// A chain window of ours in their turn, through the search (plan step 3; user, 2026-11-18: "the search should handle all decisions"):
        /// every line rebuilds their turn from its start and replays it as it really went to this window, then plays on to the end of our next
        /// turn (our draw there a blank card). Null: the brain's answer stands (no snapshot, one answer, or no line reached the window).
        /// </summary>
        private async Task<int?> TurnStartChainAsync(DecisionContext<SelectChainData> context, int brainIndex, CancellationToken ct)
        {
            var chain = context.StateData;
            var options = ChainOptionKeys(chain);
            if (options.Count < 2)
                return null;
            string brainKey = brainIndex >= 0 && brainIndex < (chain.SelectableCards?.Length ?? 0)
                ? $"chain:{SearchActions.CardKey(chain.SelectableCards![brainIndex])}:{(brainIndex < (chain.Descriptions?.Length ?? 0) ? chain.Descriptions![brainIndex] : 0)}"
                : chain.Forced ? options[0] : "pass";
            var outcome = await TurnStartSearchAsync(context.Bot, BotState.SelectChain, options, brainKey, ct);
            if (outcome == null)
                return null;
            var better = Better(context.Bot, outcome, brainKey, options.ToHashSet(), "chain", context.Header.CurrentTurn);
            return better == null ? null : ChainIndexOf(chain, better.Key) is var index and >= -1 ? index : null;
        }

        /// <summary>
        /// A yes/no or option prompt no plan answered (their turn, ours before any searched decision, or a PLAN GAP): the search from the turn's
        /// start (plan steps 3 and 4). Null: the brain answers.
        /// </summary>
        public async Task<string?> SearchedChoiceAsync(BotState state, IReadOnlyList<string> options, CancellationToken ct = default)
        {
            if (IsSearchTwin || _trackedBot is not { } bot || _ownTurnParity < 0 || options.Count < 2 || !SearchWanted(bot))
                return null;
            try
            {
                var list = options.ToList();
                var outcome = await TurnStartSearchAsync(bot, state, list, list[0], ct);
                var best = outcome == null ? null : BestByScore(outcome, list[0], list.ToHashSet());
                if (best == null)
                    return null;
                bot.LogNote($"search: {best.Key} (score {BoardEvaluator.CuPm(best.Mean, best.StdErr)} over {best.Visits} lines, from the turn's start)");
                if (state == BotState.SelectPosition && outcome != null)
                    LogBreakdowns(bot, outcome, best.Key, list.ToHashSet());
                return best.Key;
            }
            catch (Exception ex)
            {
                bot.LogNote($"search: {state} search from the turn's start failed, the brain answers: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A card pick of ours in their turn (a target, a cost, a card to take), through the search from the turn's start (plan step 3). Null:
        /// the brain picks (one answer, or no line reached the pick).
        /// </summary>
        private async Task<IList<byte>?> TurnStartPickAsync(DuelBotWrapper bot, BotState state, object data, CancellationToken ct)
        {
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
            var outcome = await TurnStartSearchAsync(bot, state, options, options[0], ct);
            var best = outcome == null ? null : BestByScore(outcome, options[0], options.ToHashSet());
            if (best == null || best.Key == "cancel")
                return null;
            var pick = picks.First(p => p.Key == best.Key);
            bot.LogNote($"search: {pick.Label} (score {BoardEvaluator.CuPm(best.Mean, best.StdErr)} over {best.Visits} lines, from the turn's start)");
            return pick.Indexes.Select(i => (byte)i).ToList();
        }

        /// <summary>
        /// The search from the start of the current turn at a prompt of ours (the turn's last checkpoint, not answered yet): the turn's replay
        /// (TurnReplay) is the way to the root. Null when there's no snapshot to start from, or no line reached the decision.
        /// </summary>
        private async Task<SearchOutcome?> TurnStartSearchAsync(DuelBotWrapper bot, BotState rootState, List<string> options, string brainKey, CancellationToken ct)
        {
            if (!SearchHost.Available || _turnStart is not { BeforeDraw: true } start || start.Turn != _currentHeader.CurrentTurn || _ownTurnParity < 0 ||
                _checkpoints.Count == 0 || _checkpoints[^1].Answer != null || !Mcts.SameKind(_checkpoints[^1].State, rootState))
            {
                FlagReplayIssue(bot, $"search: REPLAY MISS: no turn-start replay for this {rootState} (no snapshot from before the draw, or this prompt isn't recorded); the brain answers");
                return null;
            }
            bool ours = start.Turn % 2 == _ownTurnParity;
            var checkpoints = _checkpoints.Select(c => new ReplayCheckpoint(c.State, c.Phase, c.View, c.Answer)).ToList();
            var replay = TurnReplay.Prepare(start.View, ours, start.Turn, checkpoints);
            replay.Start.BlankNextDraw = !ours;
            var now = _checkpoints[^1].View;
            string rootKey = DecisionKey.Of(rootState, now, options);
            bot.LogNote($"search: from the turn's start: replaying {checkpoints.Count - 1} prompt(s) of ours this turn to this {rootState}, " +
                        $"lines to the end of {(ours ? "their next turn" : "our next turn (our draw a blank card)")}");
            // The turn's tree, reused while nothing new came out (user, 2026-11-18): the cards the replay deals are the same as when the tree was
            // built, so its earlier lines through this decision still hold; the search continues from this decision's node. Something new
            // (their card revealed, our deck's card seen): a fresh tree, as the old lines played on other guesses.
            string dealt = replay.DescribeStart();
            MctsTree tree;
            MctsNode root;
            if (_turnStartTree is { } kept && kept.Turn == start.Turn && kept.Dealt == dealt && kept.Tree.Find(rootKey) is { } known)
            {
                (tree, root) = (kept.Tree, known);
                bot.LogNote($"search: continuing this turn's tree at this decision ({known.Visits} line(s) already through it, nothing new revealed since)");
            }
            else
            {
                tree = new MctsTree { Label = ours ? "this turn" : "their turn" };
                root = new MctsNode();
                _turnStartTree = (start.Turn, dealt, tree);
            }
            var holds = HoldsFor(options);
            await RefreshStopOddsAsync();
            var evaluator = MakeEvaluator(now);
            int misses = 0;
            string? firstMiss = null;
            SearchOutcome? outcome;
            var session = Session;
            session?.Pause();
            try
            {
                outcome = await ThinkUntilSureAsync(bot, async budgetOrNull =>
                {
                    var settings = new SearchSettings(BudgetMs: budgetOrNull ?? SearchBudgetMs, Threads: DefaultSearchThreads, Horizon: DefaultSearchHorizon);
                    var search = new Mcts(replay.Start, tree, root, rootState, brainKey, Array.Empty<int>(), settings, evaluator, SearchBrains(bot), holds)
                    {
                        MayChoose = PlanMayChoose, Suggestions = PlanSuggestions, FromTurnStart = replay, RootKey = rootKey,
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
            if (misses > 0)
                FlagReplayIssue(bot, $"search: REPLAY MISS: {misses} line(s) didn't reach this {rootState} from the turn's start (first: {firstMiss})" +
                            (outcome is not { Root.Count: > 0 } ? "; no line reached it, the brain answers" : ""));
            return outcome is { Root.Count: > 0 } ? outcome : null;
        }

        // The tree of this turn's searches from the turn's start, and what the replay dealt when it was built (TurnReplay.DescribeStart).
        private (int Turn, string Dealt, MctsTree Tree)? _turnStartTree;

        private static readonly object ReplayIssuesLock = new();

        /// <summary>
        /// A replay issue (a REPLAY MISS: a line that couldn't follow the real game to a decision): noted in the duel log, and flagged in
        /// replay-issues.jsonl beside the duel logs (user, 2026-11-18: "every time the search hits a replay issue also flag it in a separate
        /// file"; reviewed and fixed with each comments review), with the log file and decision it belongs to.
        /// </summary>
        private void FlagReplayIssue(DuelBotWrapper bot, string note) => FlagForReview(bot, "replay-issues.jsonl", note);

        /// <summary>A note for the duel log, also written to a review file beside the duel logs (with the log file and decision it belongs to).</summary>
        private void FlagForReview(DuelBotWrapper bot, string file, string note)
        {
            bot.LogNote(note);
            try
            {
                var entry = new Dictionary<string, object?>
                {
                    ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["deck"] = bot.DeckName,
                    ["turn"] = _currentHeader.CurrentTurn,
                    ["log"] = bot.DuelLog?.CurrentLogPath,
                    // The decision being made: the log numbers it when it's answered, one after the last (seen 2026-11-18: every flag pointed at
                    // the decision before).
                    ["decision"] = bot.DuelLog?.DecisionCount + 1,
                    ["issue"] = note,
                };
                string line = System.Text.Json.JsonSerializer.Serialize(entry) + "\n";
                lock (ReplayIssuesLock)
                {
                    Directory.CreateDirectory(DuelLogOptions.Directory);
                    File.AppendAllText(Path.Combine(DuelLogOptions.Directory, file), line);
                }
            }
            catch { /* the flag file must never cost the bot its answer */ }
        }

        private static string SnapshotWhen(TurnStartSnapshot s) => s.BeforeDraw ? "before the draw" : $"in {PhaseWord(s.Phase)} (the draw may be in the hand)";

        private static string PhaseWord(int phase) => (DuelPhase)phase switch
        {
            DuelPhase.Draw => "the Draw Phase", DuelPhase.Standby => "the Standby Phase", DuelPhase.Main1 => "Main Phase 1",
            DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle => "the Battle Phase",
            DuelPhase.Main2 => "Main Phase 2", DuelPhase.End => "the End Phase", _ => $"phase 0x{phase:X}",
        };
    }
}
