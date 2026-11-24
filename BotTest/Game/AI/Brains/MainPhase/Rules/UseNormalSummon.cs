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
        /// RULE deck.use-normal-summon
        /// The last chance this turn (Main Phase 2, or Main Phase 1 with no Battle Phase to come) with the Normal Summon unused: a
        /// whole turn's Normal Summon thrown away is a real loss (user, 2026-11-13: "missing a normal summon/set completely is a
        /// really bad idea and should be punished"). Not a blocker to stay alive: the card that loses least by it - one that pays off
        /// in the graveyard or is expendable first (Plaguespreader, a flip, Vayu), else the least valuable in hand - set face-down.
        /// Kept when holding it is worth more than the Normal Summon (UnusedNormalSummonValue).
        /// </summary>
        private async Task<IdleCmdResponse?> UseNormalSummonAsync(SummonSituation s)
        {
            var context = s.Context;
            bool lastChance = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main2 ||
                              (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && !context.StateData.CanBattlePhase;
            if (!lastChance || NormalSummonUsed(context.Header) || s.Settable.Count == 0 || !Rule(DeckRules.UseNormalSummon))
                return null;
            var ranked = new List<(DynamicCard Card, double Value, string Why)>();
            foreach (var card in s.Settable)
            {
                var (value, why) = await HandCardValueAsync(card, s.BotMonsters);
                if (HasGraveyardEffect(card) || IsExpendable(card) || card.HasType(CardType.Flip))
                    value -= SetterHandDiscount;
                ranked.Add((card, value, why));
            }
            var pick = ranked.OrderBy(x => x.Value).First();
            if (pick.Value < UnusedNormalSummonValue)
            {
                StapleNote(context, $"set {CardNames.Get(pick.Card.ID)}: the Normal Summon would otherwise go unused this turn " +
                                    $"(it costs least to commit: {pick.Value:0.##} card(s) in hand, {pick.Why})");
                return pick.Card.SetMonster();
            }
            StapleNote(context, $"Normal Summon unused: every monster in hand is worth more kept ({CardNames.Get(pick.Card.ID)} {pick.Value:0.##})");
            return null;
        }
    }
}
