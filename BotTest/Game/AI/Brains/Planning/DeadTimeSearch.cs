using BotTest.Game.AI.Search;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The MCTS in dead time (see MctsSession): what it searches between our decisions. During their turn (and from the moment
    /// ours ends), our next turn, the draw dealt in each line; at the start of ours (Draw / Standby), the board as it stands;
    /// after each of our decisions, that decision's node (DeckBotBrain.Search), so the wait for the server or their chains
    /// keeps the tree growing. The decision on the clock finds its node in the same tree (by board and answers on offer).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Off since 2026-11-11 (user: "lets give up on the dead-time searches"): the tree grown during their turn never matched
        // the board our turn started from. The turn-start search (TurnStartMs) prepares the turn instead. DUELBOT_DEADTIME_PLAN=1 turns it on.
        public static bool DeadTimePlanning { get; set; } = Environment.GetEnvironmentVariable("DUELBOT_DEADTIME_PLAN") == "1";

        /// <summary>Only this deck's brain (by class name: "HeroFrog" for HeroFrogBotBrain) searches in dead time (both DuelSim bots share the process), or null for any.</summary>
        public static string? DeadTimeOnlyFor { get; set; }

        private static MctsSession? _session;
        private static readonly object SessionLock = new();

        /// <summary>The process's one dead-time search (brains come and go per duel; the thread stays), or null when off.</summary>
        private MctsSession? Session
        {
            get
            {
                if (!DeadTimePlanning || !SearchHost.Available || IsSearchTwin ||
                    DeadTimeOnlyFor != null && !GetType().Name.StartsWith(DeadTimeOnlyFor, StringComparison.OrdinalIgnoreCase))
                    return null;
                lock (SessionLock)
                    return _session ??= new MctsSession();
            }
        }

        private string? _deadTimeFingerprint;

        /// <summary>
        /// At every prompt outside our main and battle decisions: what the dead-time search should grow. During their turn
        /// (or as ours ends), our next turn; at the start of ours (Draw / Standby), the board as it stands. Only when the
        /// board changed since the last prompt (a new tree each time: a changed board is a different game).
        /// </summary>
        /// <summary>
        /// DuelSim: there's no human to wait for, so each bot searches its next turn for this long once per turn, as the turn
        /// passes to its opponent, standing in for a human's turn (user, 2026-11-11: "run the background check as if there
        /// were a human opponent for 3 seconds once per turn ... for both bots"). 0 = live (the background thread instead).
        /// </summary>
        public static int SimulatedDeadTimeMs { get; set; }

        // The tree the simulated dead time grew, for our next turn.
        private (int Turn, MctsTree Tree)? _deadTree;
        private int _simDeadTurn = -1;

        private async Task RetargetDeadTimeAsync(DuelBotWrapper bot, BotStateHeader header, FieldInfo field, bool endingOurTurn = false)
        {
            var session = Session;
            bool simulated = session == null && SimulatedDeadTimeMs > 0;
            if (IsSearchTwin || SearchBudgetMs <= 0 || !PlaySearchSupported || session == null && !simulated)
                return;
            var phase = (DuelPhase)header.CurrentPhase;
            // Our next turn is searched from the moment ours ends (our End Phase, or the "end the turn" decision): live, their
            // turn may bring us no prompt at all, and our Draw Phase is too late (seen 2026-11-10).
            bool theirTurn = header.CurrentPlayer == 1 || endingOurTurn || phase == DuelPhase.End;
            bool ourStart = !theirTurn && phase is DuelPhase.Draw or DuelPhase.Standby;
            if (!theirTurn && !ourStart)
                return; // our main / battle decisions point the search at their own node (see SearchCoreAsync)
            int nextTurn = header.CurrentPlayer == 0 ? header.CurrentTurn + 2 : header.CurrentTurn + 1;
            if (simulated && (!theirTurn || _simDeadTurn == nextTurn))
                return; // once per turn, as the turn passes to them
            string fingerprint = $"{header.CurrentTurn}|{theirTurn}|{Fingerprint(field.OwnSide)}#{Fingerprint(field.EnemySide)}";
            if (!simulated && fingerprint == _deadTimeFingerprint)
                return;
            _deadTimeFingerprint = fingerprint;
            if (simulated)
                _simDeadTurn = nextTurn;
            SearchView view;
            try
            {
                view = await BuildSearchViewAsync(bot);
            }
            catch
            {
                return;
            }
            view.RealTurn = header.CurrentTurn;
            view.FirstTurnOfDuel = header.CurrentTurn == 1;
            string label;
            if (theirTurn)
            {
                view = view.ProjectNextTurn();
                label = view.KnownTop.Count > 0 ? $"our next turn, drawing {CardNames.Get(view.KnownTop[0])} (known)" : "our next turn, the draw dealt in each line";
            }
            else
                label = "this turn's board";
            await RefreshStopOddsAsync();
            ProjectStandby(view);
            var tree = new MctsTree { Label = label };
            var target = MakeTarget(bot, view, tree, tree.Root, BotState.SelectIdleCmd, fingerprint, label);
            if (simulated)
            {
                var search = new Mcts(target.View, tree, tree.Root, target.RootState, null, Array.Empty<int>(),
                    target.Settings with { BudgetMs = SimulatedDeadTimeMs }, target.Evaluator, target.Brains)
                { MayChoose = target.MayChoose, Suggestions = target.Suggestions };
                var outcome = await Task.Run(() => search.Run(Interlocked.Increment(ref _searchSeed)));
                _deadTree = (nextTurn, tree);
                bot.LogNote($"mcts: dead time (simulated, {SimulatedDeadTimeMs} ms): {label}, {outcome.Iterations} lines, depth {outcome.Depth}" +
                            (outcome.BestLine is { Count: > 0 } best ? $"; best line so far: {string.Join(" -> ", best.Select(s => s.Label))}" : ""));
                return;
            }
            session!.SetTarget(target);
            bot.LogNote($"mcts: searching {label} in dead time [{KeyHash(fingerprint)}]");
        }

        /// <summary>What the dead-time search grows: this view's root prompt, in this tree from this node.</summary>
        private MctsSession.Target MakeTarget(DuelBotWrapper bot, SearchView view, MctsTree tree, MctsNode start, BotState rootState, string key, string label) => new()
        {
            Key = key,
            Label = label,
            View = view,
            Tree = tree,
            Start = start,
            RootState = rootState,
            Evaluator = MakeEvaluator(view),
            Settings = new SearchSettings(Threads: DefaultSearchThreads, Horizon: DefaultSearchHorizon),
            Brains = SearchBrains(bot),
            MayChoose = PlanMayChoose,
            Suggestions = PlanSuggestions,
        };

        private BoardEvaluator MakeEvaluator(SearchView view)
        {
            // Their cards the lines don't play (no guessing): the unknown part of their hand, and their unknown set spells/traps,
            // whose Mirror Force / Torrential / other stopper odds (the brain's backrow read) are charged to the line's board.
            int unknownHand = Math.Max(0, view.EnemyHandCount - view.EnemyKnownHand.Count);
            double explosiveness;
            double[]? attackerOdds;
            // What they could attack with next turn: their unknown hand plus their draw, from the lists they're likely on.
            int unknownForAttack = unknownHand + 1;
            lock (OpponentDeck)
            {
                explosiveness = OpponentDeck.Explosiveness() ?? DefaultExplosiveness;
                attackerOdds = OpponentDeck.AttackerChances(BoardEvaluator.AttackerThresholds, unknownForAttack, AttackerStats);
            }
            attackerOdds ??= BoardEvaluator.GenericAttackerOdds(unknownForAttack);
            // Threats in their unknown cards, for set traps (user, 2026-11-24): as attackerOdds, but bouncers and strong effects
            // (IsBigThreatAsync's StrongEffectScore) count at any ATK. Without a deck read: the attacker odds alone.
            var values = new CardValues(view.OwnGrave, view.EnemyGrave);
            // Threat-tagged monsters (Machina Fortress, Archlord Kristya) count at any level: they're special summoned.
            static bool TaggedThreat(int id) => CardRuleRegistry.AnyCardWithTag(new[] { id }, TagRules.Tag.Threat);
            bool EffectThreat(int id) =>
                BoardAssessment.KnownAttackerBouncers.ContainsKey(id) ||
                SearchHost.Cards.Get(id) is { } c && values.Get(id, 1).Field + BoardEvaluator.AtkValue(c.Attack) >= StrongEffectScore;
            double[]? threatOdds;
            lock (OpponentDeck)
                threatOdds = OpponentDeck.ThreatChances(BoardEvaluator.AttackerThresholds, unknownForAttack, AttackerStats, EffectThreat, TaggedThreat);
            threatOdds ??= attackerOdds;
            // Their hand monsters that stop a lethal battle (a win's chance): known in their hand = 1, else the deck read's chance. Honest
            // only with an attack-position LIGHT monster of theirs out, Kalut only with a Blackwing (user, 2026-11-17).
            var knownHand = KnownEnemyHand();
            double InHand(int id)
            {
                if (knownHand.Any(k => k == id || SearchHost.CanonicalId(k) == id))
                    return 1;
                lock (OpponentDeck)
                    return OpponentDeck.HandChance(id, view.EnemyHandCount) ?? 0;
            }
            var theirAttackers = view.EnemyMonsters.Where(c => c.Code > 0 && (c.Position & 0x1) != 0).ToList();
            bool lightOut = theirAttackers.Any(c => (SearchHost.Attribute(c.Code) & 0x10) != 0);
            bool blackwingOut = theirAttackers.Any(c => SearchHost.Available && SearchHost.Cards.Get(c.Code)?.Name?.StartsWith("Blackwing") == true);
            // Their next attacker for the sitting-duck check (user, 2026-11-18): the biggest Normal Summon (and tribute summon) left in their
            // deck, plus what their known hand cards add (Kalut's +1400; Honest wins any battle). Without list data: NoListNormalAttack / NoListTributeAttack.
            (int Normal, int Tribute)? biggest;
            lock (OpponentDeck)
                biggest = OpponentDeck.BiggestAttackers(AttackerStats);
            int knownBoost = 0;
            foreach (int code in knownHand)
                if (SearchHost.Available && SearchHost.Cards.Get(code)?.Text is { } text &&
                    System.Text.RegularExpressions.Regex.Match(text, @"gains? (\d+) ATK", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } gain)
                    knownBoost = Math.Max(knownBoost, int.Parse(gain.Groups[1].Value));
            bool honestKnown = knownHand.Any(k => k == Honest || SearchHost.CanonicalId(k) == Honest);
            return new(values, SearchCardBonus, unknownHand, explosiveness)
            {
                AttackerOdds = attackerOdds,
                ThreatOdds = threatOdds,
                OwnLargestNormalAttack = OwnLargestNormalAttack(_trackedBot),
                OwnDeck = view.OwnDeck, OwnHand = view.OwnHand, OwnGrave = view.OwnGrave, OwnBanished = view.OwnBanished, OwnMonsters = view.OwnMonsters.Select(c => c.Code).Where(c => c > 0).ToList(),
                GraveSetup = SearchGraveSetup,
                RewardGladiatorBattles = RewardGladiatorBattles,
                BackrowDoomRule = SearchBackrowDoomRule,
                MstOwnFutureFusion = SearchMstOwnFutureFusion,
                TheirNormalAttack = biggest?.Normal ?? BoardEvaluator.NoListNormalAttack,
                TheirTributeAttack = biggest?.Tribute ?? BoardEvaluator.NoListTributeAttack,
                TheirKnownBoost = knownBoost,
                TheirHonestKnown = honestKnown,
                UnknownSets = view.EnemySpells.Count(c => c.Code <= 0),
                MirrorForceChance = _unknownMirror,
                MirrorReadResidual = EvidenceResidual,
                TorrentialChance = _unknownTorrential,
                OtherStopChance = _otherStopChance,
                QuickPlayStopChance = _quickStopChance,
                HonestChance = lightOut ? InHand(Honest) : 0,
                KalutChance = blackwingOut ? InHand(BlackwingKalutTheMoonShadow) : 0,
                GorzChance = Math.Max(InHand(GorzTheEmissaryOfDarkness), BrainGorzChance(view)),
                BattleFaderChance = InHand(BattleFader),
                // Herald needs another Fairy in hand to send with it.
                OrangeLightChance = view.EnemyHandCount >= 2 ? InHand(HeraldOfOrangeLight) : 0,
                HeavyStormChance = _heavyChance,
                IcarusChance = _unknownIcarus,
                RiskPress = RiskPressNow,
                TheirStartLp = view.LifePoints[1],
                OurStartLp = view.LifePoints[0],
                NextTurnValue = SearchNextTurnValue,
                FieldLostAfterTurn = SearchFieldLostAfterTurn,
                BaitCodes = BaitCardIds.ToArray(),
                StormProofCodes = StormProofSets.ToArray(),
                StormGoneChance = StormGoneChance,
                OversetPressFrom = OversetPressFrom,
                StrongMonsterAttack = StrongMonsterAttack,
                DominantAtStart = (DominantCode(view.OwnMonsters, view.EnemyMonsters), DominantCode(view.EnemyMonsters, view.OwnMonsters)),
            };
        }

        /// <summary>A card's ATK and level from the card database (for their likely attackers), or null if unknown.</summary>
        /// <summary>The face-up monster on this side that nothing face-up on the other side beats (its card code), or 0.</summary>
        private static int DominantCode(List<ViewCard> side, List<ViewCard> other)
        {
            static bool FaceUp(ViewCard c) => (c.Position & 0x5) != 0; // face-up attack (0x1) or face-up defence (0x4)
            static int Atk(ViewCard c) => SearchHost.Available && SearchHost.Cards.Get(c.Code) is { } r ? r.Attack : 0;
            static int Power(ViewCard c) => SearchHost.Available && SearchHost.Cards.Get(c.Code) is { } r ? ((c.Position & 0x1) != 0 ? r.Attack : r.RealDefense) : 0;
            var top = side.Where(c => c.Code > 0 && FaceUp(c)).OrderByDescending(Atk).FirstOrDefault();
            if (top == null)
                return 0;
            int best = other.Where(c => c.Code > 0 && FaceUp(c)).Select(Power).DefaultIfEmpty(0).Max();
            return Atk(top) > best ? top.Code : 0;
        }

        private static (int Attack, int Level)? AttackerStats(int id)
        {
            if (!SearchHost.Available || SearchHost.Cards.Get(id) is not { } card || card.IsExtraDeckMonster)
                return null;
            return (card.Attack, card.Level);
        }

        // Their unknown set cards' odds of being Mirror Force, Torrential, or another stopper (Bottomless, D-Prison, ...), for
        // scoring lines. Set cards known for certain (pinned) are in the lines themselves, so they're left out here.
        private double _unknownMirror, _unknownTorrential, _otherStopChance, _quickStopChance;
        // The chance they have Heavy Storm for our set cards (the brain's read of their hand and draws), for the exposure.
        private double _heavyChance;
        // Their unknown set cards' chance of Icarus Attack (Blackwing lists only: no list data, no Icarus), for the exposure.
        private double _unknownIcarus;

        /// <summary>Reads their unknown backrow's stopper odds for the MCTS's scoring (call before building an evaluator).</summary>
        private async Task RefreshStopOddsAsync()
        {
            try { await RiskToleranceAsync(); }
            catch { /* the last value stands */ }
            try { _heavyChance = await OpponentHeavyStormChanceAsync(); }
            catch { _heavyChance = 0; }
            var unknown = (await GetEnemySpellCardsAsync())
                .Where(card => !card.IsFaceup() && OpponentDeck.KnownSet(false, card.CurrentCoordinates.Sequence) == null)
                .Select(card => (int)card.CurrentCoordinates.Sequence).ToList();
            if (unknown.Count == 0 || TrapsNegated && !OpponentDeckPredictor.HasListData)
            {
                _unknownMirror = _unknownTorrential = _otherStopChance = _unknownIcarus = 0;
                // Traps negated without list data: their sets can still be quick-play stoppers (a sure win must count them).
                _quickStopChance = unknown.Count == 0 ? 0
                    : 1 - Math.Pow(1 - SetCardStopsPushChance * QuickPlayShareOfStoppers, unknown.Count);
                return;
            }
            double Any(Func<int, double> chance) => 1 - unknown.Aggregate(1.0, (none, zone) => none * (1 - Math.Clamp(chance(zone), 0, 1)));
            // The same clue the brain uses (TheirSetCards): a set card that stayed down when Mirror Force / Torrential would have
            // been used is probably not that card (user, 2026-11-24: "give the search that clue").
            double Cleared(int zone, Func<StapleEvidence, bool> cleared) =>
                _clearedSetZones.TryGetValue(zone, out var e) && cleared(e) ? EvidenceResidual : 1;
            _unknownMirror = Any(zone => (OpponentDeck.SetZoneChance(zone, MirrorForce) ?? (TrapsNegated ? 0 : SetCardIsMirrorForceChance)) *
                                         Cleared(zone, e => e.NotMirror));
            _unknownTorrential = Any(zone => (OpponentDeck.SetZoneChance(zone, TorrentialTribute) ?? (TrapsNegated ? 0 : SetCardIsTorrentialChance)) *
                                             Cleared(zone, e => e.NotTorrential));
            double any = Any(zone => OpponentDeck.StopChance(zone) ?? (TrapsNegated ? SetCardStopsPushChance * QuickPlayShareOfStoppers : SetCardStopsPushChance));
            _unknownIcarus = Any(zone => OpponentDeck.SetZoneChance(zone, IcarusAttack) ?? 0);
            _otherStopChance = Math.Max(0, any - _unknownMirror - _unknownTorrential - _unknownIcarus);
            // Stoppers that still work while traps are negated (the win's risk under a Jinzo still up when it lands).
            _quickStopChance = Any(zone => OpponentDeck.QuickPlayStopChance(zone) ?? SetCardStopsPushChance * QuickPlayShareOfStoppers);
        }

        private Func<SearchBrain>? SearchBrains(DuelBotWrapper bot) =>
            () => new SearchBrain(this, bot.DeckName ?? "", bot.DeckHash ?? "", PlaySearchWithBrain);

        /// <summary>
        /// What one of our cards is worth to this deck where it stands, on top of the generic card values (card units: a card
        /// in hand is about 0.5-1), for scoring the MCTS's lines (user, 2026-11-11: "scored by the brain's rules about what
        /// cards are good"). Runs on search threads: card data only, no live state. Default: nothing extra.
        /// </summary>
        internal virtual double SearchCardBonus(int code, CardPlace place) => 0;

        /// <summary>
        /// What our Standby Phase does to the board before the Main Phase, for the dead-time search's boards (a revive in
        /// between would make the tree miss the real board). Deck brains apply their own (HeroFrog: Treeborn comes back while
        /// we control no spells/traps).
        /// </summary>
        internal virtual void ProjectStandby(SearchView view) { }

        /// <summary>A short stable tag for a board key (to match a decision's board with the one searched in the log).</summary>
        private static string KeyHash(string key)
        {
            uint h = 2166136261;
            foreach (char c in key)
                h = (h ^ c) * 16777619;
            return h.ToString("x8")[..6];
        }

        private static string Fingerprint(CardInfo side)
        {
            static string Ids(int[]? ids) => ids == null ? "" : string.Join(",", ids);
            static string Zones(CardArray[]? zones) => zones == null ? "" : string.Join(";", zones.Select(z => Ids(z.Cards)));
            return $"{side.LifePoints}|{Ids(side.Hand)}|{Ids(side.Grave)}|{Ids(side.Banish)}|{Zones(side.MonsterZones)}|{Zones(side.SpellZones)}|{side.Main?.Length}";
        }

        // ---------------------------------------------------------------- does Omega show our deck order?

        private int _deckProbeTurn = -1;
        private (int Turn, int First, int Last, int[] Hand)? _deckProbe;

        /// <summary>
        /// Whether Omega's field info shows our deck in order (user, 2026-11-11: "just let it know its next card if omega allows
        /// that"): once per turn of ours, logs what OwnSide.Main holds; at the next turn, whether the card drawn was its first or
        /// last entry. Read from the logs before relying on it (SearchView.KnownTop).
        /// </summary>
        private void ProbeDeckOrder(DuelBotWrapper bot, BotStateHeader header, FieldInfo field)
        {
            if (IsSearchTwin || header.CurrentPlayer != 0 || header.CurrentTurn == _deckProbeTurn)
                return;
            _deckProbeTurn = header.CurrentTurn;
            var main = field.OwnSide.Main ?? Array.Empty<int>();
            var hand = field.OwnSide.Hand ?? Array.Empty<int>();
            if (_deckProbe is { } last && last.Turn < header.CurrentTurn)
            {
                var drawn = hand.Where(id => id > 0).GroupBy(id => id)
                    .Where(g => g.Count() > last.Hand.Count(id => id == g.Key)).Select(g => g.Key).ToList();
                // Logged only if Omega ever names the deck's cards (it showed none: the draws are in the log's hand changes;
                // user, 2026-11-12: the probe's lines weren't wanted).
                if (drawn.Contains(last.First) || drawn.Contains(last.Last))
                    bot.LogNote($"deck probe: the {(drawn.Contains(last.First) ? "first" : "last")} entry of Omega's deck list was drawn");
            }
            int named = main.Count(id => id > 0);
            int first = main.FirstOrDefault(), lastId = main.LastOrDefault();
            if (named > 0)
                bot.LogNote($"deck probe: Omega names {named} of {main.Length} deck cards; first {string.Join(", ", main.Take(3).Select(id => id > 0 ? CardNames.Get(id) : "?"))}, " +
                            $"last {string.Join(", ", main.TakeLast(3).Select(id => id > 0 ? CardNames.Get(id) : "?"))}");
            _deckProbe = (header.CurrentTurn, first, lastId, hand.ToArray());
        }
    }
}
