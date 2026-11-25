using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Whether they've used their Normal Summon / Set this turn (user, 2026-11-25: "record it"): a card in their hand that needs a
    /// Tribute Summon (Tytannial) keeps its full value only while they still can (CardRuleRegistry.TributableHandCorrection). Read on
    /// their turn at each prompt of ours: a monster of theirs that wasn't in its zone at the turn's start, not from the Extra Deck and not
    /// Special Summoned (face-up, or a Set face-down one), was Normal Summoned or Set.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private int _theirNormalSummonTurn = -1;

        /// <summary>They've Normal Summoned or Set this turn (their turn only: on ours, their next Normal Summon is still to come).</summary>
        protected internal bool TheirNormalSummonUsed(BotStateHeader header) =>
            _theirNormalSummonTurn == header.CurrentTurn && _ownTurnParity >= 0 && !IsOwnTurn(header);

        private async Task TrackTheirNormalSummonAsync(DuelBotWrapper bot, BotStateHeader header)
        {
            if (_ownTurnParity < 0 || IsOwnTurn(header) || _theirNormalSummonTurn == header.CurrentTurn ||
                _turnStart is not { } start || start.Turn != header.CurrentTurn)
                return;
            var before = start.View.EnemyMonsters.ToDictionary(card => card.Sequence, card => card.Code);
            foreach (var card in await GetEnemyMonsterCardsAsync())
            {
                int zone = card.CurrentCoordinates.Sequence;
                // The zone held a card at the turn's start and it's still the same one as far as we can tell (a face-down one flipped is the
                // same card: a Flip Summon isn't a Normal Summon).
                bool wasThere = before.TryGetValue(zone, out int code) && !(code > 0 && card.ID > 0 && code != card.ID);
                if (wasThere || card.IsSpecialSummoned)
                    continue;
                bool extraDeck = card.ID > 0 && (card.HasType(CardType.Fusion) || card.HasType(CardType.Synchro) || card.HasType(CardType.Xyz));
                if (extraDeck)
                    continue;
                _theirNormalSummonTurn = header.CurrentTurn;
                bot.LogNote($"deck brain: their Normal Summon this turn: {(card.ID > 0 ? OmegaBotWrapper.Logging.CardNames.Get(card.ID) : "a set monster")} in M{zone + 1} " +
                            "(new since the turn's start, not Special Summoned)");
                return;
            }
        }
    }
}
