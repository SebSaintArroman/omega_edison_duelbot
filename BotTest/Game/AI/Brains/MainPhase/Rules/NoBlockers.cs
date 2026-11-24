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
        /// RULE deck.no-blockers
        /// Anything else would just be thrown in front of an attack. No blockers: setting or summoning a monster only to survive a
        /// turn is never worth it (user, 2026-11-10: "if you're setting blockers to stay alive you've probably lost anyway").
        /// Keep the card for a play; every summon and set left is held.
        /// </summary>
        private void NoBlockers(SummonSituation s)
        {
            if (s.Summonable.Count + s.Settable.Count > 0)
                StapleNote(s.Context, "keep monsters in hand: nothing they can beat, and no blockers: keep them for a play");
            foreach (var card in s.Settable)
                Hold(card.ID, MainAction.SetMonster, DeckRules.NoBlockers);
            foreach (var card in s.Summonable)
                Hold(card.ID, MainAction.Summon, DeckRules.NoBlockers);
        }
    }
}
