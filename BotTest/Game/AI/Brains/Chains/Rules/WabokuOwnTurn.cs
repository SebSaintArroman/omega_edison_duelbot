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
    /// RULE deck.waboku-own-turn: Waboku in our own turn only for a Gladiator Beast kept alive to tag out (or when doomed).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// CSV S8, the legitimate Waboku in our own turn: our attack is under way with a monster that gains from battling (a Gladiator
        /// Beast: it tags out after), into an attack-position monster of theirs it can't beat - Waboku keeps it alive to tag out.
        /// </summary>
        private async Task<bool> WabokuLegitimateOwnTurnAsync(DuelPhase phase)
        {
            if (phase is not (DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle or DuelPhase.Damage or DuelPhase.DamageCal))
                return false;
            var ours = await CurrentField.GetBotBattlingMonster(isOwnSide: true);
            var theirs = await CurrentField.GetBotBattlingMonster(isOwnSide: false);
            return ours.ID > 0 && theirs.ID > 0 && BattleGain(ours) != null && theirs.IsAttack() && theirs.Attack >= ours.Attack &&
                   (await GetBotMonsterCardsAsync()).Any(m => m.ID == ours.ID && m.CurrentCoordinates.Sequence == ours.CurrentCoordinates.Sequence);
        }

        /// <summary>Holds Waboku on our own turn unless it's the legitimate case (WabokuLegitimateOwnTurnAsync) or it's
        /// doomed.</summary>
        private async Task HoldWabokuOnOwnTurnAsync(DecisionContext<SelectChainData> context, DuelPhase phase)
        {
            // Waboku in our own turn (S8): costs a line unless it's your legitimate case or doomed - the planner chooses, these are
            // conditions, not the brain's pick.
            if (IsOwnTurn(context.Header) && !await WabokuLegitimateOwnTurnAsync(phase))
            {
                var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
                for (int i = 0; i < offered.Length; i++)
                    if (offered[i].IsCode(Waboku) && offered[i].CurrentCoordinates.Controller == 0 && !await DoomedAsync(offered[i]))
                        HoldKey(offered[i].ID, "chain:", DeckRules.WabokuOwnTurn);
            }
        }
    }
}
