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
    /// Removing their backrow in the Main Phase: Heavy Storm, Lyla, MST for a big play. RULES deck.heavy-storm,
    /// deck.lyla-after-battle, deck.lyla-destroy, deck.mst-for-big-play.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private static readonly System.Text.RegularExpressions.Regex StopsAttacksText = new(
            @"cannot (declare an |declare |)attack|negate (the|an|that) attack|end the battle phase|change (all|that|the attacking)[^.]{0,40}position",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Why Lyla should attack before using her effect (it turns her to defence), or null (user, 2026-11-13: "valhalla
        /// wasn't going to be able to stop an attack so lyla could have attacked, and then activated her effect in mp2"):
        /// Main Phase 1 with a battle ahead, Lyla able to attack and with something to hit, and none of their spells/traps
        /// able to stop the attack (all face-up, none that stops attacks: a set card might be Mirror Force, so that one goes first).
        /// </summary>
        private async Task<string?> LylaAttacksFirstAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, DynamicCard lyla)
        {
            if ((DuelPhase)context.Header.CurrentPhase != DuelPhase.Main1 || !state.CanBattlePhase || context.Header.CurrentTurn <= 1)
                return null;
            if (lyla.CurrentCoordinates.Location != (byte)CardLocation.MonsterZone || !lyla.IsFaceup() || !lyla.IsAttack() || lyla.Attacked)
                return null;
            var spells = await GetEnemySpellCardsAsync();
            var enemy = (await GetEnemyMonsterCardsAsync()).Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
            int Power(DynamicCard card) => !card.IsFaceup() ? OpponentMemory.FaceDownDefense(card) : card.IsAttack() ? card.Attack : card.Defense;
            var prey = enemy.Where(card => lyla.Attack > Power(card)).OrderByDescending(OpponentThreat).FirstOrDefault();
            if (enemy.Count > 0 && prey.CurrentCoordinates.Location == 0)
                return null;
            string hit = enemy.Count == 0 ? "a direct hit" : $"{(prey.IsFaceup() ? CardNames.Get(prey.ID) : "their face-down")}";
            if (spells.Any(card => !card.IsFaceup() || card.ID <= 0))
                return await LylaGambleAsync(context, lyla, enemy, prey, hit);
            foreach (var card in spells)
                if (await CardDatabase.GetAsync(card.ID) is DBCardInfo info && StopsAttacksText.IsMatch(info.CardText ?? ""))
                    return null;
            return $"their spells/traps ({string.Join(", ", spells.Select(card => CardNames.Get(card.ID)))}) can't stop her attack: she hits {hit} first";
        }

        /// <summary>Chance to gamble Lyla's attack into their set card when we don't need it gone much. Tune from logs.</summary>
        [Tunable("Chance to gamble Lyla's attack into their set card when we don't need it gone much. Tune from logs.")]
        public double LylaGambleChance { get; set; } = 0.5;

        private (int Turn, string? Why) _lylaGamble = (-1, null);

        /// <summary>
        /// The greedy Lyla line (user, 2026-11-13): Lyla our only monster, they have a monster she beats and a set card.
        /// Attack first, then her effect in Main Phase 2: if it works she takes two cards instead of one; if their trap hits
        /// her instead, it's a similar outcome (she's expendable once her effect is used anyway). Rolled once a turn,
        /// weighted by how much we need their backrow gone: never with a big play waiting on it (as with MST bait), less
        /// with each strong summon we could still make.
        /// </summary>
        private async Task<string?> LylaGambleAsync(DecisionContext<IdleCmdData> context, DynamicCard lyla, List<DynamicCard> enemy,
            DynamicCard prey, string hit)
        {
            int turn = context.Header.CurrentTurn;
            if (_lylaGamble.Turn == turn)
                return _lylaGamble.Why;
            _lylaGamble = (turn, null);
            if (enemy.Count == 0 || prey.CurrentCoordinates.Location == 0 ||
                (await GetBotMonsterCardsAsync()).Any(card => card.ID > 0 && !SameZone(card, lyla)))
                return null;
            if (await BigPlayPendingAsync(context) is string bigPlay)
            {
                StapleNote(context, $"no Lyla gamble: their backrow must go for our play ({bigPlay})");
                return null;
            }
            int strong = await StrongSummonsAvailableAsync(context);
            double chance = LylaGambleChance * Math.Max(0, 1 - LylaGambleDropPerStrongSummon * strong);
            double roll = RollAt;
            bool go = roll < chance;
            StapleNote(context, $"Lyla gamble (attack {hit}, effect in Main Phase 2: two cards if their set card doesn't stop her)? chance {chance:P0}" +
                                $"{(strong > 0 ? $" ({strong} strong summon(s) of ours would like their backrow gone)" : "")}, rolled {roll:0.00} -> {(go ? "gamble" : "effect first")}");
            if (go)
                _lylaGamble = (turn, $"a gamble: she hits {hit} first, then takes a spell/trap in Main Phase 2 (their set card may stop her, a similar outcome)");
            return _lylaGamble.Why;
        }

        private readonly Random _lylaRandom = new();

        /// <summary>
        /// Backrow removal: Heavy Storm when it pushes a big play through (see HeavyStormReasonAsync), Lyla on one of their
        /// Spell/Traps, MST to clear the way for a big play (committing to it). Not into a negator (Stardust).
        /// </summary>
        protected async Task<IdleCmdResponse?> TryBackrowRemovalAsync(DecisionContext<IdleCmdData> context, IdleCmdData state, int theirSpells, int ourOtherSpells)
        {
            if (TryGetActivable(state, HeavyStorm, out _) &&
                await HeavyStormReasonAsync(context, theirSpells, ourOtherSpells) is string stormWhy &&
                await DestructionNegatedAsync(context, HeavyStorm) == null &&
                TryActivateStaple(context, state, HeavyStorm, stormWhy, out var response, DeckRules.HeavyStorm))
                return response;
            if (theirSpells > 0 && TryGetActivable(state, LylaLightswornSorceress, out var lyla) && await LylaAttacksFirstAsync(context, state, lyla) is string attackFirst &&
                Rule(DeckRules.LylaAfterBattle))
            {
                StapleNote(context, $"Lyla's effect waits for Main Phase 2: {attackFirst}");
                Hold(LylaLightswornSorceress, MainAction.Activate, DeckRules.LylaAfterBattle);
            }
            else if (theirSpells > 0 && TryGetActivable(state, LylaLightswornSorceress, out _) &&
                await DestructionNegatedAsync(context, LylaLightswornSorceress) == null &&
                TryActivateStaple(context, state, LylaLightswornSorceress, "destroy an opponent S/T", out response, DeckRules.LylaDestroy))
                return response;
            if (theirSpells > 0 && TryGetActivable(state, MysticalSpaceTyphoon, out _) &&
                await BigPlayPendingAsync(context) is string mstPlay &&
                await DestructionNegatedAsync(context, MysticalSpaceTyphoon) == null &&
                TryActivateStaple(context, state, MysticalSpaceTyphoon, $"clear a spell/trap for a big play: {mstPlay}", out response, DeckRules.MstForBigPlay))
            {
                CommitToBigPlay(context.Header.CurrentTurn);
                return response;
            }
            return null;
        }
    }
}
