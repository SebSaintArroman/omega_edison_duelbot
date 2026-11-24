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
        /// RULE deck.summon-for-effect
        /// Round-robin 34b: a monster whose summon effect is worth a card (Armageddon Knight's DARK, Gearframe's search) is
        /// summoned for it, even if it doesn't beat their monster: the effect pays for it.
        /// </summary>
        private async Task<IdleCmdResponse?> SummonForEffectAsync(SummonSituation s)
        {
            var handNow = await GetBotHandCardsAsync();
            foreach (var candidate in s.Summonable.OrderBy(s.SafePriority))
            {
                if (await CardDatabase.GetAsync(candidate.ID) is not DBCardInfo info || !NormalSummonTriggerText.IsMatch(info.CardText ?? "") ||
                    await OnSummonEffectUselessAsync(candidate, handNow) || !Rule(DeckRules.SummonForEffect))
                    continue;
                StapleNote(s.Context, $"normal summon {CardNames.Get(candidate.ID)} for its summon effect (it pays for the card even if it doesn't beat their monsters)");
                return candidate.NormalSummon();
            }
            return null;
        }
    }
}
