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
    /// Which brain this is (version, deck variant), which shared rules its deck brain plays itself, and whose turn it is.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>Deck brains with their own main-phase Heavy Storm / MST rules set this, so the shared ones don't run.</summary>
        protected virtual bool HandlesStapleIdle => false;
        private int _lastStapleActivated;

        /// <summary>Deck brains that do their own chain logic for these staples set this, so the shared rules don't run twice.</summary>
        /// <summary>
        /// The deck brain's version, bumped with every behaviour change (recorded per duel by DuelSim, so analyses can drop
        /// duels played by versions with bugs since fixed). 0: unversioned.
        /// </summary>
        public virtual int BrainVersion => 0;

        /// <summary>
        /// Which list of its deck this brain plays (set by the deck catalog; user, 2026-11-10: "all these decks will eventually
        /// have variants"). The rules mostly follow the cards seen; this is for what differs with the same cards (MachinaJinzo's
        /// Fortress discards a Jinzo more readily). Each deck names its variants in its card-id file.
        /// </summary>
        public string Variant { get; internal set; } = StandardVariant;

        /// <summary>A deck's main list.</summary>
        public const string StandardVariant = "Standard";

        protected virtual bool HandlesStapleChains => false;

        private int _ownTurnParity = -1;

        /// <summary>Whether it is our turn (learned from main-phase and battle prompts, which only happen on our turn).</summary>
        protected bool IsOwnTurn(BotStateHeader header) => _ownTurnParity >= 0 && header.CurrentTurn % 2 == _ownTurnParity;
    }
}
