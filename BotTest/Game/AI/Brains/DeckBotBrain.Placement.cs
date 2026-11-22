using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Brains
{
    public abstract partial class DeckBotBrain
    {
        public override Task<int> OnSelectPlaceAsync(DecisionContext<SelectPlaceData> context, CancellationToken ct = default)
        {
            // Mirror Omega's centre-out monster placement in the simulator.
            var state = context.StateData;
            if (state.Player == 0 && state.Location == (short)CardLocation.MonsterZone &&
                ServerZoneFillOrder.FirstOrDefault(zone => (state.Filter & (1 << zone)) != 0, -1) is int zone and >= 0)
                return Task.FromResult(BotActionUtilities.BuildZoneResponse(0, (byte)CardLocation.MonsterZone, (byte)zone));
            return base.OnSelectPlaceAsync(context, ct);
        }
    }
}
