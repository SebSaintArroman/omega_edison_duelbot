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
        /// RULE deck.kill-before-recruiter / deck.attack-into-battle-reward
        /// Attacking a monster whose destruction rewards them (a recruiter like XX-Saber Emmersblade, a draw like Card Trooper) is
        /// discouraged, not prevented: a light hold always, and a larger one while a kill on a monster without such an effect is on
        /// offer (that kill comes first in the brain's own pick too). User, 2026-11-25 (Machina, Gearframe into Emmersblade over Ryko):
        /// "emmersblade has a destroyed by battle ability so I'd have prioritised the damage and actually clearing a monster rather than
        /// potentially benefitting them"; "we should have rules discouraging (but not preventing) attacking destroyed by battle effects,
        /// especially when there are other eligible monsters on the field". The search can't see a recruit (their deck is inert in its
        /// lines), so the target pick holds these.
        /// </summary>
        private bool RewardsBattleDestruction(DynamicCard target) => IsRecruiter(target) || RewardsDestruction(target);

        /// <summary>Whether this attacker destroys a face-up monster of theirs whose destruction doesn't reward them, among <paramref name="targets"/>.</summary>
        private bool HasPlainKill(DynamicCard attacker, IEnumerable<DynamicCard> targets)
        {
            int atk = attacker.ID > 0 ? attacker.Attack : 0;
            return targets.Any(target => target.ID > 0 && target.IsFaceup() && target.CurrentCoordinates.Controller != 0 &&
                                         !RewardsBattleDestruction(target) && atk > (target.IsAttack() ? target.Attack : target.Defense));
        }

        /// <summary>The attack target prompt: targets whose destruction rewards them are held (more while a plain kill is on offer).</summary>
        private void HoldRecruiterTargets(DecisionContext<SelectCardData> context)
        {
            var offered = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (_declaredAttacker.ID <= 0)
                return;
            string rule = HasPlainKill(_declaredAttacker, offered) ? DeckRules.KillBeforeRecruiter : DeckRules.AttackIntoBattleReward;
            foreach (var target in offered)
                if (target.ID > 0 && target.IsFaceup() && RewardsBattleDestruction(target) && Rule(rule))
                    HoldPick(target.ID, rule);
        }
    }
}
