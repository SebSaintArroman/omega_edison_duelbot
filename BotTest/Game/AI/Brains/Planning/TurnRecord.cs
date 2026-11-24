using BotTest.Game.AI.Search;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// The real turn's record for the search (TurnRecord): what our turn has done that the line penalties (LineTally)
    /// depend on, so lines started later in the turn count the same history as the reused tree's older lines (user,
    /// 2026-11-13). Wiped, with the reused trees, only when we draw cards mid-turn or the opponent activates a card or effect
    /// (user: "it should only be drawing cards or opponent card/effect activations that force commitment-wipes"; our own
    /// chains while carrying out the plan are part of it). A wipe also ends a routine being followed.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private int _recordTurn = -1;
        private int _recSets, _recEquips;
        private bool _recBattle, _recLyla, _recFusion, _recDrew;
        private double _recHoldCosts;
        private readonly List<int> _recSummons = new();
        private readonly HashSet<int> _recUsed = new();
        // The opponent's hand and deck sizes at the previous decision of our turn (cards played from their hand, cards out of their deck).
        private int _recEnemyHand = -1, _recEnemyDeck = -1;
        // Their graveyard + banished cards and their face-up field cards at the previous decision of our turn (by id).
        private List<int>? _recEnemyPiles, _recEnemyFaceUp;
        // Our deck size at the previous decision of our turn, and the cards we picked out of it since (a search or a chosen send:
        // known, so not new information). The rest of what left it went unseen: a mill (Ryko's flip, Card Trooper, Charge).
        private int _recOwnDeck = -1, _recDeckPicked;
        // The next search starts a fresh tree (the reused trees hold lines played on outdated information).
        private bool _wipeTrees;

        private void ResetTurnRecord(int turn)
        {
            _recordTurn = turn;
            _recSets = _recEquips = 0;
            _recBattle = _recLyla = _recFusion = _recDrew = false;
            _recHoldCosts = 0;
            _recSummons.Clear();
            _recUsed.Clear();
            _recEnemyHand = _recEnemyDeck = -1;
            _recOwnDeck = -1;
            _recDeckPicked = 0;
            _recEnemyPiles = null;
            _recEnemyFaceUp = null;
        }

        /// <summary>
        /// At every real decision (after the board is read): a new turn starts a new record; in our turn, it's wiped when hidden
        /// information comes into play, which the plan couldn't simulate (user, 2026-11-13):
        /// <list type="number">
        /// <item>we drew cards;</item>
        /// <item>a card came out of their deck: a search (Sangan), a recruit (Shining Angel), a draw or a mill;</item>
        /// <item>a card left their hand for anywhere but their deck: they played it (one sent from their hand to the deck by X-Saber
        /// Airbellum stays unseen and changes nothing for us);</item>
        /// <item>a face-down spell/trap of theirs was activated, or a face-down monster of theirs flipped.</item>
        /// </list>
        /// Their effects that use only known cards (Jinzo - Returner reviving Jinzo when our attack destroys it: "we should have
        /// known that it had to get a jinzo") don't: the search simulates them, so they're part of the plan.
        /// </summary>
        private void ObserveTurnRecord(DuelBotWrapper bot, BotStateHeader header, FieldInfo field)
        {
            if (IsSearchTwin)
                return;
            if (header.CurrentTurn != _recordTurn)
                ResetTurnRecord(header.CurrentTurn);
            if (!IsOwnTurn(header))
                return;
            int enemyHand = field.EnemySide.Hand?.Length ?? 0;
            int enemyDeck = field.EnemySide.Main?.Length ?? 0;
            int handDrop = _recEnemyHand >= 0 ? _recEnemyHand - enemyHand : 0;
            int deckChange = _recEnemyDeck >= 0 ? enemyDeck - _recEnemyDeck : 0;
            int ownDeck = field.OwnSide.Main?.Length ?? -1;
            int unseenFromOurDeck = _recOwnDeck >= 0 && ownDeck >= 0 ? _recOwnDeck - ownDeck - _recDeckPicked : 0;
            string? why = null;
            if (_recDrew)
                why = "we drew cards: a new hand";
            // Cards that left our deck without us choosing them: a mill (seen 2026-11-16: Ryko's flip milled 3, and the next decision's
            // tree, whose lines milled other cards, had no node for it: a DESYNC). New information, like a draw.
            else if (unseenFromOurDeck > 0)
                why = $"{unseenFromOurDeck} card(s) left our deck unseen (a mill): cards the plan couldn't know";
            else if (deckChange < 0)
                why = $"a card came out of their deck ({_recEnemyDeck} -> {enemyDeck}): a search, recruit or draw we can't predict";
            else if (handDrop > Math.Max(0, deckChange))
                why = $"they played a card from their hand ({_recEnemyHand} -> {enemyHand})";
            else if (_newFaceUpSpellZones.Count > 0)
                why = "a face-down spell/trap of theirs was activated";
            else if (_flippedFaceUpZones.Count > 0)
                why = "a face-down monster of theirs was flipped";
            else if (HiddenCardRevealed(field, handDrop) is string revealed)
                why = $"a face-down card of theirs was revealed as it left the field ({revealed})";
            _recEnemyPiles = (field.EnemySide.Grave ?? Array.Empty<int>()).Concat(field.EnemySide.Banish ?? Array.Empty<int>()).Where(id => id > 0).ToList();
            _recEnemyFaceUp = EnemyFaceUpIds(field).ToList();
            _recEnemyHand = enemyHand;
            _recEnemyDeck = enemyDeck;
            _recOwnDeck = ownDeck;
            _recDeckPicked = 0;
            if (why != null)
                WipeTurnRecord(bot, why);
        }

        private static IEnumerable<int> EnemyFaceUpIds(FieldInfo field) =>
            (field.EnemySide.MonsterZones ?? Array.Empty<CardArray>()).Concat(field.EnemySide.SpellZones ?? Array.Empty<CardArray>())
                .SelectMany(z => z.Cards ?? Array.Empty<int>()).Where(id => id > 0);

        /// <summary>
        /// A card that reached their graveyard or banished pile since the previous decision without having been face-up on their
        /// field or played from their hand: a face-down card we didn't know (our MST on a set card: seen 2026-11-15, a set Burial
        /// from a Different Dimension, and the tree that never knew it desynced). Its name, or null.
        /// </summary>
        private string? HiddenCardRevealed(FieldInfo field, int handDrop)
        {
            if (_recEnemyPiles == null || _recEnemyFaceUp == null)
                return null;
            var before = _recEnemyPiles.Concat(_recEnemyFaceUp).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            int fromHand = Math.Max(0, handDrop);
            foreach (int id in (field.EnemySide.Grave ?? Array.Empty<int>()).Concat(field.EnemySide.Banish ?? Array.Empty<int>()).Where(id => id > 0))
            {
                if (before.TryGetValue(id, out int n) && n > 0)
                {
                    before[id] = n - 1;
                    continue;
                }
                if (fromHand-- > 0)
                    continue;
                return CardNames.Get(id);
            }
            return null;
        }

        private void WipeTurnRecord(DuelBotWrapper bot, string why)
        {
            bool battle = _recBattle; // a fact, not a commitment: sets after it are Main Phase 2's
            int enemyHand = _recEnemyHand, enemyDeck = _recEnemyDeck;
            // Keep the observed baseline so the next prompt can detect a mill immediately after a wipe.
            int ownDeck = _recOwnDeck;
            ResetTurnRecord(_recordTurn);
            _recBattle = battle;
            _recEnemyHand = enemyHand;
            _recEnemyDeck = enemyDeck;
            _recOwnDeck = ownDeck;
            _wipeTrees = true;
            bot.LogNote($"search: memory wiped ({why}): this turn's commitments are dropped and the next search starts a fresh tree");
            if (_searchLine != null || _plan != null)
                DropPlan(bot, why);
        }

        /// <summary>Our Lyla's effect sent in a chain window of our turn before the Battle Phase: the record's Lyla flag.</summary>
        private void RecordChainLyla(int card)
        {
            if (IsSearchTwin || !IsOwnTurn(_currentHeader) || _currentHeader.CurrentTurn != _recordTurn || _recBattle ||
                !LineTally.Is(card, LineTally.Lyla))
                return;
            _recLyla = true;
        }

        /// <summary>Our real main-phase move (after it's chosen): what it adds to the record.</summary>
        private async Task RecordMainMoveAsync(DecisionContext<IdleCmdData> context, IdleCmdResponse chosen, int movedCard)
        {
            if (IsSearchTwin || !IsOwnTurn(context.Header))
                return;
            if (context.Header.CurrentTurn != _recordTurn)
                ResetTurnRecord(context.Header.CurrentTurn);
            var choices = SearchActions.Idle(context.StateData);
            string? key = choices.FirstOrDefault(c => c.Response.Action == chosen.Action && c.Response.Index == chosen.Index)?.Key;
            // A move the brain's rules held back: its cost stays with every line of this turn.
            if (key != null && HoldsFor(new[] { key }).TryGetValue(key, out var held))
                _recHoldCosts += held.Cost;
            switch (chosen.Action)
            {
                case MainAction.SetSpell or MainAction.SetMonster when !_recBattle:
                    _recSets++;
                    break;
                case MainAction.Summon or MainAction.SpSummon when movedCard > 0:
                    _recSummons.Add(movedCard);
                    break;
                case MainAction.Activate:
                    _recUsed.Add(movedCard);
                    MarkCardActivated(movedCard); // once-per-turn effects: the search's lines can't use them again (never filled before 2026-11-15)
                    if (key != null && long.TryParse(key[(key.LastIndexOf(':') + 1)..], out long usedDesc))
                        MarkEffectUsed(movedCard, usedDesc);
                    if (!_recBattle && LineTally.Is(movedCard, LineTally.Lyla))
                        _recLyla = true;
                    if (!_recBattle && LineTally.Is(movedCard, LineTally.Unions) && key?.Contains($"@0.{(int)CardLocation.MonsterZone}.") == true)
                        _recEquips++;
                    if (LineTally.Is(movedCard, LineTally.MiracleFusion))
                        _recFusion = true;
                    if (await DrawsCardsAsync(movedCard))
                        _recDrew = true;
                    break;
                case MainAction.ToBattlePhase:
                    _recBattle = true;
                    _recFusion = false;
                    break;
            }
        }

        private static async Task<bool> DrawsCardsAsync(int cardId) =>
            cardId > 0 && await CardDatabase.GetAsync(cardId) is DBCardInfo info && DrawText.IsMatch(info.CardText ?? "");

        /// <summary>A card of ours whose effect we used (a chain or "use this effect?" answer) in our turn.</summary>
        private void RecordEffectUsed(BotStateHeader header, int cardId, long? description = null)
        {
            if (IsSearchTwin || cardId <= 0 || !IsOwnTurn(header) || header.CurrentTurn != _recordTurn)
                return;
            if (description is long desc)
                MarkEffectUsed(cardId, desc);
            MarkCardActivated(cardId);
            _recUsed.Add(cardId);
            if (DrawsCardsAsync(cardId).GetAwaiter().GetResult())
                _recDrew = true;
        }

        public void EffectYnAnswered(SelectEffectYnData data, bool used)
        {
            if (used)
                RecordEffectUsed(_currentHeader, data.EffectCard.ID, data.Description);
        }

        /// <summary>The record as the search's lines start from it.</summary>
        private async Task<TurnRecord> BuildTurnRecordAsync()
        {
            if (_recordTurn != _currentHeader.CurrentTurn || !IsOwnTurn(_currentHeader))
                return TurnRecord.Empty;
            // Summoned this turn, still face-up on the field, not attacked and no effect used: still owes a purpose.
            var pending = new List<int>(_recSummons);
            var idle = new List<int>();
            foreach (var card in (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 && card.IsFaceup() && !card.Attacked && !_recUsed.Contains(card.ID)))
                if (pending.Remove(card.ID))
                    idle.Add(card.ID);
            return new TurnRecord
            {
                SetsBeforeBattle = _recSets, BattleEntered = _recBattle, IdleSummons = idle, LylaBeforeBattle = _recLyla,
                EquipsBeforeBattle = _recEquips, FusionWithoutBattle = _recFusion, HoldCosts = _recHoldCosts,
            };
        }
    }
}
