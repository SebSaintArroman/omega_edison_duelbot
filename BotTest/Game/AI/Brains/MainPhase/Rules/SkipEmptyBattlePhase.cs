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
        /// RULE deck.skip-empty-battle-phase
        /// Nothing can attack, or no attacker has a worthwhile attack (BattleWorthEnteringAsync): don't enter the Battle Phase.
        /// The default scorer then decides without it. (Skipping isn't logged: user, 2026-11-12.)
        /// </summary>
        private async Task<IdleCmdResponse?> SkipEmptyBattlePhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            if (!context.StateData.CanBattlePhase || !context.StateData.CanEndPhase)
                return null;
            var ownMonsters = await GetBotMonsterCardsAsync();
            bool hasAttacker = ownMonsters.Any(card => card.IsFaceup() && card.IsAttack());
            string? noAttack = !hasAttacker ? "no face-up attack-position monster"
                : await BattleWorthEnteringAsync() is string why ? why : null;
            if (noAttack == null || !Rule(DeckRules.SkipEmptyBattlePhase))
                return null;
            var noBattle = context.StateData;
            noBattle.CanBattlePhase = false;
            return await base.OnIdleCmdAsync(context with { StateData = noBattle }, ct);
        }
    }
}
