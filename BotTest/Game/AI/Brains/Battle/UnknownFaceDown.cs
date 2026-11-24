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
    /// Attacking their unknown face-down monster (the first one by zone). The bot must not use its hidden stats: it's
    /// assumed to be Ryko, so the strongest monster attacks it first - if Ryko's flip destroys it, it has already attacked
    /// and the rest can still attack; if they destroy a weaker one instead, the best monster survives.
    /// Rules, in order: the deck's preferred attacker, CheapIntoFaceDown, AvoidBattleRemoval, StrongestIntoFaceDown
    /// (each in Battle/Rules).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private async Task<BattleCmdResponse?> AttackUnknownFaceDownAsync(BattleSituation battle)
        {
            var faceDown = battle.FaceDownTargets
                .Where(card => !OpponentMemory.TryRecall(card, out _))
                .OrderBy(card => card.CurrentCoordinates.Sequence)
                .FirstOrDefault();
            if (faceDown.CurrentCoordinates.Location == 0 && faceDown.ID <= 0)
                return null; // none
            return DeckPreferredFaceDownAttack(battle, faceDown)
                ?? CheapIntoFaceDown(battle, faceDown)
                ?? await AvoidBattleRemovalAsync(battle, faceDown)
                ?? StrongestIntoFaceDown(battle, faceDown);
        }

        /// <summary>
        /// The deck's own pick for probing a face-down (Blackwing: Shura / Bora, whose effects pay off on a kill).
        /// </summary>
        private BattleCmdResponse? DeckPreferredFaceDownAttack(BattleSituation battle, DynamicCard faceDown)
        {
            if (PreferredFaceDownAttacker(battle.Attackers) is not { ID: > 0 } preferred)
                return null;
            AimAt(battle.Context.Bot, faceDown, AimKind.Deliberate, $"attack the unknown face-down with {CardNames.Get(preferred.ID)}: {PreferredFaceDownWhy(preferred)}");
            return preferred.DeclareAttack();
        }

        /// <summary>The deck's preferred attacker into an unknown face-down monster, or null. Default: none.</summary>
        protected virtual DynamicCard? PreferredFaceDownAttacker(List<DynamicCard> attackers) => null;

        /// <summary>Why the deck prefers this attacker into a face-down (for the log).</summary>
        protected virtual string PreferredFaceDownWhy(DynamicCard attacker) => "the deck's pick";
    }
}
