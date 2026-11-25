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
        /// The attack target prompt (after an attack was declared): the target the battle decision planned
        /// (_selectedDefenderCard), matched by card and zone (a face-down one by zone only). With no planned target on offer,
        /// a deliberate pick: what the attacker beats, the biggest threat first; else what costs the least damage.
        /// Also RULE deck.no-steal-recruiter (target side): Goyo-type attackers hold their recruiters as targets, so a line
        /// that takes one pays for it (user, 2026-11-17).
        /// </summary>
        private IList<byte>? AttackTargetPick(DecisionContext<SelectCardData> context)
        {
            if (context.StateData.SelectionHint != (int)DHint.ATTACKTARGET)
                return null;
            ConsoleUtils.PrintColored("OnSelectCard - Selecting Defender");
            if (StealsWhatItDestroys(_declaredAttacker))
                foreach (var offeredTarget in context.StateData.SelectableCards ?? Array.Empty<DynamicCard>())
                    if (offeredTarget.ID > 0 && offeredTarget.IsFaceup() && IsRecruiter(offeredTarget) && !StealSpeedsUpClock(_declaredAttacker, offeredTarget) &&
                        Rule(DeckRules.NoStealRecruiter))
                        HoldPick(offeredTarget.ID, DeckRules.NoStealRecruiter);
            HoldRecruiterTargets(context); // Battle/Rules/KillBeforeRecruiter.cs

            if (_selectedDefenderCard.HasValue)
            {
                var selectedDefender = _selectedDefenderCard.Value;
                if ((context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()).TryGetByIDWithZone(selectedDefender.ID, selectedDefender.CurrentCoordinates.Sequence, out _, out int selectedIndex))
                {
                    ConsoleUtils.PrintColored($"Selecting defender by card code and sequence: {selectedDefender.ID} / {selectedDefender.CurrentCoordinates.Sequence}");
                    _battledThisTurn.Add((context.Header.CurrentTurn, selectedDefender.CurrentCoordinates.Sequence, selectedDefender.ID));
                    return new List<byte> { (byte)selectedIndex };
                }

                // Face-down targets show no id to the bot, so match on the opponent's zone instead: only a face-down card
                // there (seen 2026-11-10: an aim at "the face-down monster in zone 2" took the face-up Gorz in that zone).
                int zoneIndex = Array.FindIndex(context.StateData.SelectableCards ?? Array.Empty<DynamicCard>(),
                    card => card.CurrentCoordinates.Controller != 0 && card.CurrentCoordinates.Sequence == selectedDefender.CurrentCoordinates.Sequence &&
                            (card.ID <= 0 || !card.IsFaceup()));
                if (zoneIndex >= 0)
                {
                    ConsoleUtils.PrintColored($"Selecting defender by zone: {selectedDefender.CurrentCoordinates.Sequence}");
                    return new List<byte> { (byte)zoneIndex };
                }
            }
            // No planned target on offer (an attack lock like Dupe Frog's, or the board changed): pick on purpose, and say
            // why (never silently: round-robin 1 / 21). What the attacker beats, the biggest threat first; else what costs
            // the least damage.
            var offered = (context.StateData.SelectableCards ?? Array.Empty<DynamicCard>()).Select((card, i) => (Card: card, Index: i)).ToList();
            if (offered.Count == 0)
                return null;
            int atk = _declaredAttacker.ID > 0 ? _declaredAttacker.Attack : 0;
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            var pick = offered.Where(x => atk > Power(x.Card)).OrderByDescending(x => TargetThreat(x.Card)).Select(x => (int?)x.Index).FirstOrDefault()
                       ?? offered.OrderBy(x => Math.Max(0, Power(x.Card) - atk) * (x.Card.IsFaceup() && x.Card.IsAttack() ? 1 : 0))
                           .ThenBy(x => Power(x.Card)).First().Index;
            var chosen = offered[pick].Card;
            string planned = _selectedDefenderCard is DynamicCard p ? $"the planned target ({DescribeTarget(p)} in zone {p.CurrentCoordinates.Sequence + 1}) isn't on offer" : "no target was planned";
            AimAt(context.Bot, chosen, AimKind.Fallback, $"attack target: {planned}; " +
                (atk > Power(chosen) ? $"{DescribeTarget(chosen)} is the best one {CardNames.Get(_declaredAttacker.ID)} ({atk}) beats"
                                     : $"{DescribeTarget(chosen)} costs the least ({CardNames.Get(_declaredAttacker.ID)} {atk} beats none of {offered.Count})"));
            _battledThisTurn.Add((context.Header.CurrentTurn, chosen.CurrentCoordinates.Sequence, chosen.ID));
            return new List<byte> { (byte)pick };
        }
    }
}
