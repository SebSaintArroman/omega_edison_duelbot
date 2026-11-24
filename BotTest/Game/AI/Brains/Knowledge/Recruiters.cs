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
    /// Recruiters and floaters: monsters that replace themselves when destroyed (by battle, or by anything).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Attacking their Treeborn Frog achieves nothing: in defence it just comes back next Standby Phase (user, 2026-11-12).
        /// Unless it's in attack position (damage), destroying it clears the path for a direct hit this turn (our attackers
        /// can take every monster of theirs, Treeborn included, with one left over: they may start with two monsters against
        /// our three), their spell/trap stops it coming back, or our attacker banishes / bounces what it battles. (Attacking it
        /// only to deal damage or destroy the target.)
        /// Returns why it's pointless, or null.
        /// </summary>
        protected string? TreebornAttackPointless(DynamicCard attacker, DynamicCard target, IReadOnlyList<DynamicCard> theirMonsters,
            IReadOnlyList<int> ourAttackers, int theirSpellTraps)
        {
            if (!target.IsCode(TreebornFrog) || !target.IsFaceup() || target.IsAttack())
                return null;
            // Clears the path: each of their monsters (biggest first) taken by our weakest attacker that beats it, one attacker
            // still left for the direct hit.
            var left = ourAttackers.OrderBy(atk => atk).ToList();
            bool cleared = true;
            foreach (var monster in theirMonsters.OrderByDescending(card => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card)
                         : card.IsAttack() ? card.Attack : card.Defense))
            {
                int power = !monster.IsFaceup() ? OpponentMemory.FaceDownDefense(monster) : monster.IsAttack() ? monster.Attack : monster.Defense;
                int index = left.FindIndex(atk => atk > power);
                if (index < 0) { cleared = false; break; }
                left.RemoveAt(index);
            }
            if (cleared && left.Count > 0)
                return null; // destroying it is part of clearing the way for a direct hit
            if (theirSpellTraps > 0)
                return null; // their backrow keeps it in the graveyard
            if (_removesWhatItBattles.Contains(attacker.ID))
                return null;
            return "it's Treeborn Frog in defence: it comes back next Standby Phase (they have no spell/trap to stop it), and nothing follows it for a direct hit";
        }

        /// <summary>A floater that Special Summons a replacement when destroyed (Giant Rat): it replaces itself.</summary>
        protected bool IsRecruiter(DynamicCard target) => _recruiters.Contains(target.ID);

        /// <summary>
        /// A recruiter that only recruits when destroyed by battle (Giant Rat, Mystic Tomato): any removal outside battle
        /// (Mirror Force, D-Prison, Bottomless...) stops its replacement. Others (recruit on any destruction or on being
        /// sent to the graveyard) are only stopped by banishing.
        /// </summary>
        protected bool RecruitsOnlyByBattle(DynamicCard target) => _battleOnlyRecruiters.Contains(target.ID);

        private readonly HashSet<int> _battleOnlyRecruiters = new();

        // A recruit trigger not tied to battle: "destroyed" / "sent to the graveyard" without "by battle" before the summon.
        private static readonly System.Text.RegularExpressions.Regex RecruitOnAnyDestructionText = new(
            @"(destroyed(?! by battle)(?! and sent to the (graveyard|gy) by battle)|(?<!by battle and )sent (from the field )?to the (graveyard|gy)(?! by battle))[^.]{0,120}?special summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Floaters whose reward is a Special Summon (they replace themselves on the field: Giant Rat, Mystic Tomato, ...).
        private readonly HashSet<int> _recruiters = new();

        private static readonly System.Text.RegularExpressions.Regex RecruitText = new(
            @"(destroyed|sent (from the field )?to the (graveyard|gy))[^.]{0,120}?special summon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
