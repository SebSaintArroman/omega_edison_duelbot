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
    /// A big play pending this turn (a strong summon, a synchro): the cards committed to it, and whether one is coming.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private int _bigPlayTurn = -1;
        private readonly HashSet<int> _committedBigPlayCards = new();

        /// <summary>The cards making up the big play found by the last BigPlayPendingAsync call (summons, activations, synchros).</summary>
        protected List<int> PendingBigPlayCards { get; } = new();

        /// <summary>
        /// Backrow removal was spent to push the pending big play through this turn: the plays that made it up should
        /// follow (the same conditions that justified the removal), not every other summon.
        /// </summary>
        protected void CommitToBigPlay(int turn, bool keepExisting = false)
        {
            if (!keepExisting || _bigPlayTurn != turn)
                _committedBigPlayCards.Clear();
            _bigPlayTurn = turn;
            _committedBigPlayCards.UnionWith(PendingBigPlayCards.Where(id => id > 0));
        }

        /// <summary>
        /// Whether this play is part of the big play we cleared the backrow for this turn: the card itself, or a synchro
        /// using one of its cards as material (e.g. the synchro after Plaguespreader).
        /// </summary>
        protected bool CommittedToBigPlay(BotStateHeader header, int cardId, IEnumerable<DynamicCard>? materials = null) =>
            _bigPlayTurn == header.CurrentTurn &&
            (_committedBigPlayCards.Contains(cardId) || (materials ?? Enumerable.Empty<DynamicCard>()).Any(m => _committedBigPlayCards.Contains(m.ID)));

        /// <summary>A monster at least this big counts as a strong monster for "big play" purposes.</summary>
        [Tunable("A monster at least this big counts as a strong monster for \"big play\" purposes.")]
        public int StrongMonsterAttack { get; set; } = 2000;

        /// <summary>
        /// A play worth protecting from the opponent's backrow this turn: our current board is lethal (Main Phase 1),
        /// or we can summon two or more strong monsters this turn. Default counts the strong monsters we can Special
        /// Summon or Normal Summon right now; deck brains can count their own combos.
        /// </summary>
        protected virtual async Task<string?> BigPlayPendingAsync(DecisionContext<IdleCmdData> context)
        {
            PendingBigPlayCards.Clear();
            bool battleAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.Header.CurrentTurn > 1 &&
                               context.StateData.CanBattlePhase;
            if (battleAhead)
            {
                var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).Select(card => card.Attack);
                var board = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack()).ToList();
                var defenders = BoardAssessment.Defenders(await GetEnemyMonsterCardsAsync(), OpponentMemory);
                int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
                // Only worth clearing the backrow if what attacks is worth protecting: if they trap an Armageddon
                // Knight or Dark Grepher, that's a trade we're happy with, and the Storm is saved for a real play.
                if (BoardAssessment.TurnsToWin(attackers, defenders, oppLp) == 1 && board.Any(card => !IsExpendable(card)))
                    return "our board is lethal this turn";
            }

            int strong = await StrongSummonsAvailableAsync(context);
            return strong >= 2 ? $"we can summon {strong} strong ({StrongMonsterAttack}+ ATK) monsters this turn" : null;
        }

        /// <summary>
        /// How many strong monsters we can bring out this turn. Default: distinct strong Special Summons on offer plus
        /// one strong Normal Summon. Deck brains override with their own combos (materials, hand costs).
        /// </summary>
        protected virtual Task<int> StrongSummonsAvailableAsync(DecisionContext<IdleCmdData> context)
        {
            var state = context.StateData;
            var special = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.ID > 0 && card.Attack >= StrongMonsterAttack)
                .Select(card => card.ID).Distinct().ToList();
            var normal = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .FirstOrDefault(card => card.ID > 0 && card.Attack >= StrongMonsterAttack);
            PendingBigPlayCards.Clear();
            PendingBigPlayCards.AddRange(special);
            if (normal.ID > 0)
                PendingBigPlayCards.Add(normal.ID);
            return Task.FromResult(special.Count + (normal.ID > 0 ? 1 : 0));
        }
    }
}
