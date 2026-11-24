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
    /// Attacking when their field has no monsters. With no set spells/traps either, the smallest attacker goes first, so a
    /// hand trap that answers the attack (Gorz, Battle Fader...) spends itself on the least valuable one; AttackLast cards
    /// (Chaos Sorcerer) go after everything else. With backrow up, the biggest goes first.
    /// Rules, in order: WeakDontAttackDirectly, OneHitLethalFirst, HoldForGorz, NoChipIntoBackrow (each in Battle/Rules).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private async Task<BattleCmdResponse> AttackDirectlyAsync(BattleSituation battle)
        {
            var context = battle.Context;
            bool fieldEmpty = await TheirBackrowCountsAsEmptyAsync(context);
            var worthwhile = DirectAttackersWorthTheHit(battle);
            var attacker = (fieldEmpty
                    ? worthwhile.OrderBy(card => card.IsCode(AttackLast) ? 1 : 0).ThenBy(card => card.Attack)
                    : worthwhile.OrderByDescending(card => card.Attack))
                .FirstOrDefault();

            if (OneHitLethalFirst(battle, worthwhile) is DynamicCard oneShot)
                attacker = oneShot;
            else if (fieldEmpty && attacker.ID > 0 && await HoldDirectAttackForGorzAsync(context, attacker))
            {
                ClearAim();
                return LeaveBattle(context);
            }
            else if (fieldEmpty && attacker.ID > 0)
                context.Bot.LogNote($"deck brain: opponent's field is empty, attack smallest first: {CardNames.Get(attacker.ID)}");

            if (!fieldEmpty && attacker.ID > 0 && NoChipIntoBackrow(battle, attacker))
            {
                ClearAim();
                return LeaveBattle(context);
            }

            if (attacker.ID > 0)
            {
                ConsoleUtils.PrintColored($"Direct Attack with Card ID: {attacker.ID}, Attack: {attacker.Attack}");
                AimAt(context.Bot, null, AimKind.Fallback, $"{CardNames.Get(attacker.ID)} attacks directly: their field is empty");
                return attacker.DeclareAttack();
            }

            ConsoleUtils.PrintColored("No direct attack possible.");
            ClearAim();
            return LeaveBattle(context);
        }

        /// <summary>
        /// Whether their spell/trap zone counts as empty for the attack order: it is, or their traps are negated (face-up Jinzo /
        /// Royal Decree) and their set cards are unlikely to be a quick-play spell that stops us (user, 2026-11-10).
        /// </summary>
        private async Task<bool> TheirBackrowCountsAsEmptyAsync(DecisionContext<BattleCmdData> context)
        {
            if ((await GetEnemySpellCardsAsync()).Count == 0)
                return true;
            if (await TrapsNegatedAsync() && (await SetCardStopChancesAsync()).All(stop => stop < NegatedBackrowIgnoredBelow))
            {
                context.Bot.LogNote("deck brain: their set cards can't hurt the attack: traps are negated (Jinzo / Royal Decree) and a quick-play stopper is unlikely");
                return true;
            }
            return false;
        }

        /// <summary>Monsters that attack last into an empty field (deck brains override, e.g. Chaos Sorcerer).</summary>
        protected virtual IReadOnlyCollection<int> AttackLast => Array.Empty<int>();
    }
}
