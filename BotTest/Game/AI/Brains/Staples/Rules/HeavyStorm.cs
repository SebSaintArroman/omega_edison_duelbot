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
    /// RULE deck.heavy-storm: when our Heavy Storm is worth it (HeavyStormReasonAsync).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Heavy Storm is for pushing big plays through, not for the first chance to use it. Worth it when:
        ///  1. card advantage: the opponent loses at least 2 more spells/traps than we do (the Storm is a card too), or
        ///  2. a big play is ready this turn and their backrow could stop it (even if they only have one card).
        /// Returns the reason, or null to hold it.
        /// </summary>
        protected async Task<string?> HeavyStormReasonAsync(DecisionContext<IdleCmdData> context, int theirs, int ours)
        {
            if (theirs == 0)
                return null;
            // Round-robin 35 (Machina Jinzo held Heavy Storm all game, "opponent 1 vs our 2"): our own set bait (Jar of Greed, MST,
            // Waboku...) is chained in response to our Storm, so it isn't lost; only the rest counts.
            int baitChained = (await CurrentField.GetCardsInSpellZone()).Count(card => card.ID > 0 && !card.IsFaceup() && !card.IsCode(HeavyStorm) && IsStormFree(card.ID));
            int lost = Math.Max(0, ours - baitChained);
            // And a Storm held through two of our turns with targets on their side makes way: an even trade of backrow plus the
            // Storm itself is then fine (a card that never gets used is worth nothing).
            int turn = context.Header.CurrentTurn;
            if (_stormHeldSince < 0 || turn < _stormHeldSince)
                _stormHeldSince = turn;
            int margin = turn - _stormHeldSince >= 4 ? 1 : 2;
            if (theirs >= lost + margin)
            {
                _stormHeldSince = -1;
                return $"card advantage: opponent loses {theirs} spells/traps, we lose {lost} (+ the Storm)" +
                       (baitChained > 0 ? $"; our {baitChained} set bait card(s) are chained in response, not lost" : "") +
                       (margin == 1 ? "; held two turns already, so an even trade will do" : "");
            }
            var bigPlay = await BigPlayPendingAsync(context);
            // Our set Solemn Judgment already pushes a play through one of their cards: with a single set card of theirs, the
            // Storm adds nothing for the push and costs our own backrow (user, 2026-11-11: "solemn does that too").
            if (bigPlay != null && theirs <= 1 && await SolemnReadyAsync() && Rule(DeckRules.StormNotWithSolemn))
            {
                context.Bot.LogNote($"deck brain: hold Heavy Storm: our set Solemn Judgment already gets the big play past their one spell/trap");
                Hold(HeavyStorm, MainAction.Activate, DeckRules.StormNotWithSolemn);
                return null;
            }
            if (bigPlay != null)
            {
                CommitToBigPlay(context.Header.CurrentTurn);
                return $"clear their {theirs} spell/trap(s) for a big play: {bigPlay} (committing to it this turn)";
            }
            context.Bot.LogNote($"deck brain: hold Heavy Storm: opponent {theirs} vs our {ours} spells/traps, and no big play to push through");
            Hold(HeavyStorm, MainAction.Activate, DeckRules.HeavyStorm);
            return null;
        }

        // The turn we first held Heavy Storm with targets on their side (reset when it's used).
        private int _stormHeldSince = -1;
    }
}
