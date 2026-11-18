using BotTest.Game.Field;
using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared base for real deck brains. It keeps the current behavior-oriented
    /// helpers from BotTest while moving state and field access into the brain model.
    /// </summary>
    public abstract partial class DeckBotBrain : DefaultBotBrain
    {
        // IsCode falls back to the card database's alias (Omega's ids, prompts without an alias; see Helpers.AliasResolver).
        static DeckBotBrain() => OmegaBotWrapper.Helpers.AliasResolver ??= Search.SearchHost.CanonicalId;

        protected enum PendingActionKind
        {
            None,
            Search,
            Reveal,
            Discard,
            SendToGrave,
            Revive,
            MaterialSelection,
            TargetSelection
        }

        protected readonly struct PendingActionContext
        {
            public MainAction? Action { get; init; }

            public int CardId { get; init; }

            public int Description { get; init; }

            public PendingActionKind Kind { get; init; }

            public int ContextCode { get; init; }
        }

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
        private readonly HashSet<int> _activatedThisTurn = new();
        // Our effects used this turn, "official id:effect description" (Mcts.UsedEffects).
        private readonly HashSet<string> _usedEffects = new();

        private void MarkEffectUsed(int cardId, long description)
        {
            if (cardId > 0)
                _usedEffects.Add($"{(Search.SearchHost.CanonicalId(cardId) is > 0 and var official ? official : cardId)}:{description}");
        }
        private readonly List<int> _chainDescriptions = new();
        private DynamicCard? _selectedDefenderCard;

        protected PendingActionContext PendingAction { get; private set; }

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

        public override async Task<BattleCmdResponse> OnBattleCmdAsync(DecisionContext<BattleCmdData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            // Learn from the last attack (did their set cards stay down when Mirror Force would have paid off?).
            await RefreshBackrowOddsAsync(context);
            await RiskToleranceAsync(); // RiskNow, for the attack checks (LikelyRyko)
            if (context.Header.CurrentTurn != _battleTurn)
            {
                _battleTurn = context.Header.CurrentTurn;
                _bouncerAttacksThisTurn = 0;
            }
            var response = await ChooseWithServerAttackOrderAsync(context);
            if (response.Action == BattleAction.Attack)
            {
                var attacker = AttackerFor(context.StateData, response);
                // The server attacks with the first in zone order, whichever index we send.
                _declaredAttacker = (context.StateData.AttackableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault();
                if (attacker.ID > 0)
                    await NoteAttackForMirrorForceAsync(attacker);
                if (_selectedDefenderCard is DynamicCard target && BouncesAttacker(target))
                    _bouncerAttacksThisTurn++;
                if (_selectedDefenderCard == null && _enemyFieldEmpty)
                {
                    // A direct attack into an empty field: a chance for them to Gorz. If they don't, the cards they hold now
                    // probably aren't Gorz (checked at our next decision).
                    _gorzOpportunityHand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                }
            }
            return response;
        }

        private int _battleTurn = -1;
        private int _bouncerAttacksThisTurn;
        // The monster attacking now (for the target prompt that follows the attack).
        private DynamicCard _declaredAttacker;

        /// <summary>
        /// A target the deck wants this attacker (the first in zone order: the one the server attacks with) to take out before
        /// anything else, and why, or null. Default: none.
        /// </summary>
        protected virtual Task<(DynamicCard Target, string Why)?> PriorityAttackTargetAsync(DynamicCard attacker) =>
            Task.FromResult<(DynamicCard, string)?>(null);

        private static DynamicCard AttackerFor(BattleCmdData state, BattleCmdResponse response) =>
            (state.AttackableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.ActionIndex != null && card.ActionIndex.Length > (int)BattleAction.Attack &&
                                        card.ActionIndex[(int)BattleAction.Attack] == response.Index);

        /// <summary>
        /// Known server issue (see README): Omega ignores which attacker we pick and always attacks with the first monster
        /// in AttackableCards (zone order). So plan the attack the server will actually make: if our preferred attacker
        /// isn't first, choose the best target for the first one instead. If it has nothing worth attacking, it still
        /// attacks something harmless (a direct hit, or a monster it beats) when that unblocks better attacks behind it;
        /// otherwise the battle stops there. (Main Phase 1 moves a monster that would block the order to defence.)
        /// </summary>
        private async Task<BattleCmdResponse> ChooseWithServerAttackOrderAsync(DecisionContext<BattleCmdData> context)
        {
            var all = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();
            // Return from the Different Dimension for lethal, before the attacks it adds to.
            if (await TryReturnDimensionLethalInBattleAsync(context) is BattleCmdResponse returnLethal)
                return returnLethal;
            // What we committed to this turn comes first: an attack a rule summoned for, a lethal push (DeckBotBrain.Commitments).
            if (await CommittedAttackAsync(context) is BattleCmdResponse committed)
                return committed;
            // A target the deck must take out first with the monster the server attacks with (Glads: a Special Summon lock
            // in the way of a tag-out).
            if (all.Length > 0 && await PriorityAttackTargetAsync(all[0]) is { } priority)
            {
                AimAt(context.Bot, priority.Target, AimKind.Deliberate, $"{CardNames.Get(all[0].ID)} attacks {DescribeTarget(priority.Target)} first: {priority.Why}");
                return all[0].DeclareAttack();
            }
            var response = await ChooseBattleCmdAsync(context);
            if (all.Length < 2 || response.Action != BattleAction.Attack)
                return response;
            var first = all[0];
            var chosen = AttackerFor(context.StateData, response);
            if (chosen.ActionIndex == null || SameZone(chosen, first))
                return response;

            var wanted = $"{CardNames.Get(chosen.ID)}{(_selectedDefenderCard is DynamicCard t ? $" into {DescribeTarget(t)}" : " directly")}";
            var onlyFirst = context.StateData;
            onlyFirst.AttackableCards = new[] { first };
            var forced = await ChooseBattleCmdAsync(context with { StateData = onlyFirst });
            if (forced.Action == BattleAction.Attack)
            {
                context.Bot.LogNote($"deck brain: we'd rather attack with {wanted}, but the server always attacks with the first " +
                                    $"monster in zone order, {CardNames.Get(first.ID)}: attack with it as planned for it " +
                                    $"({(_selectedDefenderCard is DynamicCard ft ? $"into {DescribeTarget(ft)}" : "directly")})");
                return forced;
            }

            // The first monster has no good attack. Something harmless still lets the better attacks behind it through.
            var opponents = (await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone)).Where(card => !card.IsDisabled()).ToList();
            opponents = await AttackableTargetsAsync(opponents);
            if (opponents.Count == 0 && first.CanDirectAttack)
            {
                AimAt(context.Bot, null, AimKind.Deliberate, $"the server attacks with {CardNames.Get(first.ID)} first (zone order), and it has no good attack; " +
                                    $"a direct hit is harmless and lets {wanted} follow");
                return all[0].DeclareAttack();
            }
            int ourLp = await CurrentField.GetBotLifePoints();
            // The whole sequence, not just the first attack (user, 2026-11-12, round-robin 8: Grepher was sacrificed into Junk
            // Warrior "so Armor Master can follow", but Armageddon Knight was next in zone order with nothing to attack, so
            // the battle stopped there). Every attacker between the first and the one we want must be able to go on too: a good
            // attack, a direct hit, or a cheap one.
            bool CanGoOn(DynamicCard attacker, DynamicCard? gone)
            {
                var left = opponents.Where(target => gone is not DynamicCard g || !SameZone(target, g)).ToList();
                if (left.Count == 0)
                    return true; // a direct hit
                return left.Any(target => target.IsFaceup() && GetAttackScore(attacker, target) > 0 && IsWorthAttacking(attacker, target)) ||
                       left.Any(target => target.IsFaceup() && !DestroysByEffectInBattle(target, attacker) && !BouncesAttacker(target) &&
                                          (target.IsDefense() || IsExpendable(attacker)) &&
                                          Math.Max(0, (target.IsDefense() ? target.Defense : target.Attack) - attacker.Attack) < Math.Min(ourLp, CheapUnblockDamage));
            }
            DynamicCard? InTheWay(DynamicCard? gone) =>
                all.Skip(1).TakeWhile(card => !SameZone(card, chosen)).Where(card => !CanGoOn(card, gone)).Cast<DynamicCard?>().FirstOrDefault();
            string Blocked(DynamicCard stuck) =>
                $"deck brain: the server attacks with {CardNames.Get(first.ID)} first (zone order) and it has no good attack; making it attack anyway " +
                $"doesn't let {wanted} follow: {CardNames.Get(stuck.ID)} comes next and has nothing it can attack, so the battle would stop there";

            var harmless = opponents
                .Where(target => target.IsFaceup() && GetAttackScore(first, target) > 0 &&
                                 !(BouncesAttacker(target) && ReSummonCost(first) >= 100) &&
                                 // Not a recruiter: destroying one is never harmless (it replaces itself).
                                 (!IsRecruiter(target) || IsWorthAttacking(first, target)))
                .OrderByDescending(target => GetAttackScore(first, target))
                .FirstOrDefault();
            if (harmless.ID > 0 && InTheWay(harmless) is DynamicCard stuckAfterKill)
            {
                context.Bot.LogNote(Blocked(stuckAfterKill));
                harmless = default;
            }
            if (harmless.ID > 0)
            {
                AimAt(context.Bot, harmless, AimKind.Deliberate, $"the server attacks with {CardNames.Get(first.ID)} first (zone order); it has no good attack, " +
                                    $"but it beats {DescribeTarget(harmless)}, which lets {wanted} follow");
                return all[0].DeclareAttack();
            }
            // A cheap attack still unblocks the better ones: bouncing off a defence-position monster costs at most a little
            // damage (no monster lost), and an expendable first monster (a flipped Ryko) can even be lost. Not into a
            // Catastor-type or a bouncer, and never for damage that matters.
            int DamageTaken(DynamicCard target) => Math.Max(0, (target.IsDefense() ? target.Defense : target.Attack) - first.Attack);
            var cheapAttack = opponents
                .Where(target => target.IsFaceup() && !DestroysByEffectInBattle(target, first) && !BouncesAttacker(target) &&
                                 (target.IsDefense() || IsExpendable(first)) &&
                                 DamageTaken(target) < Math.Min(ourLp, CheapUnblockDamage))
                .OrderBy(DamageTaken).FirstOrDefault();
            if (cheapAttack.ID > 0 && InTheWay(null) is DynamicCard stuckAfterCheap)
            {
                context.Bot.LogNote(Blocked(stuckAfterCheap));
                cheapAttack = default;
            }
            if (cheapAttack.ID > 0 && Rule(DeckRules.CheapAttackUnblocks))
            {
                AimAt(context.Bot, cheapAttack, AimKind.Deliberate, $"the server attacks with {CardNames.Get(first.ID)} first (zone order) and it has no good attack, " +
                                    $"but attacking {DescribeTarget(cheapAttack)} costs little ({DamageTaken(cheapAttack)} damage" +
                                    $"{(cheapAttack.IsDefense() ? "" : $", {CardNames.Get(first.ID)} is expendable")}) and lets {wanted} follow");
                return all[0].DeclareAttack();
            }

            AimAt(context.Bot, null, AimKind.Deliberate, $"we'd attack with {wanted}, but the server always attacks with the first monster in zone order, " +
                                $"{CardNames.Get(first.ID)}, and it has nothing it can safely attack: stop the battle here");
            return forced;
        }

        private static string DescribeTarget(DynamicCard target) =>
            target.IsFaceup() && target.ID > 0 ? CardNames.Get(target.ID) : "a face-down monster";

        /// <summary>
        /// Main Phase 1 helper for the server's attack order: the first face-up attack-position monster (zone order) attacks
        /// first. If it has nothing worth attacking but a monster behind it does, it would block (or waste) that attack,
        /// so it is better in defence. Returns the reason, or null.
        /// </summary>
        protected string? BlocksAttackOrder(DynamicCard card, List<DynamicCard> opponents)
        {
            var attackers = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked)
                .OrderBy(own => own.CurrentCoordinates.Sequence).ToList();
            if (attackers.Count < 2 || !SameZone(attackers[0], card))
                return null;
            if (HasGoodAttack(card, opponents, attackers) || HasHarmlessAttack(card, opponents))
                return null;
            var behind = attackers.Skip(1).FirstOrDefault(own => HasGoodAttack(own, opponents, attackers));
            if (behind.ID <= 0)
                return null;
            return $"the server attacks in zone order and {CardNames.Get(card.ID)} is first with nothing it can attack; " +
                   $"in defence it stops blocking {CardNames.Get(behind.ID)}'s attack";
        }

        // A face-down target counts too: the battle logic sends a strong monster into it (seen 2026-11-13: Absolute Zero
        // "had nothing to attack" against a lone face-down, so Substitoad was summoned in front of it and stopped the battle).
        private bool HasGoodAttack(DynamicCard own, List<DynamicCard> opponents, List<DynamicCard> attackers) => opponents.Count == 0
            ? !IsWeak(own) || ChangesClock(own, attackers) || IsBeater(own) || ExpendableHitter(own)
            : opponents.Any(target => target.IsFaceup() ? IsWorthAttacking(own, target)
                                                       : !IsWeak(own) && own.Attack > OpponentMemory.FaceDownDefense(target));

        /// <summary>
        /// Crashing into an equal-ATK attack-position monster (both destroyed) is useful: it clears the way and trades
        /// 1-for-1. Good when what we lose scores no more than what they lose (effects included, negated ones not), and
        /// always with a cheap attacker: a negated monster (a Vayu synchro is just a body) or an expendable one (Armed
        /// Wing / Armor Master become Vayu partners in the graveyard). Never into a monster that profits from being
        /// destroyed: a battle recruiter or floater (it replaces itself, so it isn't a trade) or one with a graveyard
        /// effect (sending it there helps them). Not into a bouncer that would cost us a token / Extra Deck monster.
        /// </summary>
        private bool IsGoodTrade(DynamicCard attacker, DynamicCard target, int attackerCount) =>
            target.IsFaceup() && target.IsAttack() && attacker.Attack == target.Attack && target.Attack > 0 &&
            !(BouncesAttacker(target) && ReSummonCost(attacker) >= 100 && !LethalIfBothRemoved(attacker, target)) &&
            !(BouncesAttacker(target) && MoleLockStopsAttack(attackerCount, out _)) &&
            !IsRecruiter(target) && !RewardsDestruction(target) && !HasGraveyardEffect(target) &&
            !IsBattleProof(target) && // a live Armor Master survives: no trade, we just lose ours
            (IsEffectNegated(attacker) || IsExpendable(attacker) || TargetThreat(attacker) <= TargetThreat(target)) &&
            AllowCrashTrade(attacker, target) && Rule(DeckRules.CrashTrades);

        /// <summary>
        /// Whether this deck takes equal-ATK crash trades with this attacker at all. Default yes; a deck whose monsters are
        /// worth more than a 1-for-1 (Machina) only trades with expendable ones.
        /// </summary>
        protected virtual bool AllowCrashTrade(DynamicCard attacker, DynamicCard target) => true;

        // (A good equal-ATK trade counts too: the battle code makes it, so the Battle Phase isn't wasted.)
        private bool HasHarmlessAttack(DynamicCard own, List<DynamicCard> opponents) => opponents.Count == 0 ||
            opponents.Any(target => target.IsFaceup() && GetAttackScore(own, target) > 0 && !(BouncesAttacker(target) && ReSummonCost(own) >= 100) &&
                                    (!IsRecruiter(target) || IsWorthAttacking(own, target))) ||
            opponents.Any(target => IsGoodTrade(own, target, Math.Max(1, _ownMonsters.Count(card => card.IsFaceup() && card.IsAttack()))));

        /// <summary>
        /// Whether the Battle Phase would do anything. The server always attacks with the first face-up attack-position
        /// monster in zone order, so it's worth entering only if that one has something it can attack: a direct hit,
        /// an unknown face-down monster (assumed Ryko, beaten by anything), a good attack, or a harmless one that lets the
        /// rest follow. E.g. a lone Armageddon Knight (1400) against an 1800 Pegasus: nothing, so skip it (and set
        /// cards now rather than "after attacking"). Returns why it isn't worth it, or null when it is.
        /// </summary>
        protected async Task<string?> BattleWorthEnteringAsync()
        {
            var attackers = (await GetBotMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .OrderBy(card => card.CurrentCoordinates.Sequence).ToList();
            if (attackers.Count == 0)
                return "no face-up attack-position monster";
            // A battle we committed to (an attack a rule summoned for, a lethal push, the search's line): never skipped.
            if (BattleCommitted)
                return null;
            var opponents = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsDisabled()).ToList();
            if (opponents.Count == 0)
                return null;
            // An unknown face-down is worth a battle only with an attacker that beats what it's assumed to be (seen 2026-11-11:
            // Substitoad, 100 ATK, entered the Battle Phase for a face-down Ryko it couldn't kill, and the flip killed it).
            if (opponents.Any(card => !card.IsFaceup()) && attackers.Any(card => card.Attack > OpponentMemory.LowestFaceDownDefense(opponents)))
                return null;
            await RefreshRewardOnDestroyAsync(opponents);
            var first = attackers[0];
            if (HasGoodAttack(first, opponents, attackers) || HasHarmlessAttack(first, opponents))
                return null;
            // A crash that pays off (Fortress destroys a card, Returner revives Jinzo): the battle step makes it (see the
            // CrashForBattlePayoff attack), so the Battle Phase isn't skipped (seen 2026-11-12: Jinzo - Returner summoned "to
            // crash into Rai-Mei", then the Battle Phase was skipped as "nothing it can attack").
            if (BattleDeathPayoff(first) != null && opponents.Any(target => target.IsFaceup() && target.IsAttack() && target.Attack >= first.Attack &&
                                                                           !BouncesAttacker(target) && !DestroysByEffectInBattle(target, first)))
                return null;
            return $"{CardNames.Get(first.ID)} attacks first (zone order) and has nothing it can attack";
        }

        /// <summary>
        /// The server ignores the zone we pick and fills monster zones centre-first (seen in the logs): M3, M2, M4, M1, M5
        /// (0-based sequences 2, 1, 3, 0, 4).
        /// </summary>
        private static readonly int[] ServerZoneFillOrder = { 2, 1, 3, 0, 4 };

        /// <summary>
        /// Before a Main Phase 1 summon: would the new monster land ahead of our attackers in zone order (the server
        /// always attacks with the first one) with nothing it can attack itself, blocking an attacker behind it that has a
        /// good attack? A monster summoned this turn can't be moved to defence, so it would stop the whole battle.
        /// E.g. Armageddon Knight (1400) landing in M2 ahead of Dark Grepher (M3, 1700) against a 1600 Topaz Tiger.
        /// Returns the reason, or null.
        /// </summary>
        protected async Task<string?> SummonWouldBlockAttackOrderAsync(DynamicCard summon)
        {
            var own = await GetBotMonsterCardsAsync();
            var occupied = own.Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
            int zone = ServerZoneFillOrder.Where(z => !occupied.Contains(z)).DefaultIfEmpty(-1).First();
            if (zone < 0)
                return null;
            var attackers = own.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .OrderBy(card => card.CurrentCoordinates.Sequence).ToList();
            if (attackers.Count == 0 || attackers.Any(card => card.CurrentCoordinates.Sequence < zone))
                return null; // nothing to block, or an attacker already goes before it
            var opponents = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsDisabled()).ToList();
            await RefreshRewardOnDestroyAsync(opponents);
            // Judge it at the ATK it will have once we've used its summon effect (Card Trooper: 400 -> 1900).
            var asSummoned = summon;
            asSummoned.Attack = ExpectedAttackAfterSummon(summon);
            asSummoned.CurrentCoordinates.Sequence = (byte)zone;
            // Judge the board as it will be with the new monster out: its arrival can make an attack worth it (e.g. a
            // Giant Rat isn't worth attacking until it threatens the new Armageddon Knight).
            var savedOwn = _ownMonsters;
            _ownMonsters = own.Append(asSummoned).ToList();
            try
            {
                if (HasGoodAttack(asSummoned, opponents, attackers) || HasHarmlessAttack(asSummoned, opponents))
                    return null;
                var blocked = attackers.FirstOrDefault(card => HasGoodAttack(card, opponents, attackers) || HasHarmlessAttack(card, opponents));
                if (blocked.ID <= 0)
                    return null;
                return $"it would land in M{zone + 1}, ahead of {CardNames.Get(blocked.ID)} (the server attacks in zone order), with nothing " +
                       $"it can attack itself: it would stop {CardNames.Get(blocked.ID)}'s attack";
            }
            finally
            {
                _ownMonsters = savedOwn;
            }
        }

        /// <summary>
        /// The ATK a monster will have once summoned and its summon effect used (e.g. Card Trooper milling 3: 400 -> 1900).
        /// Deck brains override this for their own cards; the default is its current ATK.
        /// </summary>
        protected virtual int ExpectedAttackAfterSummon(DynamicCard card) => card.Attack;

        /// <summary>Most damage we take to let a first-in-zone-order monster attack so the better attacks behind it follow. Tune from logs.</summary>
        [Tunable("Most damage we take to let a first-in-zone-order monster attack so the better attacks behind it follow. Tune from logs.")]
        public int CheapUnblockDamage { get; set; } = 1000;

        /// <summary>
        /// Whether anything we'd set waits for Main Phase 2, thought through before setting (user, 2026-11-13: "am I going to
        /// set? is a battle phase possible? do I want to attack with anything? if I want to attack, then hold the set until mp2
        /// and move into battle phase. if I don't want to attack, then consider if there's a monster i want to atk with that
        /// could be special summoned, if there is, hold the set until mp2, special summon the monster, and attack"):
        ///  1. Main Phase 1 with a Battle Phase possible;
        ///  2. an attack we want with what's out (a face-up attacker and a Battle Phase worth entering), or
        ///  3. a monster we could Special Summon now that has an attack we'd want (see SummonToAttackAsync).
        /// Otherwise there's nothing to attack with: set now (we never enter the Battle Phase just to reach Main Phase 2).
        /// </summary>
        protected async Task<bool> SetsWaitForMainPhase2Async(DecisionContext<IdleCmdData> context)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !context.StateData.CanBattlePhase || !Rule(DeckRules.SetsAfterBattle))
                return false;
            if ((await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack()) && await BattleWorthEnteringAsync() == null)
                return true;
            return await SummonToAttackAsync(context) != null;
        }

        /// <summary>
        /// A monster we can Special Summon now that we'd want to attack with this turn (it beats something worth attacking,
        /// or gets a direct hit worth making), or default. Only a reason to hold sets: whether it's actually summoned is the
        /// spend rules' call, and if it isn't, the sets are made then.
        /// </summary>
        protected async Task<DynamicCard?> SummonToAttackAsync(DecisionContext<IdleCmdData> context)
        {
            var opponents = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsDisabled()).ToList();
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            await RefreshRewardOnDestroyAsync(opponents);
            foreach (var card in context.StateData.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
            {
                if (card.ID <= 0 || !card.HasType(CardType.Monster) || card.Attack <= 0)
                    continue;
                if (HasGoodAttack(card, opponents, attackers.Append(card).ToList()))
                    return card;
            }
            return null;
        }

        /// <summary>
        /// Why this monster of ours pays off when it's destroyed by battle (e.g. Machina Fortress destroys a card), so it's
        /// worth crashing into a monster at least as big; null for most monsters. Deck brains override this.
        /// </summary>
        protected virtual string? BattleDeathPayoff(DynamicCard card) =>
            card.IsCode(LylaId) && card.IsFaceup() && _ourDeckCount < LylaDeckGuard
                ? $"only {_ourDeckCount} cards left in our deck: Lyla's End Phase mill (3 a turn) would deck us out, so she's better gone"
                : null;

        /// <summary>
        /// What our monster's death by battle against <paramref name="killer"/> gains, in TargetThreat units (ATK plus effect
        /// points), before counting the monster lost: the battle-attack weighing subtracts that. Pairs with BattleDeathPayoff
        /// (deck brains override both). Default: Lyla better gone (she isn't a loss), else nothing.
        /// </summary>
        protected virtual double BattleDeathGain(DynamicCard card, DynamicCard killer, List<DynamicCard> theirMonsters) =>
            // Lyla whose End Phase mill would deck us out goes at any cost (user, 2026-11-12): decisive, short of damage that kills
            // us (the crash candidates already exclude that).
            card.IsCode(LylaId) && BattleDeathPayoff(card) != null ? TargetThreat(card) + LylaMustGoValue : 0;

        /// <summary>Value of getting rid of a Lyla whose mill would deck us out: larger than anything else a battle weighs.</summary>
        private const double LylaMustGoValue = 100_000;

        // Our deck size at the last field read.
        private int _ourDeckCount = 40;

        /// <summary>Below this many cards in our deck, Lyla isn't summoned and one on the field crashes (her End Phase mill). Tune from logs.</summary>
        [Tunable("Below this many cards in our deck, Lyla isn't summoned and one on the field crashes (her End Phase mill). Tune from logs.")]
        public int LylaDeckGuard { get; set; } = 10;

        /// <summary>Whether our deck is too small for Lyla's End Phase mill (3 a turn).</summary>
        protected bool DeckTooSmallForLyla => _ourDeckCount < LylaDeckGuard;

        /// <summary>
        /// Final check on a Main Phase 1 decision: a summon that would block our attack order (see
        /// <see cref="SummonWouldBlockAttackOrderAsync"/>) waits for Main Phase 2, and we go to battle first.
        /// </summary>
        protected async Task<IdleCmdResponse> DeferSummonBlockingAttackOrderAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse response)
        {
            if (response.Action is not (MainAction.Summon or MainAction.SpSummon) ||
                (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !context.StateData.CanBattlePhase)
                return response;
            var candidates = response.Action == MainAction.Summon
                ? context.StateData.SummonableCards
                : context.StateData.SpecialSummonableCards;
            var card = (candidates ?? Array.Empty<DynamicCard>()).FirstOrDefault(c =>
                c.ActionIndex != null && c.ActionIndex.Length > (int)response.Action && c.ActionIndex[(int)response.Action] == response.Index);
            if (card.ID <= 0 || await SummonWouldBlockAttackOrderAsync(card) is not string why)
                return response;
            // A summon a lethal plan made (combined lethal: the summon starts the combo) goes ahead: the plan is the turn
            // (seen 2026-11-11: Armageddon Knight deferred to Main Phase 2 broke Vayu's combined lethal; user: "had it done all
            // of this in mp1 it would've won this turn").
            if (_decisionRules.Any(rule => rule.Contains("lethal", StringComparison.OrdinalIgnoreCase)))
            {
                context.Bot.LogNote($"deck brain: summon {CardNames.Get(card.ID)} now even though {why}: it's part of a lethal plan");
                return response;
            }
            if (!Rule(DeckRules.DeferSummonForAttackOrder))
                return response;
            context.Bot.LogNote($"deck brain: summon {CardNames.Get(card.ID)} in Main Phase 2 instead: {why}");
            return new IdleCmdResponse(MainAction.ToBattlePhase);
        }

        private async Task<BattleCmdResponse> ChooseBattleCmdAsync(DecisionContext<BattleCmdData> context)
        {
            ConsoleUtils.PrintColored("OnBattleCmd - Generic attack evaluation");
            var attackableCardArray = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();

            var attackableCards = attackableCardArray
                .Where(card => !card.Attacked && !card.IsDisabled())
                .OrderByDescending(card => card.Attack)
                .ToList();

            if (!attackableCards.Any())
            {
                ConsoleUtils.PrintColored("No attackable cards available.");
                ClearAim();
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            var opponentMonsters = await AttackableTargetsAsync((await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList());

            await RefreshRewardOnDestroyAsync(opponentMonsters);

            if (!opponentMonsters.Any())
            {
                // Opponent field completely empty: attack smallest first, so a hand trap that answers the attack
                // (Gorz, Battle Fader, ...) spends itself on the least valuable attacker. AttackLast cards (e.g. Chaos
                // Sorcerer) go after everything else. With backrow up, the biggest goes first as before.
                bool fieldEmpty = (await GetEnemySpellCardsAsync()).Count == 0;
                // Traps negated (face-up Jinzo / Royal Decree): their set cards count only by their chance of being a
                // quick-play spell that stops us; with little of that, the field is as good as empty (user, 2026-11-10).
                if (!fieldEmpty && await TrapsNegatedAsync() && (await SetCardStopChancesAsync()).All(stop => stop < NegatedBackrowIgnoredBelow))
                {
                    context.Bot.LogNote("deck brain: their set cards can't hurt the attack: traps are negated (Jinzo / Royal Decree) and a quick-play stopper is unlikely");
                    fieldEmpty = true;
                }
                var directAttackers = attackableCards.Where(card => card.CanDirectAttack).ToList();
                // A weak monster only attacks if its damage changes the clock (e.g. it's the difference between game
                // and not); otherwise the hit isn't worth exposing it.
                var board = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).ToList();
                var worthwhile = directAttackers.Where(card => !IsWeak(card) || ChangesClock(card, board) || ExpendableHitter(card) || IsBeater(card) || !Rule(DeckRules.WeakDontAttackDirectly)).ToList();
                foreach (var skipped in directAttackers.Where(card => !worthwhile.Any(w => SameZone(w, card))))
                    context.Bot.LogNote($"deck brain: {CardNames.Get(skipped.ID)} doesn't attack: weak, and its damage doesn't change the clock");
                var directAttacker = (fieldEmpty
                        ? worthwhile.OrderBy(card => card.IsCode(AttackLast) ? 1 : 0).ThenBy(card => card.Attack)
                        : worthwhile.OrderByDescending(card => card.Attack))
                    .FirstOrDefault();
                // One hit that is lethal on its own: take it first (a hand trap like Gorz can't stop it).
                var oneShot = worthwhile.Where(card => card.Attack >= _oppLp).OrderBy(card => card.Attack).FirstOrDefault();
                if (oneShot.ID > 0 && Rule(DeckRules.OneHitLethalFirst))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(oneShot.ID)} is lethal in one hit: attack with it first");
                    directAttacker = oneShot;
                }
                else if (fieldEmpty && directAttacker.ID > 0 && await FearGorzAsync(context) && Rule(DeckRules.HoldForGorz))
                {
                    ClearAim();
                    return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
                }
                else if (fieldEmpty && directAttacker.ID > 0)
                    context.Bot.LogNote($"deck brain: opponent's field is empty, attack smallest first: {CardNames.Get(directAttacker.ID)}");

                // Chip damage that isn't a clock only exposes the monster to the opponent's backrow (Mirror Force,
                // Sakuretsu, ...). Attack directly only if the board is a real clock (Very Long or better) or it's lethal.
                if (!fieldEmpty && directAttacker.ID > 0 && _clockSpeed is ClockSpeed.AlmostNone or ClockSpeed.None &&
                    attackableCards.Where(card => card.CanDirectAttack).Sum(card => card.Attack) < _oppLp && Rule(DeckRules.NoChipIntoBackrow))
                {
                    ClearAim();
                    context.Bot.LogNote($"deck brain: don't attack directly with {CardNames.Get(directAttacker.ID)}: " +
                                        $"our board is only a {_clockSpeed} clock, not worth exposing it to the backrow");
                    return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
                }

                if (directAttacker.ID > 0 &&
                    attackableCardArray.TryGetByID(directAttacker.ID, out _, out int index))
                {
                    ConsoleUtils.PrintColored($"Direct Attack with Card ID: {directAttacker.ID}, Attack: {directAttacker.Attack}, Index: {index}");
                    AimAt(context.Bot, null, AimKind.Fallback, $"{CardNames.Get(directAttacker.ID)} attacks directly: their field is empty");
                    return attackableCardArray[index].DeclareAttack();
                }

                ConsoleUtils.PrintColored("No direct attack possible.");
                ClearAim();
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            // Face-down monsters: the bot must not use their hidden stats. Unknown ones are assumed to be Ryko,
            // so attack them first with the strongest monster: if Ryko's flip destroys it, it has already attacked
            // and the rest can still attack; if the opponent destroys a weaker one instead, the best monster survives.
            // Exception: if the strongest is the only monster that beats a face-up target, it attacks that first.
            OpponentMemory.Observe(opponentMonsters);
            await RefreshRewardOnDestroyAsync(opponentMonsters);
            var faceUpTargets = opponentMonsters.Where(card => card.IsFaceup()).ToList();
            var faceDownTargets = opponentMonsters.Where(card => !card.IsFaceup()).ToList();

            var unknownFaceDown = faceDownTargets
                .Where(card => !OpponentMemory.TryRecall(card, out _))
                .OrderBy(card => card.CurrentCoordinates.Sequence)
                .FirstOrDefault();
            if (unknownFaceDown.CurrentCoordinates.Location != 0 || unknownFaceDown.ID > 0)
            {
                // The deck's own pick for probing a face-down (Blackwing: Shura / Bora, whose effects pay off on a kill).
                if (PreferredFaceDownAttacker(attackableCards) is { ID: > 0 } preferred &&
                    attackableCardArray.TryGetByID(preferred.ID, out _, out int preferredIndex))
                {
                    AimAt(context.Bot, unknownFaceDown, AimKind.Deliberate, $"attack the unknown face-down with {CardNames.Get(preferred.ID)}: {PreferredFaceDownWhy(preferred)}");
                    return attackableCardArray[preferredIndex].DeclareAttack();
                }
                // A cheap monster first (Necro Gardna, an expendable body) when everything we have out is cheap: if it's
                // their Ryko, its flip has nothing better to destroy (Necro even works from the graveyard). With a good
                // card out, Ryko just destroys that instead, so the usual order applies.
                // (At least enough ATK to beat the assumed face-down's DEF: a 100-ATK Substitoad kills nothing and only hands a
                // flipped Ryko its target; seen 2026-11-11.)
                var cheap = CheapAttackDodgesRyko() && LikelyRyko(unknownFaceDown)
                    ? attackableCards.Where(card => IsCheapAttacker(card) && card.Attack > OpponentMemory.FaceDownDefense(unknownFaceDown))
                        .OrderBy(card => card.Attack).FirstOrDefault()
                    : default;
                if (cheap.ID > 0 && attackableCardArray.TryGetByID(cheap.ID, out _, out int cheapIndex) && Rule(DeckRules.CheapIntoFaceDown))
                {
                    AimAt(context.Bot, unknownFaceDown, AimKind.Deliberate, $"attack the unknown face-down (Ryko chance {FaceDownRykoChance(unknownFaceDown):P0}: {FaceDownRykoWhy(unknownFaceDown)}) with a cheap monster, {CardNames.Get(cheap.ID)}: " +
                                        "if it's Ryko, its flip is wasted on it");
                    return attackableCardArray[cheapIndex].DeclareAttack();
                }
                var strongest = attackableCards[0];
                // A face-down that likely banishes (or bounces) what battles it (D.D. Warrior Lady, e.g. set after Reinforcement
                // of the Army): a valuable attacker isn't worth the trade (user, 2026-11-13: "expected to violate the 'dont
                // banish fortress' rule (can be fine sometimes...)"). Weighed: the face-down destroyed (1 card) when it isn't,
                // against our attacker lost when it is. The server attacks in zone order, so the battle stops here.
                var first = attackableCardArray[0];
                if (!IsExpendable(first) && await FaceDownRemovesAttackerChanceAsync(unknownFaceDown) is var (removeChance, removers) &&
                    removeChance > 0)
                {
                    double lose = removeChance * Math.Max(1, BotTest.Game.AI.Search.MctsEvaluator.AtkValue(first.Attack));
                    double gain = 1 - removeChance;
                    if (lose > gain && Rule(DeckRules.AvoidBattleRemoval))
                    {
                        context.Bot.LogNote($"deck brain: don't attack the face-down with {CardNames.Get(first.ID)}: it may well be {removers} " +
                                            $"({removeChance:P0}), which removes what battles it: losing it ({lose:0.#}) outweighs destroying the face-down ({gain:0.#})");
                        HoldAttack(first.ID, DeckRules.AvoidBattleRemoval);
                        ClearAim();
                        return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
                    }
                }
                bool strongestUniquelyNeeded = faceUpTargets.Any(target =>
                    IsWorthAttacking(strongest, target) &&
                    !attackableCards.Skip(1).Any(other => IsWorthAttacking(other, target)));

                if (!strongestUniquelyNeeded && strongest.Attack > OpponentMemory.FaceDownDefense(unknownFaceDown) && Rule(DeckRules.StrongestIntoFaceDown) &&
                    attackableCardArray.TryGetByID(strongest.ID, out _, out int ryukoAttackerIndex))
                {
                    AimAt(context.Bot, unknownFaceDown, AimKind.Deliberate, $"attack the unknown face-down (Ryko chance {FaceDownRykoChance(unknownFaceDown):P0}: {FaceDownRykoWhy(unknownFaceDown)}) first with the strongest monster {CardNames.Get(strongest.ID)}");
                    return attackableCardArray[ryukoAttackerIndex].DeclareAttack();
                }
            }

            // Face-down monsters the bot saw face-up earlier: use the remembered DEF.
            foreach (var remembered in faceDownTargets)
            {
                if (!OpponentMemory.TryRecall(remembered, out var known))
                    continue;
                var attacker = attackableCards.FirstOrDefault(card => card.Attack > known.BaseDefense);
                if (attacker.ID > 0 && attackableCardArray.TryGetByID(attacker.ID, out _, out int rememberedIndex) && Rule(DeckRules.AttackRememberedFaceDown))
                {
                    AimAt(context.Bot, remembered, AimKind.Deliberate, $"attack face-down {CardNames.Get(known.Id)} (remembered, {known.BaseDefense} DEF) with {CardNames.Get(attacker.ID)}");
                    return attackableCardArray[rememberedIndex].DeclareAttack();
                }
            }

            // Grand Mole: attacking into it only bounces our monster (and Mole). Worth it when the bounce gains us
            // something (an on-summon effect again), or when we have no better answer to Mole (then trying beats losing
            // to it). With a better answer in hand / set, leave Mole alone.
            // Their Treeborn Frog in defence is pointless to attack (TreebornAttackPointless): what we know for it.
            await RefreshRemovesWhatItBattlesAsync(attackableCards);
            int theirSpellTraps = (await GetEnemySpellCardsAsync()).Count;
            string? TreebornWhy(DynamicCard attacker, DynamicCard target) =>
                TreebornAttackPointless(attacker, target, opponentMonsters, attackableCards.Select(card => card.Attack).ToList(), theirSpellTraps);
            string? betterMoleAnswer = null;
            if (faceUpTargets.Any(BouncesAttacker))
            {
                foreach (var attacker in attackableCards)
                    await HasOnSummonEffectAsync(attacker.ID);
                betterMoleAnswer = await BetterAnswerToBouncerAsync();
            }

            var bestBattle = attackableCards
                .SelectMany(attacker => faceUpTargets.Select(target => new
                {
                    Attacker = attacker,
                    Target = target,
                    AttackerPower = attacker.GetRealPower(),
                    Score = GetAttackScore(attacker, target)
                }))
                .Where(option => option.Score > 0)
                .Where(option =>
                {
                    if (!IsWorthAttacking(option.Attacker, option.Target) && Rule(DeckRules.SkipUnworthyAttacks))
                    {
                        context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(option.Target.ID)} with {CardNames.Get(option.Attacker.ID)}: " +
                                            (BouncesAttacker(option.Target)
                                                ? "it returns its attacker to the hand, and a token / Extra Deck monster would be lost"
                                                : IsRecruiter(option.Target)
                                                    ? "it's a recruiter (it replaces itself when destroyed), it isn't a real threat to us, and it doesn't win the game this turn"
                                                    : $"destroying it gets them a card, it isn't a threat, and our clock is only {_clockSpeed}"));
                        return false;
                    }
                    if (StealsWhatItDestroys(option.Attacker) && IsRecruiter(option.Target) && !StealSpeedsUpClock(option.Attacker, option.Target) &&
                        Rule(DeckRules.NoStealRecruiter))
                    {
                        context.Bot.LogNote($"deck brain: don't take {CardNames.Get(option.Target.ID)} with {CardNames.Get(option.Attacker.ID)}: it's a battle recruiter " +
                                            "(they get a monster from the deck): only worth it when it would take a turn off our clock from next turn and a set card of ours can protect it");
                        return false;
                    }
                    if (TreebornWhy(option.Attacker, option.Target) is string treeborn && Rule(DeckRules.SkipUnworthyAttacks))
                    {
                        context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(option.Target.ID)} with {CardNames.Get(option.Attacker.ID)}: {treeborn}");
                        return false;
                    }
                    if (BouncesAttacker(option.Target) && MoleLockStopsAttack(attackableCards.Count, out string lockWhy) && Rule(DeckRules.MoleLock))
                    {
                        context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(option.Target.ID)} again: {lockWhy}");
                        return false;
                    }
                    if (BouncesAttacker(option.Target) && betterMoleAnswer != null && !GainsFromBounce(option.Attacker) && Rule(DeckRules.SaveMoleForBetterAnswer) &&
                        !LethalIfBothRemoved(option.Attacker, option.Target))
                    {
                        context.Bot.LogNote($"deck brain: don't attack {CardNames.Get(option.Target.ID)} with {CardNames.Get(option.Attacker.ID)}: " +
                                            $"the bounce gains us nothing and we have a better answer to it ({betterMoleAnswer})");
                        return false;
                    }
                    return true;
                })
                .OrderByDescending(option => option.Score)
                .ThenByDescending(option => option.AttackerPower)
                .FirstOrDefault();

            // Into a monster that bounces its attacker (Grand Mole), send the cheapest monster to re-summon, preferring one
            // with an on-summon effect (bouncing it is a plus: we use the effect again).
            if (bestBattle != null && BouncesAttacker(bestBattle.Target) && Rule(DeckRules.CheapestIntoBouncer))
            {
                foreach (var attacker in attackableCards)
                    await HasOnSummonEffectAsync(attacker.ID);
                var cheapest = attackableCards
                    .Where(attacker => IsWorthAttacking(attacker, bestBattle.Target))
                    .OrderBy(ReSummonCost)
                    .ThenBy(attacker => HasOnSummonEffectCached(attacker) ? 0 : 1)
                    .ThenBy(attacker => attacker.Attack)
                    .First();
                context.Bot.LogNote($"deck brain: {CardNames.Get(bestBattle.Target.ID)} returns its attacker to the hand: attack it with the cheapest to re-summon, " +
                                    $"{CardNames.Get(cheapest.ID)}{(HasOnSummonEffectCached(cheapest) ? " (its on-summon effect can be used again)" : "")}");
                bestBattle = bestBattle with { Attacker = cheapest };
            }

            // Crash into an equal-ATK attack-position monster when we lose less than they do: an attacker (token or not,
            // judged by its size and effects) worth less than the threat it takes out (e.g. a Gorz token into Muka Muka).
            var trade = attackableCards
                .SelectMany(attacker => faceUpTargets
                    .Where(target => IsGoodTrade(attacker, target, attackableCards.Count))
                    .Select(target => new { Attacker = attacker, Target = target }))
                .OrderByDescending(option => TargetThreat(option.Target))
                .ThenBy(option => TargetThreat(option.Attacker))
                .FirstOrDefault();

            // A monster that pays off when destroyed by battle (Machina Fortress destroys a card): crash it into an
            // attack-position monster at least as big, as long as the damage doesn't kill us. Not into a bouncer (it would
            // just return to the hand, no payoff).
            int crashLp = await CurrentField.GetBotLifePoints();
            var payoff = attackableCards
                .Select(attacker => (Attacker: attacker, Why: BattleDeathPayoff(attacker)))
                .Where(x => x.Why != null)
                .SelectMany(x => faceUpTargets
                    .Where(target => target.IsAttack() && target.Attack >= x.Attacker.Attack && !BouncesAttacker(target) &&
                                     !DestroysByEffectInBattle(target, x.Attacker) && // a Catastor-type kills it by effect: no battle payoff
                                     target.Attack - x.Attacker.Attack < crashLp)
                    .Select(target => (x.Attacker, Target: target, x.Why)))
                .OrderByDescending(x => BattleDeathGain(x.Attacker, x.Target, faceUpTargets) - TargetThreat(x.Attacker))
                .FirstOrDefault();

            // Weigh them instead of taking the first that qualifies (user, 2026-11-12, round-robin 1: Fortress came out to crash
            // into Dark Armed Dragon, then the free kill of Lyla came first in the step order and DAD survived). Each option's
            // value, in TargetThreat units (ATK plus effect points): a free kill is their monster; a trade their monster less
            // ours; a crash what the death gains less our monster and the damage (in full when it leaves us under 1500 LP, a
            // quarter above: LP matter little up there). Plus the best kill the other attackers still have afterwards.
            double FollowUp(DynamicCard attacker, DynamicCard target) => attackableCards
                .Where(other => !SameZone(other, attacker))
                .SelectMany(other => faceUpTargets.Where(t => !SameZone(t, target) && GetAttackScore(other, t) > 0 && IsWorthAttacking(other, t) && TreebornWhy(other, t) == null))
                .Select(t => (double)TargetThreat(t)).DefaultIfEmpty(0).Max();
            double DamageCost(int damage) => damage * (crashLp - damage < CrashDangerLp ? 1.0 : CrashSafeDamageShare);
            var options = new List<(DynamicCard Attacker, DynamicCard Target, double Value, AimKind Kind, string? Rule, string Why)>();
            if (bestBattle != null)
            {
                string boost = bestBattle.Attacker.Attack <= bestBattle.Target.GetRealPower() && HandBattleBoost(bestBattle.Attacker) > 0
                    ? $", counting on +{HandBattleBoost(bestBattle.Attacker)} from the hand" : "";
                double value = TargetThreat(bestBattle.Target) + FollowUp(bestBattle.Attacker, bestBattle.Target);
                options.Add((bestBattle.Attacker, bestBattle.Target, value, AimKind.Fallback, null,
                    $"attack {DescribeTarget(bestBattle.Target)} with {CardNames.Get(bestBattle.Attacker.ID)}: it destroys it{boost}"));
            }
            if (trade != null)
            {
                double value = TargetThreat(trade.Target) - TargetThreat(trade.Attacker) + FollowUp(trade.Attacker, trade.Target);
                options.Add((trade.Attacker, trade.Target, value, AimKind.Deliberate, DeckRules.CrashTrades,
                    $"crash {CardNames.Get(trade.Attacker.ID)} into {CardNames.Get(trade.Target.ID)} ({trade.Target.Attack} ATK each): " +
                    $"our {TargetThreat(trade.Attacker)} for their {TargetThreat(trade.Target)} threat"));
            }
            if (payoff.Why != null)
            {
                int damage = payoff.Target.Attack - payoff.Attacker.Attack;
                double gain = BattleDeathGain(payoff.Attacker, payoff.Target, faceUpTargets);
                double value = gain - TargetThreat(payoff.Attacker) - DamageCost(damage) + FollowUp(payoff.Attacker, payoff.Target);
                options.Add((payoff.Attacker, payoff.Target, value, AimKind.Deliberate, DeckRules.CrashForBattlePayoff,
                    $"crash {CardNames.Get(payoff.Attacker.ID)} ({payoff.Attacker.Attack}) into {CardNames.Get(payoff.Target.ID)} ({payoff.Target.Attack}): {payoff.Why}"));
            }
            if (options.Count > 1)
                context.Bot.LogNote("deck brain: attacks weighed: " + string.Join(" | ", options.OrderByDescending(o => o.Value)
                    .Select(o => $"{CardNames.Get(o.Attacker.ID)} into {DescribeTarget(o.Target)} {o.Value:0}")));
            // A trade or a crash is made only for a gain (round-robin 23: Treeborn Frog crashed into Raiza at 3000 LP for
            // nothing: it comes back anyway, so the 2300 damage bought nothing); a free kill always is one.
            foreach (var option in options.Where(o => o.Rule == null || o.Value > 0).OrderByDescending(o => o.Value))
            {
                if (!attackableCardArray.TryGetByID(option.Attacker.ID, out _, out int optionIndex) || option.Rule != null && !Rule(option.Rule))
                    continue;
                AimAt(context.Bot, option.Target, option.Kind, $"{option.Why} (value {option.Value:0})");
                return attackableCardArray[optionIndex].DeclareAttack();
            }
            foreach (var option in options.Where(o => o.Rule != null && o.Value <= 0))
                context.Bot.LogNote($"deck brain: no {option.Why.Split(':')[0]}: it gains nothing (value {option.Value:0})");

            // An attack that pays off just by battling (a tag-out), made safe by Shield Warrior / Waboku.
            if (await TryProtectedAttackAsync(context, attackableCardArray, attackableCards, opponentMonsters) is BattleCmdResponse protectedAttack)
                return protectedAttack;

            ConsoleUtils.PrintColored("No advantageous attack targets found.");
            // Held: the attack the search could make here is with the first attacker in zone order (the server's choice).
            if (attackableCardArray.Length > 0)
                HoldAttack(attackableCardArray[0].ID, DeckRules.NoAttackWithoutGain);
            ClearAim();
            return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
        }

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default) =>
            // A Main Phase 1 summon that would block our attack order (server zone order) waits for Main Phase 2.
            await FlipBeforeSummonAsync(context, await DeferSummonBlockingAttackOrderAsync(context, await DecideGenericMainPhaseAsync(context, ct)));

        private async Task<IdleCmdResponse> DecideGenericMainPhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnIdleCmd");
            // The deck brain may already have logged the Heavy Storm hold for this prompt: don't repeat it.
            bool stormHoldAlreadyNoted = StormHoldNoted;
            StormHoldNoted = false;
            // D.D. Crow and co. are never summoned (their value is a hand effect).
            context = context with { StateData = WithoutNeverSummons(context.StateData) };

            var opponentMonsters = (await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList();

            await RefreshRewardOnDestroyAsync(opponentMonsters);
            await RefreshBackrowOddsAsync(context);

            // Gorz in hand with an empty field: don't Set anything that would switch it off.
            if (await KeepGorzLiveAsync(context, opponentMonsters))
            {
                var keepEmpty = context.StateData;
                foreach (var card in keepEmpty.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                    Hold(card.ID, MainAction.SetMonster, DeckRules.KeepGorzLive);
                foreach (var card in keepEmpty.SpellSetableCards ?? Array.Empty<DynamicCard>())
                    Hold(card.ID, MainAction.SetSpell, DeckRules.KeepGorzLive);
                keepEmpty.MonsterSetableCards = Array.Empty<DynamicCard>();
                keepEmpty.SpellSetableCards = Array.Empty<DynamicCard>();
                context = context with { StateData = keepEmpty };
            }

            // Against Grand Mole a face-down monster does nothing (it's attacked and returned to our hand).
            if ((context.StateData.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Length > 0 &&
                SetIntoBouncerHold(context) is string bouncerWhy && Rule(DeckRules.NoSetIntoBouncer))
            {
                context.Bot.LogNote($"deck brain: don't set monsters: {bouncerWhy}");
                foreach (var card in context.StateData.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                    Hold(card.ID, MainAction.SetMonster, DeckRules.NoSetIntoBouncer);
                var noMonsterSets = context.StateData;
                noMonsterSets.MonsterSetableCards = Array.Empty<DynamicCard>();
                context = context with { StateData = noMonsterSets };
            }

            if (!HandlesStapleIdle)
            {
                var staple = await TryStapleIdleAsync(context);
                if (staple.HasValue)
                    return staple.Value;
            }

            // Sets (monsters and spells/traps) wait for Main Phase 2 when we attack this turn: setting first gains nothing
            // (and set spells/traps could be destroyed by a flipped Ryko). With no attack coming, set now.
            if (((context.StateData.MonsterSetableCards?.Length ?? 0) > 0 || (context.StateData.SpellSetableCards?.Length ?? 0) > 0) &&
                await SetsWaitForMainPhase2Async(context))
            {
                var attackFirst = context.StateData;
                foreach (var card in attackFirst.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                    Hold(card.ID, MainAction.SetMonster, DeckRules.HoldMonsterSetsForBattle);
                foreach (var card in attackFirst.SpellSetableCards ?? Array.Empty<DynamicCard>())
                    Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForBattle);
                attackFirst.MonsterSetableCards = Array.Empty<DynamicCard>();
                attackFirst.SpellSetableCards = Array.Empty<DynamicCard>();
                context = context with { StateData = attackFirst };
            }

            // Each extra set card is one more the opponent's Heavy Storm would take.
            // (Cards a Storm doesn't cost us, like Jar of Greed or Trap Dustshoot, can still be set.)
            if ((context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.ID > 0 && !IsStormFree(card.ID)) &&
                await HoldSetForHeavyStormAsync(context.Header.CurrentTurn) is string stormHold && Rule(DeckRules.HoldSetsForStorm))
            {
                if (!stormHoldAlreadyNoted)
                    context.Bot.LogNote($"deck brain: don't set more spells/traps: {stormHold}");
                foreach (var card in (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0 && !IsStormFree(card.ID)))
                    Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForStorm);
                var noSets = context.StateData;
                noSets.SpellSetableCards = (noSets.SpellSetableCards ?? Array.Empty<DynamicCard>())
                    .Where(card => IsStormFree(card.ID)).ToArray();
                context = context with { StateData = noSets };
            }

            // No two set traps for the same issue, and no more than MaxExposedSets exposed.
            if ((context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Length > 0)
            {
                int handCount = (await GetBotHandCardsAsync()).Count;
                var keep = new List<DynamicCard>();
                foreach (var card in context.StateData.SpellSetableCards!)
                    if (card.ID <= 0 || await SetRedundantAsync(card.ID, handCount) == null || !Rule(DeckRules.NoRedundantSets))
                        keep.Add(card);
                var noRedundant = context.StateData;
                noRedundant.SpellSetableCards = keep.ToArray();
                context = context with { StateData = noRedundant };
            }

            _lowLpExposure = await LowLpExposureLimitAsync();
            var repositionToDefense = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsFaceup() && card.IsAttack() && ShouldRepositionToDefense(card, opponentMonsters))
                .OrderBy(card => card.Attack)
                .FirstOrDefault();

            if (repositionToDefense.ID > 0 && Rule(DeckRules.RepositionToDefence))
            {
                ConsoleUtils.PrintColored($"Repositioning to defense: {repositionToDefense.ID}");
                if (_mirrorForceReason.TryGetValue(repositionToDefense.CurrentCoordinates.Sequence, out var mirrorWhy))
                    context.Bot.LogNote($"deck brain: {CardNames.Get(repositionToDefense.ID)} to defence: {mirrorWhy}");
                return repositionToDefense.Reposition();
            }

            // The server attacks in zone order: a first monster with nothing to attack would block the ones behind it.
            if ((DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.StateData.CanBattlePhase)
            {
                if (await GorzAttackOrderAsync(context.StateData, opponentMonsters) is { } gorzOrder && Rule(DeckRules.GorzAttackOrder))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(gorzOrder.Card.ID)} to defence: {gorzOrder.Why}");
                    return gorzOrder.Card.Reposition();
                }
                if (await BattleRemovalOrderAsync(context.StateData, opponentMonsters) is { } removalOrder && Rule(DeckRules.AvoidBattleRemoval))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(removalOrder.Card.ID)} to defence: {removalOrder.Why}");
                    return removalOrder.Card.Reposition();
                }
                foreach (var card in (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>()).Where(card => card.IsFaceup() && card.IsAttack()))
                {
                    if (BlocksAttackOrder(card, opponentMonsters) is string blockWhy && Rule(DeckRules.UnblockAttackOrder))
                    {
                        context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} to defence: {blockWhy}");
                        return card.Reposition();
                    }
                }
                // Round-robin 26 (user, 2026-11-13: "yes totally agree"): a blocker that can't change position (summoned this turn)
                // is cleared out of the way as material: a synchro with another monster of ours (one a Tuner), or the tribute for
                // a Level 5-6. The material prompts take the cheapest monsters, and a weak blocker is that.
                var blocker = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked)
                    .OrderBy(own => own.CurrentCoordinates.Sequence).FirstOrDefault();
                if (blocker.ID > 0 && BlocksAttackOrder(blocker, opponentMonsters) is string stuckWhy &&
                    !(context.StateData.ReposableCards ?? Array.Empty<DynamicCard>()).Any(card => SameZone(card, blocker)))
                {
                    var partners = _ownMonsters.Where(own => own.ID > 0 && own.IsFaceup() && !SameZone(own, blocker) &&
                                                             (own.HasType(CardType.Tuner) != blocker.HasType(CardType.Tuner))).ToList();
                    var synchro = (context.StateData.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                        .Where(card => card.HasType(CardType.Synchro) && partners.Any(p => p.Level + blocker.Level == card.Level))
                        .OrderByDescending(card => card.Attack).FirstOrDefault();
                    if (synchro.ID > 0 && synchro.Attack > blocker.Attack && Rule(DeckRules.ClearZoneBlocker))
                    {
                        context.Bot.LogNote($"deck brain: synchro summon {CardNames.Get(synchro.ID)} with {CardNames.Get(blocker.ID)} as material: {stuckWhy}");
                        return synchro.SpecialSummon();
                    }
                    var tributeSummon = (context.StateData.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level is 5 or 6)
                        .OrderByDescending(card => card.Attack).FirstOrDefault();
                    if (tributeSummon.ID > 0 && Rule(DeckRules.ClearZoneBlocker))
                    {
                        PlanTributes(context.Header.CurrentTurn, new[] { blocker }, $"{CardNames.Get(tributeSummon.ID)}'s tribute (clearing the attack order)");
                        context.Bot.LogNote($"deck brain: tribute summon {CardNames.Get(tributeSummon.ID)} off {CardNames.Get(blocker.ID)}: {stuckWhy}");
                        return tributeSummon.NormalSummon();
                    }
                }
            }

            var repositionToAttack = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsDefense() && ShouldRepositionToAttack(card, opponentMonsters))
                .OrderByDescending(card => card.Attack)
                .FirstOrDefault();
            // Into a field with no cards at all the attack is a direct one, which Gorz can answer: decide about Gorz first and
            // commit (user, 2026-11-13: "this chance to hold back should've been ran before switching hamster to attack mode.
            // make the decision as early as possible and then commit to it"). The roll is kept for the turn, so the Battle
            // Phase follows the same decision.
            if (repositionToAttack.ID > 0 && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 &&
                !opponentMonsters.Any(card => card.ID != 0 || !card.IsFaceup()) && (await GetEnemySpellCardsAsync()).Count == 0 &&
                await FearGorzAsync(context) && Rule(DeckRules.HoldForGorz))
            {
                context.Bot.LogNote($"deck brain: {CardNames.Get(repositionToAttack.ID)} stays in defence: we're holding direct attacks this turn (possible Gorz)");
                repositionToAttack = default;
            }

            if (repositionToAttack.ID > 0 && Rule(DeckRules.RepositionToAttack))
            {
                ConsoleUtils.PrintColored($"Repositioning to attack: {repositionToAttack.ID}");
                return repositionToAttack.Reposition();
            }

            // Nothing can attack: don't bother entering the Battle Phase.
            if (context.StateData.CanBattlePhase && context.StateData.CanEndPhase)
            {
                var ownMonsters = await GetBotMonsterCardsAsync();
                bool hasAttacker = ownMonsters.Any(card => card.IsFaceup() && card.IsAttack());
                string? noAttack = !hasAttacker ? "no face-up attack-position monster"
                    : await BattleWorthEnteringAsync() is string why ? why : null;
                if (noAttack != null && Rule(DeckRules.SkipEmptyBattlePhase))
                {
                    // (Skipping the Battle Phase isn't logged: user, 2026-11-12.)
                    var noBattle = context.StateData;
                    noBattle.CanBattlePhase = false;
                    return await base.OnIdleCmdAsync(context with { StateData = noBattle }, ct);
                }
            }

            // Our face-up Cyber Dragon next to a face-up Machine at the end of our turn: fuse it ourselves.
            bool battleStillAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.StateData.CanBattlePhase &&
                                    (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack());
            if (await TryChimeratechSafetyAsync(context, context.StateData, battleStillAhead) is IdleCmdResponse safety)
                return safety;

            return await base.OnIdleCmdAsync(context, ct);
        }

        public override Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default) =>
            OnSelectChainCoreAsync(context, ct);

        /// <summary>
        /// After the deck brain's chain answer (every deck, real and search twin: called from RefineChainAsync, since the deck brains
        /// override OnSelectChainAsync), the holds for this window: our cards that only change ATK/DEF, offered outside the damage step
        /// and not the brain's own pick (a doomed card the bait rules chose is), so a line that uses one early pays for it.
        /// </summary>
        private async Task RecordChainHoldsAsync(DecisionContext<SelectChainData> context, int answer)
        {
            _holds.RemoveAll(h => h.Prefix == "chain:"); // a chain window's holds are its own
            var phase = (DuelPhase)context.Header.CurrentPhase;
            // Exception: their card just activated depends on ATK (Bottomless: "1500 or more ATK"): a stat change can answer it, e.g.
            // Shrink on our own monster to get under it (user, 2026-11-15).
            // "The damage step": an attack in progress (either side's). The phase reads BattleStart through the whole battle, live and in
            // DuelSim, so it can't tell (seen 2026-11-15: every damage-step boost was being held).
            // (Battle Phase only: the under-attack flag can be stale in the Main Phase - seen 2026-11-16, Skill Successor chained in Main Phase 1.)
            bool attackInProgress = phase is DuelPhase.Damage or DuelPhase.DamageCal ||
                                    phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle &&
                                    (await CurrentField.IsBotUnderAttack() || await CurrentField.IsBotUnderAttack(false));
            if (!attackInProgress && !await TheirActivationReadsAtkAsync())
            {
                var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
                for (int i = 0; i < cards.Length; i++)
                    if (cards[i].CurrentCoordinates.Controller == 0 && await OnlyChangesStatsAsync(cards[i].ID) &&
                        !await StatBoostMayGoEarlyAsync(cards[i], phase) && !await DoomedAsync(cards[i]))
                    {
                        HoldKey(cards[i].ID, "chain:", DeckRules.StatBoostInDamageStep);
                        if (!IsSearchTwin)
                            context.Bot.LogNote($"deck brain: {CardNames.Get(cards[i].ID)} held for the damage step (it only changes ATK/DEF)");
                    }
            }
            // Waboku in our own turn (S8): costs a line unless it's your legitimate case or doomed - the planner chooses, these are
            // conditions, not the brain's pick.
            if (IsOwnTurn(context.Header) && !await WabokuLegitimateOwnTurnAsync(phase))
            {
                var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
                for (int i = 0; i < offered.Length; i++)
                    if (offered[i].IsCode(WabokuId) && offered[i].CurrentCoordinates.Controller == 0 && !await DoomedAsync(offered[i]))
                        HoldKey(offered[i].ID, "chain:", DeckRules.WabokuOwnTurn);
            }
        }

        /// <summary>Our set card is about to be removed anyway: their chain targets it, or they just activated Heavy Storm.</summary>
        private async Task<bool> DoomedAsync(DynamicCard ours)
        {
            if (ours.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return false;
            if (await OurSetCardTargetedAsync(ours))
                return true;
            return _newFaceUpSpellZones.Count > 0 && (await GetEnemySpellCardsAsync())
                .Any(card => card.IsCode(HeavyStormId) && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence));
        }

        /// <summary>
        /// CSV S8, the legitimate Waboku in our own turn: our attack is under way with a monster that gains from battling (a Gladiator
        /// Beast: it tags out after), into an attack-position monster of theirs it can't beat - Waboku keeps it alive to tag out.
        /// </summary>
        private async Task<bool> WabokuLegitimateOwnTurnAsync(DuelPhase phase)
        {
            if (phase is not (DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage or DuelPhase.DamageCal))
                return false;
            var ours = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var theirs = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            return ours.ID > 0 && theirs.ID > 0 && BattleGain(ours) != null && theirs.IsAttack() && theirs.Attack >= ours.Attack &&
                   (await GetBotMonsterCardsAsync()).Any(m => m.ID == ours.ID && m.CurrentCoordinates.Sequence == ours.CurrentCoordinates.Sequence);
        }

        /// <summary>A deck's own exception to "stat changes wait for the damage step" (Glads: Indomitable in a Main Phase while it can
        /// come back from the GY for another use). Default: none.</summary>
        protected virtual Task<bool> StatBoostMayGoEarlyAsync(DynamicCard card, DuelPhase phase) => Task.FromResult(false);

        /// <summary>Their spell/trap just activated (face-up since the last decision) reads ATK: Bottomless Trap Hole and the like.</summary>
        private async Task<bool> TheirActivationReadsAtkAsync()
        {
            if (_newFaceUpSpellZones.Count == 0)
                return false;
            foreach (var card in await GetEnemySpellCardsAsync())
                if (card.ID > 0 && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence) &&
                    await CardDatabase.GetAsync(card.ID) is DBCardInfo info && StatText.IsMatch(info.CardText ?? ""))
                    return true;
            return false;
        }

        private static readonly System.Text.RegularExpressions.Regex StatText = new(@"(ATK|DEF)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex NotOnlyStats = new(
            @"destroy|negate|Special Summon|draw|add d|add it|return|take control|cannot be destroyed|unaffected|Life Points|inflict|Set|Tribute",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A spell/trap whose effects only change ATK/DEF (Skill Successor, Rush Recklessly, Shrink): its text speaks of ATK or
        /// DEF and of nothing that removes, negates, summons, draws, searches, protects or burns.</summary>
        protected static async Task<bool> OnlyChangesStatsAsync(int cardId)
        {
            if (cardId <= 0)
                return false;
            // The engine's own card data first (always loaded, live and in DuelSim), Omega's database otherwise.
            if (Search.SearchHost.Available && Search.SearchHost.Cards.Get(cardId) is { } r)
                return (r.Type & 0x6) != 0 && StatText.IsMatch(r.Text ?? "") && !NotOnlyStats.IsMatch(r.Text ?? "");
            return await CardDatabase.GetAsync(cardId) is DBCardInfo info && (info.Type & 0x6) != 0 &&
                   StatText.IsMatch(info.CardText ?? "") && !NotOnlyStats.IsMatch(info.CardText ?? "");
        }

        private async Task<int> OnSelectChainCoreAsync(DecisionContext<SelectChainData> context, CancellationToken ct)
        {
            // Our Goyo Guardian taking the monster it destroyed: always (seen 2026-11-10: Vayu passed on taking Card Trooper).
            if (OwnTakeControlTrigger(context) is int takeControl)
                return takeControl;
            await PrepareContextAsync(context, ct);
            RegisterChainDescriptions(context.StateData.Descriptions ?? Array.Empty<int>());
            // Shield Warrior / Waboku saving our battling monster (either turn).
            if (await TryChainBattleProtectionAsync(context, context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()) is int protect)
                return protect;
            if (!HandlesStapleChains)
            {
                var staple = await TryStapleChainAsync(context);
                if (staple.HasValue)
                    return staple.Value;
            }
            return await base.OnSelectChainAsync(context, ct);
        }

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored($"[DECK BOT BRAIN] [OnSelectCard] => {(DHint)context.StateData.SelectionHint}");

            var stapleTargets = StapleTargets(context);
            if (stapleTargets != null)
                return stapleTargets;
            if (await AllureBanishPickAsync(context) is IList<byte> allureBanish)
                return allureBanish;

            // Fusion materials we planned (Chimeratech Fortress Dragon's safety fuse, Overload Fusion...).
            if (SelectPlannedMaterials(context, context.StateData.SelectableCards ?? Array.Empty<DynamicCard>(),
                    context.StateData.Minimum, context.StateData.Maximum, context.StateData.SelectionHint) is IList<byte> materials)
                return materials;

            // Brionac's discards and targets, as planned when it was activated.
            if (BrionacPick(context) is IList<byte> brionacPick)
                return brionacPick;

            // Removal on the opponent's cards (not plain "target", which may be one of ours): the shared best-target choice.
            if ((DHint)context.StateData.SelectionHint is DHint.DESTROY or DHint.REMOVE or DHint.RTOHAND or DHint.TODECK &&
                await SelectOpponentTargetsAsync(context) is IList<byte> removal)
                return removal;

            // Which monster to Special Summon from the Extra Deck (e.g. Vayu's synchro, when several are possible): the
            // one that answers a threat our board can't, otherwise the biggest. Never just the first in the list.
            var spOptions = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (context.StateData.SelectionHint == (int)DHint.SPSUMMON && spOptions.Length > 1 &&
                spOptions.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Extra && card.ID > 0))
            {
                await LearnBattleDestroyersAsync(spOptions.Select(card => card.ID));
                var enemy = await GetEnemyMonsterCardsAsync();
                var ourAtk = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup()).Select(card => card.Attack).ToList();
                int handCards = (await GetBotHandCardsAsync()).Count;
                var ranked = spOptions.Select((card, index) =>
                    {
                        var option = new SynchroOption(card.ID, card.Level, card.Attack,
                            BattleDestroyersExcept.TryGetValue(card.ID, out var spared) ? spared : null, card.Attribute);
                        return (Index: index, Card: card, Answer: SynchroAnswer(option, enemy, ourAtk, handCards));
                    })
                    .OrderByDescending(x => x.Answer != null)
                    .ThenByDescending(x => x.Card.Attack)
                    .ToList();
                int take = Math.Max(1, context.StateData.Minimum);
                var picks = ranked.Take(take).ToList();
                context.Bot.LogNote($"deck brain: Extra Deck summon: {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))}" +
                                    (picks[0].Answer != null ? $" ({picks[0].Answer})" : $" (the biggest on offer, {picks[0].Card.Attack} ATK)"));
                return picks.Select(x => (byte)x.Index).ToList();
            }

            if (context.StateData.SelectionHint == (int)DHint.TARGET)
            {
                byte[] botCards = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderByDescending(x => x.Card.Attack)
                    .Take(context.StateData.Minimum)
                    .Select(x => (byte)x.Index)
                    .ToArray();

                if (botCards.Any())
                {
                    ConsoleUtils.PrintColored($"Selected {botCards.Length} cards for TARGET.");
                    return botCards;
                }
            }

            if (context.StateData.SelectionHint == (int)DHint.ATTACKTARGET)
            {
                ConsoleUtils.PrintColored("OnSelectCard - Selecting Defender");
                // Goyo-type attackers: their recruiters are held as targets, so a line that takes one pays for it (user, 2026-11-17).
                if (StealsWhatItDestroys(_declaredAttacker))
                    foreach (var offeredTarget in context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                        if (offeredTarget.ID > 0 && offeredTarget.IsFaceup() && IsRecruiter(offeredTarget) && !StealSpeedsUpClock(_declaredAttacker, offeredTarget) &&
                            Rule(DeckRules.NoStealRecruiter))
                            HoldPick(offeredTarget.ID, DeckRules.NoStealRecruiter);

                if (_selectedDefenderCard.HasValue)
                {
                    var selectedDefender = _selectedDefenderCard.Value;
                    if ((context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()).TryGetByIDWithZone(selectedDefender.ID, selectedDefender.CurrentCoordinates.Sequence, out _, out int selectedIndex))
                    {
                        ConsoleUtils.PrintColored($"Selecting defender by card code and sequence: {selectedDefender.ID} / {selectedDefender.CurrentCoordinates.Sequence}");
                        _battledThisTurn.Add((context.Header.CurrentTurn, selectedDefender.CurrentCoordinates.Sequence, selectedDefender.ID));
                        return new List<byte> { (byte)selectedIndex };
                    }

                    // Face-down targets show no id to the bot, so match on the opponent's zone instead: only a face-down card
                    // there (seen 2026-11-10: an aim at "the face-down monster in zone 2" took the face-up Gorz in that zone).
                    int zoneIndex = Array.FindIndex(context.StateData.SelectableCards ?? Array.Empty<DynamicCard>(),
                        card => card.CurrentCoordinates.Controller != 0 && card.CurrentCoordinates.Sequence == selectedDefender.CurrentCoordinates.Sequence &&
                                (card.ID <= 0 || !card.IsFaceup()));
                    if (zoneIndex >= 0)
                    {
                        ConsoleUtils.PrintColored($"Selecting defender by zone: {selectedDefender.CurrentCoordinates.Sequence}");
                        return new List<byte> { (byte)zoneIndex };
                    }
                }
                // No planned target on offer (an attack lock like Dupe Frog's, or the board changed): pick on purpose, and say
                // why (never silently: round-robin 1 / 21). What the attacker beats, the biggest threat first; else what costs
                // the least damage.
                var offered = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()).Select((card, i) => (Card: card, Index: i)).ToList();
                if (offered.Count > 0)
                {
                    int atk = _declaredAttacker.ID > 0 ? _declaredAttacker.Attack : 0;
                    int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
                    var pick = offered.Where(x => atk > Power(x.Card)).OrderByDescending(x => TargetThreat(x.Card)).Select(x => (int?)x.Index).FirstOrDefault()
                               ?? offered.OrderBy(x => Math.Max(0, Power(x.Card) - atk) * (x.Card.IsFaceup() && x.Card.IsAttack() ? 1 : 0))
                                   .ThenBy(x => Power(x.Card)).First().Index;
                    var chosen = offered[pick].Card;
                    string planned = _selectedDefenderCard is DynamicCard p ? $"the planned target ({DescribeTarget(p)} in zone {p.CurrentCoordinates.Sequence + 1}) isn't on offer" : "no target was planned";
                    AimAt(context.Bot, chosen, AimKind.Fallback, $"attack target: {planned}; " +
                        (atk > Power(chosen) ? $"{DescribeTarget(chosen)} is the best one {CardNames.Get(_declaredAttacker.ID)} ({atk}) beats"
                                             : $"{DescribeTarget(chosen)} costs the least ({CardNames.Get(_declaredAttacker.ID)} {atk} beats none of {offered.Count})"));
                    _battledThisTurn.Add((context.Header.CurrentTurn, chosen.CurrentCoordinates.Sequence, chosen.ID));
                    return new List<byte> { (byte)pick };
                }
            }

            if (context.StateData.SelectionHint is (int)DHint.DESTROY or (int)DHint.REMOVE)
            {
                string actionName = context.StateData.SelectionHint == (int)DHint.DESTROY ? "DESTROY" : "REMOVE";
                ConsoleUtils.PrintColored($"OnSelectCard - Selecting Card to {actionName}");

                byte[] playerCards = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                    .Select((card, index) => new { Card = card, Index = index })
                    .Where(x => x.Card.Owner == 1)
                    .Take(context.StateData.Minimum)
                    .Select(x => (byte)x.Index)
                    .ToArray();

                if (playerCards.Any())
                {
                    ConsoleUtils.PrintColored($"Selected {playerCards.Length} cards for {actionName}.");
                    return playerCards;
                }

                // Only our own cards on offer (a forced destroy, e.g. Ryko's flip with their field empty): the one we lose
                // least: a spent flip monster (Ryko itself), a token, then the weakest monster; spells/traps last.
                var ours = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                    .Select((card, index) => (Card: card, Index: index))
                    .Where(x => x.Card.CurrentCoordinates.Controller == 0)
                    .ToList();
                if (ours.Count > 0 && ours.Count == (context.StateData.SelectableCards?.Length ?? 0))
                {
                    var cheapest = ours
                        .OrderBy(x => IsSpentFlipMonster(x.Card) ? 0 : x.Card.HasType(CardType.Token) ? 1 : x.Card.HasType(CardType.Monster) ? 2 : 3)
                        .ThenBy(x => x.Card.HasType(CardType.Monster) ? x.Card.Attack : 0)
                        .Take(Math.Max(1, context.StateData.Minimum))
                        .ToList();
                    context.Bot.LogNote($"deck brain: {actionName}: only our own cards on offer, the cheapest: {string.Join(", ", cheapest.Select(x => CardNames.Get(x.Card.ID)))}");
                    return cheapest.Select(x => (byte)x.Index).ToList();
                }
            }

            // Reviving one of our own monsters (Call of the Haunted, Premature Burial, Monster Reborn on our GY) with no plan for it: not
            // one whose effect works from the graveyard (user, 2026-11-16: "a penalty for selecting a monster with a graveyard effect
            // from it. that's just terrible play" - Call of the Haunted on Shield Warrior). Those are held, so a line that revives one
            // pays for it, even when it's the only choice.
            if (context.StateData.SelectionHint == (int)DHint.SPSUMMON && context.StateData.Maximum == 1 &&
                context.StateData.SelectableCards is { Length: > 0 } revivable &&
                revivable.All(card => card.CurrentCoordinates.Controller == 0 && card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.ID > 0))
            {
                var ranked = new List<(DynamicCard Card, int Index, bool GraveEffect)>();
                for (int i = 0; i < revivable.Length; i++)
                    ranked.Add((revivable[i], i, await CardDatabase.GetAsync(revivable[i].ID) is DBCardInfo info && EffectEvaluator.HasGraveyardEffect(info.CardText ?? "")));
                var pick = ranked.OrderBy(x => x.GraveEffect).ThenByDescending(x => x.Card.Attack).First();
                if (Rule(DeckRules.NoReviveGraveEffect))
                    foreach (var held in ranked.Where(x => x.GraveEffect).Select(x => x.Card.ID).Distinct())
                        HoldPick(held, DeckRules.NoReviveGraveEffect);
                context.Bot.LogNote($"deck brain: revive {CardNames.Get(pick.Card.ID)}" +
                                    (pick.GraveEffect ? " (the only choice: its graveyard effect is lost; the search's lines pay for it)" : ""));
                return new List<byte> { (byte)pick.Index };
            }

            ConsoleUtils.PrintColored("No specific defender index available, using default selection.");
            return await base.OnSelectCardAsync(context, ct);
        }

        /// <summary>
        /// Select/unselect prompts (e.g. Dark Grepher's summon cost, Chaos Sorcerer's banish cost) carry the same data as
        /// a normal card selection, so they go through the deck's own card choice (discard priorities, cost priorities)
        /// instead of the default "first card listed", which once threw away Gorz with Sirocco right next to it.
        /// The prompt repeats one card at a time; with nothing more required (minimum 0), finish.
        /// </summary>
        public override async Task<IList<byte>?> OnSelectUnselectCardAsync(DecisionContext<SelectUnselectCardData> context, CancellationToken ct = default)
        {
            var data = context.StateData;
            var cards = data.SelectableCards ?? Array.Empty<DynamicCard>();
            // Fusion materials we planned: all of them, not just the minimum.
            if (cards.Length > 0 && SelectPlannedMaterials(context, cards, data.Minimum, data.Maximum, data.SelectionHint, onePerPrompt: true) is IList<byte> materials)
                return materials;
            if (data.Minimum <= 0 || cards.Length == 0)
                return await base.OnSelectUnselectCardAsync(context, ct);
            var asSelect = new SelectCardData
            {
                SelectableCards = cards,
                Minimum = 1,
                Maximum = 1,
                SelectionHint = data.SelectionHint,
                Cancelable = data.Cancelable,
                SelectableCount = data.SelectableCount
            };
            var choice = await OnSelectCardAsync(new DecisionContext<SelectCardData>
            {
                Bot = context.Bot,
                State = context.State,
                Header = context.Header,
                StateData = asSelect,
                Field = context.Field,
                Features = context.Features
            }, ct);
            if (choice is { Count: > 0 } && choice[0] < cards.Length)
                return new List<byte> { choice[0] };
            return await base.OnSelectUnselectCardAsync(context, ct);
        }

        public override async Task<int> OnAnnounceCardAsync(DecisionContext<AnnounceCardData> context, CancellationToken ct = default)
        {
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnAnnounceCard");
            var cards = await DuelBotWrapper.GetMatchingCards(context.StateData.AppliableFilters ?? Array.Empty<int>());
            return BotActionUtilities.SelectRandomAnnouncedCard(cards, preferAlias: true);
        }

        /// <summary>A new bot (a new duel) to follow: a fresh GameField, and the per-duel memory cleared. From the first prompt, or
        /// earlier from the turn-start callback (DeckBotBrain.TurnLog), whichever comes first.</summary>
        private void AttachToBot(DuelBotWrapper bot)
        {
            _trackedBot = bot;
            _gameField = new GameField(bot);
            if (!IsSearchTwin)
                bot.KnownOpponentHand = () => KnownEnemyHand(); // the log's "their hand" line
            _trackedTurn = -1;
            _activatedThisTurn.Clear();
            _usedEffects.Clear();
            _chainDescriptions.Clear();
            PendingAction = default;
            OpponentMemory.Clear();
            Summons.Clear();
            ResetMatchup();
            OpponentDeck.Clear();
        }

        protected async Task<GameField> PrepareContextAsync<TState>(DecisionContext<TState> context, CancellationToken ct = default)
        {
            _rollChance = null; // a former roll's chance belongs to the decision that computed it
            // The tuning file's numbers (Tuning, bot-tuning.txt), once per brain, after its own constructor set its defaults.
            if (!_tuned)
            {
                _tuned = true;
                Tuning.ApplyTo(this, _sharedDefaults);
                if (!IsSearchTwin && Tuning.FilePath != null)
                    context.Bot.LogNote($"tuning: {Tuning.FilePath} ({Tuning.Overridden} number(s) differ from the built-in values)");
            }
            if (_trackedBot != context.Bot || _gameField == null)
                AttachToBot(context.Bot);

            var header = context.Header;
            _currentHeader = header;
            _prepareCount++; // a new read of the board: the risk tolerance is recomputed when asked
            // Main-phase and battle prompts only happen on our own turn: remember that turn's parity.
            if (context.StateData is IdleCmdData or BattleCmdData)
                _ownTurnParity = header.CurrentTurn % 2;
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

            // The first prompt of every turn: get the play search ready (cards scored, engines loaded).
            if (!IsSearchTwin)
                WarmUpAtTurnStart(context.Bot, header);
            var refresh = RefreshFieldAsync();
            await refresh;
            if (context.StateData is IdleCmdData && !IsSearchTwin)
                await TrackBleedingAsync(header.CurrentTurn);
            // Remember face-up opponent monsters at every decision (no extra API call: reuses the refreshed field).
            if (refresh is Task<FieldInfo> fieldTask)
            {
                OpponentMemory.ObserveZoneIds(fieldTask.Result.EnemySide);
                TrackEnemyArrivals(fieldTask.Result.EnemySide, context);
                ObserveTurnRecord(context.Bot, header, fieldTask.Result);
                ObserveReveals(fieldTask.Result);
                await SnapshotOwnZonesAsync(header);
                if (!IsSearchTwin)
                    await ObserveTempBoostsAsync(header, fieldTask.Result);
                await ObserveMonstersBeforeTheirBattleAsync(header);
                await TrackVayuNegationAsync(fieldTask.Result);
                _ownHandIds = fieldTask.Result.OwnSide.Hand ?? Array.Empty<int>();
                _theirLifePoints = fieldTask.Result.EnemySide.LifePoints;
                _weCanProtect = Search.SearchHost.Available && (fieldTask.Result.OwnSide.SpellZones ?? Array.Empty<CardArray>())
                    .Select(zone => (zone.Cards ?? Array.Empty<int>()).FirstOrDefault()).Where(id => id > 0)
                    .Any(id => Search.SearchHost.Cards.Get(id) is { } c && Search.SearchCardValues.Protection(c.Type, c.Text) > 0);
                OnFieldRefreshed(fieldTask.Result);
                // Real-duel bookkeeping, skipped by the play search's brain twins (they play thousands of imagined decisions):
                // how Extra Deck monsters got out (one not properly summoned can't be revived or taken), and the matchup.
                if (!IsSearchTwin)
                {
                    await CardDatabase.EnsureAsync(new[] { fieldTask.Result.OwnSide, fieldTask.Result.EnemySide }
                        .SelectMany(side => (side.MonsterZones ?? Array.Empty<CardArray>()).SelectMany(zone => zone.Cards ?? Array.Empty<int>())
                            .Concat(side.Grave ?? Array.Empty<int>())));
                    Summons.Observe(fieldTask.Result);
                    ObserveMatchup(fieldTask.Result);
                }
                _ourDeckCount = fieldTask.Result.OwnSide.Main?.Length ?? 40;
                if (!IsSearchTwin)
                {
                    var knownBefore = OpponentDeck.KnownHand();
                    if (OpponentDeck.ObserveField(fieldTask.Result.EnemySide) is string deckNote)
                        context.Bot.LogNote($"deck brain: {deckNote}");
                    var knownNow = OpponentDeck.KnownHand();
                    await ObserveUnseenSearchesAsync(context.Bot, fieldTask.Result.EnemySide);
                    await UpdateSpecialSummonLockAsync(fieldTask.Result);
                    // The brain's battle maths (BoardAssessment.Defenders) count a known Honest.
                    OpponentMemory.KnownHonest = KnownEnemyHand().Count(id => id == HonestId || Search.SearchHost.CanonicalId(id) == HonestId);
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
                // What the MCTS should search in the dead time after this prompt; and whether Omega shows our deck order.
                if (!IsSearchTwin)
                {
                    ProbeDeckOrder(context.Bot, header, fieldTask.Result);
                    await RetargetDeadTimeAsync(context.Bot, header, fieldTask.Result);
                }
            }
            // Grand Mole on their field / back in their hand: tracked at every decision, so a Mole that comes out,
            // bounces and returns to their hand within one Battle Phase isn't missed.
            bool bouncerKnown = BouncerThreat;
            await TrackBouncersAsync();
            if (!bouncerKnown && _bouncerInTheirHand)
                context.Bot.LogNote("deck brain: their Grand Mole-type monster went back to their hand: it can come straight back (don't set into it)");
            return CurrentField; // set by AttachToBot above (the compiler can't see that through the call)
        }

        // ------------------------------------------------------------------ Vayu-negated monsters

        // Blackwing - Vayu the Emblem of Honor: banish it + 1 non-Tuner "Blackwing" from the graveyard to Special Summon
        // 1 "Blackwing" Synchro whose Level equals their total Levels, with its effects negated.
        private const int VayuEmblemOfHonorId = 72714392;
        private const int BlackwingSetCode = 0x33;
        private readonly List<int>?[] _lastBanished = { null, null };
        // Per side: the Vayu summon on its way (turn, Level of the synchro it will summon), until that synchro arrives.
        private readonly (int Turn, int Level)?[] _vayuPending = { null, null };
        private readonly int[][] _monsterZoneIds = { new int[7], new int[7] };
        private readonly HashSet<(int Side, int Sequence, int Id)> _vayuNegated = new();

        /// <summary>
        /// Whether this monster's effects are negated. Omega never reports it (Status is always 0), so a Vayu-summoned
        /// synchro is tracked from its summon: when a Vayu is banished together with a partner, the synchro it summons
        /// is the "Blackwing" Synchro of their total Level arriving on that side (this turn).
        /// </summary>
        protected bool IsEffectNegated(DynamicCard card) =>
            card.IsDisabled() || _vayuNegated.Contains((card.CurrentCoordinates.Controller, card.CurrentCoordinates.Sequence, card.ID));

        private static bool IsBlackwing(DBCardInfo info)
        {
            for (long set = info.SetCode; set != 0; set >>= 16)
                if ((set & 0xFFF) == BlackwingSetCode)
                    return true;
            return false;
        }

        private async Task TrackVayuNegationAsync(FieldInfo field)
        {
            // A negated monster turned face-down (Book of Moon) gets its effects back for good, even once flipped up
            // again, so forget it the moment it's seen face-down.
            // (Read fresh, and only while something is marked: the cached monster lists can be a decision old.)
            if (_vayuNegated.Count > 0)
            {
                var sidesNow = new[] { await GetBotMonsterCardsAsync(), await GetEnemyMonsterCardsAsync() };
                for (int side = 0; side < 2; side++)
                    foreach (var card in sidesNow[side].Where(card => !card.IsFaceup()))
                        _vayuNegated.RemoveWhere(x => x.Side == side && x.Sequence == card.CurrentCoordinates.Sequence);
            }
            var sides = new[] { field.OwnSide, field.EnemySide };
            int turn = CurrentHeader.CurrentTurn;
            for (int side = 0; side < 2; side++)
            {
                // Newly banished cards on this side. Vayu's cost banishes it with its partner first; the synchro arrives
                // a few decisions later (its selection, zone and position prompts come in between).
                var banished = (sides[side].Banish ?? Array.Empty<int>()).ToList();
                if (_lastBanished[side] is List<int> before)
                {
                    var added = new List<int>(banished);
                    foreach (int id in before)
                        added.Remove(id);
                    if (added.Remove(VayuEmblemOfHonorId))
                    {
                        int level = 1; // Vayu
                        foreach (int partner in added)
                            if (await CardDatabase.GetAsync(partner) is DBCardInfo p && (p.Type & (int)CardType.Monster) != 0)
                                level += p.Level;
                        if (added.Count == 1)
                            _vayuPending[side] = (turn, level);
                    }
                }
                _lastBanished[side] = banished;

                var zones = sides[side].MonsterZones ?? Array.Empty<CardArray>();
                for (int seq = 0; seq < _monsterZoneIds[side].Length; seq++)
                {
                    int id = seq < zones.Length && zones[seq].Cards is { Length: > 0 } cards ? cards[0] : 0;
                    if (id == _monsterZoneIds[side][seq])
                        continue;
                    // The zone changed: whatever was negated there has left.
                    _vayuNegated.RemoveWhere(x => x.Side == side && x.Sequence == seq);
                    _monsterZoneIds[side][seq] = id;
                    // The synchro this Vayu summons: a "Blackwing" Synchro of the expected Level, this turn.
                    if (_vayuPending[side] is (int pendingTurn, int pendingLevel) && pendingTurn == turn && id > 0 &&
                        await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Synchro) != 0 &&
                        info.Level == pendingLevel && IsBlackwing(info))
                    {
                        _vayuNegated.Add((side, seq, id));
                        _vayuPending[side] = null;
                    }
                }
                if (_vayuPending[side] is (int t, _) && t != turn)
                    _vayuPending[side] = null;
            }
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

        /// <summary>
        /// Set by a deck brain that already logged its "don't set: Heavy Storm" hold in this prompt, so the shared rule
        /// doesn't repeat it. The deck brain clears it at the start of each prompt; the shared rule clears it once read.
        /// </summary>
        protected bool StormHoldNoted { get; set; }

        protected GameField CurrentField =>
            _gameField ?? throw new InvalidOperationException("The deck brain field is not initialized. Call PrepareContextAsync first.");

        protected BotStateHeader CurrentHeader => _currentHeader;

        protected virtual Task RefreshFieldAsync() => CurrentField.GetBotField();

        // ------------------------------------------------------------------ the opponent's Gorz

        /// <summary>Gorz the Emissary of Darkness: Special Summoned from the hand when its owner, controlling no cards, takes direct-attack damage.</summary>
        protected const int GorzId = 44330098;
        protected const int GorzAttack = 2700;

        /// <summary>Chance per card in the opponent's hand that it is Gorz (about 1 copy in 40). Tune from logs.</summary>
        [Tunable("Chance per card in the opponent's hand that it is Gorz (about 1 copy in 40). Tune from logs.")]
        public double GorzChancePerCard { get; set; } = 0.025;

        private int _gorzDecisionTurn = -1;
        private bool _gorzSkip;

        /// <summary>
        /// Into a completely empty field, a direct attack may let the opponent Special Summon Gorz (+ a token). Decide
        /// once per turn, probabilistically: the chance they hold it (from their hand size; none if their copy is
        /// already in the graveyard/banished), lower if our board can beat a 2700 Gorz, and lower when holding back
        /// gives up a fast clock.
        /// </summary>
        protected async Task<bool> FearGorzAsync<T>(DecisionContext<T> context)
        {
            int turn = context.Header.CurrentTurn;
            if (turn < _gorzDecisionTurn)
            {
                _knownGorzInOpponentHand = _gorzWasOnEnemyField = false; // a new duel started
                _gorzClearedCards = _gorzOpportunityHand = 0;
                _gorzSetHeldCards = 0;
                _gorzSetResidual = 1;
                _gorzPassFactor = 1;
            }
            if (_gorzDecisionTurn == turn)
            {
                if (_gorzSkip)
                    context.Bot.LogNote("deck brain: still holding direct attacks this turn (possible Gorz)");
                return _gorzSkip;
            }
            _gorzDecisionTurn = turn;
            _gorzSkip = false;

            var (haveGorz, hand, cleared, setHeld) = await GorzInHandChanceAsync();
            if (haveGorz <= 0)
                return false;
            // They passed completely (empty field, nothing played): x GorzPassBonus on top (user, 2026-11-18: "if I pass completely, increase my
            // gorz chance by 30%").
            if (_gorzPassFactor > 1)
                haveGorz = Math.Min(1, haveGorz * GorzPassBonus); // "not +30%, *1.3"
            int tokenAtk = context is DecisionContext<BattleCmdData> battle && battle.StateData.AttackableCards is { Length: > 0 } attackers
                ? attackers[0].Attack : FirstHitAttack();
            var (canAnswer, answerWhy) = await CanAnswerGorzAndTokenAsync(tokenAtk);
            double clockFactor = _clockSpeed switch
            {
                ClockSpeed.Quick => GorzQuickClockFactor,
                ClockSpeed.Long => GorzLongClockFactor,
                _ => 1.0
            };
            double chance = haveGorz * (canAnswer ? GorzAnswerableFactor : 1.0) * clockFactor;
            double roll = RollAt;
            _gorzSkip = roll < chance;
            // A known Gorz is no dice roll: hold exactly when we can't answer it, every turn alike (user, 2026-11-12: "if you
            // were afraid of gorz last turn, when you were in a better position, why are you not afraid of it this turn when
            // you're in a much worse position?!" - the 50% roll had flipped).
            if (_knownGorzInOpponentHand)
            {
                _gorzSkip = !canAnswer;
                roll = _gorzSkip ? 0 : 1;
            }
            context.Bot.LogNote($"deck brain: possible Gorz? chance to hold back {chance:P0} (holds it {haveGorz:P0} " +
                                $"{(_knownGorzInOpponentHand ? "(known: seen going to their hand)" : $"from {hand} cards{(cleared > 0 ? $", {cleared} of them held through an earlier Gorz chance they didn't take" : "")}{(setHeld > 0 ? $", {setHeld} held while they set spells/traps" : "")}")}" +
                                $"{(canAnswer ? $", we can answer it: {answerWhy}" : $", we can't answer it and its token: {answerWhy}")}, clock {_clockSpeed}) rolled {roll:0.00}" +
                                $" -> {(_gorzSkip ? "don't attack directly" : "attack")}");
            return _gorzSkip;
        }

        /// <summary>
        /// The chance their hand holds Gorz (1 when known; 0 when their copy is gone or their hand is empty). Cards they held
        /// through a direct hit into their empty field without Gorzing probably aren't Gorz; cards they held while setting
        /// spells/traps (which switches Gorz off) are less likely Gorz; cards drawn since keep the full chance.
        /// </summary>
        private async Task<(double Chance, int Hand, int Cleared, int SetHeld)> GorzInHandChanceAsync()
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            int hand = enemy.Hand?.Length ?? 0;
            bool gone = (enemy.Grave ?? Array.Empty<int>()).Contains(GorzId) || (enemy.Banish ?? Array.Empty<int>()).Contains(GorzId);
            return GorzInHandChance(hand, gone);
        }

        /// <summary>The brain's Gorz chance for their hand (per card, raised after a pass, lowered by evidence), synchronously.</summary>
        private (double Chance, int Hand, int Cleared, int SetHeld) GorzInHandChance(int hand, bool gone)
        {
            if (hand == 0 || gone)
                return (0, hand, 0, 0);
            int cleared = Math.Min(_gorzClearedCards, hand);
            int setHeld = Math.Min(_gorzSetHeldCards, hand - cleared);
            double haveGorz = _knownGorzInOpponentHand ? 1
                : 1 - Math.Pow(1 - Math.Min(1, GorzChancePerCard * _gorzPassFactor), hand - cleared - setHeld) *
                      Math.Pow(1 - GorzChancePerCard * EvidenceResidual, cleared) *
                      Math.Pow(1 - GorzChancePerCard * _gorzSetResidual, setHeld);
            return (haveGorz, hand, cleared, setHeld);
        }

        /// <summary>
        /// The brain's Gorz chance for a search's position, with the pass bonus (FearGorzAsync's number): the search's win chance uses the larger
        /// of it and the deck read's (user, 2026-11-18: "the lethal line was risky - it only worked through gorz, did it factor that in?": the
        /// lines counted only the deck read's chance).
        /// </summary>
        private protected double BrainGorzChance(Search.SearchView view)
        {
            bool gone = view.EnemyGrave.Concat(view.EnemyBanished).Any(id => id == GorzId || Search.SearchHost.CanonicalId(id) == GorzId);
            double chance = GorzInHandChance(view.EnemyHandCount, gone).Chance;
            return _gorzPassFactor > 1 ? Math.Min(1, chance * GorzPassBonus) : chance;
        }

        /// <summary>Gorz chance from which Main Phase 1 sets up the attack order for it (see GorzAttackOrderAsync). Tune from logs.</summary>
        [Tunable("Gorz chance from which Main Phase 1 sets up the attack order for it (see GorzAttackOrderAsync). Tune from logs.")]
        public double GorzOrderChance { get; set; } = 0.3;

        /// <summary>
        /// Main Phase 1, their field without a single card and Gorz likely in their hand: the first direct hit (the server
        /// attacks in zone order) brings Gorz and a token with that hit's ATK. Lead with a weaker monster so one behind it
        /// beats the token this turn (user, 2026-11-13: "you can still try and engineer it by switching things to defence"):
        /// when the current first attacker leaves a token nothing of ours beats, the monsters ahead of the best leader go to
        /// defence (they don't attack this turn; a Fortress in defence still answers Gorz). Returns the monster to switch
        /// now and why, or null. Not when the first hit alone wins.
        /// </summary>
        protected async Task<(DynamicCard Card, string Why)?> GorzAttackOrderAsync(IdleCmdData state, List<DynamicCard> opponents)
        {
            if (opponents.Any(card => card.ID != 0 || !card.IsFaceup()) || (await GetEnemySpellCardsAsync()).Count > 0)
                return null;
            var attackers = _ownMonsters.Where(own => own.ID > 0 && own.IsFaceup() && own.IsAttack() && !own.Attacked)
                .OrderBy(own => own.CurrentCoordinates.Sequence).ToList();
            if (attackers.Count < 2)
                return null;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (attackers[0].Attack >= oppLp)
                return null;
            var (chance, _, _, _) = await GorzInHandChanceAsync();
            if (chance < GorzOrderChance)
                return null;
            bool Clears(int lead) => attackers.Skip(lead + 1).Any(m => m.Attack > attackers[lead].Attack);
            if (Clears(0))
                return null;
            var reposable = state.ReposableCards ?? Array.Empty<DynamicCard>();
            for (int lead = 1; lead < attackers.Count; lead++)
            {
                if (!Clears(lead))
                    continue;
                // Everything ahead of the new leader must be able to go to defence.
                var ahead = attackers.Take(lead).ToList();
                if (!ahead.All(m => reposable.Any(r => SameZone(r, m))))
                    return null;
                var switchNow = ahead[0];
                var beater = attackers.Skip(lead + 1).First(m => m.Attack > attackers[lead].Attack);
                return (reposable.First(r => SameZone(r, switchNow)),
                    $"Gorz is likely in their hand ({chance:P0}): {CardNames.Get(attackers[0].ID)} would hit first and its {attackers[0].Attack} token " +
                    $"would be bigger than anything we have left; in defence, {CardNames.Get(attackers[lead].ID)} leads ({attackers[lead].Attack} token) " +
                    $"and {CardNames.Get(beater.ID)} beats the token this turn");
            }
            return null;
        }

        /// <summary>
        /// Main Phase 1: their unknown face-down likely banishes / bounces what battles it (D.D. Warrior Lady), and our first
        /// attacker in zone order is too valuable to trade into it while a cheaper one behind it isn't (user, 2026-11-13: "i
        /// would've attacked with dark grepher first - my deck had a high chance of dd warrior lady"). The monsters ahead of the
        /// cheaper one go to defence, one per decision, so it leads. Same weighing as the battle's own check: losing the
        /// attacker (its ATK in thousands) when it is one, against destroying the face-down (1 card) when it isn't.
        /// </summary>
        protected async Task<(DynamicCard Card, string Why)?> BattleRemovalOrderAsync(IdleCmdData state, List<DynamicCard> opponents)
        {
            var faceDown = opponents.Where(card => !card.IsFaceup() && card.CurrentCoordinates.Location != 0 && !OpponentMemory.TryRecall(card, out _))
                .OrderBy(card => card.CurrentCoordinates.Sequence).FirstOrDefault();
            if (faceDown.CurrentCoordinates.Location == 0)
                return null;
            var attackers = _ownMonsters.Where(own => own.ID > 0 && own.IsFaceup() && own.IsAttack() && !own.Attacked)
                .OrderBy(own => own.CurrentCoordinates.Sequence).ToList();
            if (attackers.Count < 2)
                return null;
            var (chance, removers) = await FaceDownRemovesAttackerChanceAsync(faceDown);
            if (chance <= 0)
                return null;
            bool TooValuable(DynamicCard m) => !IsExpendable(m) && chance * Math.Max(1, BotTest.Game.AI.Search.MctsEvaluator.AtkValue(m.Attack)) > 1 - chance;
            bool CanProbe(DynamicCard m) => !TooValuable(m) && m.Attack > OpponentMemory.FaceDownDefense(faceDown);
            if (!TooValuable(attackers[0]))
                return null;
            int lead = attackers.FindIndex(CanProbe);
            if (lead <= 0)
                return null;
            var reposable = state.ReposableCards ?? Array.Empty<DynamicCard>();
            var ahead = attackers.Take(lead).ToList();
            if (!ahead.All(m => reposable.Any(r => SameZone(r, m))))
                return null;
            return (reposable.First(r => SameZone(r, ahead[0])),
                $"their face-down may well be {removers} ({chance:P0}), which removes what battles it: {CardNames.Get(attackers[lead].ID)} " +
                $"should hit it first, and the server attacks in zone order");
        }

        private readonly Random _gorzRandom = new();

        // Known Gorz in the opponent's hand: seen there (e.g. revealed by Trap Dustshoot), or it left their field
        // without reaching the graveyard or banishment (returned to the hand). Forgotten once it shows up anywhere else.
        private bool _knownGorzInOpponentHand;
        private bool _gorzWasOnEnemyField;

        /// <summary>Deck brains call this with revealed opponent hand cards (e.g. from Trap Dustshoot).</summary>
        protected void ObserveRevealedOpponentHand(IEnumerable<DynamicCard> revealed)
        {
            var cards = revealed.ToList();
            if (cards.Any(card => BoardAssessment.KnownId(card) == GorzId))
                _knownGorzInOpponentHand = true;
            ObserveRevealedStaples(cards);
        }

        // Cards in their hand when they set spells/traps (Gorz needs their field empty, so a Gorz holder doesn't set), and
        // the share of their Gorz chance those cards keep.
        private int _gorzSetHeldCards;
        private double _gorzSetResidual = 1;

        /// <summary>
        /// Cards held while setting keep this share of their Gorz chance per spell/trap of theirs on the field (0.3 for one,
        /// 0.09 for two). Tune from logs.
        /// </summary>
        [Tunable("Cards held while setting keep this share of their Gorz chance per spell/trap of theirs on the field (0.3 for one, 0.09 for two). Tune from logs.")]
        public double GorzSetResidual { get; set; } = 0.3;

        // Their deliberate pass with a big hand and an empty field: per-card Gorz chance multiplier (1 = none).
        private double _gorzPassFactor = 1;

        /// <summary>Hand size from which passing with an empty field and nothing played reads as holding Gorz. Tune from logs.</summary>
        [Tunable("Hand size from which passing with an empty field and nothing played reads as holding Gorz. Tune from logs.")]
        public int GorzPassHand { get; set; } = 4;

        /// <summary>Per-card Gorz chance multiplier after such a pass (until they put a card on the field). Tune from logs.</summary>
        [Tunable("Per-card Gorz chance multiplier after such a pass (until they put a card on the field). Tune from logs.")]
        public double GorzPassBoost { get; set; } = 3;

        /// <summary>After such a pass, the chance they hold Gorz is multiplied by this (1.3 = 30% more). User, 2026-11-18.</summary>
        [Tunable("After a complete pass (empty field, nothing played), the chance they hold Gorz is multiplied by this (1.5 = 50% more). User, 2026-11-18.")]
        public double GorzPassBonus { get; set; } = 1.5; // user, 2026-11-18: "lets up it to x1.5" (was 1.3)

        /// <summary>They passed their turn with an empty field and a big hand that didn't shrink: their cards are likelier Gorz.</summary>
        protected void NoteOpponentPassedForGorz() => _gorzPassFactor = GorzPassBoost;

        /// <summary>They set spells/traps (now <paramref name="theirSpells"/> on their field) holding <paramref name="hand"/> cards.</summary>
        protected void NoteOpponentSetForGorz(int hand, int theirSpells)
        {
            _gorzSetHeldCards = hand;
            _gorzSetResidual = Math.Pow(GorzSetResidual, Math.Max(1, theirSpells));
        }

        // A direct attack into their empty field (a Gorz chance) is pending: their hand size then.
        private int _gorzOpportunityHand;
        // Cards in their hand that were held through a Gorz chance they didn't take.
        private int _gorzClearedCards;

        private void TrackOpponentGorz(List<DynamicCard> opponents, CardInfo enemy)
        {
            bool onField = opponents.Any(card => card.IsFaceup() && card.IsCode(GorzId));
            bool elsewhere = (enemy.Grave ?? Array.Empty<int>()).Contains(GorzId) || (enemy.Banish ?? Array.Empty<int>()).Contains(GorzId);
            int hand = enemy.Hand?.Length ?? 0;
            if (_gorzOpportunityHand > 0)
            {
                // Our direct hit resolved: no Gorz means the cards they held then probably aren't Gorz.
                if (!onField)
                    _gorzClearedCards = Math.Max(_gorzClearedCards, Math.Min(_gorzOpportunityHand, hand));
                _gorzOpportunityHand = 0;
            }
            _gorzClearedCards = Math.Min(_gorzClearedCards, hand);
            // Cards leave their hand: the ones held while setting can only be fewer now.
            _gorzSetHeldCards = Math.Min(_gorzSetHeldCards, hand);
            if (onField || elsewhere)
                _knownGorzInOpponentHand = false;
            else if (_gorzWasOnEnemyField)
                _knownGorzInOpponentHand = true; // left the field without going to the GY/banished: back in the hand
            // They put a card on the field: the deliberate-pass read no longer holds.
            if (opponents.Any(card => card.ID != 0 || !card.IsFaceup()) || (enemy.SpellZones ?? Array.Empty<CardArray>()).Any(zone => zone.Cards is { Length: > 0 }))
                _gorzPassFactor = 1;
            _gorzWasOnEnemyField = onField;
        }

        // Text of a card that can get rid of a monster (destroy or banish it): Dimensional Prison, Mirror Force, ...
        private static readonly System.Text.RegularExpressions.Regex MonsterRemovalText = new(
            @"(destroy|banish)[^.]{0,60}monster|monster[^.]{0,60}(destroy|banish)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether we could deal with a 2700 ATK Gorz: a monster that beats it, or removal we hold (a set card, or a card
        /// in hand, whose text destroys or banishes a monster: D-Prison, Mirror Force, ...). Deck brains add their own
        /// (e.g. Dark Armed Dragon, or a synchro that beats it).
        /// </summary>
        protected virtual async Task<bool> CanAnswerGorzAsync()
        {
            if (_ownMonsters.Any(card => card.IsFaceup() && card.Attack > GorzAttack))
                return true;
            // A monster that pays off when Gorz destroys it in battle answers Gorz too (Machina Fortress: its trigger destroys
            // the card that killed it; user, 2026-11-12: "fortress deals with any card with more attack than it").
            if (_ownMonsters.Any(card => card.IsFaceup() && BattleDeathPayoff(card) != null && card.IsCode(MachinaFortressAnswerIds)))
                return true;
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0)
                .Concat(await GetBotHandCardsAsync())
                .Select(card => card.ID).Distinct();
            foreach (int id in ours)
            {
                if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) == 0 &&
                    MonsterRemovalText.IsMatch(info.CardText ?? ""))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Their searchers whose search we don't get to see (the card goes to their hand unrevealed to the bot), and what
        /// each can fetch. Reinforcement of the Army: a Level 4 or lower Warrior.
        /// </summary>
        private static readonly (int Id, string Name, Func<DBCardInfo, bool> Fetches)[] UnseenSearchers =
        {
            (32807846, "Reinforcement of the Army", info => (info.Type & (int)CardType.Monster) != 0 && (info.Race & (int)CardRace.Warrior) != 0 && info.Level is > 0 and <= 4),
        };

        private EnemyRead? _lastEnemyRead;

        /// <summary>
        /// The face-up card that stops us Special Summoning (Archlord Kristya: "Neither player can Special Summon"), or null.
        /// While it's up, every Special Summon effect of ours is worth nothing (user, 2026-11-13: "all special summon effects
        /// should have a score of 0 while krystia is on the field"): e.g. Hamster isn't set for its flip (as a last-resort
        /// blocker it still can be).
        /// </summary>
        protected string? SpecialSummonLockedBy { get; private set; }

        private static readonly System.Text.RegularExpressions.Regex NeitherPlayerSpecialText = new(
            @"neither player can special summon", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex OpponentCannotSpecialText = new(
            @"your opponent cannot special summon", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private async Task UpdateSpecialSummonLockAsync(FieldInfo field)
        {
            SpecialSummonLockedBy = null;
            async Task Check(CardInfo side, bool theirs)
            {
                foreach (var zone in side.MonsterZones ?? Array.Empty<CardArray>())
                {
                    if ((zone.Cards ?? Array.Empty<int>()) is not { Length: > 0 } ids || ids[0] <= 0 || SpecialSummonLockedBy != null)
                        continue;
                    if (await CardDatabase.GetAsync(ids[0]) is DBCardInfo info &&
                        (NeitherPlayerSpecialText.IsMatch(info.CardText ?? "") || theirs && OpponentCannotSpecialText.IsMatch(info.CardText ?? "")))
                        SpecialSummonLockedBy = CardNames.Get(ids[0]);
                }
            }
            await Check(field.EnemySide, theirs: true);
            await Check(field.OwnSide, theirs: false);
        }


        /// <summary>Draw phases they've had so far this duel (turns alternate; the header says whose turn this is).</summary>
        private int TheirDrawsSoFar()
        {
            int turn = _currentHeader.CurrentTurn;
            bool theirsNow = _currentHeader.CurrentPlayer == 1;
            int draws = 0;
            for (int t = 1; t <= turn; t++)
                if (((turn - t) % 2 == 0) == theirsNow)
                    draws++;
            return draws; // their current turn's draw counts: it's done by the time we're asked anything in it
        }

        /// <summary>Counts of their side at one field read, to tell an activated searcher from a discarded one.</summary>
        private sealed record EnemyRead(int Hand, int Deck, int FaceDowns, Dictionary<int, int> Pile, Dictionary<int, int> Field)
        {
            public static EnemyRead Of(CardInfo side)
            {
                var pile = new Dictionary<int, int>();
                foreach (int id in (side.Grave ?? Array.Empty<int>()).Concat(side.Banish ?? Array.Empty<int>()).Where(id => id > 0))
                    pile[id] = pile.GetValueOrDefault(id) + 1;
                var field = new Dictionary<int, int>();
                int faceDowns = 0;
                foreach (var zone in (side.MonsterZones ?? Array.Empty<CardArray>()).Concat(side.SpellZones ?? Array.Empty<CardArray>()))
                {
                    var ids = zone.Cards ?? Array.Empty<int>();
                    if (ids.Length == 0) continue;
                    if (ids[0] > 0) field[ids[0]] = field.GetValueOrDefault(ids[0]) + 1;
                    else faceDowns++;
                }
                return new EnemyRead(side.Hand?.Length ?? 0, side.Main?.Length ?? 0, faceDowns, pile, field);
            }

            /// <summary>Their side at the start of the duel (5 in hand), plus the draws they've had since: all cards out of the deck now.</summary>
            public static EnemyRead AtStart(EnemyRead now, int draws)
            {
                int total = now.Hand + now.Deck + now.FaceDowns + now.Field.Values.Sum() + now.Pile.Values.Sum();
                return new EnemyRead(5 + draws, total - 5 - draws, 0, new Dictionary<int, int>(), new Dictionary<int, int>());
            }

            private static Dictionary<int, int> Minus(Dictionary<int, int> a, Dictionary<int, int> b) =>
                a.Select(kv => (kv.Key, Count: kv.Value - b.GetValueOrDefault(kv.Key))).Where(x => x.Count > 0).ToDictionary(x => x.Key, x => x.Count);

            /// <summary>
            /// Null when the searcher (newly in their pile) was activated between the two reads, else why it looks discarded or
            /// milled. Activated: their hand balances (the searcher out, the fetched card in: cards played from hand
            /// accounted for) and the deck gave at least one card. Discarded: the hand is one short.
            /// </summary>
            public static string? SearchedWith(EnemyRead last, EnemyRead now, int searcherId)
            {
                var pileNew = Minus(now.Pile, last.Pile);
                var pileLost = Minus(last.Pile, now.Pile);
                var fieldNew = Minus(now.Field, last.Field);
                var fieldLost = Minus(last.Field, now.Field);
                int faceDownsLost = Math.Max(0, last.FaceDowns - now.FaceDowns);
                int newFaceDowns = Math.Max(0, now.FaceDowns - last.FaceDowns);
                // Cards that reached their graveyard / banishment not from the face-up field: from hand, deck or a face-down.
                int fromElsewhere = Minus(pileNew, fieldLost).Values.Sum();
                int otherFromHand = Math.Max(0, fromElsewhere - 1 - faceDownsLost); // besides the searcher itself
                int fieldFromHand = Math.Max(0, fieldNew.Values.Sum() - pileLost.Values.Sum() - faceDownsLost);
                int handBalance = now.Hand - last.Hand + fieldFromHand + newFaceDowns + otherFromHand;
                int deckDrop = last.Deck - now.Deck;
                if (handBalance < 0)
                    return $"their hand is {-handBalance} short of what an activation leaves (hand {last.Hand} -> {now.Hand}): discarded";
                if (deckDrop < 1)
                    return "no card left their deck for it";
                return null;
            }
        }

        // Copies of each searcher seen in their graveyard so far (a new copy there = a new search).
        private readonly Dictionary<int, int> _searchersSeen = new();
        private int _searchersTurn = int.MaxValue;

        /// <summary>
        /// A searcher of theirs newly in their graveyard: the card it fetched is one of their lists' cards it can fetch, so
        /// the deck read rates each of those likely in their hand, and a monster they set next likely that card (user,
        /// 2026-11-13, D.D. Warrior Lady from Reinforcement of the Army).
        /// </summary>
        private async Task ObserveUnseenSearchesAsync(DuelBotWrapper bot, CardInfo enemy)
        {
            int turn = _currentHeader.CurrentTurn;
            if (turn < _searchersTurn)
            {
                _searchersSeen.Clear(); // a new duel
                _lastEnemyRead = null;
            }
            _searchersTurn = turn;
            var before = _lastEnemyRead;
            var now = EnemyRead.Of(enemy);
            _lastEnemyRead = now;
            var grave = enemy.Grave ?? Array.Empty<int>();
            foreach (var (id, name, fetches) in UnseenSearchers)
            {
                int count = grave.Count(g => g == id || Search.SearchHost.CanonicalId(g) == id);
                if (count <= _searchersSeen.GetValueOrDefault(id))
                {
                    _searchersSeen[id] = count;
                    continue;
                }
                _searchersSeen[id] = count;
                // Activated, or discarded / milled? (User, 2026-11-13: "reinforcement being discarded should NOT count as a
                // search.") Activated, it leaves the hand and the searched card comes in: the hand doesn't drop for it, and
                // the deck gives a card. Discarded, the hand drops by one more than the cards they played. Milled, the deck
                // gives several cards to the graveyard at once.
                // Already there at our first read (they went first): against the duel's start, a 5-card hand plus their draws
                // (the turn player draws on turn 1 too: seen 2026-11-13, hand 6 / deck 34 / Reinforcement after their turn 1).
                var last = before ?? EnemyRead.AtStart(now, TheirDrawsSoFar());
                if (EnemyRead.SearchedWith(last, now, id) is string notSearched)
                {
                    bot.LogNote($"deck brain: {name} went to their graveyard without a search ({notSearched})");
                    continue;
                }
                var candidates = new List<int>();
                foreach (int card in OpponentDeck.LikelyCards(minWeight: 1e-4))
                    if (await CardDatabase.GetAsync(card) is DBCardInfo info && fetches(info))
                        candidates.Add(card);
                OpponentDeck.ObserveSearch(candidates, name);
                if (OpponentDeck.DescribeSearch() is string described)
                    bot.LogNote($"deck brain: they searched with {name} (unseen): {described}");
            }
        }

        private static readonly System.Text.RegularExpressions.Regex AllMonstersRemovalText = new(
            @"(destroy|banish)[^.]{0,30}all[^.]{0,30}monster",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether we can deal with both halves of a Gorz (user, 2026-11-13: "do I have an answer to the gorz and the token"):
        /// Gorz itself (CanAnswerGorzAsync, deck answers included) and its token, whose ATK is the damage of the hit that
        /// summoned it (<paramref name="tokenAtk"/>). One answer each: a monster that beats it (Machina Fortress beats
        /// anything that destroys it), or removal we hold (a "destroy all" card covers both).
        /// </summary>
        protected async Task<(bool Both, string Why)> CanAnswerGorzAndTokenAsync(int tokenAtk)
        {
            if (!await CanAnswerGorzAsync())
                return (false, "nothing of ours beats a 2700 Gorz");
            if (tokenAtk <= 0)
                return (true, "an answer to Gorz, and no token to worry about");
            var units = new List<(bool Gorz, bool Token, string Name)>();
            foreach (var card in _ownMonsters.Where(card => card.IsFaceup() && card.ID > 0))
            {
                bool fortress = BattleDeathPayoff(card) != null && card.IsCode(MachinaFortressAnswerIds);
                bool gorz = fortress || card.Attack > GorzAttack, token = fortress || card.Attack > tokenAtk;
                if (gorz || token)
                    units.Add((gorz, token, CardNames.Get(card.ID)));
            }
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0).Concat(await GetBotHandCardsAsync());
            foreach (var card in ours)
            {
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info || (info.Type & (int)CardType.Monster) != 0 ||
                    !MonsterRemovalText.IsMatch(info.CardText ?? ""))
                    continue;
                units.Add((true, true, CardNames.Get(card.ID)));
                if (AllMonstersRemovalText.IsMatch(info.CardText ?? ""))
                    units.Add((true, true, CardNames.Get(card.ID) + " (all)"));
            }
            // Gorz's answer may be deck-specific (Dark Armed Dragon, a synchro): then any token answer will do.
            if (!units.Any(u => u.Gorz))
            {
                var tokenOnly = units.FirstOrDefault(u => u.Token);
                return tokenOnly.Name != null
                    ? (true, $"a deck answer to Gorz, and {tokenOnly.Name} for the {tokenAtk} token")
                    : (false, $"nothing of ours beats the {tokenAtk} ATK token Gorz would bring");
            }
            for (int i = 0; i < units.Count; i++)
                for (int j = 0; j < units.Count; j++)
                    if (i != j && units[i].Gorz && units[j].Token)
                        return (true, $"{units[i].Name} answers Gorz, {units[j].Name} the {tokenAtk} ATK token");
            return (false, $"one answer ({string.Join(", ", units.Select(u => u.Name))}) for Gorz and its {tokenAtk} ATK token");
        }

        /// <summary>The ATK of the monster that would hit first (the server attacks in zone order): the Gorz token's ATK.</summary>
        private int FirstHitAttack() => _ownMonsters
            .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && card.ID > 0)
            .OrderBy(card => card.CurrentCoordinates.Sequence).Select(card => card.Attack).FirstOrDefault();

        // Our monsters whose trigger takes the monster they destroyed by battle (Goyo Guardian: official and Omega's pre-errata id).
        protected static readonly int[] TakeControlTriggerIds = { 7391448, 7391468 };

        // Monsters whose destruction by battle destroys the card that did it (Machina Fortress): an answer to Gorz.
        private static readonly int[] MachinaFortressAnswerIds = { 5556499, 5556519 };

        /// <summary>
        /// Our Goyo Guardian's trigger in a chain prompt (the engine offers it there, not as a yes/no): always use it, a free
        /// monster for us. The engine only offers it when the monster can come back (not, e.g., a Starlight Road Stardust).
        /// </summary>
        protected int? OwnTakeControlTrigger(DecisionContext<SelectChainData> context)
        {
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int index = Array.FindIndex(cards, card => card.CurrentCoordinates.Controller == 0 && card.IsCode(TakeControlTriggerIds) &&
                                                        card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (index < 0)
                return null;
            context.Bot.LogNote($"deck brain: use {CardNames.Get(cards[index].ID)}'s effect: take the monster it destroyed");
            return index;
        }

        /// <summary>The deck's pick to attack an unknown face-down monster with (null: the shared cheap / strongest choice).</summary>
        protected virtual DynamicCard? PreferredFaceDownAttacker(List<DynamicCard> attackers) => null;

        /// <summary>Why the deck picks that attacker for a face-down (for the log).</summary>
        protected virtual string PreferredFaceDownWhy(DynamicCard attacker) => "the deck's pick";

        /// <summary>Monsters that attack last into an empty field (deck brains override, e.g. Chaos Sorcerer).</summary>
        protected virtual IReadOnlyCollection<int> AttackLast => Array.Empty<int>();

        protected virtual async Task<List<DynamicCard>> GetPlayerCardsInZoneAsync(CardLocation location, bool isOwnSide = true) =>
            (await CurrentField.GetPlayerCardsInZone(location, isOwnSide)).ToList();

        protected virtual Task<int> GetPlayerSpellZoneCountAsync(CardLocation location = CardLocation.SpellZone) =>
            CurrentField.GetPlayerSpellZoneCount(location);

        protected virtual Task<bool> HasCardInBotMonsterZoneAsync(int cardId) =>
            CurrentField.HasCardInBotMonsterZone(cardId);

        protected virtual Task<bool> HasCardInBotSpellZoneAsync(int cardId) =>
            CurrentField.HasCardInBotSpellZone(cardId);

        protected virtual Task<List<DynamicCard>> GetBotHandCardsAsync() =>
            CurrentField.GetBotHandCards();

        protected virtual Task<List<DynamicCard>> GetBotGraveyardCardsAsync() =>
            CurrentField.GetBotGraveyardCards();

        protected virtual async Task<List<DynamicCard>> GetBotMonsterCardsAsync(bool onlyFaceUp = false) =>
            WithoutTempBoosts(await CurrentField.GetBotMonsterCards(onlyFaceUp));

        protected virtual async Task<List<DynamicCard>> GetEnemyMonsterCardsAsync(bool onlyFaceUp = false) =>
            WithoutTempBoosts(await CurrentField.GetEnemyMonsterCards(onlyFaceUp));

        protected virtual Task<List<DynamicCard>> GetEnemySpellCardsAsync(bool onlyFaceUp = false) =>
            CurrentField.GetEnemySpellCards(onlyFaceUp);

        protected void SetPendingAction(PendingActionKind kind, int cardId = 0, MainAction? action = null, int description = 0, int contextCode = 0)
        {
            PendingAction = new PendingActionContext
            {
                Kind = kind,
                CardId = cardId,
                Action = action,
                Description = description,
                ContextCode = contextCode
            };
        }

        protected void ClearPendingAction() => PendingAction = default;

        protected void MarkCardActivated(int cardId)
        {
            if (cardId <= 0)
                return;
            _activatedThisTurn.Add(cardId);
            // Live Omega sends variant ids (Brain Control as 87910998, alias 87910978): the official one too, which the checks use (seen
            // 2026-11-18: a Fortress taken by Brain Control wasn't known to go back, and the search tributed the token instead).
            if (Search.SearchHost.Available && Search.SearchHost.CanonicalId(cardId) is > 0 and var official)
                _activatedThisTurn.Add(official);
        }

        protected bool WasCardActivatedThisTurn(int cardId) => _activatedThisTurn.Contains(cardId);

        protected void RegisterChainDescriptions(IEnumerable<int> descriptions)
        {
            _chainDescriptions.Clear();
            _chainDescriptions.AddRange(descriptions);
        }

        protected bool IsFirstTurnOrMain2(BotStateHeader header) =>
            header.CurrentTurn <= 1 || (DuelPhase)header.CurrentPhase == DuelPhase.Main2;

        protected bool PendingContextIs(int contextCode) => PendingAction.ContextCode == contextCode;

        protected static bool TryGetSummonable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpecialSummon(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpellSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetMonsterSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetActivable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ActivableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetReposable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ReposableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected IList<byte>? PreferCardSelection(SelectCardData state, params int[] preferredIds)
        {
            var selected = state.SelectableCards
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => x.Card.IsCode(preferredIds))
                .Take(state.Minimum)
                .Select(x => (byte)x.Index)
                .ToArray();

            return selected.Length >= state.Minimum ? selected : null;
        }

        protected virtual async Task<DynamicCard> GetBestEnemyMonster(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyMonster(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemySpell(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemySpell(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemyCard(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyCard(onlyFaceUp);

        protected static bool CanBuildLevel8Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(69248256, 83755611, 43202238));
        }

        protected static bool CanBuildLevel10Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(96633955, 84815190, 47710198));
        }

        /// <summary>Whether this monster's attack adds pressure: a target worth attacking, or a direct hit that's not weak / changes the clock.</summary>
        private bool AttackMatters(DynamicCard card, List<DynamicCard> opponents) =>
            opponents.Count == 0
                ? !IsWeak(card) || ChangesClock(card, _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()))
                : opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent));

        protected bool ShouldRepositionToAttack(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            // Flip our own face-down FLIP monster (Ryko) to use its effect: popping a face-down of theirs stops their Ryko
            // flipping for its own destroy, and destroys a battle recruiter outside battle. Worth it when they have a
            // threat on the field: any face-down monster (it could be one), a big face-up threat, or set Spell/Traps while
            // they threaten a monster of ours that isn't expendable. Not for any card.
            // Only with a card of theirs on the field for it to hit (seen 2026-11-10: Ryko flipped into their empty field and
            // had to destroy one of ours).
            // Only a flip that hits a card (Ryko's destroy), not one that only summons (seen 2026-11-10: Super-Nimble Mega
            // Hamster flipped into Archlord Kristya for nothing).
            if (!card.IsFaceup() && HasFlipEffect(card) && FlipHitsACard(card.ID) && !_enemyFieldEmpty && (_opponentHasThreat || SetSpellTrapsThreatenUs(card)) &&
                Rule(DeckRules.FlipForThreat))
                return true;
            // After the Battle Phase there's nothing to attack: attack position only exposes it (the flip above is the one reason).
            if ((DuelPhase)_currentHeader.CurrentPhase == DuelPhase.Main2)
                return false;
            // A face-down FLIP monster flipped with nothing of theirs to hit wastes the flip (and may have to hit our own card).
            if (!card.IsFaceup() && HasFlipEffect(card) && _enemyFieldEmpty)
                return false;
            if (MirrorDefenceReason(card.ID, card.Attack, countsAlreadyOnField: true, opponents, battleAhead: true,
                    AttackMatters(card, opponents), _mirrorRollTurn) != null)
                return false; // stays in defence this turn (Mirror Force roll; never when needed for lethal)
            var attackersUp = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, card)).ToList();
            // Empty field: only if its direct hit is worth it (a 200 Ryko's isn't, and it'd be left in attack position).
            // An expendable monster with a decent hit (a flipped Hamster, a used Armageddon Knight) attacks an open field anyway.
            if (!opponents.Any())
                return !IsWeak(card) || ChangesClock(card, attackersUp) || ExpendableHitter(card) || IsBeater(card);

            // One attack deals with one target: a monster only goes up if the targets it could attack outnumber the
            // attackers already up that could take them (else e.g. a 600 Necro Gardna and a 200 Ryko both go to attack
            // position for one face-down, one other monster attacks it, and both are left exposed).
            var targets = opponents.Where(opponent => IsWorthAttacking(card, opponent)).ToList();
            // An expendable monster (a Gorz token) takes an equal-ATK trade rather than sit in defence for good (user,
            // 2026-11-11: the token summoned in defence "seems to get stuck there").
            if (targets.Count == 0 && IsExpendable(card) && opponents.Any(opponent => IsGoodTrade(card, opponent, attackersUp.Count + 1)))
                return true;
            if (targets.Count == 0)
                return ClearsTheirBoard(attackersUp, opponents) && DirectHitMatters(card, attackersUp) && Rule(DeckRules.RepositionForDirectHit);
            // A face-down (assumed Ryko) is best attacked by a cheap monster when all we have out is cheap: Necro Gardna
            // into it wastes their Ryko. (With a good card out, Ryko would destroy that anyway: no special case.)
            if (IsCheapAttacker(card) && targets.Any(target => !target.IsFaceup() && LikelyRyko(target)) && !attackersUp.Any(IsCheapAttacker) &&
                CheapAttackDodgesRyko() && Rule(DeckRules.CheapIntoFaceDown))
                return true;
            // It could destroy something in attack position that our attackers already up won't get to (user, 2026-11-13: "or if
            // the defence position monster could destroy something if it were in attack. it should switch for that too"): with
            // it, more of their monsters are destroyed than without it (one attack per target).
            var worthTargets = opponents.Where(target => IsWorthAttacking(card, target) || attackersUp.Any(own => IsWorthAttacking(own, target))).ToList();
            if (Kills(attackersUp.Append(card).ToList(), worthTargets) > Kills(attackersUp, worthTargets) && Rule(DeckRules.RepositionToAttack))
                return true;
            // Do we want to attack with it at all? The attackers already up clear their monsters (one each), so this one gets
            // a direct hit: worth switching for when that hit matters (user, 2026-11-13, a Gorz token left in defence while
            // Grepher took their only monster: "do I want to attack with this defence position monster? if yes ... switch").
            return ClearsTheirBoard(attackersUp, opponents) && DirectHitMatters(card, attackersUp) && Rule(DeckRules.RepositionForDirectHit);
        }

        /// <summary>Whether these attackers can destroy every monster of theirs, one attack each.</summary>
        protected bool ClearsTheirBoard(List<DynamicCard> attackers, List<DynamicCard> opponents) =>
            Kills(attackers, opponents) == opponents.Count(card => card.ID != 0 || !card.IsFaceup());

        /// <summary>How many of these monsters of theirs our attackers can destroy, one attack each (the biggest first, each by
        /// the weakest attacker that beats it).</summary>
        protected int Kills(List<DynamicCard> attackers, List<DynamicCard> opponents)
        {
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            var left = attackers.Select(card => card.Attack).OrderBy(atk => atk).ToList();
            int kills = 0;
            foreach (var monster in opponents.Where(card => card.ID != 0 || !card.IsFaceup()).OrderByDescending(Power))
            {
                int index = left.FindIndex(atk => atk > Power(monster));
                if (index < 0)
                    continue;
                left.RemoveAt(index);
                kills++;
            }
            return kills;
        }

        /// <summary>Whether this monster's direct hit is worth making (as for an open field: not weak, or it speeds the clock).</summary>
        protected bool DirectHitMatters(DynamicCard card, List<DynamicCard> attackersUp) =>
            !IsWeak(card) || ChangesClock(card, attackersUp) || ExpendableHitter(card) || IsBeater(card);

        /// <summary>
        /// Switch to defence when the monster has nothing worth attacking and either it is weak (alone it would be an
        /// Almost None clock or none, e.g. Plaguespreader) or its DEF survives the opponent's strongest face-up attacker
        /// that would beat it in attack position. A monster that still makes a real clock stays in attack position.
        /// </summary>
        protected bool ShouldRepositionToDefense(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            int strongestOpponentAttack = opponents
                .Where(opponent => opponent.IsFaceup() && opponent.IsAttack())
                .Select(opponent => opponent.Attack)
                .DefaultIfEmpty(0)
                .Max();

            bool threatened = strongestOpponentAttack > card.Attack;
            bool survivesInDefense = card.Defense >= strongestOpponentAttack;
            // Low LP (round-robin 3 / 16; 2026-11-13: Armageddon Knight left in attack position at 600 LP against Fortress, and
            // Fortress killed us through it): a monster their attacker kills us through goes to defence, unless its attack
            // this turn wins or removes something (then the Battle Phase is its chance; after it, Main Phase 2 switches it).
            bool battleNow = (DuelPhase)_currentHeader.CurrentPhase == DuelPhase.Main1;
            if (_lowLpExposure is { } exposure && card.Attack <= exposure.MaxAttack &&
                !(battleNow && (opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent)) ||
                                BoardAssessment.TurnsToWin(_ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).Select(EffectiveAttack),
                                    BoardAssessment.Defenders(opponents, OpponentMemory), _oppLp) == 1)) &&
                Rule(DeckRules.LowLpNoExposure))
            {
                _mirrorForceReason[card.CurrentCoordinates.Sequence] = exposure.Why;
                return true;
            }
            // E.g. Chaos Sorcerer: as good in defence for clearing monsters, so a reasonable Mirror Force chance weighs
            // against the pressure its attack adds (a roll), and lethal always keeps it up.
            if (MirrorDefenceReason(card.ID, card.Attack, countsAlreadyOnField: true, opponents, battleAhead: true,
                    AttackMatters(card, opponents), _mirrorRollTurn) is string mirrorDefence)
            {
                _mirrorForceReason[card.CurrentCoordinates.Sequence] = mirrorDefence;
                return true;
            }
            bool hasGoodAttack = opponents.Any(opponent => opponent.IsFaceup() && IsWorthAttacking(card, opponent));
            if (hasGoodAttack)
                return false; // it has something worth attacking
            if (ShelterFromMirrorForce(card, opponents, survivesInDefense))
                return true;
            // Plaguespreader is synchro material, never an attacker: a 400 body at their low LP still isn't one (user, 2026-11-13:
            // "why is plague not at very least turning to defence?!").
            bool weak = IsWeak(card) || card.IsCode(PlaguespreaderZombieId);
            if (opponents.Count == 0)
            {
                // It will attack directly unless it's weak and its damage doesn't change the clock.
                var attackers = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack()).ToList();
                if (!weak || ChangesClock(card, attackers))
                    return false;
            }

            // A weak monster that isn't attacking has no reason to stay in attack position.
            return weak || (threatened && survivesInDefense);
        }

        /// <summary>
        /// Monsters that aren't worth spending protection on (Necro Gardna, sheltering from Mirror Force, a trap just to
        /// save them): a Level 4 or lower main-deck monster under 2000 ATK has usually done its job (Armageddon Knight,
        /// Dark Grepher) and is easy to replace; tokens likewise. Losing one to a trap even helps: the trap is spent.
        /// Deck brains add their own (e.g. a synchro that is material from the graveyard).
        /// </summary>
        /// (Tokens like any other small monster: under StrongMonsterAttack. How expendable is tapered in TributeCost: user,
        /// 2026-11-13, "tokens aren't inherently expendable ... tapered".)
        protected virtual bool IsExpendable(DynamicCard card) =>
            IsTemporaryMonster(card) || // leaves soon whatever we do (Returner's Jinzo, Darius' revived monster): never protected
            card.HasType(CardType.Token) && EffectiveAttack(card) < StrongMonsterAttack || IsSpentFlipMonster(card) || LylaToUseUp(card) ||
            (ReSummonCost(card) < 100 && card.Level <= 4 && card.Attack < StrongMonsterAttack);

        /// <summary>
        /// "Weak" in clock terms: on its own, hitting directly every turn, it would be Almost None (9-10 turns) or no
        /// clock at all (e.g. Plaguespreader). A 1400 ATK Gorz token is a 6-turn clock against 8000 LP: a real attacker.
        /// </summary>
        /// <summary>
        /// A beater: BeaterAttack+ ATK (Gearframe 1800, Mechanicalchaser 1850): a monster you want attacking whenever it can,
        /// whatever its effect.
        /// </summary>
        protected bool IsBeater(DynamicCard card) => card.IsFaceup() && card.Attack >= BeaterAttack;

        /// <summary>ATK from which a monster is a beater (attacks an open field whenever it can). Tune from logs.</summary>
        [Tunable("ATK from which a monster is a beater (attacks an open field whenever it can). Tune from logs.")]
        public int BeaterAttack { get; set; } = 1700;

        /// <summary>An expendable monster whose hit is still decent (ExpendableHitterMinAtk+): worth attacking an open field with.</summary>
        protected bool ExpendableHitter(DynamicCard card) => IsExpendable(card) && card.Attack >= ExpendableHitterMinAtk && Rule(DeckRules.ExpendableAttacksOpenField);

        /// <summary>ATK from which an expendable monster attacks an open field (1100 Hamster, 1400 Armageddon Knight; not a 200 Ryko). Tune from logs.</summary>
        [Tunable("ATK from which an expendable monster attacks an open field (1100 Hamster, 1400 Armageddon Knight; not a 200 Ryko). Tune from logs.")]
        public int ExpendableHitterMinAtk { get; set; } = 1000;

        protected bool IsWeak(DynamicCard card) =>
            BoardAssessment.SpeedOf(BoardAssessment.TurnsToWin(new[] { card.Attack }, Array.Empty<BoardAssessment.Defender>(), _oppLp))
                is ClockSpeed.AlmostNone or ClockSpeed.None;

        /// <summary>Whether adding this monster's direct attacks makes the board win in fewer turns (or this turn).</summary>
        protected bool ChangesClock(DynamicCard card, IEnumerable<DynamicCard> attackers)
        {
            var others = attackers.Where(own => !SameZone(own, card)).Select(own => own.Attack).ToList();
            int without = BoardAssessment.TurnsToWin(others, Array.Empty<BoardAssessment.Defender>(), _oppLp);
            others.Add(card.Attack);
            int with = BoardAssessment.TurnsToWin(others, Array.Empty<BoardAssessment.Defender>(), _oppLp);
            return with < without;
        }

        // Cards from different API calls are different structs; the zone identifies them.
        private static bool SameZone(DynamicCard a, DynamicCard b) =>
            a.CurrentCoordinates.Controller == b.CurrentCoordinates.Controller &&
            a.CurrentCoordinates.Location == b.CurrentCoordinates.Location &&
            a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;

        // ------------------------------------------------------------------ monsters that reward being destroyed

        /// <summary>
        /// Opponent monsters whose text rewards their owner when destroyed or sent to the graveyard
        /// (Kittytail, Sangan, Giant Rat, ...): killing them hands the opponent a card.
        /// </summary>
        private readonly HashSet<int> _rewardOnDestroy = new();

        /// <summary>"Your opponent cannot target other monsters for attacks, except this card" (Dupe Frog and co.).</summary>
        private static readonly System.Text.RegularExpressions.Regex AttackLockText = new(
            @"cannot target (other )?monsters( you control)? for attacks?,? except (this card|this one)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The monsters among theirs that we can actually attack: with a face-up attack lock out (Dupe Frog: "cannot target
        /// other monsters for attacks, except this card"), only the lock monsters (round-robin 21: Armageddon Knight was sent
        /// at "the unknown face-down", and the server offered only Dupe Frog, 2000 DEF, three turns running).
        /// </summary>
        protected async Task<List<DynamicCard>> AttackableTargetsAsync(List<DynamicCard> theirMonsters)
        {
            var locks = new List<DynamicCard>();
            foreach (var card in theirMonsters.Where(card => card.ID > 0 && card.IsFaceup() && !card.IsDisabled()))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && AttackLockText.IsMatch(info.CardText ?? ""))
                    locks.Add(card);
            return locks.Count > 0 ? locks : theirMonsters;
        }

        private static readonly System.Text.RegularExpressions.Regex RewardOnDestroyText = new(
            @"(destroyed|sent (from the field )?to the (graveyard|gy))[^.]{0,120}?\b(add|special summon|draw)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Refreshed each decision: how fast our board kills the opponent, and our monsters' battle stats.
        private ClockSpeed _clockSpeed = ClockSpeed.None;
        private List<DynamicCard> _ownMonsters = new();
        /// <summary>Our monsters at this decision.</summary>
        protected IReadOnlyList<DynamicCard> OwnMonsters => _ownMonsters;
        private int _oppLp;
        private List<DynamicCard> _opponents = new();
        /// <summary>Their monsters at this decision.</summary>
        protected IReadOnlyList<DynamicCard> OpponentMonsters => _opponents;
        private bool _enemyFieldEmpty;

        /// <summary>Effect points (EffectEvaluator score without its ATK part) of opponent monsters seen, by card id.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> EffectPointsCache = new();

        /// <summary>ATK-equivalent of one effect point in TargetThreat: effects and ATK balanced.</summary>
        [Tunable("ATK-equivalent of one effect point in TargetThreat: effects and ATK balanced.")]
        public static int EffectPointValue = 500;

        // Monsters that can't be destroyed by battle once per turn (Gemini Soldier), and the ones we've battled this turn.
        private readonly HashSet<int> _oncePerTurnBattleProtected = new();
        private readonly HashSet<(int Turn, int Zone, int Id)> _battledThisTurn = new();

        // Always battle-proof (Blackwing Armor Master): "This card cannot be destroyed by battle". Its effect is off when
        // negated (a Vayu-summoned Armor Master), so check IsEffectNegated before relying on it.
        private readonly HashSet<int> _battleProof = new();
        private static readonly System.Text.RegularExpressions.Regex BattleProofText = new(
            @"(^|[.:;]\s*|this card )cannot be destroyed by battle(?! once per turn)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether this monster survives any battle right now (battle-proof text, effects not negated).</summary>
        protected bool IsBattleProof(DynamicCard card) => _battleProof.Contains(card.ID) && !IsEffectNegated(card);

        // "You take no battle damage from attacks/battles involving this card" (Blackwing Armor Master).
        private readonly HashSet<int> _stopsBattleDamage = new();
        private static readonly System.Text.RegularExpressions.Regex StopsBattleDamageText = new(
            @"(take|takes) no battle damage from (attacks|battles) involving this card",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether battles with this monster deal its controller no damage right now (effects not negated).</summary>
        protected bool StopsBattleDamage(DynamicCard card) => _stopsBattleDamage.Contains(card.ID) && !IsEffectNegated(card);

        private static readonly System.Text.RegularExpressions.Regex OncePerTurnBattleProtectionText = new(
            @"once per turn[^.]{0,80}cannot be destroyed by battle|cannot be destroyed by battle once per turn",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected async Task RefreshRewardOnDestroyAsync(IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            // Their Catastor-type monsters (destroy any monster of another Attribute they battle).
            await LearnBattleDestroyersAsync(opponents.Where(card => card.IsFaceup()).Select(card => card.ID));
            // Effect scores for attack targeting (TargetThreat), and once-per-turn battle protection.
            if (opponents.Any(card => card.IsFaceup() && card.ID > 0 && !EffectPointsCache.ContainsKey(card.ID)))
            {
                var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
                foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !EffectPointsCache.ContainsKey(card.ID)))
                {
                    if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                        continue;
                    var (score, _) = await EffectEvaluator.ScoreOnFieldAsync(card.ID, grave);
                    EffectPointsCache[card.ID] = Math.Max(0, score - BotTest.Game.AI.Search.MctsEvaluator.AtkValue(info.Attack));
                    if (OncePerTurnBattleProtectionText.IsMatch(info.CardText ?? ""))
                        _oncePerTurnBattleProtected.Add(card.ID);
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_targetRemovesAttacker.Contains(card.ID)))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo removerInfo && RemovesBattledText.IsMatch(removerInfo.CardText ?? ""))
                    _targetRemovesAttacker.Add(card.ID);
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_rewardOnDestroy.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && RewardOnDestroyText.IsMatch(info.CardText ?? ""))
                {
                    _rewardOnDestroy.Add(card.ID);
                    if (RecruitText.IsMatch(info.CardText ?? ""))
                    {
                        _recruiters.Add(card.ID);
                        if (!RecruitOnAnyDestructionText.IsMatch(info.CardText ?? ""))
                            _battleOnlyRecruiters.Add(card.ID);
                    }
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_graveyardEffect.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info)
                {
                    if (EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""))
                        _graveyardEffect.Add(card.ID);
                    if (BattleProofText.IsMatch(info.CardText ?? "") && !OncePerTurnBattleProtectionText.IsMatch(info.CardText ?? ""))
                        _battleProof.Add(card.ID);
                    if (StopsBattleDamageText.IsMatch(info.CardText ?? ""))
                        _stopsBattleDamage.Add(card.ID);
                    if (FlipEffectText.IsMatch(info.CardText ?? ""))
                        _flipEffect.Add(card.ID);
                    if (StealsBattleVictimText.IsMatch(info.CardText ?? ""))
                        _stealsBattleVictim.Add(card.ID);
                }
            }
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_bouncesAttacker.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
                {
                    _bouncesAttacker.Add(card.ID);
                    BoardAssessment.KnownAttackerBouncers.TryAdd(card.ID, 0); // so clock / lethal maths account for it
                }
            }

            _ownMonsters = await GetBotMonsterCardsAsync();
            _ownSpellTrapCount = (await CurrentField.GetCardsInSpellZone()).Count();
            _opponentHasThreat = false;
            foreach (var enemy in opponents)
                if (!enemy.IsFaceup() || await IsBigThreatAsync(enemy))
                {
                    _opponentHasThreat = true;
                    break;
                }
            // Our own monsters' graveyard / FLIP effects too (we see our face-down cards), for the attack choices below.
            foreach (var card in _ownMonsters.Where(card => card.ID > 0 && !_ownTextRead.Contains(card.ID)))
            {
                _ownTextRead.Add(card.ID);
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                    continue;
                if (EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""))
                    _graveyardEffect.Add(card.ID);
                if (FlipEffectText.IsMatch(info.CardText ?? ""))
                    _flipEffect.Add(card.ID);
            }
            CheckPendingPlacement(_currentHeader, _ownMonsters);
            await LearnBattleDestroyersAsync(_ownMonsters.Where(card => card.IsFaceup()).Select(card => card.ID));
            _opponents = opponents;
            _oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var enemySpellTraps = await GetEnemySpellCardsAsync();
            _enemyFieldEmpty = opponents.Count == 0 && enemySpellTraps.Count == 0;
            _opponentSetSpellTraps = enemySpellTraps.Count(card => !card.IsFaceup());
            var enemySide = (await CurrentField.GetBotField()).EnemySide;
            TrackOpponentGorz(opponents, enemySide);
            TrackBouncerInHand(opponents, enemySide);
            await RefreshMoleLockAsync(opponents);
            int turns = BoardAssessment.TurnsToWin(
                _ownMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Select(card => card.Attack),
                BoardAssessment.Defenders(opponents, OpponentMemory), _oppLp);
            _clockSpeed = BoardAssessment.SpeedOf(turns);
        }

        protected bool RewardsDestruction(DynamicCard target) => _rewardOnDestroy.Contains(target.ID);

        // Opponent monsters with an effect that works from the graveyard (Vayu, Plaguespreader, Necro Gardna).
        private readonly HashSet<int> _graveyardEffect = new();

        // Opponent monsters that take what they destroy by battle (Goyo Guardian: "destroys an opponent's monster by
        // battle ... Special Summon that monster to your field").
        private readonly HashSet<int> _stealsBattleVictim = new();
        private static readonly System.Text.RegularExpressions.Regex StealsBattleVictimText = new(
            @"destroys an opponent's monster by battle[^.]*special summon that monster to your field",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A face-up opponent monster (effects live) that takes the monster it destroys by battle (Goyo Guardian). Setting
        /// or leaving a weak monster in front of it just hands them that monster.
        /// </summary>
        protected bool StealsBattleVictim(DynamicCard card) =>
            card.IsFaceup() && _stealsBattleVictim.Contains(card.ID) && !IsEffectNegated(card);

        /// <summary>Whether this monster (ours or theirs) has an effect that works once it's in the graveyard.</summary>
        protected bool HasGraveyardEffect(DynamicCard target) => _graveyardEffect.Contains(target.ID);

        // Our monsters whose text has been read, and the ones with a FLIP effect (Ryko).
        private readonly HashSet<int> _ownTextRead = new();
        private readonly HashSet<int> _flipEffect = new();
        private static readonly System.Text.RegularExpressions.Regex FlipEffectText = new(
            @"^\s*flip:", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether this monster of ours has a FLIP effect (Ryko).</summary>
        protected bool HasFlipEffect(DynamicCard card) => _flipEffect.Contains(card.ID);

        /// <summary>Whether this monster's FLIP effect destroys, banishes or returns a card (Ryko), from its text. Unknown: yes.</summary>
        protected static bool FlipHitsACard(int code)
        {
            if (!Search.SearchHost.Available)
                return true;
            int official = Search.SearchHost.CanonicalId(code) is > 0 and var o ? o : code;
            if (Search.SearchHost.Cards.Get(official) is not { } card)
                return true;
            string text = card.Text;
            int flip = text.IndexOf("FLIP:", StringComparison.OrdinalIgnoreCase);
            // No FLIP effect, nothing a flip can hit (2026-11-17: a set Honest, "return this card ... to your hand", read as Ryko 100%).
            if (flip < 0)
                return false;
            string effect = text[flip..];
            return effect.Contains("destroy", StringComparison.OrdinalIgnoreCase) ||
                   effect.Contains("banish", StringComparison.OrdinalIgnoreCase) ||
                   effect.Contains("return", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A face-up FLIP monster (a flipped Ryko): its effect is spent, so it is completely expendable. It never counts in
        /// Mirror Force / Torrential Tribute maths (losing it costs nothing).
        /// </summary>
        protected bool IsSpentFlipMonster(DynamicCard card) => card.IsFaceup() && HasFlipEffect(card);

        /// <summary>
        /// A cheap monster to send into a possible Ryko / unknown face-down: losing it costs little (expendable) or even
        /// helps (Necro Gardna works from the graveyard). If it's a Ryko, the flip wastes their Ryko on it.
        /// </summary>
        /// (Never a big monster: a semi-expendable 2000+ body like Armed Wing is still worth more than a dodge.)
        protected bool IsCheapAttacker(DynamicCard card) =>
            (IsExpendable(card) || HasGraveyardEffect(card)) && card.Attack < StrongMonsterAttack;

        // Our Spell/Trap Zone card count, refreshed with the field.
        private int _ownSpellTrapCount;

        // A threat on their field worth a FLIP destroy: any face-down monster (unknown: it could be one) or a big face-up
        // threat (see IsBigThreatAsync). Refreshed with the field.
        private bool _opponentHasThreat;

        // Their set Spell/Trap cards, refreshed with the field.
        private int _opponentSetSpellTraps;

        /// <summary>How many set (face-down) Spell/Traps the opponent has, as of the last field refresh.</summary>
        protected int OpponentSetSpellTraps => _opponentSetSpellTraps;

        /// <summary>
        /// Whether their set Spell/Traps are a threat worth popping: they threaten our monsters that aren't expendable
        /// (Mirror Force, Bottomless...), not our life points. So only while we have such a monster out (it's what we're
        /// doing something with this turn); with only expendable bodies out there's nothing for them to hit.
        /// </summary>
        protected bool SetSpellTrapsThreatenUs(DynamicCard except) =>
            _opponentSetSpellTraps > 0 &&
            _ownMonsters.Any(own => own.ID > 0 && own.IsFaceup() && !SameZone(own, except) && !IsExpendable(own));

        // A Special Summon from the Extra Deck by an activated effect (Vayu's graveyard synchro).
        private static readonly System.Text.RegularExpressions.Regex SummonsFromExtraText = new(
            @"special summon[^.]*from your extra deck",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Part of a decided summon: when the play we've chosen is a strong summon (2000+ ATK or an Extra Deck monster,
        /// or an effect that summons one from the Extra Deck, like Vayu's), they have set Spell/Traps and we have a
        /// face-down FLIP monster (Ryko), flip it first: Bottomless / Torrential trigger on the summon, so pop a set card
        /// before it. The play is committed, so the next decision follows through with it. Only for a summon we're
        /// actually making, never on spec.
        /// </summary>
        protected async Task<IdleCmdResponse> FlipBeforeSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse response)
        {
            var state = context.StateData;
            if (_opponentSetSpellTraps == 0 || response.Action is not (MainAction.Summon or MainAction.SpSummon or MainAction.Activate))
                return response;
            var flip = (state.ReposableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => !card.IsFaceup() && HasFlipEffect(card));
            if (flip.ID <= 0)
                return response;
            var candidates = response.Action switch
            {
                MainAction.Summon => state.SummonableCards,
                MainAction.SpSummon => state.SpecialSummonableCards,
                _ => state.ActivableCards
            };
            var card = (candidates ?? Array.Empty<DynamicCard>()).FirstOrDefault(c =>
                c.ActionIndex != null && c.ActionIndex.Length > (int)response.Action && c.ActionIndex[(int)response.Action] == response.Index);
            if (card.ID <= 0 || await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return response;
            bool strong = response.Action == MainAction.Activate
                ? SummonsFromExtraText.IsMatch(info.CardText ?? "")
                : card.Attack >= StrongMonsterAttack || card.IsExtraCard();
            if (!strong || !Rule(DeckRules.FlipBeforeSummon))
                return response;
            if (!CommittedToBigPlay(context.Header, card.ID))
            {
                PendingBigPlayCards.Clear();
                PendingBigPlayCards.Add(card.ID);
                CommitToBigPlay(context.Header.CurrentTurn, keepExisting: true);
            }
            context.Bot.LogNote($"deck brain: before {(response.Action == MainAction.Activate ? $"{CardNames.Get(card.ID)}'s summon" : $"summoning {CardNames.Get(card.ID)}")}, " +
                                $"flip {CardNames.Get(flip.ID)} to destroy a set card: Bottomless / Torrential trigger on the summon");
            return flip.Reposition();
        }

        /// <summary>
        /// Whether sending a cheap monster into a possible Ryko actually dodges anything. Ryko destroys any card on the
        /// field, not just its attacker, so it only helps when everything we have out is cheap (no Spell/Trap, no good
        /// monster): then its flip has nothing better to hit. Otherwise it just picks our best card anyway.
        /// </summary>
        protected bool CheapAttackDodgesRyko() =>
            _ownSpellTrapCount == 0 && _ownMonsters.All(own => own.ID <= 0 || IsCheapAttacker(own));

        /// <summary>
        /// Chance their face-down monster is a flip that destroys a card when flipped (Ryko, Night Assailant), from the deck
        /// lists their cards fit (a deck without Ryko makes it unlikely). Without list data: assumed (1).
        /// </summary>
        protected double FaceDownRykoChance(DynamicCard faceDown) =>
            BoardAssessment.KnownId(faceDown) is int known and > 0 ? (known == RykoId || FlipHitsACard(known) ? 1.0 : 0.0)
            : HamsterSet(faceDown.CurrentCoordinates.Sequence) ? 1.0 // what Mega Hamster set: assumed Ryko (round-robin 29a)
            : OpponentDeck.FlipDestroyChance(faceDown.CurrentCoordinates.Sequence) is double read
                // While we have little idea what deck they're on, a face-down is overweighted as Ryko (user, 2026-11-13: "bump it
                // up to say 30% until you have an idea of what deck the opponent is on").
                ? (OpponentDeck.TopTypeChance() < OpponentDeckPredictor.KnownTypeChance ? Math.Max(read, UnknownDeckRykoChance) : read)
                : 1.0;

        /// <summary>Where FaceDownRykoChance's number came from (for the log: 2026-11-17, every hold note read "Ryko 100%").</summary>
        protected string FaceDownRykoWhy(DynamicCard faceDown)
        {
            int zone = faceDown.CurrentCoordinates.Sequence;
            if (BoardAssessment.KnownId(faceDown) is int known and > 0)
                return $"known card {CardNames.Get(known)} (id {faceDown.ID}, alias {faceDown.Alias}, controller {faceDown.CurrentCoordinates.Controller})";
            if (HamsterSet(zone))
                return "set by Mega Hamster";
            if (OpponentDeck.KnownSet(true, zone) is int pinned)
                return $"pinned by the deck read as {CardNames.Get(pinned)}";
            return OpponentDeck.FlipDestroyChance(zone) is double read
                ? $"deck read {read:P0}" + (OpponentDeck.TopTypeChance() < OpponentDeckPredictor.KnownTypeChance ? $", deck unclear: at least {UnknownDeckRykoChance:P0}" : "")
                : "no deck data";
        }

        /// <summary>Least Ryko chance for a face-down while the deck read is unclear (top type under KnownTypeChance). Tune from logs.</summary>
        [Tunable("Least Ryko chance for a face-down while the deck read is unclear (top type under KnownTypeChance). Tune from logs.")]
        public double UnknownDeckRykoChance { get; set; } = 0.30;

        /// <summary>Whether to play around a Ryko in this face-down monster (its chance is at least RykoPlayAroundChance).</summary>
        /// The bar moves with the risk tolerance (DeckBotBrain.Risk): later in the duel, attacking into a face-down is a risk
        /// taken more readily.
        protected bool LikelyRyko(DynamicCard faceDown) => FaceDownRykoChance(faceDown) >= RykoPlayAroundChance * (RykoRiskOffset + RiskNow);

        /// <summary>Ryko chance from which a face-down monster is played around as a Ryko. Tune from logs.</summary>
        [Tunable("Ryko chance from which a face-down monster is played around as a Ryko. Tune from logs.")]
        public double RykoPlayAroundChance { get; set; } = 0.2;

        // ------------------------------------------------------------------ monsters that bounce their attacker

        /// <summary>
        /// Opponent monsters that return the monster battling them to the hand (Grand Mole). A token returned to the
        /// hand is gone and an Extra Deck monster goes back to the Extra Deck, so only cheap-to-re-summon monsters
        /// should battle them.
        /// </summary>
        private readonly HashSet<int> _bouncesAttacker = new();

        private static readonly System.Text.RegularExpressions.Regex BouncesAttackerText = new(
            @"battle[^.]*return[^.]*(opponent's monster|that monster|monster it battles)[^.]*hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected bool BouncesAttacker(DynamicCard target) => _bouncesAttacker.Contains(target.ID);

        // ------------------------------------------------------------------ playing against Grand Mole
        //
        // Grand Mole (and any monster that returns the monster battling it to the hand):
        //  - Attack it only with something cheap to re-summon (a Level 4 or lower main-deck monster). A token is lost and
        //    an Extra Deck monster goes back to the Extra Deck, so never with those unless that wins the game.
        //    Re-summoning can even be a plus (Armageddon Knight's summon effect again).
        //  - It doesn't threaten our life points much (900 ATK), but it is a strong card: a fine target for removal.
        //  - Our synchros don't deal with it: it attacks into them on its turn and bounces them too. Don't summon
        //    Extra Deck monsters just to "pressure" a board with Mole on it.
        //  - With a card that lets them Normal Summon during our Battle Phase (Ultimate Offering), Mole comes straight
        //    back after each bounce: every attack into it just costs them 500 LP. Attack it once per turn (e.g. to reuse
        //    Armageddon Knight), and more only if those 500 LP payments would finish them.
        //  - Prefer summoning monsters with an on-summon effect while Mole is up: if Mole bounces them (attacking into
        //    them, or them attacking it) we get to summon them and use the effect again. And they're the ones to send
        //    into Mole: the bounce is a plus for us.

        // "When this card is Normal/Special Summoned" (Armageddon Knight, ...): a bounce lets us use it again.
        private static readonly System.Text.RegularExpressions.Regex OnSummonEffectText = new(
            @"when this card is (normal |special |flip )?summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // Concurrent: the search's brain twins fill these caches from their own threads (a plain Dictionary corrupted itself
        // there: "operations that change non-concurrent collections must have exclusive access", seen 2026-11-11).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> OnSummonEffectCache = new();

        /// <summary>Whether a card has an effect when it is summoned (read from its text, cached).</summary>
        protected static async Task<bool> HasOnSummonEffectAsync(int cardId)
        {
            if (cardId <= 0)
                return false;
            if (OnSummonEffectCache.TryGetValue(cardId, out bool known))
                return known;
            bool has = await CardDatabase.GetAsync(cardId) is DBCardInfo info && OnSummonEffectText.IsMatch(info.CardText ?? "");
            OnSummonEffectCache[cardId] = has;
            return has;
        }

        private static bool HasOnSummonEffectCached(DynamicCard card) =>
            OnSummonEffectCache.TryGetValue(card.ID, out bool has) && has;

        /// <summary>
        /// Extra removal value of a card beyond its own score. None now: Grand Mole's worth is in its card score (a general
        /// CardCorrections entry, user 2026-11-13: "mole should be a general bonus"), so it counts wherever it's scored.
        /// Kept so callers stay simple; it still learns which cards bounce their attacker (read from the text), so a Grand
        /// Mole we haven't seen on the field yet is known as one.
        /// </summary>
        protected async Task<(double Bonus, string Why)> RemovalValueBonusAsync(int cardId)
        {
            if (cardId > 0 && !_bouncesAttacker.Contains(cardId) &&
                await CardDatabase.GetAsync(cardId) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
            {
                _bouncesAttacker.Add(cardId);
                BoardAssessment.KnownAttackerBouncers.TryAdd(cardId, 0);
            }
            return (0, "");
        }

        /// <summary>
        /// Whether bouncing this monster (attacking Grand Mole with it) gains us something: an on-summon effect to use
        /// again. Deck brains add their own (e.g. Dark Grepher's effect with a DARK to discard). Requires the on-summon
        /// cache to be filled for the card (HasOnSummonEffectAsync).
        /// </summary>
        protected virtual bool GainsFromBounce(DynamicCard attacker) => HasOnSummonEffectCached(attacker);

        /// <summary>
        /// A better way to deal with Grand Mole than attacking into it: removal we hold that can hit it (a set or hand
        /// card whose text destroys/banishes a monster, without a 1500+ ATK condition: D-Prison, Mirror Force when it
        /// attacks, ...). Deck brains add their own (Dark Armed Dragon, Chaos Sorcerer). Returns its name, or null.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex TributeCostText = new(
            @"\bTribute (1|one|\d+)\b[^;.]*;", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected virtual async Task<string?> BetterAnswerToBouncerAsync()
        {
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0)
                .Concat(await GetBotHandCardsAsync())
                .Select(card => card.ID).Distinct();
            foreach (int id in ours)
            {
                if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) == 0 &&
                    MonsterRemovalText.IsMatch(info.CardText ?? "") && !(info.CardText ?? "").Contains("1500 or more") &&
                    // Not one that costs a monster of ours (Icarus Attack tributes a Blackwing): spending it and a body on the
                    // bouncer is worse than the bounce (seen 2026-11-10: Bora held back from Grand Mole "for Icarus Attack").
                    !TributeCostText.IsMatch(info.CardText ?? ""))
                    return info.Name;
            }
            return null;
        }

        /// <summary>Whether the opponent has a face-up monster that bounces what battles it (Grand Mole).</summary>
        protected bool BouncerOnTheirField => _opponents.Any(card => card.IsFaceup() && BouncesAttacker(card));

        // A known bouncer that left their field without reaching the graveyard / banishment: it went back to their hand
        // (its own bounce does that), so it can come straight back. Forgotten once it shows up anywhere else.
        private readonly HashSet<int> _bouncersOnTheirField = new();
        private bool _bouncerInTheirHand;
        // Pinned set cards already reported in the log.
        private readonly HashSet<(bool Monster, int Zone, int Id)> _pinnedLogged = new();
        // Which bouncer went back to their hand (for the search: it plays it in their turn).
        private int _bouncerInHandId;

        /// <summary>
        /// The cards we know are in their hand: revealed ones (Trap Dustshoot, ...), and a Grand Mole-type monster seen going
        /// back to it. The search plays their turn with these only (user, 2026-11-11: "don't guess at unknowns").
        /// </summary>
        private List<int> KnownEnemyHand()
        {
            List<int> known;
            lock (OpponentDeck)
                known = OpponentDeck.KnownHand();
            if (_bouncerInTheirHand && _bouncerInHandId > 0 && !known.Contains(OpponentDeckPredictor.Normalize(_bouncerInHandId)))
                known.Add(_bouncerInHandId);
            return known;
        }
        private int _bouncersGoneLastSeen;
        private int _bouncersGoneAtBounce;

        /// <summary>
        /// Cheap Grand Mole tracking for every prompt, including the quick "pass" chain prompts in the opponent's turn:
        /// Mole is often summoned, bounces our set monster and returns to their hand within one Battle Phase, all while
        /// we're only asked whether to chain Jar of Greed. Missing that left us setting Ryko into a known Mole
        /// (2026-11-06, four times in one duel).
        /// </summary>
        protected async Task TrackBouncersAsync()
        {
            var opponents = await GetEnemyMonsterCardsAsync();
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_bouncesAttacker.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
                {
                    _bouncesAttacker.Add(card.ID);
                    BoardAssessment.KnownAttackerBouncers.TryAdd(card.ID, 0);
                }
            }
            TrackBouncerInHand(opponents, (await CurrentField.GetBotField()).EnemySide);
        }

        private void TrackBouncerInHand(List<DynamicCard> opponents, CardInfo enemy)
        {
            var onField = opponents.Where(card => card.IsFaceup() && _bouncesAttacker.Contains(card.ID)).Select(card => card.ID).ToHashSet();
            int graveOrBanished = (enemy.Grave ?? Array.Empty<int>()).Concat(enemy.Banish ?? Array.Empty<int>()).Count(_bouncesAttacker.Contains);
            if (onField.Count > 0)
                _bouncerInTheirHand = false; // it's back on the field (BouncerOnTheirField covers it)
            else if (_bouncerInTheirHand && graveOrBanished > _bouncersGoneAtBounce)
                _bouncerInTheirHand = false; // the one in hand went to the graveyard (discarded, ...)
            if (_bouncersOnTheirField.Any(id => !onField.Contains(id)) && graveOrBanished == _bouncersGoneLastSeen)
            {
                _bouncerInTheirHand = true; // left the field without reaching the graveyard / banishment: back in the hand
                _bouncerInHandId = _bouncersOnTheirField.First(id => !onField.Contains(id));
                _bouncersGoneAtBounce = graveOrBanished;
            }
            _bouncersGoneLastSeen = graveOrBanished;
            _bouncersOnTheirField.Clear();
            _bouncersOnTheirField.UnionWith(onField);
        }

        /// <summary>A Grand Mole-type monster is on their field, or known to be in their hand.</summary>
        protected bool BouncerThreat => BouncerOnTheirField || _bouncerInTheirHand;

        /// <summary>
        /// Against Grand Mole, a face-down monster does nothing: Mole attacks it and returns it to our hand (a flipped
        /// Ryko gets bounced too). Setting monsters is scaled by this (1 = no change, 0 = never). Tune from logs.
        /// </summary>
        [Tunable("Against Grand Mole, a face-down monster does nothing: Mole attacks it and returns it to our hand (a flipped Ryko gets bounced too). Setting monsters is scaled by this (1 = no change, 0 = never). Tune from logs.")]
        public double SetIntoBouncerFactor { get; set; } = 0.05;

        private readonly Random _bouncerRandom = new();
        private (int Turn, bool Hold) _setIntoBouncerRoll = (-1, false);

        /// <summary>
        /// Whether to hold monster sets because of Grand Mole: with a bouncer on their field or known in their hand, a
        /// set is allowed only with chance SetIntoBouncerFactor (rolled once per turn). Returns the reason to hold, or null.
        /// </summary>
        protected string? SetIntoBouncerHold<T>(DecisionContext<T> context)
        {
            if (!BouncerThreat)
                return null;
            int turn = context.Header.CurrentTurn;
            if (_setIntoBouncerRoll.Turn != turn)
                _setIntoBouncerRoll = (turn, RollAt >= SetIntoBouncerFactor);
            if (!_setIntoBouncerRoll.Hold)
                return null;
            return $"their Grand Mole-type monster is {(BouncerOnTheirField ? "on the field" : "back in their hand")}: " +
                   $"it would just attack a face-down monster and return it to our hand (set anyway {SetIntoBouncerFactor:P0} of the time)";
        }

        /// <summary>
        /// Against Grand Mole: the summon candidate with an on-summon effect (a bounce lets us use it again), or default
        /// if there's no Mole or no such candidate. Deck brains call this when choosing their Normal Summon.
        /// </summary>
        protected async Task<DynamicCard> PreferOnSummonAgainstBouncerAsync(IEnumerable<DynamicCard> candidates)
        {
            if (!BouncerOnTheirField)
                return default;
            foreach (var card in candidates.Where(card => card.ID > 0 && ReSummonCost(card) < 100))
            {
                if (await HasOnSummonEffectAsync(card.ID))
                    return card;
            }
            return default;
        }

        // Face-up opponent card that lets them Normal Summon again during our Battle Phase (Ultimate Offering).
        private static readonly System.Text.RegularExpressions.Regex ExtraSummonText = new(
            @"pay 500 life points[^.]*normal summon|opponent's battle phase[^.]*normal summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private bool _moleLock;

        /// <summary>A bouncer plus an extra-summon card (Ultimate Offering) face-up: the bouncer keeps coming back.</summary>
        protected bool MoleLockActive => _moleLock;

        /// <summary>
        /// Under the Mole lock, after one attack into the bouncer this turn, further attacks only make the opponent pay
        /// 500 LP to bring it back: stop, unless those payments would finish them.
        /// </summary>
        protected bool MoleLockStopsAttack(int attacksLeft, out string why)
        {
            why = "";
            if (!_moleLock || _bouncerAttacksThisTurn < 1)
                return false;
            if (500 * attacksLeft >= _oppLp)
                return false;
            why = $"with their extra-summon card face-up it comes straight back after each bounce (they only pay 500 LP, " +
                  $"{attacksLeft} attack(s) left vs {_oppLp} LP), and we already attacked it once this turn";
            return true;
        }

        private async Task RefreshMoleLockAsync(List<DynamicCard> opponents)
        {
            bool bouncer = opponents.Any(card => card.IsFaceup() && _bouncesAttacker.Contains(card.ID)) ||
                           (_bouncesAttacker.Count > 0 && _bouncerAttacksThisTurn > 0);
            bool extraSummon = false;
            foreach (var card in (await GetEnemySpellCardsAsync()).Where(card => card.IsFaceup() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && ExtraSummonText.IsMatch(info.CardText ?? ""))
                {
                    extraSummon = true;
                    break;
                }
            }
            _moleLock = bouncer && extraSummon;
        }

        /// <summary>
        /// Whether the rest of our board wins this turn once both the attacker and its target leave the field
        /// (the bouncer returns both): then losing a token or synchro to it is worth it.
        /// </summary>
        protected bool LethalIfBothRemoved(DynamicCard attacker, DynamicCard target)
        {
            var rest = _ownMonsters
                .Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, attacker))
                .Select(own => own.Attack);
            var defenders = BoardAssessment.Defenders(_opponents.Where(card => !SameZone(card, target)), OpponentMemory);
            return BoardAssessment.TurnsToWin(rest, defenders, _oppLp) == 1;
        }

        /// <summary>
        /// What it costs us if this monster is returned to the hand: a token or Extra Deck monster is lost (never
        /// battle a bouncer with them), a Level 5+ costs a tribute or special condition, a Level 4 or lower just a Normal Summon.
        /// </summary>
        protected static int ReSummonCost(DynamicCard card) =>
            card.HasType(CardType.Token) || card.HasType(CardType.Synchro) || card.HasType(CardType.Fusion) ||
            card.HasType(CardType.Xyz) || card.HasType(CardType.Link) ? 100
            : card.Level >= 5 ? 2 : 1;

        /// <summary>
        /// Whether a winning attack is worth making. Recruiters and floaters (Giant Rat, Card Trooper, Kittytail...) are
        /// still worth destroying when we win the battle: floating never makes a monster not worth destroying (user rule,
        /// 2026-11-09). What we don't do is crash into them (see IsGoodTrade). Not into a bouncer that would cost us a
        /// token / Extra Deck monster, unless that wins the game.
        /// </summary>
        protected bool IsWorthAttacking(DynamicCard attacker, DynamicCard target)
        {
            if (GetAttackScore(attacker, target) <= 0)
                return false;
            if (BouncesAttacker(target) && ReSummonCost(attacker) >= 100 && !LethalIfBothRemoved(attacker, target))
                return false; // it would be returned to the hand and lost, and it doesn't win the game
            return true;
        }

        protected const int TreebornFrogId = 12538374;

        // Attackers that banish or bounce the monster they battle (D.D. Warrior Lady, Grand Mole): refreshed per battle decision.
        private HashSet<int> _removesWhatItBattles = new();

        // Their face-up monsters that banish / return whatever battles them (D.D. Warrior Lady), learnt from their text.
        private readonly HashSet<int> _targetRemovesAttacker = new();

        /// <summary>Attack-score units (TargetThreat) for a graveyard piece of ours lost to banishment (Vayu). Tune from logs.</summary>
        [Tunable("Attack-score units (TargetThreat) for a graveyard piece of ours lost to banishment (Vayu). Tune from logs.")]
        public int GraveyardPieceThreat { get; set; } = 1000;

        /// <summary>Attack-score units (TargetThreat) for a hand card spent on a battle boost (Kalut). Tune from logs.</summary>
        [Tunable("Attack-score units (TargetThreat) for a hand card spent on a battle boost (Kalut). Tune from logs.")]
        public int HandBoostCardThreat { get; set; } = 1000;

        private static readonly System.Text.RegularExpressions.Regex RemovesBattledText = new(
            @"(battles?|damage calculation)[^.]{0,120}?\b(banish|return)[^.]{0,60}?(that|the opponent's|opponent's|both)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Their likely monsters that banish / bounce what battles them (from their lists' card texts), built once per duel read.
        private List<int>? _theirBattleRemovers;
        private int _theirBattleRemoversTurn = int.MaxValue;

        /// <summary>
        /// The chance their face-down is a monster that removes what battles it (D.D. Warrior Lady), from the deck read
        /// (an unseen Reinforcement of the Army search counts: see ObserveUnseenSearchesAsync), and the likeliest of them.
        /// </summary>
        protected async Task<(double Chance, string Names)> FaceDownRemovesAttackerChanceAsync(DynamicCard faceDown)
        {
            int turn = _currentHeader.CurrentTurn;
            if (_theirBattleRemovers == null || turn < _theirBattleRemoversTurn || _theirBattleRemovers.Count == 0 && turn != _theirBattleRemoversTurn)
            {
                var list = new List<int>();
                foreach (int id in OpponentDeck.LikelyCards(minWeight: 1e-4))
                    if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) != 0 &&
                        RemovesBattledText.IsMatch(info.CardText ?? ""))
                        list.Add(id);
                _theirBattleRemovers = list;
            }
            _theirBattleRemoversTurn = turn;
            int zone = faceDown.CurrentCoordinates.Sequence;
            var chances = _theirBattleRemovers.Select(id => (Id: id, Chance: OpponentDeck.MonsterZoneChance(zone, id) ?? 0))
                .Where(x => x.Chance > 0).OrderByDescending(x => x.Chance).ToList();
            double none = chances.Aggregate(1.0, (acc, x) => acc * (1 - x.Chance));
            return (1 - none, string.Join(" / ", chances.Take(2).Select(x => CardNames.Get(x.Id))));
        }

        private async Task RefreshRemovesWhatItBattlesAsync(IEnumerable<DynamicCard> attackers)
        {
            foreach (var card in attackers.Where(card => card.ID > 0 && !_removesWhatItBattles.Contains(card.ID)))
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && RemovesBattledText.IsMatch(info.CardText ?? ""))
                    _removesWhatItBattles.Add(card.ID);
        }

        /// <summary>
        /// Attacking their Treeborn Frog achieves nothing: in defence it just comes back next Standby Phase (user, 2026-11-12).
        /// Unless it's in attack position (damage), destroying it clears the path for a direct hit this turn (our attackers
        /// can take every monster of theirs, Treeborn included, with one left over: they may start with two monsters against
        /// our three), their spell/trap stops it coming back, or our attacker banishes / bounces what it battles. (Attacking it
        /// only to unblock the server's attack order, e.g. a 100 into its 100 DEF, safe, is the zone-order code's call.)
        /// Returns why it's pointless, or null.
        /// </summary>
        protected string? TreebornAttackPointless(DynamicCard attacker, DynamicCard target, IReadOnlyList<DynamicCard> theirMonsters,
            IReadOnlyList<int> ourAttackers, int theirSpellTraps)
        {
            if (!target.IsCode(TreebornFrogId) || !target.IsFaceup() || target.IsAttack())
                return null;
            // Clears the path: each of their monsters (biggest first) taken by our weakest attacker that beats it, one attacker
            // still left for the direct hit.
            var left = ourAttackers.OrderBy(atk => atk).ToList();
            bool cleared = true;
            foreach (var monster in theirMonsters.OrderByDescending(card => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card)
                         : card.IsAttack() ? card.Attack : card.Defense))
            {
                int power = !monster.IsFaceup() ? OpponentMemory.FaceDownDefense(monster) : monster.IsAttack() ? monster.Attack : monster.Defense;
                int index = left.FindIndex(atk => atk > power);
                if (index < 0) { cleared = false; break; }
                left.RemoveAt(index);
            }
            if (cleared && left.Count > 0)
                return null; // destroying it is part of clearing the way for a direct hit
            if (theirSpellTraps > 0)
                return null; // their backrow keeps it in the graveyard
            if (_removesWhatItBattles.Contains(attacker.ID))
                return null;
            return "it's Treeborn Frog in defence: it comes back next Standby Phase (they have no spell/trap to stop it), and nothing follows it for a direct hit";
        }

        /// <summary>A floater that Special Summons a replacement when destroyed (Giant Rat): it replaces itself.</summary>
        protected bool IsRecruiter(DynamicCard target) => _recruiters.Contains(target.ID);

        private static readonly System.Text.RegularExpressions.Regex AttacksDirectlyText = new(
            @"can attack (your opponent )?directly", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A monster that can attack directly (Jinzo - Returner): on Omega its attack is always a direct one, even into monsters, as Omega never
        /// asks "attack directly?" (seen 2026-11-17: Returner, summoned to destroy Lyla, hit directly). It can't be planned to battle a monster.
        /// </summary>
        protected static bool AttacksOnlyDirectlyOnOmega(int code) =>
            code > 0 && Search.SearchHost.Available && Search.SearchHost.Cards.Get(code)?.Text is string text && AttacksDirectlyText.IsMatch(text);

        // Their life points as read at this decision (PrepareContextAsync sets it from the field every time; the clock checks below).
        // 0 until the first read: then no clock speed-up is assumed.
        private int _theirLifePoints;

        // "Special Summon that monster to your field" after destroying it by battle: Goyo Guardian and the like.
        private static readonly System.Text.RegularExpressions.Regex StealsOnBattleText = new(
            @"destroys? [^.]{0,40}monster by battle[^.]{0,80}Special Summon (that|the destroyed) monster to your (side of the )?field",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>A monster that takes what it destroys by battle (Goyo Guardian).</summary>
        protected static bool StealsWhatItDestroys(DynamicCard attacker) =>
            attacker.ID > 0 && Search.SearchHost.Available && Search.SearchHost.Cards.Get(attacker.ID)?.Text is string text && StealsOnBattleText.IsMatch(text);

        /// <summary>
        /// Whether taking this recruiter is worth it (user, 2026-11-17: "if I had this monster, on the field able to attack next turn, would
        /// that speed up my opponent's clock?" and "only ... if you have a way of protecting that recruiter from being attacked. it's risky to
        /// take opponents recruiters so this should only be done very rarely"): with it attacking next turn alongside our face-up attack-position
        /// monsters, their life points fall in fewer of our turns, and a set card of ours can protect it.
        /// </summary>
        private bool StealSpeedsUpClock(DynamicCard attacker, DynamicCard target)
        {
            if (!_weCanProtect)
                return false;
            int total = _ownMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => Math.Max(0, card.Attack));
            int with = total + Math.Max(0, target.Attack);
            if (with <= 0 || _theirLifePoints <= 0)
                return false;
            int Turns(int atk) => atk <= 0 ? int.MaxValue : (_theirLifePoints + atk - 1) / atk;
            return Turns(with) < Turns(total);
        }

        // Whether a set spell/trap of ours can protect a monster (Waboku, Mirror Force...: SearchCardValues.Protection), at this decision.
        private bool _weCanProtect;

        /// <summary>
        /// A recruiter that only recruits when destroyed by battle (Giant Rat, Mystic Tomato): any removal outside battle
        /// (Mirror Force, D-Prison, Bottomless...) stops its replacement. Others (recruit on any destruction or on being
        /// sent to the graveyard) are only stopped by banishing.
        /// </summary>
        protected bool RecruitsOnlyByBattle(DynamicCard target) => _battleOnlyRecruiters.Contains(target.ID);

        private readonly HashSet<int> _battleOnlyRecruiters = new();

        // A recruit trigger not tied to battle: "destroyed" / "sent to the graveyard" without "by battle" before the summon.
        private static readonly System.Text.RegularExpressions.Regex RecruitOnAnyDestructionText = new(
            @"(destroyed(?! by battle)(?! and sent to the (graveyard|gy) by battle)|(?<!by battle and )sent (from the field )?to the (graveyard|gy)(?! by battle))[^.]{0,120}?special summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // ------------------------------------------------------------------ big vs small threats

        /// <summary>Effect score (see <see cref="EffectEvaluator"/>) from which a monster counts as a big threat on its effect alone.</summary>
        [Tunable("Effect score (see EffectEvaluator) from which a monster counts as a big threat on its effect alone.")]
        public double StrongEffectScore { get; set; } = 6;

        /// <summary>
        /// A big threat is worth spending resources (removal, an Extra Deck summon, a trap) on:
        ///  - 2000+ ATK, an Extra Deck monster, or a bouncer (Grand Mole),
        ///  - it beats a monster of ours that isn't expendable,
        ///  - or its effect is strong (score at least <see cref="StrongEffectScore"/>, e.g. Thought Ruler Archfiend).
        /// Everything else is a small threat (Giant Rat, Kinka-byo, a 1600 beater): handled by the clock, not by spending
        /// on it. A recruiter (Giant Rat) only counts on ATK or what it beats: its effect is a reason not to destroy it.
        /// Face-down cards (assumed Ryko) are never big threats.
        /// </summary>
        protected async Task<bool> IsBigThreatAsync(int id, int power)
        {
            if (id <= 0)
                return false;
            if (power >= StrongMonsterAttack)
                return true;
            if (_bouncesAttacker.Contains(id) || BoardAssessment.KnownAttackerBouncers.ContainsKey(id))
                return true;
            if (await CardDatabase.GetAsync(id) is DBCardInfo info &&
                (info.Type & ((int)CardType.Synchro | (int)CardType.Fusion | (int)CardType.Xyz | (int)CardType.Link)) != 0)
                return true;
            // On our turn, a monster that negates our attacks is a wall against our clock (weak only as an attacker).
            if (IsOwnTurn(_currentHeader) && await CardDatabase.GetAsync(id) is DBCardInfo negInfo &&
                AttackNegateText.IsMatch(negInfo.CardText ?? ""))
                return true;
            var own = await GetBotMonsterCardsAsync();
            if (own.Any(card => card.IsFaceup() && !IsExpendable(card) && power >= card.GetRealPower()))
                return true;
            if (_recruiters.Contains(id))
                return false;
            var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
            return (await EffectEvaluator.ScoreOnFieldAsync(id, grave)).Score >= StrongEffectScore;
        }

        protected Task<bool> IsBigThreatAsync(DynamicCard enemy) => IsBigThreatAsync(enemy.ID, enemy.GetRealPower());

        /// <summary>"Negate the attack" (XX-Saber Fulhelmknight): weak against us when they attack, a wall when we do.</summary>
        protected static readonly System.Text.RegularExpressions.Regex AttackNegateText = new(
            @"\bnegate[^.]{0,20}\battack\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The big threats among <paramref name="threats"/> (see <see cref="IsBigThreatAsync(int, int)"/>).</summary>
        protected async Task<List<BoardAssessment.Defender>> BigThreatsAsync(IEnumerable<BoardAssessment.Defender> threats)
        {
            await RefreshRewardOnDestroyAsync(await GetEnemyMonsterCardsAsync());
            var big = new List<BoardAssessment.Defender>();
            foreach (var threat in threats)
                if (await IsBigThreatAsync(threat.Id, threat.Power))
                    big.Add(threat);
            return big;
        }

        // Floaters whose reward is a Special Summon (they replace themselves on the field: Giant Rat, Mystic Tomato, ...).
        private readonly HashSet<int> _recruiters = new();

        private static readonly System.Text.RegularExpressions.Regex RecruitText = new(
            @"(destroyed|sent (from the field )?to the (graveyard|gy))[^.]{0,120}?special summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Assumed ATK/DEF of the monster a recruiter brings out (they fetch small monsters, usually 1500 or less).</summary>
        [Tunable("Assumed ATK/DEF of the monster a recruiter brings out (they fetch small monsters, usually 1500 or less).")]
        public int AssumedRecruitPower { get; set; } = 1500;

        /// <summary>
        /// Whether destroying this floater lets the rest of our attacks win this turn. A recruiter brings out a
        /// replacement, assumed to block one more attack in defence position.
        /// </summary>
        private bool LethalIfFloaterRemoved(DynamicCard attacker, DynamicCard target)
        {
            var rest = _ownMonsters
                .Where(own => own.IsFaceup() && own.IsAttack() && !own.Attacked && !SameZone(own, attacker))
                .Select(own => own.Attack);
            var defenders = BoardAssessment.Defenders(_opponents.Where(card => !SameZone(card, target)), OpponentMemory);
            if (_recruiters.Contains(target.ID))
                defenders.Add(new BoardAssessment.Defender(AssumedRecruitPower, false, false, 0));
            return BoardAssessment.TurnsToWin(rest, defenders, _oppLp) == 1;
        }

        // Our monsters that destroy any monster they battle unless it has a given Attribute (Ally of Justice Catastor:
        // "battles a non-DARK monster ... destroy"), filled from card text. Id -> the Attribute that is spared.
        // Known ones up front (Ally of Justice Catastor, official and Omega ids): never left to a card text lookup that can fail.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CardAttribute> BattleDestroyersExcept = new()
        {
            [26593852] = CardAttribute.Dark,
            [26593872] = CardAttribute.Dark,
        };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> CheckedBattleDestroyers = new();

        private static readonly System.Text.RegularExpressions.Regex BattleDestroyerText = new(
            @"battles a non-(dark|light|earth|water|fire|wind|divine) monster[^.]*destroy",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Reads our monsters' text once each for Catastor-like battle destruction.</summary>
        protected static async Task LearnBattleDestroyersAsync(IEnumerable<int> ourMonsterIds)
        {
            foreach (int id in ourMonsterIds.Where(id => id > 0 && !CheckedBattleDestroyers.ContainsKey(id)).Distinct().ToList())
            {
                // Marked as checked only once its text is read: a failed lookup (server busy) is retried next time instead of
                // leaving a Catastor unknown for the rest of the session (seen 2026-11-10: Goyo attacked into Catastor).
                if (await CardDatabase.GetAsync(id) is not DBCardInfo info)
                    continue;
                CheckedBattleDestroyers.TryAdd(id, 0);
                var match = BattleDestroyerText.Match(info.CardText ?? "");
                if (match.Success && System.Enum.TryParse<CardAttribute>(match.Groups[1].Value, ignoreCase: true, out var spared))
                    BattleDestroyersExcept[id] = spared;
            }
        }

        /// <summary>
        /// Whether their monster with this id destroys a monster of this Attribute by its effect when they battle (their
        /// Catastor vs our non-DARK monster), whoever attacks.
        /// </summary>
        protected static bool DestroysAttributeInBattle(int monsterId, int attribute) =>
            monsterId > 0 && BattleDestroyersExcept.TryGetValue(monsterId, out var spared) && (attribute & (int)spared) == 0;

        /// <summary>Whether the attacker's effect destroys this (face-up) defender whatever the ATK (Catastor vs non-DARK).</summary>
        protected static bool DestroysByEffectInBattle(DynamicCard attacker, DynamicCard defender) =>
            BattleDestroyersExcept.TryGetValue(attacker.ID, out var spared) && defender.IsFaceup() &&
            (defender.Attribute & (int)spared) == 0;

        /// <summary>ATK our hand can add to this attacker in the damage step (Kalut: +1400 to a Blackwing). Default: none.</summary>
        protected virtual int HandBattleBoost(DynamicCard attacker) => 0;

        /// <summary>Attack score for running into a known Honest (or Kalut) when the attack wins without it: a trade, below any kill (10,000+).</summary>
        [Tunable("Attack score for running into a known Honest (or Kalut) when the attack wins without it: a trade, below any kill (10,000+).")]
        public int HonestTradeScore { get; set; } = 6_000;

        protected const int HonestId = 37742478;
        protected const int KalutId = 85215458;
        protected const int BattleFaderId = 19665973;

        [Tunable("Honest counts as suspected in their hand from this deck-read chance (or when known).")]
        public static double HonestSuspectChance = 0.3;

        /// <summary>Honest is known in their hand, or the deck read puts it there at HonestSuspectChance or more.</summary>
        protected bool HonestSuspected(int theirHandCount) =>
            KnownEnemyHand().Any(id => id == HonestId || Search.SearchHost.CanonicalId(id) == HonestId) ||
            (OpponentDeck.HandChance(HonestId, theirHandCount) ?? 0) >= HonestSuspectChance;

        /// <summary>
        /// What a card we know is in their hand adds to their monster in a battle with this attacker: Honest gives a LIGHT
        /// monster the attacker's ATK, Kalut +1400 to a Blackwing. 0 when we know of none.
        /// </summary>
        protected int EnemyHandBattleBoost(DynamicCard defender, DynamicCard attacker)
        {
            if (!defender.IsFaceup())
                return 0;
            var known = KnownEnemyHand();
            if (known.Any(id => id == HonestId || Search.SearchHost.CanonicalId(id) == HonestId) && (defender.Attribute & 0x10) != 0 && defender.IsAttack())
                return attacker.Attack;
            if (known.Any(id => id == KalutId || Search.SearchHost.CanonicalId(id) == KalutId) &&
                Search.SearchHost.Available && Search.SearchHost.Cards.Get(defender.ID)?.Name?.StartsWith("Blackwing") == true)
                return 1400;
            return 0;
        }

        protected int GetAttackScore(DynamicCard attacker, DynamicCard defender)
        {
            int attackerPower = attacker.Attack;
            int defenderPower = defender.GetRealPower();
            // A boost from our hand in the damage step (Kalut's +1400) counts when it turns a lost or tied battle into a win: the
            // brain plans its attacks with it (user, 2026-11-10), and the chain rules then use it at the battle.
            if (attackerPower <= defenderPower && HandBattleBoost(attacker) is int boost and > 0 && attackerPower + boost > defenderPower)
                attackerPower += boost;
            // ...and theirs, when we know it's in their hand (user, 2026-11-11: the known Honest): it wins their monster the battle.
            // An attack that wins without it still goes, as a trade: our attacker for their Honest, which has to be got past
            // some time (user, 2026-11-11: "it still should've attacked"). Below any real kill.
            int handBoost = EnemyHandBattleBoost(defender, attacker);
            if (handBoost > 0 && attackerPower > defenderPower && attackerPower <= defenderPower + handBoost &&
                !DestroysByEffectInBattle(defender, attacker))
                return HonestTradeScore + TargetThreat(defender) / 4;
            defenderPower += handBoost;

            // Their Catastor-type monster destroys ours by its effect when they battle (whatever the ATK, and not by battle,
            // so no "destroyed by battle" payoff either): never attack into it, unless ours destroys it the same way.
            if (DestroysByEffectInBattle(defender, attacker) && !DestroysByEffectInBattle(attacker, defender))
                return 0;

            bool effectKill = DestroysByEffectInBattle(attacker, defender);
            if (attackerPower <= defenderPower && !effectKill)
                return 0;
            // "Cannot be destroyed by battle": it survives, so in defence attacking it does nothing. In attack position the
            // damage still goes through (a small score: below any kill), unless it also stops the battle damage (a live
            // Blackwing Armor Master: "you take no battle damage"). Both checks say no when its effects are negated.
            if (IsBattleProof(defender) && !effectKill)
                return defender.IsFaceup() && defender.IsAttack() && !StopsBattleDamage(defender)
                    ? 10_000 + (attackerPower - defenderPower) / 4
                    : 0;

            // "Once per turn, cannot be destroyed by battle" (Gemini Soldier): the first attack this turn only uses up
            // the protection (a small score: it does let a second attack finish it); once we've battled it this turn
            // its protection is gone, so a second attack destroys it, worth its full threat plus a bonus for finishing it.
            if (_oncePerTurnBattleProtected.Contains(defender.ID))
            {
                bool battled = _battledThisTurn.Contains((CurrentHeader.CurrentTurn, defender.CurrentCoordinates.Sequence, defender.ID));
                if (!battled)
                    return 10_000 + TargetThreat(defender) / 4;
                return 10_000 + 2 * TargetThreat(defender) + 1_000;
            }
            if (effectKill && attackerPower <= defenderPower)
                return 10_000 + 2 * TargetThreat(defender); // destroyed before damage: no battle damage either way

            // Killing the bigger threat matters far more than the battle damage (life points matter little), so the
            // target's threat counts double and damage only a quarter. E.g. Brionac (2300) over Moja (100) even though
            // hitting Moja deals more damage, and Muka Muka (2000/1400 in DEF) over a 1400/1400 token.
            int score = 10_000 + 2 * TargetThreat(defender);

            if (defender.IsFaceup() && defender.IsAttack())
                score += (attackerPower - defenderPower) / 4;
            // What the attack costs us (user, 2026-11-13: Vayu + Kalut into a face-up D.D. Warrior Lady, banishing Vayu, when
            // Card Trooper was there for Vayu alone: "why not just attack into card trooper?"):
            //  - a target that banishes / returns what battles it (not Grand Mole: its own rules) takes our attacker too, and
            //    a graveyard piece (Vayu) loses its graveyard value with it;
            //  - a win that needs a boost from our hand (Kalut) spends that card.
            if (defender.IsFaceup() && _targetRemovesAttacker.Contains(defender.ID) && !BouncesAttacker(defender))
                score -= 2 * (TargetThreat(attacker) + (HasGraveyardEffect(attacker) ? GraveyardPieceThreat : 0));
            if (attacker.Attack <= defender.GetRealPower() && attackerPower > defender.GetRealPower())
                score -= 2 * HandBoostCardThreat;
            return score;
        }

        /// <summary>
        /// How dangerous a face-up opponent monster is: its ATK (or DEF if higher), plus a bonus for having an effect
        /// and for being an Extra Deck monster (synchros like Brionac usually carry strong effects).
        /// </summary>
        protected int TargetThreat(DynamicCard defender)
        {
            int threat = Math.Max(defender.Attack, defender.Defense);
            // A monster whose effects are negated (e.g. a synchro summoned by Vayu) is just its body: no effect or Extra
            // Deck bonus. So at equal ATK a negated synchro is worth less than a monster with live effects, and crashing
            // it into one is a good trade.
            if (IsEffectNegated(defender))
                return threat;
            // A flip monster face-up has used its effect (a flipped Ryko): it's just its stats too (user, 2026-11-13: "because all
            // of their effects have been used, the only thing that mattered there should've been their stats").
            if (IsSpentFlipMonster(defender))
                return threat;
            // Effects balanced against ATK: about 500 per effect point (a strong effect of 4-6 points is worth a 2000-3000
            // swing), read from the effect scores cached when we read their monsters; a flat 500 if not scored yet.
            if (EffectPointsCache.TryGetValue(defender.ID, out double points))
                threat += (int)(points * EffectPointValue);
            else if (defender.HasType(CardType.Effect))
                threat += 500;
            if (defender.HasType(CardType.Synchro) || defender.HasType(CardType.Fusion) ||
                defender.HasType(CardType.Xyz) || defender.HasType(CardType.Link))
                threat += 500;
            return threat;
        }
    }
}
