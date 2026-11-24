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
    /// Evidence from staples they didn't use: their set cards stayed down through an attack worth a Mirror Force, they didn't
    /// Storm our backrow... each lowers the chance they hold that staple.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
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
    }
}
