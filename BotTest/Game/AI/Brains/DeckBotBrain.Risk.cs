using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Risk tolerance (user, 2026-11-13, round-robin 33): one setting for every play with a big payoff and a big possible
    /// loss — attacking into unknown set cards, summoning more monsters into a possible Torrential, more set cards into a
    /// possible Heavy Storm, leaving spare attackers up into a possible Mirror Force. "Later in the game more risks should
    /// be taken in general, as you're also more likely to lose next turn."
    ///
    /// 0 is play safe, 1 take any gamble; 0.5 is neutral (the rules' own chances unchanged). It rises with the turn count,
    /// when we're under pressure (their clock on us, our low LP), and when we're behind on cards; it falls when we're
    /// ahead (holding back already wins). When we lose next turn anyway it's 1.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Risk tolerance at the start of the duel. Tune from logs.</summary>
        [Tunable("Risk tolerance at the start of the duel. Tune from logs.")]
        public double RiskBase { get; set; } = 0.3;

        [Tunable("Risk tolerance added when their clock on us is 2 turns.")]
        public static double RiskTwoTurnClock = 0.2;
        [Tunable("Risk tolerance added as our life points fall: this much at 0 LP, scaled from 8000.")]
        public static double RiskLowLpMax = 0.5;
        [Tunable("Behind on cards (count or value): the curve starts at this (x (growth^deficit - 1)).")]
        public static double RiskBehindStart = 0.03;
        [Tunable("Behind on cards: how fast the added risk tolerance grows per card behind (2 = doubles each card).")]
        public static double RiskBehindGrowth = 2;
        [Tunable("Behind on cards: the most it adds to risk tolerance.")]
        public static double RiskBehindMax = 0.6;
        [Tunable("Risk tolerance taken off when we're 2+ cards ahead.")]
        public static double RiskAheadDrop = 0.1;
        [Tunable("A stall (no LP change): risk tolerance added per turn past RiskStallFreeTurns.")]
        public static double RiskStallPerTurn = 0.1;
        [Tunable("A stall: turns without an LP change before it starts adding risk tolerance.")]
        public static int RiskStallFreeTurns = 2;
        [Tunable("A stall: the most it adds to risk tolerance.")]
        public static double RiskStallMax = 0.5;
        [Tunable("Deck race: below this many cards in our deck, risk tolerance starts rising.")]
        public static int RiskDeckRaceFrom = 15;
        [Tunable("Deck race: the most it adds (at an empty deck, when ours is the smaller deck).")]
        public static double RiskDeckRaceMax = 0.4;
        [Tunable("Deck race: the share that applies when their deck is no bigger than ours.")]
        public static double RiskDeckRaceAheadShare = 0.3;
        /// <summary>Risk tolerance added per turn after turn 2, up to RiskStageMax.</summary>
        [Tunable("Risk tolerance added per turn after turn 2, up to RiskStageMax.")]
        public double RiskPerTurn { get; set; } = 0.03;
        [Tunable("")]
        public double RiskStageMax { get; set; } = 0.3;

        private int _prepareCount;
        private (int Key, double Value, string Why)? _risk;

        /// <summary>The last computed risk tolerance (for synchronous checks; refreshed by RiskToleranceAsync).</summary>
        protected double RiskNow { get; private set; } = 0.5;

        protected async Task<(double Value, string Why)> RiskToleranceAsync()
        {
            if (_risk is { } cached && cached.Key == _prepareCount)
                return (cached.Value, cached.Why);
            int turn = _currentHeader.CurrentTurn;
            var parts = new List<string>();
            double tol = RiskBase;
            double stage = Math.Min(RiskStageMax, Math.Max(0, turn - 2) * RiskPerTurn);
            if (stage > 0)
                parts.Add($"turn {turn} +{stage:0.00}");
            tol += stage;
            if (await WeLoseNextTurnAsync())
            {
                tol = 1;
                parts.Add("we lose next turn otherwise");
            }
            else
            {
                if (await TheirClockOnUsAsync() == 2)
                {
                    tol += RiskTwoTurnClock;
                    parts.Add($"their clock on us is 2 turns +{RiskTwoTurnClock:0.00}");
                }
                // Our life points, gradually (S13: "as their life points get lower they should naturally be more aggressive"): up to +0.5.
                int lp = await CurrentField.GetBotLifePoints();
                double lpPart = RiskLowLpMax * Math.Clamp((8000 - lp) / 8000.0, 0, 1);
                if (lpPart > 0.01)
                {
                    tol += lpPart;
                    parts.Add($"our LP {lp} +{lpPart:0.00}");
                }
                var field = await CurrentField.GetBotField();
                int ours = (await GetBotHandCardsAsync()).Count + (await GetBotMonsterCardsAsync()).Count(card => card.ID > 0) +
                           (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0);
                int theirs = (field.EnemySide.Hand?.Length ?? 0) + (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count;
                // Card value as well as count (B5, user 2026-11-16): hand cards 1, set cards 0.5, face-up monsters by their size (the
                // search's body value: 0.6 + the ATK table), face-up spells/traps 0.3. Same curve; the larger of the two counts (one
                // deficit, not two).
                static double MonsterValue(DynamicCard m) =>
                    !m.IsFaceup() ? Search.MctsEvaluator.SetSpellBase : Search.MctsEvaluator.MonsterBase + Search.MctsEvaluator.AtkValue(m.IsAttack() ? m.Attack : Search.MctsEvaluator.DefenceShare * m.Defense);
                double ourValue = (await GetBotHandCardsAsync()).Count +
                                  (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0).Sum(MonsterValue) +
                                  (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0).Sum(card => card.IsFaceup() ? Search.MctsEvaluator.FaceUpSpellBase : Search.MctsEvaluator.SetSpellBase);
                double theirValue = (field.EnemySide.Hand?.Length ?? 0) +
                                    (await GetEnemyMonsterCardsAsync()).Sum(MonsterValue) +
                                    (await GetEnemySpellCardsAsync()).Sum(card => card.IsFaceup() ? Search.MctsEvaluator.FaceUpSpellBase : Search.MctsEvaluator.SetSpellBase);
                double Curve(double d) => Math.Min(RiskBehindMax, RiskBehindStart * (Math.Pow(RiskBehindGrowth, d) - 1));
                double countPart = theirs > ours ? Curve(theirs - ours) : 0;
                double valuePart = theirValue > ourValue ? Curve(theirValue - ourValue) : 0;
                if (countPart > 0 || valuePart > 0.005)
                {
                    // Behind: small at first, then growing fast (user, 2026-11-16: "starting small and then exponentially increasing").
                    double behind = Math.Max(countPart, valuePart);
                    tol += behind;
                    parts.Add(countPart >= valuePart ? $"behind on cards ({ours} vs {theirs}) +{behind:0.00}"
                                                     : $"behind on card value ({ourValue:0.0} vs {theirValue:0.0}) +{behind:0.00}");
                }
                else if (ours - theirs >= 2)
                {
                    tol -= RiskAheadDrop;
                    parts.Add($"ahead on cards ({ours} vs {theirs}) -{RiskAheadDrop:0.00}");
                }
            }
            // A stall and the deck race (S13: "decks should know they have to press at some point").
            var (pressure, pressureWhy) = await StallAndDeckRaceAsync();
            if (pressure > 0)
            {
                tol += pressure;
                parts.Add(pressureWhy);
            }
            tol = Math.Clamp(tol, RiskMin, 1);
            string why = $"risk tolerance {tol:0.00} (base {RiskBase:0.00}{(parts.Count > 0 ? ", " + string.Join(", ", parts) : "")})";
            _risk = (_prepareCount, tol, why);
            RiskNow = tol;
            RiskPressNow = Math.Clamp((tol - RiskBase) / (1 - RiskBase), 0, 1);
            return (tol, why);
        }

        // ------------------------------------------------------------------ desperation (CSV S13, user 2026-11-15)

        /// <summary>
        /// How far the risk tolerance stands above its base, 0-1 (refreshed by RiskToleranceAsync): how much we have to press. The search's
        /// scoring reads it (fear of their cards down by up to 60%, damage to them up to double), and so does the spend weighing (the
        /// clock under which spending is held back drops). One score, not a separate desperation (user, 2026-11-15).
        /// </summary>
        protected internal double RiskPressNow { get; private set; }

        private int _stallTurnSeen = -1, _stallTurns;
        private (int Ours, int Theirs) _stallLp = (-1, -1);

        /// <summary>
        /// Two parts of the risk tolerance (user, 2026-11-15, S13: "decks should know they have to press at some point"):
        ///  - a stall: +0.1 for every turn past the second in which neither side's life points changed, up to +0.5 (seen 2026-11-15: two
        ///    duels decked out after 8 turns of both sides passing);
        ///  - the deck race: as our deck falls under 15 cards, up to +0.4 when ours is the smaller (a stall decks us out first).
        /// </summary>
        private async Task<(double Value, string Why)> StallAndDeckRaceAsync()
        {
            var field = await CurrentField.GetBotField();
            int ourLp = field.OwnSide.LifePoints, theirLp = field.EnemySide.LifePoints;
            int turn = _currentHeader.CurrentTurn;
            if (turn != _stallTurnSeen)
            {
                _stallTurnSeen = turn;
                _stallTurns = _stallLp == (ourLp, theirLp) ? _stallTurns + 1 : 0;
                _stallLp = (ourLp, theirLp);
            }
            var parts = new List<string>();
            double stallPart = Math.Min(RiskStallMax, RiskStallPerTurn * Math.Max(0, _stallTurns - RiskStallFreeTurns));
            if (stallPart > 0)
                parts.Add($"no LP change for {_stallTurns} turns +{stallPart:0.00}");
            int ourDeck = field.OwnSide.Main?.Length ?? 40, theirDeck = field.EnemySide.Main?.Length ?? 40;
            double deckPart = ourDeck >= RiskDeckRaceFrom ? 0 : RiskDeckRaceMax * (RiskDeckRaceFrom - ourDeck) / (double)RiskDeckRaceFrom * (ourDeck < theirDeck ? 1 : RiskDeckRaceAheadShare);
            if (deckPart > 0.01)
                parts.Add($"deck {ourDeck} vs {theirDeck} +{deckPart:0.00}");
            return (stallPart + deckPart, string.Join(", ", parts));
        }

        /// <summary>A go-chance for a gamble scaled by the risk tolerance: neutral (0.5) leaves it, 0 is x0.6, 1 is x1.4.</summary>
        protected static double RiskScaled(double chance, double tolerance) => Math.Clamp(chance * (RiskScaledBase + RiskScaledSlope * tolerance), 0, 1);

        // ------------------------------------------------------------------ answers we expect to need (round-robin 4)

        /// <summary>
        /// How many monster answers (attack or summon traps, a set Ryko / battle recruiter) we expect to need on their next
        /// turn (user, 2026-11-13: "only committing the amount of answers you're going to actually expect to need next
        /// turn"; "in the early turns of the duel it's less likely that I'm going to summon two monsters at once"). Their
        /// face-up monsters our board doesn't beat, plus what they'll summon (one early, two from turn 7 with a big hand).
        /// </summary>
        protected async Task<(int Need, string Why)> AnswersNeededNextTurnAsync()
        {
            int turn = _currentHeader.CurrentTurn;
            int theirHand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup()).Select(card => Math.Max(card.Attack, card.Defense)).ToList();
            int ourBest = ours.DefaultIfEmpty(0).Max();
            int unanswered = (await GetEnemyMonsterCardsAsync()).Count(card => card.IsFaceup() && card.IsAttack() && card.Attack >= ourBest);
            int newSummons = theirHand == 0 ? 0 : turn >= 7 && theirHand >= 4 ? 2 : 1;
            int need = unanswered + newSummons;
            return (need, $"{unanswered} of their monster(s) our board doesn't stop + {newSummons} new summon(s) expected (turn {turn}, their hand {theirHand})");
        }

        /// <summary>Our set answers to monsters: set traps for attacks or summons, and face-down monsters that destroy a card or recruit when they die.</summary>
        protected async Task<(int Have, string What)> SetAnswersAsync()
        {
            var names = new List<string>();
            foreach (var card in (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0 && !card.IsFaceup()))
                if (RoleOf(card.ID) != TrapRole.None)
                    names.Add(CardNames.Get(card.ID));
            foreach (var card in (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && !card.IsFaceup()))
                if (HasFlipEffect(card) && FlipHitsACard(card.ID) || await TextMatchesAsync(card.ID, BattleRecruiterText))
                    names.Add($"set {CardNames.Get(card.ID)}");
            return (names.Count, string.Join(", ", names));
        }

        // "When this card is destroyed by battle and sent to the GY: special summon ... from your Deck" (Mystic Tomato, ...).
        private static readonly System.Text.RegularExpressions.Regex BattleRecruiterText = new(
            @"destroyed by battle[^.]*special summon[^.]*from your deck",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // ------------------------------------------------------------------ spare attackers (round-robin 33, 43)

        /// <summary>
        /// What our attack this turn needs: lethal (the fewest attackers, strongest first, whose damage through their
        /// defenders reaches their LP), else every attacker that has a face-up target worth attacking. The rest are spares:
        /// against unknown set cards they only add to what a Mirror Force / Torrential takes.
        /// </summary>
        protected async Task<(List<DynamicCard> Needed, string Why)> AttackersNeededAsync(List<DynamicCard> attackers)
        {
            var enemy = await GetEnemyMonsterCardsAsync();
            var defenders = BoardAssessment.Defenders(enemy, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var ordered = attackers.OrderByDescending(EffectiveAttack).ToList();
            if (BoardAssessment.TurnsToWin(ordered.Select(EffectiveAttack), defenders, oppLp) == 1)
            {
                for (int n = 1; n <= ordered.Count; n++)
                    if (BoardAssessment.TurnsToWin(ordered.Take(n).Select(EffectiveAttack), defenders, oppLp) == 1)
                        return (ordered.Take(n).ToList(), $"lethal needs {n} attacker(s): {string.Join(", ", ordered.Take(n).Select(c => CardNames.Get(c.ID)))}");
            }
            var needed = attackers.Where(own => enemy.Any(target => target.IsFaceup() && IsWorthAttacking(own, target))).ToList();
            // An open field: the attackers that change the clock are the attack.
            if (enemy.Count == 0)
                needed = attackers.Where(own => !IsWeak(own) || ChangesClock(own, attackers)).ToList();
            return (needed, needed.Count == 0 ? "no attack worth making" : $"attacks worth making: {string.Join(", ", needed.Select(c => CardNames.Get(c.ID)))}");
        }

        /// <summary>
        /// Round-robin 33 (user, 2026-11-13: "yes, also true with even one set card, but even more so with 2"): a free extra
        /// body (Bora next to a Blackwing...) the attack this turn doesn't need waits in hand while they have unknown set
        /// cards: it only gives a Mirror Force / Torrential one more card. A roll from their set cards' stop chances and the
        /// risk tolerance. Returns the reason to hold it, or null to summon.
        /// </summary>
        protected async Task<string?> SpareBodyHoldAsync(DecisionContext<IdleCmdData> context, int cardId, int attack)
        {
            var stops = await SetCardStopChancesAsync();
            if (stops.Count == 0 || (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1)
                return null;
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            var enemy = await GetEnemyMonsterCardsAsync();
            var defenders = BoardAssessment.Defenders(enemy, OpponentMemory);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var atks = attackers.Select(EffectiveAttack).ToList();
            // Needed: it makes lethal, or it beats a monster none of ours already beats.
            if (BoardAssessment.TurnsToWin(atks, defenders, oppLp) > 1 && BoardAssessment.TurnsToWin(atks.Append(attack), defenders, oppLp) == 1)
                return null;
            int ourBest = atks.DefaultIfEmpty(0).Max();
            if (enemy.Any(card => card.IsFaceup() && card.GetRealPower() < attack && card.GetRealPower() >= ourBest))
                return null;
            if (attackers.Count == 0)
                return null; // the first attacker isn't a spare
            var (tol, tolWhy) = await RiskToleranceAsync();
            double stop = 1 - stops.Aggregate(1.0, (acc, s) => acc * (1 - s));
            double hold = Math.Clamp(stop * (RiskHoldCeiling - tol), 0, 1);
            double roll = RollAt;
            string text = $"{CardNames.Get(cardId)} isn't needed for this turn's attack (no lethal, nothing only it beats) and their " +
                          $"{stops.Count} set card(s) stop a push {stop:P0}; {tolWhy}: hold chance {hold:P0}, rolled {roll:0.00}";
            // No roll (user, 2026-11-17): the brain holds at an even chance or more; the search's lines pay the cost in proportion either way.
            if (roll < hold && Rule(DeckRules.SpareBodyWaits))
            {
                _rollChance = hold; // the caller's Hold pays it in proportion
                return text;
            }
            HoldScaled(cardId, null, DeckRules.SpareBodyWaits, hold);
            context.Bot.LogNote($"deck brain: {text} -> summon anyway");
            return null;
        }

        /// <summary>
        /// Round-robin 43 (user, 2026-11-13: "we definitely already discussed this ... this should be a rule either way"):
        /// before the Battle Phase, against a likely Mirror Force, the monsters the attack doesn't need go to defence first
        /// (when they can change position), so Mirror Force only takes the attackers. The shared defence switch only ran
        /// late in the fallback, so a deck brain going straight to battle for lethal never reached it. Returns the change,
        /// or null to go to battle as chosen.
        /// </summary>
        protected async Task<IdleCmdResponse?> ShelterBeforeBattleAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || MirrorForceChance <= 0)
                return null;
            var (tol, tolWhy) = await RiskToleranceAsync();
            double threshold = ShelterMirrorChance * (ShelterRiskOffset + tol);
            if (MirrorForceChance < threshold)
                return null;
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            if (attackers.Count < 2)
                return null;
            var (needed, neededWhy) = await AttackersNeededAsync(attackers);
            if (needed.Count == 0)
                return null;
            var spare = (state.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsFaceup() && card.IsAttack() && !needed.Any(n => SameZone(n, card)) && !IsExpendable(card))
                .OrderByDescending(card => card.Attack).FirstOrDefault();
            if (spare.ID <= 0 || !Rule(DeckRules.ShelterSparesFromMirror))
                return null;
            context.Bot.LogNote($"deck brain: {CardNames.Get(spare.ID)} to defence before the Battle Phase: {neededWhy}, and it isn't one of them; " +
                                $"Mirror Force chance {MirrorForceChance:P0} (shelter from {threshold:P0}; {tolWhy}), so it only takes the attackers");
            return spare.Reposition();
        }

        /// <summary>
        /// Round-robin 9 (user, 2026-11-13: yes): before a key monster attacks (an Extra Deck monster, or a strong one we can't
        /// easily replace), our set MST goes at their freshest set card, one that hasn't stayed down through an attack of ours
        /// yet (a Mirror Force / D-Prison would have gone off). Returns the activation, or null.
        /// </summary>
        protected async Task<IdleCmdResponse?> MstBeforeKeyAttackAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase)
                return null;
            var mst = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.IsCode(MysticalSpaceTyphoonId) && card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
            if (mst.ID <= 0)
                return null;
            var key = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                                                                      (card.IsExtraCard() || !IsExpendable(card) && card.Attack >= StrongMonsterAttack))
                .OrderByDescending(card => card.Attack).FirstOrDefault();
            if (key.ID <= 0)
                return null;
            var fresh = (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup() &&
                                                                      !(_clearedSetZones.TryGetValue(card.CurrentCoordinates.Sequence, out var e) && (e.NotMirror || e.NotDPrison)))
                .OrderByDescending(card => OpponentDeck.StopChance(card.CurrentCoordinates.Sequence) ?? SetCardStopsPushChance).FirstOrDefault();
            if (fresh.ID == 0 && fresh.CurrentCoordinates.Location == 0)
                return null;
            if (!Rule(DeckRules.MstBeforeKeyAttack))
                return null;
            PlanTarget(context.Header.CurrentTurn, fresh, theirs: true);
            _lastStapleActivated = MysticalSpaceTyphoonId;
            context.Bot.LogNote($"deck brain: activate our set Mystical Space Typhoon on their set card in S{fresh.CurrentCoordinates.Sequence + 1} before " +
                                $"{CardNames.Get(key.ID)} attacks: it hasn't stayed down through an attack yet (could be Mirror Force / D-Prison)");
            return mst.MainActivateFromIndex();
        }

        /// <summary>
        /// Round-robin 11 (user, 2026-11-13): "if you expect a Ryko, try to get rid of it before summoning something
        /// non-expendable ... that doesn't mean attack it and then summon the monster in MP2: hold the big monster for next turn
        /// ... unless you really need it on the field to protect yourself from lethal ... medium sized weight". A roll: the hold
        /// chance is the face-down's Ryko chance times RykoHoldWeight, scaled down as the risk tolerance rises. Not for a
        /// summon that wins (a lethal push), answers a face-up attacker that beats our board, or when we lose next turn.
        /// Returns why to hold, or null.
        /// </summary>
        protected async Task<string?> RykoBeforeSummonAsync(DecisionContext<IdleCmdData> context, DynamicCard summon)
        {
            if (summon.ID <= 0 || IsExpendable(summon) || (summon.Attack < StrongMonsterAttack && !summon.IsExtraCard()))
                return null;
            var enemy = await GetEnemyMonsterCardsAsync();
            var ryko = enemy.Where(card => !card.IsFaceup()).OrderByDescending(FaceDownRykoChance).FirstOrDefault();
            if (ryko.CurrentCoordinates.Location == 0)
                return null;
            double chance = FaceDownRykoChance(ryko);
            if (chance < RykoPlayAroundChance || LethalPushNow || _pendingLethalPush is { } pendingPush && pendingPush.Turn == context.Header.CurrentTurn || await WeLoseNextTurnAsync())
                return null;
            int ourBest = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup()).Select(EffectiveAttack).DefaultIfEmpty(0).Max();
            if (enemy.Any(card => card.IsFaceup() && card.IsAttack() && card.Attack >= ourBest))
                return null; // it answers a monster of theirs: needed now
            var (tol, tolWhy) = await RiskToleranceAsync();
            double hold = Math.Clamp(chance * RykoHoldWeight * (RiskHoldCeiling - tol), 0, 1);
            // Laid out as a table on the page (user, 2026-11-17: "this is unreadable, please lay it out as a table").
            string text = $"hold {CardNames.Get(summon.ID)} for next turn rather than give Ryko a target" +
                          $" || their set monster: Ryko {chance:P0} ({FaceDownRykoWhy(ryko)})" +
                          $" || hold strength: {hold:P0} (Ryko chance x weight {RykoHoldWeight:0.##} x ({RiskHoldCeiling:0.##} - risk tolerance))" +
                          $" || risk tolerance: {tolWhy}" +
                          $" || why held: the brain holds from 50%; the search's lines pay {hold:P0} of the hold cost";
            // No roll (user, 2026-11-17): the brain holds at an even chance or more; the search's lines pay the cost in proportion either way.
            if (hold > RollAt)
            {
                _rollChance = hold; // the caller's Hold pays it in proportion
                return text;
            }
            HoldScaled(summon.ID, null, DeckRules.RykoHoldsBigSummon, hold);
            return null;
        }

        /// <summary>Weight of the Ryko hold (medium: user, "don't obsess over this ryko rule"). Tune from logs.</summary>
        [Tunable("Weight of the Ryko hold (medium: user, \"don't obsess over this ryko rule\"). Tune from logs.")]
        public double RykoHoldWeight { get; set; } = 0.5;

        /// <summary>Mirror Force chance from which spare attackers go to defence before battle, at neutral risk tolerance. Tune from logs.</summary>
        [Tunable("Mirror Force chance from which spare attackers go to defence before battle, at neutral risk tolerance. Tune from logs.")]
        public double ShelterMirrorChance { get; set; } = 0.15;

        // ------------------------------------------------------------------ low life points (round-robin 3, 16)

        /// <summary>A typical attacker their hand can bring next turn (a Level 4 beater). Tune from logs.</summary>
        [Tunable("A typical attacker their hand can bring next turn (a Level 4 beater). Tune from logs.")]
        public int TypicalHandAttacker { get; set; } = 1800;

        /// <summary>
        /// Round-robin 3 / 16 (and the 2026-11-13 tests: Armageddon Knight left in attack position at 600 LP against Fortress,
        /// Substitoad summoned in attack position at 100 LP): a monster of ours in attack position with <paramref name="attack"/>
        /// ATK is a hole their attacker kills us through when the difference reaches our LP. Their face-up attackers count, and
        /// a typical attacker from their hand when they have cards. Returns why, or null.
        /// </summary>
        protected async Task<string?> LowLpExposureAsync(int attack) =>
            await LowLpExposureLimitAsync() is { } limit && attack <= limit.MaxAttack ? $"{limit.Why} ({attack} ATK)" : null;

        /// <summary>
        /// The highest ATK an attack-position monster of ours can have and still be a lethal hole (see LowLpExposureAsync),
        /// or null when none is (their best attacker's ATK minus our LP is below 0).
        /// </summary>
        protected async Task<(int MaxAttack, string Why)?> LowLpExposureLimitAsync()
        {
            int lp = await CurrentField.GetBotLifePoints();
            var (threat, who) = await TheirNextAttackerAsync();
            if (threat - lp < 0)
                return null;
            return (threat - lp, $"at {lp} LP, {who} attacking a monster of ours in attack position with {threat - lp} ATK or less is lethal");
        }

        /// <summary>Chance from which a monster their deck can bring next turn counts as "they'll have it" for the exposure check. Tune from logs.</summary>
        [Tunable("Chance from which a monster their deck can bring next turn counts as \"they'll have it\" for the exposure check. Tune from logs.")]
        public double LikelyAttackerChance { get; set; } = 0.4;

        /// <summary>
        /// Their strongest likely attacker next turn (user, 2026-11-13, refining round-robin 3: not a flat 1800): the biggest of
        ///  - their face-up attack-position monsters;
        ///  - known monsters in their hand (Level 4 or lower, or 5-6 with a tribute to hand);
        ///  - graveyard monsters that bring themselves back (Machina Fortress, with a hand card to discard), and Vayu with a
        ///    non-Tuner partner (a Blackwing synchro);
        ///  - the strongest normal summon their remaining deck gives them with at least LikelyAttackerChance (hand plus the draw;
        ///    a Level 5-6 counted when they'll have a tribute: a monster on their field, or Treeborn Frog in their graveyard, which
        ///    comes back for a Monarch). Without deck data, a typical TypicalHandAttacker when they have cards.
        /// </summary>
        protected async Task<(int Attack, string Who)> TheirNextAttackerAsync()
        {
            var enemyField = await GetEnemyMonsterCardsAsync();
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            int theirHand = enemy.Hand?.Length ?? 0;
            var grave = enemy.Grave ?? Array.Empty<int>();
            bool tribute = enemyField.Count > 0 || grave.Any(id => Search.SearchHost.CanonicalId(id) is var c && (c == TreebornFrogId || id == TreebornFrogId));
            int best = 0;
            string who = "nothing";
            void Consider(int atk, string what)
            {
                if (atk > best)
                {
                    best = atk;
                    who = what;
                }
            }
            foreach (var card in enemyField.Where(card => card.IsFaceup() && card.IsAttack()))
                Consider(card.Attack, $"their {CardNames.Get(card.ID)} ({card.Attack})");
            foreach (int id in OpponentDeck.KnownHand())
                if (await CardDatabase.GetAsync(id) is DBCardInfo h && (h.Type & (int)CardType.Monster) != 0 && (h.Level <= 4 || tribute && h.Level <= 6))
                    Consider(h.Attack, $"{CardNames.Get(id)} known in their hand ({h.Attack})");
            var partnerLevels = new List<(int Level, int Id)>();
            int vayuLevel = 0;
            foreach (int id in grave)
            {
                if (await CardDatabase.GetAsync(id) is not DBCardInfo g || (g.Type & (int)CardType.Monster) == 0)
                    continue;
                string text = g.CardText ?? "";
                if (CardNames.Get(id).Contains("Vayu") && System.Text.RegularExpressions.Regex.IsMatch(text, @"Synchro", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    vayuLevel = g.Level;
                else if ((g.Type & (int)CardType.Tuner) == 0 && (g.Type & (int)CardType.Synchro) == 0)
                    partnerLevels.Add((g.Level, id));
                if (theirHand > 0 && System.Text.RegularExpressions.Regex.IsMatch(text, @"special summon this card[^.]*(from your (hand or )?(GY|Graveyard)|\(from your hand or (GY|Graveyard)\))",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    Consider(g.Attack, $"{CardNames.Get(id)} back from their graveyard ({g.Attack})");
            }
            // Vayu's synchro: its Level is Vayu's plus the banished partner's, so each partner in their graveyard names the
            // strongest Blackwing synchro of that Level in their (likely) Extra Deck (user, 2026-11-13: "you know from the
            // partner how much attack it could get to").
            if (vayuLevel > 0 && partnerLevels.Count > 0)
            {
                var synchros = new List<(int Level, int Attack, int Id)>();
                foreach (int id in OpponentDeck.LikelyCards(0.05))
                    if (await CardDatabase.GetAsync(id) is DBCardInfo x && (x.Type & (int)CardType.Synchro) != 0 && CardNames.Get(id).Contains("Blackwing"))
                        synchros.Add((x.Level, x.Attack, id));
                foreach (var (level, partner) in partnerLevels.Distinct())
                    if (synchros.Where(s => s.Level == vayuLevel + level).OrderByDescending(s => s.Attack).FirstOrDefault() is { Id: > 0 } topSynchro)
                        Consider(topSynchro.Attack, $"{CardNames.Get(topSynchro.Id)} ({topSynchro.Attack}) from Vayu + {CardNames.Get(partner)} in their graveyard");
                if (synchros.Count == 0)
                    Consider(VayuSynchroAttack, $"a Blackwing synchro from Vayu in their graveyard (~{VayuSynchroAttack}; their Extra Deck unknown)");
            }
            int unknown = Math.Max(0, theirHand - OpponentDeck.KnownHand().Count) + 1; // their unknown hand and the next draw
            if (OpponentDeck.AttackerChances(Search.MctsEvaluator.AttackerThresholds, unknown, AttackerStats, tribute ? 6 : 4) is { } odds)
            {
                for (int t = odds.Length - 1; t >= 0; t--)
                    if (odds[t] >= LikelyAttackerChance)
                    {
                        Consider(Search.MctsEvaluator.AttackerThresholds[t],
                            $"a {Search.MctsEvaluator.AttackerThresholds[t]}+ attacker from their deck ({odds[t]:P0} with their hand and draw{(tribute ? ", a tribute summon included" : "")})");
                        break;
                    }
            }
            else if (theirHand > 0 || enemyField.Any(card => !card.IsFaceup()))
                Consider(tribute ? Math.Max(TypicalHandAttacker, 2400) : TypicalHandAttacker,
                    tribute ? "a Monarch-sized tribute summon (Treeborn / a monster to tribute)" : $"a typical {TypicalHandAttacker} attacker from their hand");
            return (best, who);
        }

        /// <summary>A Blackwing synchro Vayu brings back (Armor Master 2500, Armed Wing 2300, Silverwind 2800). Tune from logs.</summary>
        [Tunable("A Blackwing synchro Vayu brings back (Armor Master 2500, Armed Wing 2300, Silverwind 2800). Tune from logs.")]
        public int VayuSynchroAttack { get; set; } = 2500;

        // ------------------------------------------------------------------ bleeding (round-robin 34c)

        private (int Turn, int Lp, int Monsters)? _lastOwnDecision;
        private int _bleedTurnSeen = -1;

        /// <summary>Our turns in a row that began with less LP after a turn we ended with no monster (round-robin 34c).</summary>
        protected int BleedingTurns { get; private set; }

        /// <summary>Called at each of our main-phase decisions: at a new turn, compares with how our last turn ended.</summary>
        private async Task TrackBleedingAsync(int turn)
        {
            int lp = await CurrentField.GetBotLifePoints();
            int monsters = (await GetBotMonsterCardsAsync()).Count(card => card.ID > 0);
            if (_bleedTurnSeen != turn && _lastOwnDecision is { } last && last.Turn < turn)
            {
                _bleedTurnSeen = turn;
                BleedingTurns = last.Monsters == 0 && lp < last.Lp ? BleedingTurns + 1 : 0;
            }
            if (turn < (_lastOwnDecision?.Turn ?? 0))
                BleedingTurns = 0; // a new duel
            _lastOwnDecision = (turn, lp, monsters);
        }

        /// <summary>The exposure limit for this decision (set before the shared position checks; see ShouldRepositionToDefense).</summary>
        private (int MaxAttack, string Why)? _lowLpExposure;
    }
}
