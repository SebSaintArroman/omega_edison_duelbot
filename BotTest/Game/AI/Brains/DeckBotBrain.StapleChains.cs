using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared chain responses for staple cards (moved from the Vayu brain, which uses them as they are): answering an
    /// attack with Mirror Force / Dimensional Prison (or a deck's own attack negate, see TryChainAttackNegateAsync; once
    /// per attack, holding a single-target answer for a bigger attacker still to come), Gorz on the right hit, Bottomless /
    /// Torrential on a summon, Trap Dustshoot.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected const int CardTrooperId = 85087012;
        /// <summary>Ryko, Lightsworn Hunter (official id; Omega's pre-errata copy has it as its alias, so IsCode matches both).</summary>
        protected const int RykoId = 21502796;
        /// <summary>Lyla, Lightsworn Sorceress (official id; Omega's pre-errata copy has it as its alias).</summary>
        protected const int LylaId = 22624373;
        protected const int UpstartGoblinId = 70368879;
        protected const int ReinforcementOfTheArmyId = 32807846;
        protected const int ChargeOfTheLightBrigadeId = 94886282;

        /// <summary>
        /// ATK a monster is effectively worth: Card Trooper mills 3 to become 400 + 3x500 = 1900, so treat it as 1900.
        /// </summary>
        protected static int EffectiveAttack(DynamicCard card) =>
            card.IsCode(CardTrooperId) ? Math.Max(card.Attack, 1900) : card.Attack;

        // One answer per attack: the attack we answered (turn, attacker zone, attacker id), and prompts since.
        private bool _respondedToCurrentAttack;
        private (int Turn, int Zone, int Id) _respondedAttackKey;
        private int _promptsSinceAnswer;

        /// <summary>
        /// A deck's own cards that negate an attack without removing the attacker (Vayu's Necro Gardna). They count as
        /// answers for "one answer per attack"; the deck chains them in TryChainAttackNegateAsync.
        /// </summary>
        protected virtual int[] AttackNegateIds => Array.Empty<int>();

        /// <summary>
        /// The deck's own attack negate, tried after the shared removal answers (none by default). Returns the chain
        /// index, or null.
        /// </summary>
        protected virtual Task<int?> TryChainAttackNegateAsync(
            DecisionContext<SelectChainData> context,
            DynamicCard[] options,
            List<DynamicCard> botMonsters,
            List<DynamicCard> enemyMonsters,
            IncomingAttack? incoming) => Task.FromResult<int?>(null);

        /// <summary>
        /// A deck's own card chained during an attack (Blackwing's Icarus Attack destroying the attacker) answers that attack:
        /// the shared answers (D-Prison, Mirror Force) don't add a second card for it (seen 2026-11-12: Icarus destroyed Nova
        /// Summoner and Shining Angel, then D-Prison was chained on Nova too).
        /// </summary>
        protected async Task MarkAttackAnsweredAsync<T>(DecisionContext<T> context)
        {
            if (!await CurrentField.IsBotUnderAttack())
                return;
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            _respondedToCurrentAttack = true;
            _respondedAttackKey = (context.Header.CurrentTurn, attacker.CurrentCoordinates.Sequence, attacker.ID);
            _promptsSinceAnswer = 0;
        }

        private bool IsAttackAnswer(DynamicCard card) => card.IsCode(DimensionalPrisonId, MirrorForceId, CompulsoryEvacuationDeviceId) || card.IsCode(AttackNegateIds);

        /// <summary>A shared-rule note in the duel log.</summary>
        protected static void StapleNote<T>(DecisionContext<T> context, string message) => context.Bot.LogNote($"deck brain: {message}");

        /// <summary>Chains this card if it's on offer (and its rule fires). The rule is rolled only once it's on offer.</summary>
        protected bool TryChainStaple(DecisionContext<SelectChainData> context, DynamicCard[] options, int cardId, string reason, out int index, string? rule = null)
        {
            index = Array.FindIndex(options, card => card.IsCode(cardId));
            if (index < 0)
                return false;
            if (rule != null && !Rule(rule))
            {
                index = -1;
                return false;
            }
            StapleNote(context, $"chain {CardNames.Get(cardId)}: {reason}");
            return true;
        }

        /// <summary>
        /// Our responses to the opponent's attacks and summons: Gorz, Mirror Force / Dimensional Prison / the deck's own
        /// attack negate (one answer per attack), Bottomless / Torrential (one per summon), Trap Dustshoot. Null when none applies.
        /// </summary>
        protected async Task<int?> TryChainResponsesAsync(DecisionContext<SelectChainData> context, DynamicCard[] options, bool botTurn, CancellationToken ct = default)
        {

            // One cheap call: attack responses (Mirror Force, D-Prison, attack negates) only matter while under attack.
            bool underAttack = await CurrentField.IsBotUnderAttack();
            if (!underAttack)
                _respondedToCurrentAttack = false;
            else if (_respondedToCurrentAttack)
            {
                // A new attack (another attacker, or a later turn) is a new issue: the answer to the last one doesn't cover it.
                await PrepareContextAsync(context, ct);
                var attackerNow = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                _promptsSinceAnswer++;
                // Our answer already removed the attacker (D-Prison banished it, Mirror Force destroyed it): the prompts
                // right after it resolves are still the same attack, even if the server now points at a copy of the
                // attacker (another X-Saber Airbellum) or the card where it went. Seen 2026-11-06: D-Prison + Necro Gardna
                // on one attack.
                var enemyField = await GetEnemyMonsterCardsAsync();
                bool answeredGone = !enemyField.Any(card => card.CurrentCoordinates.Sequence == _respondedAttackKey.Zone && card.ID == _respondedAttackKey.Id);
                bool attackerOffField = attackerNow.ID <= 0 || attackerNow.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone;
                bool sameAttack = (context.Header.CurrentTurn, attackerNow.CurrentCoordinates.Sequence, attackerNow.ID) == _respondedAttackKey ||
                                  context.Header.CurrentTurn == _respondedAttackKey.Turn && answeredGone &&
                                  (attackerOffField || attackerNow.ID == _respondedAttackKey.Id && _promptsSinceAnswer <= 2);
                if (!sameAttack)
                    _respondedToCurrentAttack = false;
                else if (answeredGone)
                    StapleNote(context, $"the attacker we answered ({CardNames.Get(_respondedAttackKey.Id)} in M{_respondedAttackKey.Zone + 1}) is already gone: " +
                                  "still the same attack, no second answer");
            }

            bool hasNonAttackCandidate = options.Any(card =>
                card.IsCode(GorzId, BottomlessTrapHoleId, TorrentialTributeId, TrapDustshootId, DDCrowId, SolemnJudgmentId, CompulsoryEvacuationDeviceId,
                    ReturnFromTheDifferentDimensionId));
            if (!underAttack && !hasNonAttackCandidate)
            {
                StapleNote(context, "not under attack and nothing else to chain, pass");
                return -1;
            }

            await PrepareContextAsync(context, ct);
            bool needMonsters = underAttack || options.Any(card => card.IsCode(TorrentialTributeId));
            var enemyMonsters = needMonsters ? await GetEnemyMonsterCardsAsync() : new List<DynamicCard>();
            var botMonsters = needMonsters ? await GetBotMonsterCardsAsync() : new List<DynamicCard>();

            // Return from the Different Dimension in their Battle Phase: a last resort against a lethal attack.
            if (await TryChainReturnDimensionAsync(context, options, botTurn) is int returnIndex)
                return returnIndex;

            // D.D. Crow: their card targets a card in their graveyard, banish the target.
            if (await TryChainCrowAsync(context, options) is int crowIndex)
                return crowIndex;

            // Compulsory Evacuation Device saving our own monster from their removal (Mirror Force, Torrential, a target).
            if (await TryChainCompulsorySaveAsync(context, options) is int saveIndex)
                return saveIndex;

            // Solemn Judgment: only to push our big play through, or to stop a card that swings the game.
            if (await TryChainSolemnAsync(context, options, botTurn) is int solemnIndex)
                return solemnIndex;

            // Gorz: wait for the biggest direct attack (biggest token), unless waiting would lose the game.
            int index = -1;
            if (options.Any(card => card.IsCode(GorzId)))
            {
                var gorzWhy = await GorzNowAsync(context, enemyMonsters);
                if (gorzWhy != null && TryChainStaple(context, options, GorzId, gorzWhy, out index, rule: DeckRules.GorzChain))
                    return index;
            }

            // Being attacked: answer once per attack.
            if (underAttack && !botTurn && _respondedToCurrentAttack &&
                options.Any(IsAttackAnswer))
                StapleNote(context, "this attack is already answered by a card in the chain: don't chain a second card for it");
            if (underAttack && !botTurn && !_respondedToCurrentAttack)
            {
                // (Their flipped Ryko doesn't count: Mirror Force taking it gains nothing.)
                int attackers = enemyMonsters.Count(card => card.IsFaceup() && card.IsAttack() && !IsSpentFlipMonster(card));
                // D-Prison targets the attacker: Thought Ruler Archfiend and co. just negate it (their LP cost never
                // matters in practice). Then it isn't an answer at all.
                bool dPrisonOk = true;
                if (options.Any(card => card.IsCode(DimensionalPrisonId)))
                {
                    var dPrisonTarget = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                    if (await TrapTargetNegatedAsync(dPrisonTarget) is string negated && Rule(DeckRules.NoTrapIntoNegator))
                    {
                        dPrisonOk = false;
                        StapleNote(context, $"don't use Dimensional Prison: {negated}");
                    }
                }
                // Single-target answers (D-Prison, an attack negate) wait for a bigger attacker still to come.
                bool holdSingle = options.Any(IsAttackAnswer) &&
                                  await HoldForBiggerAttackerAsync(context, enemyMonsters, botMonsters,
                                      banishing: dPrisonOk && options.Any(card => card.IsCode(DimensionalPrisonId)),
                                      removalAvailable: options.Any(card => card.IsCode(DimensionalPrisonId, MirrorForceId))) &&
                                  Rule(DeckRules.HoldForBiggerAttacker);
                // Stardust and co. would just negate Mirror Force (D-Prison banishes, so it's unaffected).
                bool mirrorOk = !options.Any(card => card.IsCode(MirrorForceId)) ||
                                await DestructionNegatedAsync(context, MirrorForceId) == null;
                // Being attacked isn't a reason on its own: answer when letting it through costs us something (a card,
                // their floater's effect, lethal), or the attacker is a strong card worth removing anyway. Never answer
                // our own monster they took control of (it comes back at the End Phase).
                var incoming = await AssessIncomingAttackAsync(enemyMonsters);
                bool answer = incoming == null || incoming.Lethal ||
                              !incoming.OurOwnMonster && (incoming.CostsUs || incoming.WorthRemovingAnyway);
                // Spare answers: with two or more removal traps set, a trap left sitting there is worth nothing, so one
                // goes on any attack that does something (damage or a destroyed monster), small threat or not.
                int removalSet = options.Count(card => card.IsCode(DimensionalPrisonId, MirrorForceId));
                bool hurts = incoming != null && !incoming.OurOwnMonster &&
                             (incoming.Target.ID <= 0 || incoming.Attacker.Attack >= (incoming.Target.IsAttack() ? incoming.Target.Attack : incoming.Target.Defense + 1));
                if (!answer && removalSet >= 2 && hurts && Rule(DeckRules.SpareRemovalTrap))
                {
                    answer = true;
                    StapleNote(context, $"{removalSet} removal traps set: a spare one is worth using on this attack");
                }
                // Removing a recruiter outside battle stops its replacement: Giant Rat (recruits only when destroyed by
                // battle) to any removal, others only to banishing.
                bool banish = answer || incoming != null && !incoming.OurOwnMonster && incoming.WorthBanishingAnyway;
                bool destroy = answer || incoming != null && !incoming.OurOwnMonster && incoming.WorthDestroyingAnyway;
                string why = incoming?.Why ?? "attacker unknown";
                string removalWhy = why + (incoming?.RemovalWhy ?? "");
                bool hasAnswer = options.Any(IsAttackAnswer);
                if (hasAnswer && !banish)
                    StapleNote(context, $"let the attack through: {why}");
                // One answer per attack (like Bottomless / Torrential per summon): decide on the mass card first. Mirror
                // Force when it takes 2+ attack-position monsters; only if it isn't chosen, the single answers
                // (D-Prison first: it banishes and keeps Mirror Force for a bigger board; then Mirror Force on the lone
                // attacker; then the deck's own attack negate). Once one is chained, _respondedToCurrentAttack stops a second.
                bool chained =
                    attackers >= 2 && mirrorOk && incoming?.OurOwnMonster != true &&
                    TryChainStaple(context, options, MirrorForceId, $"under attack, {attackers} attack-position monsters ({removalWhy})", out index, rule: DeckRules.MirrorForceMulti) ||
                    banish && !holdSingle && dPrisonOk && TryChainStaple(context, options, DimensionalPrisonId, $"banish the attacker: {removalWhy}", out index, rule: DeckRules.DimensionalPrison) ||
                    destroy && !holdSingle && mirrorOk && TryChainStaple(context, options, MirrorForceId, $"destroy the attacker: {removalWhy}", out index, rule: DeckRules.MirrorForceSingle);
                // Compulsory Evacuation Device at the last moment, when the attack comes (not on the summon: they might bring
                // out something better first). An Extra Deck attacker or a token is removed for good; any other attacker
                // whose attack costs us is bounced back to their hand.
                bool bounceRemoves = incoming != null && BounceRemoves(incoming.Attacker);
                if (!chained && !holdSingle && incoming != null && (banish && bounceRemoves || answer) &&
                    TryChainStaple(context, options, CompulsoryEvacuationDeviceId,
                        $"{(bounceRemoves ? "return the attacker to the Extra Deck" : "bounce the attacker to their hand")}: {removalWhy}",
                        out index, rule: DeckRules.CompulsoryOnAttack))
                {
                    PlanTarget(context.Header.CurrentTurn, incoming.Attacker, theirs: true);
                    chained = true;
                }
                if (!chained && !holdSingle && options.Any(card => card.IsCode(AttackNegateIds)) &&
                    await TryChainAttackNegateAsync(context, options, botMonsters, enemyMonsters, incoming) is int negate && negate >= 0)
                {
                    index = negate;
                    chained = true;
                }
                if (chained)
                {
                    _respondedToCurrentAttack = true;
                    var answered = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                    _respondedAttackKey = (context.Header.CurrentTurn, answered.CurrentCoordinates.Sequence, answered.ID);
                    _promptsSinceAnswer = 0;
                    return index;
                }
            }

            if (!botTurn)
            {
                // Bottomless / Torrential after an opponent's summon: one card per summon (shared staple rule).
                if (options.Any(card => card.IsCode(BottomlessTrapHoleId, TorrentialTributeId)) &&
                    await AnswerSummonAsync(context) is int summonAnswer && summonAnswer >= 0)
                    return summonAnswer;
                // (Compulsory Evacuation Device waits for the attack: see above.)

                if (TryChainStaple(context, options, TrapDustshootId, "opponent has 4+ cards in hand", out index, rule: DeckRules.TrapDustshoot))
                    return index;
                // (No automatic End Phase MST: a set MST is worth more as bait for the opponent's removal.)
            }
            return null;
        }

        // Our monsters' ignition effects worth using in the priority window after they are summoned (not Lyla).
        /// <summary>
        /// Whether to let this attack through and save a single-target answer for a more dangerous opponent monster
        /// that hasn't attacked yet (e.g. let Kittytail's 200 through and D-Prison Airbellum, whose direct hit would
        /// discard a card from our hand). Life points matter little; cards do. Never holds if this attack is lethal.
        /// </summary>
        protected async Task<bool> HoldForBiggerAttackerAsync<T>(
            DecisionContext<T> context,
            List<DynamicCard> enemyMonsters,
            List<DynamicCard> botMonsters,
            bool banishing,
            bool removalAvailable)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (attacker.ID <= 0)
                return false;
            if (attacker.Attack >= await CurrentField.GetBotLifePoints())
                return false;
            // Read freshly summoned attackers (a new Grand Mole) before valuing them.
            await RefreshRewardOnDestroyAsync(enemyMonsters);

            bool SameZone(DynamicCard a, DynamicCard b) =>
                a.CurrentCoordinates.Location == b.CurrentCoordinates.Location && a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;
            var stillToAttack = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, attacker))
                .ToList();
            if (stillToAttack.Count == 0)
                return false;

            int ourBestBlocker = botMonsters
                .Select(card => card.IsFaceup() ? card.GetRealPower() : OpponentMemory.FaceDownDefense(card))
                .DefaultIfEmpty(0).Max();
            var grave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
            // Banishing (D-Prison) is worth most against monsters that would otherwise come back from the graveyard
            // (e.g. King of the Beasts: "Special Summon this card from your hand or Graveyard").
            async Task<(double, string)> ValueAsync(DynamicCard card)
            {
                int id = card.ID;
                // How good the card is overall (its card correction included: Grand Mole's general bonus).
                var (score, why) = await EffectEvaluator.ScoreOnFieldAsync(id, grave, ourBestBlocker);
                if (banishing && await EffectEvaluator.ReturnsFromGraveyardAsync(id))
                    return (score + EffectEvaluator.BanishRecursionBonus, $"{why} +{EffectEvaluator.BanishRecursionBonus} (comes back from the GY: banish it)");
                if (banishing && await EffectEvaluator.GraveyardEffectBanishAsync(id, grave) is (double gyBonus, string gyWhy))
                    return (score + gyBonus, $"{why}; {gyWhy}");
                return (score, why);
            }

            // What answering each attack is worth: what the attack would cost us (the monster it destroys, or a direct
            // hit's damage and battle-damage effect), plus, with removal, how good the attacker is to get rid of. A negate
            // (e.g. Necro Gardna) doesn't remove the attacker, so then only the cost counts.
            var currentTarget = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var hand = await GetBotHandCardsAsync();
            var (attackerCost, attackerCostWhy) = await AttackCostAsync(attacker, currentTarget.ID > 0 ? currentTarget : null, botMonsters, hand);
            // Only let this attack through if it costs us next to nothing (a small hit): holding when it destroys one of
            // our monsters or hits hard lost games (2,000-duel analysis, 2026-11-08).
            // Fewer answers than attackers: they go on the biggest threats, so an attack that only costs us an expendable monster
            // (Plaguespreader) doesn't force the answer out (user, 2026-11-13: "why use dprison on freed. you should've saved it
            // for krystia - if you have fewer answers than they have threats, you should focus your answers on their biggest
            // threats").
            int answers = (await CurrentField.GetCardsInSpellZone()).Count(card => card.IsCode(DimensionalPrisonId, MirrorForceId, CompulsoryEvacuationDeviceId));
            bool scarce = answers < 1 + stillToAttack.Count;
            bool onlyExpendable = currentTarget.ID > 0 && IsExpendable(currentTarget);
            if (attackerCost >= HoldMaxCurrentCost && scarce && onlyExpendable)
                StapleNote(context, $"answers are scarce ({answers} for {1 + stillToAttack.Count} attackers) and {CardNames.Get(attacker.ID)}'s attack only " +
                                    $"costs our expendable {CardNames.Get(currentTarget.ID)}: weigh saving the answer for a bigger threat");
            else if (attackerCost >= HoldMaxCurrentCost)
            {
                StapleNote(context, $"don't save the answer: {CardNames.Get(attacker.ID)}'s attack costs us now ({attackerCostWhy})");
                return false;
            }
            var (attackerScore0, attackerWhy0) = removalAvailable ? await ValueAsync(attacker) : (0.0, "");
            double attackerScore = attackerCost + attackerScore0;
            string attackerWhy = removalAvailable ? $"{attackerWhy0}; {attackerCostWhy}" : attackerCostWhy;
            DynamicCard bigger = default;
            string biggerWhy = "";
            double biggerScore = attackerScore;
            foreach (var card in stillToAttack)
            {
                var (cost, costWhy) = await AttackCostAsync(card, null, botMonsters, hand);
                var (score0, why0) = removalAvailable ? await ValueAsync(card) : (0.0, "");
                double score = cost + score0;
                if (score > biggerScore)
                {
                    bigger = card;
                    biggerScore = score;
                    biggerWhy = $"{CardNames.Get(card.ID)}: {(removalAvailable ? $"{why0}; " : "")}{costWhy}";
                }
            }
            if (bigger.ID <= 0)
                return false;

            // Only worth waiting if we'd actually use the answer on that attack: otherwise we end up answering neither.
            if (await WouldAnswerLaterAsync(bigger, removalAvailable, botMonsters) is not string later)
            {
                StapleNote(context, $"don't save the answer for {CardNames.Get(bigger.ID)}: we wouldn't use it on that attack either " +
                              "(not a big threat, and its attack wouldn't cost us anything that matters)");
                return false;
            }

            StapleNote(context, $"let {CardNames.Get(attacker.ID)}'s attack through ({attackerWhy}); save the answer for {biggerWhy} ({later})");
            return true;
        }


        /// <summary>
        /// What one attack would cost us, in rough "card" points: our monster it destroys (2 for the card + its ATK/1000,
        /// +1.5 for an Extra Deck monster that's hard to replace), or for a direct hit the card its battle-damage effect
        /// takes from our hand (the best card there) plus the damage (big when it's lethal or leaves us within reach of
        /// their board, otherwise small). <paramref name="target"/> is the attack's known target; for attacks still to
        /// come it's the most valuable monster of ours they beat, or a direct hit if they beat none.
        /// </summary>
        protected async Task<(double Cost, string Why)> AttackCostAsync(DynamicCard attacker, DynamicCard? target, List<DynamicCard> botMonsters, List<DynamicCard> hand)
        {
            double MonsterValue(DynamicCard card) =>
                CardValueBase + card.GetRealPower() / 1000.0 + (card.HasType(CardType.Synchro) || card.HasType(CardType.Fusion) || card.HasType(CardType.Xyz) ? ExtraDeckMonsterValue : 0);
            var victim = target ?? botMonsters
                .Where(card => card.ID > 0 && card.IsFaceup() && !IsExpendable(card) && attacker.Attack > card.GetRealPower())
                .OrderByDescending(MonsterValue).FirstOrDefault();
            // Let through, the attacker survives the turn: what it still does afterwards is ours to lose too (CSV M1: Lyla let through
            // destroyed one of our spells/traps in Main Phase 2: two cards lost where D-Prison costs one).
            var (after, afterWhy) = await SurvivorRemovalCostAsync(attacker);
            if (victim is DynamicCard v && v.ID > 0 && v.IsFaceup())
            {
                if (attacker.Attack <= v.GetRealPower())
                    return (after, after > 0 ? $"its attack on our {CardNames.Get(v.ID)} costs us nothing, but {afterWhy}" : $"its attack on our {CardNames.Get(v.ID)} costs us nothing");
                return (MonsterValue(v) + after, $"it would destroy our {CardNames.Get(v.ID)}{(after > 0 ? $", and {afterWhy}" : "")}");
            }
            // A direct hit.
            int lp = await CurrentField.GetBotLifePoints();
            int theirBoard = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).Sum(card => card.Attack);
            double damage = attacker.Attack >= lp ? LethalHitCost : lp - attacker.Attack <= theirBoard ? NearLethalHitCost : attacker.Attack / DirectHitScale;
            string why = $"a {attacker.Attack} direct hit";
            if (hand.Count > 0 && await CardDatabase.GetAsync(attacker.ID) is DBCardInfo info && HandDiscardOnHitText.IsMatch(info.CardText ?? ""))
            {
                // A random card (Airbellum): the expected loss is the average value of our hand.
                double total = 0;
                var parts = new List<string>();
                foreach (var card in hand)
                {
                    var (value, valueWhy) = await HandCardValueAsync(card, botMonsters);
                    total += value;
                    parts.Add($"{valueWhy} {value:0.#}");
                }
                double expected = total / hand.Count;
                return (damage + expected + after, $"{why} whose effect discards a random card from our hand (expected loss {expected:0.#}: {string.Join("; ", parts)})" +
                                                    (after > 0 ? $", and {afterWhy}" : ""));
            }
            return (damage + after, after > 0 ? $"{why}, and {afterWhy}" : why);
        }

        private static readonly System.Text.RegularExpressions.Regex DestroysSpellTrapText = new(
            @"destroy[^.]{0,60}Spell/Trap|Spell/Trap[^.]{0,60}destroy",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// What an attacker of theirs still takes from us this turn if it survives (CSV M1): one whose own effect destroys a spell/trap
        /// (Lyla) costs one of our spells/traps while we have another besides the answer we'd hold. 0 when it can't.
        /// </summary>
        private async Task<(double Cost, string Why)> SurvivorRemovalCostAsync(DynamicCard attacker)
        {
            if (attacker.ID <= 0 || IsEffectNegated(attacker) || await CardDatabase.GetAsync(attacker.ID) is not DBCardInfo info ||
                !DestroysSpellTrapText.IsMatch(info.CardText ?? ""))
                return (0, "");
            int ours = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && card.CurrentCoordinates.Controller == 0);
            return ours <= 1 ? (0, "") : (SurvivorSpellKillCost, $"{CardNames.Get(attacker.ID)} lives to destroy one of our spells/traps afterwards");
        }

        /// <summary>
        /// How useful a card in our hand is, in rough card points: a normal card is about 2 + ATK/1000 (2.5 for a spell/trap),
        /// Gorz 4, and a card that works from the graveyard close to free: discarding it puts it
        /// where it works. Deck brains override this for their own key cards (e.g. a live Dark Armed Dragon).
        /// </summary>
        protected virtual async Task<(double Value, string Why)> HandCardValueAsync(DynamicCard card, List<DynamicCard> botMonsters)
        {
            if (card.IsCode(GorzId))
                return (GorzHandValue, "Gorz");
            if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""))
                return (DiscardedGraveyardPieceCost, $"{CardNames.Get(card.ID)} (works from the graveyard: discarding it costs little)");
            double plain = card.HasType(CardType.Monster) ? CardValueBase + BotTest.Game.AI.Search.MctsEvaluator.AtkValue(card.Attack) : SpellTrapCardValue;
            return (plain, CardNames.Get(card.ID));
        }

        /// <summary>
        /// The most the current attack may cost us (AttackCostAsync points: a destroyed monster is 2+, a small direct hit
        /// under 1) for a single answer to be saved for a bigger attacker. Tune from logs.
        /// </summary>
        [Tunable("The most the current attack may cost us (AttackCostAsync points: a destroyed monster is 2+, a small direct hit under 1) for a single answer to be saved for a bigger attacker. Tune from logs.")]
        public double HoldMaxCurrentCost { get; set; } = 1.0;

        /// <summary>What losing a graveyard-effect card from hand to a discard costs. Tune from logs.</summary>
        [Tunable("What losing a graveyard-effect card from hand to a discard costs. Tune from logs.")]
        public double DiscardedGraveyardPieceCost { get; set; } = 0.3;

        /// <summary>
        /// Whether we'd answer this attacker's attack when it comes: removal for a big threat, or any answer when its
        /// attack would cost us (it beats a monster of ours that isn't expendable, or its hit leaves us within reach of
        /// their board). Returns the reason, or null.
        /// </summary>
        protected async Task<string?> WouldAnswerLaterAsync(DynamicCard attacker, bool removalAvailable, List<DynamicCard> botMonsters)
        {
            if (removalAvailable && await IsBigThreatAsync(attacker))
                return "a big threat, worth removing";
            var kept = botMonsters.FirstOrDefault(card => card.IsFaceup() && !IsExpendable(card) && attacker.Attack > card.GetRealPower());
            if (kept.ID > 0)
                return $"it would destroy our {CardNames.Get(kept.ID)}";
            int lp = await CurrentField.GetBotLifePoints();
            int theirBoard = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).Sum(card => card.Attack);
            if (lp - attacker.Attack <= theirBoard)
                return $"its hit would leave us within reach of their board ({theirBoard} ATK)";
            return null;
        }
    }
}
