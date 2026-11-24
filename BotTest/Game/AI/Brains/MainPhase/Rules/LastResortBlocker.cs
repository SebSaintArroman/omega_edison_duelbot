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
        /// RULE deck.last-resort-blocker
        /// Round-robin 5 / 14b (user, 2026-11-13: "a last resort behaviour"): their board (and hand) is likely lethal next turn
        /// (LastResortLethalChance) and a blocker makes it much less likely (LastResortBlockerDrop): set the best one. D.D. Crow
        /// only when there's no other monster ("any other monster would be a better set").
        /// </summary>
        private async Task<IdleCmdResponse?> LastResortBlockerAsync(SummonSituation s)
        {
            if (s.Settable.Count == 0 && _neverSummonSet.ID <= 0)
                return null;
            var (lethal, withBlocker, lethalWhy) = await OpponentLethalRiskAsync(s.EnemyMonsters);
            if (lethal < LastResortLethalChance || withBlocker > lethal - LastResortBlockerDrop || !Rule(DeckRules.LastResortBlocker))
                return null;
            var blocker = s.Settable.OrderByDescending(card => card.Defense).FirstOrDefault();
            if (blocker.ID <= 0)
                blocker = _neverSummonSet;
            StapleNote(s.Context, $"last resort: set {CardNames.Get(blocker.ID)} as a blocker: their lethal chance {lethal:P0}, {withBlocker:P0} with a blocker ({lethalWhy})");
            return blocker.SetMonster();
        }
    }
}
