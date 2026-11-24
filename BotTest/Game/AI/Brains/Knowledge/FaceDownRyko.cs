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
    /// The chance an unknown face-down monster of theirs is Ryko (or another flip that destroys a card).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Whether sending a cheap monster into a possible Ryko actually dodges anything. Ryko destroys any card on the
        /// field, not just its attacker, so it only helps when everything we have out is cheap (no Spell/Trap, no good
        /// monster): then its flip has nothing better to hit. Otherwise it just picks our best card anyway.
        /// </summary>
        protected bool CheapAttackDodgesRyko() =>
            _ownSpellTrapCount == 0 && _ownMonsters.All(own => own.ID <= 0 || IsCheapAttacker(own));

        /// <summary>
        /// Chance their face-down monster is a flip that destroys a card when flipped (Ryko, Night Assailant), from the deck
        /// lists their cards fit (a deck without Ryko makes it unlikely). Without list data: assumed (1).
        /// </summary>
        protected double FaceDownRykoChance(DynamicCard faceDown) =>
            BoardAssessment.KnownId(faceDown) is int known and > 0 ? (known == RykoLightswornHunter || FlipHitsACard(known) ? 1.0 : 0.0)
            : HamsterSet(faceDown.CurrentCoordinates.Sequence) ? 1.0 // what Mega Hamster set: assumed Ryko (round-robin 29a)
            : OpponentDeck.FlipDestroyChance(faceDown.CurrentCoordinates.Sequence) is double read
                // While we have little idea what deck they're on, a face-down is overweighted as Ryko (user, 2026-11-13: "bump it
                // up to say 30% until you have an idea of what deck the opponent is on").
                ? (OpponentDeck.TopTypeChance() < OpponentDeckPredictor.KnownTypeChance ? Math.Max(read, UnknownDeckRykoChance) : read)
                : 1.0;

        /// <summary>Where FaceDownRykoChance's number came from (for the log: 2026-11-17, every hold note read "Ryko 100%").</summary>
        protected string FaceDownRykoWhy(DynamicCard faceDown)
        {
            int zone = faceDown.CurrentCoordinates.Sequence;
            if (BoardAssessment.KnownId(faceDown) is int known and > 0)
                return $"known card {CardNames.Get(known)} (id {faceDown.ID}, alias {faceDown.Alias}, controller {faceDown.CurrentCoordinates.Controller})";
            if (HamsterSet(zone))
                return "set by Mega Hamster";
            if (OpponentDeck.KnownSet(true, zone) is int pinned)
                return $"pinned by the deck read as {CardNames.Get(pinned)}";
            return OpponentDeck.FlipDestroyChance(zone) is double read
                ? $"deck read {read:P0}" + (OpponentDeck.TopTypeChance() < OpponentDeckPredictor.KnownTypeChance ? $", deck unclear: at least {UnknownDeckRykoChance:P0}" : "")
                : "no deck data";
        }

        /// <summary>Least Ryko chance for a face-down while the deck read is unclear (top type under KnownTypeChance). Tune from logs.</summary>
        [Tunable("Least Ryko chance for a face-down while the deck read is unclear (top type under KnownTypeChance). Tune from logs.")]
        public double UnknownDeckRykoChance { get; set; } = 0.30;

        /// <summary>Whether to play around a Ryko in this face-down monster (its chance is at least RykoPlayAroundChance).</summary>
        /// The bar moves with the risk tolerance (DeckBotBrain.Risk): later in the duel, attacking into a face-down is a risk
        /// taken more readily.
        protected bool LikelyRyko(DynamicCard faceDown) => FaceDownRykoChance(faceDown) >= RykoPlayAroundChance * (RykoRiskOffset + RiskNow);

        /// <summary>Ryko chance from which a face-down monster is played around as a Ryko. Tune from logs.</summary>
        [Tunable("Ryko chance from which a face-down monster is played around as a Ryko. Tune from logs.")]
        public double RykoPlayAroundChance { get; set; } = 0.2;
    }
}
