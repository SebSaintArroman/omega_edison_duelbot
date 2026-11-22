using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Plaguespreader Zombie (moved from the Vayu brain; the Machina brain plays it too): from the graveyard it special
    /// summons itself (a hand card goes on top of the deck), a Level 2 Tuner for a synchro with one of our non-Tuners.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected const int PlaguespreaderZombieId = 33420078;

        /// <summary>The synchro Plaguespreader was brought out for this turn (summoned straight after it).</summary>
        protected int PlagueTargetSynchro;
        protected int PlagueTargetTurn = -1;

        private async Task<bool> CanRevivePlaguespreaderAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            int handAfterCost)
        {
            int opponentLife = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (opponentLife < 400)
                return true;

            var ours = await GetBotMonsterCardsAsync();
            var theirs = await GetEnemyMonsterCardsAsync();
            var defenders = BoardAssessment.Defenders(theirs, OpponentMemory);
            var attackers = ours.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack);
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase;
            if (battleAhead && BoardAssessment.TurnsToWin(attackers, defenders, opponentLife - 399, maxTurns: 1) == 1)
                return true;

            if (await PlagueSynchroPlanAsync(context, state, theirs, ours, handAfterCost) is not { } plan)
                return false;
            if (plan.Answer != null)
                return true;

            var afterSynchro = ours.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                !SameZone(card, plan.Material)).Select(EffectiveAttack).Append(plan.Synchro.Attack);
            return battleAhead && BoardAssessment.TurnsToWin(afterSynchro, defenders, opponentLife, maxTurns: 1) == 1;
        }

        /// <summary>
        /// Plaguespreader (Level 2 Tuner) + a non-Tuner on the field: the synchro of that Level + 2 from our actual Extra
        /// Deck. Prefer the material whose synchro answers a threat (Card Trooper L3 -> Catastor into a non-DARK boss),
        /// otherwise the biggest synchro. Judged by the spend rules (Plaguespreader is a one-shot that costs a hand card).
        /// </summary>
        protected async Task<IdleCmdResponse?> TryPlaguespreaderSynchroAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, string rule)
        {
            // Plaguespreader in hand with the Normal Summon: summoned and synchroed as one routine.
            if (await TryPlagueSummonSynchroAsync(context, state, enemyMonsters, botMonsters, rule) is IdleCmdResponse fromHand)
                return fromHand;
            if (!TryGetActivable(state, PlaguespreaderZombieId, out _))
                return null;
            int handAfterCost = (await GetBotHandCardsAsync()).Count - 1; // Plaguespreader puts a hand card on top of the deck
            if (await PlagueSynchroPlanAsync(context, state, enemyMonsters, botMonsters, handAfterCost) is not { } plan)
                return null;
            string what = $"Plaguespreader (one-shot) + {CardNames.Get(plan.Material.ID)} into {CardNames.Get(plan.Synchro.Id)}";
            if (await JustifySpendAsync(context, what, newAtk: plan.Synchro.Attack, fieldMaterials: new[] { plan.Material }, removal: false,
                    cardId: PlaguespreaderZombieId, extraDeck: true, effectAnswer: plan.Answer) is not string why || !Rule(rule))
                return null;
            if (await LylaClearsTheWayAsync(context, state, what) is IdleCmdResponse lylaFirst)
                return lylaFirst;
            // The revive and the synchro as one routine (user, 2026-11-13: no Plaguespreader brought back for a synchro that then
            // isn't made; seen: Plaguespreader revived, Cyber Dragon paid, and the Gottoms it was for held back). Checked and
            // scored on the engine, against every other move, before Plaguespreader leaves the graveyard.
            if (!await ContinueRoutineAsync(context, "Plaguespreader synchro", $"{what}: {why}",
                    new RoutineStep(MainAction.Activate, PlaguespreaderZombieId, "Plaguespreader from the graveyard"),
                    new[] { new RoutineStep(MainAction.SpSummon, plan.Synchro.Id, $"synchro summon {CardNames.Get(plan.Synchro.Id)}") }) ||
                !TryActivateStaple(context, state, PlaguespreaderZombieId, why, out var response))
                return null;
            PlagueTargetSynchro = plan.Synchro.Id;
            PlagueTargetTurn = context.Header.CurrentTurn;
            return response;
        }

        /// <summary>
        /// Plaguespreader Zombie Normal Summoned from the hand is for a synchro straight after (user, 2026-11-13: "if you're
        /// summoning plague you should synchro straight after - add that as a routine. if you just want to get plague into play
        /// from hand, it should be a set (in mp2)"): the summon and the synchro as one routine, picked and justified like the
        /// graveyard revive. Null when there's no synchro worth it (the generic summon rules don't summon it face-up either).
        /// </summary>
        private async Task<IdleCmdResponse?> TryPlagueSummonSynchroAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, string rule)
        {
            if (!(state.SummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(PlaguespreaderZombieId)))
                return null;
            int handAfterCost = (await GetBotHandCardsAsync()).Count - 1; // Plaguespreader itself leaves the hand
            if (await PlagueSynchroPlanAsync(context, state, enemyMonsters, botMonsters, handAfterCost) is not { } plan)
                return null;
            string what = $"Normal Summon Plaguespreader + {CardNames.Get(plan.Material.ID)} into {CardNames.Get(plan.Synchro.Id)}";
            if (await JustifySpendAsync(context, what, newAtk: plan.Synchro.Attack, fieldMaterials: new[] { plan.Material }, removal: false,
                    cardId: PlaguespreaderZombieId, extraDeck: true, effectAnswer: plan.Answer) is not string why || !Rule(rule))
                return null;
            var response = await StartRoutineAsync(context, "Plaguespreader summon synchro", $"{what}: {why}", new[]
            {
                new RoutineStep(MainAction.Summon, PlaguespreaderZombieId, "normal summon Plaguespreader Zombie"),
                new RoutineStep(MainAction.SpSummon, plan.Synchro.Id, $"synchro summon {CardNames.Get(plan.Synchro.Id)}"),
            });
            if (response == null)
                return null;
            PlagueTargetSynchro = plan.Synchro.Id;
            PlagueTargetTurn = context.Header.CurrentTurn;
            return response;
        }

        /// <summary>
        /// Before a synchro play: Lyla, Normal Summoned, destroys one of their spells/traps first, clearing the way like MST
        /// (user, 2026-11-13: "should've used lyla before summoning plague and going for a synchro. lyla, like MST, would 'clear
        /// the way' for the play"). Summon and effect as one routine; the synchro is re-planned after (Lyla may be its material).
        /// </summary>
        private async Task<IdleCmdResponse?> LylaClearsTheWayAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, string play)
        {
            var lyla = (state.SummonableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(LylaId));
            if (lyla.ID <= 0 || (await GetEnemySpellCardsAsync()).Count == 0 || await DestructionNegatedAsync(context, LylaId) != null ||
                !Rule(DeckRules.LylaClearsTheWay))
                return null;
            return await StartRoutineAsync(context, "Lyla clears the way", $"before {play}: Lyla destroys one of their spells/traps first, like MST", new[]
            {
                new RoutineStep(MainAction.Summon, LylaId, "normal summon Lyla, Lightsworn Sorceress"),
                new RoutineStep(MainAction.Activate, LylaId, "Lyla's effect: destroy one of their spells/traps"),
            });
        }

        /// <summary>The synchro Plaguespreader (Level 2 Tuner) makes with one of our face-up non-Tuners: lethal first, then
        /// one that answers a threat, then the biggest. Null when there's none.</summary>
        private async Task<(DynamicCard Material, SynchroOption Synchro, string? Answer)?> PlagueSynchroPlanAsync(DecisionContext<IdleCmdData> context,
            IdleCmdData state, List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, int handAfterCost)
        {
            var ourAtk = botMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList();
            (DynamicCard Material, SynchroOption Synchro, string? Answer)? best = null;
            // A synchro that wins this turn beats any other plan (Brionac bouncing their last blocker).
            bool canAttack = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            foreach (var material in botMonsters.Where(card => card.IsFaceup() && card.Level is >= 1 and <= 10 && !card.HasType(CardType.Tuner)))
            {
                int others = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, material))
                    .Sum(EffectiveAttack);
                if (await LethalSynchroForLevelAsync(material.Level + 2, enemyMonsters, others, handAfterCost, oppLp, canAttack) is { } lethal)
                {
                    best = (material, lethal.Synchro, lethal.Answer);
                    break;
                }
            }
            foreach (var material in botMonsters.Where(card => best?.Answer?.StartsWith("lethal") != true &&
                                                               card.IsFaceup() && card.Level is >= 1 and <= 10 && !card.HasType(CardType.Tuner)))
            {
                var option = await BestSynchroForLevelAsync(material.Level + 2, enemyMonsters, ourAtk);
                if (option == null)
                    continue;
                var (synchro, answer) = option.Value;
                bool better = best == null ||
                              (answer != null && best.Value.Answer == null) ||
                              ((answer != null) == (best.Value.Answer != null) && synchro.Attack > best.Value.Synchro.Attack);
                if (better)
                    best = (material, synchro, answer);
            }
            return best;
        }

        /// <summary>
        /// A Tuner of ours already on the field (Plaguespreader brought out for another reason, a summoned Tuner) plus a
        /// non-Tuner of ours: the synchro of their Levels, the best one on offer (an answer first, else the biggest; lethal
        /// first of all), when it's worth the materials. A Plaguespreader on the field is a one-shot body (banished when it
        /// leaves): its synchro costs nearly nothing. Seen 2026-11-10: Lyla (effect used) + Plaguespreader on the field and
        /// no Brionac (Level 6), because only Plaguespreader from the graveyard was looked at.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryFieldTunerSynchroAsync(DecisionContext<IdleCmdData> context, IdleCmdData state,
            List<DynamicCard> enemyMonsters, List<DynamicCard> botMonsters, string rule)
        {
            var tuners = botMonsters.Where(card => card.IsFaceup() && card.HasType(CardType.Tuner) && !card.HasType(CardType.Synchro) && card.Level > 0).ToList();
            var others = botMonsters.Where(card => card.IsFaceup() && !card.HasType(CardType.Tuner) && card.Level > 0 && !card.HasType(CardType.Xyz)).ToList();
            if (tuners.Count == 0 || others.Count == 0 || (state.SpecialSummonableCards?.Length ?? 0) == 0)
                return null;
            var ourAtk = botMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).ToList();
            bool canAttack = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && state.CanBattlePhase && context.Header.CurrentTurn > 1;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            int hand = (await GetBotHandCardsAsync()).Count;
            (DynamicCard Tuner, DynamicCard Material, SynchroOption Synchro, string? Answer)? best = null;
            foreach (var tuner in tuners)
                foreach (var material in others)
                {
                    int level = tuner.Level + material.Level;
                    int rest = botMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, tuner) && !SameZone(card, material))
                        .Sum(EffectiveAttack);
                    SynchroOption? synchro = null;
                    string? answer = null;
                    if (await LethalSynchroForLevelAsync(level, enemyMonsters, rest, hand, oppLp, canAttack) is { } lethal)
                        (synchro, answer) = (lethal.Synchro, lethal.Answer);
                    else if (await BestSynchroForLevelAsync(level, enemyMonsters, ourAtk) is { } option)
                        (synchro, answer) = (option.Synchro, option.Answer);
                    if (synchro is not { } s || !TryGetSpecialSummon(state, s.Id, out _))
                        continue;
                    bool better = best == null || answer?.StartsWith("lethal") == true && best.Value.Answer?.StartsWith("lethal") != true ||
                                  (answer != null) && best.Value.Answer == null ||
                                  (answer != null) == (best.Value.Answer != null) && s.Attack > best.Value.Synchro.Attack;
                    if (better)
                        best = (tuner, material, s, answer);
                }
            if (best is not { } plan)
                return null;
            bool freeTuner = plan.Tuner.IsCode(PlaguespreaderZombieId);
            string what = $"{CardNames.Get(plan.Tuner.ID)} + {CardNames.Get(plan.Material.ID)} into {CardNames.Get(plan.Synchro.Id)}";
            // A Plaguespreader already on the field only pays off as synchro material, and the synchro turns two monsters into one:
            // no spend hold applies (user, 2026-11-13: "you should've synchro'd with it when you summoned it, and every turn since").
            string? why = freeTuner
                ? "Plaguespreader on the field only pays off as synchro material (a one-shot body, banished when it leaves), and the synchro adds no monster"
                : await JustifySpendAsync(context, what, newAtk: plan.Synchro.Attack, fieldMaterials: new[] { plan.Tuner, plan.Material }, removal: false,
                    cardId: plan.Synchro.Id, extraDeck: true, effectAnswer: plan.Answer);
            if (why == null || !TryGetSpecialSummon(state, plan.Synchro.Id, out var summon) || !Rule(rule))
                return null;
            if (await LylaClearsTheWayAsync(context, state, what) is IdleCmdResponse lylaFirst)
                return lylaFirst;
            StapleNote(context, $"special summon {what}: {why}");
            return summon.SpecialSummon();
        }

        /// <summary>Right after Plaguespreader: summon the synchro it was brought out for.</summary>
        protected IdleCmdResponse? TryPlagueTargetSynchro(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (PlagueTargetSynchro <= 0 || PlagueTargetTurn != context.Header.CurrentTurn ||
                !TryGetSpecialSummon(state, PlagueTargetSynchro, out var synchro))
                return null;
            StapleNote(context, $"special summon {CardNames.Get(PlagueTargetSynchro)}: the synchro Plaguespreader was summoned for");
            PlagueTargetSynchro = 0;
            return synchro.SpecialSummon();
        }
    }
}
