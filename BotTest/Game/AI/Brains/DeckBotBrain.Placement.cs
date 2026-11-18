using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Monster zone placement. The server always attacks with the first monster in zone order (see
    /// ChooseWithServerAttackOrderAsync), so summon each monster into the zone that puts our board in the order we'd
    /// want to attack in:
    ///  - into opponent monsters: monsters with a "destroys a monster by battle" effect first (Goyo takes the biggest
    ///    thing it beats), then largest first, so the smaller ones are left for direct hits (a Gorz token copies the
    ///    damage, so it comes out smaller);
    ///  - into an empty field: smallest first (a hand trap spends itself on the least valuable attacker), AttackLast
    ///    cards (e.g. Chaos Sorcerer) after the rest;
    ///  - monsters that won't attack (weak, set face-down) at the back, so they never block the order.
    /// The order is judged from the board when the monster is summoned. Whether the server honours the zone we pick
    /// is checked at the next decision and logged.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // Text of a monster with an effect when it destroys a monster by battle (Goyo Guardian, ...).
        private static readonly System.Text.RegularExpressions.Regex BattleDestroyEffectText = new(
            @"destroys (an|a|your) (opponent's )?monster by battle",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>ATK a monster will attack with this turn (deck brains adjust, e.g. Card Trooper after milling).</summary>
        protected virtual int PlacementAttack(int cardId, int attack) => attack;

        private (int CardId, int Zone, int Turn)? _pendingPlacement;

        /// <summary>
        /// Off: the 2026-11-06 13:36 duel showed the server ignores our zone choice too (asked for M5 three times, the
        /// monster went to M3 each time: it fills zones centre-out). Turn on if the server is ever fixed.
        /// </summary>
        public bool ChooseMonsterZones { get; set; } = false;

        public override async Task<int> OnSelectPlaceAsync(DecisionContext<SelectPlaceData> context, CancellationToken ct = default)
        {
            var state = context.StateData;
            // Not choosing: answer what the live server does anyway (it fills zones centre-out and ignores our pick), so the
            // simulator, which honours the answer, places monsters where live Omega would (seen 2026-11-13: DuelSim put Substitoad
            // in M1, ahead of Absolute Zero, where the attack-order check expected the server's M2).
            if (!ChooseMonsterZones && state.Player == 0 && state.Location == (short)CardLocation.MonsterZone &&
                ServerZoneFillOrder.FirstOrDefault(z => (state.Filter & (1 << z)) != 0, -1) is int serverZone and >= 0)
                return BotActionUtilities.BuildZoneResponse(0, (byte)CardLocation.MonsterZone, (byte)serverZone);
            if (!ChooseMonsterZones || state.Player != 0 || state.Location != (short)CardLocation.MonsterZone)
                return await base.OnSelectPlaceAsync(context, ct);
            var free = Enumerable.Range(0, 5).Where(seq => (state.Filter & (1 << seq)) != 0).ToList();
            if (free.Count <= 1 || state.CardID <= 0)
                return await base.OnSelectPlaceAsync(context, ct);

            await PrepareContextAsync(context, ct);
            var own = (await GetBotMonsterCardsAsync()).Where(card => card.ID > 0).ToList();
            var enemy = await GetEnemyMonsterCardsAsync();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            bool intoMonsters = enemy.Count > 0;

            if (await CardDatabase.GetAsync(state.CardID) is not DBCardInfo info)
                return await base.OnSelectPlaceAsync(context, ct);
            bool newBattleEffect = BattleDestroyEffectText.IsMatch(info.CardText ?? "");
            int newKey = PlacementKey(state.CardID, PlacementAttack(state.CardID, info.Attack), attacks: true, newBattleEffect, intoMonsters, oppLp);

            var existing = new List<(int Zone, int Key)>();
            foreach (var card in own.Where(card => card.CurrentCoordinates.Sequence < 5))
            {
                bool attacks = card.IsFaceup() && card.IsAttack();
                bool battleEffect = await TextMatchesAsync(card.ID, BattleDestroyEffectText);
                existing.Add((card.CurrentCoordinates.Sequence, PlacementKey(card.ID, card.Attack, attacks, battleEffect, intoMonsters, oppLp)));
            }

            // The free zone with the fewest monsters out of order around it; ties: toward the front if it should attack
            // before everything there, toward the back if after, otherwise the middle of the gap.
            int before = existing.Count(e => e.Key < newKey);
            int after = existing.Count(e => e.Key > newKey);
            int Inversions(int zone) =>
                existing.Count(e => e.Key < newKey && e.Zone > zone) + existing.Count(e => e.Key > newKey && e.Zone < zone);
            int best = free
                .OrderBy(Inversions)
                .ThenBy(zone => after == 0 ? -zone : before == 0 ? zone : Math.Abs(zone - 2))
                .First();

            string order = intoMonsters ? "into their monsters: Goyo-type first, then largest first" : "into an empty field: smallest first";
            context.Bot.LogNote($"deck brain: place {CardNames.Get(state.CardID)} in M{best + 1} so the server's zone-order attacks follow our plan " +
                                $"({order}; {before} of ours should attack before it, {after} after)");
            _pendingPlacement = (state.CardID, best, context.Header.CurrentTurn);
            return BotActionUtilities.BuildZoneResponse(0, (byte)CardLocation.MonsterZone, (byte)best);
        }

        /// <summary>Lower attacks earlier. Monsters that won't attack go last.</summary>
        private int PlacementKey(int cardId, int attack, bool attacks, bool battleEffect, bool intoMonsters, int oppLp)
        {
            bool weak = BoardAssessment.SpeedOf(BoardAssessment.TurnsToWin(new[] { attack }, Array.Empty<BoardAssessment.Defender>(), oppLp))
                        is ClockSpeed.AlmostNone or ClockSpeed.None;
            if (!attacks || weak)
                return 1_000_000 + attack;
            if (intoMonsters)
                return (battleEffect ? 0 : 100_000) - attack;
            return (AttackLast.Contains(cardId) ? 100_000 : 0) + attack;
        }

        /// <summary>Logs whether the server put the last placed monster where we asked (it ignores the attack index).</summary>
        private void CheckPendingPlacement(BotStateHeader header, List<DynamicCard> ownMonsters)
        {
            if (_pendingPlacement is not (int cardId, int zone, int turn))
                return;
            var placed = ownMonsters.Where(card => card.IsCode(cardId)).ToList();
            if (placed.Count == 0 && header.CurrentTurn == turn)
                return; // not on the field yet (still resolving)
            _pendingPlacement = null;
            if (placed.Count == 0)
                return;
            bool honoured = placed.Any(card => card.CurrentCoordinates.Sequence == zone);
            _trackedBot?.LogNote(honoured
                ? $"deck brain: placement check: {CardNames.Get(cardId)} is in M{zone + 1} as asked"
                : $"deck brain: placement check: asked for M{zone + 1} but the server put {CardNames.Get(cardId)} in " +
                  $"{string.Join("/", placed.Select(card => $"M{card.CurrentCoordinates.Sequence + 1}"))} (it may ignore zone choices too)");
        }
    }
}
