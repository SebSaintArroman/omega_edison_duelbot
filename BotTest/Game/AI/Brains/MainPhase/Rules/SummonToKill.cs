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
        /// RULE deck.summon-to-kill
        /// A face-up defence-position effect monster of theirs that our summon destroys in battle this turn (user, 2026-11-12: "I
        /// would've attacked lyla with jinzo returner here a long time ago": Lyla in defence at 200, our 600 kept in hand because it
        /// didn't beat their 2500). The kill is worth exposing the monster, even next to a bigger attacker. Not a Treeborn Frog that
        /// would just come back. The attack is committed.
        /// </summary>
        private IdleCmdResponse? SummonToKill(SummonSituation s)
        {
            var prey = s.EnemyMonsters.Where(card => card.IsFaceup() && !card.IsAttack() && card.ID > 0 && !card.HasType(CardType.Normal) &&
                                                     TreebornAttackPointless(default, card, s.EnemyMonsters, s.OurAttackersNow.Append(s.Summonable.Select(m => m.Attack).DefaultIfEmpty(0).Max()).ToList(), s.TheirSpellTraps) == null)
                .OrderByDescending(OpponentThreat).FirstOrDefault();
            var killer = prey.ID > 0 && s.BattleNow
                ? s.Summonable.Where(card => card.Attack > prey.Defense && !AttacksOnlyDirectlyOnOmega(card.ID)).OrderBy(card => card.Attack).FirstOrDefault()
                : default;
            if (killer.ID <= 0 || !Rule(DeckRules.SummonToKill))
                return null;
            StapleNote(s.Context, $"normal summon {CardNames.Get(killer.ID)}: it destroys {CardNames.Get(prey.ID)} ({prey.Defense} DEF) in battle this turn");
            CommitAttack(s.Context.Header.CurrentTurn, killer.ID, prey, $"{CardNames.Get(killer.ID)} was summoned to destroy {CardNames.Get(prey.ID)} in battle");
            return killer.NormalSummon();
        }
    }
}
