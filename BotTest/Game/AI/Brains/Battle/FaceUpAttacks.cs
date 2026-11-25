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
    /// Attacking their face-up monsters. Three kinds of attack are found, then weighed against each other:
    ///  - the best free kill (an attack that destroys their monster), skipping the attacks the rules rule out:
    ///    SkipUnworthyAttacks, NoStealRecruiter, MoleLock, SaveMoleForBetterAnswer; into a bouncer, CheapestIntoBouncer
    ///    picks the attacker;
    ///  - a crash trade into an equal-ATK monster (CrashTrades);
    ///  - a crash that pays off when our monster dies (CrashForBattlePayoff).
    /// Each rule is in Battle/Rules.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>One attack: who attacks what, how strong the attacker is, and how good the attack scores.</summary>
        private sealed record AttackOption(DynamicCard Attacker, DynamicCard Target, int AttackerPower, int Score);

        /// <summary>An attacker and its target.</summary>
        private sealed record AttackPair(DynamicCard Attacker, DynamicCard Target);

        private async Task<BattleCmdResponse?> AttackFaceUpMonsterAsync(BattleSituation battle)
        {
            // Grand Mole: attacking into it only bounces our monster (and Mole). Worth it when the bounce gains us
            // something (an on-summon effect again), or when we have no better answer to Mole (then trying beats losing
            // to it). With a better answer in hand / set, leave Mole alone.
            // Their Treeborn Frog in defence is pointless to attack (TreebornAttackPointless): what we know for it.
            await RefreshRemovesWhatItBattlesAsync(battle.Attackers);
            battle.TheirSpellTraps = (await GetEnemySpellCardsAsync()).Count;
            string? betterMoleAnswer = null;
            if (battle.FaceUpTargets.Any(BouncesAttacker))
            {
                foreach (var attacker in battle.Attackers)
                    await HasOnSummonEffectAsync(attacker.ID);
                betterMoleAnswer = await BetterAnswerToBouncerAsync();
            }

            var kill = BestKill(battle, betterMoleAnswer);
            kill = await CheapestIntoBouncerAsync(battle, kill);
            var trade = CrashTrade(battle);
            battle.OurLifePoints = await CurrentField.GetBotLifePoints();
            var payoff = CrashForPayoff(battle);
            return WeighAttacks(battle, kill, trade, payoff);
        }

        /// <summary>
        /// The best attack that destroys one of their face-up monsters (highest attack score, then the stronger attacker),
        /// among the attacks no rule rules out.
        /// </summary>
        private AttackOption? BestKill(BattleSituation battle, string? betterMoleAnswer) => battle.Attackers
            .SelectMany(attacker => battle.FaceUpTargets.Select(target =>
                new AttackOption(attacker, target, attacker.GetRealPower(), GetAttackScore(attacker, target))))
            .Where(option => option.Score > 0)
            .Where(option => !SkipUnworthyAttack(battle, option.Attacker, option.Target) &&
                             !NoStealRecruiterInBattle(battle, option.Attacker, option.Target) &&
                             !SkipPointlessTreebornAttack(battle, option.Attacker, option.Target) &&
                             !MoleLockStopsThisAttack(battle, option.Attacker, option.Target) &&
                             !SaveMoleForBetterAnswer(battle, option.Attacker, option.Target, betterMoleAnswer))
            // RULE deck.kill-before-recruiter (Battle/Rules/KillBeforeRecruiter.cs): a kill on a monster that doesn't replace itself first.
            .OrderByDescending(option => !RewardsBattleDestruction(option.Target) || !Rule(DeckRules.KillBeforeRecruiter) ? 1 : 0)
            .ThenByDescending(option => option.Score)
            .ThenByDescending(option => option.AttackerPower)
            .FirstOrDefault();

        /// <summary>Why attacking their Treeborn Frog is pointless (TreebornAttackPointless), or null.</summary>
        private string? TreebornWhy(BattleSituation battle, DynamicCard attacker, DynamicCard target) =>
            TreebornAttackPointless(attacker, target, battle.Targets, battle.Attackers.Select(card => card.Attack).ToList(), battle.TheirSpellTraps);

        /// <summary>
        /// Weigh the kill, the trade and the crash instead of taking the first that qualifies (user, 2026-11-12, round-robin 1:
        /// Fortress came out to crash into Dark Armed Dragon, then the free kill of Lyla came first in the step order and DAD
        /// survived). Each option's value, in TargetThreat units (ATK plus effect points): a free kill is their monster; a
        /// trade their monster less ours; a crash what the death gains less our monster and the damage (in full when it leaves
        /// us under CrashDangerLp, a share above: LP matter little up there). Plus the best kill the other attackers still have
        /// afterwards. A trade or a crash is made only for a gain (round-robin 23: Treeborn Frog crashed into Raiza at 3000 LP
        /// for nothing: it comes back anyway, so the 2300 damage bought nothing); a free kill always is one.
        /// </summary>
        private BattleCmdResponse? WeighAttacks(BattleSituation battle, AttackOption? kill, AttackPair? trade,
            (DynamicCard Attacker, DynamicCard Target, string? Why) payoff)
        {
            var context = battle.Context;
            double FollowUp(DynamicCard attacker, DynamicCard target) => battle.Attackers
                .Where(other => !SameZone(other, attacker))
                .SelectMany(other => battle.FaceUpTargets.Where(t => !SameZone(t, target) && GetAttackScore(other, t) > 0 && IsWorthAttacking(other, t) && TreebornWhy(battle, other, t) == null))
                .Select(t => (double)TargetThreat(t)).DefaultIfEmpty(0).Max();
            double DamageCost(int damage) => damage * (battle.OurLifePoints - damage < CrashDangerLp ? 1.0 : CrashSafeDamageShare);

            var options = new List<(DynamicCard Attacker, DynamicCard Target, double Value, AimKind Kind, string? Rule, string Why)>();
            if (kill != null)
            {
                string boost = kill.Attacker.Attack <= kill.Target.GetRealPower() && HandBattleBoost(kill.Attacker) > 0
                    ? $", counting on +{HandBattleBoost(kill.Attacker)} from the hand" : "";
                double value = TargetThreat(kill.Target) + FollowUp(kill.Attacker, kill.Target);
                options.Add((kill.Attacker, kill.Target, value, AimKind.Fallback, null,
                    $"attack {DescribeTarget(kill.Target)} with {CardNames.Get(kill.Attacker.ID)}: it destroys it{boost}"));
            }
            if (trade != null)
            {
                double value = TargetThreat(trade.Target) - TargetThreat(trade.Attacker) + FollowUp(trade.Attacker, trade.Target);
                options.Add((trade.Attacker, trade.Target, value, AimKind.Deliberate, DeckRules.CrashTrades,
                    $"crash {CardNames.Get(trade.Attacker.ID)} into {CardNames.Get(trade.Target.ID)} ({trade.Target.Attack} ATK each): " +
                    $"our {TargetThreat(trade.Attacker)} for their {TargetThreat(trade.Target)} threat"));
            }
            if (payoff.Why != null)
            {
                int damage = payoff.Target.Attack - payoff.Attacker.Attack;
                double gain = BattleDeathGain(payoff.Attacker, payoff.Target, battle.FaceUpTargets);
                double value = gain - TargetThreat(payoff.Attacker) - DamageCost(damage) + FollowUp(payoff.Attacker, payoff.Target);
                options.Add((payoff.Attacker, payoff.Target, value, AimKind.Deliberate, DeckRules.CrashForBattlePayoff,
                    $"crash {CardNames.Get(payoff.Attacker.ID)} ({payoff.Attacker.Attack}) into {CardNames.Get(payoff.Target.ID)} ({payoff.Target.Attack}): {payoff.Why}"));
            }
            if (options.Count > 1)
                context.Bot.LogNote("deck brain: attacks weighed: " + string.Join(" | ", options.OrderByDescending(o => o.Value)
                    .Select(o => $"{CardNames.Get(o.Attacker.ID)} into {DescribeTarget(o.Target)} {o.Value:0}")));
            foreach (var option in options.Where(o => o.Rule == null || o.Value > 0).OrderByDescending(o => o.Value))
            {
                if (option.Rule != null && !Rule(option.Rule))
                    continue;
                AimAt(context.Bot, option.Target, option.Kind, $"{option.Why} (value {option.Value:0})");
                return option.Attacker.DeclareAttack();
            }
            foreach (var option in options.Where(o => o.Rule != null && o.Value <= 0))
                context.Bot.LogNote($"deck brain: no {option.Why.Split(':')[0]}: it gains nothing (value {option.Value:0})");
            return null;
        }
    }
}
