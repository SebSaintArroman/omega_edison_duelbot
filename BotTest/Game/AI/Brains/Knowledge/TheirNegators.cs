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
    /// Their monsters that negate our cards: Stardust Dragon and similar (negate a destruction effect), Thought Ruler
    /// Archfiend and similar (negate a trap that targets).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        // ------------------------------------------------------------------ the opponent's Stardust Dragon (and similar)

        // "When a card or effect is activated that would destroy a card(s) on the field: ... negate the activation"
        private static readonly System.Text.RegularExpressions.Regex DestructionNegatorText = new(
            @"activated that would destroy[^.]*negate|negate the activation[^.]*would destroy",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex DestroysText = new(
            @"\bdestroy", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static async Task<bool> TextMatchesAsync(int cardId, System.Text.RegularExpressions.Regex pattern) =>
            cardId > 0 && await CardDatabase.GetAsync(cardId) is DBCardInfo info && pattern.IsMatch(info.CardText ?? "");

        /// <summary>
        /// Whether a face-up opponent monster like Stardust Dragon would just negate this destroy card. Returns the
        /// reason to hold it, or null when it's fine to activate. It's fine anyway when:
        ///  1. it isn't a destroy card, or no such monster is face-up,
        ///  2. we have another destroy effect available: this one baits the negation so the other goes through,
        ///  3. the negator has to go now: without it the opponent can't kill us next turn, or without it we have lethal
        ///     (the negation tributes it, clearing it from the field until the End Phase).
        /// </summary>
        protected async Task<string?> DestructionNegatedAsync<T>(DecisionContext<T> context, int cardId)
        {
            if (!await TextMatchesAsync(cardId, DestroysText))
                return null;
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            DynamicCard negator = default;
            foreach (var card in enemyMonsters.Where(card => card.IsFaceup() && !card.IsDisabled()))
            {
                if (await TextMatchesAsync(card.ID, DestructionNegatorText))
                {
                    negator = card;
                    break;
                }
            }
            if (negator.ID <= 0)
                return null;
            string name = CardNames.Get(negator.ID);

            // 2. Another destroy effect is available this decision: use this one as bait.
            var available = context.StateData switch
            {
                IdleCmdData idle => idle.ActivableCards ?? Array.Empty<DynamicCard>(),
                SelectChainData chain => chain.SelectableCards ?? Array.Empty<DynamicCard>(),
                _ => Array.Empty<DynamicCard>()
            };
            foreach (var other in available.Where(card => card.ID > 0 && !card.IsCode(cardId)).Select(card => card.ID).Distinct())
            {
                if (await TextMatchesAsync(other, DestroysText))
                {
                    context.Bot.LogNote($"deck brain: {CardNames.Get(cardId)} baits {name}'s negation so {CardNames.Get(other)} goes through");
                    return null;
                }
            }

            // 3a. It is what makes the opponent's board lethal on us.
            int ourLp = await CurrentField.GetBotLifePoints();
            int theirAttack = enemyMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack);
            int ourBlockers = (await GetBotMonsterCardsAsync()).Count;
            if (ourBlockers == 0 && theirAttack >= ourLp && theirAttack - negator.Attack < ourLp)
            {
                context.Bot.LogNote($"deck brain: activate {CardNames.Get(cardId)} into {name} anyway: we need it off the field to survive");
                return null;
            }

            // 3b. It is the only thing between us and lethal.
            var attackers = (await GetBotMonsterCardsAsync()).Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Select(card => card.Attack).ToList();
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            var without = BoardAssessment.Defenders(enemyMonsters.Where(card => !SameZone(card, negator)), OpponentMemory);
            if (BoardAssessment.TurnsToWin(attackers, without, oppLp) == 1)
            {
                context.Bot.LogNote($"deck brain: activate {CardNames.Get(cardId)} into {name} anyway: with it gone we have lethal");
                return null;
            }

            string why = $"{name} would negate {CardNames.Get(cardId)} (it destroys)";
            if (!Rule(DeckRules.NoDestroyIntoNegator))
                return null;
            context.Bot.LogNote($"deck brain: hold {CardNames.Get(cardId)}: {why}");
            Hold(cardId, null, DeckRules.NoDestroyIntoNegator);
            return why;
        }

        // ------------------------------------------------------------------ the opponent's Thought Ruler Archfiend (and similar)

        // "When a Trap Card's effect that targets 1 Psychic-Type monster on the field is activated: You can pay 1000
        // Life Points; negate the activation, and if you do, destroy it."
        private static readonly System.Text.RegularExpressions.Regex TrapTargetNegatorText = new(
            @"trap card'?s? (effect )?that targets[^.]*?(?<race>\w+)-type monster[^.]*negate|trap card'?s? (effect )?that targets[^.]*negate",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex PayLifePointsText = new(
            @"pay (\d+) life points", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether a face-up opponent monster (Thought Ruler Archfiend) would negate one of our Traps that targets this
        /// monster. Its cost (1000 LP) only stops them when they can't afford it, so treat it as negated unless their
        /// life points are at or below the cost. Returns the reason to hold the trap, or null when it's fine to use.
        /// </summary>
        protected async Task<string?> TrapTargetNegatedAsync(DynamicCard target)
        {
            if (target.ID <= 0)
                return null;
            int oppLp = await CurrentField.GetBotLifePoints(isOwnSide: false);
            foreach (var negator in (await GetEnemyMonsterCardsAsync()).Where(card => card.IsFaceup() && !card.IsDisabled() && card.ID > 0))
            {
                if (await CardDatabase.GetAsync(negator.ID) is not DBCardInfo info)
                    continue;
                string text = info.CardText ?? "";
                var match = TrapTargetNegatorText.Match(text);
                if (!match.Success)
                    continue;
                // It protects monsters of a Type ("Psychic-Type monster"), or just itself if no Type is named.
                string race = match.Groups["race"].Value;
                bool covered = race.Length > 0
                    ? System.Enum.TryParse<CardRace>(race, ignoreCase: true, out var r) && (target.Race & (int)r) != 0
                    : SameZone(negator, target);
                if (!covered)
                    continue;
                int cost = PayLifePointsText.Match(text) is { Success: true } pay ? int.Parse(pay.Groups[1].Value) : 0;
                if (cost > 0 && oppLp <= cost)
                    return null; // they can't afford the cost
                return $"{CardNames.Get(negator.ID)} would negate a Trap that targets {CardNames.Get(target.ID)}" +
                       (cost > 0 ? $" (they just pay {cost} LP, and have {oppLp})" : "");
            }
            return null;
        }
    }
}
