using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace OmegaBotWrapper.Heuristics
{
    /// <summary>
    /// A brain that can look again at its own main-phase and battle answers before they're sent (e.g. by playing the
    /// turn forward in a simulator). Called after the brain's answer, so every deck override is covered.
    /// </summary>
    public interface IDecisionRefiner
    {
        /// <summary>A main-phase or battle decision starts (before the brain is asked).</summary>
        void BeginDecision();

        Task<IdleCmdResponse> RefineIdleAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse brainChoice, CancellationToken ct = default);
        Task<BattleCmdResponse> RefineBattleAsync(DecisionContext<BattleCmdData> context, BattleCmdResponse brainChoice, CancellationToken ct = default);

        /// <summary>
        /// The planned answer to a card selection that follows a refined decision (e.g. the discards and targets of the
        /// effect the search chose), or null to let the brain choose.
        /// </summary>
        IList<byte>? PlannedCards(BotState state, DynamicCard[] cards, int minimum, int maximum, bool cancelable);

        /// <summary>The planned answer to a yes/no (effect or question) that follows a refined decision, or null.</summary>
        bool? PlannedYesNo(BotState state);

        /// <summary>The planned answer to an optional effect (the brain may insist on one: a trigger it always uses), or null.</summary>
        bool? PlannedEffectYn(SelectEffectYnData data) => PlannedYesNo(BotState.SelectEffectYn);

        /// <summary>An optional effect the deck always uses (Black Whirlwind): no search or plan may decline it.</summary>
        bool AlwaysUses(SelectEffectYnData data) => false;

        /// <summary>The planned option (index into <paramref name="options"/>) that follows a refined decision, or null.</summary>
        int? PlannedOption(int[] options);

        /// <summary>
        /// The turn plan's next main-phase step, asked before the brain: when a plan is being followed and its step is on
        /// offer, that is the move (the brain's rules had their say when the plan was chosen). Null: the brain decides.
        /// </summary>
        Task<IdleCmdResponse?> PlannedIdleAsync(DecisionContext<IdleCmdData> context) => Task.FromResult<IdleCmdResponse?>(null);

        /// <summary>
        /// A chain prompt after the brain answered it (<paramref name="brainIndex"/>): the refined answer (the play search's, when
        /// it replans mid-chain), or null to keep the brain's.
        /// </summary>
        Task<int?> RefineChainAsync(DecisionContext<SelectChainData> context, int brainIndex, CancellationToken ct = default) => Task.FromResult<int?>(null);

        /// <summary>A card pick (SelectCard / SelectTribute / SelectUnselectCard data), before the plan and the brain: the refined answer
        /// (the play search re-planning at the pick when something new was revealed), or null.</summary>
        Task<IList<byte>?> RefinePickAsync(BotState state, object data, CancellationToken ct = default) => Task.FromResult<IList<byte>?>(null);

        /// <summary>Any answer we sent (the prompt's state, its data, the bytes): the search replays our turn from these.</summary>
        void ResponseSent(BotState state, object? data, byte[] response) { }

        /// <summary>A new turn seen (the first poll that shows it, before anything in it is answered): the brain's turn-start snapshot.</summary>
        Task TurnStartedAsync(DuelBotWrapper bot, BotStateHeader header) => Task.CompletedTask;

        /// <summary>A position required by the brain's rules, before a plan or search can choose.</summary>
        CardPosition? RequiredSummonPosition(SelectPositionData state) => null;

        /// <summary>The plan's choice at a prompt answered by keys, or null when no plan covers it.</summary>
        string? PlannedChoice(BotState state, IReadOnlyList<string> options) => null;

        /// <summary>The search's choice among the given keys, or null to let the brain answer.</summary>
        Task<string?> SearchedChoiceAsync(BotState state, IReadOnlyList<string> options, CancellationToken ct = default) => Task.FromResult<string?>(null);

        /// <summary>Any prompt, before it is answered (by the brain, a plan's step, or a replay): the brain's turn record.</summary>
        Task PromptArrivedAsync(DuelBotWrapper bot, BotStateHeader header, BotState state) => Task.CompletedTask;

        /// <summary>Our answer to a chain prompt is being sent (index into the selectable cards, -1 for no chain).</summary>
        void ChainAnswered(DecisionContext<SelectChainData> context, int index) { }

        /// <summary>Our answer to a "use this effect?" prompt is being sent.</summary>
        void EffectYnAnswered(SelectEffectYnData data, bool used) { }

        /// <summary>The turn plan's next battle step, asked before the brain (see PlannedIdleAsync). Null: the brain decides.</summary>
        Task<BattleCmdResponse?> PlannedBattleAsync(DecisionContext<BattleCmdData> context) => Task.FromResult<BattleCmdResponse?>(null);
    }
}
