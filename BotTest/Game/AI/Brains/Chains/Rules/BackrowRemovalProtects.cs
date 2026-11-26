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
    /// RULE deck.backrow-removal-protects: our spell/trap removal (MST, Dust Tornado, Heavy Storm) clears the way for our monsters, in our
    /// Main Phase 1 before a summon or an attack; anywhere else it's held (user, 2026-11-26: "spell and trap removal should be used to protect
    /// your monsters (before summoning big things/before attacking), not just randomly destroy things"; seen: MST at their set card in their
    /// Draw Phase). Not held: a doomed card (it's used anyway), a face-up card of theirs that matters (deck.mst-into-continuous), and Machina
    /// Jinzo's own Future Fusion in the Draw Phase.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private static readonly int[] BackrowRemoval = { MysticalSpaceTyphoon, DustTornado, HeavyStorm };

        /// <summary>A face-up spell/trap of theirs worth removing any time (FaceUpSpellMattersAsync), or Jinzo's own Future Fusion case.</summary>
        private async Task<bool> BackrowRemovalHasStandingUseAsync(DuelPhase phase)
        {
            if (SearchMstOwnFutureFusion && phase == DuelPhase.Draw)
                return true;
            foreach (var card in await GetEnemySpellCardsAsync())
                if (await FaceUpSpellMattersAsync(card) != null)
                    return true;
            return false;
        }

        /// <summary>A chain window: our removal offered outside our Main Phase 1 is held (unless doomed or it has a standing use).</summary>
        private async Task HoldBackrowRemovalOutsideMain1Async(DecisionContext<SelectChainData> context, DuelPhase phase)
        {
            if (IsOwnTurn(context.Header) && phase == DuelPhase.Main1)
                return;
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            bool? standing = null;
            for (int i = 0; i < offered.Length; i++)
            {
                if (offered[i].CurrentCoordinates.Controller != 0 || !offered[i].IsCode(BackrowRemoval) || await DoomedAsync(offered[i]))
                    continue;
                standing ??= await BackrowRemovalHasStandingUseAsync(phase);
                if (standing == true)
                    return;
                HoldKey(offered[i].ID, "chain:", DeckRules.BackrowRemovalProtects);
            }
        }

        /// <summary>
        /// Our main phase: our removal is held unless it's Main Phase 1 with a summon or a Battle Phase still to come (whatever the brain chose:
        /// its flow can pick a play before it reaches its removal rules).
        /// </summary>
        private async Task HoldBackrowRemovalOutsideMain1Async(DecisionContext<IdleCmdData> context)
        {
            var data = context.StateData;
            var phase = (DuelPhase)context.Header.CurrentPhase;
            bool clearsTheWay = phase == DuelPhase.Main1 &&
                (data.CanBattlePhase || (data.SummonableCards?.Length ?? 0) > 0 || (data.SpecialSummonableCards?.Length ?? 0) > 0);
            if (clearsTheWay)
                return;
            bool? standing = null;
            foreach (var card in data.ActivableCards ?? Array.Empty<DynamicCard>())
            {
                if (!card.IsCode(BackrowRemoval) || await DoomedAsync(card))
                    continue;
                standing ??= await BackrowRemovalHasStandingUseAsync(phase);
                if (standing == true)
                    return;
                Hold(card.ID, MainAction.Activate, DeckRules.BackrowRemovalProtects);
            }
        }
    }
}
