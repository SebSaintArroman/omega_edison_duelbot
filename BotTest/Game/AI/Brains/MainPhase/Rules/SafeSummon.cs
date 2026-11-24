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
        /// RULE deck.safe-summon
        /// Summon something that beats (or meets nothing to fight) their strongest face-up attacker and is a real attacker: a weak
        /// monster face-up is just exposed, and a small value-set monster (Shield Warrior, Necro Gardna, Vayu...) is worth more set
        /// (it bluffs a Ryko and still reaches the graveyard; user, 2026-11-11). The deck's order first, then the biggest.
        /// </summary>
        private IdleCmdResponse? SafeSummon(SummonSituation s)
        {
            var safeSummon = s.Summonable
                .Where(card => card.Attack > s.StrongestEnemy && !s.Weak(card) && !(card.IsCode(ValueSetIds) && card.Attack < ValueSetSummonAttack))
                .OrderBy(s.SafePriority)
                .ThenByDescending(card => card.Attack)
                .FirstOrDefault();
            if (safeSummon.ID <= 0 || !Rule(DeckRules.SafeSummon))
                return null;
            StapleNote(s.Context, $"normal summon {CardNames.Get(safeSummon.ID)} ({safeSummon.Attack} ATK beats strongest opponent attacker {s.StrongestEnemy})");
            return safeSummon.NormalSummon();
        }
    }
}
