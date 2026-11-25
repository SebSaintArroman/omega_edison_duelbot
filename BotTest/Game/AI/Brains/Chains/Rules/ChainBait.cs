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
    /// RULES deck.chain-bait, deck.doomed-card-used and deck.doomed-monster-used: our set cards (and monsters) about to be
    /// removed anyway are used first, and bait cards are chained to draw out their answers.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Set cards worth keeping face-down as bait: chained when the opponent's removal would hit them.</summary>
        // Waboku too: set, it's chained when their removal hits it, rather than lost (user, 2026-11-11).
        /// <summary>Shared bait-tagged cards; deck brains add their own (e.g. Burial from a Different Dimension).</summary>
        protected static readonly int[] DefaultBaitCards = CardRuleRegistry.CardsWithTag(TagRules.Tag.Bait).ToArray();

        /// <summary>Bait cards for this deck right now (deck brains add their own, e.g. Burial from a Different Dimension).</summary>
        protected virtual IReadOnlyCollection<int> BaitCardIds => CardRuleRegistry.CardsWithTag(TagRules.Tag.Bait);

        /// <summary>
        /// Bait: a set Jar of Greed / Legacy of Yata-Garasu / MST is chained when the opponent's current activation would
        /// remove it, so their removal hits nothing (and MST takes one of their cards for free). Threats: an opponent
        /// card targeting its zone, or a Normal/Quick-Play spell or Normal trap being activated whose text removes all
        /// spells/traps. Returns the chain index, or null.
        /// </summary>
        // The removal we chained bait against this turn (it resolves after our chain links), so the rest of the bait follows.
        private (int Turn, int Phase, string Name, int Reach)? _baitThreat;

        /// <summary>What one chain-bait decision sees: the offered cards, which of ours are bait or costly doomed cards, their cards.</summary>
        private sealed class ChainBaitSituation
        {
            public required DecisionContext<SelectChainData> Context { get; init; }
            public required DynamicCard[] Options { get; init; }
            /// <summary>A set card of ours free to activate that goes off when about to be removed (bait, or doomed and it pays off).</summary>
            public required Func<DynamicCard, bool> IsSetBait { get; init; }
            /// <summary>A set card of ours with an activation cost of its own (Solemn, Icarus, Raigeki Break): weighed, the card itself free.</summary>
            public required Func<DynamicCard, bool> IsCostlyDoomed { get; init; }
            /// <summary>A bait we'd chain now (MST only with a real target).</summary>
            public required Func<DynamicCard, bool> UsableBait { get; init; }
            public required List<DynamicCard> EnemySpells { get; init; }
            public required List<DynamicCard> EnemyCards { get; init; }
            public required List<DynamicCard> OurSpellZone { get; init; }
            /// <summary>Every spell/trap of ours their removal could take, face-up continuous ones too.</summary>
            public int OurTargets { get; init; }
            /// <summary>Their target coordinate is our card at this coordinate.</summary>
            public required Func<CardCoordinate, CardCoordinate, bool> HitsOurs { get; init; }
            /// <summary>Chain the option at this index (RULE deck.chain-bait); -1 when the rule doesn't fire.</summary>
            public required Func<int, string, int> Chain { get; init; }
            /// <summary>A costly doomed card weighed (DoomedCostlyWorthAsync): its index to chain, or null.</summary>
            public required Func<int, DynamicCard?, int, string, Task<int?>> CostlyDoomed { get; init; }
        }

        private static IEnumerable<CardCoordinate> TargetedBy(DynamicCard card) =>
            (card.ChainOnlyOwnTargets ?? Array.Empty<CardCoordinate>()).Concat(card.OwnTargets ?? Array.Empty<CardCoordinate>());

        /// <summary>
        /// Our set cards about to be removed: used first. Not only the bait staples (Jar of Greed, Yata, MST): any set card of ours
        /// the server lets us activate here, about to be removed, goes off if activating it does any good at all (RULE
        /// deck.doomed-card-used; user, 2026-11-12: "if a card is about to be destroyed, and activating it would have a positive
        /// effect, no matter how small, then it should always be used"). A doomed Torrential uses the same board valuation as a
        /// summon response. "Always use it" is for cards free to activate; a card with a cost of its own (Solemn's half LP, Icarus
        /// Attack's tribute, Raigeki Break's discard) is weighed like the normal card, with the card itself counted as free because
        /// it's lost either way (user, 2026-11-12; round-robin 15: Solemn went off "as a doomed card" on a Pot of Greed).
        /// The steps, in order: their card targets ours; their removal with no target reported; mass removal being activated.
        /// </summary>
        protected async Task<int?> TryChainBaitAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            var bait = BaitCardIds;
            bool torrentialPays = false;
            if (options.Any(card => IsOurSetCard(card) && card.IsCode(TorrentialTribute)))
            {
                var (worth, torrentialWhy) = TorrentialWorthIt(await GetEnemyMonsterCardsAsync(),
                    (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 || card.CurrentCoordinates.Location != 0).ToList(),
                    await TorrentialDefendingLpAsync(context));
                torrentialPays = worth;
                context.Bot.LogNote($"deck brain: doomed Torrential Tribute: {torrentialWhy}");
            }
            bool PaysOff(DynamicCard card) => !card.IsCode(TorrentialTribute) || torrentialPays;
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
                if (options[index].IsCode(MysticalSpaceTyphoon))
                    _lastStapleActivated = MysticalSpaceTyphoon;
                context.Bot.LogNote($"deck brain: chain {CardNames.Get(options[index].ID)} before it is removed: {why}");
                return index;
            }
            var ourSpellZone = await CurrentField.GetCardsInSpellZone();
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
            // MST only goes as bait when it has a real target (seen 2026-11-10: chained at their resolving MST, the only card it
            // could take: it did nothing).
            bool mstHasTarget = enemySpells.Any(card => card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && WorthRemovingSpellTrap(card));
            // Every spell/trap of ours their removal could take, face-up continuous ones too (seen 2026-11-10: "1 set, 100% it hits
            // this one" with two face-up Black Whirlwinds beside it).
            var b = new ChainBaitSituation
            {
                Context = context, Options = options, IsSetBait = IsSetBait, IsCostlyDoomed = IsCostlyDoomed,
                UsableBait = card => IsSetBait(card) && (!card.IsCode(MysticalSpaceTyphoon) || mstHasTarget),
                EnemySpells = enemySpells, EnemyCards = enemyCards, OurSpellZone = ourSpellZone.ToList(),
                OurTargets = ourSpellZone.Count(card => card.ID > 0 && WorthRemovingSpellTrap(card)),
                HitsOurs = HitsOurs, Chain = Chain, CostlyDoomed = CostlyDoomed,
            };
            return await ChainBaitWhenTargetedAsync(b)
                ?? await ChainBaitAgainstUntargetedRemovalAsync(b)
                ?? await ChainBaitAgainstMassRemovalAsync(b);
        }

        /// <summary>
        /// Targeted: chain the bait only when the opponent's card targets it (otherwise it stays as a decoy). The server can report
        /// this on either side: their card's targets (CurrentChainTargets / TargetCards), or our card's "targeted by" lists
        /// (ChainOnlyOwnTargets / OwnTargets), on the prompt's copy of the card or on a fresh field read. Check all of them.
        /// </summary>
        private async Task<int?> ChainBaitWhenTargetedAsync(ChainBaitSituation b)
        {
            var options = b.Options;
            for (int i = 0; i < options.Length; i++)
            {
                // UsableBait, not IsSetBait: a targeted MST still needs a target of theirs, never one of ours (user, 2026-11-25:
                // Ryko targeted our set MST, they had no spells/traps, and MST was chained onto our own Black Whirlwind).
                if (!b.UsableBait(options[i]) && !b.IsCostlyDoomed(options[i]))
                    continue;
                var at = options[i].CurrentCoordinates;
                var attacker = b.EnemyCards.FirstOrDefault(card =>
                    (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>())
                        .Any(t => b.HitsOurs(t, at)));
                var fieldCopy = b.OurSpellZone.FirstOrDefault(card => card.CurrentCoordinates.Sequence == at.Sequence);
                var by = TargetedBy(options[i]).Concat(TargetedBy(fieldCopy)).FirstOrDefault(t => t.Controller == 1);
                string? targetedHow = attacker.ID > 0 ? $"{CardNames.Get(attacker.ID)} targets it"
                    : by.Location != 0 ? $"the opponent's card in {(by.Location == (byte)CardLocation.SpellZone ? $"S{by.Sequence + 1}" : $"location {by.Location} #{by.Sequence + 1}")} targets it"
                    : null;
                if (targetedHow == null)
                    continue;
                if (b.IsCostlyDoomed(options[i]))
                {
                    if (await b.CostlyDoomed(i, attacker.ID > 0 ? attacker : null, 1, targetedHow) is int costlyIndex)
                        return costlyIndex;
                    continue;
                }
                return b.Chain(i, $"{targetedHow}: use it before it is removed");
            }
            return null;
        }

        /// <summary>
        /// The opponent just activated something that removes spells/traps (a new face-up spell, or the monster they just summoned,
        /// e.g. XX-Saber Hyunlei on its Synchro Summon), but no target was reported. Then it's a probabilistic call: if it can take
        /// N of our set cards and we have S, a given bait is hit with chance about min(1, N / S) (they aim at ours, not their own).
        /// Chain when that's likely (BaitHitChance). Still in the chain we started against their removal (our bait is now the latest
        /// link): the removal hasn't resolved yet, so the rest of our bait goes too while it can still be hit (seen 2026-11-10: MST
        /// chained against Hyunlei, then Jar of Greed passed because the latest link was ours).
        /// </summary>
        private async Task<int?> ChainBaitAgainstUntargetedRemovalAsync(ChainBaitSituation b)
        {
            var context = b.Context;
            var options = b.Options;
            int ourTargets = b.OurTargets;
            if (context.Header.LastChainPlayer == 0 && _baitThreat is { } threat && threat.Turn == context.Header.CurrentTurn && threat.Phase == context.Header.CurrentPhase)
            {
                double hitNow = ourTargets == 0 ? 0 : Math.Min(1, (double)threat.Reach / ourTargets);
                if (hitNow >= BaitHitChance && options.Any(b.UsableBait))
                {
                    int next = Enumerable.Range(0, options.Length).Where(i => b.UsableBait(options[i]))
                        .OrderBy(i => options[i].IsCode(MysticalSpaceTyphoon) ? 0 : options[i].IsCode(JarOfGreed, LegacyOfYataGarasu) ? 1 : 2)
                        .First();
                    return b.Chain(next, $"still in the chain: {threat.Name} removes up to {threat.Reach} spell/trap(s) and we have {ourTargets} it could take: " +
                                         $"{hitNow:P0} it hits this one too, use it first");
                }
            }
            if (context.Header.LastChainPlayer != 0)
                _baitThreat = null; // a new chain: the earlier threat has resolved
            if (context.Header.LastChainPlayer != 1)
                return null;

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
                var bait0 = options.FirstOrDefault(c => b.IsSetBait(c) || b.IsCostlyDoomed(c));
                var field0 = b.OurSpellZone.FirstOrDefault(c => c.CurrentCoordinates.Sequence == bait0.CurrentCoordinates.Sequence);
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
                if (!options.Any(b.UsableBait))
                {
                    // Only a card with a cost is in reach: weighed with the card itself free.
                    string how = $"{CardNames.Get(card.ID)} removes up to {reach} spell/trap(s) and we have {ourSet} it could take ({hit:P0} it hits this one)";
                    for (int i = 0; i < options.Length; i++)
                        if (b.IsCostlyDoomed(options[i]) && await b.CostlyDoomed(i, card, reach, how) is int costlyIndex)
                            return costlyIndex;
                    if (options.Any(c => c.IsCode(MysticalSpaceTyphoon) && b.IsSetBait(c)))
                        context.Bot.LogNote($"deck brain: {CardNames.Get(card.ID)} may take our set MST, but MST has nothing worth taking " +
                                            "(their only spell/traps are resolving): keep it down");
                    continue;
                }
                // Best bait first: MST takes one of their cards, then the draws, then the rest.
                int best = Enumerable.Range(0, options.Length).Where(i => b.UsableBait(options[i]))
                    .OrderBy(i => options[i].IsCode(MysticalSpaceTyphoon) ? 0 : options[i].IsCode(JarOfGreed, LegacyOfYataGarasu) ? 1 : 2)
                    .First();
                _baitThreat = (context.Header.CurrentTurn, context.Header.CurrentPhase, CardNames.Get(card.ID), reach);
                return b.Chain(best, $"{CardNames.Get(card.ID)} removes up to {reach} spell/trap(s) and we have {ourSet} it could take: " +
                                     $"{hit:P0} it hits this one, use it before it is removed (no target was reported; {targetInfo})");
            }
            return null;
        }

        /// <summary>
        /// Mass removal being activated: theirs, or our own (our Heavy Storm would destroy our set Jar of Greed too, so use it in
        /// response rather than lose it). Not MST: it would only destroy a card the mass removal destroys anyway.
        /// </summary>
        private async Task<int?> ChainBaitAgainstMassRemovalAsync(ChainBaitSituation b)
        {
            var options = b.Options;
            var ourFaceUp = (await CurrentField.GetCardsInSpellZone()).Where(card => card.ID > 0 && card.IsFaceup());
            foreach (var card in b.EnemySpells.Where(card => card.IsFaceup()).Concat(ourFaceUp))
            {
                bool oneShot = card.HasType(CardType.Spell) || card.HasType(CardType.Trap);
                bool staysOnField = card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip);
                if (!oneShot || staysOnField)
                    continue; // a face-up Normal/Quick-Play spell or Normal trap is one being activated right now
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info || !MassBackrowRemoval.IsMatch(info.CardText ?? ""))
                    continue;
                int baitIndex = Array.FindIndex(options, option => b.IsSetBait(option) && !option.IsCode(MysticalSpaceTyphoon));
                bool ours = card.CurrentCoordinates.Controller == 0;
                if (baitIndex < 0)
                {
                    // Only a card with a cost left: weighed with the card itself free (their mass removal takes every one of ours).
                    if (!ours)
                        for (int i = 0; i < options.Length; i++)
                            if (b.IsCostlyDoomed(options[i]) &&
                                await b.CostlyDoomed(i, card, b.OurTargets, $"{CardNames.Get(card.ID)} would clear our backrow ({b.OurTargets} of ours)") is int costlyIndex)
                                return costlyIndex;
                    return null;
                }
                return b.Chain(baitIndex, ours
                    ? $"our own {CardNames.Get(card.ID)} would destroy it too: use it in response"
                    : $"{CardNames.Get(card.ID)} would clear our backrow: use it in response");
            }
            return null;
        }
    }
}
