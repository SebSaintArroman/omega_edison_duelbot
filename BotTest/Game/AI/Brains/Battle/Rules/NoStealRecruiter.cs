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
        /// RULE deck.no-steal-recruiter (attack side; the attack-target prompt holds recruiters too, see
        /// Selection/CardSelection.cs)
        /// Goyo Guardian (any monster that takes what it destroys by battle) doesn't attack a battle recruiter: they get a
        /// monster from the deck. Only worth it when taking it would take a turn off our clock from next turn
        /// (StealSpeedsUpClock). True: skip this attack.
        /// </summary>
        private bool NoStealRecruiterInBattle(BattleSituation battle, DynamicCard attacker, DynamicCard target)
        {
            if (!StealsWhatItDestroys(attacker) || !IsRecruiter(target) || StealSpeedsUpClock(attacker, target) || !Rule(DeckRules.NoStealRecruiter))
                return false;
            battle.Context.Bot.LogNote($"deck brain: don't take {CardNames.Get(target.ID)} with {CardNames.Get(attacker.ID)}: it's a battle recruiter " +
                                       "(they get a monster from the deck): only worth it when it would take a turn off our clock from next turn and a set card of ours can protect it");
            return true;
        }
    }
}
