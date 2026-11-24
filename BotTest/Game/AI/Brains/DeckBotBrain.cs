using BotTest.Game.Field;
using BotTest.Utils;
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
    /// Shared base for real deck brains. This file is the core: rule gates, tuning, and keeping up with the duel
    /// (PrepareContextAsync). The decisions live in folders, each listing its rules in order:
    ///  - Battle/BattlePhase.cs (rules in Battle/Rules), MainPhase/MainPhase.cs (MainPhase/Rules),
    ///  - Chains/ChainDecision.cs (Chains/Rules), Selection/CardSelection.cs (Selection/Rules);
    /// what we know about the duel is in Knowledge/, field reading in Core/, the shared staples in Staples/.
    /// </summary>
    public abstract partial class DeckBotBrain : DefaultBotBrain
    {
        // IsCode falls back to the card database's alias (Omega's ids, prompts without an alias; see Helpers.AliasResolver).
        static DeckBotBrain() => OmegaBotWrapper.Helpers.AliasResolver ??= Search.SearchHost.CanonicalId;

        private DuelBotWrapper? _trackedBot;

        /// <summary>
        /// Each rule's probability of firing when its conditions hold (default 1; see <see cref="RuleGates"/>). Rule
        /// names are declared in RuleSet classes (DeckRules, VayuRules, ...) so tuning can list them.
        /// </summary>
        public RuleGates Rules { get; } = new();

        /// <summary>
        /// Gate for one rule: true if it fires this time. Put it first in the rule's condition, e.g.
        /// <c>if (Rule(VayuRules.TrooperMill) &amp;&amp; ...)</c>. A rule skipped by its probability is noted in the duel log.
        /// </summary>
        protected bool Rule(string name)
        {
            bool fired = Rules.Roll(name, out double p);
            if (fired && p > 0)
                _decisionRules.Add(name); // for the play search verdict (which rules shaped this decision)
            if (!fired && p > 0)
                _trackedBot?.LogNote($"rule {name} skipped this time (fires {p:P0} of the time)");
            // In the duel log: which rules fired (or rolled not to) at each decision, for reading a duel.
            if (p > 0)
                _trackedBot?.LogNote($"rule-event|{name}|{(fired ? 1 : 0)}|{p:0.###}|{_currentHeader.CurrentTurn}");
            return fired;
        }

        private GameField? _gameField;
        private BotStateHeader _currentHeader;
        private int _trackedTurn = -1;
        private DynamicCard? _selectedDefenderCard;

        /// <summary>Opponent monsters seen face-up, so they're still known after being flipped face-down. Reset per duel.</summary>
        protected OpponentMemory OpponentMemory { get; } = new();

        /// <summary>Which Extra Deck monsters weren't properly summoned (they can't be revived or taken by Goyo). Reset per duel.</summary>
        protected SummonTracker Summons { get; } = new();

        protected DeckBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
            Tuning.EnsureLoaded(); // the static numbers (search weights, rule costs) before anything reads them
            // The shared numbers before a deck brain's own constructor changes any (those go in the deck's own file: Tuning.ApplyTo).
            _sharedDefaults = Tuning.Snapshot(this, typeof(DeckBotBrain));
        }

        private readonly Dictionary<string, object?> _sharedDefaults;

        /// <summary>Applies the tuning files to this brain now (DuelSim tuning lists every brain's numbers this way).</summary>
        public void ApplyTuning()
        {
            _tuned = true;
            Tuning.ApplyTo(this, _sharedDefaults);
        }

        private bool _tuned;

        /// <summary>A new bot (a new duel) to follow: a fresh GameField, and the per-duel memory cleared. From the first prompt, or
        /// earlier from the turn-start callback (DeckBotBrain.TurnLog), whichever comes first.</summary>
        private void AttachToBot(DuelBotWrapper bot)
        {
            _trackedBot = bot;
            _gameField = new GameField(bot);
            if (!IsSearchTwin)
                bot.KnownOpponentHand = () => KnownEnemyHand(); // the log's "their hand" line
            _trackedTurn = -1;
            _threatenedState.Clear();
            _assessedStrategicState = TagRules.BotStrategicState.Base;
            _lastLoggedStrategicState = null;
            _theirAttackedZones = (-1, new());
            _opponentBeforeTheirTurn = (-1, new());
            _opponentSummonedAndAttackedWithThreat = -1;
            _activatedThisTurn.Clear();
            _usedEffects.Clear();
            _chainDescriptions.Clear();
            PendingAction = default;
            OpponentMemory.Clear();
            Summons.Clear();
            ResetMatchup();
            OpponentDeck.Clear();
        }

        /// <summary>
        /// Called at the start of every decision: brings the brain up to date with the duel. In order: the tuning (once), a new
        /// duel or turn resets what belongs to the old one, the play search warms up, the field is read, and everything we
        /// track is updated from it (ObserveFieldAsync), then Grand Mole.
        /// </summary>
        protected async Task<GameField> PrepareContextAsync<TState>(DecisionContext<TState> context, CancellationToken ct = default)
        {
            _rollChance = null; // a former roll's chance belongs to the decision that computed it
            ApplyTuningOnce(context.Bot);
            if (_trackedBot != context.Bot || _gameField == null)
                AttachToBot(context.Bot);

            var header = context.Header;
            _currentHeader = header;
            _prepareCount++; // a new read of the board: the risk tolerance is recomputed when asked
            // Main-phase and battle prompts only happen on our own turn: remember that turn's parity.
            if (context.StateData is IdleCmdData or BattleCmdData)
            {
                _ownTurnParity = header.CurrentTurn % 2;
                ClearClaimedRevives();
            }
            ResetForNewDuelOrTurn(header);

            // The first prompt of every turn: get the play search ready (cards scored, engines loaded).
            if (!IsSearchTwin)
                WarmUpAtTurnStart(context.Bot, header);
            var refresh = RefreshFieldAsync();
            await refresh;
            if (context.StateData is IdleCmdData && !IsSearchTwin)
                await TrackBleedingAsync(header.CurrentTurn);
            // Remember face-up opponent monsters at every decision (no extra API call: reuses the refreshed field).
            if (refresh is Task<FieldInfo> fieldTask)
                await ObserveFieldAsync(context, header, fieldTask.Result);

            // Grand Mole on their field / back in their hand: tracked at every decision, so a Mole that comes out,
            // bounces and returns to their hand within one Battle Phase isn't missed.
            bool bouncerKnown = BouncerThreat;
            await TrackBouncersAsync();
            if (!bouncerKnown && _bouncerInTheirHand)
                context.Bot.LogNote("deck brain: their Grand Mole-type monster went back to their hand: it can come straight back (don't set into it)");
            return CurrentField; // set by AttachToBot above (the compiler can't see that through the call)
        }

        /// <summary>The tuning file's numbers (Tuning, bot-tuning.txt), once per brain, after its own constructor set its defaults.</summary>
        private void ApplyTuningOnce(DuelBotWrapper bot)
        {
            if (_tuned)
                return;
            _tuned = true;
            Tuning.ApplyTo(this, _sharedDefaults);
            if (!IsSearchTwin && Tuning.FilePath != null)
                bot.LogNote($"tuning: {Tuning.FilePath} ({Tuning.Overridden} number(s) differ from the built-in values)" +
                            (TuningProfile.Current is { } profile ? $"; this side's own values: {profile.Name} ({profile.Values.Count})" : ""));
        }

        /// <summary>A new duel forgets what we knew about their hand; a new turn clears what was this turn's.</summary>
        private void ResetForNewDuelOrTurn(BotStateHeader header)
        {
            if (header.CurrentTurn < _trackedTurn)
            {
                // A new duel: forget what we knew about their hand.
                _bouncerInTheirHand = false;
                _bouncersOnTheirField.Clear();
                _bouncersGoneLastSeen = _bouncersGoneAtBounce = 0;
                OpponentDeck.Clear();
                Summons.Clear();
                ResetMatchup();
            }
            if (header.CurrentTurn != _trackedTurn)
            {
                // Effect scores can depend on their graveyard (revivers): rescore each turn.
                EffectPointsCache.Clear();
                _battledThisTurn.Clear();
                _trackedTurn = header.CurrentTurn;
                _activatedThisTurn.Clear();
                _usedEffects.Clear();
                _chainDescriptions.Clear();
                PendingAction = default;
            }
        }

        /// <summary>Everything we track, updated from the field just read.</summary>
        private async Task ObserveFieldAsync<TState>(DecisionContext<TState> context, BotStateHeader header, FieldInfo field)
        {
            OpponentMemory.ObserveZoneIds(field.EnemySide);
            TrackEnemyArrivals(field.EnemySide, context);
            ObserveTurnRecord(context.Bot, header, field);
            ObserveReveals(field);
            await SnapshotOwnZonesAsync(header);
            if (!IsSearchTwin)
                await ObserveTempBoostsAsync(header, field);
            await ObserveMonstersBeforeTheirBattleAsync(header);
            await TrackVayuNegationAsync(field);
            _ownHandIds = field.OwnSide.Hand ?? Array.Empty<int>();
            _theirLifePoints = field.EnemySide.LifePoints;
            _weCanProtect = Search.SearchHost.Available && (field.OwnSide.SpellZones ?? Array.Empty<CardArray>())
                .Select(zone => (zone.Cards ?? Array.Empty<int>()).FirstOrDefault()).Where(id => id > 0)
                .Where(id => id != TorrentialTribute && id != MysticalSpaceTyphoon) // Torrential Tribute kills what it'd protect, MST protects nothing (user, 2026-11-19)
                .Any(id => Search.SearchHost.Cards.Get(id) is { } c && CardValues.Protection(c.Type, c.Text) > 0);
            OnFieldRefreshed(field);
            // Real-duel bookkeeping, skipped by the play search's brain twins (they play thousands of imagined decisions):
            // how Extra Deck monsters got out (one not properly summoned can't be revived or taken), and the matchup.
            if (!IsSearchTwin)
            {
                await CardDatabase.EnsureAsync(new[] { field.OwnSide, field.EnemySide }
                    .SelectMany(side => (side.MonsterZones ?? Array.Empty<CardArray>()).SelectMany(zone => zone.Cards ?? Array.Empty<int>())
                        .Concat(side.Grave ?? Array.Empty<int>())));
                Summons.Observe(field);
                ObserveMatchup(field);
            }
            _ourDeckCount = field.OwnSide.Main?.Length ?? 40;
            if (!IsSearchTwin)
                await ReadOpponentDeckAsync(context, field);
            // What the MCTS should search in the dead time after this prompt; and whether Omega shows our deck order.
            if (!IsSearchTwin)
            {
                ProbeDeckOrder(context.Bot, header, field);
                await RetargetDeadTimeAsync(context.Bot, header, field);
            }
        }

        /// <summary>
        /// The deck read (OpponentDeck) from their side of the field: what we know is in their hand, their unseen searches, a
        /// Special Summon lock, a known Honest, their face-down monsters' likely DEF, and their set cards we can pin down.
        /// </summary>
        private async Task ReadOpponentDeckAsync<TState>(DecisionContext<TState> context, FieldInfo field)
        {
            var knownBefore = OpponentDeck.KnownHand();
            if (OpponentDeck.ObserveField(field.EnemySide) is string deckNote)
                context.Bot.LogNote($"deck brain: {deckNote}");
            var knownNow = OpponentDeck.KnownHand();
            await ObserveUnseenSearchesAsync(context.Bot, field.EnemySide);
            await UpdateSpecialSummonLockAsync(field);
            // The brain's battle maths (BoardAssessment.Defenders) count a known Honest.
            OpponentMemory.KnownHonest = KnownEnemyHand().Count(id => id == Honest || Search.SearchHost.CanonicalId(id) == Honest);
            // Their face-down monsters' likely DEF, from the deck read (user, 2026-11-16).
            OpponentMemory.ExpectedSetDefense.Clear();
            if (Search.SearchHost.Available)
                for (int zone = 0; zone < 5; zone++)
                {
                    var odds = OpponentDeck.LikelyMonsterZone(zone).Where(o => Search.SearchHost.Cards.Get(o.Id) != null).ToList();
                    double total = odds.Sum(o => o.Chance);
                    if (total > 0)
                        OpponentMemory.ExpectedSetDefense[zone] =
                            (int)Math.Round(odds.Sum(o => o.Chance * Math.Max(0, Search.SearchHost.Cards.Get(o.Id)!.Defense)) / total);
                }
            for (int zone = 0; zone < 5; zone++)
                foreach (bool monster in new[] { false, true })
                    if (OpponentDeck.KnownSet(monster, zone) is int pinned && !_pinnedLogged.Contains((monster, zone, pinned)))
                    {
                        _pinnedLogged.Add((monster, zone, pinned));
                        context.Bot.LogNote($"deck brain: their set {(monster ? "monster" : "card")} in {(monster ? "M" : "S")}{zone + 1} is " +
                                            $"{CardNames.Get(pinned)} (set from a hand we knew in full, the only card that could be it)");
                    }
            if (!knownNow.OrderBy(x => x).SequenceEqual(knownBefore.OrderBy(x => x)))
                context.Bot.LogNote($"deck brain: known in their hand: {(knownNow.Count == 0 ? "nothing" : string.Join(", ", knownNow.Select(id => CardNames.Get(id))))}");
        }


        /// <summary>
        /// Called with the freshly read field at every decision, so deck brains can cache board-dependent judgements
        /// that synchronous rules like <see cref="IsExpendable"/> need. No extra API calls.
        /// </summary>
        protected virtual void OnFieldRefreshed(FieldInfo field) { }

        // ------------------------------------------------------------------ the opponent's deck

        /// <summary>
        /// The opponent's deck read from real Edison deck lists (see <see cref="OpponentDeckPredictor"/>): every card they
        /// show this duel narrows it down. Gives what their set cards (and hand) likely are: e.g. against Frogs a set card is
        /// almost surely not Dimensional Prison.
        /// </summary>
        protected OpponentDeckPredictor OpponentDeck { get; } = new();

        /// <summary>Reports their hand revealed by an effect (e.g. Trap Dustshoot) to the deck read: what it holds right now.</summary>
        protected void ObserveRevealedOpponentCards<TState>(DecisionContext<TState> context, IEnumerable<int> cardIds)
        {
            if (OpponentDeck.ObserveRevealedHand(cardIds) is string deckNote)
                context.Bot.LogNote($"deck brain: {deckNote}");
        }

        protected GameField CurrentField =>
            _gameField ?? throw new InvalidOperationException("The deck brain field is not initialized. Call PrepareContextAsync first.");

        protected BotStateHeader CurrentHeader => _currentHeader;

        protected virtual Task RefreshFieldAsync() => CurrentField.GetBotField();
    }
}
