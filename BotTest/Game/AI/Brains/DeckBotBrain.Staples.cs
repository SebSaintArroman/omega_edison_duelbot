using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Staple cards almost every deck of this format plays (Gorz, Mirror Force, Torrential Tribute): how the bot
    /// uses its own copies, and how it plays around the opponent's. Mirror Force and Torrential Tribute are limited
    /// to one per deck, so once the opponent's copy is in their graveyard or banished, it can be ignored.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected const int MirrorForceId = 44095762;
        protected const int TorrentialTributeId = 53582587;
        protected const int JarOfGreedId = 83968380;
        protected const int LegacyOfYataGarasuId = 30461781;
        protected const int HeavyStormId = 19613556;
        protected const int MysticalSpaceTyphoonId = 5318639;

        /// <summary>Chance per card in the opponent's hand that it is Heavy Storm (one copy per deck). Tune from logs.</summary>
        [Tunable("Chance per card in the opponent's hand that it is Heavy Storm (one copy per deck). Tune from logs.")]
        public double HeavyStormChancePerCard { get; set; } = 0.025;

        /// <summary>Deck brains with their own main-phase Heavy Storm / MST rules set this, so the shared ones don't run.</summary>
        protected virtual bool HandlesStapleIdle => false;

        private bool _knownHeavyStormInOpponentHand;
        private int _lastStapleActivated;

        // ------------------------------------------------------------------ the opponent's Stardust Dragon (and similar)

        // "When a card or effect is activated that would destroy a card(s) on the field: ... negate the activation"
        private static readonly System.Text.RegularExpressions.Regex DestructionNegatorText = new(
            @"activated that would destroy[^.]*negate|negate the activation[^.]*would destroy",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex DestroysText = new(
            @"\bdestroy", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static async Task<bool> TextMatchesAsync(int cardId, System.Text.RegularExpressions.Regex pattern) =>
            cardId > 0 && await CardDatabase.GetAsync(cardId) is DBCardInfo info && pattern.IsMatch(info.CardText ?? "");

        /// <summary>
        /// Whether a face-up opponent monster like Stardust Dragon would just negate this destroy card. Returns the
        /// reason to hold it, or null when it's fine to activate. It's fine anyway when:
        ///  1. it isn't a destroy card, or no such monster is face-up,
        ///  2. we have another destroy effect available: this one baits the negation so the other goes through,
        ///  3. the negator has to go now: without it the opponent can't kill us next turn, or without it we have lethal
        ///     (the negation tributes it, clearing it from the field until the End Phase).
        /// </summary>
        protected async Task<string?> DestructionNegatedAsync<T>(DecisionContext<T> context, int cardId)
        {
            if (!await TextMatchesAsync(cardId, DestroysText))
                return null;
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            DynamicCard negator = default;
            foreach (var card in enemyMonsters.Where(card => card.IsFaceup() && !card.IsDisabled()))
            {
                if (await TextMatchesAsync(card.ID, DestructionNegatorText))
                {
                    negator = card;
                    break;
                }
            }
            if (negator.ID <= 0)
                return null;
            string name = CardNames.Get(negator.ID);

            // 2. Another destroy effect is available this decision: use this one as bait.
            var available = context.StateData switch
            {
                IdleCmdData idle => idle.ActivableCards ?? Array.Empty<DynamicCard>(),
                SelectChainData chain => chain.SelectableCards ?? Array.Empty<DynamicCard>(),
                _ => Array.Empty<DynamicCard>()
            };
            foreach (var other in available.Where(card => card.ID > 0 && !card.IsCode(cardId)).Select(card => card.ID).Distinct())
            {
                if (await TextMatchesAsync(other, DestroysText))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(cardId)} baits {name}'s negation so {CardNames.Get(other)} goes through");
                    return null;
                }
            }

            // 3a. It is what makes the opponent's board lethal on us.
            int ourLp = await CurrentField.GetBotLifePoints();
            int theirAttack = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack);
            int ourBlockers = (await GetBotMonsterCardsAsync()).Count;
            if (ourBlockers == 0 && theirAttack >= ourLp && theirAttack - negator.Attack < ourLp)
            {
                context.Bot.LogNote($"deck brain: activate {CardNames.Get(cardId)} into {name} anyway: we need it off the field to survive");
                return null;
            }

            // 3b. It is the only thing between us and lethal.
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(card => card.Attack).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var without = BoardAssessment.Defenders(enemyMonsters.Where(card => !SameZone(card, negator)), OpponentMemory);
            if (BoardAssessment.TurnsToWin(attackers, without, oppLp) == 1)
            {
                context.Bot.LogNote($"deck brain: activate {CardNames.Get(cardId)} into {name} anyway: with it gone we have lethal");
                return null;
            }

            string why = $"{name} would negate {CardNames.Get(cardId)} (it destroys)";
            if (!Rule(DeckRules.NoDestroyIntoNegator))
                return null;
            context.Bot.LogNote($"deck brain: hold {CardNames.Get(cardId)}: {why}");
            Hold(cardId, null, DeckRules.NoDestroyIntoNegator);
            return why;
        }

        // ------------------------------------------------------------------ the opponent's Thought Ruler Archfiend (and similar)

        // "When a Trap Card's effect that targets 1 Psychic-Type monster on the field is activated: You can pay 1000
        // Life Points; negate the activation, and if you do, destroy it."
        private static readonly System.Text.RegularExpressions.Regex TrapTargetNegatorText = new(
            @"trap card'?s? (effect )?that targets[^.]*?(?<race>\w+)-type monster[^.]*negate|trap card'?s? (effect )?that targets[^.]*negate",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex PayLifePointsText = new(
            @"pay (\d+) life points", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether a face-up opponent monster (Thought Ruler Archfiend) would negate one of our Traps that targets this
        /// monster. Its cost (1000 LP) only stops them when they can't afford it, so treat it as negated unless their
        /// life points are at or below the cost. Returns the reason to hold the trap, or null when it's fine to use.
        /// </summary>
        protected async Task<string?> TrapTargetNegatedAsync(DynamicCard target)
        {
            if (target.ID <= 0)
                return null;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            foreach (var negator in (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && !card.IsDisabled() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(negator.ID) is not DBCardInfo info)
                    continue;
                string text = info.CardText ?? "";
                var match = TrapTargetNegatorText.Match(text);
                if (!match.Success)
                    continue;
                // It protects monsters of a Type ("Psychic-Type monster"), or just itself if no Type is named.
                string race = match.Groups["race"].Value;
                bool covered = race.Length > 0
                    ? System.Enum.TryParse<CardRace>(race, ignoreCase: true, out var r) && (target.Race & (int)r) != 0
                    : SameZone(negator, target);
                if (!covered)
                    continue;
                int cost = PayLifePointsText.Match(text) is { Success: true } pay ? int.Parse(pay.Groups[1].Value) : 0;
                if (cost > 0 && oppLp <= cost)
                    return null; // they can't afford the cost
                return $"{CardNames.Get(negator.ID)} would negate a Trap that targets {CardNames.Get(target.ID)}" +
                       (cost > 0 ? $" (they just pay {cost} LP, and have {oppLp})" : "");
            }
            return null;
        }

        // ------------------------------------------------------------------ the opponent's Heavy Storm

        /// <summary>Chance the opponent holds Heavy Storm: 0 once their copy is in the GY/banished, 1 if seen in hand.</summary>
        protected async Task<double> OpponentHeavyStormChanceAsync()
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            bool gone = (enemy.Grave ?? Array.Empty<int>()).Contains(HeavyStormId) || (enemy.Banish ?? Array.Empty<int>()).Contains(HeavyStormId);
            if (gone)
            {
                _knownHeavyStormInOpponentHand = false;
                return 0;
            }
            if (_knownHeavyStormInOpponentHand)
                return 1;
            int hand = enemy.Hand?.Length ?? 0;
            // Cards they held through a turn where they'd have Stormed us are probably not Heavy Storm; cards held while
            // they put their own spells/traps out are less likely to be; new draws count fully.
            _stormClearedCards = Math.Min(_stormClearedCards, hand);
            _stormSoftClearedCards = Math.Min(_stormSoftClearedCards, hand - _stormClearedCards);
            int fresh = hand - _stormClearedCards - _stormSoftClearedCards;
            double none = Math.Pow(1 - HeavyStormChancePerCard, fresh) *
                          Math.Pow(1 - HeavyStormChancePerCard * EvidenceResidual, _stormClearedCards) *
                          Math.Pow(1 - HeavyStormChancePerCard * _stormSoftResidual, _stormSoftClearedCards);
            return 1 - none;
        }

        // ------------------------------------------------------------------ evidence: staples the opponent didn't use

        /// <summary>
        /// How much of a card's chance remains once it stayed unused when the opponent would have used it (they might
        /// have held it on purpose, so not zero).
        /// </summary>
        [Tunable("How much of a card's chance remains once it stayed unused when the opponent would have used it (they might have held it on purpose, so not zero).")]
        public double EvidenceResidual { get; set; } = 0.2;

        /// <summary>
        /// Weaker evidence: the opponent put their own spells/traps on the field, which their Heavy Storm would destroy
        /// too. Cards they held then keep this share of their Heavy Storm chance per spell/trap of theirs on the field
        /// (0.4 for one, 0.16 for two, ~0.06 for three: the more they set, the more a Storm would cost them). Tune from logs.
        /// </summary>
        [Tunable("Weaker evidence: the opponent put their own spells/traps on the field, which their Heavy Storm would destroy too. Cards they held then keep this share of their Heavy Storm chance per spell/trap of theirs on the field (0.4 for one, 0.16 for two, ~0.06 for three: the more they set, the more a Storm would cost them). Tune from logs.")]
        public double OwnBackrowResidual { get; set; } = 0.4;

        private int _stormSoftClearedCards;
        private double _stormSoftResidual = 1;

        private sealed class StapleEvidence
        {
            public bool NotMirror;
            public bool NotTorrential;
            public bool NotDPrison;
        }

        // Opponent set spell/trap zones (sequence) and what we've learned they probably aren't.
        private readonly Dictionary<int, StapleEvidence> _clearedSetZones = new();
        private int _stormClearedCards;

        // Pending checks: an attack worth a Mirror Force was declared into these set zones; resolved at our next decision.
        private HashSet<int>? _pendingMirrorZones;
        // ...and an attack worth a Dimensional Prison (a strong attacker).
        private HashSet<int>? _pendingDPrisonZones;
        private int _lastIdleTurn = -1;
        private int _lastIdleOwnMonsters = -1;
        private int _ourSetAtTurnEnd;
        private int _theirSpellsAtTurnEnd;
        private int _theirHandAtTurnEnd = -1;

        private StapleEvidence EvidenceFor(int zone) =>
            _clearedSetZones.TryGetValue(zone, out var e) ? e : _clearedSetZones[zone] = new StapleEvidence();

        /// <summary>Called when we declare an attack: if Mirror Force would be worth it for the opponent, remember their set zones.</summary>
        protected async Task NoteAttackForMirrorForceAsync(DynamicCard attacker)
        {
            // D-Prison answers one strong attacker: any attack by one is worth it for them.
            if (attacker.Attack >= StrongMonsterAttack)
            {
                var setNow = (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup()).Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
                if (setNow.Count > 0)
                    _pendingDPrisonZones = setNow;
            }
            var ourAttackers = (await GetBotMonsterCardsAsync()).Count(card => card.IsFaceup() && card.IsAttack());
            if (ourAttackers < 2 && attacker.Attack < StrongMonsterAttack)
                return; // a single small attacker: they'd likely save Mirror Force
            var zones = (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup())
                .Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
            if (zones.Count > 0)
                _pendingMirrorZones = zones;
        }

        /// <summary>
        /// Turns what happened since our last decision into evidence:
        ///  - Mirror Force: we attacked in a way they'd have answered, and the set cards are still down;
        ///  - Torrential Tribute: we summoned onto a board where Torrential would have paid off for them, and they're still down;
        ///  - Heavy Storm: their whole turn passed while we had 2+ set cards and at least 2 more than them.
        /// </summary>
        private async Task ResolveStapleEvidenceAsync<T>(DecisionContext<T> context, HashSet<int> setZones)
        {
            if (_pendingMirrorZones != null)
            {
                foreach (int zone in _pendingMirrorZones.Where(setZones.Contains))
                    EvidenceFor(zone).NotMirror = true;
                if (_pendingMirrorZones.Overlaps(setZones))
                    context.Bot.LogNote("deck brain: their set card(s) stayed down through an attack worth a Mirror Force: probably not Mirror Force");
                _pendingMirrorZones = null;
            }
            if (_pendingDPrisonZones != null)
            {
                foreach (int zone in _pendingDPrisonZones.Where(setZones.Contains))
                    EvidenceFor(zone).NotDPrison = true;
                if (_pendingDPrisonZones.Overlaps(setZones))
                    context.Bot.LogNote("deck brain: their set card(s) stayed down through a strong attacker's attack: probably not Dimensional Prison");
                _pendingDPrisonZones = null;
            }

            if (context.StateData is not IdleCmdData)
                return;

            int turn = context.Header.CurrentTurn;
            var ourMonsters = await GetBotMonsterCardsAsync();
            var theirMonsters = await GetEnemyMonsterCardsAsync();
            int ourSet = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && !card.IsFaceup());
            int theirSpells = (await GetEnemySpellCardsAsync()).Count;

            // Torrential: we summoned since the last main-phase decision this turn, onto a board worth wiping for them.
            if (turn == _lastIdleTurn && ourMonsters.Count > _lastIdleOwnMonsters && setZones.Count > 0 &&
                TorrentialWorthIt(ourMonsters, theirMonsters).Worth)
            {
                foreach (int zone in setZones)
                    EvidenceFor(zone).NotTorrential = true;
                context.Bot.LogNote("deck brain: we summoned into a board worth a Torrential for them and nothing happened: probably not Torrential");
            }

            // Heavy Storm: first main-phase decision of a new turn of ours; their turn just passed.
            if (turn != _lastIdleTurn && _lastIdleTurn >= 0 &&
                _ourSetAtTurnEnd >= 2 && _ourSetAtTurnEnd >= _theirSpellsAtTurnEnd + 2 && ourSet >= _ourSetAtTurnEnd)
            {
                int hand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                if (_theyCouldWaitAtTurnEnd)
                {
                    // Not under pressure (Grand Mole stalling, or no clock on them): holding Heavy Storm for more of our
                    // cards was a fine choice for them, so this is only weak evidence.
                    _stormSoftClearedCards = Math.Max(0, hand - _stormClearedCards);
                    _stormSoftResidual = Math.Min(_stormSoftResidual, UnpressuredStormResidual);
                    context.Bot.LogNote($"deck brain: they didn't Heavy Storm our {_ourSetAtTurnEnd} set cards, but they're under no pressure " +
                                        $"and can wait for more: their hand keeps {_stormSoftResidual:P0} of its Heavy Storm chance");
                }
                else
                {
                    _stormClearedCards = hand;
                    _stormSoftClearedCards = 0;
                    context.Bot.LogNote($"deck brain: they didn't Heavy Storm our {_ourSetAtTurnEnd} set cards: their {_stormClearedCards} card(s) in hand probably aren't Heavy Storm");
                }
            }
            // Weaker: during their turn they put more of their own spells/traps out (a Storm would hit those too).
            else if (turn != _lastIdleTurn && _lastIdleTurn >= 0 && theirSpells > _theirSpellsAtTurnEnd)
            {
                int hand = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                _stormSoftClearedCards = Math.Max(0, hand - _stormClearedCards);
                _stormSoftResidual = Math.Pow(OwnBackrowResidual, theirSpells);
                context.Bot.LogNote($"deck brain: they added {theirSpells - _theirSpellsAtTurnEnd} spell/trap(s) to their own field " +
                                    $"({theirSpells} in total): their hand keeps {_stormSoftResidual:P0} of its Heavy Storm chance");
                // Gorz too: it needs their field empty, so a Gorz holder doesn't set. The cards in their hand now keep only part
                // of their Gorz chance (cards drawn later keep the full chance).
                NoteOpponentSetForGorz(hand, theirSpells);
                context.Bot.LogNote($"deck brain: ...and of its Gorz chance {Math.Pow(GorzSetResidual, theirSpells):P0}");
            }

            // Gorz: their whole turn passed with nothing done (their field still empty) while holding a big hand that didn't
            // shrink: a deliberate pass, the way a player holding Gorz plays. Their cards become likelier Gorz.
            if (turn != _lastIdleTurn && _lastIdleTurn >= 0 && _theirHandAtTurnEnd >= 0 && theirMonsters.Count == 0 && theirSpells == 0)
            {
                int handNow = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
                if (handNow >= GorzPassHand && handNow > _theirHandAtTurnEnd)
                {
                    NoteOpponentPassedForGorz();
                    context.Bot.LogNote($"deck brain: they passed their turn with an empty field and {handNow} cards in hand (nothing played): " +
                                        $"their cards are likelier Gorz (x{GorzPassBoost:0.#})");
                }
            }

            _lastIdleTurn = turn;
            _lastIdleOwnMonsters = ourMonsters.Count;
            _ourSetAtTurnEnd = ourSet;
            _theirSpellsAtTurnEnd = theirSpells;
            _theirHandAtTurnEnd = (await CurrentField.GetBotField()).EnemySide.Hand?.Length ?? 0;
            _theyCouldWaitAtTurnEnd = TheyCanWait;
        }

        private bool _theyCouldWaitAtTurnEnd;

        /// <summary>Share of Heavy Storm chance kept when they skip a Storm while under no pressure (weak evidence).</summary>
        [Tunable("Share of Heavy Storm chance kept when they skip a Storm while under no pressure (weak evidence).")]
        public double UnpressuredStormResidual { get; set; } = 0.7;

        /// <summary>How much more the urge to hold back sets grows when they can wait (see TheyCanWait).</summary>
        [Tunable("How much more the urge to hold back sets grows when they can wait (see TheyCanWait).")]
        public double UnpressuredExposureFactor { get; set; } = 2;

        /// <summary>
        /// The opponent is under no pressure to act: a Grand Mole-type monster stalls our attacks, or our board has them
        /// on no real clock. Then they can sit on Heavy Storm (and other answers) until we've committed more.
        /// </summary>
        protected bool TheyCanWait => BouncerOnTheirField || _clockSpeed is ClockSpeed.AlmostNone or ClockSpeed.None;

        /// <summary>
        /// Set cards a Heavy Storm doesn't really cost us: they can be used in response (Jar of Greed, Legacy of
        /// Yata-Garasu) or go off straight away (Trap Dustshoot). Not MST: chained to Heavy Storm it only hits a card the Storm destroys anyway. Deck brains add their own (quick-plays, ...).
        /// </summary>
        protected virtual IReadOnlyCollection<int> StormProofSets { get; } =
            new[] { JarOfGreedId, LegacyOfYataGarasuId, TrapDustshootId };

        /// <summary>
        /// Set cards that don't count towards Heavy Storm exposure or the set cap: storm-proof sets, and bait cards
        /// (Jar of Greed, MST, Burial...), which are set to be removed (the user's rule, 2026-11-06).
        /// </summary>
        protected bool IsStormFree(int cardId) => StormProofSets.Contains(cardId) || BaitCardIds.Contains(cardId);

        protected const int TrapDustshootId = 64697231;

        /// <summary>
        /// How fast the urge to hold grows with what we already have exposed: the k-th extra set is held with chance
        /// 1 - (1 - storm chance)^(weight * k^2). With a 12% Heavy Storm chance and weight 2: the 2nd set ~23%, the 3rd
        /// ~64%, the 4th ~90%. Nobody should set four into a possible Heavy Storm. Tune from logs.
        /// </summary>
        [Tunable("How fast the urge to hold grows with what we already have exposed: the k-th extra set is held with chance 1 - (1 - storm chance)^(weight * k^2). With a 12% Heavy Storm chance and weight 2: the 2nd set ~23%, the 3rd ~64%, the 4th ~90%. Nobody should set four into a possible Heavy Storm. Tune from logs.")]
        public double StormExposureWeight { get; set; } = 2;

        /// <summary>
        /// Each card we already have set is one more Heavy Storm would take with it. The first set card is always fine,
        /// and cards Heavy Storm doesn't cost us (StormProofSets) are always fine and don't count. Each extra one is held
        /// back with a chance that grows quickly with how many we already have exposed. Returns null to set, or the
        /// reason to hold.
        /// </summary>
        protected async Task<string?> HoldSetForHeavyStormAsync(int turn, int cardId = 0)
        {
            if (cardId > 0 && IsStormFree(cardId))
                return null;
            int ourSet = (await CurrentField.GetCardsInSpellZone())
                .Count(card => card.ID > 0 && !card.IsFaceup() && !IsStormFree(card.ID));
            if (ourSet == 0)
                return null;
            // One roll per extra set per turn (the main phase asks repeatedly; re-rolling would always set eventually).
            var key = (turn, ourSet);
            if (_stormHoldRolls.TryGetValue(key, out var cached))
                return cached;
            double chance = await OpponentHeavyStormChanceAsync();
            string? hold = null;
            if (chance > 0)
            {
                bool wait = TheyCanWait;
                // Early in the duel more careful with exposure, later less (user, 2026-11-13: "maybe adjust the risk tolerance
                // for heavy storm as the game goes on").
                var (riskTol, riskWhy) = await RiskToleranceAsync();
                double weight = StormExposureWeight * (wait ? UnpressuredExposureFactor : 1) * (RiskHoldCeiling - riskTol);
                double holdChance = 1 - Math.Pow(1 - chance, weight * ourSet * ourSet);
                double roll = RollAt;
                string text = $"we already have {ourSet} exposed set card(s); Heavy Storm chance {chance:P0} ({riskWhy})" +
                              $"{(_knownHeavyStormInOpponentHand ? " (seen in their hand)" : "")}" +
                              $"{(wait ? " and they're under no pressure, so they can wait for us to commit more" : "")}" +
                              $", so hold chance {holdChance:P0}, rolled {roll:0.00}";
                hold = roll < holdChance ? $"hold it in hand: {text}" : null;
            }
            if (_stormHoldRolls.Count > 64)
                _stormHoldRolls.Clear();
            _stormHoldRolls[key] = hold;
            return hold;
        }

        private readonly Dictionary<(int Turn, int Set), string?> _stormHoldRolls = new();

        // ------------------------------------------------------------------ overlapping sets

        protected const int DimensionalPrisonId = 70342110;
        protected const int SakuretsuArmorId = 56120475;

        /// <summary>
        /// Most exposed (not storm-proof) set cards we keep at once. Above this, one Heavy Storm is a disaster. Can be
        /// exceeded only when the opponent can no longer have Heavy Storm, or the card would be discarded at the End Phase.
        /// </summary>
        [Tunable("Most exposed (not storm-proof) set cards we keep at once. Above this, one Heavy Storm is a disaster. Can be exceeded only when the opponent can no longer have Heavy Storm, or the card would be discarded at the End Phase.")]
        public int MaxExposedSets { get; set; } = 2;

        /// <summary>Which issue a trap answers. Two set traps for the same issue only give a Heavy Storm more to take.</summary>
        protected enum TrapRole { None, Attack, Summon }

        protected virtual TrapRole RoleOf(int id) => id switch
        {
            MirrorForceId or DimensionalPrisonId or SakuretsuArmorId => TrapRole.Attack,
            TorrentialTributeId or BottomlessTrapHoleId => TrapRole.Summon,
            _ => TrapRole.None,
        };

        /// <summary>Answers that deal with every attacker at once: a second attack answer next to one adds nothing.</summary>
        protected virtual bool AnswersEveryAttacker(int id) => id == MirrorForceId;

        /// <summary>
        /// Returns why setting <paramref name="cardId"/> now would be redundant or overexposed, or null to set it.
        /// Redundant: a set trap already answers the same issue. A second attack answer is still set when the opponent has
        /// more big threats than our set single-target answers cover (and no Mirror Force is set). Overexposed: we already
        /// have <see cref="MaxExposedSets"/> exposed set cards. Neither applies to storm-proof cards, when the card would be
        /// discarded at the End Phase (<paramref name="handCount"/> 7+), or (for the cap) when they can't have Heavy Storm.
        /// </summary>
        protected async Task<string?> SetRedundantAsync(int cardId, int handCount)
        {
            if (IsStormFree(cardId) || handCount > 6)
                return null;
            var set = (await CurrentField.GetCardsInSpellZone())
                .Where(card => card.ID > 0 && !card.IsFaceup() && !IsStormFree(card.ID)).ToList();
            var role = RoleOf(cardId);
            // Only the answers we expect to need next turn (user, 2026-11-13, round-robin 4: Mirror Force + Bottomless + Jar on
            // turn 2, all taken by Heavy Storm; "I'd have set one real trap and one jar of greed and stopped there ... maybe even
            // 0 real traps if I'd set a ryko or a battle recruiter"). Later in the duel (higher risk tolerance) one spare.
            if (role != TrapRole.None)
            {
                var (have, haveWhat) = await SetAnswersAsync();
                var (need, needWhy) = await AnswersNeededNextTurnAsync();
                var (tol, tolWhy) = await RiskToleranceAsync();
                // After their Heavy Storm is gone, one more is fine (user, 2026-11-13: "after heavy storm more should be allowed"),
                // unless their deck punishes backrow other ways ("blackwings and glads can still punish it even after heavy storm").
                bool stormGone = await OpponentHeavyStormChanceAsync() < StormGoneChance;
                string? punisher = stormGone ? await OpponentBackrowPunisherAsync() : null;
                int allowed = Math.Max(1, need + (tol >= ExtraSetRiskTolerance ? 1 : 0) + (stormGone && punisher == null ? 1 : 0));
                if (have >= allowed && Rule(DeckRules.OnlyNeededAnswers))
                    return $"we already have {have} answer(s) set ({haveWhat}) and expect to need {need} next turn ({needWhy}; {tolWhy}" +
                           (stormGone ? punisher != null ? $"; their Heavy Storm is gone but {punisher}" : "; their Heavy Storm is gone: one more allowed" : "") +
                           "): only commit the answers we'll need";
            }
            if (role != TrapRole.None)
            {
                var same = set.Where(card => RoleOf(card.ID) == role).ToList();
                if (same.Count > 0)
                {
                    string have = string.Join(", ", same.Select(card => CardNames.Get(card.ID)));
                    if (role != TrapRole.Attack || same.Any(card => AnswersEveryAttacker(card.ID)))
                        return $"{have} already answers the same thing (one answer per issue; a second only gives Heavy Storm more)";
                    int big = 0;
                    foreach (var enemy in await GetEnemyMonsterCardsAsync())
                        if (enemy.ID > 0 && enemy.IsFaceup() && await IsBigThreatAsync(enemy))
                            big++;
                    if (big <= same.Count)
                        return $"{have} already answers their attacks ({big} big threat(s) on their side, {same.Count} answer(s) set)";
                }
            }
            // The opponent's deck can threaten our backrow from its board (X-Sabers: Emmersblade -> Hyunlei).
            if (await MatchupHoldSetAsync(set.Count) is string matchupHold)
                return matchupHold;
            if (set.Count >= MaxExposedSets && await OpponentHeavyStormChanceAsync() > 0)
            {
                // Under pressure the cap gives (CSV S3, user: "if they have two threats already on field and you only have enough traps
                // to answer those two threats you may need a third trap even if they might have heavy"): their big threats outnumber
                // or match our sets, or the risk tolerance is past half-way (low LP, a stall, the deck race, losing next turn).
                await RiskToleranceAsync();
                int bigThreats = (await GetEnemyMonsterCardsAsync()).Count(m => m.IsFaceup() && m.Attack >= StrongMonsterAttack);
                if (bigThreats >= set.Count || RiskPressNow >= OversetPressFrom)
                    return null;
                return $"already {set.Count} exposed set cards (max {MaxExposedSets}): one Heavy Storm would take them all";
            }
            return null;
        }

        /// <summary>
        /// Order to set in: attack answers first (Mirror Force first when they have 2+ monsters, since it takes them all,
        /// otherwise Dimensional Prison, which banishes), then summon answers, then other traps, then bait and
        /// storm-proof cards last (they don't count towards the cap, so they still go down after).
        /// </summary>
        protected int SetOrder(int cardId, int enemyMonsters) =>
            RoleOf(cardId) == TrapRole.Attack ? ((cardId == MirrorForceId) == (enemyMonsters >= 2) ? 0 : 1)
            : RoleOf(cardId) == TrapRole.Summon ? 2
            : IsStormFree(cardId) ? 4
            : 3;

        // ------------------------------------------------------------------ our Heavy Storm / MST (shared defaults)

        /// <summary>
        /// Default main-phase use of our Heavy Storm and MST for deck brains without their own rules: Heavy Storm when the
        /// opponent has 2+ spells/traps and more than us (counting the Storm itself); MST on an opponent's set card
        /// before attacking.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryStapleIdleAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            var enemySpells = await GetEnemySpellCardsAsync();
            int ourSpells = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && !card.IsCode(HeavyStormId));

            // Free card advantage (Upstart, Charge, Reinforcement), backrow removal (Heavy Storm, Lyla, MST for a big
            // play; otherwise MST is set as bait, see TryChainBaitAsync), Card Trooper.
            if (await TryFreeCardAdvantageAsync(context, state) is IdleCmdResponse free)
                return free;
            if (await TryBackrowRemovalAsync(context, state, enemySpells.Count, ourSpells) is IdleCmdResponse backrow)
                return backrow;
            if (await TryMonsterStaplesAsync(context) is IdleCmdResponse monsterStaple)
                return monsterStaple;
            if (await TryCardTrooperAsync(context, state) is IdleCmdResponse trooper)
                return trooper;
            if (await TryReturnDimensionLethalAsync(context, state) is IdleCmdResponse returnLethal)
                return returnLethal;

            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.StateData.CanBattlePhase &&
                               (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack());
            var setMst = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.IsCode(MysticalSpaceTyphoonId));
            if (setMst.ID > 0 && !battleAhead && await HoldSetForHeavyStormAsync(context.Header.CurrentTurn, MysticalSpaceTyphoonId) == null)
            {
                context.Bot.LogNote("deck brain: set Mystical Space Typhoon as bait for the opponent's removal");
                return setMst.SetSpellTrap();
            }
            return null;
        }

        /// <summary>
        /// Heavy Storm is for pushing big plays through, not for the first chance to use it. Worth it when:
        ///  1. card advantage: the opponent loses at least 2 more spells/traps than we do (the Storm is a card too), or
        ///  2. a big play is ready this turn and their backrow could stop it (even if they only have one card).
        /// Returns the reason, or null to hold it.
        /// </summary>
        protected async Task<string?> HeavyStormReasonAsync(DecisionContext<IdleCmdData> context, int theirs, int ours)
        {
            if (theirs == 0)
                return null;
            // Round-robin 35 (Machina Jinzo held Heavy Storm all game, "opponent 1 vs our 2"): our own set bait (Jar of Greed, MST,
            // Waboku...) is chained in response to our Storm, so it isn't lost; only the rest counts.
            int baitChained = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && !card.IsFaceup() && !card.IsCode(HeavyStormId) && IsStormFree(card.ID));
            int lost = Math.Max(0, ours - baitChained);
            // And a Storm held through two of our turns with targets on their side makes way: an even trade of backrow plus the
            // Storm itself is then fine (a card that never gets used is worth nothing).
            int turn = context.Header.CurrentTurn;
            if (_stormHeldSince < 0 || turn < _stormHeldSince)
                _stormHeldSince = turn;
            int margin = turn - _stormHeldSince >= 4 ? 1 : 2;
            if (theirs >= lost + margin)
            {
                _stormHeldSince = -1;
                return $"card advantage: opponent loses {theirs} spells/traps, we lose {lost} (+ the Storm)" +
                       (baitChained > 0 ? $"; our {baitChained} set bait card(s) are chained in response, not lost" : "") +
                       (margin == 1 ? "; held two turns already, so an even trade will do" : "");
            }
            var bigPlay = await BigPlayPendingAsync(context);
            // Our set Solemn Judgment already pushes a play through one of their cards: with a single set card of theirs, the
            // Storm adds nothing for the push and costs our own backrow (user, 2026-11-11: "solemn does that too").
            if (bigPlay != null && theirs <= 1 && await SolemnReadyAsync() && Rule(DeckRules.StormNotWithSolemn))
            {
                context.Bot.LogNote($"deck brain: hold Heavy Storm: our set Solemn Judgment already gets the big play past their one spell/trap");
                Hold(HeavyStormId, MainAction.Activate, DeckRules.StormNotWithSolemn);
                return null;
            }
            if (bigPlay != null)
            {
                CommitToBigPlay(context.Header.CurrentTurn);
                return $"clear their {theirs} spell/trap(s) for a big play: {bigPlay} (committing to it this turn)";
            }
            context.Bot.LogNote($"deck brain: hold Heavy Storm: opponent {theirs} vs our {ours} spells/traps, and no big play to push through");
            Hold(HeavyStormId, MainAction.Activate, DeckRules.HeavyStorm);
            return null;
        }

        // The turn we first held Heavy Storm with targets on their side (reset when it's used).
        private int _stormHeldSince = -1;

        private int _bigPlayTurn = -1;
        private readonly HashSet<int> _committedBigPlayCards = new();

        /// <summary>The cards making up the big play found by the last BigPlayPendingAsync call (summons, activations, synchros).</summary>
        protected List<int> PendingBigPlayCards { get; } = new();

        /// <summary>
        /// Backrow removal was spent to push the pending big play through this turn: the plays that made it up should
        /// follow (the same conditions that justified the removal), not every other summon.
        /// </summary>
        protected void CommitToBigPlay(int turn, bool keepExisting = false)
        {
            if (!keepExisting || _bigPlayTurn != turn)
                _committedBigPlayCards.Clear();
            _bigPlayTurn = turn;
            _committedBigPlayCards.UnionWith(PendingBigPlayCards.Where(id => id > 0));
        }

        /// <summary>
        /// Whether this play is part of the big play we cleared the backrow for this turn: the card itself, or a synchro
        /// using one of its cards as material (e.g. the synchro after Plaguespreader).
        /// </summary>
        protected bool CommittedToBigPlay(BotStateHeader header, int cardId, IEnumerable<DynamicCard>? materials = null) =>
            _bigPlayTurn == header.CurrentTurn &&
            (_committedBigPlayCards.Contains(cardId) || (materials ?? Enumerable.Empty<DynamicCard>()).Any(m => _committedBigPlayCards.Contains(m.ID)));

        /// <summary>Set cards worth keeping face-down as bait: chained when the opponent's removal would hit them.</summary>
        // Waboku too: set, it's chained when their removal hits it, rather than lost (user, 2026-11-11).
        protected static readonly int[] DefaultBaitCards = { JarOfGreedId, LegacyOfYataGarasuId, MysticalSpaceTyphoonId, WabokuId };

        /// <summary>Bait cards for this deck right now (deck brains add their own, e.g. Burial from a Different Dimension).</summary>
        protected virtual IReadOnlyCollection<int> BaitCardIds => DefaultBaitCards;

        /// <summary>
        /// Whether their card being activated targets this set spell/trap of ours (their Lyla, MST, Dust Tornado...): it's
        /// gone when the chain resolves, so using it now costs nothing extra. The server reports targets on either side (their
        /// card's targets, or our card's "targeted by" lists, on the prompt's copy or a fresh field read): all are checked.
        /// </summary>
        protected async Task<bool> OurSetCardTargetedAsync(DynamicCard ours)
        {
            if (ours.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return false;
            var enemySpells = await GetEnemySpellCardsAsync();
            var enemyCards = enemySpells.Concat(await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).ToList();
            var ourSpellZone = await CurrentField.GetCardsInSpellZone();
            var at = ours.CurrentCoordinates;
            var theirSpellZones = enemySpells.Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
            // Their card's targets come from their side (controller 1 = us: seen 2026-11-10, their MST on our Black Whirlwind as
            // [1/8/2]; 2026-11-12, Lyla on our Jar of Greed as [1/8/1] with their own set card in S2 too). Our own side's
            // controller only counts as ours when their zone there is empty.
            bool HitsOurs(CardCoordinate t) =>
                t.Location == at.Location && t.Sequence == at.Sequence &&
                (t.Controller != at.Controller || t.Location == (byte)CardLocation.SpellZone && !theirSpellZones.Contains(t.Sequence));
            if (enemyCards.Any(card => (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>()).Any(HitsOurs)))
                return true;
            static IEnumerable<CardCoordinate> TargetedBy(DynamicCard card) =>
                (card.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Concat(card.OwnTargets ?? Array.Empty<CardCoordinate>());
            var fieldCopy = ourSpellZone.FirstOrDefault(card => card.CurrentCoordinates.Sequence == at.Sequence);
            return TargetedBy(ours).Concat(TargetedBy(fieldCopy)).Any(t => t.Controller == 1);
        }

        // Text that removes cards which can be spells/traps (not just monsters): "destroy up to 2 cards your opponent
        // controls" (Crystal Raigeki), "destroy 1 Spell/Trap" (MST), "return 1 card to the hand"...
        private static readonly System.Text.RegularExpressions.Regex CardRemovalText = new(
            @"(destroy|banish|return|send|shuffle)[^.]{0,60}?\b(1|one|2|two|up to \d|that|target(ed)?)\b[^.]{0,50}?\b(cards?|spells?|traps?|spell/trap|spell & trap)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Spell/trap text that clears our backrow without targeting it (Heavy Storm, Giant Trunade, ...).
        private static readonly System.Text.RegularExpressions.Regex MassBackrowRemoval = new(
            @"(destroy|return|banish) all .{0,40}(spell|cards)|all spell (and|&|or) trap",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Bait: a set Jar of Greed / Legacy of Yata-Garasu / MST is chained when the opponent's current activation would
        /// remove it, so their removal hits nothing (and MST takes one of their cards for free). Threats: an opponent
        /// card targeting its zone, or a Normal/Quick-Play spell or Normal trap being activated whose text removes all
        /// spells/traps. Returns the chain index, or null.
        /// </summary>
        // The removal we chained bait against this turn (it resolves after our chain links), so the rest of the bait follows.
        private (int Turn, int Phase, string Name, int Reach)? _baitThreat;

        /// <summary>One of our cards in the Spell/Trap zone (offered in a chain prompt): it may be about to be removed (TryChainBaitAsync).</summary>
        protected static bool IsOurSetCard(DynamicCard card) =>
            card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && card.CurrentCoordinates.Controller == 0;

        protected async Task<int?> TryChainBaitAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            var bait = BaitCardIds;
            // Not only the bait staples (Jar of Greed, Yata, MST): any set card of ours the server lets us activate here, about
            // to be removed, goes off if activating it does any good at all (user, 2026-11-12: "if a card is about to be
            // destroyed, and activating it would have a positive effect, no matter how small, then it should always be used").
            // Torrential Tribute only when it takes more of theirs than of ours.
            // A doomed Torrential: the normal Torrential weighing (count and value; spent flip monsters are worth their stats).
            var (torrentialPays, torrentialWhy) = TorrentialWorthIt(await GetEnemyMonsterCardsAsync(),
                (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 || card.CurrentCoordinates.Location != 0).ToList());
            if (options.Any(card => IsOurSetCard(card) && card.IsCode(TorrentialTributeId)))
                context.Bot.LogNote($"deck brain: doomed Torrential Tribute: {torrentialWhy}");
            bool PaysOff(DynamicCard card) => !card.IsCode(TorrentialTributeId) || torrentialPays;
            // "Always use it" is for cards free to activate (Jar of Greed, Compulsory, MST). A card with a cost of its own (Solemn's
            // half LP, Icarus Attack's tribute, Raigeki Break's discard) is weighed like the normal card, with the card itself
            // counted as free because it's lost either way (user, 2026-11-12: "treat them as the normal card but give it the
            // bonus it would get if it went +1"; round-robin 15: Solemn went off "as a doomed card" on a Pot of Greed).
            var costly = new HashSet<int>();
            foreach (var card in options.Where(IsOurSetCard))
                if (card.ID > 0 && await CardDatabase.GetAsync(card.ID) is DBCardInfo costInfo && ActivationCostText.IsMatch(costInfo.CardText ?? ""))
                    costly.Add(card.ID);
            bool IsSetBait(DynamicCard card) => IsOurSetCard(card) && !costly.Contains(card.ID) &&
                                                (bait.Contains(card.ID) || PaysOff(card) && Rule(DeckRules.DoomedCardUsed));
            bool IsCostlyDoomed(DynamicCard card) => IsOurSetCard(card) && costly.Contains(card.ID);
            if (!options.Any(IsSetBait) && !options.Any(IsCostlyDoomed))
                return null;

            var enemySpells = await GetEnemySpellCardsAsync();
            var enemyCards = enemySpells.Concat(await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup()).ToList();

            int Chain(int index, string why)
            {
                if (!Rule(DeckRules.ChainBait))
                    return -1;
                if (options[index].IsCode(MysticalSpaceTyphoonId))
                    _lastStapleActivated = MysticalSpaceTyphoonId;
                context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[index].ID)} before it is removed: {why}");
                return index;
            }

            // Targeted: chain the bait only when the opponent's card targets it (otherwise it stays as a decoy). The
            // server can report this on either side: their card's targets (CurrentChainTargets / TargetCards), or our
            // card's "targeted by" lists (ChainOnlyOwnTargets / OwnTargets), on the prompt's copy of the card or on a
            // fresh field read. Check all of them.
            var ourSpellZone = await CurrentField.GetCardsInSpellZone();
            static IEnumerable<CardCoordinate> TargetedBy(DynamicCard card) =>
                (card.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Concat(card.OwnTargets ?? Array.Empty<CardCoordinate>());
            // Their card's targets come from their side: controller 1 is us (seen 2026-11-10: their MST on our Black Whirlwind
            // as [1/8/2]; 2026-11-12: Lyla on our Jar of Greed as [1/8/1], their own set card in S2 too, and we kept Jar down
            // "not our bait"). Our own side's controller only counts as ours when their zone there is empty.
            var theirSpellZones = enemySpells.Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
            bool HitsOurs(CardCoordinate t, CardCoordinate at) =>
                t.Location == at.Location && t.Sequence == at.Sequence &&
                (t.Controller != at.Controller || t.Location == (byte)CardLocation.SpellZone && !theirSpellZones.Contains(t.Sequence));
            // A doomed card with a cost: its own weighing, the card itself free (DoomedCostlyWorthAsync). Logged either way.
            async Task<int?> CostlyDoomed(int i, DynamicCard? threat, int reach, string how)
            {
                var (use, why) = await DoomedCostlyWorthAsync(context, options[i], threat, reach);
                if (!use)
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(options[i].ID)} is about to be removed ({how}), but not worth its cost: {why}");
                    return null;
                }
                return Chain(i, $"{how}; {why}");
            }

            for (int i = 0; i < options.Length; i++)
            {
                if (!IsSetBait(options[i]) && !IsCostlyDoomed(options[i]))
                    continue;
                var at = options[i].CurrentCoordinates;
                var attacker = enemyCards.FirstOrDefault(card =>
                    (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>())
                        .Any(t => HitsOurs(t, at)));
                var fieldCopy = ourSpellZone.FirstOrDefault(card => card.CurrentCoordinates.Sequence == at.Sequence);
                var by = TargetedBy(options[i]).Concat(TargetedBy(fieldCopy)).FirstOrDefault(t => t.Controller == 1);
                string? targetedHow = attacker.ID > 0 ? $"{CardNames.Get(attacker.ID)} targets it"
                    : by.Location != 0 ? $"the opponent's card in {(by.Location == (byte)CardLocation.SpellZone ? $"S{by.Sequence + 1}" : $"location {by.Location} #{by.Sequence + 1}")} targets it"
                    : null;
                if (targetedHow == null)
                    continue;
                if (IsCostlyDoomed(options[i]))
                {
                    if (await CostlyDoomed(i, attacker.ID > 0 ? attacker : null, 1, targetedHow) is int costlyIndex)
                        return costlyIndex;
                    continue;
                }
                return Chain(i, $"{targetedHow}: use it before it is removed");
            }

            // The opponent just activated something that removes spells/traps (a new face-up spell, or the monster they
            // just summoned, e.g. XX-Saber Hyunlei on its Synchro Summon), but no target was reported. Then it's a
            // probabilistic call: if it can take N of our set cards and we have S, a given bait is hit with chance
            // about min(1, N / S) (they aim at ours, not their own). Chain when that's likely (BaitHitChance).
            // Still in the chain we started against their removal (our bait is now the latest link): the removal hasn't
            // resolved yet, so the rest of our bait goes too while it can still be hit (seen 2026-11-10: MST chained against
            // Hyunlei, then Jar of Greed passed because the latest link was ours).
            // MST only goes as bait when it has a real target (seen 2026-11-10: chained at their resolving MST, the only card
            // it could take: it did nothing).
            bool mstHasTarget = enemySpells.Any(card => card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && WorthRemovingSpellTrap(card));
            bool UsableBait(DynamicCard card) => IsSetBait(card) && (!card.IsCode(MysticalSpaceTyphoonId) || mstHasTarget);
            // Every spell/trap of ours their removal could take, face-up continuous ones too (seen 2026-11-10: "1 set, 100%
            // it hits this one" with two face-up Black Whirlwinds beside it).
            int ourTargets = ourSpellZone.Count(card => card.ID > 0 && WorthRemovingSpellTrap(card));
            if (context.Header.LastChainPlayer == 0 && _baitThreat is { } threat && threat.Turn == context.Header.CurrentTurn && threat.Phase == context.Header.CurrentPhase)
            {
                double hitNow = ourTargets == 0 ? 0 : Math.Min(1, (double)threat.Reach / ourTargets);
                if (hitNow >= BaitHitChance && options.Any(UsableBait))
                {
                    int next = Enumerable.Range(0, options.Length).Where(i => UsableBait(options[i]))
                        .OrderBy(i => options[i].IsCode(MysticalSpaceTyphoonId) ? 0 : options[i].IsCode(JarOfGreedId, LegacyOfYataGarasuId) ? 1 : 2)
                        .First();
                    return Chain(next, $"still in the chain: {threat.Name} removes up to {threat.Reach} spell/trap(s) and we have {ourTargets} it could take: " +
                                       $"{hitNow:P0} it hits this one too, use it first");
                }
            }
            if (context.Header.LastChainPlayer != 0)
                _baitThreat = null; // a new chain: the earlier threat has resolved

            if (context.Header.LastChainPlayer == 1)
            {
                int ourSet = ourTargets;
                var (activators, activatorHow) = await ActivatedNowAsync();
                if (activators.Count > 0)
                    context.Bot.LogNote($"deck brain: their chain link: {string.Join(", ", activators.Select(card => CardNames.Get(card.ID)))} ({activatorHow})");
                string Coords(IEnumerable<CardCoordinate>? list) =>
                    string.Join(",", (list ?? Array.Empty<CardCoordinate>()).Select(t => $"{t.Controller}/{t.Location}/{t.Sequence}"));
                // A reported target on this chain that isn't our bait settles it: that is the removal being used, and the bait
                // stays down; no guessing from other cards of theirs (seen 2026-11-10: MST aimed elsewhere, then Jar of Greed
                // chained on a guess about Mist Valley Falcon).
                var aimed = activators.FirstOrDefault(card =>
                    (card.CurrentChainTargets?.Length ?? 0) > 0 || (card.TargetCards?.Length ?? 0) > 0);
                if (aimed.ID > 0)
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(aimed.ID)} targets [{Coords((aimed.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(aimed.TargetCards ?? Array.Empty<CardCoordinate>()))}], " +
                                        "not our bait: keep it down");
                    activators = new List<DynamicCard>();
                }
                foreach (var card in activators)
                {
                    // A target was reported, and it isn't our bait (that case chained above): the bait is safe, no guessing
                    // (seen 2026-11-10: their MST aimed at our Black Whirlwind, reported as [1/8/2], and we "guessed" 100%
                    // it hit our MST).
                    var reported = (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>()).ToList();
                    if (reported.Count > 0)
                    {
                        context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} targets [{Coords(reported)}], not our bait: keep it down");
                        continue;
                    }
                    int reach = await SpellTrapRemovalReachAsync(card.ID);
                    // The card this threat is weighed for: a free bait, or (when only one with a cost is on offer) that card.
                    var bait0 = options.FirstOrDefault(c => IsSetBait(c) || IsCostlyDoomed(c));
                    var field0 = ourSpellZone.FirstOrDefault(c => c.CurrentCoordinates.Sequence == bait0.CurrentCoordinates.Sequence);
                    string targetInfo = $"target info: theirs chain=[{Coords(card.CurrentChainTargets)}] targets=[{Coords(card.TargetCards)}]; " +
                                        $"ours prompt=[{Coords(TargetedBy(bait0))}] field=[{Coords(TargetedBy(field0))}]";
                    if (reach <= 0)
                    {
                        if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && CardRemovalText.IsMatch(info.CardText ?? ""))
                            context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} can remove cards but no target was reported, so " +
                                                $"{CardNames.Get(bait0.ID)} stays down as a decoy ({targetInfo})");
                        continue;
                    }
                    double hit = ourSet == 0 ? 0 : Math.Min(1, (double)reach / ourSet);
                    if (hit < BaitHitChance)
                    {
                        context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} can take {reach} of our {ourSet} set cards: " +
                                            $"{hit:P0} a given one is hit, keep the bait down ({targetInfo})");
                        continue;
                    }
                    if (!options.Any(UsableBait))
                    {
                        // Only a card with a cost is in reach: weighed with the card itself free.
                        string how = $"{CardNames.Get(card.ID)} removes up to {reach} spell/trap(s) and we have {ourSet} it could take ({hit:P0} it hits this one)";
                        for (int i = 0; i < options.Length; i++)
                            if (IsCostlyDoomed(options[i]) && await CostlyDoomed(i, card, reach, how) is int costlyIndex)
                                return costlyIndex;
                        if (options.Any(card => card.IsCode(MysticalSpaceTyphoonId) && IsSetBait(card)))
                            context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} may take our set MST, but MST has nothing worth taking " +
                                                "(their only spell/traps are resolving): keep it down");
                        continue;
                    }
                    // Best bait first: MST takes one of their cards, then the draws, then the rest.
                    int best = Enumerable.Range(0, options.Length).Where(i => UsableBait(options[i]))
                        .OrderBy(i => options[i].IsCode(MysticalSpaceTyphoonId) ? 0 : options[i].IsCode(JarOfGreedId, LegacyOfYataGarasuId) ? 1 : 2)
                        .First();
                    _baitThreat = (context.Header.CurrentTurn, context.Header.CurrentPhase, CardNames.Get(card.ID), reach);
                    return Chain(best, $"{CardNames.Get(card.ID)} removes up to {reach} spell/trap(s) and we have {ourSet} it could take: " +
                                       $"{hit:P0} it hits this one, use it before it is removed (no target was reported; {targetInfo})");
                }
            }

            // Mass removal being activated: theirs, or our own (our Heavy Storm would destroy our set Jar of Greed too,
            // so use it in response rather than lose it).
            var ourFaceUp = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0 && card.IsFaceup());
            foreach (var card in enemySpells.Where(card => card.IsFaceup()).Concat(ourFaceUp))
            {
                bool oneShot = card.HasType(CardType.Spell) || card.HasType(CardType.Trap);
                bool staysOnField = card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip);
                if (!oneShot || staysOnField)
                    continue; // a face-up Normal/Quick-Play spell or Normal trap is one being activated right now
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && MassBackrowRemoval.IsMatch(info.CardText ?? ""))
                {
                    // Not MST: it would only destroy a card the mass removal destroys anyway.
                    int baitIndex = Array.FindIndex(options, option => IsSetBait(option) && !option.IsCode(MysticalSpaceTyphoonId));
                    bool ours = card.CurrentCoordinates.Controller == 0;
                    if (baitIndex < 0)
                    {
                        // Only a card with a cost left: weighed with the card itself free (their mass removal takes every one of ours).
                        if (!ours)
                            for (int i = 0; i < options.Length; i++)
                                if (IsCostlyDoomed(options[i]) &&
                                    await CostlyDoomed(i, card, ourTargets, $"{CardNames.Get(card.ID)} would clear our backrow ({ourTargets} of ours)") is int costlyIndex)
                                    return costlyIndex;
                        return null;
                    }
                    return Chain(baitIndex, ours
                        ? $"our own {CardNames.Get(card.ID)} would destroy it too: use it in response"
                        : $"{CardNames.Get(card.ID)} would clear our backrow: use it in response");
                }
            }
            return null;
        }

        /// <summary>
        /// An activation cost before the effect: "Pay half your LP;", "Discard 1 card, then target...", "Tribute 1 monster;".
        /// A card with one isn't free to use even when it's about to be removed (DoomedCostlyWorthAsync).
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ActivationCostText = new(
            @"(^|[.:]\s*)(pay|discard|tribute|banish|send)\b[^;.]*(;|, then target)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>LP worth one card when paying a cost (LP matter little above ~1500; a cost that leaves us in reach is never paid).</summary>
        [Tunable("LP worth one card when paying a cost (LP matter little above ~1500; a cost that leaves us in reach is never paid).")]
        public int LifePointsPerCard { get; set; } = 2000;

        /// <summary>
        /// Whether a card of ours with an activation cost, about to be removed, is worth using now: the normal card's trade
        /// with its own card counted as free, since it's lost either way (user, 2026-11-12: "treat them as the normal card but
        /// give it the bonus it would get if it went +1"). Solemn Judgment: negating the chain link costs them that card, and
        /// saves the other cards of ours it would take; against half our LP (LifePointsPerCard a card; never when what's left
        /// is within reach of their board). Other costly cards: their deck's own logic decides (override).
        /// </summary>
        protected virtual async Task<(bool Use, string Why)> DoomedCostlyWorthAsync(DecisionContext<SelectChainData> context, DynamicCard ours,
            DynamicCard? threat, int reach)
        {
            // Raigeki Break and Icarus Attack (user, 2026-11-18: "it should have seen raigeki break was doomed and weighed discarding one card in
            // hand against killing krystia"; "why didn't doomed logic kick in for icarus atk?"): the card itself is free, so the trade is what
            // it destroys against what it costs (a discard / a Winged Beast tribute).
            if (ours.IsCode(RaigekiBreakId) || ours.IsCode(IcarusAttackId))
                return await DoomedRemovalWorthAsync(context, ours);
            if (!ours.IsCode(SolemnJudgmentId))
                return (false, $"{CardNames.Get(ours.ID)} has a cost of its own; its own logic decides");
            if (threat is not DynamicCard link || link.ID <= 0 || !(link.HasType(CardType.Spell) || link.HasType(CardType.Trap)))
                return (false, "Solemn Judgment negates a spell/trap activation or a summon, and the card removing it isn't one we can see as the chain link");
            int lp = await CurrentField.GetBotLifePoints();
            int after = lp - lp / 2;
            int theirThrough = await TheirAttackThroughAsync();
            // User, 2026-11-13: if our whole LP is already within reach, halving it changes nothing, and when we're about to
            // win it doesn't matter: either way the cost is ignored.
            var ourMonsters = await GetBotMonsterCardsAsync();
            bool winNow = context.Header.CurrentPlayer == 0 &&
                BoardAssessment.TurnsToWin(ourMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(EffectiveAttack).ToList(),
                    BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory), await CurrentField.GetBotLifePoints(isOwnSide: false)) == 1 &&
                (DuelPhase)context.Header.CurrentPhase is DuelPhase.Main1 or DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage;
            if (!winNow && lp > theirThrough && after <= theirThrough)
                return (false, $"paying half our LP leaves {after}, within reach of their board ({theirThrough} ATK past our monsters)");
            int ourSpellTraps = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID != 0);
            int savedOthers = Math.Max(0, Math.Min(reach, ourSpellTraps) - 1); // Solemn itself is used either way
            double benefit = 1 + savedOthers;                                   // their card spent for nothing, plus ours saved
            bool lpCostFree = lp <= theirThrough || winNow;
            double cost = lpCostFree ? 0 : (lp / 2) / (double)LifePointsPerCard;
            string sums = $"negating {CardNames.Get(link.ID)} is worth {benefit:0.#} card(s) (theirs{(savedOthers > 0 ? $", and {savedOthers} of ours saved" : "")}; " +
                          "Solemn itself is lost either way) against " +
                          (lpCostFree ? (winNow ? "no real LP cost (we win this turn)" : $"no real LP cost (our {lp} LP is already within reach of their {theirThrough} ATK through)")
                                      : $"{lp / 2} LP ({cost:0.#} card(s) at {LifePointsPerCard} LP a card)");
            return benefit > cost ? (true, $"chain Solemn Judgment (pay {lp / 2} LP): {sums}") : (false, sums);
        }

        protected const int RaigekiBreakId = 4178474;

        /// <summary>
        /// A doomed Raigeki Break / Icarus Attack, weighed with itself free: the best card(s) of theirs it destroys (Raigeki 1, Icarus 2: a face-up
        /// monster by its effect score, a face-down card at UnknownCardTargetWorth) against what it costs (Raigeki: our cheapest card in hand to
        /// discard, by its effect score in hand; Icarus: our cheapest Winged Beast to tribute, by its effect score on the field). Used when the
        /// gain is bigger; Raigeki's target and Icarus's tribute are planned.
        /// </summary>
        protected async Task<(bool Use, string Why)> DoomedRemovalWorthAsync(DecisionContext<SelectChainData> context, DynamicCard ours)
        {
            bool icarus = ours.IsCode(IcarusAttackId);
            var field = await CurrentField.GetBotField();
            var theirGrave = field.EnemySide.Grave ?? Array.Empty<int>();
            var ourGrave = field.OwnSide.Grave ?? Array.Empty<int>();
            var theirs = (await GetEnemyMonsterCardsAsync()).Concat(await GetEnemySpellCardsAsync())
                .Where(card => card.ID > 0 || card.Position != 0).ToList();
            var worths = new List<(DynamicCard Card, double Worth)>();
            foreach (var card in theirs)
            {
                // A face-up Normal / Quick-Play spell or Normal trap is resolving (the one removing ours): nothing to destroy.
                if (card.IsFaceup() && !card.HasType(CardType.Monster) && !WorthRemovingSpellTrap(card))
                    continue;
                double worth = card.IsFaceup() && card.ID > 0
                    ? (await EffectEvaluator.ScoreOnFieldAsync(card.ID, theirGrave)).Score
                    : UnknownCardTargetWorth;
                worths.Add((card, worth));
            }
            var targets = worths.OrderByDescending(w => w.Worth).Take(icarus ? 2 : 1).ToList();
            if (targets.Count == 0)
                return (false, $"{CardNames.Get(ours.ID)}: nothing of theirs worth destroying");
            double gain = targets.Sum(t => t.Worth);
            string targetsText = string.Join(" + ", targets.Select(t => $"{(t.Card.ID > 0 && t.Card.IsFaceup() ? CardNames.Get(t.Card.ID) : "a face-down card")} ({t.Worth:0.#})"));
            if (icarus)
            {
                var wings = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && (card.Race & (int)CardRace.WingedBeast) != 0).ToList();
                if (wings.Count == 0)
                    return (false, "Icarus Attack: no Winged Beast of ours to tribute");
                // A Winged Beast that's about to be destroyed anyway costs nothing to tribute (user, 2026-11-18: "the cheapest winged beast to
                // tribute is a doomed winged-beast"): its worth times the chance it survives.
                var doomed = await DoomedMonstersAsync(context.Header);
                var costs = new List<(DynamicCard Card, double Worth)>();
                foreach (var wing in wings)
                {
                    double worth = (await EffectEvaluator.ScoreOnFieldAsync(wing.ID, ourGrave)).Score;
                    var fate = doomed.FirstOrDefault(d => d.Card.ID == wing.ID && d.Card.CurrentCoordinates.Sequence == wing.CurrentCoordinates.Sequence);
                    costs.Add((wing, fate.Card.ID > 0 ? worth * (1 - Math.Clamp(fate.Chance, 0, 1)) : worth));
                }
                var tribute = costs.OrderBy(c => c.Worth).First();
                string sums = $"Icarus Attack is free (it's being removed): destroying {targetsText} = {gain:0.#} against tributing {CardNames.Get(tribute.Card.ID)} ({tribute.Worth:0.#})";
                if (gain <= tribute.Worth)
                    return (false, sums);
                PlanTributes(context.Header.CurrentTurn, new[] { tribute.Card }, "Icarus Attack");
                return (true, sums);
            }
            var hand = await GetBotHandCardsAsync();
            if (hand.Count == 0)
                return (false, "Raigeki Break: no card in hand to discard");
            var discards = new List<(DynamicCard Card, double Worth)>();
            foreach (var card in hand)
                // A card that works from the graveyard costs less to discard (user, 2026-11-18: "prefer discarding cards with graveyard effects"):
                // its worth in hand less its worth in the graveyard.
                discards.Add((card, (await EffectEvaluator.ScoreAsync(card.ID, ourGrave)).Score - (await EffectEvaluator.ScoreInGraveAsync(card.ID, ourGrave)).Score));
            var discard = discards.OrderBy(d => d.Worth).First();
            string raigekiSums = $"Raigeki Break is free (it's being removed): destroying {targetsText} against discarding {CardNames.Get(discard.Card.ID)} ({discard.Worth:0.#})";
            if (gain <= discard.Worth)
                return (false, raigekiSums);
            PlanTarget(context.Header.CurrentTurn, targets[0].Card, theirs: true);
            return (true, raigekiSums);
        }

        [Tunable("A face-down card of theirs as a target for a doomed Raigeki Break / Icarus Attack (effect points).")]
        public static double UnknownCardTargetWorth = 1.5;

        /// <summary>Chance a set bait card is hit (untargeted-looking removal) from which it's chained. Tune from logs.</summary>
        [Tunable("Chance a set bait card is hit (untargeted-looking removal) from which it's chained. Tune from logs.")]
        public double BaitHitChance { get; set; } = 0.5;

        /// <summary>
        /// Spell/trap removers whose text the card database doesn't have (shown as "#id" in the logs): how many they take.
        /// XX-Saber Hyunlei: on Synchro Summon, destroy up to 3 spells/traps.
        /// </summary>
        private static readonly Dictionary<int, int> KnownSpellTrapRemovers = new()
        {
            [2203790] = 3, // XX-Saber Hyunlei
        };

        // "destroy up to 3 Spells/Traps", "target 1 Spell/Trap ... destroy", "destroy 2 cards your opponent controls"...
        private static readonly System.Text.RegularExpressions.Regex SpellTrapRemovalCount = new(
            @"(?:up to |)(\d|one|two|three)\s+(?:spells?/traps?|spell(?:s)? (?:and|&|or) traps?|spell/trap cards?|cards?)\b[^.]{0,80}?(destroy|banish|return)\b|(destroy|banish|return)\b[^.]{0,40}?(?:up to |)(\d|one|two|three)\s+(?:spells?/traps?|spell(?:s)? (?:and|&|or) traps?|spell/trap cards?|cards?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // "destroy 1 monster", "destroy up to 2 face-up monsters", "destroy all monsters".
        private static readonly System.Text.RegularExpressions.Regex MonsterRemovalCount = new(
            @"(destroy|banish|return|send)\b[^.]{0,40}?(?:up to |)(\d|one|two|three|all)\s+(?:face-up |face-down |attack position |)monsters?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Round-robin 19: our monsters the chain link being resolved is likely to remove, with the chance for each. A target
        /// reported on one of ours is certain; otherwise the link (ActivatedNowAsync) is read from text: how many cards or
        /// monsters it can remove against how many of ours it could pick from (seen 2026-11-12: Gyzarus's summon effect, "destroy
        /// up to 2 cards", took Bora and our Jar of Greed while Icarus Attack, which could have tributed Bora, was passed).
        /// Our turn, or no link of theirs: empty.
        /// </summary>
        protected async Task<List<(DynamicCard Card, double Chance, string How)>> DoomedMonstersAsync(BotStateHeader header)
        {
            var result = new List<(DynamicCard, double, string)>();
            if (header.LastChainPlayer != 1)
                return result;
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0).ToList();
            if (ours.Count == 0)
                return result;
            var theirCards = (await GetEnemyMonsterCardsAsync()).Concat(await GetEnemySpellCardsAsync()).ToList();
            foreach (var card in theirCards)
                foreach (var t in (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>()))
                    if (t.Location == (byte)CardLocation.MonsterZone && ours.FirstOrDefault(m => m.CurrentCoordinates.Sequence == t.Sequence && t.Controller != 1 - m.CurrentCoordinates.Controller) is { ID: > 0 } hit &&
                        !result.Any(r => SameZone(r.Item1, hit)))
                        result.Add((hit, 1.0, $"{CardNames.Get(card.ID)} targets it"));
            if (result.Count > 0)
                return result;
            var (activators, how) = await ActivatedNowAsync();
            int ourSpells = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0);
            foreach (var card in activators)
            {
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                    continue;
                string text = info.CardText ?? "";
                var monsters = MonsterRemovalCount.Match(text);
                int reach;
                int pool;
                if (monsters.Success)
                {
                    string n = monsters.Groups[2].Value.ToLowerInvariant();
                    reach = n switch { "all" => 99, "one" => 1, "two" => 2, "three" => 3, _ => int.TryParse(n, out int v) ? v : 1 };
                    pool = ours.Count;
                }
                else
                {
                    reach = await SpellTrapRemovalReachAsync(card.ID);
                    if (reach <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(text, @"\bcards?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        continue;
                    pool = ours.Count + ourSpells;
                }
                double chance = Math.Min(1, (double)reach / Math.Max(1, pool));
                foreach (var m in ours)
                    result.Add((m, chance, $"{CardNames.Get(card.ID)} ({how}) removes up to {(reach >= 99 ? "all" : reach.ToString())} of our {pool} card(s)"));
                break;
            }
            return result;
        }

        /// <summary>
        /// Whether their deck (the lists they're likely on) still punishes our backrow after Heavy Storm: cards other than Heavy
        /// Storm / MST that destroy spells/traps or "cards" (Icarus Attack, Bestiari, Gyzarus...), at least BackrowPunisherCount
        /// of them. Returns a note naming them, or null. Null without list data.
        /// </summary>
        protected async Task<string?> OpponentBackrowPunisherAsync()
        {
            if (_backrowPunisher is { } cached && cached.Turn == _currentHeader.CurrentTurn)
                return cached.Note;
            var names = new List<string>();
            foreach (int id in OpponentDeck.LikelyCards(0.2))
                if (id != HeavyStormId && id != MysticalSpaceTyphoonId && await SpellTrapRemovalReachAsync(id) > 0)
                    names.Add(CardNames.Get(id));
            string? note = names.Count >= BackrowPunisherCount ? $"their deck still hits backrow ({string.Join(", ", names.Take(4))})" : null;
            _backrowPunisher = (_currentHeader.CurrentTurn, note);
            return note;
        }

        private (int Turn, string? Note)? _backrowPunisher;

        /// <summary>Backrow-hitting cards (besides Heavy Storm / MST) in their deck from which we stay careful after their Storm.</summary>
        [Tunable("Backrow-hitting cards (besides Heavy Storm / MST) in their deck from which we stay careful after their Storm.")]
        public int BackrowPunisherCount { get; set; } = 2;

        /// <summary>How many spells/traps this card's effect can remove (0 if none, or unknown).</summary>
        protected async Task<int> SpellTrapRemovalReachAsync(int cardId)
        {
            if (KnownSpellTrapRemovers.TryGetValue(cardId, out int known))
                return known;
            if (await CardDatabase.GetAsync(cardId) is not DBCardInfo info)
                return 0;
            string text = info.CardText ?? "";
            var match = SpellTrapRemovalCount.Match(text);
            if (!match.Success)
                return MassBackrowRemoval.IsMatch(text) ? 5 : 0;
            // Its own cards only ("return 1 card you control to the hand": Mist Valley Falcon's attack cost): no threat to ours
            // (seen 2026-11-10: Jar of Greed chained as bait "against" Falcon).
            string after = text.Substring(match.Index, Math.Min(text.Length - match.Index, match.Length + 30));
            if (after.Contains("you control", StringComparison.OrdinalIgnoreCase) && !after.Contains("opponent", StringComparison.OrdinalIgnoreCase) &&
                !after.Contains("on the field", StringComparison.OrdinalIgnoreCase))
                return 0;
            string n = match.Groups[1].Success && match.Groups[1].Value != "" ? match.Groups[1].Value : match.Groups[5].Value;
            return n.ToLowerInvariant() switch { "one" => 1, "two" => 2, "three" => 3, _ => int.TryParse(n, out int v) ? v : 1 };
        }

        /// <summary>A monster at least this big counts as a strong monster for "big play" purposes.</summary>
        [Tunable("A monster at least this big counts as a strong monster for \"big play\" purposes.")]
        public int StrongMonsterAttack { get; set; } = 2000;

        /// <summary>
        /// A play worth protecting from the opponent's backrow this turn: our current board is lethal (Main Phase 1),
        /// or we can summon two or more strong monsters this turn. Default counts the strong monsters we can Special
        /// Summon or Normal Summon right now; deck brains can count their own combos.
        /// </summary>
        protected virtual async Task<string?> BigPlayPendingAsync(DecisionContext<IdleCmdData> context)
        {
            PendingBigPlayCards.Clear();
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               context.StateData.CanBattlePhase;
            if (battleAhead)
            {
                var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).Select(card => card.Attack);
                var board = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).ToList();
                var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                // Only worth clearing the backrow if what attacks is worth protecting: if they trap an Armageddon
                // Knight or Dark Grepher, that's a trade we're happy with, and the Storm is saved for a real play.
                if (BoardAssessment.TurnsToWin(attackers, defenders, oppLp) == 1 && board.Any(card => !IsExpendable(card)))
                    return "our board is lethal this turn";
            }

            int strong = await StrongSummonsAvailableAsync(context);
            return strong >= 2 ? $"we can summon {strong} strong ({StrongMonsterAttack}+ ATK) monsters this turn" : null;
        }

        /// <summary>
        /// How many strong monsters we can bring out this turn. Default: distinct strong Special Summons on offer plus
        /// one strong Normal Summon. Deck brains override with their own combos (materials, hand costs).
        /// </summary>
        protected virtual Task<int> StrongSummonsAvailableAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            var special = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Attack >= StrongMonsterAttack)
                .Select(card => card.ID).Distinct().ToList();
            var normal = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.ID > 0 && card.Attack >= StrongMonsterAttack);
            PendingBigPlayCards.Clear();
            PendingBigPlayCards.AddRange(special);
            if (normal.ID > 0)
                PendingBigPlayCards.Add(normal.ID);
            return Task.FromResult(special.Count + (normal.ID > 0 ? 1 : 0));
        }

        /// <summary>
        /// A spell/trap worth removing: set, or face-up and staying on the field (Continuous, Field, Equip). A face-up Normal
        /// or Quick-Play Spell or Normal / Counter Trap is only on the field while it resolves: removing it does nothing.
        /// </summary>
        protected static bool WorthRemovingSpellTrap(DynamicCard card) =>
            !card.IsFaceup() || card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip);

        /// <summary>MST target: the opponent's set cards first, then their face-up spells/traps; never our own.</summary>
        // Synchro materials picked in the current selection (StapleTargets): never picked twice. Cleared at each main decision.
        private readonly HashSet<(int, int, int)> _materialsPicked = new();

        protected IList<byte>? StapleTargets(DecisionContext<SelectCardData> context)
        {
            if (CrowTarget(context) is IList<byte> crow)
                return crow;
            if (GraveBanishPick(context) is IList<byte> banish)
                return banish;
            if (ReturnDimensionPick(context) is IList<byte> returned)
                return returned;
            // Synchro materials, when there's a choice: the cheapest by tribute cost (a spent Lyla, or one whose mill would deck us
            // out, goes first; user, 2026-11-12).
            if ((DHint)context.StateData.SelectionHint == DHint.SMATERIAL && context.StateData.SelectableCards is { Length: > 1 } materialCards &&
                materialCards.All(card => card.CurrentCoordinates.Controller == 0 && card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone))
            {
                // Select/unselect prompts come one card at a time, and picking a card already chosen unchooses it: never pick the
                // same card twice in one selection (a loop that hung a whole duel, 2026-11-12). Reset at the next main decision.
                int take = Math.Max(1, context.StateData.Minimum);
                static (int, int, int) Spot(DynamicCard card) => (card.CurrentCoordinates.Controller, card.CurrentCoordinates.Location, card.CurrentCoordinates.Sequence);
                var cheapest = Enumerable.Range(0, materialCards.Length).Where(i => !_materialsPicked.Contains(Spot(materialCards[i])))
                    .OrderBy(i => TributeCost(materialCards[i])).Take(take).ToList();
                if (cheapest.Count == take)
                {
                    foreach (int i in cheapest)
                        _materialsPicked.Add(Spot(materialCards[i]));
                    context.Bot.LogNote($"deck brain: synchro material {string.Join(" + ", cheapest.Select(i => $"{CardNames.Get(materialCards[i].ID)} (cost {TributeCost(materialCards[i]):0.#})"))}");
                    return cheapest.Select(i => (byte)i).ToList();
                }
            }
            // Brionac's discards and targets as planned (here, so every deck brain reaches them before its own discard / target
            // handling: round-robin 44a, Blackwing's discard handler took the prompt and discarded 1 of the planned 2).
            if (BrionacPick(context) is IList<byte> brionac)
                return brionac;
            if (PlannedTarget(context) is IList<byte> planned)
                return planned;
            // A tribute cost asked as a card selection (the release hint: Icarus Attack, Substitoad...): the planned tribute,
            // else the cheapest by TributeCost (seen 2026-11-11: Icarus fell to the wrapper default and tributed Armor Master over Vayu).
            if (SelectTributeCard(context) is IList<byte> tribute)
                return tribute;
            if (_lastStapleActivated != MysticalSpaceTyphoonId)
                return null;
            var hint = (DHint)context.StateData.SelectionHint;
            if (hint is not (DHint.DESTROY or DHint.TARGET))
                return null;
            _lastStapleActivated = 0;
            int? wanted = _mstTargetZone;
            _mstTargetZone = null;
            var theirs = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                .Select((card, index) => (Card: card, Index: index))
                .Where(x => x.Card.CurrentCoordinates.Controller != 0)
                // A card that stays on the field first: a face-up Normal / Quick-Play Spell or Normal / Counter Trap is only
                // there while it resolves, so destroying it does nothing (seen 2026-11-10: our MST took their resolving MST).
                .OrderBy(x => WorthRemovingSpellTrap(x.Card) ? 0 : 1)
                // The card we chained MST at (a continuous card whose effect is resolving), if it's there.
                .ThenBy(x => wanted.HasValue && x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone &&
                              x.Card.CurrentCoordinates.Sequence == wanted.Value ? 0 : 1)
                .ThenBy(x => x.Card.IsFaceup() ? 1 : 0)
                .ThenByDescending(x => SetCardDanger(x.Card))
                .ToList();
            if (theirs.Count == 0)
                return null;
            var pick = theirs[0];
            context.Bot.LogNote($"deck brain: Mystical Space Typhoon target: {(pick.Card.IsFaceup() ? CardNames.Get(pick.Card.ID) : $"the set card in S{pick.Card.CurrentCoordinates.Sequence + 1} ({DescribeSetCardOdds(pick.Card)})")}");
            return new List<byte> { (byte)pick.Index };
        }

        // Face-up spell/trap text that actually gets in our way (or props up their board).
        private static readonly System.Text.RegularExpressions.Regex DisruptiveSpellText = new(
            @"cannot (attack|declare an attack|activate|special summon|normal summon|be normal summoned|conduct)|negate|" +
            @"monsters? your opponent controls (lose|cannot)|when this card leaves the field, destroy|" +
            @"(gains?|increase) [^.]{0,30}atk|your opponent (cannot|must)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether a face-up opponent spell/trap is worth spending targeted removal (MST) on, rather than one of their
        /// set cards: it disrupts us (stops attacks or summons, negates, weakens our monsters), props up their board
        /// (an equip, or Call of the Haunted / Premature Burial: destroying it destroys the monster too), or it is part of
        /// the Grand Mole lock (Ultimate Offering with a Mole around). Otherwise a set card, which might be Mirror Force
        /// or Torrential, matters more. Returns the reason, or null.
        /// </summary>
        protected async Task<string?> FaceUpSpellMattersAsync(DynamicCard card)
        {
            if (!card.IsFaceup() || card.ID <= 0 || card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return null;
            if (card.HasType(CardType.Equip))
                return "an equip propping up their monster";
            if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return null;
            string text = info.CardText ?? "";
            if (ExtraSummonText.IsMatch(text) && BouncerThreat)
                return "it keeps their Grand Mole coming back";
            if (DisruptiveSpellText.IsMatch(text))
                return "it gets in our way";
            return null;
        }

        // The opponent's card MST was chained at (its zone), so the target prompt picks it.
        private int? _mstTargetZone;

        /// <summary>
        /// A continuous / equip / field spell or trap needs to stay on the field for its effect to resolve: chain MST to
        /// its activation and destroying it stops the effect too (Ultimate Offering's summon, Call of the Haunted's
        /// revive, ...). When the opponent has just activated something and one of their face-up cards like that is
        /// worth removing (FaceUpSpellMattersAsync), chain our MST (set, or in hand on our turn) at it. Returns the
        /// chain index, or null.
        /// </summary>
        protected async Task<int?> TryChainMstIntoContinuousAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int mst = Array.FindIndex(options, card => card.IsCode(MysticalSpaceTyphoonId));
            if (mst < 0 || context.Header.LastChainPlayer != 1)
                return null; // no MST to chain, or it isn't the opponent who just activated something
            // Only in response to the continuous card's own effect. If a spell/trap just turned face-up, that's what they
            // activated (e.g. a D-Prison on our attacker): MST only goes at it if it is itself a continuous card that
            // matters; destroying an unrelated continuous card stops nothing.
            bool newActivation = _newFaceUpSpellZones.Count > 0;
            foreach (var card in (await GetEnemySpellCardsAsync()).Where(card => card.IsFaceup() &&
                         (card.HasType(CardType.Continuous) || card.HasType(CardType.Equip) || card.HasType(CardType.Field))))
            {
                if (newActivation && !_newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence))
                    continue;
                if (await FaceUpSpellMattersAsync(card) is not string why)
                    continue;
                if (await DestructionNegatedAsync(context, MysticalSpaceTyphoonId) != null || !Rule(DeckRules.MstIntoContinuous))
                    return null;
                _lastStapleActivated = MysticalSpaceTyphoonId;
                _mstTargetZone = card.CurrentCoordinates.Sequence;
                context.Bot.LogNote($"deck brain: chain Mystical Space Typhoon at {CardNames.Get(card.ID)}: {why}; " +
                                    "destroying a continuous card in response stops its effect too");
                return mst;
            }
            return null;
        }

        /// <summary>
        /// How much an opponent's set spell/trap is worth hitting with targeted removal: set cards with no evidence
        /// against them (still possibly Mirror Force / Torrential) score highest. Ties are broken randomly, never by zone.
        /// 0 for anything else.
        /// </summary>
        protected double SetCardDanger(DynamicCard card)
        {
            if (card.CurrentCoordinates.Controller == 0 || card.IsFaceup() ||
                card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return 0;
            _clearedSetZones.TryGetValue(card.CurrentCoordinates.Sequence, out var e);
            double danger = 1;
            // With the deck lists: how likely it is to be a real trap that stops us (not a bait card like Jar of Greed).
            if (OpponentDeck.StopChance(card.CurrentCoordinates.Sequence) is double stop)
                danger += stop * (e?.NotMirror == true || e?.NotTorrential == true ? PartialReadFactor : 1);
            else if (!TrapsNegated)
            {
                danger += e?.NotMirror == true ? SetCardIsMirrorForceChance * EvidenceResidual : SetCardIsMirrorForceChance;
                danger += e?.NotTorrential == true ? SetCardIsTorrentialChance * EvidenceResidual : SetCardIsTorrentialChance;
            }
            return danger; // (no random tie-break jitter: user, 2026-11-17, no rolls)
        }

        private string DescribeSetCardOdds(DynamicCard card)
        {
            _clearedSetZones.TryGetValue(card.CurrentCoordinates.Sequence, out var e);
            var notes = new List<string>();
            if (e?.NotMirror == true) notes.Add("probably not Mirror Force");
            if (e?.NotTorrential == true) notes.Add("probably not Torrential");
            return notes.Count > 0 ? string.Join(", ", notes) : "no evidence against it being Mirror Force / Torrential";
        }

        /// <summary>Chance any one of the opponent's set cards stops a push (Mirror Force, Torrential, Sakuretsu, D-Prison, ...). Tune from logs.</summary>
        [Tunable("Chance any one of the opponent's set cards stops a push (Mirror Force, Torrential, Sakuretsu, D-Prison, ...). Tune from logs.")]
        public double SetCardStopsPushChance { get; set; } = 0.35;

        /// <summary>
        /// Chance a push (more monsters onto the board to win this turn) gets through the opponent's set cards. Each set
        /// card stops it with SetCardStopsPushChance, less if it already stayed down when Mirror Force / Torrential
        /// would have paid off. Two unknown set cards: ~42%.
        /// </summary>
        protected async Task<(double Chance, string Why)> PushGetsThroughAsync()
        {
            var stops = await SetCardStopChancesAsync();
            // A known Honest in their hand with a face-up LIGHT monster of theirs to back: one battle is theirs for sure (user,
            // 2026-11-11: "pushes won't work under Honest").
            int honest = OpponentMemory.KnownHonest > 0 &&
                         (await GetEnemyMonsterCardsAsync()).Any(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && (card.Attribute & 0x10) != 0)
                ? OpponentMemory.KnownHonest : 0;
            if (honest > 0)
                return (0, $"a known Honest in their hand wins the battle for their LIGHT monster");
            // Their face-down monsters (round-robin 27: "lethal this turn" through a 49%-Ryko face-down, and it was Ryko): an attack
            // has to go through each, and a Ryko flip destroys one of our attackers. A probability, like the set cards.
            var faceDowns = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsFaceup()).ToList();
            double rykoThrough = faceDowns.Aggregate(1.0, (acc, card) => acc * (1 - FaceDownRykoChance(card) * RykoBreaksPush));
            string rykoWhy = faceDowns.Count > 0 ? $"; their {faceDowns.Count} face-down monster(s) Ryko {string.Join("/", faceDowns.Select(c => FaceDownRykoChance(c).ToString("P0")))}" : "";
            if (stops.Count == 0)
                return faceDowns.Count == 0 ? (1, "they have no set cards") : (rykoThrough, $"no set cards{rykoWhy}: {rykoThrough:P0} it gets through");
            int setCount = stops.Count;
            // Our set Solemn Judgment stops any one problem: the most dangerous of their set cards counts as answered.
            bool solemn = await SolemnReadyAsync();
            if (solemn)
                stops.Remove(stops.Max());
            double through = stops.Aggregate(1.0, (acc, stop) => acc * (1 - stop)) * rykoThrough;
            return (through, $"{setCount} set card(s) could stop it ({string.Join(", ", stops.Select(s => s.ToString("P0")))} each" +
                             $"{(OpponentDeckPredictor.HasListData ? ", from the deck lists" : "")}){(solemn ? " (our set Solemn Judgment stops one)" : "")}{rykoWhy}: " +
                             $"{through:P0} it gets through");
        }

        /// <summary>Chance a Ryko flip in the way breaks a lethal push (it destroys one of the attackers). Tune from logs.</summary>
        [Tunable("Chance a Ryko flip in the way breaks a lethal push (it destroys one of the attackers). Tune from logs.")]
        public double RykoBreaksPush { get; set; } = 0.8;

        /// <summary>
        /// Each of their set spells/traps: the chance it can stop a push (our attack or summon). From the deck lists their
        /// cards fit when there's data (a deck without Mirror Force / D-Prison / Bottomless... has few stoppers; cards
        /// already seen are out), otherwise SetCardStopsPushChance. Cut down when it stayed down where a Mirror Force /
        /// Torrential would have paid off.
        /// </summary>
        protected async Task<List<double>> SetCardStopChancesAsync()
        {
            var stops = new List<double>();
            foreach (var card in (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup()))
            {
                _clearedSetZones.TryGetValue(card.CurrentCoordinates.Sequence, out var e);
                double factor = (e?.NotMirror == true ? PartialReadFactor : 1) * (e?.NotTorrential == true ? PartialReadFactor : 1);
                // Without list data and traps negated: only a quick-play spell could stop us (a small share of set cards).
                double fallback = TrapsNegated ? SetCardStopsPushChance * QuickPlayShareOfStoppers : SetCardStopsPushChance;
                stops.Add((OpponentDeck.StopChance(card.CurrentCoordinates.Sequence) ?? fallback) * factor);
            }
            return stops;
        }

        /// <summary>Without deck-list data: the share of push-stopping set cards that are quick-play spells (Book of Moon,
        /// Enemy Controller...), what's left of the threat while traps are negated. Tune from logs.</summary>
        [Tunable("Without deck-list data: the share of push-stopping set cards that are quick-play spells (Book of Moon, Enemy Controller...), what's left of the threat while traps are negated. Tune from logs.")]
        public double QuickPlayShareOfStoppers { get; set; } = 0.2;

        /// <summary>Traps negated: a set card whose chance of stopping an attack is below this is ignored as backrow. Tune from logs.</summary>
        [Tunable("Traps negated: a set card whose chance of stopping an attack is below this is ignored as backrow. Tune from logs.")]
        public double NegatedBackrowIgnoredBelow { get; set; } = 0.1;

        /// <summary>No trap can be activated now (face-up Jinzo on either side, or Royal Decree); read with the backrow odds.</summary>
        protected bool TrapsNegated { get; private set; }

        private const int JinzoId = 77585513;
        private const int RoyalDecreeId = 51452091;

        protected async Task<bool> TrapsNegatedAsync()
        {
            var monsters = (await GetBotMonsterCardsAsync()).Concat(await GetEnemyMonsterCardsAsync());
            var spells = (await CurrentField.GetCardsInSpellZone()).Concat(await GetEnemySpellCardsAsync());
            return monsters.Any(card => card.ID > 0 && card.IsFaceup() && card.IsCode(JinzoId)) ||
                   spells.Any(card => card.ID > 0 && card.IsFaceup() && card.IsCode(RoyalDecreeId));
        }

        /// <summary>Chance that any one of the opponent's set cards is Mirror Force (about 1 in 12 set cards). Tune from logs.</summary>
        [Tunable("Chance that any one of the opponent's set cards is Mirror Force (about 1 in 12 set cards). Tune from logs.")]
        public double SetCardIsMirrorForceChance { get; set; } = 0.08;
        /// <summary>Chance that any one of the opponent's set cards is Torrential Tribute.</summary>
        [Tunable("Chance that any one of the opponent's set cards is Torrential Tribute.")]
        public double SetCardIsTorrentialChance { get; set; } = 0.08;
        /// <summary>Chance that any one of the opponent's set cards is Dimensional Prison. Tune from logs.</summary>
        [Tunable("Chance that any one of the opponent's set cards is Dimensional Prison. Tune from logs.")]
        public double SetCardIsDPrisonChance { get; set; } = 0.1;
        /// <summary>Chance a set card is it when we know they hold it (e.g. seen by Trap Dustshoot) and they have set cards.</summary>
        [Tunable("Chance a set card is it when we know they hold it (e.g. seen by Trap Dustshoot) and they have set cards.")]
        public double KnownStapleSetChance { get; set; } = 0.6;

        /// <summary>Deck brains that do their own chain logic for these staples set this, so the shared rules don't run twice.</summary>
        /// <summary>
        /// The deck brain's version, bumped with every behaviour change (recorded per duel by DuelSim, so analyses can drop
        /// duels played by versions with bugs since fixed). 0: unversioned.
        /// </summary>
        public virtual int BrainVersion => 0;

        /// <summary>
        /// Which list of its deck this brain plays (set by the deck catalog; user, 2026-11-10: "all these decks will eventually
        /// have variants"). The rules mostly follow the cards seen; this is for what differs with the same cards (MachinaJinzo's
        /// Fortress discards a Jinzo more readily). Each deck names its variants in its card-id file.
        /// </summary>
        public string Variant { get; internal set; } = StandardVariant;

        /// <summary>A deck's main list.</summary>
        public const string StandardVariant = "Standard";

        protected virtual bool HandlesStapleChains => false;

        private int _ownTurnParity = -1;

        /// <summary>Whether it is our turn (learned from main-phase and battle prompts, which only happen on our turn).</summary>
        protected bool IsOwnTurn(BotStateHeader header) => _ownTurnParity >= 0 && header.CurrentTurn % 2 == _ownTurnParity;

        // ------------------------------------------------------------------ the opponent's Mirror Force / Torrential Tribute

        private bool _knownMirrorInOpponentHand;
        private bool _knownTorrentialInOpponentHand;

        /// <summary>Dimensional Prison seen in the opponent's hand (e.g. by Trap Dustshoot) and not used since: probably set or held.</summary>
        protected bool KnownDPrisonInOpponentHand { get; private set; }

        /// <summary>Chance the opponent has Mirror Force set right now (refreshed each main phase / battle decision).</summary>
        protected double MirrorForceChance { get; private set; }
        /// <summary>Chance the opponent has Torrential Tribute set right now.</summary>
        protected double TorrentialChance { get; private set; }
        private string? _lastSetZoneRead;

        /// <summary>Chance the opponent has Dimensional Prison set right now (seen in their hand, evidence from attacks).</summary>
        protected double DPrisonChance { get; private set; }

        private readonly Dictionary<int, string> _mirrorForceReason = new();
        private readonly Random _stapleRandom = new();
        private int _mirrorRollTurn = -1;
        private readonly Dictionary<int, bool> _mirrorRolls = new();

        private void ObserveRevealedStaples(IEnumerable<DynamicCard> revealed)
        {
            foreach (var card in revealed)
            {
                int id = BoardAssessment.KnownId(card);
                if (id == MirrorForceId) _knownMirrorInOpponentHand = true;
                if (id == TorrentialTributeId) _knownTorrentialInOpponentHand = true;
                if (id == HeavyStormId) _knownHeavyStormInOpponentHand = true;
                if (id == DimensionalPrisonId) KnownDPrisonInOpponentHand = true;
            }
        }

        // ------------------------------------------------------------------ defence against Mirror Force

        /// <summary>Mirror Force chance from which monsters that work as well in defence go to defence. Tune from logs.</summary>
        [Tunable("Mirror Force chance from which monsters that work as well in defence go to defence. Tune from logs.")]
        public double MirrorDefenceChance { get; set; } = 0.05;

        /// <summary>
        /// Monsters whose main value is an effect that works in defence too (Chaos Sorcerer banishes from defence):
        /// with any reasonable chance of Mirror Force they go to defence, losing only pressure.
        /// </summary>
        protected virtual IReadOnlyCollection<int> DefenceUnderMirrorForce => Array.Empty<int>();

        /// <summary>
        /// When its attack does matter (pressure: a target worth hitting, or a faster clock), defence is a roll: chance
        /// MirrorForceChance x this weight (e.g. 9% -> 27%, 28% -> 84%). Tune from logs.
        /// </summary>
        [Tunable("When its attack does matter (pressure: a target worth hitting, or a faster clock), defence is a roll: chance MirrorForceChance x this weight (e.g. 9% -> 27%, 28% -> 84%). Tune from logs.")]
        public double MirrorDefenceWeight { get; set; } = 3;

        private readonly Dictionary<(int Turn, int CardId), double> _mirrorDefenceRolls = new();

        /// <summary>
        /// Why <paramref name="cardId"/> should be in defence (a DefenceUnderMirrorForce card with a reasonable Mirror
        /// Force chance), or null. Its effect works in defence, so defence only costs pressure:
        ///  - its attack is needed for lethal this turn: never defence;
        ///  - its attack adds nothing (<paramref name="attackMatters"/> false): defence;
        ///  - otherwise a probabilistic call: defence with chance MirrorForceChance x MirrorDefenceWeight (rolled once
        ///    per card per turn), so it still attacks often when Mirror Force is unlikely.
        /// </summary>
        protected string? MirrorDefenceReason(int cardId, int attack, bool countsAlreadyOnField, IEnumerable<DynamicCard> opponents,
            bool battleAhead, bool attackMatters, int turn)
        {
            if (!DefenceUnderMirrorForce.Contains(cardId) || MirrorForceChance < MirrorDefenceChance)
                return null;
            if (battleAhead)
            {
                var board = _ownMonsters.Where(own => own.IsFaceup() && own.IsAttack() && !(countsAlreadyOnField && own.ID == cardId))
                    .Select(own => own.Attack).Append(attack).ToList();
                if (BoardAssessment.TurnsToWin(board, BoardAssessment.Defenders(opponents.ToList(), OpponentMemory), _oppLp) == 1)
                    return null; // its attack wins the game
            }
            if (!attackMatters)
                return $"Mirror Force chance {MirrorForceChance:P0} and its attack adds nothing now: its effect works just as well in defence";
            double defend = Math.Min(1, MirrorForceChance * MirrorDefenceWeight);
            if (!_mirrorDefenceRolls.TryGetValue((turn, cardId), out double roll))
            {
                if (_mirrorDefenceRolls.Count > 32)
                    _mirrorDefenceRolls.Clear();
                roll = _mirrorDefenceRolls[(turn, cardId)] = RollAt;
            }
            return roll < defend
                ? $"Mirror Force chance {MirrorForceChance:P0}: defence chance {defend:P0} (its attack would add pressure), rolled {roll:0.00}; its effect works just as well in defence"
                : null;
        }

        public override async Task<CardPosition> OnSelectPositionAsync(DecisionContext<SelectPositionData> context, CancellationToken ct = default)
        {
            var positions = context.StateData.Positions ?? Array.Empty<byte>();
            int id = context.StateData.CardID;
            // Summoned on their turn (Gorz and its token, a Battle Fader, a trap monster): attack position only if it beats
            // every attacker of theirs that can still attack; otherwise defence takes no damage (seen 2026-11-10: the Gorz
            // token went in attack position with Faultroll and Hyunlei still to attack).
            if (context.Header.CurrentPlayer == 1 && positions.Contains((byte)CardPosition.FaceUpDefence) &&
                positions.Contains((byte)CardPosition.FaceUpAttack))
            {
                await PrepareContextAsync(context, ct);
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo summoned ? summoned.Attack : 0;
                var attackers = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
                var beats = attackers.Where(card => card.Attack > attack).OrderByDescending(card => card.Attack).FirstOrDefault();
                if (beats.ID > 0 && Rule(DeckRules.DefenceOnTheirTurn))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: their {CardNames.Get(beats.ID)} ({beats.Attack}) can still attack it");
                    return CardPosition.FaceUpDefence;
                }
            }
            // Our turn at low life points: in attack position, a bigger monster of theirs attacking it next turn deals the
            // difference, and when that alone is lethal, defence (user, 2026-11-13: "summoning armed wing in attack lost the
            // game ... because gorz attacked armed wing"). Not when our board wins this turn.
            if (context.Header.CurrentPlayer == 0 && positions.Contains((byte)CardPosition.FaceUpDefence) &&
                positions.Contains((byte)CardPosition.FaceUpAttack))
            {
                await PrepareContextAsync(context, ct);
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo summoned ? summoned.Attack : 0;
                int ourLp = await CurrentField.GetBotLifePoints();
                var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).ToList();
                var killer = theirs.Where(card => card.Attack - attack >= ourLp).OrderByDescending(card => card.Attack).FirstOrDefault();
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                bool winning = BoardAssessment.TurnsToWin((await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Select(EffectiveAttack).Append(attack).ToList(), BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory), oppLp) == 1;
                if (killer.ID > 0 && !winning && Rule(DeckRules.DefenceWhenHitIsLethal))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: in attack position their {CardNames.Get(killer.ID)} ({killer.Attack}) " +
                                        $"attacking it deals {killer.Attack - attack}, lethal at our {ourLp} LP");
                    return CardPosition.FaceUpDefence;
                }
            }
            if (DefenceUnderMirrorForce.Contains(id) && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                await PrepareContextAsync(context, ct);
                await RefreshBackrowOddsAsync(context);
                _ownMonsters = await GetBotMonsterCardsAsync();
                int attack = await CardDatabase.GetAsync(id) is DBCardInfo info ? info.Attack : 0;
                bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1;
                var enemy = await GetEnemyMonsterCardsAsync();
                // Summoned now: if it will use its effect this turn (a face-up target), it can't attack anyway. Otherwise its
                // attack matters when there's a battle ahead and something to hit (or a direct attack).
                bool usesEffect = enemy.Any(card => card.IsFaceup() && card.ID > 0);
                bool attackMatters = battleAhead && !usesEffect;
                if (MirrorDefenceReason(id, attack, countsAlreadyOnField: false, enemy, battleAhead, attackMatters, context.Header.CurrentTurn) is string why &&
                    Rule(DeckRules.SummonInDefenceUnderMirror))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(id)} in defence: {why}");
                    return CardPosition.FaceUpDefence;
                }
            }
            return await base.OnSelectPositionAsync(context, ct);
        }

        /// <summary>Chance a monster of ours the opponent can run over in battle actually gets destroyed that way. Tune from logs.</summary>
        [Tunable("Chance a monster of ours the opponent can run over in battle actually gets destroyed that way. Tune from logs.")]
        public double BattleKillChance { get; set; } = 0.6;

        /// <summary>
        /// Chance our face-up monster is sent to the graveyard soon (destroyed, not banished): run over in battle by one
        /// of their face-up monsters, or hit by Mirror Force / Torrential Tribute. Their banishing answers (Dimensional
        /// Prison, Bottomless) don't count: they keep it out of the graveyard.
        /// </summary>
        protected double ReachesGraveChance(DynamicCard ours, IEnumerable<DynamicCard> enemyMonsters)
        {
            int power = ours.IsAttack() ? ours.Attack : ours.Defense;
            bool runOver = enemyMonsters.Any(enemy => enemy.ID > 0 && enemy.IsFaceup() && !enemy.IsDisabled() && enemy.Attack >= power);
            double survive = (1 - (runOver ? BattleKillChance : 0)) * (1 - MirrorForceChance) * (1 - TorrentialChance);
            return 1 - survive;
        }

        /// <summary>Recomputes the chance the opponent has Mirror Force / Torrential Tribute among their set cards.</summary>
        protected async Task RefreshBackrowOddsAsync<T>(DecisionContext<T> context)
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            var gone = (enemy.Grave ?? Array.Empty<int>()).Concat(enemy.Banish ?? Array.Empty<int>()).ToHashSet();
            var setZones = (await GetEnemySpellCardsAsync()).Where(card => !card.IsFaceup())
                .Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();

            // A face-up Jinzo (theirs or ours) or Royal Decree: no trap can be activated, so their set cards are only a threat
            // as quick-play spells (user, 2026-11-10: "the chance of me having anything that's going to punish an attack is
            // very low (0 if I don't have any quick play spells in my deck)").
            bool trapsNegated = await TrapsNegatedAsync();
            if (trapsNegated != OpponentDeck.TrapsNegated && context.Bot.DuelLog != null)
                context.Bot.LogNote(trapsNegated
                    ? "deck brain: traps are negated (face-up Jinzo / Royal Decree): their set cards only threaten us as quick-play spells"
                    : "deck brain: traps work again (no face-up Jinzo / Royal Decree)");
            OpponentDeck.TrapsNegated = trapsNegated;
            TrapsNegated = trapsNegated;

            if (gone.Contains(MirrorForceId)) _knownMirrorInOpponentHand = false;
            if (gone.Contains(TorrentialTributeId)) _knownTorrentialInOpponentHand = false;
            if (gone.Contains(DimensionalPrisonId)) KnownDPrisonInOpponentHand = false;

            // Forget evidence about zones whose set card is gone (a new card set there starts fresh).
            foreach (int zone in _clearedSetZones.Keys.Where(zone => !setZones.Contains(zone)).ToList())
                _clearedSetZones.Remove(zone);

            await ResolveStapleEvidenceAsync(context, setZones);

            // With the Edison deck lists, each set card gets its own chance from the lists their cards fit (copies they run,
            // minus those already seen; a card set after we saw their hand is likely one we saw). Without them, a flat
            // per-card rate. singleCopy (flat rate only): a card they run one of; once used, it's gone.
            bool fromLists = OpponentDeckPredictor.HasListData;
            double Odds(int id, bool known, double perCard, Func<StapleEvidence, bool> cleared, bool singleCopy = true)
            {
                if (setZones.Count == 0 || !fromLists && singleCopy && gone.Contains(id))
                    return 0;
                // Each set card: its own chance, cut down if it stayed down when the opponent would have used it.
                double none = setZones.Aggregate(1.0, (acc, zone) =>
                    acc * (1 - (fromLists ? OpponentDeck.SetZoneChance(zone, id) ?? perCard : perCard) *
                               (_clearedSetZones.TryGetValue(zone, out var e) && cleared(e) ? EvidenceResidual : 1)));
                double chance = 1 - none;
                return known && !fromLists ? Math.Max(KnownStapleSetChance, chance) : chance;
            }

            MirrorForceChance = trapsNegated ? 0 : Odds(MirrorForceId, _knownMirrorInOpponentHand, SetCardIsMirrorForceChance, e => e.NotMirror);
            TorrentialChance = trapsNegated ? 0 : Odds(TorrentialTributeId, _knownTorrentialInOpponentHand, SetCardIsTorrentialChance, e => e.NotTorrential);
            DPrisonChance = trapsNegated ? 0 : Odds(DimensionalPrisonId, KnownDPrisonInOpponentHand, SetCardIsDPrisonChance, e => e.NotDPrison, singleCopy: false);

            // What their set cards likely are, once per turn when it changes (from the deck lists).
            var faceDownMonsters = fromLists
                ? (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsFaceup()).Select(card => (int)card.CurrentCoordinates.Sequence).ToList()
                : new List<int>();
            // Only when a duel log is written: describing a zone scores every candidate card against every list (slow, and
            // the play search's brain twins make thousands of decisions).
            if (fromLists && context.Bot.DuelLog != null && setZones.Count + faceDownMonsters.Count > 0)
            {
                string read =string.Join(" | ", setZones.OrderBy(z => z).Select(z => $"S{z + 1}: {OpponentDeck.DescribeSetZone(z)}")
                    .Concat(faceDownMonsters.OrderBy(z => z).Select(z =>
                        $"M{z + 1}: {OpponentDeck.DescribeMonsterZone(z)} (destroying flip like Ryko {OpponentDeck.FlipDestroyChance(z) ?? 0:P0})")));
                if (read != _lastSetZoneRead)
                {
                    _lastSetZoneRead = read;
                    context.Bot.LogNote($"deck brain: their set cards from the deck lists: {read} " +
                                        $"(Mirror Force {MirrorForceChance:P0}, Torrential {TorrentialChance:P0}, D-Prison {DPrisonChance:P0} among them)");
                }
            }

            int turn = context.Header.CurrentTurn;
            if (turn != _mirrorRollTurn)
            {
                _mirrorRollTurn = turn;
                _mirrorRolls.Clear();
                _mirrorForceReason.Clear();
            }
        }

        /// <summary>
        /// Mirror Force destroys every attack-position monster when one of them attacks. A monster that isn't attacking
        /// this turn while others are is safer in defence. If its DEF survives the opponent's strongest attacker there
        /// is no downside, so always; otherwise roll with the chance Mirror Force is set (a weak DEF can be run over).
        /// Rolled once per monster per turn.
        /// </summary>
        protected bool ShelterFromMirrorForce(DynamicCard card, List<DynamicCard> opponents, bool survivesInDefense)
        {
            if (MirrorForceChance <= 0)
                return false;
            // Not worth protecting: an expendable monster lost to Mirror Force at least spends it, and it keeps the
            // clock up. A low DEF is weak in defence anyway.
            if (IsExpendable(card) || (!survivesInDefense && card.Defense < card.Attack / 2))
                return false;
            bool othersAttack = _ownMonsters.Any(own =>
                !SameZone(own, card) && own.IsFaceup() && own.IsAttack() &&
                (opponents.Count == 0 ? !IsWeak(own) : opponents.Any(target => target.IsFaceup() && IsWorthAttacking(own, target))));
            if (!othersAttack)
                return false;

            int zone = card.CurrentCoordinates.Sequence;
            if (!_mirrorRolls.TryGetValue(zone, out bool shelter))
            {
                double roll = RollAt;
                shelter = survivesInDefense || roll < MirrorForceChance;
                _mirrorRolls[zone] = shelter;
                _mirrorForceReason[zone] = survivesInDefense
                    ? $"not attacking while others do; Mirror Force chance {MirrorForceChance:P0} and its DEF holds, so no downside"
                    : $"not attacking while others do; Mirror Force chance {MirrorForceChance:P0}, rolled {roll:0.00} -> {(shelter ? "shelter" : "stay")}";
            }
            return shelter;
        }

        // ------------------------------------------------------------------ answering an attack

        /// <summary>
        /// During the opponent's Battle Phase: their monsters that can still attack this turn (face-up, attack position,
        /// not yet attacked, and not the one attacking right now: the "attacked" flag is only set once its battle ends,
        /// e.g. when it flipped our Ryko). Removal at that point should hit one of these (e.g. Ryko's flip into the
        /// Pegasus still to come, not the Falcon that already attacked). Empty outside their Battle Phase.
        /// </summary>
        protected async Task<Func<DynamicCard, bool>> StillToAttackThisTurnAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            bool theirBattle = !IsOwnTurn(header) &&
                               phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (!theirBattle)
                return _ => false;
            var current = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            bool NotCurrent(DynamicCard card) => !(current.ID > 0 && card.CurrentCoordinates.Sequence == current.CurrentCoordinates.Sequence);
            // Omega never sets "attacked" on their monsters, and an attack we get no prompt for (seen 2026-11-13: Nova Summoner
            // destroyed our Ryko unseen) leaves no trace: each monster of ours lost since their Battle Phase began counts as one
            // attack (one of them the current battler's). When those cover all their other attackers, none is still to come
            // (user: "nova had already attacked so was no threat").
            var theirAttackers = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack()).ToList();
            int lost = _ourMonstersBeforeTheirBattle.Turn == header.CurrentTurn
                ? Math.Max(0, _ourMonstersBeforeTheirBattle.Count - (await GetBotMonsterCardsAsync()).Count(card => card.ID != 0))
                : 0;
            int earlierAttacks = lost - (current.ID > 0 ? 1 : 0);
            int others = theirAttackers.Count(NotCurrent);
            if (others > 0 && earlierAttacks >= others)
                return _ => false;
            var attacked = _theirAttackedZones.Turn == header.CurrentTurn ? _theirAttackedZones.Zones : new HashSet<int>();
            return card => card.CurrentCoordinates.Controller == 1 &&
                           card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                           card.IsFaceup() && card.IsAttack() && !card.Attacked && NotCurrent(card) &&
                           !attacked.Contains(card.CurrentCoordinates.Sequence);
        }

        // Their monster zones we saw battling this turn (their turn number): Omega never marks their attackers, and the battler is gone by
        // the time a trigger after the battle asks us (seen 2026-11-18: Ryko, flipped by Chimeratech Fortress Dragon's attack, destroyed that
        // same Chimeratech: "the one that had attacked", while the other Chimeratech and Machina Fortress were still to come).
        private (int Turn, HashSet<int> Zones) _theirAttackedZones = (-1, new HashSet<int>());

        /// <summary>At every prompt of ours (PromptArrived) in their Battle Phase: the monster of theirs battling now has attacked.</summary>
        protected async Task TrackTheirAttackersAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            if (IsOwnTurn(header) ||
                phase is not (DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle))
                return;
            var current = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (current.ID == 0 || current.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone)
                return;
            if (_theirAttackedZones.Turn != header.CurrentTurn)
                _theirAttackedZones = (header.CurrentTurn, new HashSet<int>());
            _theirAttackedZones.Zones.Add(current.CurrentCoordinates.Sequence);
        }

        // Our monsters on the field at the last decision before their Battle Phase this turn (their turn number), for counting
        // the attacks we got no prompt for.
        private (int Turn, int Count) _ourMonstersBeforeTheirBattle = (-1, 0);

        /// <summary>At every real decision: before their Battle Phase (their turn, or our turn ending), the count of our monsters.</summary>
        private async Task ObserveMonstersBeforeTheirBattleAsync(BotStateHeader header)
        {
            var phase = (DuelPhase)header.CurrentPhase;
            bool theirBattle = !IsOwnTurn(header) &&
                               phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (theirBattle && _ourMonstersBeforeTheirBattle.Turn == header.CurrentTurn)
                return;
            int count = (await GetBotMonsterCardsAsync()).Count(card => card.ID != 0);
            // On our own turn the count is for their next turn (their Battle Phase may come with no prompt before it).
            _ourMonstersBeforeTheirBattle = (IsOwnTurn(header) ? header.CurrentTurn + 1 : header.CurrentTurn, count);
        }

        /// <summary>What letting the current opponent attack through would cost us.</summary>
        protected sealed record IncomingAttack(
            DynamicCard Attacker,
            DynamicCard Target,
            bool CostsUs,
            bool Lethal,
            bool OurOwnMonster,
            bool WorthRemovingAnyway,
            string Why)
        {
            /// <summary>
            /// Worth banishing even when letting it through costs nothing: a big threat, or a recruiter (removing it
            /// outside battle stops the replacement it would bring out).
            /// </summary>
            public bool WorthBanishingAnyway { get; init; }

            /// <summary>
            /// Worth destroying (Mirror Force) even when letting it through costs nothing: a big threat, or a recruiter
            /// that only recruits when destroyed by battle (Giant Rat). One that recruits on any destruction needs banishing.
            /// </summary>
            public bool WorthDestroyingAnyway { get; init; }

            /// <summary>Why removing it is worth it anyway (empty if it isn't). Only for removal notes, not Necro Gardna's.</summary>
            public string RemovalWhy { get; init; } = "";

            /// <summary>
            /// The only cost is life points: a non-lethal hit that leaves us within reach of their board next turn. A
            /// negate (Necro Gardna) shouldn't be spent on that: it works just as well next turn, at the last moment.
            /// </summary>
            public bool OnlyLifePointsCost { get; init; }
        }

        // Attacker text that rewards its owner when it hits (X-Saber Airbellum's discard, ...).
        private static readonly System.Text.RegularExpressions.Regex BattleDamageEffectText = new(
            @"inflicts? (battle )?damage to your opponent[^.]*:|direct attack[^.]*:|inflicts battle damage",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Reads the attack in progress: does letting it through cost us anything that matters? Life points alone
        /// don't, cards do. It costs us when:
        ///  - it is lethal (or this hit plus the attacks still to come are),
        ///  - it destroys a monster of ours that isn't expendable,
        ///  - it bounces (Grand Mole) a token or Extra Deck monster of ours (lost), a main-deck one just returns to hand,
        ///  - the attacker's "when it inflicts battle damage" effect would land (e.g. Airbellum's discard: a -1),
        ///  - the attacker dies in this battle and its "when destroyed" effect rewards them (Kittytail, Giant Rat).
        /// Removing an attacker we own (taken with Brain Control) is never right: it comes back at the End Phase.
        /// Strong attackers (Extra Deck, 2000+ ATK, bouncers, strong effects) are worth removing anyway.
        /// </summary>
        protected async Task<IncomingAttack?> AssessIncomingAttackAsync(List<DynamicCard> enemyMonsters)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (attacker.ID <= 0)
                return null;
            // Read the attacker's text (floater, bouncer) even if it was summoned this turn.
            await RefreshRewardOnDestroyAsync(enemyMonsters);
            var target = await CurrentField.GetBotBattlingMonster();
            bool direct = target.ID <= 0;
            int lp = await CurrentField.GetBotLifePoints();
            var costs = new List<string>();
            var fine = new List<string>();

            bool ourOwn = attacker.Owner == 0;
            if (ourOwn)
                fine.Add($"{CardNames.Get(attacker.ID)} is our own monster (they took control): it comes back to us");

            int damage = direct ? attacker.Attack
                : target.IsAttack() && attacker.Attack > target.Attack ? attacker.Attack - target.Attack : 0;
            int stillToCome = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, attacker))
                .Sum(card => card.Attack);
            bool lethal = damage >= lp || (direct && damage + stillToCome >= lp && stillToCome < lp);
            if (lethal)
                costs.Add($"{damage} damage would (help) finish us at {lp} LP");
            // Life points matter once they're within reach: a hit that leaves us where their current board kills us
            // next turn costs us, even if it isn't lethal now.
            int theirBoard = enemyMonsters.Where(card => card.IsFaceup()).Sum(card => card.Attack);
            int lpAfter = lp - damage - (direct ? stillToCome : 0);
            bool withinReach = !lethal && damage > 0 && !ourOwn && lpAfter <= theirBoard;
            if (withinReach)
                costs.Add($"it leaves us at {Math.Max(0, lpAfter)} LP, within reach of their board ({theirBoard} ATK) next turn");

            bool bounces = BouncesAttacker(attacker);
            if (!direct && bounces)
            {
                if (ReSummonCost(target) >= 100)
                    costs.Add($"it returns our {CardNames.Get(target.ID)} to the hand: a token / Extra Deck monster is lost");
                else
                    fine.Add($"it only returns our {CardNames.Get(target.ID)} to the hand (and itself too)");
            }
            else if (!direct)
            {
                bool destroyed = !DestroysByEffectInBattle(target, attacker) &&
                                 (target.IsAttack() ? attacker.Attack >= target.Attack : attacker.Attack > target.Defense);
                if (destroyed && !IsExpendable(target))
                    costs.Add($"it would destroy our {CardNames.Get(target.ID)}");
                else if (destroyed)
                    fine.Add($"our {CardNames.Get(target.ID)} is expendable");
                else
                    fine.Add($"our {CardNames.Get(target.ID)} survives");
            }

            if (damage > 0 && await TextMatchesAsync(attacker.ID, BattleDamageEffectText))
                costs.Add($"its battle-damage effect would land (a {damage} hit)");

            bool attackerDies = !direct && !bounces && (target.IsAttack() && target.Attack >= attacker.Attack || DestroysByEffectInBattle(target, attacker));
            if (attackerDies && RewardsDestruction(attacker))
                costs.Add($"it would die in battle and its effect would reward them: banish/stop it first");

            // Worth removing anyway: only a big threat (not e.g. Giant Rat or Kinka-byo). A recruiter is still worth
            // removing outside battle, which stops its replacement: by any removal if it only recruits when destroyed by
            // battle (Giant Rat), else only by banishing.
            bool strong = !ourOwn && (bounces || await IsBigThreatAsync(attacker));
            bool recruiter = !ourOwn && IsRecruiter(attacker);
            bool battleOnlyRecruiter = recruiter && RecruitsOnlyByBattle(attacker);

            bool costsUs = !ourOwn && costs.Count > 0 || lethal;
            string why = $"{CardNames.Get(attacker.ID)} attacks {(direct ? "directly" : CardNames.Get(target.ID))}: " +
                         (costs.Count > 0 ? string.Join("; ", costs) : "it costs us nothing that matters") +
                         (fine.Count > 0 ? $" ({string.Join("; ", fine)})" : "");
            string removalWhy = strong ? "; it is a big threat, worth removing anyway"
                : battleOnlyRecruiter ? "; removing it outside battle stops the monster it would bring out"
                : recruiter ? "; banishing it stops the monster it would bring out"
                : "";
            return new IncomingAttack(attacker, target, costsUs, lethal, ourOwn, strong, why)
            {
                WorthBanishingAnyway = strong || recruiter,
                WorthDestroyingAnyway = strong || battleOnlyRecruiter,
                RemovalWhy = removalWhy,
                OnlyLifePointsCost = withinReach && costs.Count == 1
            };
        }

        // ------------------------------------------------------------------ our Gorz

        /// <summary>
        /// Gorz in hand and nothing on our field: Gorz answers a direct attack (and deters it), so don't set cards or
        /// blockers that would switch it off. Unless Gorz can't save us: the opponent's biggest face-up attacker
        /// already deals lethal damage in one hit.
        /// </summary>
        protected async Task<bool> KeepGorzLiveAsync<T>(DecisionContext<T> context, List<DynamicCard> enemyMonsters)
        {
            if (!(await GetBotHandCardsAsync()).Any(card => card.IsCode(GorzId)))
                return false;
            if ((await GetBotMonsterCardsAsync()).Count > 0 || (await CurrentField.GetCardsInSpellZone()).Any(card => card.ID > 0))
                return false;
            int biggest = enemyMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (biggest >= await CurrentField.GetBotLifePoints())
            {
                context.Bot.LogNote($"deck brain: Gorz can't save us from a {biggest} ATK hit: play normally");
                return false;
            }
            // A direct hit that discards from our hand (X-Saber Airbellum) can take Gorz before it's used: an empty field
            // isn't a safe plan then, so develop the board instead of holding everything back for Gorz.
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(enemy.ID) is DBCardInfo info && HandDiscardOnHitText.IsMatch(info.CardText ?? ""))
                {
                    context.Bot.LogNote($"deck brain: don't keep the field empty for Gorz: their {CardNames.Get(enemy.ID)}'s direct hit can discard it; develop instead");
                    return false;
                }
            }
            // Not always the priority: weigh what Gorz would get us (a 2700 body plus a token copying their biggest hit,
            // if they attack into it at all) against what our other plays would do this turn. Too complex to simulate
            // fully, so it's a probability, rolled once per turn: hold chance = base + per 1000 of their biggest
            // attacker - per alternative play we have, kept within 0-100% (it can be a certain hold or a certain play).
            int turn = context.Header.CurrentTurn;
            if (_gorzHoldRoll.Turn != turn)
            {
                int plays = await AlternativePlaysAsync();
                // Solemn Judgment in hand too: it waits for Gorz (see ChooseTrapSetAsync), so it isn't another play, and
                // Gorz gets more priority than usual (Solemn is set right after it comes out).
                bool solemnInHand = (await GetBotHandCardsAsync()).Any(card => card.IsCode(SolemnJudgmentId));
                if (solemnInHand)
                    plays = Math.Max(0, plays - 1);
                double chance = Math.Clamp(GorzHoldBase + GorzHoldPerThousand * biggest / 1000.0 - GorzHoldPerPlay * plays +
                                           (solemnInHand ? GorzHoldWithSolemn : 0), GorzHoldMin, GorzHoldMax);
                double roll = RollAt;
                string why = $"Gorz would bring a 2700 body + a token copying their biggest hit ({biggest}); we have {plays} other play(s) this turn" +
                             $"{(solemnInHand ? ", Solemn Judgment waits in hand for Gorz (+priority)" : "")}: hold chance {chance:P0}, rolled {roll:0.00}";
                _gorzHoldRoll = (turn, roll < chance, why);
                context.Bot.LogNote(roll < chance
                    ? $"deck brain: keep the field empty so Gorz stays live ({why})"
                    : $"deck brain: play normally instead of keeping Gorz live ({why})");
            }
            return _gorzHoldRoll.Hold;
        }

        private (int Turn, bool Hold, string Why) _gorzHoldRoll = (-1, false, "");

        /// <summary>Hold-for-Gorz chance before adjustments. Tune from logs.</summary>
        [Tunable("Hold-for-Gorz chance before adjustments. Tune from logs.")]
        public double GorzHoldBase { get; set; } = 0.45;
        /// <summary>Added per 1000 ATK of their biggest attacker (the token copies that hit). Tune from logs.</summary>
        [Tunable("Added per 1000 ATK of their biggest attacker (the token copies that hit). Tune from logs.")]
        public double GorzHoldPerThousand { get; set; } = 0.1;
        /// <summary>Taken off per other play we could make this turn. Tune from logs.</summary>
        [Tunable("Taken off per other play we could make this turn. Tune from logs.")]
        public double GorzHoldPerPlay { get; set; } = 0.15;
        /// <summary>Added to the hold-for-Gorz chance when Solemn Judgment is in hand too (it waits for Gorz). Tune from logs.</summary>
        [Tunable("Added to the hold-for-Gorz chance when Solemn Judgment is in hand too (it waits for Gorz). Tune from logs.")]
        public double GorzHoldWithSolemn { get; set; } = 0.3;
        [Tunable("")]
        public double GorzHoldMin { get; set; } = 0;
        [Tunable("")]
        public double GorzHoldMax { get; set; } = 1;

        /// <summary>
        /// How many real plays we have this turn besides keeping Gorz live (for the Gorz hold roll). Default: monsters
        /// we could summon and traps we could set. Deck brains add their own (graveyard plays).
        /// </summary>
        protected virtual async Task<int> AlternativePlaysAsync()
        {
            var hand = await GetBotHandCardsAsync();
            return hand.Count(card => card.ID > 0 && !card.IsCode(GorzId) &&
                                      (card.HasType(CardType.Monster) || card.HasType(CardType.Trap)));
        }

        /// <summary>
        /// Gorz triggers on each direct hit, and its token copies the damage, so wait for the opponent's biggest
        /// attacker. Use it now if this attacker is the biggest one left, or if taking every remaining attack before
        /// Gorz comes out would lose the game. Returns the reason to summon now, or null to wait.
        /// </summary>
        // A battle-damage effect that takes a card from our hand (X-Saber Airbellum discards one at random).
        protected static readonly System.Text.RegularExpressions.Regex HandDiscardOnHitText = new(
            @"inflicts? battle damage[^.]{0,120}?(discard|send)[^.]{0,60}?hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected async Task<string?> GorzNowAsync<T>(DecisionContext<T> context, List<DynamicCard> enemyMonsters)
        {
            var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            if (attacker.ID <= 0)
                return "special summon Gorz (attacker unknown)";

            // A hit whose effect takes a card from our hand (X-Saber Airbellum) could take Gorz itself: use it now.
            if (await CardDatabase.GetAsync(attacker.ID) is DBCardInfo attackerInfo && HandDiscardOnHitText.IsMatch(attackerInfo.CardText ?? ""))
                return $"special summon Gorz: {CardNames.Get(attacker.ID)}'s hit would discard from our hand, and that could be Gorz";

            if (enemyMonsters.Count == 0)
                enemyMonsters = await GetEnemyMonsterCardsAsync();
            var stillToAttack = enemyMonsters
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && !SameZone(card, attacker))
                .ToList();
            int biggestLeft = stillToAttack.Select(card => card.Attack).DefaultIfEmpty(0).Max();
            if (attacker.Attack >= biggestLeft)
                return $"special summon Gorz: {CardNames.Get(attacker.ID)} ({attacker.Attack}) is the biggest attacker, biggest token";

            int lp = await CurrentField.GetBotLifePoints();
            int remainingDamage = stillToAttack.Sum(card => card.Attack);
            if (remainingDamage >= lp || !Rule(DeckRules.GorzWaitsForBiggest))
                return $"special summon Gorz: waiting for the {biggestLeft} attacker would lose ({remainingDamage} damage still to come vs {lp} LP)";

            context.Bot.LogNote($"deck brain: hold Gorz: {CardNames.Get(attacker.ID)} ({attacker.Attack}) isn't the biggest; wait for the {biggestLeft} attacker ({remainingDamage} damage to come vs {lp} LP)");
            return null;
        }

        /// <summary>
        /// The Jar of Greed / Gorz trick: Gorz needs us to control no cards when the direct attack hits. If a set draw
        /// card (Jar of Greed, Legacy of Yata-Garasu) is our only card, flip it in response to the direct attack: it
        /// draws a card and clears the way for Gorz. Returns the chain index, or null.
        /// </summary>
        protected async Task<int?> TryDrawCardForGorzAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int setDraw = Array.FindIndex(options, card =>
                card.IsCode(JarOfGreedId, LegacyOfYataGarasuId) && card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone);
            if (setDraw < 0 || IsOwnTurn(context.Header))
                return null;
            if (!(await GetBotHandCardsAsync()).Any(card => card.IsCode(GorzId)) ||
                (await GetBotMonsterCardsAsync()).Count > 0 ||
                (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0) != 1 ||
                !await CurrentField.IsBotUnderAttack())
                return null;
            // The attack must still be live: if our answer already removed the attacker (Dimensional Prison banished it),
            // there's no hit coming and the trick only shows them Gorz.
            var liveAttacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            var theirMonsters = await GetEnemyMonsterCardsAsync();
            if (liveAttacker.ID <= 0 || !theirMonsters.Any(card => card.ID == liveAttacker.ID &&
                                                                card.CurrentCoordinates.Sequence == liveAttacker.CurrentCoordinates.Sequence))
            {
                context.Bot.LogNote($"deck brain: keep {CardNames.Get(options[setDraw].ID)} down: the attacker is already gone, no hit is coming");
                return null;
            }
            // Only on the attack we want Gorz on (the biggest one, or the one waiting would lose to): clearing the field
            // earlier just tells them Gorz is in hand, and they can hold back their bigger attacker.
            if (await GorzNowAsync(context, await GetEnemyMonsterCardsAsync()) is not string gorzWhy)
            {
                context.Bot.LogNote($"deck brain: keep {CardNames.Get(options[setDraw].ID)} down for now: this isn't the attack we want " +
                                    "Gorz on, and clearing the field early would show them Gorz");
                return null;
            }
            if (!Rule(DeckRules.DrawForGorz))
                return null;
            context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[setDraw].ID)}: it's our only card and the attack is direct, " +
                                $"so drawing with it enables Gorz on this attack ({gorzWhy})");
            return setDraw;
        }

        // ------------------------------------------------------------------ shared chain rules for our staples

        /// <summary>
        /// Default use of Gorz, Mirror Force and Torrential Tribute for deck brains without their own rules:
        /// Gorz on the biggest direct hit, Mirror Force when attacked with 2+ attack-position monsters (or 1 if it is
        /// our only answer), Torrential when the opponent has 2+ monsters and more than us. Returns null to fall through.
        /// </summary>
        protected async Task<int?> TryStapleChainAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (context.StateData.Forced ||
                !options.Any(card => card.IsCode(GorzId, MirrorForceId, TorrentialTributeId, BottomlessTrapHoleId, JarOfGreedId, LegacyOfYataGarasuId,
                    MysticalSpaceTyphoonId, DimensionalPrisonId, TrapDustshootId, DDCrowId, SolemnJudgmentId, CompulsoryEvacuationDeviceId, WabokuId)) &&
                !options.Any(IsOurSetCard))
                return null;

            var bait = await TryChainBaitAsync(context);
            if (bait.HasValue)
                return bait.Value;
            var mstIntoContinuous = await TryChainMstIntoContinuousAsync(context);
            if (mstIntoContinuous.HasValue)
                return mstIntoContinuous.Value;
            var jarForGorz = await TryDrawCardForGorzAsync(context);
            if (jarForGorz.HasValue)
                return jarForGorz.Value;

            // Gorz, Mirror Force / Dimensional Prison on an attack, Bottomless / Torrential on a summon,
            // Trap Dustshoot: the shared responses.
            var response = await TryChainResponsesAsync(context, options, IsOwnTurn(context.Header));
            return response is int index && index >= 0 ? index : null;
        }

        // ------------------------------------------------------------------ answering the opponent's summon

        protected const int BottomlessTrapHoleId = 29401950;

        // Opponent monster zones as of the previous decision, the zones that last changed (their latest summon), and
        // whether we've already chained an answer to that summon.
        private HashSet<(int Zone, int Id)> _enemyZoneSnapshot = new();
        private HashSet<int> _latestArrivalZones = new();
        private int _arrivalStamp;
        private (int Turn, int Phase) _arrivalWhen = (-1, -1);
        private int _answeredArrivalStamp = -1;

        /// <summary>Called at every decision: notices monsters that arrived on the opponent's field since the last one.</summary>
        private void TrackEnemyArrivals(CardInfo enemySide, object decision)
        {
            // Once per prompt: a second look in the same prompt would see no change and forget what just arrived.
            if (ReferenceEquals(decision, _lastTrackedDecision))
                return;
            _lastTrackedDecision = decision;
            var now = new HashSet<(int Zone, int Id)>();
            var zones = enemySide.MonsterZones ?? Array.Empty<CardArray>();
            for (int zone = 0; zone < zones.Length; zone++)
            {
                var ids = zones[zone].Cards ?? Array.Empty<int>();
                if (ids.Length > 0)
                    now.Add((zone, ids[0]));
            }
            var arrived = now.Where(x => !_enemyZoneSnapshot.Contains(x)).Select(x => x.Zone).ToHashSet();
            // A face-down card (id 0) in a zone that now shows a card: flipped face-up since the previous decision.
            _flippedFaceUpZones = now.Where(x => x.Id > 0 && _enemyZoneSnapshot.Contains((x.Zone, 0))).Select(x => x.Zone).ToHashSet();
            // Round-robin 29a (user, 2026-11-13: "assume it's always a ryko if omega doesn't show you"): a face-down monster
            // arriving while their Super-Nimble Mega Hamster has just turned face-up this turn is what the Hamster set.
            if (now.Any(x => x.Id == SuperNimbleMegaHamsterId && _flippedFaceUpZones.Contains(x.Zone)))
                _hamsterFlipTurn = _currentHeader.CurrentTurn;
            foreach (var (zone, id) in now.Where(x => x.Id == 0 && arrived.Contains(x.Zone)))
                if (_hamsterFlipTurn == _currentHeader.CurrentTurn)
                    _hamsterSetZones.Add(zone);
            _hamsterSetZones.RemoveWhere(zone => !now.Contains((zone, 0)));
            if (arrived.Count > 0)
            {
                _latestArrivalZones = arrived;
                _arrivalStamp++;
                _arrivalWhen = (_currentHeader.CurrentTurn, _currentHeader.CurrentPhase);
            }
            _enemyZoneSnapshot = now;

            // Spell/trap zones: a card that just turned face-up (activated from the set position or the hand) is what
            // the opponent just activated. Face-down cards report id 0.
            var spells = new HashSet<(int Zone, int Id)>();
            var spellZones = enemySide.SpellZones ?? Array.Empty<CardArray>();
            for (int zone = 0; zone < spellZones.Length; zone++)
            {
                var ids = spellZones[zone].Cards ?? Array.Empty<int>();
                if (ids.Length > 0 && ids[0] > 0)
                    spells.Add((zone, ids[0]));
            }
            _newFaceUpSpellZones = spells.Where(x => !_enemySpellSnapshot.Contains(x)).Select(x => x.Zone).ToHashSet();
            _enemySpellSnapshot = spells;
            // When each face-down card of theirs was set (a card set this turn can't be activated yet).
            for (int zone = 0; zone < Math.Min(5, spellZones.Length); zone++)
            {
                var ids = spellZones[zone].Cards ?? Array.Empty<int>();
                bool faceDown = ids.Length > 0 && ids[0] == 0;
                if (!faceDown)
                    _enemySetTurn.Remove(zone);
                else if (!_enemySetTurn.ContainsKey(zone))
                    _enemySetTurn[zone] = _currentHeader.CurrentTurn;
            }
        }

        private readonly Dictionary<int, int> _enemySetTurn = new();

        private const int SuperNimbleMegaHamsterId = 5220687;
        private int _hamsterFlipTurn = -1;
        // Their monster zones holding the face-down monster a Mega Hamster set (a flip monster from the deck: Ryko).
        private readonly HashSet<int> _hamsterSetZones = new();

        /// <summary>Their face-down monster here was set by Super-Nimble Mega Hamster (assumed Ryko).</summary>
        protected bool HamsterSet(int zone) => _hamsterSetZones.Contains(zone);

        /// <summary>Their face-down spell/trap in this zone was set this turn, so it can't be activated in response yet.</summary>
        protected bool EnemySetThisTurn(int zone) => _enemySetTurn.TryGetValue(zone, out int turn) && turn == _currentHeader.CurrentTurn;

        private object? _lastTrackedDecision;
        private HashSet<(int Zone, int Id)> _enemySpellSnapshot = new();
        private HashSet<int> _newFaceUpSpellZones = new();

        /// <summary>Part of the opponent's latest summon (arrived on their field since the decision before it).</summary>
        /// <summary>A monster effect that starts on its own summon: "When / If this card is (Normal / Special / Flip) Summoned".</summary>
        private static readonly System.Text.RegularExpressions.Regex OnSummonTriggerText = new(
            @"\b(when|if) this card is (normal |special |flip |tribute )?summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// What the opponent most likely just activated (their chain link at this prompt), and how we know. Omega doesn't say
        /// which card a chain link is (card Status is always 0, chain targets mostly empty), so it's read from what changed
        /// on their field (user, 2026-11-12, round-robin 15: Pot of Greed was the link, but a Machina Fortress out since
        /// their last turn was taken for it, and its battle-destruction effect "threatened" our Solemn Judgment):
        ///  1. a spell/trap that just turned face-up (or arrived face-up from the hand): that is the link, for certain, and
        ///     no monster is guessed alongside it;
        ///  2. otherwise only a monster whose effect fits this moment: one that arrived this phase with an on-summon effect,
        ///     or a Flip monster that just turned face-up. A monster that has just been sitting there (Fortress: "destroyed
        ///     by battle") isn't activating anything.
        /// Empty when nothing fits (a hand or graveyard effect, an ignition effect we can't see): then nothing is assumed.
        /// </summary>
        protected async Task<(List<DynamicCard> Cards, string How)> ActivatedNowAsync()
        {
            var spells = (await GetEnemySpellCardsAsync())
                .Where(card => card.IsFaceup() && card.ID > 0 && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence)).ToList();
            if (spells.Count > 0)
                return (spells, "a spell/trap that just turned face-up is the chain link");
            var found = new List<DynamicCard>();
            foreach (var card in (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                    continue;
                string text = info.CardText ?? "";
                if (IsLatestArrival(card) && OnSummonTriggerText.IsMatch(text) ||
                    _flippedFaceUpZones.Contains(card.CurrentCoordinates.Sequence) && FlipEffectText.IsMatch(text))
                    found.Add(card);
            }
            return (found, found.Count > 0 ? "a monster whose summon / flip effect fits this moment" : "nothing on their field fits");
        }

        // Their monster zones holding a card that was face-down at the previous decision and is face-up now (a Flip).
        private HashSet<int> _flippedFaceUpZones = new();

        /// <remarks>
        /// Only in the turn and phase it arrived: an arrival is a guess at what is activating an effect now (Omega doesn't
        /// say which card a chain link is), and a monster that came out earlier isn't "just summoned" any more (round-robin 15:
        /// a Machina Fortress out since their last turn was taken as the activator of their Pot of Greed, and Solemn Judgment
        /// went off as a doomed card).
        /// </remarks>
        protected bool IsLatestArrival(DynamicCard card) =>
            card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
            _arrivalWhen == (_currentHeader.CurrentTurn, _currentHeader.CurrentPhase) &&
            _latestArrivalZones.Contains(card.CurrentCoordinates.Sequence);

        /// <summary>Whether we've already chained a card answering the opponent's latest summon.</summary>
        protected bool LatestSummonAnswered => _answeredArrivalStamp == _arrivalStamp;

        protected void MarkLatestSummonAnswered() => _answeredArrivalStamp = _arrivalStamp;

        /// <summary>
        /// Answers the opponent's summon with Bottomless Trap Hole or Torrential Tribute: one card per summon, never a
        /// second (Torrential on top of Bottomless just makes Bottomless fizzle).
        ///  1. Torrential first, if it's worth it. If Bottomless could take the summoned monster alone and Torrential is
        ///     only worth it because of that monster, hold Torrential for a bigger board.
        ///  2. Bottomless only if Torrential wasn't chosen.
        ///  - Already answered this summon (our card is in the chain): nothing more.
        /// Returns the option index, -1 to pass, or null when neither card is offered.
        /// </summary>
        protected async Task<int?> AnswerSummonAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int bottomless = Array.FindIndex(options, card => card.IsCode(BottomlessTrapHoleId));
            int torrential = Array.FindIndex(options, card => card.IsCode(TorrentialTributeId));
            if (bottomless < 0 && torrential < 0)
                return null;

            var enemy = await GetEnemyMonsterCardsAsync();
            // Read what just arrived (a monster flipped face-up this moment, e.g. their Ryko, must count as a spent FLIP
            // monster in the Torrential maths, not as a card they lose).
            await RefreshRewardOnDestroyAsync(enemy);
            var ours = await GetBotMonsterCardsAsync();
            // Bottomless takes the summoned 1500+ ATK monsters. (If the summon wasn't tracked, assume all of them.)
            var bottomlessTargets = enemy.Where(card => card.IsFaceup() && card.Attack >= 1500 && IsLatestArrival(card)).ToList();
            if (bottomlessTargets.Count == 0)
                bottomlessTargets = enemy.Where(card => card.IsFaceup() && card.Attack >= 1500).ToList();
            var rest = enemy.Where(card => !bottomlessTargets.Any(t => SameZone(t, card))).ToList();

            // One card per summon: whatever we chain second would hit the same monster (Torrential on top of Bottomless
            // just makes Bottomless fizzle), so once one is in the chain, nothing else goes on it.
            if (LatestSummonAnswered && Rule(DeckRules.OneAnswerPerSummon))
            {
                context.Bot.LogNote("deck brain: this summon is already answered by a card in the chain: don't chain a second card for it");
                return -1;
            }

            // Decide on the mass card first: Torrential, if it's worth it.
            var all = torrential >= 0 ? TorrentialWorthIt(enemy, ours) : (Worth: false, Why: "");
            bool torrentialLive = torrential >= 0 && all.Worth && await DestructionNegatedAsync(context, TorrentialTributeId) == null &&
                                  Rule(DeckRules.TorrentialTribute);
            bool bottomlessLive = bottomless >= 0 && bottomlessTargets.Count > 0 &&
                                  await DestructionNegatedAsync(context, BottomlessTrapHoleId) == null &&
                                  Rule(DeckRules.BottomlessTrapHole);
            if (torrentialLive)
            {
                // ...except when Torrential is only worth it because of the summoned monster and Bottomless can take
                // that one alone: then Bottomless banishes it and Torrential stays set for a bigger board.
                var withoutSummon = TorrentialWorthIt(rest, ours);
                if (!bottomlessLive || withoutSummon.Worth || !Rule(DeckRules.BottomlessOverTorrential))
                {
                    MarkLatestSummonAnswered();
                    context.Bot.LogNote($"deck brain: chain Torrential Tribute: {all.Why}" +
                                        (bottomlessLive ? " (worth it even without the summoned monster, so no Bottomless)" : ""));
                    return torrential;
                }
                context.Bot.LogNote($"deck brain: hold Torrential Tribute: only worth it because of the summoned monster ({withoutSummon.Why} without it); Bottomless takes that one alone");
            }
            else if (torrential >= 0)
                context.Bot.LogNote($"deck brain: hold Torrential Tribute: {all.Why}");

            // Then the single card, only when Torrential wasn't chosen.
            if (bottomlessLive)
            {
                MarkLatestSummonAnswered();
                context.Bot.LogNote($"deck brain: chain Bottomless Trap Hole: opponent summoned {string.Join(", ", bottomlessTargets.Select(card => $"{CardNames.Get(card.ID)} ({card.Attack})"))}");
                return bottomless;
            }
            return -1;
        }

        /// <summary>
        /// Whether wiping every monster with Torrential Tribute trades in our favour, by both count and quality:
        /// the opponent must lose at least 2 cards and more than we do, and the threat value of what they lose must
        /// beat what we lose (losing our Dark Armed Dragon to kill two 1400s isn't worth it). Tokens count as cards,
        /// valued by their size like any other monster.
        /// </summary>
        protected (bool Worth, string Why) TorrentialWorthIt(List<DynamicCard> enemyMonsters, List<DynamicCard> ourMonsters)
        {
            // (A flipped Ryko counts as a card worth its stats: TargetThreat values a spent flip monster by its stats alone.)
            enemyMonsters = enemyMonsters.Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
            int Value(DynamicCard card) =>
                card.IsFaceup() ? TargetThreat(card)
                : OpponentMemory.FaceDownDefense(card) + 500; // unknown face-down: its likely DEF (deck read) + a small effect
            int theirCards = enemyMonsters.Count;
            int ourCards = ourMonsters.Count;
            int theirValue = enemyMonsters.Sum(Value);
            int ourValue = ourMonsters.Sum(Value);
            bool worth = theirCards >= 2 && theirCards > ourCards && theirValue > ourValue;
            return (worth, $"opponent loses {theirCards} card(s) worth {theirValue}, we lose {ourCards} worth {ourValue} -> {(worth ? "yes" : "no")}");
        }

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            if (context.StateData.EffectCard.IsCode(GorzId))
            {
                var why = await GorzNowAsync(context, await GetEnemyMonsterCardsAsync());
                if (why != null)
                    context.Bot.LogNote($"deck brain: {why}");
                return why != null;
            }
            // Our own optional effects are worth using by default (Cyber Phoenix's draw, a search, a revive): the default
            // brain declined every one. The exceptions are effects that cost us a lot: wiping our own board ("destroy all
            // cards", "send all") or paying life points. Deck brains decide those (and any card they know better).
            var card = context.StateData.EffectCard;
            // Ryko's flip destroys a card on the field: with nothing of theirs there it would hit our own (seen 2026-11-09).
            if (card.IsCode(RykoId) && card.CurrentCoordinates.Controller == 0 &&
                (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count == 0)
            {
                context.Bot.LogNote("deck brain: don't use Ryko's flip: they have no card on the field (it would destroy our own)");
                return false;
            }
            if (card.ID > 0 && card.CurrentCoordinates.Controller == 0)
            {
                string text = await CardDatabase.GetAsync(card.ID) is DBCardInfo info ? info.CardText ?? "" : "";
                if (CostlyOptionalEffect.IsMatch(text))
                {
                    context.Bot.LogNote($"deck brain: don't use {CardNames.Get(card.ID)}'s optional effect: it costs us a lot (wipes our board or pays life points)");
                    return false;
                }
                context.Bot.LogNote($"deck brain: use {CardNames.Get(card.ID)}'s optional effect");
                return true;
            }
            return await base.OnSelectEffectYnAsync(context, ct);
        }

        // Optional effects that cost us a lot: our board wiped too, or life points paid.
        private static readonly System.Text.RegularExpressions.Regex CostlyOptionalEffect = new(
            @"destroy all cards|send all (other )?cards|destroy all monsters on the field|pay \d+ (life points|lp)|pay half",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
