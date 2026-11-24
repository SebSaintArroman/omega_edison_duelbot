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
    /// Main Phase decisions (the generic part, before the deck brain and the default scorer). DecideGenericMainPhaseAsync
    /// lists the rules in the order they are applied (each rule is in MainPhase/Rules).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default) =>
            await FlipBeforeSummonAsync(context, await DecideGenericMainPhaseAsync(context, ct));

        /// <summary>
        /// The generic Main Phase decision. The steps, in order:
        ///  1. options the rules take away (each holds what it removes, so a search line that does it anyway pays the
        ///     rule's cost): KeepGorzLive, NoSetIntoBouncer;
        ///  2. the staples (TryStapleIdleAsync), unless the deck brain plays them itself;
        ///  3. more options taken away: HoldSetsForBattle, HoldSetsForStorm, NoRedundantSets;
        ///  4. battle positions: RepositionToDefence, RepositionToAttack;
        ///  5. SkipEmptyBattlePhase;
        ///  6. Chimeratech Fortress Dragon from our own Cyber Dragon before the turn ends (TryChimeratechSafetyAsync);
        ///  7. the default scorer, with what's left.
        /// Each rule is in MainPhase/Rules.
        /// </summary>
        private async Task<IdleCmdResponse> DecideGenericMainPhaseAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnIdleCmd");
            // The deck brain may already have logged the Heavy Storm hold for this prompt: don't repeat it.
            bool stormHoldAlreadyNoted = StormHoldNoted;
            StormHoldNoted = false;
            // D.D. Crow and co. are never summoned (their value is a hand effect).
            context = context with { StateData = WithoutNeverSummons(context.StateData) };
            var state = context.StateData;
            var allowedSets = new List<DynamicCard>();
            foreach (var card in state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                if (await TaggedMonsterSetAllowedAsync(card))
                    allowedSets.Add(card);
            state.MonsterSetableCards = allowedSets.ToArray();
            context = context with { StateData = state };

            var opponentMonsters = (await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList();
            await RefreshRewardOnDestroyAsync(opponentMonsters);
            await RefreshBackrowOddsAsync(context);

            // 1.
            context = await NoSetsWhileGorzIsLiveAsync(context, opponentMonsters);
            context = NoMonsterSetsIntoBouncer(context);

            // 2.
            if (!HandlesStapleIdle && await TryStapleIdleAsync(context) is IdleCmdResponse staple)
                return staple;

            // 3.
            context = await SetsWaitForBattleAsync(context);
            context = await HoldSetsForStormAsync(context, stormHoldAlreadyNoted);
            context = await DropRedundantSetsAsync(context);

            // 4.
            _lowLpExposure = await LowLpExposureLimitAsync();
            if (RepositionToDefence(context, opponentMonsters) is IdleCmdResponse toDefence)
                return toDefence;
            if (await RepositionToAttackAsync(context, opponentMonsters) is IdleCmdResponse toAttack)
                return toAttack;

            // 5.
            if (await SkipEmptyBattlePhaseAsync(context, ct) is IdleCmdResponse noBattle)
                return noBattle;

            // 6. Our face-up Cyber Dragon next to a face-up Machine at the end of our turn: fuse it ourselves.
            bool battleStillAhead = (DuelPhase)context.Header.CurrentPhase == DuelPhase.Main1 && context.StateData.CanBattlePhase &&
                                    (await GetBotMonsterCardsAsync()).Any(card => card.IsFaceup() && card.IsAttack());
            if (await TryChimeratechSafetyAsync(context, context.StateData, battleStillAhead) is IdleCmdResponse safety)
                return safety;

            // 7.
            return await base.OnIdleCmdAsync(context, ct);
        }

        /// <summary>
        /// Set by a deck brain that already logged its "don't set: Heavy Storm" hold in this prompt, so the shared rule
        /// doesn't repeat it. The deck brain clears it at the start of each prompt; the shared rule clears it once read.
        /// </summary>
        protected bool StormHoldNoted { get; set; }
    }
}
