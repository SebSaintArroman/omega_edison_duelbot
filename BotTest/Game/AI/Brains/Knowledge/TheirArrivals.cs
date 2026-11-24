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
    /// What arrived on their field since the last decision (summons, sets, flips, activations), for the answers to it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ answering the opponent's summon


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
            if (now.Any(x => x.Id == SuperNimbleMegaHamster && _flippedFaceUpZones.Contains(x.Zone)))
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
    }
}
