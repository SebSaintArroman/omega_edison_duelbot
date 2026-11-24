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
        /// RULES deck.hold-monster-sets-for-battle and deck.hold-sets-for-battle (decided by deck.sets-after-battle, see
        /// SetsWaitForMainPhase2Async in Battle/EnteringBattle.cs)
        /// Sets (monsters and spells/traps) wait for Main Phase 2 when we attack this turn: setting first gains nothing (and set
        /// spells/traps could be destroyed by a flipped Ryko). With no attack coming, set now. Removes the sets from the options.
        /// </summary>
        private async Task<DecisionContext<IdleCmdData>> SetsWaitForBattleAsync(DecisionContext<IdleCmdData> context)
        {
            if (((context.StateData.MonsterSetableCards?.Length ?? 0) == 0 && (context.StateData.SpellSetableCards?.Length ?? 0) == 0) ||
                !await SetsWaitForMainPhase2Async(context))
                return context;
            var attackFirst = context.StateData;
            foreach (var card in attackFirst.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                Hold(card.ID, MainAction.SetMonster, DeckRules.HoldMonsterSetsForBattle);
            foreach (var card in attackFirst.SpellSetableCards ?? Array.Empty<DynamicCard>())
                Hold(card.ID, MainAction.SetSpell, DeckRules.HoldSetsForBattle);
            attackFirst.MonsterSetableCards = Array.Empty<DynamicCard>();
            attackFirst.SpellSetableCards = Array.Empty<DynamicCard>();
            return context with { StateData = attackFirst };
        }

        /// <summary>
        /// RULE deck.hold-monster-sets-for-battle (summon side)
        /// Monster sets and blockers wait for Main Phase 2 when we attack this turn: setting first gains nothing, and the battle's
        /// outcome may change what's worth setting. True: decide nothing now (the sets are held).
        /// </summary>
        private async Task<bool> HoldMonsterSetsForBattleAsync(SummonSituation s)
        {
            if (!await SetsWaitForMainPhase2Async(s.Context) || !Rule(DeckRules.HoldMonsterSetsForBattle))
                return false;
            if (s.Summonable.Count + s.Settable.Count > 0)
                StapleNote(s.Context, "hold monster sets until Main Phase 2");
            foreach (var card in s.Settable)
                Hold(card.ID, MainAction.SetMonster, DeckRules.HoldMonsterSetsForBattle);
            return true;
        }
    }
}
