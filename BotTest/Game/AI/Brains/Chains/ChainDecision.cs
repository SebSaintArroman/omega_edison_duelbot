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
    /// Chain windows: whether to chain, and what. Our Goyo taking control first, then battle protection, then the staples,
    /// then the default scorer. RecordChainHoldsAsync records what the rules hold back so the search pays to break it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        public override Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default) =>
            OnSelectChainCoreAsync(context, ct);

        /// <summary>
        /// After the deck brain's chain answer (every deck, real and search twin: called from RefineChainAsync, since the deck brains
        /// override OnSelectChainAsync), the holds for this window, so a line that uses a held card pays for it: stat changes outside the damage step
        /// (StatBoostInDamageStep), Waboku on our own turn (WabokuOwnTurn), then the deck's own (RecordDeckChainHoldsAsync).
        /// </summary>
        private async Task RecordChainHoldsAsync(DecisionContext<SelectChainData> context, int answer)
        {
            _holds.RemoveAll(h => h.Prefix == "chain:"); // a chain window's holds are its own
            var phase = (DuelPhase)context.Header.CurrentPhase;
            await HoldEarlyStatChangesAsync(context, phase); // Chains/Rules/StatBoostInDamageStep.cs
            await HoldWabokuOnOwnTurnAsync(context, phase);  // Chains/Rules/WabokuOwnTurn.cs
            await HoldCompulsoryBeforeAttackAsync(context, answer); // Chains/Rules/CompulsoryWaitsForAttack.cs
            await RecordDeckChainHoldsAsync(context);
        }

        /// <summary>A deck's own holds for a chain window (its cards' effects offered there that its rules say not to use). Default: none.</summary>
        protected virtual Task RecordDeckChainHoldsAsync(DecisionContext<SelectChainData> context) => Task.CompletedTask;

        /// <summary>Our set card is about to be removed anyway: their chain targets it, or they just activated Heavy Storm.</summary>
        private async Task<bool> DoomedAsync(DynamicCard ours, BotStateHeader? header = null)
        {
            if (ours.CurrentCoordinates.Location != (byte)CardLocation.SpellZone)
                return false;
            var decision = header ?? _currentHeader;
            if (await TreatBackrowAsDoomedAsync(IsOwnTurn(decision), (DuelPhase)decision.CurrentPhase))
                return true;
            if (await OurSetCardTargetedAsync(ours))
                return true;
            return _newFaceUpSpellZones.Count > 0 && (await GetEnemySpellCardsAsync())
                .Any(card => card.IsCode(HeavyStorm) && _newFaceUpSpellZones.Contains(card.CurrentCoordinates.Sequence));
        }

        private async Task<int> OnSelectChainCoreAsync(DecisionContext<SelectChainData> context, CancellationToken ct)
        {
            // Our Goyo Guardian taking the monster it destroyed: always (seen 2026-11-10: Vayu passed on taking Card Trooper).
            if (OwnTakeControlTrigger(context) is int takeControl)
                return takeControl;
            await PrepareContextAsync(context, ct);
            RegisterChainDescriptions(context.StateData.Descriptions ?? Array.Empty<int>());
            // Shield Warrior / Waboku saving our battling monster (either turn).
            if (await TryChainBattleProtectionAsync(context, context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()) is int protect)
                return protect;
            if (!HandlesStapleChains)
            {
                var staple = await TryStapleChainAsync(context);
                if (staple.HasValue)
                    return staple.Value;
            }
            return await base.OnSelectChainAsync(context, ct);
        }

        // Our monsters whose trigger takes the monster they destroyed by battle (Goyo Guardian: official and Omega's pre-errata id).
        protected static readonly int[] TakeControlTriggerIds = { GoyoGuardian, GoyoGuardianPreErrata };

        // Monsters whose destruction by battle destroys the card that did it (Machina Fortress): an answer to Gorz.
        private static readonly int[] MachinaFortressAnswerIds = { MachinaFortress, MachinaFortressPreErrata };

        /// <summary>
        /// Our Goyo Guardian's trigger in a chain prompt (the engine offers it there, not as a yes/no): always use it, a free
        /// monster for us. The engine only offers it when the monster can come back (not, e.g., a Starlight Road Stardust).
        /// </summary>
        protected int? OwnTakeControlTrigger(DecisionContext<SelectChainData> context)
        {
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int index = Array.FindIndex(cards, card => card.CurrentCoordinates.Controller == 0 && card.IsCode(TakeControlTriggerIds) &&
                                                        card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
            if (index < 0)
                return null;
            context.Bot.LogNote($"deck brain: use {CardNames.Get(cards[index].ID)}'s effect: take the monster it destroyed");
            return index;
        }
    }
}
