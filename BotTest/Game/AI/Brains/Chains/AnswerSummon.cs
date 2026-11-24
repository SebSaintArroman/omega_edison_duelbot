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
    /// Answering their summon with our set traps (Bottomless Trap Hole, Torrential Tribute...): one answer per summon.
    /// RULES deck.one-answer-per-summon, deck.torrential-tribute, deck.bottomless-trap-hole.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Answers the opponent's summon with Bottomless Trap Hole or Torrential Tribute: one card per summon, never a
        /// second (Torrential on top of Bottomless just makes Bottomless fizzle).
        ///  1. Torrential first, if it's worth it. If Bottomless could take the summoned monster alone and Torrential is
        ///     only worth it because of that monster, hold Torrential for a bigger board.
        ///  2. Bottomless only if Torrential wasn't chosen.
        ///  - Already answered this summon (our card is in the chain): nothing more.
        /// Returns the option index, -1 to pass, or null when neither card is offered.
        /// </summary>
        protected async Task<int?> AnswerSummonAsync(DecisionContext<SelectChainData> context)
        {
            var options = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            int bottomless = Array.FindIndex(options, card => card.IsCode(BottomlessTrapHole));
            int torrential = Array.FindIndex(options, card => card.IsCode(TorrentialTribute));
            if (bottomless < 0 && torrential < 0)
                return null;

            var enemy = await GetEnemyMonsterCardsAsync();
            // Read what just arrived (a monster flipped face-up this moment, e.g. their Ryko, must count as a spent FLIP
            // monster in the Torrential maths, not as a card they lose).
            await RefreshRewardOnDestroyAsync(enemy);
            var ours = await GetBotMonsterCardsAsync();
            // Bottomless takes the summoned 1500+ ATK monsters. (If the summon wasn't tracked, assume all of them.)
            var bottomlessTargets = enemy.Where(card => card.IsFaceup() && card.Attack >= 1500 && IsLatestArrival(card)).ToList();
            if (bottomlessTargets.Count == 0)
                bottomlessTargets = enemy.Where(card => card.IsFaceup() && card.Attack >= 1500).ToList();
            var rest = enemy.Where(card => !bottomlessTargets.Any(t => SameZone(t, card))).ToList();

            // One card per summon: whatever we chain second would hit the same monster (Torrential on top of Bottomless
            // just makes Bottomless fizzle), so once one is in the chain, nothing else goes on it.
            if (LatestSummonAnswered && Rule(DeckRules.OneAnswerPerSummon))
            {
                context.Bot.LogNote("deck brain: this summon is already answered by a card in the chain: don't chain a second card for it");
                return -1;
            }

            // Decide on the mass card first: Torrential, if it's worth it.
            int ourLp = torrential >= 0 ? await TorrentialDefendingLpAsync(context) : 0;
            var all = torrential >= 0 ? TorrentialWorthIt(enemy, ours, ourLp) : (Worth: false, Why: "");
            bool torrentialLive = torrential >= 0 && all.Worth && await DestructionNegatedAsync(context, TorrentialTribute) == null &&
                                  Rule(DeckRules.TorrentialTribute);
            bool bottomlessLive = bottomless >= 0 && bottomlessTargets.Count > 0 &&
                                  await DestructionNegatedAsync(context, BottomlessTrapHole) == null &&
                                  Rule(DeckRules.BottomlessTrapHole);
            if (torrentialLive)
            {
                // ...except when Torrential is only worth it because of the summoned monster and Bottomless can take
                // that one alone: then Bottomless banishes it and Torrential stays set for a bigger board.
                var withoutSummon = TorrentialWorthIt(rest, ours, ourLp);
                if (!bottomlessLive || withoutSummon.Worth || !Rule(DeckRules.BottomlessOverTorrential))
                {
                    MarkLatestSummonAnswered();
                    context.Bot.LogNote($"deck brain: chain Torrential Tribute: {all.Why}" +
                                        (bottomlessLive ? " (worth it even without the summoned monster, so no Bottomless)" : ""));
                    return torrential;
                }
                context.Bot.LogNote($"deck brain: hold Torrential Tribute: only worth it because of the summoned monster ({withoutSummon.Why} without it); Bottomless takes that one alone");
            }
            else if (torrential >= 0)
                context.Bot.LogNote($"deck brain: hold Torrential Tribute: {all.Why}");

            // Then the single card, only when Torrential wasn't chosen.
            if (bottomlessLive)
            {
                MarkLatestSummonAnswered();
                context.Bot.LogNote($"deck brain: chain Bottomless Trap Hole: opponent summoned {string.Join(", ", bottomlessTargets.Select(card => $"{CardNames.Get(card.ID)} ({card.Attack})"))}");
                return bottomless;
            }
            return -1;
        }

        private async Task<int> TorrentialDefendingLpAsync(DecisionContext<SelectChainData> context) =>
            !IsOwnTurn(context.Header) && (DuelPhase)context.Header.CurrentPhase is DuelPhase.Main1 or DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Battle
                ? await CurrentField.GetBotLifePoints()
                : 0;

        /// <summary>
        /// Whether the monsters removed are worth more than the monsters lost, or the wipe clears lethal attack
        /// damage. Pass the defending player's LP when the attacking side can still battle this turn.
        /// </summary>
        protected (bool Worth, string Why) TorrentialWorthIt(List<DynamicCard> enemyMonsters, List<DynamicCard> ourMonsters, int defendingLp = 0)
        {
            // (A flipped Ryko counts as a card worth its stats: TargetThreat values a spent flip monster by its stats alone.)
            enemyMonsters = enemyMonsters.Where(card => card.ID != 0 || !card.IsFaceup()).ToList();
            int Value(DynamicCard card) =>
                card.IsFaceup() ? TargetThreat(card)
                : OpponentMemory.FaceDownDefense(card) + 500; // unknown face-down: its likely DEF (deck read) + a small effect
            int theirCards = enemyMonsters.Count;
            int ourCards = ourMonsters.Count;
            int theirValue = enemyMonsters.Sum(Value);
            int ourValue = ourMonsters.Sum(Value);
            int directDamage = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(card => card.Attack).OrderByDescending(attack => attack).Skip(ourCards).Sum();
            bool preventsLethal = defendingLp > 0 && directDamage >= defendingLp;
            bool worth = preventsLethal || theirValue > ourValue;
            string lethal = preventsLethal ? $", prevents {directDamage} direct damage at {defendingLp} LP" : "";
            return (worth, $"opponent loses {theirCards} card(s) worth {theirValue}, we lose {ourCards} worth {ourValue}{lethal} -> {(worth ? "yes" : "no")}");
        }
    }
}
