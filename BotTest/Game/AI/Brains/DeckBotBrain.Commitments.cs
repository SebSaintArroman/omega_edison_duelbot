using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using BotTest.Game.AI.Search;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Turn commitments: what one part of the logic committed to this turn, which later parts honour, so the bot doesn't
    /// throw away its own plan halfway. The user calls a later rule overriding an earlier plan an "inconsistency", the
    /// cause of the worst plays (2026-11-12):
    ///  - Jinzo - Returner summoned "to crash into Rai-Mei", then the Battle Phase skipped as "nothing it can attack";
    ///  - Machina Fortress summoned as "lethal this turn", then the attack held for fear of Gorz;
    ///  - the search replacing the brain's move with a lethal line (summon Ryko), then the brain's battle rule stopping it.
    /// The commitments:
    ///  - an attack (this attacker into this target): the Battle Phase isn't skipped, and the attack is made first;
    ///  - a lethal push: the Battle Phase isn't skipped, and no hold (Gorz fear, chip into backrow, weak attackers) stops it;
    ///  - the search's line, once it replaced the brain's move: its later main / battle steps are taken while on offer.
    /// A commitment dropped is always logged with why.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private readonly List<(int Turn, int AttackerId, int TargetZone, int TargetId, string Why)> _attackCommitments = new();
        private (int Turn, string Why)? _lethalPush;
        // The search's line after the move it replaced: its own main / battle steps, and how far along we are.
        private (int Turn, List<PlanStep> Steps, int Next)? _searchLine;

        /// <summary>How an attack target was chosen: it decides whether the search's target may replace it (AimAttack).</summary>
        protected enum AimKind
        {
            /// <summary>Whatever the step order of the battle logic reached first (a free kill, a harmless hit): no plan behind it.</summary>
            Fallback,
            /// <summary>A rule chose it for a reason of its own (a face-down probe, a bouncer, a protected attack, a deck's priority).</summary>
            Deliberate,
            /// <summary>A commitment made earlier this turn (the move that set the attack up): it stands.</summary>
            Committed,
            /// <summary>The search's target (its line, or its statistics beat a fallback).</summary>
            Search,
        }

        private AimKind _aimKind = AimKind.Fallback;

        /// <summary>
        /// The one way to choose an attack target (null: a direct attack). Every choice has a reason, written to the duel log
        /// (user, 2026-11-12: "nothing picks a target silently"; round-robin 1 picked Lyla with no note at all).
        /// </summary>
        protected void AimAt(DuelBotWrapper bot, DynamicCard? target, AimKind kind, string why)
        {
            _selectedDefenderCard = target;
            _aimKind = kind;
            bot.LogNote($"deck brain: {why}");
        }

        /// <summary>No attack target (no attack, or the target comes from elsewhere): nothing to log.</summary>
        private void ClearAim()
        {
            _selectedDefenderCard = null;
            _aimKind = AimKind.Fallback;
        }

        // Commitments a rule made at this decision, for the move it chose: they take effect only once that move is really made
        // (SettleCommitments, when the decision's final answer is known). A move the search replaced, or a later rule changed,
        // leaves no commitment behind (user, 2026-11-12: "make sure the commitattack stuff works consistently").
        private readonly List<(int Turn, int AttackerId, int TargetZone, int TargetId, string Why)> _pendingAttacks = new();
        private (int Turn, string Why)? _pendingLethalPush;

        /// <summary>
        /// Commit to attacking this target with this attacker this turn: the rule that summons (or sets up) the attacker for
        /// that attack. Takes effect when the move is made (the move's card is the attacker).
        /// </summary>
        protected void CommitAttack(int turn, int attackerId, DynamicCard target, string why) =>
            _pendingAttacks.Add((turn, attackerId, target.CurrentCoordinates.Sequence, target.ID, why));

        /// <summary>Commit to this attacker attacking this turn (directly, or the best monster it beats): a summon made to hit now.</summary>
        protected void CommitAttackThisTurn(int turn, int attackerId, string why) =>
            _pendingAttacks.Add((turn, attackerId, -1, 0, why));

        /// <summary>Commit to an all-out attack this turn: a spend was justified as lethal. Takes effect when the brain's move is made.</summary>
        protected void CommitLethalPush(int turn, string why) => _pendingLethalPush = (turn, why);

        /// <summary>
        /// The decision's final answer is known: the commitments made for it take effect when their move is the one made (an
        /// attack commitment: the move uses the attacker's card, or the attacker is already on the field and the brain's move
        /// went ahead; a lethal push: the brain's move went ahead), and are dropped, with a note, otherwise.
        /// </summary>
        protected void SettleCommitments(DuelBotWrapper bot, int turn, int movedCardId, bool movedSummons, bool brainMoveKept, IReadOnlyCollection<int> ourMonsterIds)
        {
            _attackCommitments.RemoveAll(c => c.Turn != turn);
            foreach (var pending in _pendingAttacks.Where(p => p.Turn == turn))
            {
                // The brain's move went ahead and it is (or summons) the attacker; or the search replaced it with a face-up summon of
                // that same attacker (not a set: a set monster can't attack; seen in the entry-1 replay, a set Returner kept the crash).
                bool made = brainMoveKept ? pending.AttackerId == movedCardId || ourMonsterIds.Contains(pending.AttackerId)
                                          : pending.AttackerId == movedCardId && movedSummons;
                if (made)
                {
                    // The latest plan for an attacker replaces any earlier one (seen 2026-11-13: Laquari "summoned to destroy Ryko",
                    // then boosted twice over Fortress by a routine; the first commitment won and the boosts went to waste).
                    int replaced = _attackCommitments.RemoveAll(c => c.AttackerId == pending.AttackerId);
                    if (replaced > 0)
                        bot.LogNote($"deck brain: {CardNames.Get(pending.AttackerId)}'s earlier attack plan is replaced: {pending.Why}");
                    _attackCommitments.Add(pending);
                    bot.LogNote($"deck brain: committed: {CardNames.Get(pending.AttackerId)} attacks " +
                                $"{(pending.TargetZone < 0 ? "this turn" : $"{(pending.TargetId > 0 ? CardNames.Get(pending.TargetId) : "the monster")} in zone {pending.TargetZone + 1}")} ({pending.Why})");
                }
                else
                    bot.LogNote($"deck brain: not committed: {CardNames.Get(pending.AttackerId)}'s attack ({pending.Why}): the move it was planned with wasn't made");
            }
            _pendingAttacks.Clear();
            if (_pendingLethalPush is { } push && push.Turn == turn)
            {
                if (brainMoveKept)
                {
                    _lethalPush = push;
                    bot.LogNote($"deck brain: committed: an all-out attack this turn ({push.Why})");
                }
                else
                    bot.LogNote($"deck brain: not committed: the all-out attack ({push.Why}): the move it was planned with wasn't made");
            }
            _pendingLethalPush = null;
        }

        /// <summary>A lethal push committed this turn.</summary>
        protected bool LethalPushNow => _lethalPush is { } push && push.Turn == _currentHeader.CurrentTurn;

        /// <summary>Anything committed this turn that needs the Battle Phase.</summary>
        protected bool BattleCommitted =>
            LethalPushNow || _attackCommitments.Any(c => c.Turn == _currentHeader.CurrentTurn) ||
            _searchLine is { } line && line.Turn == _currentHeader.CurrentTurn && line.Steps.Skip(line.Next).Any(s => s.Key == "battle" || s.Key.StartsWith("attack:"));

        /// <summary>
        /// The committed attack for the monster the server attacks with now (the first in zone order), or for a lethal push
        /// an attack that keeps it going. Null when there's none (the deck's usual battle logic decides).
        /// </summary>
        private async Task<BattleCmdResponse?> CommittedAttackAsync(DecisionContext<BattleCmdData> context)
        {
            var all = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();
            int turn = context.Header.CurrentTurn;
            _attackCommitments.RemoveAll(c => c.Turn != turn);
            if (all.Length == 0)
                return null;
            var first = all[0];
            var enemy = await GetEnemyMonsterCardsAsync();
            int PowerOf(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            foreach (var commitment in _attackCommitments.ToList())
            {
                // "Attacks this turn" (no set target): direct into a clear field, else the most threatening monster it beats.
                if (commitment.TargetZone < 0)
                {
                    if (!first.IsCode(commitment.AttackerId))
                    {
                        if (!all.Any(card => card.IsCode(commitment.AttackerId)))
                            _attackCommitments.Remove(commitment);
                        continue;
                    }
                    _attackCommitments.Remove(commitment);
                    if (enemy.Count == 0 && first.CanDirectAttack)
                    {
                        AimAt(context.Bot, null, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks directly as committed: {commitment.Why}");
                        return HeldCommittedAttack(first);
                    }
                    var prey = enemy.Where(card => first.Attack > PowerOf(card)).OrderByDescending(OpponentThreat).FirstOrDefault();
                    if (prey.CurrentCoordinates.Location != 0)
                    {
                        AimAt(context.Bot, prey, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks {DescribeTarget(prey)} as committed: {commitment.Why}");
                        return HeldCommittedAttack(first);
                    }
                    context.Bot.LogNote($"deck brain: the attack we committed to ({commitment.Why}) is off: nothing it beats is left");
                    continue;
                }
                var target = enemy.FirstOrDefault(card => card.CurrentCoordinates.Sequence == commitment.TargetZone && (card.ID == commitment.TargetId || card.ID <= 0));
                if (target.CurrentCoordinates.Location == 0)
                {
                    _attackCommitments.Remove(commitment);
                    context.Bot.LogNote($"deck brain: the attack we committed to ({commitment.Why}) is off: its target is gone");
                    continue;
                }
                if (!first.IsCode(commitment.AttackerId))
                {
                    if (!all.Any(card => card.IsCode(commitment.AttackerId)))
                    {
                        _attackCommitments.Remove(commitment);
                        context.Bot.LogNote($"deck brain: the attack we committed to ({commitment.Why}) is off: the attacker can't attack");
                    }
                    continue; // another monster attacks first (zone order); the commitment waits for its turn
                }
                _attackCommitments.Remove(commitment);
                AimAt(context.Bot, target, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks {DescribeTarget(target)} as committed: {commitment.Why}");
                return HeldCommittedAttack(first);
            }
            if (!LethalPushNow)
                return null;
            // The lethal push: direct when the field is clear, else the first attacker takes the monster it beats (the most
            // threatening), or, with more attackers behind it, the weakest monster (out of the way, the rest follow).
            string why = _lethalPush!.Value.Why;
            // Still lethal on paper with what can attack now (the spend may have been held later, or something removed)?
            // Through their blockers, not just our total ATK (round-robin 44c: 2300 "reached" 1125 LP with Blizzard still in
            // the way, and the forced attack ran into it).
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (BoardAssessment.TurnsToWin(all.Select(EffectiveAttack).ToList(), BoardAssessment.Defenders(enemy, OpponentMemory), oppLp) != 1)
            {
                context.Bot.LogNote($"deck brain: the lethal push we committed to ({why}) is off: our attackers ({string.Join(", ", all.Select(card => EffectiveAttack(card)))}) " +
                                    $"no longer get through their {enemy.Count} monster(s) for their {oppLp} LP");
                _lethalPush = null;
                return null;
            }
            if (enemy.Count == 0 && first.CanDirectAttack)
            {
                // Into a field with no cards at all the first hit may bring Gorz and its token instead of the win (12:30 duel,
                // user 2026-11-13): push on when we can answer both, else the push is off (FearGorzAsync decides, as for any
                // direct attack).
                bool noCards = (await GetEnemySpellCardsAsync()).Count == 0;
                if (noCards && first.Attack < oppLp && await FearGorzAsync(context) && Rule(DeckRules.HoldForGorz))
                {
                    context.Bot.LogNote($"deck brain: the lethal push we committed to ({why}) is off: a Gorz we can't answer would stop it");
                    _lethalPush = null;
                    return null;
                }
                AimAt(context.Bot, null, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks directly: the lethal push we committed to ({why})");
                return HeldCommittedAttack(first);
            }
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            // The first attacker (zone order) takes the monster it beats, the most threatening. One it can't beat isn't
            // "cleared out of the way" by crashing into it: the push is off (user, 2026-11-13: after Gorz, Shura and Card Trooper
            // crashed into a 2500 token "to clear the way": "why are they all attacking tokens").
            var pick = enemy.Where(card => first.Attack > Power(card)).OrderByDescending(OpponentThreat).FirstOrDefault();
            if (pick.CurrentCoordinates.Location != 0)
            {
                AimAt(context.Bot, pick, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks {DescribeTarget(pick)}: the lethal push we committed to ({why})");
                return HeldCommittedAttack(first);
            }
            // It beats nothing: it still attacks to clear the way (the server attacks in zone order, so the monsters behind it can
            // only attack after it), but only when one behind it has a meaningful attack waiting (user, 2026-11-13: "clearing the
            // way is still something we may need to do ... there has to be a meaningful attack waiting for it to be worth
            // clearing the way"). The cheapest crash: into a defence-position monster (we only take the difference) before an
            // attack-position one (ours dies), smallest first, within CheapUnblockDamage and never lethal.
            var behind = all.Skip(1).Where(card => !card.Attacked).ToList();
            var waiting = behind.FirstOrDefault(card => HasGoodAttack(card, enemy, all.ToList()));
            int ourLp = await CurrentField.GetBotLifePoints();
            var crash = enemy.Where(card => card.CurrentCoordinates.Location != 0)
                .OrderBy(card => card.IsFaceup() && card.IsAttack() ? 1 : 0).ThenBy(Power)
                .FirstOrDefault(card => Power(card) - first.Attack <= Math.Min(CheapUnblockDamage, ourLp - 1));
            if (waiting.ID > 0 && crash.CurrentCoordinates.Location != 0 && Rule(DeckRules.UnblockAttackOrder))
            {
                AimAt(context.Bot, crash, AimKind.Committed, $"{CardNames.Get(first.ID)} attacks {DescribeTarget(crash)} to clear the way: it beats nothing, " +
                                                             $"but {CardNames.Get(waiting.ID)} behind it has a real attack (the lethal push: {why})");
                return HeldCommittedAttack(first);
            }
            context.Bot.LogNote($"deck brain: the lethal push we committed to ({why}) is off: {CardNames.Get(first.ID)} beats nothing of theirs " +
                                $"({string.Join(", ", enemy.Select(card => $"{DescribeTarget(card)} {Power(card)}"))})" +
                                (waiting.ID > 0 ? ", and clearing the way costs too much" : ", and nothing behind it has a meaningful attack to clear the way for"));
            _lethalPush = null;
            return null;
        }

        /// <summary>A committed attack: the search pays (KeepCommittedAttack) for lines that leave the Battle Phase instead.</summary>
        private BattleCmdResponse HeldCommittedAttack(DynamicCard attacker)
        {
            HoldBattleExit(DeckRules.KeepCommittedAttack);
            return attacker.DeclareAttack();
        }


        private static bool IsMainOrBattle(PlanStep step) => step.State is BotState.SelectIdleCmd or BotState.SelectBattleCmd;

        /// <summary>
        /// The search's line's next main / battle step when it's on offer here (taken: we committed to the line when the
        /// search replaced the brain's move), else null. A step not on offer drops the line, logged.
        /// </summary>
        private string? NextSearchLineKey(DuelBotWrapper bot, BotState state, IReadOnlyCollection<string> offered)
        {
            if (_searchLine is not { } line || line.Turn != _currentHeader.CurrentTurn || line.Next >= line.Steps.Count || _planAdoptedThisDecision)
                return null;
            var step = line.Steps[line.Next];
            if (step.State != state)
                return null;
            var key = RoutineStepKey(step, offered) ?? SearchActions.MatchKey(step.Key, offered.ToList()) ?? (offered.Contains(step.Key) ? step.Key : null);
            if (key == null)
            {
                DropPlan(bot, $"its next step ({step.Label}) isn't on offer");
                return null;
            }
            // A routine step's own bookkeeping (Vayu's planned synchro, the Normal Summon used...), run as it's taken.
            if (_stepActions.Remove(step, out var onTake))
                onTake();
            // The picks that come with this step (its target, cost...) are followed too, up to the line's next main / battle step.
            var picks = line.Steps.Skip(line.Next + 1).TakeWhile(s => !IsMainOrBattle(s)).ToList();
            _plan = picks.Count > 0 ? (line.Turn, picks, 0) : null;
            int nextMain = line.Next + 1 + picks.Count;
            _searchLine = nextMain < line.Steps.Count ? line with { Next = nextMain } : null;
            bot.LogNote($"plan ({_planInfo?.Source ?? "search"}): {step.Label}" +
                        (picks.Count > 0 ? $" ({string.Join(", ", picks.Select(p => p.Label))})" : ""));
            return key;
        }
    }
}
