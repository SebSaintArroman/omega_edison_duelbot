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
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// RULE deck.cheapest-into-bouncer
        /// Into a monster that bounces its attacker (Grand Mole), send the cheapest monster to re-summon, preferring one with
        /// an on-summon effect (bouncing it is a plus: we use the effect again). Returns the kill with that attacker.
        /// </summary>
        private async Task<AttackOption?> CheapestIntoBouncerAsync(BattleSituation battle, AttackOption? kill)
        {
            if (kill == null || !BouncesAttacker(kill.Target) || !Rule(DeckRules.CheapestIntoBouncer))
                return kill;
            foreach (var attacker in battle.Attackers)
                await HasOnSummonEffectAsync(attacker.ID);
            var cheapest = battle.Attackers
                .Where(attacker => IsWorthAttacking(attacker, kill.Target))
                .OrderBy(ReSummonCost)
                .ThenBy(attacker => HasOnSummonEffectCached(attacker) ? 0 : 1)
                .ThenBy(attacker => attacker.Attack)
                .First();
            battle.Context.Bot.LogNote($"deck brain: {CardNames.Get(kill.Target.ID)} returns its attacker to the hand: attack it with the cheapest to re-summon, " +
                                       $"{CardNames.Get(cheapest.ID)}{(HasOnSummonEffectCached(cheapest) ? " (its on-summon effect can be used again)" : "")}");
            return kill with { Attacker = cheapest };
        }
    }
}
