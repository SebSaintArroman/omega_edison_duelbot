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
        /// <summary>Graveyard cards (sequence, id) our revives targeted in the chain being built; cleared at our next
        /// main/battle prompt (the chain has resolved by then) and each turn.</summary>
        private (int Turn, HashSet<(int Sequence, int Id)> Slots) _claimedRevives = (-1, new HashSet<(int, int)>());

        /// <summary>The chain is over: its revive targets no longer block a pick.</summary>
        private void ClearClaimedRevives() => _claimedRevives.Slots.Clear();

        /// <summary>
        /// RULE deck.no-revive-grave-effect
        /// Reviving one of our own monsters (Call of the Haunted, Premature Burial, Monster Reborn on our GY) with no plan for
        /// it: not one whose effect works from the graveyard (user, 2026-11-16: "a penalty for selecting a monster with a
        /// graveyard effect from it. that's just terrible play" - Call of the Haunted on Shield Warrior). Those are held, so a
        /// line that revives one pays for it, even when it's the only choice. Otherwise the biggest.
        /// </summary>
        private async Task<IList<byte>?> RevivePickAsync(DecisionContext<SelectCardData> context)
        {
            if (context.StateData.SelectionHint != (int)DHint.SPSUMMON || context.StateData.Maximum != 1 ||
                context.StateData.SelectableCards is not { Length: > 0 } revivable ||
                !revivable.All(card => card.CurrentCoordinates.Controller == 0 && card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.ID > 0))
                return null;
            // Revives already targeted by our earlier links of the chain being built (Future Fusion sending several Jinzo -
            // Returners at once, user 2026-11-24: "they should not target a jinzo that has already been targeted"): a second
            // link onto the same card fizzles. Those are skipped while another copy is left.
            if (_claimedRevives.Turn != context.Header.CurrentTurn)
                _claimedRevives = (context.Header.CurrentTurn, new HashSet<(int, int)>());
            var claimed = _claimedRevives.Slots;
            var ranked = new List<(DynamicCard Card, int Index, bool GraveEffect, bool Claimed)>();
            for (int i = 0; i < revivable.Length; i++)
                ranked.Add((revivable[i], i, await CardDatabase.GetAsync(revivable[i].ID) is DBCardInfo info && EffectEvaluator.HasGraveyardEffect(info.CardText ?? ""),
                    claimed.Contains((revivable[i].CurrentCoordinates.Sequence, revivable[i].ID))));
            var pick = ranked.OrderBy(x => x.Claimed).ThenBy(x => x.GraveEffect).ThenByDescending(x => x.Card.Attack).First();
            if (ranked.Any(x => x.Claimed))
                context.Bot.LogNote($"deck brain: revive skips {string.Join(", ", ranked.Where(x => x.Claimed).Select(x => $"{CardNames.Get(x.Card.ID)} (GY {x.Card.CurrentCoordinates.Sequence})"))}: " +
                                    "already targeted by an earlier link of this chain" + (pick.Claimed ? " (no other copy left)" : ""));
            claimed.Add((pick.Card.CurrentCoordinates.Sequence, pick.Card.ID));
            if (Rule(DeckRules.NoReviveGraveEffect))
                foreach (var held in ranked.Where(x => x.GraveEffect).Select(x => x.Card.ID).Distinct())
                    HoldPick(held, DeckRules.NoReviveGraveEffect);
            context.Bot.LogNote($"deck brain: revive {CardNames.Get(pick.Card.ID)}" +
                                (pick.GraveEffect ? " (the only choice: its graveyard effect is lost; the search's lines pay for it)" : ""));
            return new List<byte> { (byte)pick.Index };
        }
    }
}
