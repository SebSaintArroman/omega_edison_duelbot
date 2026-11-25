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
    /// What we activated this turn and what a follow-up prompt belongs to (a search, a discard, a target...).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        protected enum PendingActionKind
        {
            None,
            Search,
            Reveal,
            Discard,
            SendToGrave,
            Revive,
            MaterialSelection,
            TargetSelection
        }

        protected readonly struct PendingActionContext
        {
            public MainAction? Action { get; init; }

            public int CardId { get; init; }

            public int Description { get; init; }

            public PendingActionKind Kind { get; init; }

            public int ContextCode { get; init; }
        }
        private readonly HashSet<int> _activatedThisTurn = new();
        // Our effects used this turn, "official id:effect description" (Mcts.UsedEffects).
        private readonly HashSet<string> _usedEffects = new();

        /// <summary>Our card's effects used this turn (_usedEffects), one bit per description index (id*16+index), for the rebuild (SearchPosition).</summary>
        private int UsedEffectMask(OmegaBotWrapper.Struct.DynamicCard card)
        {
            if (card.ID <= 0)
                return 0;
            string id = (Search.SearchHost.CanonicalId(card.ID) is > 0 and var official ? official : card.ID) + ":";
            int mask = 0;
            foreach (string used in _usedEffects)
                if (used.StartsWith(id, StringComparison.Ordinal) && long.TryParse(used[id.Length..], out long desc) && desc >= 16)
                    mask |= 1 << (int)(desc & 15);
            return mask;
        }

        private void MarkEffectUsed(int cardId, long description)
        {
            if (cardId > 0)
                _usedEffects.Add($"{(Search.SearchHost.CanonicalId(cardId) is > 0 and var official ? official : cardId)}:{description}");
        }
        private readonly List<int> _chainDescriptions = new();

        protected PendingActionContext PendingAction { get; private set; }

        protected void SetPendingAction(PendingActionKind kind, int cardId = 0, MainAction? action = null, int description = 0, int contextCode = 0)
        {
            PendingAction = new PendingActionContext
            {
                Kind = kind,
                CardId = cardId,
                Action = action,
                Description = description,
                ContextCode = contextCode
            };
        }

        protected void ClearPendingAction() => PendingAction = default;

        protected void MarkCardActivated(int cardId)
        {
            if (cardId <= 0)
                return;
            _activatedThisTurn.Add(cardId);
            // Live Omega sends variant ids (Brain Control as 87910998, alias 87910978): the official one too, which the checks use (seen
            // 2026-11-18: a Fortress taken by Brain Control wasn't known to go back, and the search tributed the token instead).
            if (Search.SearchHost.Available && Search.SearchHost.CanonicalId(cardId) is > 0 and var official)
                _activatedThisTurn.Add(official);
        }

        protected bool WasCardActivatedThisTurn(int cardId) => _activatedThisTurn.Contains(cardId);

        protected void RegisterChainDescriptions(IEnumerable<int> descriptions)
        {
            _chainDescriptions.Clear();
            _chainDescriptions.AddRange(descriptions);
        }

        protected bool IsFirstTurnOrMain2(BotStateHeader header) =>
            header.CurrentTurn <= 1 || (DuelPhase)header.CurrentPhase == DuelPhase.Main2;

        protected bool PendingContextIs(int contextCode) => PendingAction.ContextCode == contextCode;
    }
}
