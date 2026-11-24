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
    /// Shared rules for era staples around Machines and the graveyard:
    ///  - Chimeratech Fortress Dragon: everyone plays it, and it's made by sending Cyber Dragon + Machines from EITHER
    ///    field (the opponent's only if face-up). So we never leave our face-up Cyber Dragon next to another face-up
    ///    Machine; if we end up there anyway, at the end of our turn we fuse it ourselves (Cyber Dragon + our weakest
    ///    Machine, plus any face-up Machines of theirs) so at least they can't.
    ///  - D.D. Crow is never summoned: it's chained to the opponent's card that targets a card in their graveyard, and
    ///    banishes that target.
    ///  - Fusion material prompts follow the plan made when the fusion was decided.
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        /// <summary>Monsters that are never Normal Summoned or Set: their value is a hand effect (D.D. Crow).</summary>
        protected static readonly int[] NeverSummonIds = { DDCrow };

        protected static bool IsMachine(DynamicCard card) => (card.Race & (int)CardRace.Machine) != 0;

        /// <summary>The idle state with the never-summon monsters (D.D. Crow) taken out of the summon / set lists.</summary>
        /// <summary>A D.D. Crow that could have been set at the last filtered decision: only for the last-resort blocker (round-robin 14b).</summary>
        private DynamicCard _neverSummonSet;

        protected IdleCmdData WithoutNeverSummons(IdleCmdData state)
        {
            _neverSummonSet = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(NeverSummonIds));
            state.SummonableCards = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(NeverSummonIds)).ToArray();
            state.MonsterSetableCards = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).Where(card => !card.IsCode(NeverSummonIds)).ToArray();
            // The hard rules (see PlanMayChoose): the brain obeys them like the search and the planner.
            state.SummonableCards = state.SummonableCards.Where(card => PlanMayChoose(card.ID, MainAction.Summon)).ToArray();
            state.MonsterSetableCards = state.MonsterSetableCards
                .Where(card => !CardRuleRegistry.NeverSets(card) && PlanMayChoose(card.ID, MainAction.SetMonster)).ToArray();
            state.SpecialSummonableCards = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Where(card => PlanMayChoose(card.ID, MainAction.SpSummon)).ToArray();
            return state;
        }

        // ------------------------------------------------------------------ Chimeratech Fortress Dragon

        /// <summary>
        /// A monster of ours that leaves the field before their turn (a Jinzo revived by Jinzo - Returner is destroyed in our
        /// End Phase): no Chimeratech material for them (user, 2026-11-10). Default: none.
        /// </summary>
        protected virtual bool GoneByTheirTurn(DynamicCard card) => false;

        /// <summary>
        /// Why our board is open to the opponent's Chimeratech Fortress Dragon (our face-up Cyber Dragon next to another
        /// face-up Machine of ours), or null.
        /// </summary>
        protected string? ChimeratechExposure(IEnumerable<DynamicCard> botMonsters)
        {
            var faceUp = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && !GoneByTheirTurn(card)).ToList();
            var cyber = faceUp.FirstOrDefault(card => card.IsCode(CyberDragon));
            if (cyber.ID <= 0)
                return null;
            var machine = faceUp.FirstOrDefault(card => IsMachine(card) && !SameZone(card, cyber));
            return machine.ID > 0
                ? $"our face-up Cyber Dragon next to our face-up {CardNames.Get(machine.ID)}: their Chimeratech Fortress Dragon takes both"
                : null;
        }

        /// <summary>
        /// Why summoning this monster face-up would open us to Chimeratech Fortress Dragon (Cyber Dragon next to a face-up
        /// Machine of ours), or null.
        /// </summary>
        protected string? SummonExposesChimeratech(DynamicCard summoning, IEnumerable<DynamicCard> botMonsters)
        {
            var faceUp = botMonsters.Where(card => card.ID > 0 && card.IsFaceup() && !GoneByTheirTurn(card)).ToList();
            if (summoning.IsCode(CyberDragon) && faceUp.FirstOrDefault(IsMachine) is { ID: > 0 } machine)
                return $"Cyber Dragon next to our face-up {CardNames.Get(machine.ID)} opens us to their Chimeratech Fortress Dragon";
            return IsMachine(summoning) && faceUp.Any(card => card.IsCode(CyberDragon))
                ? $"{CardNames.Get(summoning.ID)} (a Machine) next to our face-up Cyber Dragon opens us to their Chimeratech Fortress Dragon"
                : null;
        }

        private async Task<List<DynamicCard>> ChimeratechExposingNormalSummonsAsync(
            IEnumerable<DynamicCard> summons, List<DynamicCard> monsters)
        {
            var exposed = summons.Where(card => card.ID > 0 && SummonExposesChimeratech(card, monsters) != null).ToList();
            if (exposed.Any(card => card.HasType(CardType.Union)) && await UnionHostFreeAsync(monsters))
                exposed.RemoveAll(card => card.HasType(CardType.Union));
            return exposed;
        }

        private async Task<IdleCmdData> WithoutChimeratechSummonsAsync(IdleCmdData state)
        {
            var normals = state.SummonableCards ?? Array.Empty<DynamicCard>();
            var specials = state.SpecialSummonableCards ?? Array.Empty<DynamicCard>();
            if ((!normals.Any(IsMachine) && !specials.Any(card => card.IsCode(MachinaFortress))) ||
                !Rules.Roll(DeckRules.NoChimeratechExposure, out _) || !OpponentMayHaveChimeratech(out _))
                return state;
            var monsters = await GetBotMonsterCardsAsync();
            var exposed = await ChimeratechExposingNormalSummonsAsync(normals, monsters);
            if (exposed.Count > 0)
                state.SummonableCards = normals.Where(card => !exposed.Any(other => other.ID == card.ID)).ToArray();
            state.SpecialSummonableCards = specials.Where(card =>
                !card.IsCode(MachinaFortress) || SummonExposesChimeratech(card, monsters) == null).ToArray();
            return state;
        }

        /// <summary>
        /// Whether some face-up Machine of ours can still take a union (a monster holds one union at a time): a union we
        /// summon next to our Cyber Dragon can then equip itself away instead of staying a face-up Machine.
        /// </summary>
        protected async Task<bool> UnionHostFreeAsync(List<DynamicCard> botMonsters)
        {
            var equippedZones = (await CurrentField.GetCardsInSpellZone())
                .Where(card => card.ID > 0 && card.IsFaceup() && card.HasType(CardType.Union) && card.EquipTarget.Location == (byte)CardLocation.MonsterZone)
                .Select(card => (int)card.EquipTarget.Sequence).ToHashSet();
            return botMonsters.Any(card => card.ID > 0 && card.IsFaceup() && IsMachine(card) && !card.HasType(CardType.Union) &&
                                           !equippedZones.Contains(card.CurrentCoordinates.Sequence) &&
                                           (card.EquipCards ?? Array.Empty<CardCoordinate>()).Length == 0);
        }

        /// <summary>
        /// Whether their Extra Deck may hold Chimeratech Fortress Dragon (the deck-list read: lists their cards fit that
        /// run it), at least ChimeratechFearChance. Without list data: assumed (most decks play it). Logged when it doesn't.
        /// </summary>
        protected Task<bool> OpponentMayHaveChimeratechAsync<T>(DecisionContext<T> context)
        {
            if (OpponentMayHaveChimeratech(out double chance))
                return Task.FromResult(true);
            StapleNote(context, $"their deck likely has no Chimeratech Fortress Dragon ({chance:P0} from the deck lists): Cyber Dragon next to our Machines is safe");
            return Task.FromResult(false);
        }

        private bool OpponentMayHaveChimeratech(out double chance)
        {
            var read = ReadOpponentExtraDeckChance(ChimeratechFortressDragon);
            chance = read.GetValueOrDefault();
            return read == null || chance >= ChimeratechFearChance;
        }

        private Func<int, double?>? _searchOpponentExtraDeckChance;

        private double? ReadOpponentExtraDeckChance(int id)
        {
            if (_searchOpponentExtraDeckChance != null)
                return _searchOpponentExtraDeckChance(id);
            lock (OpponentDeck)
                return OpponentDeck.ExtraDeckChance(id);
        }

        /// <summary>Chance their Extra Deck holds Chimeratech Fortress Dragon from which we play around it. Tune from logs.</summary>
        [Tunable("Chance their Extra Deck holds Chimeratech Fortress Dragon from which we play around it. Tune from logs.")]
        public double ChimeratechFearChance { get; set; } = 0.25;

        /// <summary>The fusion materials we planned when deciding a fusion (matched by controller, location, zone and id).</summary>
        private (int Turn, List<(int Controller, int Location, int Sequence, int Id)> Cards, string What)? _fusionPlan;

        /// <summary>Remembers the materials for the fusion we're about to make (see <see cref="SelectPlannedMaterials"/>).</summary>
        protected void PlanFusionMaterials(int turn, IEnumerable<DynamicCard> materials, string what) =>
            _fusionPlan = (turn, materials.Select(card => ((int)card.CurrentCoordinates.Controller, (int)card.CurrentCoordinates.Location,
                (int)card.CurrentCoordinates.Sequence, card.ID)).ToList(), what);

        /// <summary>
        /// A fusion material prompt: the planned materials still on offer, or an empty list to finish once the minimum is
        /// met. Null when there's no plan. A select/unselect prompt (<paramref name="onePerPrompt"/>) takes ONE card per
        /// answer (Omega rejects several at once and the duel stalls; seen 2026-11-07 with Overload Fusion): the prompt
        /// repeats for the next one. Each card picked is ticked off the plan, so a picked card is never picked (unselected)
        /// again.
        /// </summary>
        protected IList<byte>? SelectPlannedMaterials<T>(DecisionContext<T> context, DynamicCard[] cards, int minimum, int maximum, int hint,
            bool onePerPrompt = false)
        {
            if (hint != (int)DHint.FMATERIAL || _fusionPlan is not { } plan || plan.Turn != context.Header.CurrentTurn)
                return null;
            bool Matches((int Controller, int Location, int Sequence, int Id) p, DynamicCard card) =>
                p.Location == card.CurrentCoordinates.Location && p.Sequence == card.CurrentCoordinates.Sequence &&
                (p.Controller == card.CurrentCoordinates.Controller || card.ID <= 0 || card.IsCode(p.Id)) &&
                (card.ID <= 0 || card.IsCode(p.Id));
            var picks = cards.Select((card, index) => (Card: card, Index: index))
                .Where(x => plan.Cards.Any(p => Matches(p, x.Card)))
                .Take(onePerPrompt ? 1 : Math.Max(1, maximum)).ToList();
            if (picks.Count == 0)
            {
                if (minimum <= 0)
                {
                    _fusionPlan = null;
                    return new List<byte>(); // done: finish the selection
                }
                // Forced to pick something we didn't plan: the cheapest on offer.
                var cheapest = cards.Select((card, index) => (Card: card, Index: index)).OrderBy(x => x.Card.Attack)
                    .Take(onePerPrompt ? 1 : minimum).ToList();
                StapleNote(context, $"{plan.What}: material not in the plan, forced: {string.Join(", ", cheapest.Select(x => DescribePick(x.Card)))}");
                return cheapest.Select(x => (byte)x.Index).ToList();
            }
            foreach (var pick in picks)
                plan.Cards.RemoveAll(p => Matches(p, pick.Card));
            if (plan.Cards.Count == 0 && !onePerPrompt)
                _fusionPlan = null;
            StapleNote(context, $"{plan.What}: material {string.Join(", ", picks.Select(x => DescribePick(x.Card)))}" +
                                (plan.Cards.Count > 0 ? $" ({plan.Cards.Count} more to pick)" : " (last one)"));
            return picks.Select(x => (byte)x.Index).ToList();
        }

        /// <summary>
        /// End of our turn with our face-up Cyber Dragon next to another face-up Machine: fuse Chimeratech Fortress Dragon
        /// ourselves (Cyber Dragon + our weakest face-up Machine, plus every face-up Machine of theirs, which it removes),
        /// so the opponent can't use ours. Only once no attack is still coming this turn.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryChimeratechSafetyAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, bool battleAhead)
        {
            if (battleAhead || !TryGetSpecialSummon(state, ChimeratechFortressDragon, out var chimeratech))
                return null;
            var botMonsters = await GetBotMonsterCardsAsync();
            if (ChimeratechExposure(botMonsters) is not string exposure)
                return null;
            if (!await OpponentMayHaveChimeratechAsync(context))
                return null;
            var faceUp = botMonsters.Where(card => card.ID > 0 && card.IsFaceup()).ToList();
            var cyber = faceUp.First(card => card.IsCode(CyberDragon));
            var weakest = faceUp.Where(card => IsMachine(card) && !SameZone(card, cyber)).OrderBy(card => card.Attack).First();
            var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && IsMachine(card)).ToList();
            if (!Rule(DeckRules.ChimeratechSafetyFuse))
                return null;
            var materials = new List<DynamicCard> { cyber, weakest };
            materials.AddRange(theirs);
            PlanFusionMaterials(context.Header.CurrentTurn, materials, "Chimeratech Fortress Dragon (safety fuse)");
            StapleNote(context, $"special summon Chimeratech Fortress Dragon ourselves: {exposure}; fuse Cyber Dragon + our weakest Machine " +
                                $"({CardNames.Get(weakest.ID)}){(theirs.Count > 0 ? $" + their {string.Join(", ", theirs.Select(card => CardNames.Get(card.ID)))}" : "")} " +
                                $"({materials.Count * 1000} ATK) so they can't");
            return chimeratech.SpecialSummon();
        }

        // ------------------------------------------------------------------ D.D. Crow

        // The opponent's graveyard card our D.D. Crow is banishing (their effect targets it): (Location, Sequence).
        private (int Turn, int Sequence, int Id)? _crowTarget;

        // Card text that targets a card in a graveyard (Monster Reborn, Call of the Haunted, Premature Burial...).
        private static readonly System.Text.RegularExpressions.Regex GraveyardTargetText = new(
            @"target[^.]{0,60}\b(in|from) (your|either player's|either|a) (GY|Graveyard)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// D.D. Crow in response to the opponent's card that targets a card in their graveyard: banish that target (their
        /// revive / recursion fizzles). The target as reported by the server, or (when none was reported) their best
        /// graveyard monster if the card being activated targets a graveyard card. Returns the chain index, or null.
        /// </summary>
        protected async Task<int?> TryChainCrowAsync(DecisionContext<SelectChainData> context, DynamicCard[] options)
        {
            int crow = Array.FindIndex(options, card => card.IsCode(DDCrow));
            if (crow >= 0 && context.Header.LastChainPlayer != 1 && await CrowAgainstDadAsync(context) is int dadTarget)
            {
                _crowTarget = (context.Header.CurrentTurn, dadTarget, ((await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>())[dadTarget]);
                return crow;
            }
            if (crow >= 0 && await CrowAgainstReviverAsync(context) is int reviverTarget)
            {
                _crowTarget = (context.Header.CurrentTurn, reviverTarget, ((await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>())[reviverTarget]);
                return crow;
            }
            if (crow < 0 || context.Header.LastChainPlayer != 1)
                return null;
            var enemyField = (await GetEnemyMonsterCardsAsync()).Concat(await GetEnemySpellCardsAsync()).Where(card => card.ID > 0).ToList();
            var theirGrave = (await CurrentField.GetBotField()).EnemySide.Grave ?? Array.Empty<int>();
            if (theirGrave.Length == 0)
                return null;

            // A reported target in a graveyard: their GY (Crow can only banish from the opponent's GY).
            foreach (var card in enemyField)
            {
                var targets = (card.CurrentChainTargets ?? Array.Empty<CardCoordinate>()).Concat(card.TargetCards ?? Array.Empty<CardCoordinate>());
                foreach (var t in targets.Where(t => t.Location == (byte)CardLocation.Grave && t.Sequence < theirGrave.Length))
                {
                    if (t.Controller == 0 && (await CurrentField.GetBotField()).OwnSide.Grave?.Length > t.Sequence)
                        continue; // our graveyard: Crow can't touch it
                    if (!Rule(DeckRules.DDCrowChain))
                        return null;
                    _crowTarget = (context.Header.CurrentTurn, t.Sequence, theirGrave[t.Sequence]);
                    StapleNote(context, $"chain D.D. Crow: {CardNames.Get(card.ID)} targets {CardNames.Get(theirGrave[t.Sequence])} in their graveyard: banish it");
                    return crow;
                }
            }

            // No target reported (Omega often doesn't): read what the card they just activated can target from its text
            // ("target 1 Level 4 or lower Reptile monster in your GY") and keep only the cards in their graveyard that fit.
            // The best of those is the likely target. Nothing fits (or the text can't be read): don't waste Crow.
            // The chain link as read from their field: a new face-up spell/trap first, else a monster whose effect fits now.
            var (activated, activatedHow) = await ActivatedNowAsync();
            foreach (var card in activated)
            {
                if (await CardDatabase.GetAsync(card.ID) is not DBCardInfo info || GraveyardTargetText.Match(info.CardText ?? "") is not { Success: true } clause)
                    continue;
                var fits = GraveyardTargetFilter(clause.Value);
                int best = -1, bestAtk = -1;
                var candidates = new List<string>();
                for (int i = 0; i < theirGrave.Length; i++)
                {
                    if (await CardDatabase.GetAsync(theirGrave[i]) is not DBCardInfo g || !fits(g))
                        continue;
                    candidates.Add(CardNames.Get(theirGrave[i]));
                    if (g.Attack > bestAtk)
                    {
                        best = i;
                        bestAtk = g.Attack;
                    }
                }
                if (best < 0)
                {
                    StapleNote(context, $"keep D.D. Crow: {CardNames.Get(card.ID)} targets a graveyard card (\"{clause.Value.Trim()}\"), but nothing in their graveyard fits");
                    return null;
                }
                if (!Rule(DeckRules.DDCrowChain))
                    return null;
                _crowTarget = (context.Header.CurrentTurn, best, theirGrave[best]);
                StapleNote(context, $"chain D.D. Crow: {CardNames.Get(card.ID)} targets \"{clause.Value.Trim()}\" (no target reported); in their graveyard " +
                                    $"that's {string.Join(", ", candidates)}: banish {CardNames.Get(theirGrave[best])}, the likely target " +
                                    $"(chain link read as {CardNames.Get(card.ID)}: {activatedHow}; new face-up S/T zones [{string.Join(",", _newFaceUpSpellZones)}])");
                return crow;
            }
            return null;
        }

        /// <summary>
        /// Round-robin 14a (user, 2026-11-13): Treeborn, Vayu, Plaguespreader and co. are answered by chaining Crow to their
        /// effects (above). Dark Armed Dragon is summoned without a chain, so the one way to stop it is to banish a DARK first:
        /// on their turn (Standby / Main Phase 1, nothing on the chain), with exactly three DARKs in their graveyard and DAD
        /// likely in their hand, banish one — a DARK with a graveyard effect of its own (Vayu, Plaguespreader, Necro Gardna)
        /// first. "Firing crow in advance is just worse than chaining it so don't do this too often": a roll on the DAD
        /// chance times CrowPreemptWeight, once per turn. Returns their graveyard index to banish, or null.
        /// </summary>
        private async Task<int?> CrowAgainstDadAsync(DecisionContext<SelectChainData> context)
        {
            var header = context.Header;
            if (IsOwnTurn(header) || (DuelPhase)header.CurrentPhase is not (DuelPhase.Standby or DuelPhase.Main1) || _crowPreemptTurn == header.CurrentTurn)
                return null;
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            var grave = enemy.Grave ?? Array.Empty<int>();
            var darks = new List<(int Index, DBCardInfo Info)>();
            for (int i = 0; i < grave.Length; i++)
                if (await CardDatabase.GetAsync(grave[i]) is DBCardInfo g && (g.Type & (int)CardType.Monster) != 0 && (g.Attribute & (int)CardAttribute.Dark) != 0)
                    darks.Add((i, g));
            if (darks.Count != 3)
                return null;
            double dad = OpponentDeck.HandChance(DarkArmedDragon, enemy.Hand?.Length ?? 0) ?? 0;
            _crowPreemptTurn = header.CurrentTurn;
            double go = dad * CrowPreemptWeight;
            double roll = RollAt;
            if (go <= 0 || roll >= go || !Rule(DeckRules.CrowStopsDad))
            {
                if (go > 0)
                    StapleNote(context, $"keep D.D. Crow: Dark Armed Dragon in their hand {dad:P0}, 3 DARKs in their graveyard; banish-first chance {go:P0}, rolled {roll:0.00}");
                return null;
            }
            var pick = darks.OrderByDescending(d => GraveyardEffectText.IsMatch(d.Info.CardText ?? "") ? 1 : 0).ThenByDescending(d => d.Info.Attack).First();
            StapleNote(context, $"D.D. Crow now: Dark Armed Dragon in their hand {dad:P0} and exactly 3 DARKs in their graveyard; banishing " +
                                $"{CardNames.Get(grave[pick.Index])} stops it (chance {go:P0}, rolled {roll:0.00})");
            return pick.Index;
        }

        /// <summary>
        /// Round-robin 14a, extended (user, 2026-11-13): Crow on a big monster in their graveyard that brings itself back (Machina
        /// Fortress, with a hand card to pay) when we have no set answer to it (Bottomless, D-Prison, Mirror Force, a set Ryko...).
        /// Either turn: also when their chain link is removal about to take our last answer (their MST on our Bottomless leaves
        /// nothing for the Fortress). Returns their graveyard index, or null.
        /// </summary>
        private async Task<int?> CrowAgainstReviverAsync(DecisionContext<SelectChainData> context)
        {
            var enemy = (await CurrentField.GetBotField()).EnemySide;
            if ((enemy.Hand?.Length ?? 0) == 0)
                return null;
            var grave = enemy.Grave ?? Array.Empty<int>();
            int pick = -1, pickAtk = 0;
            for (int i = 0; i < grave.Length; i++)
                if (await CardDatabase.GetAsync(grave[i]) is DBCardInfo g && (g.Type & (int)CardType.Monster) != 0 && g.Attack >= StrongMonsterAttack &&
                    SelfReviveText.IsMatch(g.CardText ?? "") && g.Attack > pickAtk)
                {
                    pick = i;
                    pickAtk = g.Attack;
                }
            if (pick < 0)
                return null;
            var (have, haveWhat) = await SetAnswersAsync();
            int lost = 0;
            if (context.Header.LastChainPlayer == 1)
                foreach (var card in (await ActivatedNowAsync()).Cards)
                    lost = Math.Max(lost, Math.Min(have, await SpellTrapRemovalReachAsync(card.ID)));
            if (have - lost > 0 || !Rule(DeckRules.CrowStopsReviver))
                return null;
            StapleNote(context, $"D.D. Crow on {CardNames.Get(grave[pick])} in their graveyard: it brings itself back ({pickAtk}) and " +
                                (have == 0 ? "we have no set answer to it" : $"their removal is taking our last answer ({haveWhat})"));
            return pick;
        }

        // A monster that special summons itself from the graveyard ("Special Summon this card (from your hand or GY)").
        private static readonly System.Text.RegularExpressions.Regex SelfReviveText = new(
            @"special summon this card[^.]*(from your (hand or )?(GY|Graveyard)|\(from your hand or (GY|Graveyard)\))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private int _crowPreemptTurn = -1;

        /// <summary>Weight of Crow's banish-first against DAD (chaining is better, so not too often). Tune from logs.</summary>
        [Tunable("Weight of Crow's banish-first against DAD (chaining is better, so not too often). Tune from logs.")]
        public double CrowPreemptWeight { get; set; } = 0.6;

        // A monster whose effect works from the graveyard ("... in your GY/Graveyard: ...").
        private static readonly System.Text.RegularExpressions.Regex GraveyardEffectText = new(
            @"(this card is in your (GY|Graveyard)|from your (GY|Graveyard)|banish this card from your (GY|Graveyard))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly (string Name, CardRace Race)[] RaceNames =
        {
            ("Beast-Warrior", CardRace.BeastWarrior), ("Winged Beast", CardRace.WingedBeast), ("Sea Serpent", CardRace.SeaSerpent),
            ("Divine-Beast", CardRace.DivineBeast), ("Spellcaster", CardRace.Spellcaster), ("Warrior", CardRace.Warrior),
            ("Fairy", CardRace.Fairy), ("Fiend", CardRace.Fiend), ("Zombie", CardRace.Zombie), ("Machine", CardRace.Machine),
            ("Aqua", CardRace.Aqua), ("Pyro", CardRace.Pyro), ("Rock", CardRace.Rock), ("Plant", CardRace.Plant),
            ("Insect", CardRace.Insect), ("Thunder", CardRace.Thunder), ("Dragon", CardRace.Dragon), ("Beast", CardRace.Beast),
            ("Dinosaur", CardRace.Dinosaur), ("Fish", CardRace.Fish), ("Reptile", CardRace.Reptile), ("Psychic", CardRace.Psychic), ("Wyrm", CardRace.Wyrm),
        };

        private static readonly (string Name, CardAttribute Attribute)[] AttributeNames =
        {
            ("EARTH", CardAttribute.Earth), ("WATER", CardAttribute.Water), ("FIRE", CardAttribute.Fire), ("WIND", CardAttribute.Wind),
            ("LIGHT", CardAttribute.Light), ("DARK", CardAttribute.Dark),
        };

        /// <summary>
        /// What a "target ... in your GY" clause allows: card names in quotes, monster vs spell/trap, Level N or lower /
        /// higher, a monster type (Reptile, Dragon...) and an Attribute (LIGHT, DARK...). Anything not stated is allowed.
        /// </summary>
        private static Func<DBCardInfo, bool> GraveyardTargetFilter(string clause)
        {
            var names = System.Text.RegularExpressions.Regex.Matches(clause, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            bool monster = System.Text.RegularExpressions.Regex.IsMatch(clause, @"\bmonsters?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool spell = System.Text.RegularExpressions.Regex.IsMatch(clause, @"\bspells?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool trap = System.Text.RegularExpressions.Regex.IsMatch(clause, @"\btraps?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var orLower = System.Text.RegularExpressions.Regex.Match(clause, @"Level (\d+) or lower", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var orHigher = System.Text.RegularExpressions.Regex.Match(clause, @"Level (\d+) or higher", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var races = RaceNames.Where(r => System.Text.RegularExpressions.Regex.IsMatch(clause, $@"\b{System.Text.RegularExpressions.Regex.Escape(r.Name)}\b"))
                .Select(r => r.Race).ToList();
            // "Beast" also matches inside "Beast-Warrior" / "Winged Beast": drop it when a longer name matched.
            if (races.Contains(CardRace.Beast) && (races.Contains(CardRace.BeastWarrior) || races.Contains(CardRace.WingedBeast)))
                races.Remove(CardRace.Beast);
            var attributes = AttributeNames.Where(a => System.Text.RegularExpressions.Regex.IsMatch(clause, $@"\b{a.Name}\b")).Select(a => a.Attribute).ToList();
            return card =>
            {
                if (names.Count > 0 && !names.Any(n => string.Equals(n, card.Name, StringComparison.OrdinalIgnoreCase)))
                    return false;
                bool isMonster = (card.Type & (int)CardType.Monster) != 0;
                bool isSpell = (card.Type & (int)CardType.Spell) != 0;
                bool isTrap = (card.Type & (int)CardType.Trap) != 0;
                if ((monster || spell || trap) && !(monster && isMonster || spell && isSpell || trap && isTrap))
                    return false;
                if (orLower.Success && (!isMonster || card.Level > int.Parse(orLower.Groups[1].Value)))
                    return false;
                if (orHigher.Success && (!isMonster || card.Level < int.Parse(orHigher.Groups[1].Value)))
                    return false;
                if (races.Count > 0 && !races.Any(r => (card.Race & (int)r) != 0))
                    return false;
                if (attributes.Count > 0 && !attributes.Any(a => (card.Attribute & (int)a) != 0))
                    return false;
                return true;
            };
        }

        /// <summary>
        /// A banish from their graveyard with no planned target (D.D. Crow fired by the search, say): a card with a graveyard effect
        /// first, then a Fairy (user, 2026-11-14: "for playing against fairies: getting the fairies out of my grave should be the
        /// priority when picking grave targets over non-fairy monsters (although cards with graveyard effects are still higher
        /// priority than that)"; 2026-11-17: D.D. Crow took Mirror Force over Soul of Purity and Light), then other monsters by
        /// ATK, spells/traps last. The lower-ranked cards are held, so the search's lines pay to pick them. Null if not that prompt.
        /// </summary>
        [Tunable("The deck-read chance (their deck type, or their list running Dark Armed Dragon) from which a graveyard fuel goal applies.")]
        public static readonly Tuned<double> FuelGoalDeckChance = new(0.5);
        [Tunable("Against a Fairy deck: Fairies in their graveyard up to which banishing one is a goal (a card wants 4 there).")]
        public static readonly Tuned<int> FairyFuelLimit = new(4);
        [Tunable("Against a Dark Armed Dragon deck: DARKs in their graveyard up to which banishing one is a goal (DAD wants 3).")]
        public static readonly Tuned<int> DarkFuelLimit = new(3);

        protected IList<byte>? GraveBanishPick(DecisionContext<SelectCardData> context)
        {
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if ((DHint)context.StateData.SelectionHint != DHint.REMOVE || cards.Length < 2 || context.StateData.Maximum != 1 ||
                !cards.All(card => card.CurrentCoordinates.Controller != 0 && card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.ID > 0))
                return null;
            // Fuel goals (user, 2026-11-17): against a Fairy deck, its Fairies while they have 4 or fewer in the graveyard (a card that wants
            // 4 there: keep them away from it); against a Dark Armed Dragon deck, its DARKs while they have 3 or fewer (DAD wants exactly 3).
            static bool IsMonster(int id) => (Search.SearchHost.Type(id) & (int)CardType.Monster) != 0;
            static bool IsFairy(int id) => IsMonster(id) && (Search.SearchHost.Race(id) & (long)CardRace.Fairy) != 0;
            static bool IsDark(int id) => IsMonster(id) && (Search.SearchHost.Attribute(id) & (int)CardAttribute.Dark) != 0;
            bool fairyGoal = OpponentDeck.TypeChance("Fairy") >= FuelGoalDeckChance && cards.Count(card => IsFairy(card.ID)) <= FairyFuelLimit;
            bool darkGoal = (OpponentDeck.RunsChance(DarkArmedDragon) ?? 0) >= FuelGoalDeckChance && cards.Count(card => IsDark(card.ID)) <= DarkFuelLimit;
            int Tier(int id)
            {
                string text = Search.SearchHost.Available ? Search.SearchHost.Cards.Get(id)?.Text ?? "" : "";
                if (EffectEvaluator.HasGraveyardEffect(text))
                    return 3;
                if (!IsMonster(id))
                    return 0;
                return fairyGoal && IsFairy(id) || darkGoal && IsDark(id) ? 2 : 1;
            }
            // Among cards that work from the graveyard, the one whose graveyard effects score most (EffectEvaluator.ScoreInGraveAsync; the
            // cards in their GY are already in the card cache, so this completes at once).
            var theirGrave = cards.Select(card => card.ID).ToList();
            double GraveScore(int id) => CardDatabase.TryGetCached(id, out _) ? EffectEvaluator.ScoreInGraveAsync(id, theirGrave).GetAwaiter().GetResult().Score : 0;
            var ranked = cards.Select((card, index) => (Card: card, Index: index, Tier: Tier(card.ID)))
                .OrderByDescending(x => x.Tier).ThenByDescending(x => x.Tier == 3 ? GraveScore(x.Card.ID) : 0).ThenByDescending(x => x.Card.Attack).ToList();
            var pick = ranked[0];
            if (!Rule(DeckRules.GraveBanishTarget))
                return null;
            foreach (var other in ranked.Where(x => x.Tier < pick.Tier).Select(x => x.Card.ID).Distinct())
                HoldPick(other, DeckRules.GraveBanishTarget);
            string why = pick.Tier switch { 3 => "it works from the graveyard", 2 => (IsFairy(pick.Card.ID) && fairyGoal ? "a Fairy: keeps them short of the Fairies their deck wants there" : "a DARK: keeps them short of Dark Armed Dragon's 3"), 1 => "their best monster there", _ => "nothing better on offer" };
            StapleNote(context, $"banish {CardNames.Get(pick.Card.ID)} from their graveyard: {why}");
            return new List<byte> { (byte)pick.Index };
        }

        /// <summary>D.D. Crow's target: the graveyard card we chained it for. Null if this isn't that prompt.</summary>
        protected IList<byte>? CrowTarget(DecisionContext<SelectCardData> context)
        {
            if (_crowTarget is not { } target || target.Turn != context.Header.CurrentTurn ||
                (DHint)context.StateData.SelectionHint is not (DHint.REMOVE or DHint.TARGET))
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int index = Array.FindIndex(cards, card => card.CurrentCoordinates.Controller != 0 &&
                card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.IsCode(target.Id));
            if (index < 0)
                index = Array.FindIndex(cards, card => card.CurrentCoordinates.Controller != 0 &&
                    card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.CurrentCoordinates.Sequence == target.Sequence);
            if (index < 0)
                return null;
            _crowTarget = null;
            StapleNote(context, $"D.D. Crow banishes {CardNames.Get(target.Id)}");
            return new List<byte> { (byte)index };
        }
    }
}
