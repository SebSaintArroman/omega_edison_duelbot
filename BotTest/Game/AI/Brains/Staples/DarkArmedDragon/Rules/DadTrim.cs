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
    /// RULE deck.dad-trim: Dark Armed Dragon in hand with more than 3 DARKs in the graveyard: trim it to exactly 3.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Dark Armed Dragon in hand but more than 3 DARKs in the graveyard: trim it to exactly 3 with banishing costs — Vayu's
        /// synchro (Vayu + a partner, -2), Plaguespreader leaving the graveyard (-1, its cost returns another hand card, never
        /// DAD), Chaos Sorcerer's cost (-1). The whole trim is planned first; only with a plan landing on 3 is the spend
        /// weighed (a spend justified as lethal commits an all-out attack: seen 2026-11-13, "stuck" yet "committed" every turn).
        /// </summary>
        protected async Task<IdleCmdResponse?> TryDadTrimAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, List<DynamicCard> hand)
        {
            if (!hand.Any(card => card.IsCode(DarkArmedDragon)) || TryGetSpecialSummon(state, DarkArmedDragon, out _))
                return null;
            int darks = await DarksInGraveAsync();
            if (darks <= 3 || !Rule(DeckRules.DadTrim))
                return null;
            var grave = await GetBotGraveyardCardsAsync();
            int vayuSteps = TryGetActivable(state, BlackwingVayuTheEmblemOfHonor, out _)
                ? Math.Min(grave.Count(c => c.IsCode(BlackwingVayuTheEmblemOfHonor)), grave.Count(c => c.IsCode(VayuPartnerIds)))
                : 0;
            int plagueSteps = TryGetActivable(state, PlaguespreaderZombie, out _) &&
                hand.Any(c => !c.IsCode(DarkArmedDragon)) &&
                await CanRevivePlaguespreaderAsync(context, state, Math.Max(0, hand.Count - 2)) ? 1 : 0;
            int chaosSteps = TryGetSpecialSummon(state, ChaosSorcerer, out _) ? 1 : 0;
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
                    fieldMaterials: null, removal: true, cardId: DarkArmedDragon) is not string dadPlan)
                return null;
            string steps = string.Join(" + ", new[] { pv > 0 ? $"Vayu x{pv} (-{2 * pv})" : null, pp > 0 ? "Plaguespreader (-1)" : null, pc > 0 ? "Chaos Sorcerer (-1)" : null }
                .Where(s => s != null));
            // The whole trim and the summon it's for as one routine: checked and scored on the engine, and followed through (no
            // graveyard trimmed for a DAD that then doesn't come).
            int turn = context.Header.CurrentTurn;
            var routine = Enumerable.Repeat(new RoutineStep(MainAction.Activate, BlackwingVayuTheEmblemOfHonor, "Vayu's synchro (trim 2 DARKs)"), pv)
                .Concat(Enumerable.Repeat(new RoutineStep(MainAction.Activate, PlaguespreaderZombie, "Plaguespreader from the graveyard (trim 1 DARK)",
                    OnTake: () => PlagueForDadTurn = turn), pp))
                .Concat(Enumerable.Repeat(new RoutineStep(MainAction.SpSummon, ChaosSorcerer, "Chaos Sorcerer (trim 1 DARK)"), pc))
                .Append(new RoutineStep(MainAction.SpSummon, DarkArmedDragon, "special summon Dark Armed Dragon (3 DARKs)"))
                .ToList();
            if (!await ContinueRoutineAsync(context, "Dark Armed Dragon trim", $"{darks} DARKs to 3 ({steps}) for {dadPlan}", routine[0], routine.Skip(1).ToList()))
                return null;
            if (pv > 0 && TryGetActivable(state, BlackwingVayuTheEmblemOfHonor, out var vayu))
            {
                StapleNote(context, $"banish Vayu + a Blackwing partner to trim the graveyard ({darks} -> {darks - 2} DARKs; plan: {steps} = 3) for {dadPlan}");
                return vayu.MainActivateFromIndex();
            }
            if (pp > 0 && TryGetActivable(state, PlaguespreaderZombie, out var plague))
            {
                StapleNote(context, $"Plaguespreader leaves the graveyard ({darks} -> {darks - 1} DARKs; plan: {steps} = 3) for {dadPlan}");
                PlagueForDadTurn = context.Header.CurrentTurn;
                return plague.MainActivateFromIndex();
            }
            if (pc > 0 && TryGetSpecialSummon(state, ChaosSorcerer, out var chaos))
            {
                StapleNote(context, $"Chaos Sorcerer: its cost banishes a DARK ({darks} -> {darks - 1} DARKs; plan: {steps} = 3) for {dadPlan}");
                return chaos.SpecialSummon();
            }
            return null;
        }
    }
}
