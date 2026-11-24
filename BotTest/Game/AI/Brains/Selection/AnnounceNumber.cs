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
    /// The "declare a number" prompt.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Card Trooper asks how many cards to send (1-3): always the most. Milling fewer is just bad (less ATK, less
        /// graveyard fuel); if milling isn't wanted, it isn't activated at all.
        /// </summary>
        public override async Task<int> OnAnnounceNumberAsync(DecisionContext<AnnounceNumberData> context, CancellationToken ct = default)
        {
            var options = context.StateData.SelectableOptions ?? Array.Empty<int>();
            if (_lastStapleActivated == CardTrooper && options.Length > 0)
            {
                await PrepareContextAsync(context, ct);
                StapleNote(context, $"Card Trooper: send {options.Max()} cards");
                return options.Max();
            }
            return await base.OnAnnounceNumberAsync(context, ct);
        }
    }
}
