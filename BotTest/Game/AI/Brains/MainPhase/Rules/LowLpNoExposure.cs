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
        /// RULE deck.low-lp-no-exposure (summon side)
        /// At low LP a monster that would be a lethal hole in attack position isn't summoned (round-robin 3; 2026-11-13: Substitoad
        /// summoned at 100 LP "because 100 beats their strongest attacker, 0", and an 800 attacker killed us through it).
        /// Takes those monsters out of the summon options.
        /// </summary>
        private void NoSummonIntoLowLpExposure(SummonSituation s, (int MaxAttack, string Why)? exposure)
        {
            bool Exposed(DynamicCard card) => exposure is { } limit && EffectiveAttack(card) <= limit.MaxAttack;
            if (exposure is not { } shown || !s.Summonable.Any(Exposed) || !Rule(DeckRules.LowLpNoExposure))
                return;
            StapleNote(s.Context, $"don't summon {string.Join(", ", s.Summonable.Where(Exposed).Select(card => CardNames.Get(card.ID)))} in attack position: {shown.Why}");
            foreach (var card in s.Summonable.Where(Exposed))
                Hold(card.ID, MainAction.Summon, DeckRules.LowLpNoExposure);
            s.Summonable = s.Summonable.Where(card => !Exposed(card)).ToList();
        }
    }
}
