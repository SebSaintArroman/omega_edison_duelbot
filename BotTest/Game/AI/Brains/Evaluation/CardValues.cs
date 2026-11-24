using DuelSim.Engine;
using BotTest.Game.AI.Search;
using System.Collections.Concurrent;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// What a card's effects are worth where it sits, in EffectEvaluator points (a searcher or a negate about 4, a draw 3,
    /// destruction 3, a revive half of what it can revive), with ATK left out: the position score counts current ATK on
    /// its own (effects and current ATK together, never printed ATK).
    /// </summary>
    internal readonly record struct CardValue(
        /// <summary>On the field (what it can still do there: spent on-summon effects don't count).</summary>
        double Field,
        /// <summary>In hand or set (everything it can do once played).</summary>
        double Held,
        /// <summary>In the graveyard: only cards that work from there (Vayu, Plaguespreader, Treeborn, Necro Gardna).</summary>
        double Grave,
        /// <summary>
        /// Gains from being set face-down: a flip effect (Ryko), or an effect when it's destroyed or sent to the graveyard
        /// or works from there (Sangan, Card Trooper, Necro Gardna). Any other monster set is only a wall, and walls are
        /// worth little (user, 2026-11-10).
        /// </summary>
        bool LikesSet = false,
        /// <summary>How well it protects our monsters / life points from a set position when they attack or summon (0-1): a trap or
        /// quick-play that stops the attack or removes the attacker (Mirror Force, Sakuretsu, D-Prison, Waboku, Magic Cylinder 1;
        /// Bottomless, Compulsory 0.8; Torrential, Raigeki Break, Icarus, Book of Moon 0.5). Only counts when there's something to
        /// protect (BoardEvaluator.ProtectionBonus).</summary>
        double Protects = 0,
        /// <summary>What it can remove that's already on the field, at any time from a set position (user, 2026-11-16, "removes
        /// threat"): Removes.Monster (Compulsory, Raigeki Break, Book of Moon), Removes.SpellTrap (Dust Tornado, MST), both
        /// (Icarus Attack, Raigeki Break on a card), or None. Cards that only hit a summon or an attacker aren't here.</summary>
        Removes Removes = Removes.None,
        /// <summary>Held without the effects valued by their targets (TargetEffects), which the search's hand value adds itself.</summary>
        double HeldUntargeted = 0,
        /// <summary>Whether its text has an effect valued by its targets (a search, a Special Summon from a zone, a send from the deck).</summary>
        bool HasTargetEffects = false);

    [Flags]
    internal enum Removes { None = 0, Monster = 1, SpellTrap = 2, Any = 3 }

    /// <summary>
    /// Card values for one search, from the brains' own effect scorer (EffectEvaluator) on the search's card database.
    /// Graveyard-dependent effects (revivers, "add from your graveyard") are judged against each side's graveyard at
    /// the decision. Computed once per card and side, from any search thread.
    /// </summary>
    internal sealed class CardValues
    {
        private readonly ConcurrentDictionary<(int Code, int Side), CardValue> _values = new();
        private readonly IReadOnlyCollection<int>[] _graves;

        public CardValues(IReadOnlyCollection<int> ownGrave, IReadOnlyCollection<int> enemyGrave)
        {
            _graves = new[] { ownGrave, enemyGrave };
            foreach (int id in ownGrave.Concat(enemyGrave))
                Seed(id);
        }

        // Values that don't depend on the graveyard, kept for every later search (scoring a card is the slow part of a
        // new search: 2026-11-10 it took about a second per decision when every search started empty).
        private static readonly ConcurrentDictionary<int, CardValue> Shared = new();
        private static readonly ConcurrentDictionary<int, bool> GraveDependent = new();

        public CardValue Get(int code, int side)
        {
            if (code <= 0)
                return default;
            bool dependent = GraveDependent.GetOrAdd(code, c =>
            {
                Seed(c);
                return CardDatabase.TryGetCached(c, out var card) && MentionsGraveyard.IsMatch(card.CardText ?? "");
            });
            return dependent
                ? _values.GetOrAdd((code, side), key => Compute(key.Code, _graves[key.Side]))
                : Shared.GetOrAdd(code, c => Compute(c, Array.Empty<int>()));
        }

        // A card whose value depends on what's in a graveyard (revivers, "add from your graveyard", graveyard counts).
        private static readonly System.Text.RegularExpressions.Regex MentionsGraveyard = new(@"graveyard|\bgy\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static void Seed(int id)
        {
            if (id > 0 && SearchHost.Available && SearchHost.Cards.Get(id) is { } record)
                CardDatabase.Seed(record.ToDbCardInfo());
        }

        // "FLIP:", "flip summoned", "when this card is destroyed / sent to the graveyard".
        private static readonly System.Text.RegularExpressions.Regex LikesSetText = new(
            @"\bflip\b|this card is (destroyed|sent (from the field )?to the (graveyard|gy))|if this card is sent to the (graveyard|gy)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Set cards that protect (user, 2026-11-16: "traps should get a bonus if they're likely to protect that monster / your life
        // points"): full protection stops the attack or takes the attacker on its attack; partial takes a monster on its summon or one
        // monster at any time.
        private static readonly System.Text.RegularExpressions.Regex ProtectsFully = new(
            @"negate (the|that) attack|end the battle phase|cannot declare an attack|cannot attack|declares an attack.{0,80}(destroy|banish|return)|" +
            @"attack position monsters? your opponent controls|cannot be destroyed by battle|inflict damage to your opponent equal to",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex ProtectsMostly = new(
            @"summons? a monster(\(s\))?.{0,80}(destroy|banish|return)|target 1 monster on the field; return",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex ProtectsPartly = new(
            @"destroy all monsters on the field|target \d (face-up )?(monsters?|cards?) on the field; (destroy|banish)|destroy 1 (face-up )?monster|" +
            @"change (that target|it) to face-down|negate the summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static double Protection(int type, string? cardText)
        {
            bool trap = (type & (int)CardType.Trap) != 0;
            bool quickPlay = (type & (int)CardType.Spell) != 0 && (type & (int)CardType.QuickPlay) != 0;
            if (!trap && !quickPlay)
                return 0;
            string text = cardText ?? "";
            return ProtectsFully.IsMatch(text) ? 1 : ProtectsMostly.IsMatch(text) ? 0.8 : ProtectsPartly.IsMatch(text) ? 0.5 : 0;
        }

        // Removal usable at any time on a card already on the field (not "when a monster is summoned / declares an attack").
        private static readonly System.Text.RegularExpressions.Regex RemovesAnyCard = new(
            @"target \d+ (face-up )?cards? on the field;[^.]{0,60}(destroy|banish|return|shuffle)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RemovesMonster = new(
            @"target \d+ (face-up )?monsters? (on the field|your opponent controls);[^.]{0,60}(destroy|banish|return|shuffle|change (that target|it) to face-down)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RemovesSpellTrap = new(
            @"target \d+ (face-up )?spells?(/|, | or )traps?( cards?)? (on the field|your opponent controls);[^.]{0,40}(destroy|banish|return)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // Triggered only by a summon or an attack: not "already on the field".
        private static readonly System.Text.RegularExpressions.Regex OnlyOnSummonOrAttack = new(
            @"^(when|if) (your )?(opponent'?s? )?(a )?monsters? (is |would be )?(normal |special |flip )?summon|^when an opponent'?s monster declares an attack|^when a monster(\(s\))? would be summoned",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static Removes Removal(int type, string? cardText)
        {
            bool trap = (type & (int)CardType.Trap) != 0;
            bool quickPlay = (type & (int)CardType.Spell) != 0 && (type & (int)CardType.QuickPlay) != 0;
            string text = cardText ?? "";
            if (!trap && !quickPlay || OnlyOnSummonOrAttack.IsMatch(text.TrimStart()))
                return Removes.None;
            return RemovesAnyCard.IsMatch(text) ? Removes.Any
                : (RemovesMonster.IsMatch(text) ? Removes.Monster : Removes.None) | (RemovesSpellTrap.IsMatch(text) ? Removes.SpellTrap : Removes.None);
        }

        [Tunable("A card that works from the graveyard is worth at least this there (card points), whatever its graveyard effects score.")]
        public static readonly Tuned<double> GraveFloor = new(0); // user, 2026-11-17: the graveyard readings count as they are (Necro Gardna 0.5); was 1
        [Tunable("Vayu's own graveyard worth (card points; its pairing is scored by the Vayu / Blackwing brains on top). User, 2026-11-17: about as valuable in the GY as Plaguespreader, more than Necro Gardna.")]
        public static readonly Tuned<double> VayuGraveReading = new(0.8);

        private static CardValue Compute(int code, IReadOnlyCollection<int> grave)
        {
            Seed(code);
            if (!CardDatabase.TryGetCached(code, out var card))
                return default;
            try
            {
                double atk = (card.Type & (int)CardType.Monster) != 0 ? BoardEvaluator.AtkValue(card.Attack) : 0;
                // All lookups hit the seeded cache, so these complete at once.
                double field = EffectEvaluator.ScoreOnFieldAsync(code, grave).GetAwaiter().GetResult().Score - atk;
                double held = EffectEvaluator.ScoreAsync(code, grave).GetAwaiter().GetResult().Score - atk;
                double inGrave = EffectEvaluator.ScoreInGraveAsync(code, grave).GetAwaiter().GetResult().Score;
                bool targeted = TargetEffects.Effects(card.CardText ?? "").Count > 0;
                double heldUntargeted = targeted ? EffectEvaluator.ScoreWithoutTargetEffectsAsync(code, grave).GetAwaiter().GetResult().Score - atk : held;
                // An unmatched Vayu gets its own reading. The graveyard evaluator replaces the individual readings of a live pair.
                bool vayu = SearchHost.Is(code, BlackwingVayuTheEmblemOfHonor);
                string text = card.CardText ?? "";
                bool fromGrave = EffectEvaluator.HasGraveyardEffect(text) || EffectEvaluator.HasGraveyardPlay(text);
                bool likesSet = fromGrave || LikesSetText.IsMatch(text);
                // (The card's correction, on its CardRules entry, is already in both scores: EffectEvaluator adds it where it's scored.)
                return new CardValue(Math.Max(0, field), Math.Max(0, held), vayu ? VayuGraveReading : fromGrave ? Math.Max(GraveFloor, inGrave) : 0, likesSet, Protection(card.Type, card.CardText),
                    Removal(card.Type, card.CardText), Math.Max(0, heldUntargeted), targeted);
            }
            catch
            {
                return default;
            }
        }
    }
}
