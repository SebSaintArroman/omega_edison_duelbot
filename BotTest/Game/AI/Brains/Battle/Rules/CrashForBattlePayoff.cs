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
    /// RULE deck.crash-for-battle-payoff: a monster that pays off when destroyed by battle (Machina Fortress) crashes into
    /// a monster at least as big.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// The crash to make: a monster that pays off when destroyed by battle (Machina Fortress destroys a card) into an
        /// attack-position monster at least as big, as long as the damage doesn't kill us. Not into a bouncer (it would just
        /// return to the hand, no payoff), nor into a monster that destroys it by effect (no battle payoff). The best gain
        /// less our monster first. Weighed against the other attacks in FaceUpAttacks.WeighAttacks. Why is null: none.
        /// </summary>
        private (DynamicCard Attacker, DynamicCard Target, string? Why) CrashForPayoff(BattleSituation battle) => battle.Attackers
            .Select(attacker => (Attacker: attacker, Why: BattleDeathPayoff(attacker)))
            .Where(x => x.Why != null)
            .SelectMany(x => battle.FaceUpTargets
                .Where(target => target.IsAttack() && target.Attack >= x.Attacker.Attack && !BouncesAttacker(target) &&
                                 !DestroysByEffectInBattle(target, x.Attacker) && // a Catastor-type kills it by effect: no battle payoff
                                 target.Attack - x.Attacker.Attack < battle.OurLifePoints)
                .Select(target => (x.Attacker, Target: target, x.Why)))
            .OrderByDescending(x => BattleDeathGain(x.Attacker, x.Target, battle.FaceUpTargets) - TargetThreat(x.Attacker))
            .FirstOrDefault();

        /// <summary>
        /// Why this monster of ours pays off when it's destroyed by battle (e.g. Machina Fortress destroys a card), so it's
        /// worth crashing into a monster at least as big; null for most monsters. Deck brains override this.
        /// </summary>
        protected virtual string? BattleDeathPayoff(DynamicCard card) =>
            card.IsCode(LylaLightswornSorceress) && card.IsFaceup() && _ourDeckCount < LylaDeckGuard
                ? $"only {_ourDeckCount} cards left in our deck: Lyla's End Phase mill (3 a turn) would deck us out, so she's better gone"
                : null;

        /// <summary>
        /// What our monster's death by battle against <paramref name="killer"/> gains, in TargetThreat units (ATK plus effect
        /// points), before counting the monster lost: the battle-attack weighing subtracts that. Pairs with BattleDeathPayoff
        /// (deck brains override both). Default: Lyla better gone (she isn't a loss), else nothing.
        /// </summary>
        protected virtual double BattleDeathGain(DynamicCard card, DynamicCard killer, List<DynamicCard> theirMonsters) =>
            // Lyla whose End Phase mill would deck us out goes at any cost (user, 2026-11-12): decisive, short of damage that kills
            // us (the crash candidates already exclude that).
            card.IsCode(LylaLightswornSorceress) && BattleDeathPayoff(card) != null ? TargetThreat(card) + LylaMustGoValue : 0;

        /// <summary>Value of getting rid of a Lyla whose mill would deck us out: larger than anything else a battle weighs.</summary>
        private const double LylaMustGoValue = 100_000;
    }
}
