using BotTest.Game.Field;
using BotTest.Utils;
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
    /// Monsters that bounce their attacker (Grand Mole): tracked on their field and in their hand, the Mole lock, and
    /// what we do about it (no sets into it, on-summon monsters gain from the bounce).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ monsters that bounce their attacker

        /// <summary>
        /// Opponent monsters that return the monster battling them to the hand (Grand Mole). A token returned to the
        /// hand is gone and an Extra Deck monster goes back to the Extra Deck, so only cheap-to-re-summon monsters
        /// should battle them.
        /// </summary>
        private readonly HashSet<int> _bouncesAttacker = new();

        private static readonly System.Text.RegularExpressions.Regex BouncesAttackerText = new(
            @"battle[^.]*return[^.]*(opponent's monster|that monster|monster it battles)[^.]*hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected bool BouncesAttacker(DynamicCard target) => _bouncesAttacker.Contains(target.ID);

        // ------------------------------------------------------------------ playing against Grand Mole
        //
        // Grand Mole (and any monster that returns the monster battling it to the hand):
        //  - Attack it only with something cheap to re-summon (a Level 4 or lower main-deck monster). A token is lost and
        //    an Extra Deck monster goes back to the Extra Deck, so never with those unless that wins the game.
        //    Re-summoning can even be a plus (Armageddon Knight's summon effect again).
        //  - It doesn't threaten our life points much (900 ATK), but it is a strong card: a fine target for removal.
        //  - Our synchros don't deal with it: it attacks into them on its turn and bounces them too. Don't summon
        //    Extra Deck monsters just to "pressure" a board with Mole on it.
        //  - With a card that lets them Normal Summon during our Battle Phase (Ultimate Offering), Mole comes straight
        //    back after each bounce: every attack into it just costs them 500 LP. Attack it once per turn (e.g. to reuse
        //    Armageddon Knight), and more only if those 500 LP payments would finish them.
        //  - Prefer summoning monsters with an on-summon effect while Mole is up: if Mole bounces them (attacking into
        //    them, or them attacking it) we get to summon them and use the effect again. And they're the ones to send
        //    into Mole: the bounce is a plus for us.

        // "When this card is Normal/Special Summoned" (Armageddon Knight, ...): a bounce lets us use it again.
        private static readonly System.Text.RegularExpressions.Regex OnSummonEffectText = new(
            @"when this card is (normal |special |flip )?summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // Concurrent: the search's brain twins fill these caches from their own threads (a plain Dictionary corrupted itself
        // there: "operations that change non-concurrent collections must have exclusive access", seen 2026-11-11).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> OnSummonEffectCache = new();

        /// <summary>Whether a card has an effect when it is summoned (read from its text, cached).</summary>
        protected static async Task<bool> HasOnSummonEffectAsync(int cardId)
        {
            if (cardId <= 0)
                return false;
            if (OnSummonEffectCache.TryGetValue(cardId, out bool known))
                return known;
            bool has = await CardDatabase.GetAsync(cardId) is DBCardInfo info && OnSummonEffectText.IsMatch(info.CardText ?? "");
            OnSummonEffectCache[cardId] = has;
            return has;
        }

        private static bool HasOnSummonEffectCached(DynamicCard card) =>
            OnSummonEffectCache.TryGetValue(card.ID, out bool has) && has;

        /// <summary>
        /// Extra removal value of a card beyond its own score. None now: Grand Mole's worth is in its card score (a general
        /// its CardRules correction, user 2026-11-13: "mole should be a general bonus"), so it counts wherever it's scored.
        /// Kept so callers stay simple; it still learns which cards bounce their attacker (read from the text), so a Grand
        /// Mole we haven't seen on the field yet is known as one.
        /// </summary>
        protected async Task<(double Bonus, string Why)> RemovalValueBonusAsync(int cardId)
        {
            if (cardId > 0 && !_bouncesAttacker.Contains(cardId) &&
                await CardDatabase.GetAsync(cardId) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
            {
                _bouncesAttacker.Add(cardId);
                BoardAssessment.KnownAttackerBouncers.TryAdd(cardId, 0);
            }
            return (0, "");
        }

        /// <summary>
        /// Whether bouncing this monster (attacking Grand Mole with it) gains us something: an on-summon effect to use
        /// again. Deck brains add their own (e.g. Dark Grepher's effect with a DARK to discard). Requires the on-summon
        /// cache to be filled for the card (HasOnSummonEffectAsync).
        /// </summary>
        protected virtual bool GainsFromBounce(DynamicCard attacker) => HasOnSummonEffectCached(attacker);

        /// <summary>
        /// A better way to deal with Grand Mole than attacking into it: removal we hold that can hit it (a set or hand
        /// card whose text destroys/banishes a monster, without a 1500+ ATK condition: D-Prison, Mirror Force when it
        /// attacks, ...). Deck brains add their own (Dark Armed Dragon, Chaos Sorcerer). Returns its name, or null.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex TributeCostText = new(
            @"\bTribute (1|one|\d+)\b[^;.]*;", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        protected virtual async Task<string?> BetterAnswerToBouncerAsync()
        {
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0)
                .Concat(await GetBotHandCardsAsync())
                .Select(card => card.ID).Distinct();
            foreach (int id in ours)
            {
                if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) == 0 &&
                    MonsterRemovalText.IsMatch(info.CardText ?? "") && !(info.CardText ?? "").Contains("1500 or more") &&
                    // Not one that costs a monster of ours (Icarus Attack tributes a Blackwing): spending it and a body on the
                    // bouncer is worse than the bounce (seen 2026-11-10: Bora held back from Grand Mole "for Icarus Attack").
                    !TributeCostText.IsMatch(info.CardText ?? ""))
                    return info.Name;
            }
            return null;
        }

        /// <summary>Whether the opponent has a face-up monster that bounces what battles it (Grand Mole).</summary>
        protected bool BouncerOnTheirField => _opponents.Any(card => card.IsFaceup() && BouncesAttacker(card));

        // A known bouncer that left their field without reaching the graveyard / banishment: it went back to their hand
        // (its own bounce does that), so it can come straight back. Forgotten once it shows up anywhere else.
        private readonly HashSet<int> _bouncersOnTheirField = new();
        private bool _bouncerInTheirHand;
        // Which bouncer went back to their hand (for the search: it plays it in their turn).
        private int _bouncerInHandId;
        private int _bouncersGoneLastSeen;
        private int _bouncersGoneAtBounce;

        /// <summary>
        /// Cheap Grand Mole tracking for every prompt, including the quick "pass" chain prompts in the opponent's turn:
        /// Mole is often summoned, bounces our set monster and returns to their hand within one Battle Phase, all while
        /// we're only asked whether to chain Jar of Greed. Missing that left us setting Ryko into a known Mole
        /// (2026-11-06, four times in one duel).
        /// </summary>
        protected async Task TrackBouncersAsync()
        {
            var opponents = await GetEnemyMonsterCardsAsync();
            foreach (var card in opponents.Where(card => card.IsFaceup() && card.ID > 0 && !_bouncesAttacker.Contains(card.ID)))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && BouncesAttackerText.IsMatch(info.CardText ?? ""))
                {
                    _bouncesAttacker.Add(card.ID);
                    BoardAssessment.KnownAttackerBouncers.TryAdd(card.ID, 0);
                }
            }
            TrackBouncerInHand(opponents, (await CurrentField.GetBotField()).EnemySide);
        }

        private void TrackBouncerInHand(List<DynamicCard> opponents, CardInfo enemy)
        {
            var onField = opponents.Where(card => card.IsFaceup() && _bouncesAttacker.Contains(card.ID)).Select(card => card.ID).ToHashSet();
            int graveOrBanished = (enemy.Grave ?? Array.Empty<int>()).Concat(enemy.Banish ?? Array.Empty<int>()).Count(_bouncesAttacker.Contains);
            if (onField.Count > 0)
                _bouncerInTheirHand = false; // it's back on the field (BouncerOnTheirField covers it)
            else if (_bouncerInTheirHand && graveOrBanished > _bouncersGoneAtBounce)
                _bouncerInTheirHand = false; // the one in hand went to the graveyard (discarded, ...)
            if (_bouncersOnTheirField.Any(id => !onField.Contains(id)) && graveOrBanished == _bouncersGoneLastSeen)
            {
                _bouncerInTheirHand = true; // left the field without reaching the graveyard / banishment: back in the hand
                _bouncerInHandId = _bouncersOnTheirField.First(id => !onField.Contains(id));
                _bouncersGoneAtBounce = graveOrBanished;
            }
            _bouncersGoneLastSeen = graveOrBanished;
            _bouncersOnTheirField.Clear();
            _bouncersOnTheirField.UnionWith(onField);
        }

        /// <summary>A Grand Mole-type monster is on their field, or known to be in their hand.</summary>
        protected bool BouncerThreat => BouncerOnTheirField || _bouncerInTheirHand;

        /// <summary>
        /// Against Grand Mole, a face-down monster does nothing: Mole attacks it and returns it to our hand (a flipped
        /// Ryko gets bounced too). Setting monsters is scaled by this (1 = no change, 0 = never). Tune from logs.
        /// </summary>
        [Tunable("Against Grand Mole, a face-down monster does nothing: Mole attacks it and returns it to our hand (a flipped Ryko gets bounced too). Setting monsters is scaled by this (1 = no change, 0 = never). Tune from logs.")]
        public double SetIntoBouncerFactor { get; set; } = 0.05;

        private readonly Random _bouncerRandom = new();
        private (int Turn, bool Hold) _setIntoBouncerRoll = (-1, false);

        /// <summary>
        /// Whether to hold monster sets because of Grand Mole: with a bouncer on their field or known in their hand, a
        /// set is allowed only with chance SetIntoBouncerFactor (rolled once per turn). Returns the reason to hold, or null.
        /// </summary>
        protected string? SetIntoBouncerHold<T>(DecisionContext<T> context)
        {
            if (!BouncerThreat)
                return null;
            int turn = context.Header.CurrentTurn;
            if (_setIntoBouncerRoll.Turn != turn)
                _setIntoBouncerRoll = (turn, RollAt >= SetIntoBouncerFactor);
            if (!_setIntoBouncerRoll.Hold)
                return null;
            return $"their Grand Mole-type monster is {(BouncerOnTheirField ? "on the field" : "back in their hand")}: " +
                   $"it would just attack a face-down monster and return it to our hand (set anyway {SetIntoBouncerFactor:P0} of the time)";
        }

        /// <summary>
        /// Against Grand Mole: the summon candidate with an on-summon effect (a bounce lets us use it again), or default
        /// if there's no Mole or no such candidate. Deck brains call this when choosing their Normal Summon.
        /// </summary>
        protected async Task<DynamicCard> PreferOnSummonAgainstBouncerAsync(IEnumerable<DynamicCard> candidates)
        {
            if (!BouncerOnTheirField)
                return default;
            foreach (var card in candidates.Where(card => card.ID > 0 && ReSummonCost(card) < 100))
            {
                if (await HasOnSummonEffectAsync(card.ID))
                    return card;
            }
            return default;
        }

        // Face-up opponent card that lets them Normal Summon again during our Battle Phase (Ultimate Offering).
        private static readonly System.Text.RegularExpressions.Regex ExtraSummonText = new(
            @"pay 500 life points[^.]*normal summon|opponent's battle phase[^.]*normal summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private bool _moleLock;

        /// <summary>A bouncer plus an extra-summon card (Ultimate Offering) face-up: the bouncer keeps coming back.</summary>
        protected bool MoleLockActive => _moleLock;

        /// <summary>
        /// Under the Mole lock, after one attack into the bouncer this turn, further attacks only make the opponent pay
        /// 500 LP to bring it back: stop, unless those payments would finish them.
        /// </summary>
        protected bool MoleLockStopsAttack(int attacksLeft, out string why)
        {
            why = "";
            if (!_moleLock || _bouncerAttacksThisTurn < 1)
                return false;
            if (500 * attacksLeft >= _oppLp)
                return false;
            why = $"with their extra-summon card face-up it comes straight back after each bounce (they only pay 500 LP, " +
                  $"{attacksLeft} attack(s) left vs {_oppLp} LP), and we already attacked it once this turn";
            return true;
        }

        private async Task RefreshMoleLockAsync(List<DynamicCard> opponents)
        {
            bool bouncer = opponents.Any(card => card.IsFaceup() && _bouncesAttacker.Contains(card.ID)) ||
                           (_bouncesAttacker.Count > 0 && _bouncerAttacksThisTurn > 0);
            bool extraSummon = false;
            foreach (var card in (await GetEnemySpellCardsAsync()).Where(card => card.IsFaceup() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && ExtraSummonText.IsMatch(info.CardText ?? ""))
                {
                    extraSummon = true;
                    break;
                }
            }
            _moleLock = bouncer && extraSummon;
        }
    }
}
