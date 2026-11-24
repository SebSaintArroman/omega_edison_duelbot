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
    /// The "use this effect?" prompt: costly optional effects are declined unless a rule wants them.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            if (context.StateData.EffectCard.IsCode(GorzTheEmissaryOfDarkness))
            {
                var why = await GorzNowAsync(context, await GetEnemyMonsterCardsAsync());
                if (why != null)
                    context.Bot.LogNote($"deck brain: {why}");
                return why != null;
            }
            // Our own optional effects are worth using by default (Cyber Phoenix's draw, a search, a revive): the default
            // brain declined every one. The exceptions are effects that cost us a lot: wiping our own board ("destroy all
            // cards", "send all") or paying life points. Deck brains decide those (and any card they know better).
            var card = context.StateData.EffectCard;
            if (!await GoodDiscardDeckSendAllowedAsync(card.ID, context.Header, context.Bot))
                return false;
            // Ryko's flip destroys a card on the field: with nothing of theirs there it would hit our own (seen 2026-11-09).
            if (card.IsCode(RykoLightswornHunter) && card.CurrentCoordinates.Controller == 0 &&
                (await GetEnemyMonsterCardsAsync()).Count + (await GetEnemySpellCardsAsync()).Count == 0)
            {
                context.Bot.LogNote("deck brain: don't use Ryko's flip: they have no card on the field (it would destroy our own)");
                return false;
            }
            if (card.ID > 0 && card.CurrentCoordinates.Controller == 0)
            {
                string text = await CardDatabase.GetAsync(card.ID) is DBCardInfo info ? info.CardText ?? "" : "";
                if (CostlyOptionalEffect.IsMatch(text))
                {
                    context.Bot.LogNote($"deck brain: don't use {CardNames.Get(card.ID)}'s optional effect: it costs us a lot (wipes our board or pays life points)");
                    return false;
                }
                context.Bot.LogNote($"deck brain: use {CardNames.Get(card.ID)}'s optional effect");
                return true;
            }
            return await base.OnSelectEffectYnAsync(context, ct);
        }

        // Optional effects that cost us a lot: our board wiped too, or life points paid.
        private static readonly System.Text.RegularExpressions.Regex CostlyOptionalEffect = new(
            @"destroy all cards|send all (other )?cards|destroy all monsters on the field|pay \d+ (life points|lp)|pay half",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
