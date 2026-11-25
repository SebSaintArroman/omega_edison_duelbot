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
        /// RULE deck.bounce-self-summoner
        /// Returning to the hand (Compulsory Evacuation Device, Brionac) a monster of theirs that Special Summons itself from the hand
        /// (Machina Fortress, XX-Saber Faultroll, Cyber Dragon) only sends it back for a turn, outside an attack it's stopping: a large
        /// hold on that target (user, 2026-11-25, Machina #109: Compulsory in their Draw Phase bounced Faultroll).
        /// </summary>
        private async Task<HashSet<int>> BouncedComesBackAsync(DecisionContext<SelectCardData> context)
        {
            var result = new HashSet<int>();
            if (context.StateData.SelectionHint != (int)DHint.RTOHAND || await CurrentField.IsBotUnderAttack())
                return result;
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            for (int i = 0; i < offered.Length; i++)
                if (offered[i].ID > 0 && offered[i].CurrentCoordinates is { Controller: 1, Location: (byte)CardLocation.MonsterZone } &&
                    await SummonsItselfFromHandAsync(offered[i].ID))
                    result.Add(i);
            return result;
        }

        /// <summary>The holds for this bounce pick (BouncedComesBackAsync): every line that picks one pays.</summary>
        private async Task HoldBounceSelfSummonersAsync(DecisionContext<SelectCardData> context)
        {
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            foreach (int i in await BouncedComesBackAsync(context))
                if (Rule(DeckRules.BounceSelfSummoner))
                    HoldPick(offered[i].ID, DeckRules.BounceSelfSummoner);
        }

        // "Special Summon this card from your hand or GY" (Fortress), "Must first be Special Summoned (from your hand)" (Faultroll),
        // "you can Special Summon this card (from your hand)" (Cyber Dragon).
        private static readonly System.Text.RegularExpressions.Regex SelfSummonFromHandText = new(
            @"(special summon this card|this card can be special summoned|must (first )?be special summoned)[^.]{0,60}?from (your|the) hand",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static async Task<bool> SummonsItselfFromHandAsync(int cardId) => await TextMatchesAsync(cardId, SelfSummonFromHandText);
    }
}
