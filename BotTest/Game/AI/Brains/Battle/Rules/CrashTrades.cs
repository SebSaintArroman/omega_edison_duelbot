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
    /// RULE deck.crash-trades: crash into an equal-ATK attack-position monster when we lose no more than they do.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// The trade to make: an attacker (token or not, judged by its size and effects) worth less than the threat it takes
        /// out (e.g. a Gorz token into Muka Muka); the biggest threat first, then our cheapest attacker. Weighed against the
        /// other attacks in FaceUpAttacks.WeighAttacks. Null: none.
        /// </summary>
        private AttackPair? CrashTrade(BattleSituation battle) => battle.Attackers
            .SelectMany(attacker => battle.FaceUpTargets
                .Where(target => IsGoodTrade(attacker, target, battle.Attackers.Count))
                .Select(target => new AttackPair(attacker, target)))
            .OrderByDescending(option => TargetThreat(option.Target))
            .ThenBy(option => TargetThreat(option.Attacker))
            .FirstOrDefault();

        /// <summary>
        /// Crashing into an equal-ATK attack-position monster (both destroyed) is useful: it clears the way and trades
        /// 1-for-1. Good when what we lose scores no more than what they lose (effects included, negated ones not), and
        /// always with a cheap attacker: a negated monster (a Vayu synchro is just a body) or an expendable one (Armed
        /// Wing / Armor Master become Vayu partners in the graveyard). Never into a monster that profits from being
        /// destroyed: a battle recruiter or floater (it replaces itself, so it isn't a trade) or one with a graveyard
        /// effect (sending it there helps them). Not into a bouncer that would cost us a token / Extra Deck monster.
        /// </summary>
        private bool IsGoodTrade(DynamicCard attacker, DynamicCard target, int attackerCount) =>
            target.IsFaceup() && target.IsAttack() && attacker.Attack == target.Attack && target.Attack > 0 &&
            !(BouncesAttacker(target) && ReSummonCost(attacker) >= 100 && !LethalIfBothRemoved(attacker, target)) &&
            !(BouncesAttacker(target) && MoleLockStopsAttack(attackerCount, out _)) &&
            !IsRecruiter(target) && !RewardsDestruction(target) && !HasGraveyardEffect(target) &&
            !IsBattleProof(target) && // a live Armor Master survives: no trade, we just lose ours
            (IsEffectNegated(attacker) || IsExpendable(attacker) || TargetThreat(attacker) <= TargetThreat(target)) &&
            AllowCrashTrade(attacker, target) && Rule(DeckRules.CrashTrades);

        /// <summary>
        /// Whether this deck takes equal-ATK crash trades with this attacker at all. Default yes; a deck whose monsters are
        /// worth more than a 1-for-1 (Machina) only trades with expendable ones.
        /// </summary>
        protected virtual bool AllowCrashTrade(DynamicCard attacker, DynamicCard target) => true;
    }
}
