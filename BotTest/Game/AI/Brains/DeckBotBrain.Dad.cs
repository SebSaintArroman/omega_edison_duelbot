using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Dark Armed Dragon as a shared staple (user, 2026-11-13: "allure rules can be moved to staples, along with DAD"): the
    /// graveyard trim to exactly 3 DARKs, and when its effect is worth a DARK. Its targets are in DeckBotBrain.Targeting;
    /// its summon stays with each deck (when the summon is worth it and what goes first is deck play).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected const int VayuId = 72714392;
        /// <summary>A live Vayu pair in our graveyard counts this share of the synchro it makes (its body: the synchro's effects are negated).
        /// Below 1: a goal stays below what it converts into (user, 2026-11-16).</summary>
        [Tunable("A live Vayu pair in our graveyard (Vayu + a non-Tuner Blackwing whose Levels make a Blackwing Synchro in our Extra Deck) counts this share of that synchro's body (its effects are negated). Below 1: a goal stays below what it converts into.")]
        internal static double VayuPairShare = 0.15; // user, 2026-11-17: weighted toward the cards, less toward the pairing (was 0.5)

        private readonly record struct CardBits(string Name, int Type, int Level, int Attack);

        // Our Extra Deck when the search started (the Vayu pairs' synchros).
        private List<int> _searchExtra = new();

        /// <summary>
        /// The Vayu pairs in our graveyard (user, 2026-11-17: "special graveyard scores for vayu pairings. they're an exception - they can be
        /// a specific adjustment"): each Vayu with its own non-Tuner Blackwing partner there, whose Levels add up to a Blackwing Synchro still
        /// in our Extra Deck, is worth VayuPairShare of that synchro's body (base worth + ATK; Vayu negates its effects). Each Vayu, partner
        /// and synchro copy used once, best synchro first. A Vayu with no pair is the deck's own lone-Vayu value (SearchCardBonus).
        /// </summary>
        internal double VayuPairsValue(DuelSim.Engine.SideSummary own)
        {
            if (!Search.SearchHost.Available)
                return 0;
            static CardBits? Card(int code) => Search.SearchHost.Cards.Get(code) is { } r ? new CardBits(r.Name ?? "", r.Type, r.Level, r.Attack) : null;
            int vayus = own.GraveCodes.Count(code => Search.SearchHost.Is(code, VayuId));
            if (vayus == 0)
                return 0;
            var partners = own.GraveCodes.Where(code => !Search.SearchHost.Is(code, VayuId))
                .Select(Card).Where(c => c is { } p && p.Name.StartsWith("Blackwing") && (p.Type & 0x1) != 0 && (p.Type & 0x1000) == 0)
                .Select(c => c!.Value.Level).ToList();
            var synchros = _searchExtra.Select(Card).Where(c => c is { } s && s.Name.StartsWith("Blackwing") && (s.Type & 0x2000) != 0)
                .Select(c => c!.Value).ToList();
            double value = 0;
            for (int v = 0; v < vayus; v++)
            {
                // The best synchro any remaining partner makes with this Vayu (Level 1 + the partner's Level).
                var best = partners.Distinct()
                    .SelectMany(level => synchros.Where(s => s.Level == 1 + level).Select(s => (Level: level, Synchro: s)))
                    .OrderByDescending(x => x.Synchro.Attack).FirstOrDefault();
                if (best.Synchro.Name == null)
                    break;
                partners.Remove(best.Level);
                synchros.Remove(best.Synchro);
                value += VayuPairShare * (Search.MctsEvaluator.MonsterBase + Search.MctsEvaluator.AtkValue(best.Synchro.Attack));
            }
            return value;
        }
        protected const int PlaguespreaderId = 33420078;
        protected const int ChaosSorcererId = 9596126;
        protected const int NecroGardnaDadId = 4906301;

        /// <summary>The non-Tuners a Vayu in our graveyard pairs with for its synchro (the deck's Blackwings). Default: none.</summary>
        protected virtual int[] VayuPartnerIds => Array.Empty<int>();

        /// <summary>The turn Plaguespreader left the graveyard to trim the DARK count for DAD (its summon follows).</summary>
        protected int PlagueForDadTurn { get; set; } = -1;

        /// <summary>Mirror Force chance from which DAD clears a set card before attacking, even at the cost of a graveyard piece.</summary>
        [Tunable("Mirror Force chance from which DAD clears a set card before attacking, even at the cost of a graveyard piece.")]
        public double DadClearBackrowMirrorChance { get; set; } = 0.1;

        protected async Task<int> DarksInGraveAsync() =>
            (await GetBotGraveyardCardsAsync()).Count(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0);

        protected async Task<bool> DadInHandAsync() =>
            (await GetBotHandCardsAsync()).Any(card => card.IsCode(DarkArmedDragonId));

        /// <summary>
        /// Dark Armed Dragon in hand but more than 3 DARKs in the graveyard: trim it to exactly 3 with banishing costs — Vayu's
        /// synchro (Vayu + a partner, -2), Plaguespreader leaving the graveyard (-1, its cost returns another hand card, never
        /// DAD), Chaos Sorcerer's cost (-1). The whole trim is planned first; only with a plan landing on 3 is the spend
        /// weighed (a spend justified as lethal commits an all-out attack: seen 2026-11-13, "stuck" yet "committed" every turn).
        /// </summary>
        protected async Task<IdleCmdResponse?> TryDadTrimAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            if (!hand.Any(card => card.IsCode(DarkArmedDragonId)) || TryGetSpecialSummon(state, DarkArmedDragonId, out _))
                return null;
            int darks = await DarksInGraveAsync();
            if (darks <= 3 || !Rule(DeckRules.DadTrim))
                return null;
            var grave = await GetBotGraveyardCardsAsync();
            int vayuSteps = TryGetActivable(state, VayuId, out _)
                ? Math.Min(grave.Count(c => c.IsCode(VayuId)), grave.Count(c => c.IsCode(VayuPartnerIds)))
                : 0;
            int plagueSteps = TryGetActivable(state, PlaguespreaderId, out _) && hand.Any(c => !c.IsCode(DarkArmedDragonId)) ? 1 : 0;
            int chaosSteps = TryGetSpecialSummon(state, ChaosSorcererId, out _) ? 1 : 0;
            int need = darks - 3;
            (int V, int P, int C)? plan = null;
            for (int v = 0; v <= vayuSteps && plan == null; v++)
                for (int p = 0; p <= plagueSteps && plan == null; p++)
                    for (int c = 0; c <= chaosSteps && plan == null; c++)
                        if (2 * v + p + c == need)
                            plan = (v, p, c);
            if (plan is not (int pv, int pp, int pc))
            {
                StapleNote(context, $"Dark Armed Dragon is stuck: {darks} DARKs in the graveyard and no set of trims lands exactly on 3 " +
                                    $"(Vayu pairs {vayuSteps} x -2, Plaguespreader {plagueSteps} x -1, Chaos Sorcerer {chaosSteps} x -1)");
                return null;
            }
            if (await JustifySpendAsync(context, "Dark Armed Dragon (once the graveyard is trimmed to 3 DARKs)", newAtk: 2800,
                    fieldMaterials: null, removal: true, cardId: DarkArmedDragonId) is not string dadPlan)
                return null;
            string steps = string.Join(" + ", new[] { pv > 0 ? $"Vayu x{pv} (-{2 * pv})" : null, pp > 0 ? "Plaguespreader (-1)" : null, pc > 0 ? "Chaos Sorcerer (-1)" : null }
                .Where(s => s != null));
            // The whole trim and the summon it's for as one routine: checked and scored on the engine, and followed through (no
            // graveyard trimmed for a DAD that then doesn't come).
            int turn = context.Header.CurrentTurn;
            var routine = Enumerable.Repeat(new RoutineStep(MainAction.Activate, VayuId, "Vayu's synchro (trim 2 DARKs)"), pv)
                .Concat(Enumerable.Repeat(new RoutineStep(MainAction.Activate, PlaguespreaderId, "Plaguespreader from the graveyard (trim 1 DARK)",
                    OnTake: () => PlagueForDadTurn = turn), pp))
                .Concat(Enumerable.Repeat(new RoutineStep(MainAction.SpSummon, ChaosSorcererId, "Chaos Sorcerer (trim 1 DARK)"), pc))
                .Append(new RoutineStep(MainAction.SpSummon, DarkArmedDragonId, "special summon Dark Armed Dragon (3 DARKs)"))
                .ToList();
            if (!await ContinueRoutineAsync(context, "Dark Armed Dragon trim", $"{darks} DARKs to 3 ({steps}) for {dadPlan}", routine[0], routine.Skip(1).ToList()))
                return null;
            if (pv > 0 && TryGetActivable(state, VayuId, out var vayu))
            {
                StapleNote(context, $"banish Vayu + a Blackwing partner to trim the graveyard ({darks} -> {darks - 2} DARKs; plan: {steps} = 3) for {dadPlan}");
                return vayu.MainActivateFromIndex();
            }
            if (pp > 0 && TryGetActivable(state, PlaguespreaderId, out var plague))
            {
                StapleNote(context, $"Plaguespreader leaves the graveyard ({darks} -> {darks - 1} DARKs; plan: {steps} = 3) for {dadPlan}");
                PlagueForDadTurn = context.Header.CurrentTurn;
                return plague.MainActivateFromIndex();
            }
            if (pc > 0 && TryGetSpecialSummon(state, ChaosSorcererId, out var chaos))
            {
                StapleNote(context, $"Chaos Sorcerer: its cost banishes a DARK ({darks} -> {darks - 1} DARKs; plan: {steps} = 3) for {dadPlan}");
                return chaos.SpecialSummon();
            }
            return null;
        }

        /// <summary>
        /// Whether Dark Armed Dragon's effect (banish a DARK from the graveyard: destroy a card) is worth it now. Free while the
        /// graveyard has DARKs with no graveyard effect of their own; with only play pieces left (Vayu and the partners it needs,
        /// Plaguespreader, Necro Gardna), only to clear a likely Mirror Force before DAD attacks, on a big threat, or when holding
        /// back loses. Never while another DAD in hand needs the 3 DARKs. Returns the reason, or null (held).
        /// </summary>
        /// <summary>
        /// DARKs in our graveyard Dark Armed Dragon can banish at no real cost (not Vayu and the partners it needs,
        /// Plaguespreader, Necro Gardna): the same count DadEffectWhyAsync uses, without its notes.
        /// </summary>
        protected async Task<int> DadSpareDarksAsync()
        {
            var grave = await GetBotGraveyardCardsAsync();
            var darks = grave.Where(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0).ToList();
            int vayus = darks.Count(card => card.IsCode(VayuId));
            int keptPartners = Math.Min(grave.Count(card => card.IsCode(VayuPartnerIds)), vayus);
            int playPieces = vayus + keptPartners + darks.Count(card => card.IsCode(PlaguespreaderId, NecroGardnaDadId));
            return Math.Max(0, darks.Count - playPieces);
        }

        /// <summary>Why destroying one of their monsters with Dark Armed Dragon makes our board lethal this turn, or null.</summary>
        private async Task<string?> DadMakesLethalAsync(List<DynamicCard> enemyMonsters, int theirSpells)
        {
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (BoardAssessment.TurnsToWin(attackers, BoardAssessment.Defenders(enemyMonsters, OpponentMemory), oppLp) == 1)
                return null; // already lethal: no need
            foreach (var victim in enemyMonsters)
            {
                var rest = enemyMonsters.Where(card => !SameZone(card, victim)).ToList();
                if (BoardAssessment.TurnsToWin(attackers, BoardAssessment.Defenders(rest, OpponentMemory), oppLp) != 1)
                    continue;
                if (rest.Count == 0 && theirSpells == 0 && _knownGorzInOpponentHand && attackers.All(atk => atk < oppLp))
                    return null; // their field empty: the known Gorz stops the push
                return $"destroying their {DescribeTarget(victim)} makes our board lethal this turn ({string.Join(" + ", attackers)} into {oppLp} LP): " +
                       "worth a graveyard play piece";
            }
            return null;
        }

        protected async Task<string?> DadEffectWhyAsync<T>(DecisionContext<T> context)
        {
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var enemySpells = await GetEnemySpellCardsAsync();
            if (enemyMonsters.Count == 0 && enemySpells.Count == 0)
                return null;
            var grave = await GetBotGraveyardCardsAsync();
            var darks = grave.Where(card => card.HasType(CardType.Monster) && (card.Attribute & (int)CardAttribute.Dark) != 0).ToList();
            if (darks.Count == 0)
                return null;
            int vayus = darks.Count(card => card.IsCode(VayuId));
            int partners = grave.Count(card => card.IsCode(VayuPartnerIds));
            int keptPartners = Math.Min(partners, vayus);
            int playPieces = vayus + keptPartners + darks.Count(card => card.IsCode(PlaguespreaderId, NecroGardnaDadId));
            int spare = darks.Count - playPieces;
            if (await DadInHandAsync() && darks.Count <= 3)
            {
                StapleNote(context, "hold Dark Armed Dragon's effect: another Dark Armed Dragon in hand needs the 3 DARKs in the graveyard");
                Hold(DarkArmedDragonId, MainAction.Activate, DeckRules.DadEffect);
                return null;
            }
            string targets = $"{enemyMonsters.Count} monster(s), {enemySpells.Count} spell/trap(s)";
            if (spare > 0)
                return $"Dark Armed Dragon is on the field and the graveyard has {spare} DARK(s) with no graveyard effect to banish: " +
                       $"destroying one of their cards ({targets}) costs nothing that matters (repeatable)";
            bool attackingSoon = IsOwnTurn(context.Header) && (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1;
            int setCards = enemySpells.Count(card => !card.IsFaceup());
            if (attackingSoon && setCards > 0 && MirrorForceChance >= DadClearBackrowMirrorChance)
                return $"we attack with Dark Armed Dragon this turn and they have {setCards} set card(s) (Mirror Force chance {MirrorForceChance:P0}): " +
                       "clear one first, even though the cost is a graveyard piece";
            // Destroying their blocker makes our board lethal this turn: worth a graveyard piece (user, 2026-11-13: "banishing
            // gardna for dad isn't something you should really do, [but] in this case you would have lethal"). Not into a
            // known Gorz: with their field emptied, the first direct hit brings it instead of the win.
            if (attackingSoon && enemyMonsters.Count > 0 && await DadMakesLethalAsync(enemyMonsters, enemySpells.Count) is string lethalWhy)
                return lethalWhy;
            foreach (var enemy in enemyMonsters.Where(card => card.IsFaceup()))
                if (await IsBigThreatAsync(enemy))
                    return $"destroy their {CardNames.Get(enemy.ID)} (a big threat), even though the cost is a graveyard play piece";
            if (await WeLoseNextTurnAsync())
                return "holding back loses: destroy one of their cards even though the cost is a graveyard play piece";
            if (!Rule(DeckRules.HoldDadEffectForPieces))
                return "rule skipped: destroy one of their cards even though the cost is a graveyard play piece";
            StapleNote(context, $"hold Dark Armed Dragon's effect: the only DARKs left to banish have graveyard effects (Vayu pieces, " +
                                $"Plaguespreader, Necro Gardna), and nothing of theirs ({targets}) is worth one");
            Hold(DarkArmedDragonId, MainAction.Activate, DeckRules.HoldDadEffectForPieces);
            return null;
        }
    }
}
