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
    /// The Battle Phase: which attack to declare, or when to leave. ChooseBattleCmdAsync lists the attack rules in the
    /// order they are tried (each rule is in Battle/Rules).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        public override async Task<BattleCmdResponse> OnBattleCmdAsync(DecisionContext<BattleCmdData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            // Learn from the last attack (did their set cards stay down when Mirror Force would have paid off?).
            await RefreshBackrowOddsAsync(context);
            await RiskToleranceAsync(); // RiskNow, for the attack checks (LikelyRyko)
            PrepareBattleTurn(context.Header.CurrentTurn);
            return await ChooseBattleActionAsync(context);
        }

        private async Task RecordBattleChoiceAsync(DecisionContext<BattleCmdData> context, BattleCmdResponse response)
        {
            PrepareBattleTurn(context.Header.CurrentTurn);
            if (response.Action == BattleAction.Attack)
            {
                var attacker = Search.SearchActions.AttackerFor(context.StateData, response.Index);
                _declaredAttacker = attacker;
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
        }

        private void PrepareBattleTurn(int turn)
        {
            if (turn != _battleTurn)
            {
                _battleTurn = turn;
                _bouncerAttacksThisTurn = 0;
            }
        }

        private int _battleTurn = -1;
        private int _bouncerAttacksThisTurn;
        // The monster attacking now (for the target prompt that follows the attack).
        private DynamicCard _declaredAttacker;

        /// <summary>
        /// A target the deck wants this attacker to take out before
        /// anything else, and why, or null. Default: none.
        /// </summary>
        protected virtual Task<(DynamicCard Target, string Why)?> PriorityAttackTargetAsync(DynamicCard attacker) =>
            Task.FromResult<(DynamicCard, string)?>(null);

        private async Task<BattleCmdResponse> ChooseBattleActionAsync(DecisionContext<BattleCmdData> context)
        {
            var all = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();
            // Return from the Different Dimension for lethal, before the attacks it adds to.
            if (await TryReturnDimensionLethalInBattleAsync(context) is BattleCmdResponse returnLethal)
                return returnLethal;
            // What we committed to this turn comes first: an attack a rule summoned for, a lethal push (DeckBotBrain.Commitments).
            if (await CommittedAttackAsync(context) is BattleCmdResponse committed)
                return committed;
            foreach (var attacker in all)
            {
                if (await PriorityAttackTargetAsync(attacker) is not { } priority)
                    continue;
                AimAt(context.Bot, priority.Target, AimKind.Deliberate, $"{CardNames.Get(attacker.ID)} attacks {DescribeTarget(priority.Target)} first: {priority.Why}");
                return attacker.DeclareAttack();
            }
            return await ChooseBattleCmdAsync(context);
        }

        private static string DescribeTarget(DynamicCard target) =>
            target.IsFaceup() && target.ID > 0 ? CardNames.Get(target.ID) : "a face-down monster";

        /// <summary>What one attack decision sees: the prompt, our attackers (biggest first) and their monsters.</summary>
        private sealed class BattleSituation
        {
            public BattleSituation(DecisionContext<BattleCmdData> context, DynamicCard[] offered, List<DynamicCard> attackers, List<DynamicCard> targets)
            {
                Context = context;
                Offered = offered;
                Attackers = attackers;
                Targets = targets;
            }

            public DecisionContext<BattleCmdData> Context { get; }
            /// <summary>Every monster the prompt offers to attack with.</summary>
            public DynamicCard[] Offered { get; }
            /// <summary>Our monsters that can attack now: not attacked yet, not disabled; biggest ATK first.</summary>
            public List<DynamicCard> Attackers { get; }
            /// <summary>Their monsters we may attack.</summary>
            public List<DynamicCard> Targets { get; }
            public List<DynamicCard> FaceUpTargets { get; set; } = new();
            public List<DynamicCard> FaceDownTargets { get; set; } = new();
            /// <summary>Their set spells/traps (counted once their monsters are known).</summary>
            public int TheirSpellTraps { get; set; }
            /// <summary>Our life points (read before weighing crashes).</summary>
            public int OurLifePoints { get; set; }
        }

        /// <summary>Leaving the Battle Phase: to Main Phase 2 when we can, else the End Phase.</summary>
        private static BattleCmdResponse LeaveBattle(DecisionContext<BattleCmdData> context) =>
            context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;

        /// <summary>
        /// The generic attack choice. The steps, in order (the first that declares an attack, or leaves, decides):
        ///  1. nothing can attack: leave;
        ///  2. their field has no monsters: direct attacks (Battle/DirectAttacks.cs);
        ///  3. an unknown face-down monster: who probes it (Battle/UnknownFaceDown.cs);
        ///  4. a face-down monster we saw face-up before (Battle/Rules/AttackRememberedFaceDown.cs);
        ///  5. a face-up monster: the best kill, trade or crash, weighed (Battle/FaceUpAttacks.cs);
        ///  6. an attack made safe by Shield Warrior / Waboku (DeckBotBrain.BattleProtection.cs);
        ///  7. no attack gains anything: leave (Battle/Rules/NoAttackWithoutGain.cs).
        /// </summary>
        private async Task<BattleCmdResponse> ChooseBattleCmdAsync(DecisionContext<BattleCmdData> context)
        {
            ConsoleUtils.PrintColored("OnBattleCmd - Generic attack evaluation");
            var offered = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();
            var attackers = offered
                .Where(card => !card.Attacked && !card.IsDisabled())
                .OrderByDescending(card => card.Attack)
                .ToList();

            // 1. Nothing can attack.
            if (!attackers.Any())
            {
                ConsoleUtils.PrintColored("No attackable cards available.");
                ClearAim();
                return LeaveBattle(context);
            }

            var targets = await AttackableTargetsAsync((await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList());
            await RefreshRewardOnDestroyAsync(targets);
            var battle = new BattleSituation(context, offered, attackers, targets);

            // 2. Their field has no monsters.
            if (!targets.Any())
                return await AttackDirectlyAsync(battle);

            // Face-down monsters: the bot must not use their hidden stats (OpponentMemory remembers ones seen face-up).
            OpponentMemory.Observe(targets);
            await RefreshRewardOnDestroyAsync(targets);
            battle.FaceUpTargets = targets.Where(card => card.IsFaceup()).ToList();
            battle.FaceDownTargets = targets.Where(card => !card.IsFaceup()).ToList();

            return await AttackUnknownFaceDownAsync(battle)                    // 3.
                ?? AttackRememberedFaceDown(battle)                           // 4.
                ?? await AttackFaceUpMonsterAsync(battle)                     // 5.
                ?? await TryProtectedAttackAsync(context, attackers, targets) // 6.
                ?? NoAttackWithoutGain(battle);                               // 7.
        }
    }
}
