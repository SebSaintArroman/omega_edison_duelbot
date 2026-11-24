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
    /// Their hand as far as we know it, and their set cards we could pin down.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Pinned set cards already reported in the log.
        private readonly HashSet<(bool Monster, int Zone, int Id)> _pinnedLogged = new();

        /// <summary>
        /// The cards we know are in their hand: revealed ones (Trap Dustshoot, ...), and a Grand Mole-type monster seen going
        /// back to it. The search plays their turn with these only (user, 2026-11-11: "don't guess at unknowns").
        /// </summary>
        private List<int> KnownEnemyHand()
        {
            List<int> known;
            lock (OpponentDeck)
                known = OpponentDeck.KnownHand();
            if (_bouncerInTheirHand && _bouncerInHandId > 0 && !known.Contains(OpponentDeckPredictor.Normalize(_bouncerInHandId)))
                known.Add(_bouncerInHandId);
            return known;
        }
    }
}
