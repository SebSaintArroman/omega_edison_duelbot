using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared resource rules (moved from the Vayu brain): is a play worth the cards it spends? JustifySpendAsync weighs
    /// lethal, answering a threat, racing, refunded plays, holding for a better time, and a roll for pushing the clock
    /// (more likely against an open opponent: few cards, no set traps). Deck brains pass their own play details
    /// (spare copies, what refunds the play) and hooks.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Whether we have used our Normal Summon this turn: a Normal Summon or monster set actually made (AfterMainMoveAsync),
        /// not one a deck rule only planned. Seen 2026-11-13: the brain planned to set Ryko, the fixed Pot of Greed rule
        /// played instead, and the Normal Summon counted as used, so "Gearframe first" was skipped and Fortress discarded it.
        /// </summary>
        protected virtual bool NormalSummonUsed(BotStateHeader header) => _normalSummonMadeTurn == header.CurrentTurn;

        // The turn our last Normal Summon / monster set was made (-1: none this duel).
        private int _normalSummonMadeTurn = -1;

        /// <summary>
        /// Decides whether spending a resource (special summon from hand, removal effect, Vayu, Plaguespreader, synchro) is worth it.
        /// Returns the reason when it is, or null (and logs why) when the bot should hold it:
        ///  1. it wins this turn (only while the Battle Phase is still ahead),
        ///  2. it answers a threat the current board can't handle,
        ///  3. the opponent isn't on a clock yet and it creates or speeds one up.
        /// </summary>
        protected async Task<string?> JustifySpendAsync<T>(
            DecisionContext<T> context,
            string what,
            int newAtk,
            IReadOnlyCollection<DynamicCard>? fieldMaterials,
            bool removal,
            int cardId = 0,
            bool extraDeck = false,
            bool allowPush = true,
            string? effectAnswer = null,
            string? refundedBy = null,
            int spares = 0)
        {
            _rollChance = null;
            var why = await JustifySpendCoreAsync(context, what, newAtk, fieldMaterials, removal, cardId, extraDeck, allowPush, effectAnswer, refundedBy, spares);
            // Held: a search line that makes the play anyway pays for going against the spend rules (any action with the card:
            // Vayu's activation, a synchro's summon, Plaguespreader's revive). From the push chance (a former roll): in proportion.
            if (why == null)
                Hold(cardId, null, DeckRules.HoldSpend);
            else if (_rollChance is double holdChance)
            {
                _rollChance = null;
                HoldScaled(cardId, null, DeckRules.HoldSpend, holdChance);
            }
            // Justified as lethal: the battle that follows is a lethal push, which later rules don't hold back (Gorz fear,
            // chip into backrow): see DeckBotBrain.Commitments (user, 2026-11-12: Fortress summoned "lethal this turn", then
            // no attack for fear of Gorz).
            else if (why.Contains("lethal") && !IsSearchTwin)
                CommitLethalPush(context.Header.CurrentTurn, $"{what}: {why}");
            return why;
        }

        private async Task<string?> JustifySpendCoreAsync<T>(
            DecisionContext<T> context,
            string what,
            int newAtk,
            IReadOnlyCollection<DynamicCard>? fieldMaterials,
            bool removal,
            int cardId,
            bool extraDeck,
            bool allowPush,
            string? effectAnswer,
            string? refundedBy,
            int spares)
        {
            // Spare copies of the resource (e.g. more Vayus in the graveyard) loosen the holding: each one lowers the
            // clock we hold at by SpareClockStep turns and raises the push chance by SparePushBoost.
            int holdWhenClockAtMost = HoldWhenClockAtMost - spares * SpareClockStep;
            string spareText = spares > 0 ? $"; {spares} spare (more copies ready to use), so held less tightly" : "";
            // Pressing (the risk tolerance above its base: low LP, a stall, the deck race, late in the duel): we only hold back while
            // the clock is already short (S13).
            await RiskToleranceAsync();
            int pressSteps = (int)Math.Round(RiskPressNow * 3);
            if (pressSteps > 0)
            {
                holdWhenClockAtMost -= pressSteps;
                spareText += $"; pressing ({RiskPressNow:P0} of the way to full risk): held only on a clock of {Math.Max(0, holdWhenClockAtMost)} turn(s) or less";
            }
            var botMonsters = await GetBotMonsterCardsAsync();
            var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);

            var attackers = botMonsters.Select(EffectiveAttack).ToList();
            var after = new List<int>(attackers);
            foreach (var material in fieldMaterials ?? Array.Empty<DynamicCard>())
                after.Remove(material.Attack);
            if (newAtk > 0)
                after.Add(newAtk);

            // Only big threats justify spending (see IsBigThreatAsync): a small one (Giant Rat, which we wouldn't even
            // attack) is left to the clock rules below.
            var allThreats = BoardAssessment.Threats(attackers, defenders);
            var threats = await BigThreatsAsync(allThreats);
            var smallThreats = allThreats.Where(t => !threats.Contains(t)).ToList();
            // The big threats this spend actually deals with: removal takes the biggest, a monster beats (or trades with) some.
            // A monster doesn't answer a Catastor-type threat that destroys it by effect in battle (a non-DARK monster vs
            // Ally of Justice Catastor), whatever the ATK.
            int newAttribute = !removal && newAtk > 0 && cardId > 0 && await CardDatabase.GetAsync(cardId) is DBCardInfo newInfo ? newInfo.Attribute : 0;
            var destroyedBy = threats.Where(t => newAttribute != 0 && DestroysAttributeInBattle(t.Id, newAttribute)).ToList();
            if (destroyedBy.Count > 0)
                StapleNote(context, $"{what} can't answer {BoardAssessment.DescribeThreats(destroyedBy)}: it destroys a monster like ours " +
                                    "by its effect when they battle, whatever the ATK");
            var answered = removal
                ? threats.OrderByDescending(t => t.Power).Take(1).ToList()
                : threats.Where(t => newAtk >= t.Power && !destroyedBy.Contains(t) && !t.HonestBacked).ToList(); // a known Honest wins that battle
            var defendersAfter = new List<BoardAssessment.Defender>(defenders);
            if (removal && defendersAfter.Count > 0)
                defendersAfter.Remove(defendersAfter.OrderByDescending(d => d.Power).First());

            int clockNow = BoardAssessment.TurnsToWin(attackers, defenders, oppLp);
            int clockAfter = BoardAssessment.TurnsToWin(after, defendersAfter, oppLp);

            var header = context.Header;
            bool battleAhead = (DuelPhase)header.CurrentPhase == DuelPhase.Main1 && header.CurrentTurn > 1;
            bool addsMonster = newAtk > 0;

            // Already lethal on board: any one of our attacks wins, so more monsters only add exposure to Mirror Force
            // / Torrential. (Removal that clears a blocker is still fine.)
            if (battleAhead && clockNow == 1 && addsMonster && Rule(DeckRules.HoldWhenAlreadyLethal))
            {
                StapleNote(context, $"hold {what}: our board is already lethal this turn; another monster only adds exposure to their backrow");
                return null;
            }

            if (battleAhead && clockAfter == 1 && Rule(DeckRules.SpendForLethal))
            {
                // A lethal push into set cards: it may well be stopped (Torrential, Mirror Force...), and then we've
                // overextended for nothing. Roll for it, unless it costs nothing extra on board or holding back loses.
                if (!addsMonster)
                    return $"{what}: lethal this turn";
                var (through, throughWhy) = await PushGetsThroughAsync();
                if (through >= 1)
                    return $"{what}: lethal this turn";
                if (await WeLoseNextTurnAsync())
                    return $"{what}: lethal this turn, and holding back loses ({throughWhy})";
                var (goChance, aheadNote) = await LethalPushChanceAsync(through, clockNow);
                bool go = await PushRollAsync(header, goChance);
                if (go)
                    return $"{what}: lethal this turn ({throughWhy}{aheadNote}; rolled to go for it)";
                StapleNote(context, $"hold {what}: lethal on paper, but {throughWhy}{aheadNote}; rolled to hold rather than overextend into it");
                return null;
            }

            // A synchro whose effect deals with a threat our board can't (e.g. Catastor into a non-DARK monster).
            if (effectAnswer != null && Rule(DeckRules.SpendForEffectAnswer))
                return $"{what}: {effectAnswer}";

            // Low life points with nothing on our field: their board can finish us, so a monster is worth it even if it
            // doesn't beat their biggest (it blocks one attack and makes them commit). Life points matter little until
            // they're low; here they are. Not while an empty field keeps Gorz live.
            if (addsMonster && !botMonsters.Any(card => card.ID > 0))
            {
                int ourLp = await CurrentField.GetBotLifePoints();
                int theirBoard = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack);
                if (theirBoard >= ourLp && Rule(DeckRules.SpendForBlockerWhenLow) && context is DecisionContext<IdleCmdData> idleContext &&
                    !await KeepGorzLiveAsync(idleContext, await GetEnemyMonsterCardsAsync()))
                    return $"{what}: we're at {ourLp} LP with an empty field and their board ({theirBoard} ATK) can finish us: a blocker is worth it";
            }

            // Grand Mole is especially strong against us: removal that can take it is worth spending.
            bool bouncerUp = defenders.Any(d => d.BouncesAttacker);
            if (removal && bouncerUp && Rule(DeckRules.SpendRemovalOnBouncer))
                return $"{what}: removes their {BoardAssessment.DescribeThreats(defenders.Where(d => d.BouncesAttacker))} " +
                       "(it stalls our attacks and bounces our synchros; get rid of it when possible)";

            // Grand Mole (a monster that bounces what battles it): it attacks into an Extra Deck monster on its turn and
            // sends it back to the Extra Deck. Synchros only pressure through it if they win now or answer something else.
            if (extraDeck && bouncerUp && !answered.Any(t => !t.BouncesAttacker) && Rule(DeckRules.HoldSynchroIntoBouncer))
            {
                StapleNote(context, $"hold {what}: {BoardAssessment.DescribeThreats(defenders.Where(d => d.BouncesAttacker))} " +
                              "would just bounce it back to the Extra Deck, and it doesn't win this turn or answer anything else");
                return null;
            }

            // We already spent Heavy Storm / MST to clear the way for this turn's big play: follow through with the
            // plays that made it up (and a synchro using one of them as material), not with everything; and only
            // while each one still adds something (a threat left to answer, or a faster clock band).
            if (CommittedToBigPlay(header, cardId, fieldMaterials))
            {
                bool answers = answered.Count > 0;
                bool faster = BoardAssessment.SpeedOf(clockAfter) < BoardAssessment.SpeedOf(clockNow);
                if (answers || faster)
                    return $"{what}: part of the big play the backrow was cleared for ({(answers ? $"answers {BoardAssessment.DescribeThreats(answered)}" : $"clock {BoardAssessment.Describe(clockNow)} -> {BoardAssessment.Describe(clockAfter)}")})";
                // A monster of theirs that only the new one can run over this turn: what's ready to attack (face-up attack
                // position, not attacked yet) can't. Seen 2026-11-11: Heavy Storm cleared the way, then Goyo was held as
                // "no threat left" because Shura (1800, but in defence after Blizzard's revive) was counted against Freed (1700).
                if (battleAhead && addsMonster)
                {
                    var ready = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                        .Select(EffectiveAttack).DefaultIfEmpty(0).Max();
                    var runsOver = defenders.Where(d => newAtk > d.Power && ready <= d.Power && !d.HonestBacked).ToList();
                    if (runsOver.Count > 0 && Rule(DeckRules.SpendToAnswerThreat))
                    {
                        if (cardId > 0)
                            CommitAttackThisTurn(header.CurrentTurn, cardId, $"{what} runs over {BoardAssessment.DescribeThreats(runsOver)}");
                            return $"{what}: part of the big play the backrow was cleared for (runs over {BoardAssessment.DescribeThreats(runsOver)}, which nothing ready to attack can)";
                    }
                }
                StapleNote(context, $"hold {what}: part of the big play, but it adds nothing now: " +
                              (threats.Count > 0 ? $"it doesn't answer {BoardAssessment.DescribeThreats(threats)}" : "no threat left") +
                              $", and the clock stays {BoardAssessment.Describe(clockNow)}");
                return null;
            }

            // Equal ATK still gets rid of the threat as a 1-for-1 trade. (Synchros summoned by Vayu have their effects
            // negated, so e.g. Armor Master's battle protection doesn't apply; it really is a trade.)
            if (answered.Count > 0 && Rule(DeckRules.SpendToAnswerThreat))
                return $"{what}: answers threat {BoardAssessment.DescribeThreats(answered)}" +
                       (answered.Count < threats.Count ? $" (not {BoardAssessment.DescribeThreats(threats.Except(answered))})" : "");

            // Materials on the field doing nothing (in defence, already attacked, or beating none of their monsters): turning
            // them into a monster that runs over what nothing ready to attack can is the play, not a spend to save for (user,
            // 2026-11-12: Blizzard and Shura in defence held from Goyo / Armed Wing "no big threat" against two D.D. Warrior
            // Ladies; "why didnt the bot synchro summon??? massive misplay").
            var materialList = (fieldMaterials ?? Array.Empty<DynamicCard>()).ToList();
            if (addsMonster && materialList.Count > 0 && defenders.Count > 0)
            {
                // (Not "already attacked": in Main Phase 2 every attacker has, and the rule then overrode the spend rules'
                // own hold on the very next decision - an inconsistency, seen 2026-11-12. Main Phase 1 only, below.)
                bool Idle(DynamicCard m) => !m.IsAttack() || defenders.All(d => EffectiveAttack(m) <= d.Power);
                var ready = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                                                      !materialList.Any(m => m.CurrentCoordinates.Sequence == card.CurrentCoordinates.Sequence))
                    .Select(EffectiveAttack).DefaultIfEmpty(0).Max();
                var runsOver = defenders.Where(d => newAtk > d.Power && ready <= d.Power && !d.HonestBacked).ToList();
                int theirBest = defenders.Where(d => d.AttackPosition && !d.FaceDown).Select(d => d.Power).DefaultIfEmpty(0).Max();
                if (battleAhead && materialList.All(Idle) && runsOver.Count > 0 && Rule(DeckRules.IdleMaterialsSynchro))
                {
                    if (cardId > 0)
                        CommitAttackThisTurn(header.CurrentTurn, cardId, $"{what} runs over {BoardAssessment.DescribeThreats(runsOver)}");
                        return $"{what}: its materials do nothing where they are, and it runs over {BoardAssessment.DescribeThreats(runsOver)}, which nothing ready to attack can";
                }
            }

            // Round-robin 30 (user, 2026-11-13: "yes. also it's rare you'll ever want to synchro in mp2"): a synchro from field
            // materials in Main Phase 1 when it hits harder this turn than its materials together (Knight + Plaguespreader into
            // Goyo: 2800 instead of Knight's 1400 direct, then the synchro in Main Phase 2 anyway). Only into an open field or past
            // what it beats; a material with a kill of its own to make first isn't idle (the rule above covers the rest).
            if (extraDeck && addsMonster && battleAhead && materialList.Count > 0 && Rule(DeckRules.SynchroBeforeBattle))
            {
                int MaterialHit(DynamicCard m) => !m.IsFaceup() || !m.IsAttack() || m.Attacked ? 0
                    : defenders.Count == 0 ? EffectiveAttack(m)
                    : defenders.Any(d => EffectiveAttack(m) > d.Power) ? 1 : 0; // a kill: worth keeping its attack
                int materialHits = materialList.Sum(MaterialHit);
                bool materialKills = defenders.Count > 0 && materialList.Any(m => MaterialHit(m) == 1);
                int synchroHit = defenders.Count == 0 ? newAtk : 0;
                // Damage alone only justifies it when the materials are expendable: a valuable material (Machina Fortress) is a
                // better card than a synchro that merely hits a little harder (user, 2026-11-13: "fortress is a better card than
                // gottoms and gottoms shouldn't be made"; seen: Plaguespreader revived, Cyber Dragon paid, for a Gottoms that then
                // wasn't made).
                bool expendable = materialList.All(m => TributeCost(m) <= ExpendableTributeCost);
                if (expendable && !materialKills && synchroHit > materialHits && materialList.All(m => !m.Attacked))
                    return $"{what}: in Main Phase 1 it hits for {synchroHit}, more than its materials' {materialHits} together (expendable materials)";
                // ...or it gets over a monster of theirs that neither its materials nor our other ready attackers can (user,
                // 2026-11-13: "or gets over something the materials cant").
                int bestOther = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Select(EffectiveAttack).DefaultIfEmpty(0).Max();
                var over = defenders.Where(d => !d.FaceDown && newAtk > d.Power && bestOther <= d.Power && !d.HonestBacked)
                    .OrderByDescending(d => d.Power).Select(d => (BoardAssessment.Defender?)d).FirstOrDefault();
                if (over is { } overThis && materialList.All(m => !m.Attacked))
                    return $"{what}: in Main Phase 1 it gets over {BoardAssessment.DescribeThreats(new[] { overThis })}, which none of ours can";
            }

            // Low on cards with nothing set: a bigger body that they can't run over in their turn is worth a resource.
            if (await SurvivabilityReasonAsync(what, newAtk, fieldMaterials) is string survives)
                return survives;

            // A play we get straight back (Miracle Dig / Burial returning Vayu and its partner afterwards) is nearly free:
            // worth it whenever it improves the clock at all, not only to answer a threat.
            if (refundedBy != null && clockAfter < clockNow && Rule(DeckRules.SpendWhenRefunded))
                return $"{what}: nearly free ({refundedBy}); clock {BoardAssessment.Describe(clockNow)} -> {BoardAssessment.Describe(clockAfter)}";

            // Racing: if their board kills us before (or as soon as) ours kills them, speeding our clock up is the
            // defence (offence is a good defence). Worth it when it makes us win the race: it's our turn, so on equal
            // clocks we land the last hit first.
            // Short of winning it, a faster clock only matters when we're close behind (see below).
            if (clockAfter < clockNow)
            {
                int theirClock = await TheirClockOnUsAsync();
                // Behind, pressure only matters when it's close: one turn behind, with them not winning on their very next
                // turn. Further behind, a faster clock doesn't change the race, and the resource is worth more held (user,
                // 2026-11-11: Plaguespreader + Dark Grepher into Brionac at 400 LP against 7400 and an empty field, a 5-turn
                // clock to 4, was "overkill": "just attack with grepher and save the plaguespreader resource for later").
                bool wins = clockAfter <= theirClock;
                bool close = clockAfter - theirClock <= 1 && theirClock >= 2;
                if (theirClock != BoardAssessment.NoClock && theirClock <= clockNow && (wins || close) && Rule(DeckRules.SpendWhenRacing))
                    return $"{what}: racing: they're on course to kill us in {theirClock} turn(s), ours goes " +
                           $"{BoardAssessment.Describe(clockNow)} -> {BoardAssessment.Describe(clockAfter)}" +
                           (wins ? ", so we win the race" : "; one turn behind, so the pressure makes them answer it");
            }

            // An open opponent (no set Spell/Traps, few cards in hand) can't punish the push: speed comes first, so the roll
            // below decides instead of a hard hold. With traps set or a big hand, saving for a better time comes first.
            int setTraps = OpponentSetSpellTraps;
            int handNow = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
            bool opponentOpen = setTraps == 0 && handNow <= PushOpenHandMax;
            if (!allowPush && !opponentOpen && clockNow > holdWhenClockAtMost && clockAfter < clockNow && Rule(DeckRules.HoldForThreatOrWin))
            {
                StapleNote(context, $"hold {what}: kept for answering a threat or winning, not just for a clock " +
                              $"({BoardAssessment.Describe(clockNow)} -> {BoardAssessment.Describe(clockAfter)})");
                return null;
            }

            // A push is about damage and destroying things by battle this turn: with no Battle Phase ahead (Main Phase 2, turn 1)
            // there is no push. Main Phase 2 is for defensive plays only, which have their own reasons above (user, 2026-11-13).
            if (!battleAhead && clockNow > holdWhenClockAtMost && clockAfter < clockNow)
            {
                StapleNote(context, $"hold {what}: no push without a Battle Phase this turn (Main Phase 2 is for defensive plays only)");
                return null;
            }

            if (clockNow > holdWhenClockAtMost && clockAfter < clockNow && Rule(DeckRules.PushForClock))
            {
                // Pushing the clock is a judgement call, so roll for it instead of using a hard rule.
                int oppHand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                bool freeSummonLeft = context.StateData is IdleCmdData idle
                    ? (idle.SummonableCards ?? Array.Empty<DynamicCard>())
                        .Any(card => card.ID > 0 && !card.HasType(CardType.Flip) && card.Level <= 4)
                    : !NormalSummonUsed(header) && (await GetBotHandCardsAsync())
                        .Any(card => card.HasType(CardType.Monster) && !card.HasType(CardType.Flip) && card.Level <= 4);
                // A set Solemn Judgment stops any one problem: push more recklessly (one of their set cards counts as answered,
                // and their Torrential / Mirror Force is one we can stop).
                bool solemn = await SolemnReadyAsync();
                // Their set cards hold the push back by how likely each is to stop it (from the deck lists when there's data).
                var stops = await SetCardStopChancesAsync();
                if (solemn && stops.Count > 0)
                    stops.Remove(stops.Max());
                double setTrapFactor = stops.Aggregate(1.0, (acc, stop) =>
                    acc * Math.Max(0, 1 - (1 - PushSetTrapFactor) * stop / SetCardStopsPushChance));
                double chance = PushChance(clockNow, clockAfter, oppHand, freeSummonLeft, stops.Count, setTrapFactor);
                // Their big turn: likely blown out the longer it goes, so push (take more risks); a spent opponent, less.
                var (bigTurnFactor, bigTurnWhy) = await BigTurnPushFactorAsync();
                chance = Math.Clamp(chance * bigTurnFactor, 0, 1);
                // The duel's risk tolerance (later in the game, under pressure: more gambles; DeckBotBrain.Risk).
                var (riskTol, riskWhy) = await RiskToleranceAsync();
                chance = RiskScaled(chance, riskTol);
                bigTurnWhy += $", {riskWhy}";
                if (solemn)
                    chance = Math.Min(1, chance * SolemnPushBoost);
                if (spares > 0)
                    chance = Math.Min(1, chance * Math.Pow(SparePushBoost, spares));
                // Torrential Tribute punishes adding a monster to an existing board, and Mirror Force a second attacker:
                // the more we'd lose, the less we push.
                // (A flipped Ryko doesn't count: losing it to either costs nothing.)
                bool overextends = !solemn && newAtk > 0 && botMonsters.Any(card => !IsSpentFlipMonster(card)) && TorrentialChance > 0;
                // Less feared when pressing (the risk tolerance above its base: up to 60% less, as in the search; user, 2026-11-16).
                double fear = 1 - Search.MctsEvaluator.RiskDiscountMax * RiskPressNow;
                if (overextends)
                    chance *= 1 - TorrentialChance * fear;
                bool secondAttacker = !solemn && newAtk > 0 && botMonsters.Any(card => card.IsFaceup() && card.IsAttack() && !IsSpentFlipMonster(card)) && MirrorForceChance > 0;
                if (secondAttacker)
                    chance *= 1 - MirrorForceChance * fear;
                double roll = RollAt;
                string clockText = $"{BoardAssessment.Describe(clockNow)} -> {BoardAssessment.Describe(clockAfter)}";
                string rollText = $"push? chance {chance:P0} (clock {clockText}, opp hand {oppHand}, their set S/T {setTraps}{(opponentOpen ? " (open: speed first)" : "")}" +
                                  $"{(freeSummonLeft ? ", normal summon unused" : "")}" +
                                  $"{(overextends ? $", Torrential chance {TorrentialChance:P0} with {botMonsters.Count} monster(s) already out" : "")}" +
                                  $"{(secondAttacker ? $", Mirror Force chance {MirrorForceChance:P0} with another attacker out" : "")}" +
                                  $"{(solemn ? ", our set Solemn Judgment stops one problem: push harder" : "")}" +
                                  $", {bigTurnWhy}" +
                                  $"{(OpponentDeckPredictor.HasListData && stops.Count > 0 ? $", their set cards stop it {string.Join("/", stops.Select(s => s.ToString("P0")))}" : "")}" +
                                  $"{spareText}) rolled {roll:0.00}";
                // No roll (user, 2026-11-17): pushed at an even chance or more; the search's lines pay HoldSpend in proportion to the
                // chance of holding either way (JustifySpendAsync).
                _rollChance = 1 - chance;
                if (roll < chance)
                    return $"{what}: {rollText} -> push";
                StapleNote(context, $"hold {what}: {rollText} -> hold");
                return null;
            }

            StapleNote(context, $"hold {what}: " +
                          (clockNow == BoardAssessment.NoClock ? "no clock yet" : $"opponent already on a {BoardAssessment.Describe(clockNow)}") +
                          $"{(threats.Count == 0 ? ", no big threat" : $", doesn't answer {BoardAssessment.DescribeThreats(threats)}")}" +
                          $"{(smallThreats.Count > 0 ? $" (small threats only: {BoardAssessment.DescribeThreats(smallThreats)})" : "")}" +
                          $", not lethal (after: {BoardAssessment.Describe(clockAfter)}){spareText}");
            return null;
        }

        // One roll per turn and per backrow size for a lethal push into set cards (the main phase asks repeatedly).
        private (int Turn, int Chance) _pushRollKey = (-1, -1);

        /// <summary>
        /// Turns until they kill us if nothing changes: their face-up board against our monsters as blockers, or 1 if
        /// their board plus hand is likely lethal next turn (low life points against a big hand).
        /// </summary>
        protected async Task<int> TheirClockOnUsAsync()
        {
            var enemy = await GetEnemyMonsterCardsAsync();
            var theirAttack = enemy.Where(card => card.IsFaceup()).Select(card => card.Attack);
            var ourBlockers = BoardAssessment.Defenders(await GetBotMonsterCardsAsync());
            int lp = await CurrentField.GetBotLifePoints();
            int clock = BoardAssessment.TurnsToWin(theirAttack, ourBlockers, lp);
            // Their hand only counts once our life points are really low (a big hand at 8000 LP is no race).
            if (lp <= RaceHandLifePoints && (await OpponentLethalRiskAsync(enemy)).LethalChance >= LikelyLethalChance)
                return 1;
            return clock;
        }

        /// <summary>
        /// The go-chance for a lethal push into set cards. Normally the chance it gets through. When we're well ahead
        /// (our current board already has them on a quick clock, and they have no real clock on us), holding back very
        /// likely wins next turn anyway, while running into a Torrential / Mirror Force swings the game: then the push
        /// needs much better odds (through ^ AheadPushExponent, e.g. 65% -> 27%). Returns the chance and a note.
        /// </summary>
        protected async Task<(double Chance, string Note)> LethalPushChanceAsync(double through, int clockNow)
        {
            if (through >= 1)
                return (through, "");
            double chance = through;
            string note = "";
            int theirClock = await TheirClockOnUsAsync();
            if (BoardAssessment.SpeedOf(clockNow) == ClockSpeed.Quick && (theirClock == BoardAssessment.NoClock || theirClock > 2))
            {
                chance = Math.Pow(through, AheadPushExponent);
                note = $"; we're well ahead (already a {BoardAssessment.Describe(clockNow)}, " +
                       $"{(theirClock == BoardAssessment.NoClock ? "no clock on us" : $"their clock on us {theirClock} turns")}), " +
                       $"so holding back costs little: go chance {through:P0} -> {chance:P0}";
            }
            // The duel's risk tolerance (DeckBotBrain.Risk).
            var (riskTol, riskWhy) = await RiskToleranceAsync();
            if (Math.Abs(riskTol - 0.5) > 0.01)
            {
                double beforeRisk = chance;
                chance = RiskScaled(chance, riskTol);
                note += $"; {riskWhy}: go chance {beforeRisk:P0} -> {chance:P0}";
            }
            // Their big turn: the likelier it is, the more waiting costs, so take the risk more often.
            var (factor, bigTurnWhy) = await BigTurnPushFactorAsync();
            if (factor != 1)
            {
                double before = chance;
                chance = Math.Clamp(chance * factor, 0, 1);
                note += $"; {bigTurnWhy}: go chance {before:P0} -> {chance:P0}";
            }
            return (chance, note);
        }

        /// <summary>
        /// The chance they have a big turn next turn that blows us out: how explosive their deck is (from the deck lists
        /// their cards fit: Combo / Ramp high, Control low) times what they have to do it with: cards in hand, and graveyard
        /// cards that make a play from there (Vayu, Plaguespreader, Fortress; cards that do nothing there count 0). An
        /// explosive deck with no cards does little; a medium deck with a full hand can do a lot.
        /// threat = 1 - e^(-explosiveness x (hand x BigTurnPerHandCard + graveyard plays x BigTurnPerGravePlay)).
        /// </summary>
        protected async Task<(double Threat, string Why)> BigTurnThreatAsync()
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            int hand = enemy.Hand?.Length ?? 0;
            // Graveyard quality, not quantity: only cards that make a play from there (Vayu, Plaguespreader, Fortress...).
            int gravePlays = 0;
            foreach (int id in enemy.Grave ?? Array.Empty<int>())
                if (id > 0 && await CardDatabase.GetAsync(id) is DBCardInfo info && EffectEvaluator.HasGraveyardPlay(info.CardText ?? ""))
                    gravePlays++;
            double explosive = OpponentDeck.Explosiveness() ?? DefaultExplosiveness;
            double threat = 1 - Math.Exp(-explosive * (hand * BigTurnPerHandCard + gravePlays * BigTurnPerGravePlay));
            return (threat, $"their big-turn chance {threat:P0} (deck explosiveness {explosive:0.00}, {hand} in hand, " +
                            $"{gravePlays} graveyard card(s) that make plays)");
        }

        /// <summary>
        /// Push multiplier from their big-turn chance: 1 + BigTurnPushWeight x (threat - BigTurnBaseline), at least
        /// BigTurnPushMinFactor. Likely blown out the longer it goes: push (take risks); a spent or slow opponent: a little
        /// less.
        /// </summary>
        protected async Task<(double Factor, string Why)> BigTurnPushFactorAsync()
        {
            var (threat, why) = await BigTurnThreatAsync();
            double factor = Math.Max(BigTurnPushMinFactor, 1 + BigTurnPushWeight * (threat - BigTurnBaseline));
            return (factor, $"{why}: push x{factor:0.00}");
        }

        /// <summary>Explosiveness assumed without deck-list data. Tune from logs.</summary>
        [Tunable("Explosiveness assumed without deck-list data. Tune from logs.")]
        public double DefaultExplosiveness { get; set; } = 0.5;
        /// <summary>Big-turn weight of each card in their hand. Tune from logs.</summary>
        [Tunable("Big-turn weight of each card in their hand. Tune from logs.")]
        public double BigTurnPerHandCard { get; set; } = 0.35;
        /// <summary>
        /// Big-turn weight of each card in their graveyard that makes a play from there (Vayu, Fortress): close to a card
        /// in hand. Cards that do nothing there count 0. Tune from logs.
        /// </summary>
        [Tunable("Big-turn weight of each card in their graveyard that makes a play from there (Vayu, Fortress): close to a card in hand. Cards that do nothing there count 0. Tune from logs.")]
        public double BigTurnPerGravePlay { get; set; } = 0.3;
        /// <summary>Big-turn chance at which pushing is neither boosted nor damped. Tune from logs.</summary>
        [Tunable("Big-turn chance at which pushing is neither boosted nor damped. Tune from logs.")]
        public double BigTurnBaseline { get; set; } = 0.4;
        /// <summary>How strongly their big-turn chance moves the push chance. Tune from logs.</summary>
        [Tunable("How strongly their big-turn chance moves the push chance. Tune from logs.")]
        public double BigTurnPushWeight { get; set; } = 1.5;
        /// <summary>The push chance is never damped below this factor by a low big-turn chance. Tune from logs.</summary>
        [Tunable("The push chance is never damped below this factor by a low big-turn chance. Tune from logs.")]
        public double BigTurnPushMinFactor { get; set; } = 0.8;

        protected Task<bool> PushRollAsync(BotStateHeader header, double through)
        {
            var key = (header.CurrentTurn, (int)Math.Round(through * 100));
            if (_pushRollKey != key)
            {
                _pushRollKey = key;
                _pushRollGo = RollAt < through;
            }
            return Task.FromResult(_pushRollGo);
        }

        /// <summary>
        /// Whether waiting is likely to lose: the opponent's face-up attackers alone would beat our life points next
        /// turn through our current blockers (each blocker soaks up one attack).
        /// </summary>
        protected async Task<bool> WeLoseNextTurnAsync()
        {
            var lethal = await OpponentLethalRiskAsync(await GetEnemyMonsterCardsAsync());
            return lethal.LethalChance >= LikelyLethalChance;
        }

        /// <summary>
        /// Probability of spending a resource just to put the opponent on (or speed up) a clock:
        ///  - base: how much faster the clock gets (7 -> 3 turns is a big gain, 4 -> 3 a small one),
        ///  - x0.8 per card in the opponent's hand (a big hand means threats are coming, so save answers),
        ///  - x0.5 if the Normal Summon is still unused (develop for free first),
        ///  - x1.5 if there is no clock at all yet,
        ///  - x0.7 per set Spell/Trap they have (it can punish the push: save for a better time),
        ///  - x1.5 when they're open (no set Spell/Traps, 2 or fewer cards in hand): speed first.
        /// </summary>
        protected double PushChance(int clockNow, int clockAfter, int opponentHand, bool freeSummonLeft, int opponentSetTraps,
            double? setTrapFactor = null)
        {
            int before = clockNow == BoardAssessment.NoClock ? 10 : clockNow;
            int after = clockAfter == BoardAssessment.NoClock ? 10 : clockAfter;
            double chance = Math.Clamp((before - after) / PushClockTurnsForCertain, 0, 1);
            chance *= Math.Pow(PushHandFactorPerCard, opponentHand);
            // Their set cards: PushSetTrapFactor each for an average card, scaled by how likely each is to stop the push.
            chance *= setTrapFactor ?? Math.Pow(PushSetTrapFactor, opponentSetTraps);
            if (opponentSetTraps == 0 && opponentHand <= PushOpenHandMax)
                chance *= PushOpenBoost;
            if (freeSummonLeft)
                chance *= PushFreeSummonFactor;
            if (clockNow == BoardAssessment.NoClock)
                chance *= PushNoClockBoost;
            // No artificial floor/ceiling: a push can be always right or always wrong.
            return Math.Clamp(chance, 0, 1);
        }

        /// <summary>
        /// If the opponent is already on a clock of this many turns or fewer, hold resources unless they win now or answer a threat.
        /// 2 = only hold on a quick clock, 5 = also hold on a long clock (default), larger = hold on very long clocks too.
        /// </summary>
        [Tunable("If the opponent is already on a clock of this many turns or fewer, hold resources unless they win now or answer a threat. 2 = only hold on a quick clock, 5 = also hold on a long clock (default), larger = hold on very long clocks too.")]
        public int HoldWhenClockAtMost { get; set; } = 5;

        /// <summary>Clock turns each spare resource copy lowers the hold threshold (<see cref="HoldWhenClockAtMost"/>) by.</summary>
        [Tunable("Clock turns each spare resource copy lowers the hold threshold (HoldWhenClockAtMost) by.")]
        public int SpareClockStep { get; set; } = 2;

        /// <summary>Push chance multiplier per spare resource copy.</summary>
        [Tunable("Push chance multiplier per spare resource copy.")]
        public double SparePushBoost { get; set; } = 1.5;


        protected bool _pushRollGo;

        /// <summary>At or below this many life points, their hand counts towards "they kill us next turn" for racing.</summary>
        [Tunable("At or below this many life points, their hand counts towards \"they kill us next turn\" for racing.")]
        public int RaceHandLifePoints { get; set; } = 3000;

        /// <summary>Exponent on the get-through chance for a lethal push when we're well ahead anyway.</summary>
        [Tunable("Exponent on the get-through chance for a lethal push when we're well ahead anyway.")]
        public double AheadPushExponent { get; set; } = 3;

        // Weights for PushChance; tune these from the logged rolls.
        [Tunable("")]
        public double PushHandFactorPerCard { get; set; } = 0.8;

        [Tunable("")]
        public double PushFreeSummonFactor { get; set; } = 0.5;

        [Tunable("")]
        public double PushNoClockBoost { get; set; } = 1.5;

        [Tunable("")]
        public double PushSetTrapFactor { get; set; } = 0.7;

        [Tunable("")]
        public double PushOpenBoost { get; set; } = 1.5;

        /// <summary>An opponent with no set Spell/Traps and at most this many cards in hand is "open": speed comes first.</summary>
        [Tunable("An opponent with no set Spell/Traps and at most this many cards in hand is \"open\": speed comes first.")]
        public int PushOpenHandMax { get; set; } = 2;

    }
}
