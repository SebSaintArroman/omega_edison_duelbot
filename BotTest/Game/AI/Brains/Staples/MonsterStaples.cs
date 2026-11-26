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
    /// Staple spells that deal with a monster, shared by every deck (user, 2026-11-13: "move this to generic brain, it's a
    /// staple"; round-robin 36, 47). Deck brains call <see cref="TryMonsterStaplesAsync"/> after their own rules for these
    /// cards (Hero Frog Monarchs keeps its Monarch-tribute uses of Brain Control first).
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        // "When this card is destroyed" (any way, not only by battle): Card Trooper's draw. Destroying it by card effect
        // gives them its payoff (user, 2026-11-13: "just don't use it on card trooper please lol").
        private static readonly System.Text.RegularExpressions.Regex DestroyedAnyWayPayoffText = new(
            @"(when|if) this card is destroyed(,|\s+and sent)(?![^.]*by battle)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Smashing Ground, then Brain Control. Null when neither is worth it now.</summary>
        protected async Task<IdleCmdResponse?> TryMonsterStaplesAsync(DecisionContext<IdleCmdData> context)
        {
            if (await TrySmashingGroundAsync(context) is IdleCmdResponse smash)
                return smash;
            if (await TryBrainControlAsync(context) is IdleCmdResponse brain)
                return brain;
            return null;
        }

        /// <summary>
        /// Smashing Ground destroys their face-up monster with the highest DEF. Used when that monster is a big threat (IsBigThreatAsync),
        /// or Dupe Frog, whose lock it breaks (user, 2026-11-13). Never on a monster that pays off whenever it's destroyed (Card
        /// Trooper's draw).
        /// </summary>
        private async Task<IdleCmdResponse?> TrySmashingGroundAsync(DecisionContext<IdleCmdData> context)
        {
            if (!TryGetActivable(context.StateData, SmashingGround, out var smash))
                return null;
            var (use, why) = await SmashingGroundVerdictAsync(context);
            if (!use)
            {
                if (why != null)
                {
                    if (!IsSearchTwin)
                        context.Bot.LogNote($"deck brain: hold Smashing Ground: {why}");
                    Hold(SmashingGround, MainAction.Activate, DeckRules.SmashingGround);
                }
                return null;
            }
            if (!Rule(DeckRules.SmashingGround))
                return null;
            context.Bot.LogNote($"deck brain: activate Smashing Ground on {why}");
            return smash.MainActivateFromIndex();
        }

        /// <summary>
        /// Whether Smashing Ground is worth it now, and why (null reason: no face-up monster of theirs). Not worth it on a monster that
        /// pays off whenever it's destroyed, nor on one that isn't a big threat (user, 2026-11-26: "waste of smashing ground - it should
        /// only be used when I have a threat").
        /// </summary>
        private async Task<(bool Use, string? Why)> SmashingGroundVerdictAsync(DecisionContext<IdleCmdData> context)
        {
            var faceUp = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.ID > 0).ToList();
            if (faceUp.Count == 0)
                return (false, null);
            int topDef = faceUp.Max(card => card.Defense);
            var hit = faceUp.Where(card => card.Defense == topDef).OrderByDescending(OpponentThreat).First();
            if (await CardDatabase.GetAsync(hit.ID) is DBCardInfo info && DestroyedAnyWayPayoffText.IsMatch(info.CardText ?? ""))
                return (false, $"their highest DEF is {CardNames.Get(hit.ID)}, which pays off whenever it's destroyed");
            bool locks = await TextMatchesAsync(hit.ID, AttackLockText);
            // A threat by the bot's usual test (IsBigThreatAsync: the Threat tag, ATK over our bar, Extra Deck, a bouncer, beats a monster of
            // ours worth keeping, a strong effect), not "we can't attack over it": with no monster of ours, that's every monster (user,
            // 2026-11-26). A lock (Dupe Frog) still counts: it forces our attacks onto it (user, 2026-11-13).
            if (!locks && !await IsBigThreatAsync(hit))
                return (false, $"no threat: their highest DEF, {CardNames.Get(hit.ID)} ({hit.GetRealPower()}), isn't a big threat");
            return (true, $"{CardNames.Get(hit.ID)} ({hit.GetRealPower()}, highest DEF {topDef}): " +
                          (locks ? "its lock forces our attacks onto it" : "a big threat"));
        }

        /// <summary>
        /// Smashing Ground's hold whatever the brain chose (its flow can pick a play before it reaches the staples; seen 2026-11-26: the
        /// brain set Ryko, the search fired Smashing Ground on Versago with no hold). Not when its flow already recorded it.
        /// </summary>
        private async Task HoldSmashingGroundAsync(DecisionContext<IdleCmdData> context)
        {
            if (!TryGetActivable(context.StateData, SmashingGround, out _) || _holds.Any(h => h.Rule == DeckRules.SmashingGround))
                return;
            if (await SmashingGroundVerdictAsync(context) is (false, string why))
            {
                if (!IsSearchTwin)
                    context.Bot.LogNote($"deck brain: hold Smashing Ground: {why}");
                Hold(SmashingGround, MainAction.Activate, DeckRules.SmashingGround);
            }
        }

        /// <summary>
        /// Brain Control (pay 800 LP: take a face-up monster of theirs that can be Normal Summoned until the End Phase).
        /// Worth the card when the monster then (round-robin 36):
        ///  1. makes lethal with our attackers;
        ///  2. becomes tribute for a Level 5-6 in hand, or synchro material with our face-up tuner (they lose it for good);
        ///  3. destroys another of their monsters in battle that none of ours beats.
        /// Main Phase 1 with a Battle Phase ahead (2 also works later in the turn). Not for a monster that's merely in the way:
        /// it comes back at the End Phase.
        /// </summary>
        private async Task<IdleCmdResponse?> TryBrainControlAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            if (!TryGetActivable(state, BrainControl, out var brainControl))
                return null;
            int lp = await CurrentField.GetBotLifePoints();
            if (lp <= 800)
                return null;
            var enemy = await GetEnemyMonsterCardsAsync();
            var takeable = enemy.Where(card => card.IsFaceup() && card.ID > 0 &&
                                               !(card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) || card.HasType(CardType.Xyz) ||
                                                 card.HasType(CardType.Token) || card.HasType(CardType.Ritual)))
                .OrderByDescending(OpponentThreat).ToList();
            if (takeable.Count == 0)
                return null;
            int turn = context.Header.CurrentTurn;
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && turn > 1;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0).ToList();
            var ourAtks = ours.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack).ToList();
            var tributeTarget = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => card.Level is 5 or 6)
                .OrderByDescending(card => card.Attack).FirstOrDefault();
            var tuner = ours.FirstOrDefault(card => card.IsFaceup() && card.HasType(CardType.Tuner));

            foreach (var take in takeable)
            {
                var rest = enemy.Where(card => !SameZone(card, take)).ToList();
                var defendersRest = BoardAssessment.Defenders(rest, OpponentMemory);
                bool canAttack = take.IsAttack();
                // 1. Lethal.
                if (battleAhead && canAttack && BoardAssessment.TurnsToWin(ourAtks.Append(take.Attack), defendersRest, oppLp) == 1 &&
                    BoardAssessment.TurnsToWin(ourAtks, BoardAssessment.Defenders(enemy, OpponentMemory), oppLp) > 1 && Rule(DeckRules.BrainControl))
                {
                    PlanTarget(turn, take, theirs: true);
                    CommitLethalPush(turn, $"Brain Control takes {CardNames.Get(take.ID)}: lethal with our attackers");
                    context.Bot.LogNote($"deck brain: activate Brain Control on {CardNames.Get(take.ID)} ({take.Attack}): with our attackers it's lethal ({oppLp} LP)");
                    return brainControl.MainActivateFromIndex();
                }
                // 2. Tribute for a Level 5-6 in hand (they lose it for good).
                if (tributeTarget.ID > 0 && Rule(DeckRules.BrainControl))
                {
                    PlanTarget(turn, take, theirs: true);
                    string goal = $"take {CardNames.Get(take.ID)} and tribute it for {CardNames.Get(tributeTarget.ID)}";
                    if (await ContinueRoutineAsync(context, "Brain Control tribute", goal,
                            new RoutineStep(MainAction.Activate, BrainControl, "Brain Control"),
                            new[] { new RoutineStep(MainAction.Summon, tributeTarget.ID, $"tribute summon {CardNames.Get(tributeTarget.ID)}") }))
                    {
                        context.Bot.LogNote($"deck brain: activate Brain Control: {goal} (their monster is gone for good)");
                        return brainControl.MainActivateFromIndex();
                    }
                }
                // 2b. Synchro material with our face-up tuner.
                if (tuner.ID > 0 && !take.HasType(CardType.Tuner) && await BestSynchroForLevelAsync(tuner.Level + take.Level, rest, ourAtks) is { } synchro &&
                    Rule(DeckRules.BrainControl))
                {
                    PlanTarget(turn, take, theirs: true);
                    string goal = $"take {CardNames.Get(take.ID)} and synchro it with {CardNames.Get(tuner.ID)} into {CardNames.Get(synchro.Synchro.Id)}";
                    if (await ContinueRoutineAsync(context, "Brain Control synchro", goal,
                            new RoutineStep(MainAction.Activate, BrainControl, "Brain Control"),
                            new[] { new RoutineStep(MainAction.SpSummon, synchro.Synchro.Id, $"synchro summon {CardNames.Get(synchro.Synchro.Id)}") }))
                    {
                        context.Bot.LogNote($"deck brain: activate Brain Control: {goal} (their monster is gone for good)");
                        return brainControl.MainActivateFromIndex();
                    }
                }
                // 3. A kill: it beats another of theirs that none of ours beats.
                if (battleAhead && canAttack && lp > 800 + DangerLifePoints)
                {
                    var victim = rest.Where(card => card.IsFaceup() && card.GetRealPower() < take.Attack && !ourAtks.Any(atk => atk > card.GetRealPower()))
                        .OrderByDescending(OpponentThreat).FirstOrDefault();
                    if (victim.ID > 0 && Rule(DeckRules.BrainControl))
                    {
                        PlanTarget(turn, take, theirs: true);
                        CommitAttack(turn, take.ID, victim, $"Brain Control took {CardNames.Get(take.ID)} to destroy {CardNames.Get(victim.ID)}");
                        context.Bot.LogNote($"deck brain: activate Brain Control on {CardNames.Get(take.ID)} ({take.Attack}): it destroys their " +
                                            $"{CardNames.Get(victim.ID)} ({victim.GetRealPower()}) in battle, which none of ours beats");
                        return brainControl.MainActivateFromIndex();
                    }
                }
            }
            Hold(BrainControl, MainAction.Activate, DeckRules.BrainControl);
            return null;
        }
    }
}
