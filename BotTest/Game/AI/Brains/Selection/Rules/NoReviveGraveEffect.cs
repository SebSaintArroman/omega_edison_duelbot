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
        /// RULE deck.no-revive-grave-effect
        /// Reviving one of our own monsters (Call of the Haunted, Premature Burial, Monster Reborn on our GY) with no plan for
        /// it: not one whose effect works from the graveyard (user, 2026-11-16: "a penalty for selecting a monster with a
        /// graveyard effect from it. that's just terrible play" - Call of the Haunted on Shield Warrior). Those are held, so a
        /// line that revives one pays for it, even when it's the only choice. Otherwise the biggest.
        /// </summary>
        private async Task<IList<byte>?> RevivePickAsync(DecisionContext<SelectCardData> context)
        {
            if (context.StateData.SelectionHint != (int)DHint.SPSUMMON || context.StateData.Maximum != 1 ||
                context.StateData.SelectableCards is not { Length: > 0 } revivable ||
                !revivable.All(card => card.CurrentCoordinates.Controller == 0 && card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.ID > 0))
                return null;
            var ranked = new List<(DynamicCard Card, int Index, bool GraveEffect)>();
            for (int i = 0; i < revivable.Length; i++)
                ranked.Add((revivable[i], i, await CardDatabase.GetAsync(revivable[i].ID) is DBCardInfo info && EffectEvaluator.HasGraveyardEffect(info.CardText ?? "")));
            var pick = ranked.OrderBy(x => x.GraveEffect).ThenByDescending(x => x.Card.Attack).First();
            if (Rule(DeckRules.NoReviveGraveEffect))
                foreach (var held in ranked.Where(x => x.GraveEffect).Select(x => x.Card.ID).Distinct())
                    HoldPick(held, DeckRules.NoReviveGraveEffect);
            context.Bot.LogNote($"deck brain: revive {CardNames.Get(pick.Card.ID)}" +
                                (pick.GraveEffect ? " (the only choice: its graveyard effect is lost; the search's lines pay for it)" : ""));
            return new List<byte> { (byte)pick.Index };
        }
    }
}
