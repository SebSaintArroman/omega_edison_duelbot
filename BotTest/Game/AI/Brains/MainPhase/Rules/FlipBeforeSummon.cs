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
    /// RULE deck.flip-before-summon: flip our face-down monster before a summon that would be better made after it.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // A Special Summon from the Extra Deck by an activated effect (Vayu's graveyard synchro).
        private static readonly System.Text.RegularExpressions.Regex SummonsFromExtraText = new(
            @"special summon[^.]*from your extra deck",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Part of a decided summon: when the play we've chosen is a strong summon (2000+ ATK or an Extra Deck monster,
        /// or an effect that summons one from the Extra Deck, like Vayu's), they have set Spell/Traps and we have a
        /// face-down FLIP monster (Ryko), flip it first: Bottomless / Torrential trigger on the summon, so pop a set card
        /// before it. The play is committed, so the next decision follows through with it. Only for a summon we're
        /// actually making, never on spec.
        /// </summary>
        protected async Task<IdleCmdResponse> FlipBeforeSummonAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse response)
        {
            var state = context.StateData;
            if (_opponentSetSpellTraps == 0 || response.Action is not (MainAction.Summon or MainAction.SpSummon or MainAction.Activate))
                return response;
            var flip = (state.ReposableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => !card.IsFaceup() && HasFlipEffect(card));
            if (flip.ID <= 0)
                return response;
            var candidates = response.Action switch
            {
                MainAction.Summon => state.SummonableCards,
                MainAction.SpSummon => state.SpecialSummonableCards,
                _ => state.ActivableCards
            };
            var card = (candidates ?? Array.Empty<DynamicCard>()).FirstOrDefault(c =>
                c.ActionIndex != null && c.ActionIndex.Length > (int)response.Action && c.ActionIndex[(int)response.Action] == response.Index);
            if (card.ID <= 0 || await CardDatabase.GetAsync(card.ID) is not DBCardInfo info)
                return response;
            bool strong = response.Action == MainAction.Activate
                ? SummonsFromExtraText.IsMatch(info.CardText ?? "")
                : card.Attack >= StrongMonsterAttack || card.IsExtraCard();
            if (!strong || !Rule(DeckRules.FlipBeforeSummon))
                return response;
            if (!CommittedToBigPlay(context.Header, card.ID))
            {
                PendingBigPlayCards.Clear();
                PendingBigPlayCards.Add(card.ID);
                CommitToBigPlay(context.Header.CurrentTurn, keepExisting: true);
            }
            context.Bot.LogNote($"deck brain: before {(response.Action == MainAction.Activate ? $"{CardNames.Get(card.ID)}'s summon" : $"summoning {CardNames.Get(card.ID)}")}, " +
                                $"flip {CardNames.Get(flip.ID)} to destroy a set card: Bottomless / Torrential trigger on the summon");
            return flip.Reposition();
        }
    }
}
