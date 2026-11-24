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
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// RULE deck.hold-sets-for-storm
        /// Each extra set card is one more their Heavy Storm would take (HoldSetForHeavyStormAsync). Cards a Storm doesn't
        /// cost us (Jar of Greed, Trap Dustshoot: IsStormFree) can still be set. Removes the other spell/trap sets.
        /// </summary>
        private async Task<DecisionContext<IdleCmdData>> HoldSetsForStormAsync(DecisionContext<IdleCmdData> context, bool alreadyNoted)
        {
            if (!(context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Any(card => card.ID > 0 && !IsStormFree(card.ID)) ||
                await HoldSetForHeavyStormAsync(context.Header.CurrentTurn) is not string stormHold || !Rule(DeckRules.HoldSetsForStorm))
                return context;
            if (!alreadyNoted)
                context.Bot.LogNote($"deck brain: don't set more spells/traps: {stormHold}");
            foreach (var card in (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>()).Where(card => card.ID > 0 && !IsStormFree(card.ID)))
                Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForStorm);
            var noSets = context.StateData;
            noSets.SpellSetableCards = (noSets.SpellSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => IsStormFree(card.ID)).ToArray();
            return context with { StateData = noSets };
        }

        /// <summary>
        /// Set cards a Heavy Storm doesn't really cost us: they can be used in response (Jar of Greed, Legacy of
        /// Yata-Garasu) or go off straight away (Trap Dustshoot). Not MST: chained to Heavy Storm it only hits a card the Storm destroys anyway. Deck brains add their own (quick-plays, ...).
        /// </summary>
        protected virtual IReadOnlyCollection<int> StormProofSets { get; } =
            new[] { JarOfGreed, LegacyOfYataGarasu, TrapDustshoot };

        /// <summary>
        /// Set cards that don't count towards Heavy Storm exposure or the set cap: storm-proof sets, and bait cards
        /// (Jar of Greed, MST, Burial...), which are set to be removed (the user's rule, 2026-11-06).
        /// </summary>
        protected bool IsStormFree(int cardId) => StormProofSets.Contains(cardId) || BaitCardIds.Contains(cardId);


        /// <summary>
        /// How fast the urge to hold grows with what we already have exposed: the k-th extra set is held with chance
        /// 1 - (1 - storm chance)^(weight * k^2). With a 12% Heavy Storm chance and weight 2: the 2nd set ~23%, the 3rd
        /// ~64%, the 4th ~90%. Nobody should set four into a possible Heavy Storm. Tune from logs.
        /// </summary>
        [Tunable("How fast the urge to hold grows with what we already have exposed: the k-th extra set is held with chance 1 - (1 - storm chance)^(weight * k^2). With a 12% Heavy Storm chance and weight 2: the 2nd set ~23%, the 3rd ~64%, the 4th ~90%. Nobody should set four into a possible Heavy Storm. Tune from logs.")]
        public double StormExposureWeight { get; set; } = 2;

        /// <summary>
        /// Each card we already have set is one more Heavy Storm would take with it. The first set card is always fine,
        /// and cards Heavy Storm doesn't cost us (StormProofSets) are always fine and don't count. Each extra one is held
        /// back with a chance that grows quickly with how many we already have exposed. Returns null to set, or the
        /// reason to hold.
        /// </summary>
        protected async Task<string?> HoldSetForHeavyStormAsync(int turn, int cardId = 0)
        {
            if (cardId > 0 && IsStormFree(cardId))
                return null;
            int ourSet = (await CurrentField.GetCardsInSpellZone())
                .Count(card => card.ID > 0 && !card.IsFaceup() && !IsStormFree(card.ID));
            if (ourSet == 0)
                return null;
            // One roll per extra set per turn (the main phase asks repeatedly; re-rolling would always set eventually).
            var key = (turn, ourSet);
            if (_stormHoldRolls.TryGetValue(key, out var cached))
                return cached;
            double chance = await OpponentHeavyStormChanceAsync();
            string? hold = null;
            if (chance > 0)
            {
                bool wait = TheyCanWait;
                // Early in the duel more careful with exposure, later less (user, 2026-11-13: "maybe adjust the risk tolerance
                // for heavy storm as the game goes on").
                var (riskTol, riskWhy) = await RiskToleranceAsync();
                double weight = StormExposureWeight * (wait ? UnpressuredExposureFactor : 1) * (RiskHoldCeiling - riskTol);
                double holdChance = 1 - Math.Pow(1 - chance, weight * ourSet * ourSet);
                double roll = RollAt;
                string text = $"we already have {ourSet} exposed set card(s); Heavy Storm chance {chance:P0} ({riskWhy})" +
                              $"{(_knownHeavyStormInOpponentHand ? " (seen in their hand)" : "")}" +
                              $"{(wait ? " and they're under no pressure, so they can wait for us to commit more" : "")}" +
                              $", so hold chance {holdChance:P0}, rolled {roll:0.00}";
                hold = roll < holdChance ? $"hold it in hand: {text}" : null;
            }
            if (_stormHoldRolls.Count > 64)
                _stormHoldRolls.Clear();
            _stormHoldRolls[key] = hold;
            return hold;
        }

        private readonly Dictionary<(int Turn, int Set), string?> _stormHoldRolls = new();
    }
}
