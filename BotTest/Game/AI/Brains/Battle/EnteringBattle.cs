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
    /// Whether the Battle Phase is worth entering, and whether sets wait for Main Phase 2 because we attack first.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private bool HasGoodAttack(DynamicCard own, List<DynamicCard> opponents, List<DynamicCard> attackers) => opponents.Count == 0
            ? !IsWeak(own) || ChangesClock(own, attackers) || IsBeater(own) || ExpendableHitter(own)
            : opponents.Any(target => target.IsFaceup() ? IsWorthAttacking(own, target)
                                                       : !IsWeak(own) && own.Attack > OpponentMemory.FaceDownDefense(target));

        // (A good equal-ATK trade counts too: the battle code makes it, so the Battle Phase isn't wasted.)
        private bool HasHarmlessAttack(DynamicCard own, List<DynamicCard> opponents) => opponents.Count == 0 ||
            opponents.Any(target => target.IsFaceup() && GetAttackScore(own, target) > 0 && !(BouncesAttacker(target) && ReSummonCost(own) >= 100) &&
                                    (!IsRecruiter(target) || IsWorthAttacking(own, target))) ||
            opponents.Any(target => IsGoodTrade(own, target, Math.Max(1, _ownMonsters.Count(card => card.IsFaceup() && card.IsAttack()))));

        /// <summary>
        /// Whether any available attacker has a worthwhile attack.
        /// </summary>
        protected async Task<string?> BattleWorthEnteringAsync()
        {
            var attackers = (await GetBotMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .ToList();
            if (attackers.Count == 0)
                return "no face-up attack-position monster";
            // A battle we committed to (an attack a rule summoned for, a lethal push, the search's line): never skipped.
            if (BattleCommitted)
                return null;
            var opponents = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsDisabled()).ToList();
            if (opponents.Count == 0)
                return null;
            // An unknown face-down is worth a battle only with an attacker that beats what it's assumed to be (seen 2026-11-11:
            // Substitoad, 100 ATK, entered the Battle Phase for a face-down Ryko it couldn't kill, and the flip killed it).
            if (opponents.Any(card => !card.IsFaceup()) && attackers.Any(card => card.Attack > OpponentMemory.LowestFaceDownDefense(opponents)))
                return null;
            await RefreshRewardOnDestroyAsync(opponents);
            if (attackers.Any(attacker => HasGoodAttack(attacker, opponents, attackers) || HasHarmlessAttack(attacker, opponents)))
                return null;
            // A crash that pays off (Fortress destroys a card, Returner revives Jinzo): the battle step makes it (see the
            // CrashForBattlePayoff attack), so the Battle Phase isn't skipped (seen 2026-11-12: Jinzo - Returner summoned "to
            // crash into Rai-Mei", then the Battle Phase was skipped as "nothing it can attack").
            if (attackers.Any(attacker => BattleDeathPayoff(attacker) != null && opponents.Any(target =>
                    target.IsFaceup() && target.IsAttack() && target.Attack >= attacker.Attack &&
                    !BouncesAttacker(target) && !DestroysByEffectInBattle(target, attacker))))
                return null;
            return "no attacker has a worthwhile attack";
        }

        /// <summary>
        /// Whether anything we'd set waits for Main Phase 2, thought through before setting (user, 2026-11-13: "am I going to
        /// set? is a battle phase possible? do I want to attack with anything? if I want to attack, then hold the set until mp2
        /// and move into battle phase. if I don't want to attack, then consider if there's a monster i want to atk with that
        /// could be special summoned, if there is, hold the set until mp2, special summon the monster, and attack"):
        ///  1. Main Phase 1 with a Battle Phase possible;
        ///  2. an attack we want with what's out (a face-up attacker and a Battle Phase worth entering), or
        ///  3. a monster we could Special Summon now that has an attack we'd want (see SummonToAttackAsync).
        /// Otherwise there's nothing to attack with: set now (we never enter the Battle Phase just to reach Main Phase 2).
        /// </summary>
        protected async Task<bool> SetsWaitForMainPhase2Async(DecisionContext<IdleCmdData> context)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !context.StateData.CanBattlePhase || !Rule(DeckRules.SetsAfterBattle))
                return false;
            if ((await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack()) && await BattleWorthEnteringAsync() == null)
                return true;
            return await SummonToAttackAsync(context) != null;
        }

        /// <summary>
        /// A monster we can Special Summon now that we'd want to attack with this turn (it beats something worth attacking,
        /// or gets a direct hit worth making), or default. Only a reason to hold sets: whether it's actually summoned is the
        /// spend rules' call, and if it isn't, the sets are made then.
        /// </summary>
        protected async Task<DynamicCard?> SummonToAttackAsync(DecisionContext<IdleCmdData> context)
        {
            var opponents = (await GetEnemyMonsterCardsAsync()).Where(card => !card.IsDisabled()).ToList();
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            await RefreshRewardOnDestroyAsync(opponents);
            foreach (var card in context.StateData.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
            {
                if (card.ID <= 0 || !card.HasType(CardType.Monster) || card.Attack <= 0)
                    continue;
                if (HasGoodAttack(card, opponents, attackers.Append(card).ToList()))
                    return card;
            }
            return null;
        }
    }
}
