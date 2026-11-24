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
    /// The opponent's Gorz: the chance it is in their hand, and whether we hold direct attacks for it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ the opponent's Gorz

        protected const int GorzAttack = 2700;

        /// <summary>Chance per card in the opponent's hand that it is Gorz (about 1 copy in 40). Tune from logs.</summary>
        [Tunable("Chance per card in the opponent's hand that it is Gorz (about 1 copy in 40). Tune from logs.")]
        public double GorzChancePerCard { get; set; } = 0.025;

        private int _gorzDecisionTurn = -1;
        private bool _gorzSkip;

        /// <summary>
        /// Into a completely empty field, a direct attack may let the opponent Special Summon Gorz (+ a token). Decide
        /// once per turn, probabilistically: the chance they hold it (from their hand size; none if their copy is
        /// already in the graveyard/banished), lower if our board can beat a 2700 Gorz, and lower when holding back
        /// gives up a fast clock.
        /// </summary>
        protected async Task<bool> FearGorzAsync<T>(DecisionContext<T> context, int? leadAttack = null)
        {
            int turn = context.Header.CurrentTurn;
            if (turn < _gorzDecisionTurn)
            {
                _knownGorzInOpponentHand = _gorzWasOnEnemyField = false; // a new duel started
                _gorzClearedCards = _gorzOpportunityHand = 0;
                _gorzSetHeldCards = 0;
                _gorzSetResidual = 1;
                _gorzPassFactor = 1;
            }
            if (_gorzDecisionTurn == turn)
            {
                if (_gorzSkip)
                    context.Bot.LogNote("deck brain: still holding direct attacks this turn (possible Gorz)");
                return _gorzSkip;
            }
            _gorzDecisionTurn = turn;
            _gorzSkip = false;

            var (haveGorz, hand, cleared, setHeld) = await GorzInHandChanceAsync();
            if (haveGorz <= 0)
                return false;
            // They passed completely (empty field, nothing played): x GorzPassBonus on top (user, 2026-11-18: "if I pass completely, increase my
            // gorz chance by 30%").
            if (_gorzPassFactor > 1)
                haveGorz = Math.Min(1, haveGorz * GorzPassBonus); // "not +30%, *1.3"
            int tokenAtk = leadAttack ?? FirstHitAttack();
            var (canAnswer, answerWhy) = await CanAnswerGorzAndTokenAsync(tokenAtk);
            double clockFactor = _clockSpeed switch
            {
                ClockSpeed.Quick => GorzQuickClockFactor,
                ClockSpeed.Long => GorzLongClockFactor,
                _ => 1.0
            };
            double chance = haveGorz * (canAnswer ? GorzAnswerableFactor : 1.0) * clockFactor;
            double roll = RollAt;
            _gorzSkip = roll < chance;
            // A known Gorz is no dice roll: hold exactly when we can't answer it, every turn alike (user, 2026-11-12: "if you
            // were afraid of gorz last turn, when you were in a better position, why are you not afraid of it this turn when
            // you're in a much worse position?!" - the 50% roll had flipped).
            if (_knownGorzInOpponentHand)
            {
                _gorzSkip = !canAnswer;
                roll = _gorzSkip ? 0 : 1;
            }
            context.Bot.LogNote($"deck brain: possible Gorz? chance to hold back {chance:P0} (holds it {haveGorz:P0} " +
                                $"{(_knownGorzInOpponentHand ? "(known: seen going to their hand)" : $"from {hand} cards{(cleared > 0 ? $", {cleared} of them held through an earlier Gorz chance they didn't take" : "")}{(setHeld > 0 ? $", {setHeld} held while they set spells/traps" : "")}")}" +
                                $"{(canAnswer ? $", we can answer it: {answerWhy}" : $", we can't answer it and its token: {answerWhy}")}, clock {_clockSpeed}) rolled {roll:0.00}" +
                                $" -> {(_gorzSkip ? "don't attack directly" : "attack")}");
            return _gorzSkip;
        }

        /// <summary>
        /// The chance their hand holds Gorz (1 when known; 0 when their copy is gone or their hand is empty). Cards they held
        /// through a direct hit into their empty field without Gorzing probably aren't Gorz; cards they held while setting
        /// spells/traps (which switches Gorz off) are less likely Gorz; cards drawn since keep the full chance.
        /// </summary>
        private async Task<(double Chance, int Hand, int Cleared, int SetHeld)> GorzInHandChanceAsync()
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            int hand = enemy.Hand?.Length ?? 0;
            bool gone = (enemy.Grave ?? Array.Empty<int>()).Contains(GorzTheEmissaryOfDarkness) || (enemy.Banish ?? Array.Empty<int>()).Contains(GorzTheEmissaryOfDarkness);
            return GorzInHandChance(hand, gone);
        }

        /// <summary>The brain's Gorz chance for their hand (per card, raised after a pass, lowered by evidence), synchronously.</summary>
        private (double Chance, int Hand, int Cleared, int SetHeld) GorzInHandChance(int hand, bool gone)
        {
            if (hand == 0 || gone)
                return (0, hand, 0, 0);
            int cleared = Math.Min(_gorzClearedCards, hand);
            int setHeld = Math.Min(_gorzSetHeldCards, hand - cleared);
            double haveGorz = _knownGorzInOpponentHand ? 1
                : 1 - Math.Pow(1 - Math.Min(1, GorzChancePerCard * _gorzPassFactor), hand - cleared - setHeld) *
                      Math.Pow(1 - GorzChancePerCard * EvidenceResidual, cleared) *
                      Math.Pow(1 - GorzChancePerCard * _gorzSetResidual, setHeld);
            return (haveGorz, hand, cleared, setHeld);
        }

        /// <summary>
        /// The brain's Gorz chance for a search's position, with the pass bonus (FearGorzAsync's number): the search's win chance uses the larger
        /// of it and the deck read's (user, 2026-11-18: "the lethal line was risky - it only worked through gorz, did it factor that in?": the
        /// lines counted only the deck read's chance).
        /// </summary>
        private protected double BrainGorzChance(Search.SearchView view)
        {
            bool gone = view.EnemyGrave.Concat(view.EnemyBanished).Any(id => id == GorzTheEmissaryOfDarkness || Search.SearchHost.CanonicalId(id) == GorzTheEmissaryOfDarkness);
            double chance = GorzInHandChance(view.EnemyHandCount, gone).Chance;
            return _gorzPassFactor > 1 ? Math.Min(1, chance * GorzPassBonus) : chance;
        }

        private readonly Random _gorzRandom = new();

        // Known Gorz in the opponent's hand: seen there (e.g. revealed by Trap Dustshoot), or it left their field
        // without reaching the graveyard or banishment (returned to the hand). Forgotten once it shows up anywhere else.
        private bool _knownGorzInOpponentHand;
        private bool _gorzWasOnEnemyField;

        /// <summary>Deck brains call this with revealed opponent hand cards (e.g. from Trap Dustshoot).</summary>
        protected void ObserveRevealedOpponentHand(IEnumerable<DynamicCard> revealed)
        {
            var cards = revealed.ToList();
            if (cards.Any(card => BoardAssessment.KnownId(card) == GorzTheEmissaryOfDarkness))
                _knownGorzInOpponentHand = true;
            ObserveRevealedStaples(cards);
        }

        // Cards in their hand when they set spells/traps (Gorz needs their field empty, so a Gorz holder doesn't set), and
        // the share of their Gorz chance those cards keep.
        private int _gorzSetHeldCards;
        private double _gorzSetResidual = 1;

        /// <summary>
        /// Cards held while setting keep this share of their Gorz chance per spell/trap of theirs on the field (0.3 for one,
        /// 0.09 for two). Tune from logs.
        /// </summary>
        [Tunable("Cards held while setting keep this share of their Gorz chance per spell/trap of theirs on the field (0.3 for one, 0.09 for two). Tune from logs.")]
        public double GorzSetResidual { get; set; } = 0.3;

        // Their deliberate pass with a big hand and an empty field: per-card Gorz chance multiplier (1 = none).
        private double _gorzPassFactor = 1;

        /// <summary>Hand size from which passing with an empty field and nothing played reads as holding Gorz. Tune from logs.</summary>
        [Tunable("Hand size from which passing with an empty field and nothing played reads as holding Gorz. Tune from logs.")]
        public int GorzPassHand { get; set; } = 4;

        /// <summary>Per-card Gorz chance multiplier after such a pass (until they put a card on the field). Tune from logs.</summary>
        [Tunable("Per-card Gorz chance multiplier after such a pass (until they put a card on the field). Tune from logs.")]
        public double GorzPassBoost { get; set; } = 3;

        /// <summary>After such a pass, the chance they hold Gorz is multiplied by this (1.3 = 30% more). User, 2026-11-18.</summary>
        [Tunable("After a complete pass (empty field, nothing played), the chance they hold Gorz is multiplied by this (1.5 = 50% more). User, 2026-11-18.")]
        public double GorzPassBonus { get; set; } = 1.5; // user, 2026-11-18: "lets up it to x1.5" (was 1.3)

        /// <summary>They passed their turn with an empty field and a big hand that didn't shrink: their cards are likelier Gorz.</summary>
        protected void NoteOpponentPassedForGorz() => _gorzPassFactor = GorzPassBoost;

        /// <summary>They set spells/traps (now <paramref name="theirSpells"/> on their field) holding <paramref name="hand"/> cards.</summary>
        protected void NoteOpponentSetForGorz(int hand, int theirSpells)
        {
            _gorzSetHeldCards = hand;
            _gorzSetResidual = Math.Pow(GorzSetResidual, Math.Max(1, theirSpells));
        }

        // A direct attack into their empty field (a Gorz chance) is pending: their hand size then.
        private int _gorzOpportunityHand;
        // Cards in their hand that were held through a Gorz chance they didn't take.
        private int _gorzClearedCards;

        private void TrackOpponentGorz(List<DynamicCard> opponents, CardInfo enemy)
        {
            bool onField = opponents.Any(card => card.IsFaceup() && card.IsCode(GorzTheEmissaryOfDarkness));
            bool elsewhere = (enemy.Grave ?? Array.Empty<int>()).Contains(GorzTheEmissaryOfDarkness) || (enemy.Banish ?? Array.Empty<int>()).Contains(GorzTheEmissaryOfDarkness);
            int hand = enemy.Hand?.Length ?? 0;
            if (_gorzOpportunityHand > 0)
            {
                // Our direct hit resolved: no Gorz means the cards they held then probably aren't Gorz.
                if (!onField)
                    _gorzClearedCards = Math.Max(_gorzClearedCards, Math.Min(_gorzOpportunityHand, hand));
                _gorzOpportunityHand = 0;
            }
            _gorzClearedCards = Math.Min(_gorzClearedCards, hand);
            // Cards leave their hand: the ones held while setting can only be fewer now.
            _gorzSetHeldCards = Math.Min(_gorzSetHeldCards, hand);
            if (onField || elsewhere)
                _knownGorzInOpponentHand = false;
            else if (_gorzWasOnEnemyField)
                _knownGorzInOpponentHand = true; // left the field without going to the GY/banished: back in the hand
            // They put a card on the field: the deliberate-pass read no longer holds.
            if (opponents.Any(card => card.ID != 0 || !card.IsFaceup()) || (enemy.SpellZones ?? Array.Empty<CardArray>()).Any(zone => zone.Cards is { Length: > 0 }))
                _gorzPassFactor = 1;
            _gorzWasOnEnemyField = onField;
        }

        // Text of a card that can get rid of a monster (destroy or banish it): Dimensional Prison, Mirror Force, ...
        private static readonly System.Text.RegularExpressions.Regex MonsterRemovalText = new(
            @"(destroy|banish)[^.]{0,60}monster|monster[^.]{0,60}(destroy|banish)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether we could deal with a 2700 ATK Gorz: a monster that beats it, or removal we hold (a set card, or a card
        /// in hand, whose text destroys or banishes a monster: D-Prison, Mirror Force, ...). Deck brains add their own
        /// (e.g. Dark Armed Dragon, or a synchro that beats it).
        /// </summary>
        protected virtual async Task<bool> CanAnswerGorzAsync()
        {
            if (_ownMonsters.Any(card => card.IsFaceup() && card.Attack > GorzAttack))
                return true;
            // A monster that pays off when Gorz destroys it in battle answers Gorz too (Machina Fortress: its trigger destroys
            // the card that killed it; user, 2026-11-12: "fortress deals with any card with more attack than it").
            if (_ownMonsters.Any(card => card.IsFaceup() && BattleDeathPayoff(card) != null && card.IsCode(MachinaFortressAnswerIds)))
                return true;
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0)
                .Concat(await GetBotHandCardsAsync())
                .Select(card => card.ID).Distinct();
            foreach (int id in ours)
            {
                if (await CardDatabase.GetAsync(id) is DBCardInfo info && (info.Type & (int)CardType.Monster) == 0 &&
                    MonsterRemovalText.IsMatch(info.CardText ?? ""))
                    return true;
            }
            return false;
        }

        private static readonly System.Text.RegularExpressions.Regex AllMonstersRemovalText = new(
            @"(destroy|banish)[^.]{0,30}all[^.]{0,30}monster",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether we can deal with both halves of a Gorz (user, 2026-11-13: "do I have an answer to the gorz and the token"):
        /// Gorz itself (CanAnswerGorzAsync, deck answers included) and its token, whose ATK is the damage of the hit that
        /// summoned it (<paramref name="tokenAtk"/>). One answer each: a monster that beats it (Machina Fortress beats
        /// anything that destroys it), or removal we hold (a "destroy all" card covers both).
        /// </summary>
        protected async Task<(bool Both, string Why)> CanAnswerGorzAndTokenAsync(int tokenAtk)
        {
            if (!await CanAnswerGorzAsync())
                return (false, "nothing of ours beats a 2700 Gorz");
            if (tokenAtk <= 0)
                return (true, "an answer to Gorz, and no token to worry about");
            var units = new List<(bool Gorz, bool Token, string Name)>();
            foreach (var card in _ownMonsters.Where(card => card.IsFaceup() && card.ID > 0))
            {
                bool fortress = BattleDeathPayoff(card) != null && card.IsCode(MachinaFortressAnswerIds);
                bool gorz = fortress || card.Attack > GorzAttack, token = fortress || card.Attack > tokenAtk;
                if (gorz || token)
                    units.Add((gorz, token, CardNames.Get(card.ID)));
            }
            var ours = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0).Concat(await GetBotHandCardsAsync());
            foreach (var card in ours)
            {
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info || (info.Type & (int)CardType.Monster) != 0 ||
                    !MonsterRemovalText.IsMatch(info.CardText ?? ""))
                    continue;
                units.Add((true, true, CardNames.Get(card.ID)));
                if (AllMonstersRemovalText.IsMatch(info.CardText ?? ""))
                    units.Add((true, true, CardNames.Get(card.ID) + " (all)"));
            }
            // Gorz's answer may be deck-specific (Dark Armed Dragon, a synchro): then any token answer will do.
            if (!units.Any(u => u.Gorz))
            {
                var tokenOnly = units.FirstOrDefault(u => u.Token);
                return tokenOnly.Name != null
                    ? (true, $"a deck answer to Gorz, and {tokenOnly.Name} for the {tokenAtk} token")
                    : (false, $"nothing of ours beats the {tokenAtk} ATK token Gorz would bring");
            }
            for (int i = 0; i < units.Count; i++)
                for (int j = 0; j < units.Count; j++)
                    if (i != j && units[i].Gorz && units[j].Token)
                        return (true, $"{units[i].Name} answers Gorz, {units[j].Name} the {tokenAtk} ATK token");
            return (false, $"one answer ({string.Join(", ", units.Select(u => u.Name))}) for Gorz and its {tokenAtk} ATK token");
        }

        /// <summary>The smallest available direct hit, used to estimate the Gorz token's ATK.</summary>
        private int FirstHitAttack() => _ownMonsters
            .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked && card.ID > 0)
            .Select(card => card.Attack).DefaultIfEmpty().Min();
    }
}
