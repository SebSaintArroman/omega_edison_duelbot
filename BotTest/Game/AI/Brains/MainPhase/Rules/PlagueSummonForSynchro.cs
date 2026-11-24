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
        /// RULE deck.plague-summon-for-synchro
        /// Plaguespreader Zombie is summoned face-up only as the first step of its synchro routine (TryPlagueSummonSynchroAsync),
        /// never as an attacker; on its own it's set (user, 2026-11-13). Takes it out of the summon options, and returns the
        /// Plaguespreader the safe summon would otherwise have been (default: none).
        /// </summary>
        private DynamicCard PlagueOnlySummonedForSynchro(SummonSituation s)
        {
            var plagueWanted = s.Summonable.FirstOrDefault(card => card.IsCode(PlaguespreaderZombie) && card.Attack > s.StrongestEnemy && !s.Weak(card));
            if (!s.Summonable.Any(card => card.IsCode(PlaguespreaderZombie)) || !Rule(DeckRules.PlagueSummonForSynchro))
                return default;
            foreach (var card in s.Summonable.Where(card => card.IsCode(PlaguespreaderZombie)))
                Hold(card.ID, MainAction.Summon, DeckRules.PlagueSummonForSynchro);
            s.Summonable = s.Summonable.Where(card => !card.IsCode(PlaguespreaderZombie)).ToList();
            return plagueWanted;
        }

        /// <summary>
        /// Plaguespreader would have been the safe summon: just getting it into play from the hand is a set, in Main Phase 2.
        /// </summary>
        private IdleCmdResponse? PlagueSetInMainPhase2(SummonSituation s, DynamicCard plagueWanted)
        {
            if (plagueWanted.ID <= 0 || (DuelPhase)s.Context.Header.CurrentPhase != DuelPhase.Main2 ||
                (s.State.MonsterSetableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(card => card.IsCode(PlaguespreaderZombie)) is not { ID: > 0 } plagueSet)
                return null;
            StapleNote(s.Context, "set Plaguespreader Zombie: into play from the hand without a synchro now, so set (in Main Phase 2), not summoned");
            return plagueSet.SetMonster();
        }
    }
}
