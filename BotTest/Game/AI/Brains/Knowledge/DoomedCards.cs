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
    /// Our cards about to be removed anyway (their chain targets them, a mass removal is resolving), and what using a doomed
    /// card is worth.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
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

        /// <summary>One of our cards in the Spell/Trap zone (offered in a chain prompt): it may be about to be removed (TryChainBaitAsync).</summary>
        protected static bool IsOurSetCard(DynamicCard card) =>
            card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && card.CurrentCoordinates.Controller == 0;

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
            if (ours.IsCode(RaigekiBreak) || ours.IsCode(IcarusAttack))
                return await DoomedRemovalWorthAsync(context, ours);
            if (!ours.IsCode(SolemnJudgment))
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


        /// <summary>
        /// A doomed Raigeki Break / Icarus Attack, weighed with itself free: the best card(s) of theirs it destroys (Raigeki 1, Icarus 2: a face-up
        /// monster by its effect score, a face-down card at UnknownCardTargetWorth) against what it costs (Raigeki: our cheapest card in hand to
        /// discard, by its effect score in hand; Icarus: our cheapest Winged Beast to tribute, by its effect score on the field). Used when the
        /// gain is bigger; Raigeki's target and Icarus's tribute are planned.
        /// </summary>
        protected async Task<(bool Use, string Why)> DoomedRemovalWorthAsync(DecisionContext<SelectChainData> context, DynamicCard ours)
        {
            bool icarus = ours.IsCode(IcarusAttack);
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
        public static readonly Tuned<double> UnknownCardTargetWorth = new(1.5);

        /// <summary>Chance a set bait card is hit (untargeted-looking removal) from which it's chained. Tune from logs.</summary>
        [Tunable("Chance a set bait card is hit (untargeted-looking removal) from which it's chained. Tune from logs.")]
        public double BaitHitChance { get; set; } = 0.5;

        /// <summary>
        /// Spell/trap removers whose text the card database doesn't have (shown as "#id" in the logs): how many they take.
        /// XX-Saber Hyunlei: on Synchro Summon, destroy up to 3 spells/traps.
        /// </summary>
        private static readonly Dictionary<int, int> KnownSpellTrapRemovers = new()
        {
            [XXSaberHyunlei] = 3, // XX-Saber Hyunlei
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
            // Their attack declared at a monster of ours that loses the battle: doomed too (user, 2026-11-18: "would blizzard have been marked as
            // doomed if an attack was declared at it? it should have been").
            var phase = (DuelPhase)header.CurrentPhase;
            if (!IsOwnTurn(header) && phase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle)
            {
                var attacker = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
                var defender = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
                if (attacker.ID > 0 && defender.ID > 0 && defender.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && defender.IsFaceup())
                {
                    int power = defender.IsAttack() ? defender.Attack : defender.Defense;
                    if (defender.IsAttack() ? attacker.Attack >= power : attacker.Attack > power)
                    {
                        result.Add((defender, 1.0, $"{CardNames.Get(attacker.ID)} ({attacker.Attack}) attacks it ({power}): it loses the battle"));
                        return result;
                    }
                }
            }
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
    }
}
