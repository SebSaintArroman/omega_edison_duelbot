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
    /// Return from the Different Dimension (shared staple, first in BlackwingHybrid, 2026-11-10): pay half our LP, Special
    /// Summon as many of our banished monsters as possible; they're banished again in the End Phase. Two uses only (user,
    /// 2026-11-12):
    ///  1. our turn, Main Phase 1 or the Battle Phase: the returned monsters attack for lethal (what the card is for);
    ///  2. much more rarely, their Battle Phase: the returned monsters block a lethal attack (a last resort).
    /// Never for card advantage: the monsters go away in the End Phase whichever turn it is (round-robin 45 / 49: their End
    /// Phase use paid 1125 and 3600 LP, and the monsters were banished again).
    /// Monsters to keep are picked by ATK when there are more than free zones.
    /// </summary>
    public abstract partial class DeckBotBrain
    {

        /// <summary>Our banished monsters Return from the Different Dimension would bring back (as many as our free zones), best ATK first.</summary>
        private async Task<List<DynamicCard>> ReturnDimensionPreviewAsync()
        {
            int freeZones = 5 - (await GetBotMonsterCardsAsync()).Count(card => card.ID > 0 || card.CurrentCoordinates.Location != 0);
            if (freeZones <= 0)
                return new List<DynamicCard>();
            return (await CurrentField.GetCardsInLocation(CardLocation.Removed))
                .Where(card => card.ID > 0 && card.CurrentCoordinates.Controller == 0 && card.HasType(CardType.Monster) && card.IsFaceup())
                .OrderByDescending(card => card.Attack)
                .Take(freeZones)
                .ToList();
        }

        /// <summary>
        /// The damage these attackers deal through these monsters: each monster takes the weakest attacker that beats it
        /// (a face-down counts as 2000), the rest hit directly. Null when a monster can't be beaten (then no direct hits).
        /// </summary>
        private static int? DamageThrough(List<int> attackers, IEnumerable<(int Power, bool AttackPosition)> blockers)
        {
            var left = attackers.OrderBy(atk => atk).ToList();
            int damage = 0;
            foreach (var (power, attackPosition) in blockers.OrderByDescending(b => b.Power))
            {
                int index = left.FindIndex(atk => atk > power);
                if (index < 0)
                    return null;
                if (attackPosition)
                    damage += left[index] - power;
                left.RemoveAt(index);
            }
            return damage + left.Sum();
        }

        private static (int Power, bool AttackPosition) AsBlocker(DynamicCard card) =>
            card.IsFaceup() ? (card.GetRealPower(), card.IsAttack()) : (2000, false);

        /// <summary>Use 1: the returned monsters, with our attackers still to attack, are lethal through their monsters.</summary>
        private async Task<string?> ReturnDimensionLethalWhyAsync()
        {
            var back = await ReturnDimensionPreviewAsync();
            if (back.Count == 0)
                return null;
            var attackers = (await GetBotMonsterCardsAsync())
                .Where(card => card.ID > 0 && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(EffectiveAttack)
                .Concat(back.Select(card => card.Attack))
                .ToList();
            var theirs = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID > 0 || card.CurrentCoordinates.Location != 0).Select(AsBlocker);
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            if (DamageThrough(attackers, theirs) is not int damage || damage < oppLp || !Rule(DeckRules.ReturnDimensionLethal))
                return null;
            return $"{back.Count} back ({string.Join(", ", back.Select(card => CardNames.Get(card.ID)))}), {damage} damage against their {oppLp} LP is lethal";
        }

        /// <summary>Use 1 in Main Phase 1 (Battle Phase ahead).</summary>
        protected async Task<IdleCmdResponse?> TryReturnDimensionLethalAsync(DecisionContext<IdleCmdData> context, IdleCmdData state)
        {
            if (!TryGetActivable(state, ReturnFromTheDifferentDimension, out _) ||
                (DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || context.Header.CurrentTurn <= 1)
                return null;
            if (await ReturnDimensionLethalWhyAsync() is not string why)
                return null;
            StapleNote(context, $"activate Return from the Different Dimension: {why}");
            TryActivateStaple(context, state, ReturnFromTheDifferentDimension, "lethal", out var response);
            return response;
        }

        /// <summary>Use 1 in our Battle Phase (some attacks may already be behind us).</summary>
        private async Task<BattleCmdResponse?> TryReturnDimensionLethalInBattleAsync(DecisionContext<BattleCmdData> context)
        {
            var card = (context.StateData.ActivableCards ?? Array.Empty<DynamicCard>()).FirstOrDefault(c => c.IsCode(ReturnFromTheDifferentDimension));
            if (card.ID <= 0 || card.ActionActivateIndex is not { Length: > 0 } activations)
                return null;
            if (await ReturnDimensionLethalWhyAsync() is not string why)
                return null;
            StapleNote(context, $"activate Return from the Different Dimension in the Battle Phase: {why}");
            MarkStapleActivated(ReturnFromTheDifferentDimension);
            return (BattleAction.Activate, (ushort)activations[0].ActionIndex);
        }

        /// <summary>
        /// Use 2, a last resort: their Battle Phase, their attacks still to come are lethal on our board, and with the
        /// returned monsters as blockers (summoned in defence on their turn) and our LP halved they no longer are. Returns the
        /// chain index, or null.
        /// </summary>
        protected async Task<int?> TryChainReturnDimensionAsync(DecisionContext<SelectChainData> context, DynamicCard[] options, bool botTurn)
        {
            int index = Array.FindIndex(options, card => card.IsCode(ReturnFromTheDifferentDimension) && card.CurrentCoordinates.Controller == 0);
            var phase = (DuelPhase)context.Header.CurrentPhase;
            if (index < 0 || botTurn || phase is not (DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.Battle))
                return null;
            var back = await ReturnDimensionPreviewAsync();
            if (back.Count == 0)
                return null;
            var attackers = (await GetEnemyMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Select(card => card.Attack).ToList();
            if (attackers.Count == 0)
                return null;
            var ours = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0 || card.CurrentCoordinates.Location != 0).Select(AsBlocker).ToList();
            int lp = await CurrentField.GetBotLifePoints();
            if (DamageThrough(attackers, ours) is not int lethalNow || lethalNow < lp)
                return null;
            // Returned monsters go in defence on their turn (OnSelectPositionAsync): blockers that take no damage.
            var blockers = ours.Concat(back.Select(card => (card.Defense, false))).ToList();
            int lpAfter = lp - lp / 2;
            int? after = DamageThrough(attackers, blockers);
            if (after is int stillLethal && stillLethal >= lpAfter)
            {
                StapleNote(context, $"their attacks are lethal ({lethalNow} vs our {lp} LP), and Return from the Different Dimension doesn't stop it " +
                                    $"({stillLethal} vs {lpAfter} after paying): keep it");
                return null;
            }
            if (!Rule(DeckRules.ReturnDimensionBlockLethal))
                return null;
            StapleNote(context, $"last resort: their attacks are lethal ({lethalNow} vs our {lp} LP); Return from the Different Dimension brings back " +
                                $"{back.Count} blocker(s) ({string.Join(", ", back.Select(card => CardNames.Get(card.ID)))}), leaving {after ?? 0} damage against {lpAfter} LP");
            MarkStapleActivated(ReturnFromTheDifferentDimension);
            return index;
        }

        /// <summary>More banished monsters than free zones: the biggest ATK come back.</summary>
        private IList<byte>? ReturnDimensionPick(DecisionContext<SelectCardData> context)
        {
            if (_lastStapleActivated != ReturnFromTheDifferentDimension || (DHint)context.StateData.SelectionHint != DHint.SPSUMMON)
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0 || cards.Any(card => card.CurrentCoordinates.Location != (byte)CardLocation.Removed || card.CurrentCoordinates.Controller != 0))
                return null;
            _lastStapleActivated = 0;
            int count = Math.Max(context.StateData.Minimum, Math.Min(context.StateData.Maximum, cards.Length));
            var picks = cards.Select((card, i) => (Card: card, Index: i)).OrderByDescending(x => x.Card.Attack).Take(count).ToList();
            context.Bot.LogNote($"deck brain: Return from the Different Dimension brings back {string.Join(", ", picks.Select(x => CardNames.Get(x.Card.ID)))} (biggest ATK)");
            return picks.Select(x => (byte)x.Index).ToList();
        }
    }
}
