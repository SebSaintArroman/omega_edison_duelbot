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
    /// Shared main-phase rules for staple cards (moved from the Vayu brain): free card advantage (Upstart Goblin, Charge
    /// of the Light Brigade, Reinforcement of the Army), backrow removal (Heavy Storm, Lyla, MST), Card Trooper, Jar of
    /// Greed / Legacy when there's no play, and the safe Normal Summon / Set / blocker choice (setting Ryko, value sets,
    /// not setting into Grand Mole or Goyo, keeping Gorz live). Deck brains call these in their own order.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private int _stapleActivationTurn = -1;
        private readonly Dictionary<int, int> _stapleActivations = new();

        /// <summary>
        /// Activates this card if it's on offer and its rule fires (rolled only once it's on offer). At most 3 times per
        /// turn per card, as a guard against an effect the server keeps offering.
        /// </summary>
        protected bool TryActivateStaple(DecisionContext<IdleCmdData> context, IdleCmdData state, int cardId, string reason, out IdleCmdResponse response, string? rule = null)
        {
            response = default;
            if (!TryGetActivable(state, cardId, out var card))
                return false;
            if (rule != null && !Rule(rule))
                return false;
            if (_stapleActivationTurn != context.Header.CurrentTurn)
            {
                _stapleActivationTurn = context.Header.CurrentTurn;
                _stapleActivations.Clear();
            }
            _stapleActivations.TryGetValue(cardId, out int used);
            if (used >= 3)
                return false;
            _stapleActivations[cardId] = used + 1;
            StapleNote(context, $"activate {CardNames.Get(cardId)}: {reason}");
            _lastStapleActivated = cardId;
            response = card.MainActivateFromIndex();
            return true;
        }

        /// <summary>Records a staple activated outside TryActivateStaple (e.g. chained in the priority window).</summary>
        protected void MarkStapleActivated(int cardId) => _lastStapleActivated = cardId;

        protected static int StaplePriorityIndex(int[] priority, DynamicCard card)
        {
            int index = Array.FindIndex(priority, id => card.IsCode(id));
            return index < 0 ? priority.Length : index;
        }

        // ------------------------------------------------------------------ our Heavy Storm / MST (shared defaults)

        /// <summary>
        /// Default main-phase use of our Heavy Storm and MST for deck brains without their own rules: Heavy Storm when the
        /// opponent has 2+ spells/traps and more than us (counting the Storm itself); MST on an opponent's set card
        /// before attacking.
        /// </summary>
        protected async Task<IdleCmdResponse?> TryStapleIdleAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            var enemySpells = await GetEnemySpellCardsAsync();
            int ourSpells = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && !card.IsCode(HeavyStorm));

            // Free card advantage (Upstart, Charge, Reinforcement), backrow removal (Heavy Storm, Lyla, MST for a big
            // play; otherwise MST is set as bait, see TryChainBaitAsync), Card Trooper.
            if (await TryFreeCardAdvantageAsync(context, state) is IdleCmdResponse free)
                return free;
            if (await TryBackrowRemovalAsync(context, state, enemySpells.Count, ourSpells) is IdleCmdResponse backrow)
                return backrow;
            if (await TryMonsterStaplesAsync(context) is IdleCmdResponse monsterStaple)
                return monsterStaple;
            if (await TryCardTrooperAsync(context, state) is IdleCmdResponse trooper)
                return trooper;
            if (await TryReturnDimensionLethalAsync(context, state) is IdleCmdResponse returnLethal)
                return returnLethal;

            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.StateData.CanBattlePhase &&
                               (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack());
            var setMst = (context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.IsCode(MysticalSpaceTyphoon));
            if (setMst.ID > 0 && !battleAhead && await HoldSetForHeavyStormAsync(context.Header.CurrentTurn, MysticalSpaceTyphoon) == null)
            {
                context.Bot.LogNote("deck brain: set Mystical Space Typhoon as bait for the opponent's removal");
                return setMst.SetSpellTrap();
            }
            return null;
        }
    }
}
