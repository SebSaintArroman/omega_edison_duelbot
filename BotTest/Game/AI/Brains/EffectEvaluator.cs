using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;
using System.Text.RegularExpressions;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Rough "how useful is this card right now" score from its effect text, using keywords instead of a
    /// hand-written table (there are thousands of cards). Conditions that can be checked against public
    /// information (e.g. "add 1 Level 4 Beast-Type monster from your Graveyard") are checked against the
    /// owner's graveyard, so a card whose effect has no target right now scores low.
    /// Weights are guesses meant to be tuned from duel logs; add special cases where keywords mislead.
    /// </summary>
    public static class EffectEvaluator
    {
        private sealed record Rule(string Name, Regex Pattern, double Value);

        private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // An effect's points as the tuning file gives them (EffectEvaluator.Effect.<name>), else the built-in value.
        private static double V(string name, double value)
        {
            string text = Tuning.Entry($"EffectEvaluator.Effect.{name.Replace(' ', '-')}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "");
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : value;
        }

        static EffectEvaluator() => Tuning.Flush();

        [Tunable("An effect that needs a graveyard target (recover, revive) keeps this share of its points when there's none.")]
        public static double NoTargetShare = 0.1;
        [Tunable("A monster effect that needs battle damage: its points x this when nothing of theirs blocks it.")]
        public static double LiveBattleTriggerFactor = 2;
        [Tunable("A monster effect that needs battle damage: its points x this when their monster blocks it.")]
        public static double BlockedBattleTriggerFactor = 0.5;
        [Tunable("A monster effect that only works when it's destroyed: this share of its points.")]
        public static double OnDestroyedShare = 0.5;
        [Tunable("Banishing a card with a graveyard effect: the base chance it's worth it.")]
        public static double GraveBanishBase = 0.3;
        [Tunable("Banishing a card with a graveyard effect: added chance per monster in their graveyard it can use (up to 4).")]
        public static double GraveBanishPerPartner = 0.15;
        [Tunable("Banishing a card with a graveyard effect: chance taken off per copy already in their graveyard.")]
        public static double GraveBanishPerCopy = 0.25;

        private static readonly Rule[] Rules =
        {
            new("searches the deck", R(@"from your deck to your hand|add .{0,80}? from your deck"), V("searches the deck", 4)),
            new("special summons from the deck", R(@"special summon .{0,80}? from your deck"), V("special summons from the deck", 4)),
            new("takes control", R(@"take control|gain control"), V("takes control", 5)),
            // Negating effects/activations is strong; negating an attack only stalls one attack (like Necro Gardna).
            new("negates", R(@"\bnegate(?![^.]{0,20}\battack\b)"), V("negates", 4)),
            new("negates an attack", R(@"\bnegate[^.]{0,20}\battack\b"), V("negates an attack", 0.5)),
            new("draws", R(@"\bdraw\b"), V("draws", 3)),
            new("special summons", R(@"special summon"), V("special summons", 2)),
            new("destroys", R(@"\bdestroy (1|2|all|that|the|it|up to)"), V("destroys", 3)),
            new("banishes", R(@"\bbanish (1|2|all|that|the|it|up to)"), V("banishes", 2.5)),
            new("bounces", R(@"return .{0,60}? to (the|its owner's) hand"), V("bounces", 2)),
            new("recovers from the graveyard", R(@"from your (graveyard|gy) to your hand|add .{0,80}? from your (graveyard|gy)"), V("recovers from the graveyard", 2)),
            new("protects itself", R(@"cannot be destroyed"), V("protects itself", 1.5)),
            new("inflicts damage", R(@"inflict .{0,30}? damage"), V("inflicts damage", 1)),
            new("discards the opponent's hand", R(@"discard .{0,40}? (from|in) your opponent's hand|your opponent discards"), V("discards the opponent's hand", 3))
        };

        /// <summary>
        /// Cards whose keywords mislead the scorer, with a correction and the reason. E.g. Mist Valley Falcon's "return 1
        /// card you control to the hand to attack" reads as a cost, but it's a strong positive: it re-uses their own
        /// cards' effects (re-summon a Crystal Beast, re-set a trap) every turn on a 2000 beater.
        /// </summary>
        private static readonly Dictionary<int, (double Bonus, string Why)> KnownCardAdjustments = new()
        {
            [82199284] = (V("card.82199284", 4), "Mist Valley Falcon: its 'return a card to attack' recycles their own cards' effects, a strong positive"),
            [68366996] = (V("card.68366996", 5), "Gemini Soldier: survives a battle once per turn and summons a monster from the deck: an incredibly powerful effect on a 500 body")
        };

        // "Level 4", "Level 3 or lower", "Level 7 or higher"
        private static readonly Regex LevelRequirement = R(@"level (\d+)( or (lower|higher))?");
        // Monster Types as written in card text, longest first so "Winged Beast" wins over "Beast".
        private static readonly (string Text, CardRace Race)[] RaceWords =
        {
            ("winged beast", CardRace.WingedBeast), ("beast-warrior", CardRace.BeastWarrior), ("sea serpent", CardRace.SeaSerpent),
            ("divine-beast", CardRace.DivineBeast), ("creator god", CardRace.CreatorGod), ("spellcaster", CardRace.Spellcaster),
            ("dinosaur", CardRace.Dinosaur), ("warrior", CardRace.Warrior), ("machine", CardRace.Machine), ("reptile", CardRace.Reptile),
            ("psychic", CardRace.Psychic), ("thunder", CardRace.Thunder), ("cyberse", CardRace.Cyberse), ("zombie", CardRace.Zombie),
            ("insect", CardRace.Insect), ("dragon", CardRace.Dragon), ("beast", CardRace.Beast), ("fairy", CardRace.Fairy),
            ("fiend", CardRace.Fiend), ("plant", CardRace.Plant), ("aqua", CardRace.Aqua), ("pyro", CardRace.Pyro),
            ("rock", CardRace.Rock), ("fish", CardRace.Fish), ("wyrm", CardRace.Wyrm)
        };
        private static readonly Regex GraveyardClause = R(@"[^.]*(from|in) your (graveyard|gy)\b[^.]*");

        // Clauses that read like effects but are restrictions or drawbacks: "Cannot be Special Summoned", a Spirit
        // returning itself to the hand at the End Phase, "banish it when it leaves the field". Removed before scoring,
        // or e.g. Kinka-byo (400 ATK) scores as a summoner, a bouncer and a banisher.
        private static readonly Regex Drawbacks = R(
            @"cannot be (normal |special |normal or special )?summoned[^.;]*|" +
            @"banish (it|this card|them) when (it|they) leaves? the field|" +
            @"during the end phase[^.]*?return (this card|it) to (the|its owner's|your) hand|" +
            @"return (this card|it) to (the|its owner's|your) hand during the end phase" + "|" +
            // The summoned monster's effects negated (Vayu's synchro): a drawback, not a negate effect (user, 2026-11-17).
            @"(with|but) (its|their) effects? negated|(its|their) effects? (are|is) negated");

        // A Special Summon of a monster from the graveyard (not of itself, e.g. King of the Beasts).
        private static readonly Regex RevivesFromGraveyard = R(@"(in|from) your (graveyard|gy)\b");
        private static readonly Regex RevivesItself = R(@"special summon this card");
        // A Special Summon of some other card ("Special Summon 1 ...", "Special Summon that monster"), not just this card / "it".
        private static readonly Regex SummonsAnother = R(@"special summon (?!this card\b|it\b)");
        // A union monster's equip / unequip sentence.
        private static readonly Regex UnionUnequip = R(@"unequip this card");

        [Tunable("A monster effect that only Special Summons the card itself (a union's unequip, Machina Fortress's own summon): this many points.")]
        public static double SelfSummonValue = 0.25;
        [Tunable("A Tuner monster (in hand or on the field): this many points, for the synchros it opens.")]
        public static double TunerValue = 0.5;
        [Tunable("A monster that brings itself back from the graveyard (Plaguespreader, Treeborn, Machina Fortress): this many points there (one card regained).")]
        public static double GraveSelfReviveValue = 1.0;
        [Tunable("A graveyard effect that boosts ATK / DEF for a battle (Skill Successor): this many points there.")]
        public static double GraveStatBoostValue = 0.75; // user, 2026-11-18 (was 0.5)

        // "Banish this card (and ...) from your GY" as a cost: not a revive from the GY (Vayu's synchro comes from the Extra Deck).
        // What a summon sentence summons from: the part from "Special Summon" on (Vayu targets and banishes cards in the GY as its cost,
        // then summons from the Extra Deck: not a revive).
        private static string WithoutGraveCost(string s)
        {
            int at = s.IndexOf("special summon", StringComparison.OrdinalIgnoreCase);
            return at >= 0 ? s[at..] : s;
        }
        private static readonly Regex ReturnsItselfToHand = R(@"(add|return) this card (from your (graveyard|gy) )?to your hand");
        private static readonly Regex GainsStats = R(@"gains? \d+ (atk|def)");
        [Tunable("A search or recruit from the deck that only happens when the card is destroyed (Peacekeeper, Shining Angel): it replaces the card, this many points.")]
        public static double OnDestroyedReplaceValue = 1.0;

        /// <summary>
        /// Share of the best graveyard target's own score a reviver adds (Kinka-byo bringing back Moja, which in turn
        /// brings out King of the Beasts): reviving is only as good as what it revives.
        /// </summary>
        [Tunable("Share of the best graveyard target's own score a reviver adds (Kinka-byo bringing back Moja, which in turn brings out King of the Beasts): reviving is only as good as what it revives.")]
        public static double ReviveTargetShare = 0.5;

        /// <summary>Removal of all of something (Absolute Zero, Mirror Force, Heavy Storm) against one card: this factor on the rule's value.</summary>
        [Tunable("Removal of all of something (Absolute Zero, Mirror Force, Heavy Storm) against one card: this factor on the rule's value. user, 2026-11-13: \"removing all should be worth about 1.75x the original\"")]
        public static double AllRemovalFactor = 1.75; // user, 2026-11-13: "removing all should be worth about 1.75x the original"

        private static readonly Regex RemovesAll = R(@"\b(destroy|banish|return) all\b");

        // The sentence holding an effect, to read its trigger.
        // Built once per rule and reused: compiling a regex per card and rule made scoring a card take milliseconds (2026-11-10).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Regex, Regex> Sentences = new();
        private static Regex SentenceWith(Regex rule) => Sentences.GetOrAdd(rule, r => R($@"[^.]*(?:{r})[^.]*"));
        // Triggers only when the opponent destroys it (floaters like Giant Rat): the opponent can usually just not.
        private static readonly Regex OnDestroyedTrigger = R(@"(destroyed|sent (from the field )?to the (graveyard|gy))");
        // Triggers when it deals battle damage / attacks directly (e.g. X-Saber Airbellum's discard).
        private static readonly Regex OnBattleDamageTrigger = R(@"inflicts? (battle )?damage|direct attack");

        /// <summary>
        /// Usefulness score for a card: effect keywords, plus ATK/1000 for monsters.
        /// <paramref name="ownerGraveIds"/> is the card owner's graveyard (public), used to check graveyard conditions.
        /// <paramref name="enemyBestBlocker"/> is the best battle stat the card's owner would have to get past (the
        /// other player's strongest monster, 0 if none): an effect that needs battle damage is live when the card's
        /// ATK beats it, and weak otherwise. Effects that only trigger when the card is destroyed count half, since
        /// the other player can usually avoid destroying it.
        /// </summary>
        public static async Task<(double Score, string Why)> ScoreAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds, int? enemyBestBlocker = null) =>
            Corrected(await ScoreCoreAsync(cardId, ownerGraveIds, enemyBestBlocker, pressureOnly: false, depth: 0), cardId, inHand: true);

        /// <summary>
        /// What the card does from the graveyard (user, 2026-11-17: "a grave-effect-analyser to go with the hand and field ones"): only its
        /// sentences that work while it's in the GY (Vayu's synchro, Plaguespreader's and Fortress's revives, Necro Gardna's negate, Treeborn
        /// coming back), scored with the same effect values; no body. 0 for a card that does nothing there.
        /// </summary>
        public static async Task<(double Score, string Why)> ScoreInGraveAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds) =>
            await ScoreCoreAsync(cardId, ownerGraveIds, null, pressureOnly: false, depth: 0, inGrave: true);

        // The card's own correction (Search.CardCorrections: the user's per-card calls), where it's scored: in hand (ScoreAsync)
        // the general and hand-only parts, on the field (ScoreOnFieldAsync) the general part.
        private static (double Score, string Why) Corrected((double Score, string Why) scored, int cardId, bool inHand)
        {
            double correction = BotTest.Game.AI.Search.CardCorrections.Get(cardId, inHand);
            return correction == 0 ? scored
                : (scored.Score + correction, $"{scored.Why} {(correction > 0 ? "+" : "")}{correction:0.#} (card correction{(inHand ? ", in hand" : "")})");
        }

        /// <summary>
        /// ScoreAsync for a monster already on the field: its on-summon effects (XX-Saber Hyunlei's "when Synchro Summoned:
        /// destroy") are spent, and its own summoning procedure ("Special Summon this card from your hand") no longer
        /// matters, so neither counts. What it can still do (Faultroll's once-per-turn revive) does.
        /// </summary>
        /// <param name="destroying">Scored as a target for our card effect that destroys it: its effects that trigger on that
        /// destruction (Sangan's search, a floater's replacement) count against it instead of for it (user, 2026-11-13:
        /// "effects like sangan that trigger on destruction should count negatively when selecting them for an effect that
        /// would cause destruction"). Battle-only triggers ("destroyed by battle") don't fire, and are scored as usual.</param>
        public static async Task<(double Score, string Why)> ScoreOnFieldAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds, int? enemyBestBlocker = null,
            bool destroying = false) =>
            Corrected(await ScoreCoreAsync(cardId, ownerGraveIds, enemyBestBlocker, pressureOnly: false, depth: 0, onField: true, destroying: destroying), cardId, inHand: false);

        // A destruction trigger only by battle ("destroyed by battle", not "by battle or card effect"): an effect's
        // destruction doesn't set it off.
        private static readonly Regex DestroyedByBattleOnly = R(@"destroyed by battle(?![^.:]*card effect)");

        // "When this card is Synchro Summoned", "If this card is Normal or Special Summoned", "FLIP:"... (spent once on the field).
        private static readonly Regex OnSummonTrigger = R(
            @"(when|if) this card is (normal |special |synchro |xyz |fusion |tribute |flip )?(summoned|(normal or special|normal or flip) summoned)|^\s*flip:");
        // An effect used from the hand only: its cost is the card itself in hand (D.D. Crow, Kuriboh, Honest: "discard this card").
        // (Battle Fader: "Special Summon this card from your hand, then end the Battle Phase": seen 2026-11-18, a used Battle Fader on the field
        // still scored its effect, and Substitoad tributed Junk Synchron instead of it.)
        private static readonly Regex HandOnlyEffect = R(@"discard this card|this card from your hand|(send|banish|reveal) this card (from|in) your hand|while this card is in your hand");
        // Its own summoning procedure from the hand ("You can Special Summon this card (from your hand)").
        private static readonly Regex SelfSummonProcedure = R(@"special summon this card(?! from your (graveyard|gy))");
        // An effect it can use again and again ("Once per turn: ...").
        private static readonly Regex Repeatable = R(@"once per turn");

        /// <summary>Bonus for a Special Summon effect a monster can use every turn (Faultroll's revive, Gemini Soldier's recruit).</summary>
        [Tunable("Bonus for a Special Summon effect a monster can use every turn (Faultroll's revive, Gemini Soldier's recruit).")]
        public static double RepeatableSummonBonus = 1.5;

        /// <summary>
        /// How much pressure a monster's attacks put on our life points: its ATK plus only the effects that fire when it
        /// hits (battle damage / direct attack, e.g. Airbellum's discard). For life-point questions only: a 900 ATK
        /// Grand Mole is little pressure, but still a strong card to remove (use ScoreAsync for that).
        /// </summary>
        public static Task<(double Score, string Why)> PressureAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds, int ourBestBlocker) =>
            ScoreCoreAsync(cardId, ownerGraveIds, ourBestBlocker, pressureOnly: true, depth: 0);

        private static async Task<(double Score, string Why)> ScoreCoreAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds, int? enemyBestBlocker, bool pressureOnly, int depth, bool onField = false,
            bool destroying = false, bool inGrave = false)
        {
            var info = await CardDatabase.GetAsync(cardId);
            if (info is not DBCardInfo card)
                return (0, "unknown card");

            string text = Drawbacks.Replace(card.CardText ?? "", "");
            var reasons = new List<string>();
            double score = 0;

            if ((card.Type & (int)CardType.Monster) != 0 && !inGrave)
            {
                score += BotTest.Game.AI.Search.MctsEvaluator.AtkValue(card.Attack); // the user's ATK table (MctsEvaluator.AtkTable), as everywhere
                reasons.Add($"{card.Attack} ATK: {BotTest.Game.AI.Search.MctsEvaluator.AtkValue(card.Attack):0.00}");
                // A Tuner opens synchros (user, 2026-11-17: "being a tuner should count towards the monster's effect score too").
                if ((card.Type & 0x1000) != 0 && !pressureOnly)
                {
                    score += TunerValue;
                    reasons.Add($"Tuner: {TunerValue}");
                }
            }

            foreach (var rule in Rules)
            {
                if (!rule.Pattern.IsMatch(text))
                    continue;

                double value = rule.Value;
                bool isMonster = (card.Type & (int)CardType.Monster) != 0;
                // Every sentence with this effect; on the field, drop the spent ones (on-summon triggers, its own
                // summoning procedure). Among the rest, a Special Summon of another monster from the graveyard beats
                // summoning itself (Faultroll: "Special Summon this card from your hand" comes first in its text).
                var sentences = SentenceWith(rule.Pattern).Matches(text).Select(m => m.Value).ToList();
                // From the graveyard only what works there counts (whatever the card would do on the field or from the hand can't).
                if (inGrave)
                {
                    sentences = sentences.Where(s => GraveyardEffect.IsMatch(s) || IsGraveyardPlaySentence(s)).ToList();
                    if (sentences.Count == 0)
                        continue;
                }
                if (onField && isMonster)
                {
                    bool hadAny = sentences.Count > 0;
                    var notSpent = sentences.Where(s => !OnSummonTrigger.IsMatch(s) && !(rule.Name == "special summons" && SelfSummonProcedure.IsMatch(s))).ToList();
                    // Graveyard effects (Vayu, Necro Gardna) can't be used while it's on the field, nor hand-only ones (D.D. Crow's "discard
                    // this card": seen 2026-11-18, a summoned Crow kept its whole effect, at the field weight, and the search summoned it).
                    var notGrave = notSpent.Where(s => !GraveyardEffect.IsMatch(s)).ToList();
                    sentences = notGrave.Where(s => !HandOnlyEffect.IsMatch(s)).ToList();
                    if (sentences.Count == 0)
                    {
                        reasons.Add(!hadAny || notSpent.Count == 0 ? $"{rule.Name} (on summon: already used)"
                            : notGrave.Count == 0 ? $"{rule.Name} (works from the GY: not on the field)" : $"{rule.Name} (works from the hand: not on the field)");
                        continue;
                    }
                }
                string trigger = (rule.Name == "special summons"
                    ? sentences.FirstOrDefault(s => RevivesFromGraveyard.IsMatch(WithoutGraveCost(s)) && !RevivesItself.IsMatch(s))
                    : null) ?? sentences.FirstOrDefault() ?? "";
                // A card that only summons itself (a union's "unequip and Special Summon it", Machina Fortress from the hand or GY): very
                // small, and never the "repeatable" bonus (user, 2026-11-17: Peacekeeper's union effect scored like recruiting another monster
                // every turn; "only count self-summon effects very small").
                // A union's equip / unequip ("Unequip this card and Special Summon it") isn't a summon effect at all (user, 2026-11-17:
                // "gearframe and peacekeeper don't have self-summon effects. union effects should not count as self-summons").
                if (rule.Name == "special summons")
                    sentences = sentences.Where(s => !UnionUnequip.IsMatch(s)).ToList();
                if (rule.Name == "special summons" && sentences.Count == 0)
                {
                    reasons.Add("union equip / unequip: not a summon effect");
                    continue;
                }
                if (rule.Name == "special summons" && sentences.All(s => !SummonsAnother.IsMatch(s)))
                {
                    // From the graveyard, coming back is a whole card regained (user, 2026-11-17: Plaguespreader, Treeborn, Fortress).
                    double self = inGrave ? GraveSelfReviveValue : SelfSummonValue;
                    score += self;
                    reasons.Add(inGrave ? $"revives itself from the GY: {self}" : $"summons itself: {self}");
                    continue;
                }
                if (isMonster && rule.Name.StartsWith("special summons") && Repeatable.IsMatch(trigger) && !OnDestroyedTrigger.IsMatch(trigger))
                {
                    value += RepeatableSummonBonus;
                    reasons.Add($"+{RepeatableSummonBonus} (repeatable: once per turn)");
                }
                if (pressureOnly && !OnBattleDamageTrigger.IsMatch(trigger))
                    continue; // not something its attacks do to us
                if (rule.Name == "recovers from the graveyard" && !await GraveyardHasTargetAsync(text, ownerGraveIds))
                {
                    value *= NoTargetShare;
                    reasons.Add($"{rule.Name} (no target in GY)");
                }
                else if (rule.Name == "special summons" && RevivesFromGraveyard.IsMatch(WithoutGraveCost(trigger)) && !RevivesItself.IsMatch(trigger))
                {
                    // Reviving is worth what it revives: nothing without a target, more with a strong one.
                    var targets = await GraveyardTargetsAsync(trigger, ownerGraveIds);
                    if (targets.Count == 0)
                    {
                        value *= NoTargetShare;
                        reasons.Add($"{rule.Name} (no target in GY)");
                    }
                    else if (depth == 0)
                    {
                        double best = 0;
                        int bestId = 0;
                        foreach (int target in targets.Distinct())
                        {
                            var (targetScore, _) = await ScoreCoreAsync(target, ownerGraveIds, null, pressureOnly: false, depth: 1);
                            if (targetScore > best)
                                (best, bestId) = (targetScore, target);
                        }
                        value += best * ReviveTargetShare;
                        reasons.Add($"{rule.Name} (revives {CardNames.Get(bestId)})");
                    }
                    else
                    {
                        reasons.Add(rule.Name);
                    }
                }
                else if (isMonster && enemyBestBlocker.HasValue && OnBattleDamageTrigger.IsMatch(trigger))
                {
                    bool live = card.Attack > enemyBestBlocker.Value;
                    value *= live ? LiveBattleTriggerFactor : BlockedBattleTriggerFactor;
                    reasons.Add(live ? $"{rule.Name} (live: nothing blocks it)" : $"{rule.Name} (needs battle damage, blocked)");
                }
                else if (isMonster && OnDestroyedTrigger.IsMatch(trigger))
                {
                    // A search / recruit that needs the card to die only replaces it (user, 2026-11-17: Peacekeeper's search "goes +0 because it
                    // has to die for it to be activated"): one card, not a share of a searcher's points.
                    value = rule.Name is "searches the deck" or "special summons from the deck" ? OnDestroyedReplaceValue : value * OnDestroyedShare;
                    if (destroying && !DestroyedByBattleOnly.IsMatch(trigger))
                    {
                        value = -value;
                        reasons.Add($"{rule.Name} (on destruction: destroying it sets it off, counts against)");
                    }
                    else
                        reasons.Add($"{rule.Name} (only if destroyed)");
                }
                else
                {
                    reasons.Add(rule.Name);
                }
                // Removal of everything (Absolute Zero, Mirror Force, Heavy Storm, Torrential) is worth more than of one card
                // (user, 2026-11-13: "destroying everything should be worth more than destroying a single card").
                if (rule.Name is "destroys" or "banishes" or "bounces" && RemovesAll.IsMatch(trigger))
                {
                    value *= AllRemovalFactor;
                    reasons.Add($"x{AllRemovalFactor} (all of them)");
                }
                score += value;
            }

            // From the graveyard, two things no rule above covers (user, 2026-11-17, sense-checking the GY scores): getting this card back to
            // the hand (Indomitable Gladiator Beast) is a card regained; a stat boost (Skill Successor's banish) is a small edge in one battle.
            if (inGrave)
            {
                var graveSentences = Regex.Matches(text, @"[^.]+").Select(m => m.Value).Where(s => GraveyardEffect.IsMatch(s) || IsGraveyardPlaySentence(s)).ToList();
                if (graveSentences.Any(s => ReturnsItselfToHand.IsMatch(s)))
                {
                    score += GraveSelfReviveValue;
                    reasons.Add($"back to the hand from the GY: {GraveSelfReviveValue}");
                }
                if (graveSentences.Any(s => GainsStats.IsMatch(s)))
                {
                    score += GraveStatBoostValue;
                    reasons.Add($"a stat boost from the GY: {GraveStatBoostValue}");
                }
            }

            if (!pressureOnly && (KnownCardAdjustments.TryGetValue(cardId, out var adjust) ||
                                  card.Alias != 0 && KnownCardAdjustments.TryGetValue(card.Alias, out adjust)))
            {
                score += adjust.Bonus;
                reasons.Add($"{(adjust.Bonus >= 0 ? "+" : "")}{adjust.Bonus} ({adjust.Why})");
            }

            if (onField && (card.Type & (int)CardType.Monster) != 0 && GraveyardEffect.IsMatch(text))
                reasons.Add("works from the GY: avoid sending it there, banish it if possible");

            if ((card.Type & (int)CardType.Normal) != 0 && (card.Type & (int)CardType.Effect) == 0)
                reasons.Add("no effect");

            return (score, $"{card.Name}: {score:0.0} ({string.Join(", ", reasons)})");
        }

        /// <summary>Extra value of banishing (instead of destroying) a monster that can come back from the graveyard.</summary>
        [Tunable("Extra value of banishing (instead of destroying) a monster that can come back from the graveyard.")]
        public static double BanishRecursionBonus = 2;

        private static readonly Regex SelfRecursion = R(@"special summon this card from[^.]*(graveyard|\bgy\b)");

        // An effect that only works while the card is in the graveyard (Vayu, Plaguespreader, Necro Gardna).
        private static readonly Regex GraveyardEffect = R(
            @"this card is in (the|your) (graveyard|gy)|banish this card (and [^.]*? )?from (the|your) (graveyard|gy)");

        /// <summary>Whether the card text has an effect that works while the card is in the graveyard.</summary>
        public static bool HasGraveyardEffect(string cardText) => GraveyardEffect.IsMatch(cardText);

        // THIS card works from the graveyard: "if this card is in your GY", "banish this card from your GY", "Special Summon
        // this card (from your hand or GY)" / "this card from your GY". Not effects on other graveyard cards (Chaos
        // Sorcerer's cost, Red-Eyes Darkness Metal Dragon's revive).
        private static readonly Regex FromGraveyard = R(
            @"this card is in (the|your) (graveyard|gy)|banish this card (and [^.]*? )?from (the|your) (graveyard|gy)|" +
            @"this card (\([^)]*\b(graveyard|gy)\b[^)]*\)|from (the|your) (hand or )?(graveyard|gy))");
        private static readonly Regex MakesPlay = R(@"special summon|add [^.]*? to your hand|draw");

        /// <summary>
        /// Whether a card in the graveyard is fuel for a play: an effect that works from there and special summons, adds
        /// or draws (Vayu, Plaguespreader, Machina Fortress). Defensive ones (Necro Gardna negating an attack) and cards that
        /// do nothing there don't count.
        /// </summary>
        public static bool HasGraveyardPlay(string cardText) =>
            // Whole effects (up to the full stop): one that works while the card sits in the graveyard (activatable from
            // there, any time its condition holds) and makes a play. A trigger on being sent / destroyed to the graveyard
            // (Sangan, Card Trooper's draw) is used up by the time the card is there: it doesn't count, like a "when Normal
            // Summoned" effect once the monster is out.
            Regex.Matches(cardText, @"[^.]+").Any(effect =>
                FromGraveyard.IsMatch(effect.Value) && MakesPlay.IsMatch(effect.Value) && !SentToGraveyardTrigger.IsMatch(effect.Value));

        private static bool IsGraveyardPlaySentence(string s) => FromGraveyard.IsMatch(s) && MakesPlay.IsMatch(s) && !SentToGraveyardTrigger.IsMatch(s);

        // "If this card is sent (from the field) to the GY:", "When this card is destroyed and sent to the Graveyard:".
        private static readonly Regex SentToGraveyardTrigger = R(
            @"(sent|destroyed)[^.:]*?(to|in) (the|your) (gy|graveyard)[^.:]*?:");

        /// <summary>Banish bonus for a card with a graveyard effect when the roll says it's worth banishing (low: banishing
        /// what it would make is usually just as good).</summary>
        [Tunable("Banish bonus for a card with a graveyard effect when the roll says it's worth banishing (low: banishing what it would make is usually just as good).")]
        public static double GraveyardEffectBanishBonus = 1;

        /// <summary>
        /// Whether to prefer banishing this card (instead of sending it to the graveyard, where its effect works), decided
        /// by a roll: more likely when the owner's graveyard has no copy of it yet and plenty of monsters its effect can
        /// use (e.g. Vayu with Blackwing partners), less likely when copies are already there (one more adds little).
        /// Returns the bonus (0 or <see cref="GraveyardEffectBanishBonus"/>) and the reason, or (0, null) if it has no
        /// graveyard effect.
        /// </summary>
        public static async Task<(double Bonus, string? Why)> GraveyardEffectBanishAsync(int cardId, IReadOnlyCollection<int> ownerGraveIds, Random? rng = null)
        {
            if (await CardDatabase.GetAsync(cardId) is not DBCardInfo card)
                return (0, null);
            string text = card.CardText ?? "";
            var sentence = SentenceWith(GraveyardEffect).Match(text);
            if (!sentence.Success)
                return (0, null);
            int code = card.Alias != 0 ? card.Alias : cardId;
            int copies = 0;
            foreach (int id in ownerGraveIds)
                if (id == cardId || id == code || await CardDatabase.GetAsync(id) is DBCardInfo g && g.Alias == code)
                    copies++;
            int partners = GraveyardClause.IsMatch(sentence.Value) ? (await GraveyardTargetsAsync(sentence.Value, ownerGraveIds)).Count : 0;
            double p = Math.Clamp(GraveBanishBase + GraveBanishPerPartner * Math.Min(partners, 4) - GraveBanishPerCopy * copies, 0.05, 0.9);
            bool banish = p >= 0.5; // no roll (user, 2026-11-17): banish when it's at least an even chance
            string why = $"works from the GY: avoid sending it there ({copies} already in their GY, {partners} monster(s) it can use: " +
                         $"banish chance {p:P0} -> {(banish ? $"+{GraveyardEffectBanishBonus}, banish it" : "not worth it")})";
            return (banish ? GraveyardEffectBanishBonus : 0, why);
        }

        /// <summary>Whether the card can Special Summon itself from the graveyard (so destroying it isn't enough).</summary>
        public static async Task<bool> ReturnsFromGraveyardAsync(int cardId) =>
            await CardDatabase.GetAsync(cardId) is DBCardInfo card && SelfRecursion.IsMatch(card.CardText ?? "");

        /// <summary>Checks a "... from your Graveyard" clause's Level/Type requirement against the graveyard.</summary>
        private static async Task<bool> GraveyardHasTargetAsync(string text, IReadOnlyCollection<int> graveIds)
        {
            if (!GraveyardClause.IsMatch(text))
                return graveIds.Count > 0;
            return (await GraveyardTargetsAsync(text, graveIds)).Count > 0;
        }

        /// <summary>Graveyard monsters meeting a "... in/from your Graveyard" clause's Level/Type requirement.</summary>
        private static async Task<List<int>> GraveyardTargetsAsync(string text, IReadOnlyCollection<int> graveIds)
        {
            var found = new List<int>();
            var clause = GraveyardClause.Match(text);
            if (!clause.Success)
                clause = Regex.Match(text, ".*");

            var level = LevelRequirement.Match(clause.Value);
            string lower = clause.Value.ToLowerInvariant();
            CardRace? requiredRace = null;
            foreach (var (word, race) in RaceWords)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(word)}(-type)? monster"))
                {
                    requiredRace = race;
                    break;
                }
            }

            foreach (int id in graveIds)
            {
                if (await CardDatabase.GetAsync(id) is not DBCardInfo grave || (grave.Type & (int)CardType.Monster) == 0)
                    continue;

                if (level.Success)
                {
                    int required = int.Parse(level.Groups[1].Value);
                    string direction = level.Groups[3].Value.ToLowerInvariant();
                    bool levelOk = direction switch
                    {
                        "lower" => grave.Level <= required,
                        "higher" => grave.Level >= required,
                        _ => grave.Level == required
                    };
                    if (!levelOk)
                        continue;
                }

                if (requiredRace.HasValue && (grave.Race & (int)requiredRace.Value) == 0)
                    continue;

                found.Add(id);
            }

            return found;
        }
    }
}
