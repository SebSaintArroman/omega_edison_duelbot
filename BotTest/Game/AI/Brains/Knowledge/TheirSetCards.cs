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
    /// Their set spells/traps: the chance each is Mirror Force, Torrential Tribute, Dimensional Prison or another stopper,
    /// updated from the deck read and what they revealed (RefreshBackrowOddsAsync), and whether a push gets through them.
    /// Mirror Force and Torrential Tribute are limited to one per deck, so once their copy is in their graveyard or
    /// banished, it can be ignored.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
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


        protected async Task<bool> TrapsNegatedAsync()
        {
            var monsters = (await GetBotMonsterCardsAsync()).Concat(await GetEnemyMonsterCardsAsync());
            var spells = (await CurrentField.GetCardsInSpellZone()).Concat(await GetEnemySpellCardsAsync());
            return monsters.Any(card => card.ID > 0 && card.IsFaceup() && card.IsCode(Jinzo)) ||
                   spells.Any(card => card.ID > 0 && card.IsFaceup() && card.IsCode(RoyalDecree));
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
                if (id == MirrorForce) _knownMirrorInOpponentHand = true;
                if (id == TorrentialTribute) _knownTorrentialInOpponentHand = true;
                if (id == HeavyStorm) _knownHeavyStormInOpponentHand = true;
                if (id == DimensionalPrison) KnownDPrisonInOpponentHand = true;
            }
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

            if (gone.Contains(MirrorForce)) _knownMirrorInOpponentHand = false;
            if (gone.Contains(TorrentialTribute)) _knownTorrentialInOpponentHand = false;
            if (gone.Contains(DimensionalPrison)) KnownDPrisonInOpponentHand = false;

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

            MirrorForceChance = trapsNegated ? 0 : Odds(MirrorForce, _knownMirrorInOpponentHand, SetCardIsMirrorForceChance, e => e.NotMirror);
            TorrentialChance = trapsNegated ? 0 : Odds(TorrentialTribute, _knownTorrentialInOpponentHand, SetCardIsTorrentialChance, e => e.NotTorrential);
            DPrisonChance = trapsNegated ? 0 : Odds(DimensionalPrison, KnownDPrisonInOpponentHand, SetCardIsDPrisonChance, e => e.NotDPrison, singleCopy: false);

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
    }
}
