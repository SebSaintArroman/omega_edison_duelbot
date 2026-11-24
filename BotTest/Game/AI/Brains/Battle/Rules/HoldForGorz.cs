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
        /// RULE deck.hold-for-gorz (Battle Phase side; the Main Phase side keeps a defence-position monster from switching
        /// to attack, see MainPhase/Rules/RepositionToAttack.cs)
        /// Into a completely empty field, our direct attack is what lets their Gorz out: when Gorz is likely enough
        /// (FearGorzAsync, rolled once per turn), we leave the Battle Phase instead.
        /// </summary>
        private async Task<bool> HoldDirectAttackForGorzAsync(DecisionContext<BattleCmdData> context, DynamicCard attacker) =>
            await FearGorzAsync(context, attacker.Attack) && Rule(DeckRules.HoldForGorz);
    }
}
