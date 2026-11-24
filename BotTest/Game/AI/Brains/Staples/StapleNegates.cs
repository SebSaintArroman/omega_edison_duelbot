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
    /// Shared rules for two staple traps:
    ///  - Solemn Judgment (pay half our LP): only to push our big play through (usually lethal) or to stop a card that
    ///    swings the game: a major threat being summoned, or a big Heavy Storm / Torrential Tribute / Mirror Force.
    ///  - Compulsory Evacuation Device (return a monster to the hand): removal against Extra Deck monsters and tokens
    ///    (they don't come back) and any attacker whose attack costs us, at the last moment: when it attacks. Deck brains can also bounce their own monsters with it
    ///    (Machina: Gearframe, to search again).
    /// Planned targets (Compulsory's) are picked in StapleTargets.
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        // A target chosen when deciding to use a card: (turn, controller, location, zone, id).
        private (int Turn, int Controller, int Location, int Sequence, int Id)? _plannedTarget;

        /// <summary>Remembers the target for the card we're about to use (picked in StapleTargets).</summary>
        /// <param name="theirs">Whose card it is, when known (cards read from the opponent's side can carry controller 0).</param>
        protected void PlanTarget(int turn, DynamicCard target, bool? theirs = null) =>
            _plannedTarget = (turn, theirs is bool t ? (t ? 1 : 0) : target.CurrentCoordinates.Controller, target.CurrentCoordinates.Location, target.CurrentCoordinates.Sequence, target.ID);

        private IList<byte>? PlannedTarget(DecisionContext<SelectCardData> context)
        {
            if (_plannedTarget is not { } plan || plan.Turn != context.Header.CurrentTurn)
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int index = Array.FindIndex(cards, card => card.CurrentCoordinates.Location == plan.Location &&
                card.CurrentCoordinates.Sequence == plan.Sequence && card.CurrentCoordinates.Controller == plan.Controller &&
                (plan.Id <= 0 || card.ID <= 0 || card.IsCode(plan.Id)));
            if (index < 0)
                index = Array.FindIndex(cards, card => card.CurrentCoordinates.Location == plan.Location &&
                    card.CurrentCoordinates.Sequence == plan.Sequence && plan.Id > 0 && card.IsCode(plan.Id));
            if (index < 0)
                return null;
            _plannedTarget = null;
            StapleNote(context, $"target {DescribePick(cards[index])} (as planned)");
            return new List<byte> { (byte)index };
        }

        /// <summary>
        /// Another way we have of dealing with this opponent monster, so Solemn Judgment isn't needed: a set trap that
        /// answers it (Bottomless on its summon, D-Prison / Mirror Force when it attacks, Compulsory on an Extra Deck
        /// monster or token). Deck brains add their own (Machina: a Fortress crashing into it, Brionac). Null if none.
        /// </summary>
        protected virtual async Task<string?> OtherAnswerToAsync(DynamicCard threat)
        {
            var ourSet = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0).ToList();
            if (threat.Attack >= 1500 && ourSet.Any(card => card.IsCode(BottomlessTrapHole)))
                return "our set Bottomless Trap Hole takes it once it's summoned";
            if (ourSet.FirstOrDefault(card => card.IsCode(DimensionalPrison, MirrorForce)) is { ID: > 0 } trap)
                return $"our set {CardNames.Get(trap.ID)} answers it when it attacks";
            if (BounceRemoves(threat) && ourSet.Any(card => card.IsCode(CompulsoryEvacuationDevice)))
                return "our set Compulsory Evacuation Device sends it back to the Extra Deck";
            return null;
        }

        /// <summary>
        /// Whether we have Solemn Judgment set and can afford it (LP at least SolemnMinLifePoints): it stops any one
        /// problem, so pushes can be more reckless.
        /// </summary>
        protected async Task<bool> SolemnReadyAsync()
        {
            if (!(await CurrentField.GetCardsInSpellZone()).Any(card => card.IsCode(SolemnJudgment)))
                return false;
            int lp = await CurrentField.GetBotLifePoints();
            return lp >= SolemnMinLifePoints || lp <= await TheirAttackThroughAsync();
        }

        /// <summary>
        /// The ATK of their face-up monsters that gets past ours (each of our monsters blocks one attacker, the biggest
        /// first). When our whole LP is already within this, Solemn Judgment's half-LP cost changes nothing about how easily
        /// we die to their board, so it's ignored (user, 2026-11-13).
        /// </summary>
        protected async Task<int> TheirAttackThroughAsync()
        {
            int blockers = (await GetBotMonsterCardsAsync()).Count(m => m.ID > 0);
            return (await GetEnemyMonsterCardsAsync()).Where(m => m.IsFaceup()).Select(m => m.Attack)
                .OrderByDescending(a => a).Skip(blockers).Sum();
        }

        /// <summary>Push chance multiplier with Solemn Judgment set. Tune from logs.</summary>
        [Tunable("Push chance multiplier with Solemn Judgment set. Tune from logs.")]
        public double SolemnPushBoost { get; set; } = 2;

        /// <summary>Below this many LP, paying half for Solemn Judgment is too risky to count on it. Tune from logs.</summary>
        [Tunable("Below this many LP, paying half for Solemn Judgment is too risky to count on it. Tune from logs.")]
        public int SolemnMinLifePoints { get; set; } = 2000;

        /// <summary>ATK from which a summoned big threat is "major" enough for Solemn Judgment. Tune from logs.</summary>
        [Tunable("ATK from which a summoned big threat is \"major\" enough for Solemn Judgment. Tune from logs.")]
        public int SolemnMajorThreatAtk { get; set; } = 2800;

        protected static bool IsExtraDeckMonster(DynamicCard card) =>
            card.HasType(CardType.Synchro) || card.HasType(CardType.Fusion) || card.HasType(CardType.Xyz) || card.HasType(CardType.Link);

        // ------------------------------------------------------------------ Solemn Judgment

        // A summon effect that takes a card off the field: Caius (banish), Raiza (top of the deck), ... (round-robin 17).
        private static readonly System.Text.RegularExpressions.Regex SummonRemovesCardText = new(
            @"(destroy|banish|return|place|send|shuffle)[^.]{0,80}\bcards?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Solemn Judgment: half our LP, so only on what we absolutely have no other way of dealing with (usually a big
        /// Heavy Storm / Torrential / Mirror Force):
        ///  - pushing our play through: their response on our turn while our board is lethal this turn;
        ///  - a big Heavy Storm (3+ of our spells/traps), Mirror Force taking 2+ of our attackers (or stopping lethal),
        ///    Torrential Tribute that costs us much more than them;
        ///  - a major threat being summoned (a big threat at SolemnMajorThreatAtk+ that our board doesn't beat) that we have
        ///    no other answer to (see OtherAnswerToAsync).
        /// Never when the LP left would be within reach of their board (unless it wins now).
        /// </summary>
        protected async Task<int?> TryChainSolemnAsync(DecisionContext<SelectChainData> context, DynamicCard[] options, bool botTurn)
        {
            int solemn = Array.FindIndex(options, card => card.IsCode(SolemnJudgment));
            if (solemn < 0)
                return null;
            int ourLp = await CurrentField.GetBotLifePoints();
            int after = ourLp - ourLp / 2;
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var ourMonsters = await GetBotMonsterCardsAsync();
            // What their board gets through our monsters (each blocks one attacker, the biggest first): a Solemn that keeps our
            // monster keeps that block (the 2026-11-13 Fortress: 900 left behind it was safe; 3100 into an empty field wasn't).
            int blockers = ourMonsters.Count(m => m.ID > 0);
            int theirThrough = await TheirAttackThroughAsync();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool lethalNow = botTurn && BoardAssessment.TurnsToWin(ourMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack).ToList(), BoardAssessment.Defenders(enemyMonsters, OpponentMemory), oppLp) == 1 &&
                (DuelPhase)context.Header.CurrentPhase is DuelPhase.Main1 or DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage;
            // User, 2026-11-13: the LP cost is ignored when our whole LP is already in reach of their board (halving it makes
            // their board no more lethal: Solemn is often less risky at low LP), and when we're about to win.
            bool lpCostFree = ourLp <= theirThrough || lethalNow;
            int lpCost = lpCostFree ? 0 : ourLp / 2;
            string freeWhy = lethalNow ? "we win this turn" : $"our {ourLp} LP is already within reach of their board ({theirThrough} ATK through)";
            string? why = null;

            // Their spell/trap being activated.
            if (context.Header.LastChainPlayer == 1)
            {
                var activated = (await GetEnemySpellCardsAsync())
                    .Where(card => card.IsFaceup() && card.ID > 0 && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence)).ToList();
                var ourSpells = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID != 0).ToList();
                foreach (var card in activated)
                {
                    if (lethalNow)
                        why = $"push our lethal play through their {CardNames.Get(card.ID)}";
                    else if (card.IsCode(HeavyStorm) && ourSpells.Count >= 3)
                        why = $"a big Heavy Storm: it would take {ourSpells.Count} of our spells/traps";
                    else if (card.IsCode(MirrorForce) &&
                             ourMonsters.Count(m => m.IsFaceup() && m.IsAttack() && !IsExpendable(m) && !IsSpentFlipMonster(m)) >= 2)
                        why = "a big Mirror Force: it would take 2+ of our attackers";
                    else if (card.IsCode(TorrentialTribute))
                    {
                        var ours = ourMonsters.Where(m => !IsSpentFlipMonster(m)).ToList();
                        var (worthForThem, _) = TorrentialWorthIt(ours, enemyMonsters);
                        if (worthForThem && ours.Count >= enemyMonsters.Count + 2)
                            why = $"a big Torrential Tribute: it would take {ours.Count} of ours against {enemyMonsters.Count} of theirs";
                    }
                    // Removal that would cost the deck something it can't get back (Machina: a Fortress banished),
                    // with no Compulsory to save it: a probabilistic call (the deck brain sets the chance).
                    if (why == null && !options.Any(o => o.IsCode(CompulsoryEvacuationDevice)))
                    {
                        bool banishes = await RemovalBanishesAsync(card);
                        foreach (var hit in await RemovalHitsAsync(card))
                        {
                            // Losing it loses the game: their face-up attackers get through what's left of our board for lethal
                            // (seen 2026-11-13: Bottomless on our only Fortress at 1800 LP, "protect chance 70%, rolled 1.00",
                            // and their 1700 + 1400 attacked into an empty field; user: "you lose if you don't").
                            var remaining = ourMonsters.Where(m => m.ID > 0 && !(m.CurrentCoordinates.Sequence == hit.CurrentCoordinates.Sequence &&
                                                                               m.CurrentCoordinates.Location == hit.CurrentCoordinates.Location)).ToList();
                            var theirAttacks = enemyMonsters.Where(m => m.IsFaceup()).Select(m => m.Attack).OrderByDescending(a => a).ToList();
                            int through = theirAttacks.Skip(remaining.Count).Sum();
                            if (through >= ourLp && hit.ID > 0)
                            {
                                why = $"protect our {CardNames.Get(hit.ID)} from their {CardNames.Get(card.ID)}: without it their board ({theirAttacks.Sum()} ATK) " +
                                      $"is lethal next turn ({through} through, our LP {ourLp})";
                                break;
                            }
                            double chance = SolemnToProtectChance(hit, banishes);
                            if (chance <= 0)
                                continue;
                            double roll = RollAt;
                            if (roll < chance)
                            {
                                why = $"protect our {CardNames.Get(hit.ID)} from their {CardNames.Get(card.ID)} ({(banishes ? "banished" : "destroyed")}: " +
                                      $"chance {chance:P0}, rolled {roll:0.00})";
                                break;
                            }
                            StapleNote(context, $"keep Solemn Judgment: their {CardNames.Get(card.ID)} hits our {CardNames.Get(hit.ID)}, " +
                                                $"protect chance {chance:P0}, rolled {roll:0.00}");
                        }
                    }
                    if (why != null)
                        break;
                }
            }

            // A major threat being summoned that we have absolutely no other way of dealing with.
            if (why == null && !botTurn && !LatestSummonAnswered)
            {
                var arriving = enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0 && IsLatestArrival(card)).ToList();
                int ourBest = ourMonsters.Where(card => card.IsFaceup()).Select(EffectiveAttack).DefaultIfEmpty(0).Max();
                foreach (var card in arriving)
                {
                    // Major: a big threat at SolemnMajorThreatAtk+ that nothing of ours already beats.
                    bool major = card.Attack >= SolemnMajorThreatAtk && card.Attack >= ourBest && await IsBigThreatAsync(card);
                    if (!major)
                        continue;
                    if (await OtherAnswerToAsync(card) is string other)
                    {
                        StapleNote(context, $"keep Solemn Judgment for {CardNames.Get(card.ID)}: we have another way of dealing with it ({other})");
                        continue;
                    }
                    why = $"stop a major threat we have no other answer to: {CardNames.Get(card.ID)} ({card.Attack})";
                    break;
                }
            }

            // Round-robin 17 (user, 2026-11-13: "depends how strong a monster you have, and if it's worth saving, but yes maybe"):
            // a summon whose own effect removes a card of ours (Caius, Raiza, Thestalos...). Worth it when what it would take is
            // worth saving (a strong monster of ours, or this Solemn itself: then the Solemn is lost either way and counts free),
            // weighed in cards: their monster (+1; a tribute summon +0.5 more, they paid for it) and our card saved, against the
            // Solemn and half our LP.
            if (why == null && !botTurn && !LatestSummonAnswered)
            {
                foreach (var card in enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0 && IsLatestArrival(card)))
                {
                    if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info || !OnSummonTriggerText.IsMatch(info.CardText ?? "") ||
                        !SummonRemovesCardText.IsMatch(info.CardText ?? ""))
                        continue;
                    var best = ourMonsters.Where(m => m.IsFaceup() && m.ID > 0 && !IsExpendable(m) && EffectiveAttack(m) >= StrongMonsterAttack)
                        .OrderByDescending(EffectiveAttack).FirstOrDefault();
                    bool spellsToo = System.Text.RegularExpressions.Regex.IsMatch(info.CardText ?? "", @"\bcard\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    bool solemnTarget = spellsToo && best.ID <= 0; // with nothing better of ours there, the set Solemn is the likely target
                    if (best.ID <= 0 && !solemnTarget)
                        continue;
                    bool tribute = info.Level >= 5 || System.Text.RegularExpressions.Regex.IsMatch(info.CardText ?? "", @"Tribute Summoned", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    double gain = SolemnSummonGain + (tribute ? SolemnTributeBonus : 0) + (best.ID > 0 ? SolemnSavedCardGain : 0);
                    double cost = (solemnTarget ? 0 : 1) + lpCost / (double)LifePointsPerCard;
                    string weigh = $"{CardNames.Get(card.ID)}'s summon effect would take " +
                                   (best.ID > 0 ? $"our {CardNames.Get(best.ID)}" : "this Solemn (lost either way)") +
                                   $": gain {gain:0.#} card(s) (their monster{(tribute ? ", a tribute summon" : "")}{(best.ID > 0 ? ", ours saved" : "")}) " +
                                   $"vs cost {cost:0.#} (Solemn{(solemnTarget ? " free" : "")} + " +
                                   (lpCostFree ? $"no LP cost: {freeWhy})"
                                               : $"{ourLp / 2} LP)");
                    if (gain - cost >= SolemnMinNetGain)
                    {
                        why = $"negate the summon: {weigh}";
                        break;
                    }
                    StapleNote(context, $"keep Solemn Judgment: {weigh}");
                }
            }

            if (why == null)
                return null;
            if (!lethalNow && !lpCostFree && after <= theirThrough)
            {
                StapleNote(context, $"don't use Solemn Judgment ({why}): paying half our LP leaves {after}, within reach of their board ({theirThrough} ATK past our {blockers} monster(s))");
                return null;
            }
            if (!Rule(DeckRules.SolemnJudgment))
                return null;
            if (why.StartsWith("stop a major threat"))
                MarkLatestSummonAnswered();
            StapleNote(context, $"chain Solemn Judgment (pay {ourLp / 2} LP" +
                (lpCostFree ? $", no real cost: {freeWhy}" : "") + $"): {why}");
            return solemn;
        }

        // ------------------------------------------------------------------ Compulsory Evacuation Device

        // (Compulsory on their monsters waits for the attack, the last moment: see TryChainResponsesAsync. Activating it on
        // the summon gains nothing and they might bring out something better first.)

        /// <summary>
        /// Compulsory Evacuation Device on our OWN monster in response to the opponent's removal (Mirror Force, Torrential
        /// Tribute, or a card that targets it): our best monster that would be destroyed goes back to the hand instead.
        /// Only a real one (a main-deck monster of CompulsorySaveMinAtk+ that isn't expendable; an Extra Deck monster would
        /// just go back to the Extra Deck).
        /// </summary>
        protected async Task<int?> TryChainCompulsorySaveAsync(DecisionContext<SelectChainData> context, DynamicCard[] options)
        {
            int compulsory = Array.FindIndex(options, card => card.IsCode(CompulsoryEvacuationDevice));
            if (compulsory < 0 || context.Header.LastChainPlayer != 1)
                return null;
            var activated = (await GetEnemySpellCardsAsync())
                .Where(card => card.IsFaceup() && card.ID > 0 && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence)).ToList();
            foreach (var card in activated)
            {
                var hit = await RemovalHitsAsync(card);
                // Banishing removal (D-Prison, Bottomless) also takes away any way back from the graveyard.
                bool banishes = await RemovalBanishesAsync(card);
                var save = hit.Where(m => WorthSavingToHand(m, banishes)).OrderByDescending(TargetThreat).FirstOrDefault();
                if (save.ID <= 0 || !Rule(DeckRules.CompulsorySavesOwn))
                    continue;
                PlanTarget(context.Header.CurrentTurn, save, theirs: false);
                StapleNote(context, $"chain Compulsory Evacuation Device on our own {CardNames.Get(save.ID)}: their {CardNames.Get(card.ID)} would {(banishes ? "banish" : "destroy")} it, " +
                                    "back to the hand instead");
                return compulsory;
            }
            return null;
        }

        /// <summary>
        /// Whether our monster is worth returning to the hand to save it (Compulsory Evacuation Device): by default a
        /// main-deck monster of CompulsorySaveMinAtk+ that isn't expendable. <paramref name="removalBanishes"/>: the removal
        /// banishes (no way back from the graveyard). Deck brains refine it (Machina: Gearframe searches again when
        /// re-summoned; Fortress needs another discard to come back, so only when that beats losing it).
        /// </summary>
        protected virtual bool WorthSavingToHand(DynamicCard card, bool removalBanishes) =>
            !IsExtraDeckMonster(card) && !IsExpendable(card) && card.Attack >= CompulsorySaveMinAtk;

        /// <summary>
        /// Our face-up monsters the opponent's removal card being activated would hit: Mirror Force (our attack-position
        /// ones), Torrential (all), Bottomless (1500+ ATK: the one just summoned), Dimensional Prison (our attacker), or
        /// whatever the card reports as its target.
        /// </summary>
        protected async Task<List<DynamicCard>> RemovalHitsAsync(DynamicCard removal)
        {
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup()).ToList();
            if (removal.IsCode(MirrorForce))
                return ours.Where(m => m.IsAttack()).ToList();
            if (removal.IsCode(TorrentialTribute))
                return ours;
            if (removal.IsCode(BottomlessTrapHole))
                return ours.Where(m => m.Attack >= 1500).ToList();
            var targets = (removal.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(removal.TargetCards ?? Array.Empty<CardCoordinate>()).ToList();
            var targeted = ours.Where(m => targets.Any(t => t.Location == (byte)CardLocation.MonsterZone && t.Sequence == m.CurrentCoordinates.Sequence)).ToList();
            if (targeted.Count == 0 && removal.IsCode(DimensionalPrison))
            {
                var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                targeted = ours.Where(m => attacker.ID > 0 && m.CurrentCoordinates.Sequence == attacker.CurrentCoordinates.Sequence).ToList();
            }
            return targeted;
        }

        /// <summary>Whether this removal card banishes (D-Prison, Bottomless...): no way back from the graveyard.</summary>
        protected static async Task<bool> RemovalBanishesAsync(DynamicCard removal) =>
            await CardDatabase.GetAsync(removal.ID) is DBCardInfo info && (info.CardText ?? "").Contains("banish", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Chance to spend Solemn Judgment protecting this monster of ours from the removal (when Compulsory can't): 0 by
        /// default. Deck brains raise it for what they can't afford to lose (Machina: a Fortress being banished).
        /// </summary>
        protected virtual double SolemnToProtectChance(DynamicCard ours, bool removalBanishes) => 0;

        private readonly Random _solemnRandom = new();

        /// <summary>ATK from which our own monster is worth saving with Compulsory Evacuation Device. Tune from logs.</summary>
        [Tunable("ATK from which our own monster is worth saving with Compulsory Evacuation Device. Tune from logs.")]
        public int CompulsorySaveMinAtk { get; set; } = 2000;

        /// <summary>The attacker is an Extra Deck monster or a token: bouncing it removes it for good.</summary>
        protected static bool BounceRemoves(DynamicCard card) =>
            card.ID > 0 && (IsExtraDeckMonster(card) || card.HasType(CardType.Token));
    }
}
