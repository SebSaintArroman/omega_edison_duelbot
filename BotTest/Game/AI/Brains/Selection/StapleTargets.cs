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
    /// Targets for our staples' effects, as planned when they were activated (StapleTargets), and which spell/trap of theirs
    /// is worth removing.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// A spell/trap worth removing: set, or face-up and staying on the field (Continuous, Field, Equip). A face-up Normal
        /// or Quick-Play Spell or Normal / Counter Trap is only on the field while it resolves: removing it does nothing.
        /// </summary>
        protected static bool WorthRemovingSpellTrap(DynamicCard card) =>
            !card.IsFaceup() || card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip);

        /// <summary>MST target: the opponent's set cards first, then their face-up spells/traps; never our own.</summary>
        // Synchro materials picked in the current selection (StapleTargets): never picked twice. Cleared at each main decision.
        private readonly HashSet<(int, int, int)> _materialsPicked = new();

        protected IList<byte>? StapleTargets(DecisionContext<SelectCardData> context)
        {
            if (CrowTarget(context) is IList<byte> crow)
                return crow;
            if (GraveBanishPick(context) is IList<byte> banish)
                return banish;
            if (ReturnDimensionPick(context) is IList<byte> returned)
                return returned;
            // Synchro materials, when there's a choice: the cheapest by tribute cost (a spent Lyla, or one whose mill would deck us
            // out, goes first; user, 2026-11-12).
            if ((DHint)context.StateData.SelectionHint == DHint.SMATERIAL && context.StateData.SelectableCards is { Length: > 1 } materialCards &&
                materialCards.All(card => card.CurrentCoordinates.Controller == 0 && card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone))
            {
                // Select/unselect prompts come one card at a time, and picking a card already chosen unchooses it: never pick the
                // same card twice in one selection (a loop that hung a whole duel, 2026-11-12). Reset at the next main decision.
                int take = Math.Max(1, context.StateData.Minimum);
                static (int, int, int) Spot(DynamicCard card) => (card.CurrentCoordinates.Controller, card.CurrentCoordinates.Location, card.CurrentCoordinates.Sequence);
                var cheapest = Enumerable.Range(0, materialCards.Length).Where(i => !_materialsPicked.Contains(Spot(materialCards[i])))
                    .OrderBy(i => TributeCost(materialCards[i])).Take(take).ToList();
                if (cheapest.Count == take)
                {
                    foreach (int i in cheapest)
                        _materialsPicked.Add(Spot(materialCards[i]));
                    context.Bot.LogNote($"deck brain: synchro material {string.Join(" + ", cheapest.Select(i => $"{CardNames.Get(materialCards[i].ID)} (cost {TributeCost(materialCards[i]):0.#})"))}");
                    return cheapest.Select(i => (byte)i).ToList();
                }
            }
            // Brionac's discards and targets as planned (here, so every deck brain reaches them before its own discard / target
            // handling: round-robin 44a, Blackwing's discard handler took the prompt and discarded 1 of the planned 2).
            if (BrionacPick(context) is IList<byte> brionac)
                return brionac;
            if (PlannedTarget(context) is IList<byte> planned)
                return planned;
            // A tribute cost asked as a card selection (the release hint: Icarus Attack, Substitoad...): the planned tribute,
            // else the cheapest by TributeCost (seen 2026-11-11: Icarus fell to the wrapper default and tributed Armor Master over Vayu).
            if (SelectTributeCard(context) is IList<byte> tribute)
                return tribute;
            if (_lastStapleActivated != MysticalSpaceTyphoon)
                return null;
            var hint = (DHint)context.StateData.SelectionHint;
            if (hint is not (DHint.DESTROY or DHint.TARGET))
                return null;
            _lastStapleActivated = 0;
            int? wanted = _mstTargetZone;
            _mstTargetZone = null;
            var theirs = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                .Select((card, index) => (Card: card, Index: index))
                .Where(x => x.Card.CurrentCoordinates.Controller != 0)
                // A card that stays on the field first: a face-up Normal / Quick-Play Spell or Normal / Counter Trap is only
                // there while it resolves, so destroying it does nothing (seen 2026-11-10: our MST took their resolving MST).
                .OrderBy(x => WorthRemovingSpellTrap(x.Card) ? 0 : 1)
                // The card we chained MST at (a continuous card whose effect is resolving), if it's there.
                .ThenBy(x => wanted.HasValue && x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone &&
                              x.Card.CurrentCoordinates.Sequence == wanted.Value ? 0 : 1)
                .ThenBy(x => x.Card.IsFaceup() ? 1 : 0)
                .ThenByDescending(x => SetCardDanger(x.Card))
                .ToList();
            if (theirs.Count == 0)
                return null;
            var pick = theirs[0];
            context.Bot.LogNote($"deck brain: Mystical Space Typhoon target: {(pick.Card.IsFaceup() ? CardNames.Get(pick.Card.ID) : $"the set card in S{pick.Card.CurrentCoordinates.Sequence + 1} ({DescribeSetCardOdds(pick.Card)})")}");
            return new List<byte> { (byte)pick.Index };
        }
    }
}
