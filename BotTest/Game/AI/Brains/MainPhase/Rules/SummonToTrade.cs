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
        /// RULE deck.summon-to-trade
        /// Round-robin 34a (user, 2026-11-13): a monster that destroys one of their monsters in battle this turn (one none of ours
        /// already beats: one attack per target), attack position too, when the trade is even or better. If it's smaller than
        /// their biggest and nothing else removes that, it dies next turn: counted (our card for theirs), usually still fine.
        /// The attack is committed.
        /// </summary>
        private IdleCmdResponse? SummonToTrade(SummonSituation s)
        {
            if (!s.BattleNow)
                return null;
            var oursUp = s.BotMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            foreach (var candidate in s.Summonable.OrderBy(s.SafePriority).ThenBy(card => card.Attack))
            {
                var victim = s.EnemyMonsters.Where(card => card.IsFaceup() && card.ID > 0 && card.GetRealPower() < EffectiveAttack(candidate) &&
                                                           !oursUp.Any(own => EffectiveAttack(own) > card.GetRealPower()) &&
                                                           TreebornAttackPointless(default, card, s.EnemyMonsters, s.OurAttackersNow.Append(candidate.Attack).ToList(), s.TheirSpellTraps) == null)
                    .OrderByDescending(OpponentThreat).FirstOrDefault();
                if (victim.ID <= 0)
                    continue;
                var survivor = s.EnemyMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !SameZone(card, victim) && card.Attack > candidate.Attack)
                    .OrderByDescending(card => card.Attack).FirstOrDefault();
                // Even or better: their monster is worth at least ours (ours expendable, or theirs as strong).
                bool even = IsExpendable(candidate) || victim.GetRealPower() >= candidate.Attack - 500 || victim.HasType(CardType.Effect);
                if (!even || !Rule(DeckRules.SummonToTrade))
                    continue;
                StapleNote(s.Context, $"normal summon {CardNames.Get(candidate.ID)}: it destroys {CardNames.Get(victim.ID)} ({victim.GetRealPower()}) in battle this turn" +
                                      (survivor.ID > 0 ? $"; their {CardNames.Get(survivor.ID)} ({survivor.Attack}) probably kills it next turn: our card for theirs, even" : ""));
                CommitAttack(s.Context.Header.CurrentTurn, candidate.ID, victim, $"{CardNames.Get(candidate.ID)} was summoned to destroy {CardNames.Get(victim.ID)} in battle");
                return candidate.NormalSummon();
            }
            return null;
        }
    }
}
