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
        /// RULE deck.keep-gorz-live
        /// Gorz in our hand with an empty field: don't set anything that would switch it off (Gorz needs us to control no
        /// cards). Removes every set from the options.
        /// </summary>
        private async Task<DecisionContext<IdleCmdData>> NoSetsWhileGorzIsLiveAsync(DecisionContext<IdleCmdData> context, List<DynamicCard> opponentMonsters)
        {
            if (!await KeepGorzLiveAsync(context, opponentMonsters))
                return context;
            var keepEmpty = context.StateData;
            foreach (var card in keepEmpty.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                Hold(card.ID, MainAction.SetMonster, DeckRules.KeepGorzLive);
            foreach (var card in keepEmpty.SpellSetableCards ?? Array.Empty<DynamicCard>())
                Hold(card.ID, MainAction.SetSpell, DeckRules.KeepGorzLive);
            keepEmpty.MonsterSetableCards = Array.Empty<DynamicCard>();
            keepEmpty.SpellSetableCards = Array.Empty<DynamicCard>();
            return context with { StateData = keepEmpty };
        }

        /// <summary>
        /// RULE deck.keep-gorz-live (summon side)
        /// Gorz in hand and an empty field: Gorz is the defence; a set card or a blocker would switch it off. True: keep every
        /// monster in hand (held).
        /// </summary>
        private async Task<bool> KeepMonstersInHandForGorzAsync(SummonSituation s)
        {
            if (!await KeepGorzLiveAsync(s.Context, s.EnemyMonsters) || !Rule(DeckRules.KeepGorzLive))
                return false;
            StapleNote(s.Context, "keep monsters in hand: the field is empty and Gorz is in hand, so it stays live");
            foreach (var card in s.Settable)
                Hold(card.ID, MainAction.SetMonster, DeckRules.KeepGorzLive);
            foreach (var card in s.Summonable)
                Hold(card.ID, MainAction.Summon, DeckRules.KeepGorzLive);
            return true;
        }
    }
}
