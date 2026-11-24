using BotTest.Game.AI.Search;
using OmegaBotWrapper.Enum;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    internal readonly record struct StrategicCard(int Code, int Zone, int Attack, int Defense, bool FaceUp, bool AttackPosition, bool Disabled);

    internal sealed class ThreatenedState
    {
        internal readonly record struct Assessment(IReadOnlyList<StrategicCard> Threats, int Answers, bool IsPressuring = false)
        {
            public TagRules.BotStrategicState State =>
                Threats.Count > Answers ? TagRules.BotStrategicState.Threatened :
                IsPressuring ? TagRules.BotStrategicState.Pressuring : TagRules.BotStrategicState.Base;
        }

        private readonly Dictionary<(int Zone, int Code), HashSet<(int Zone, int Code)>> _presentWhenSet = new();

        public void Clear() => _presentWhenSet.Clear();

        public ThreatenedState Copy()
        {
            var copy = new ThreatenedState();
            foreach (var (trap, monsters) in _presentWhenSet)
                copy._presentWhenSet.Add(trap, new(monsters));
            return copy;
        }

        public Assessment Evaluate(
            IReadOnlyCollection<int> deck, IReadOnlyCollection<int> hand, IReadOnlyList<StrategicCard> ownMonsters,
            IReadOnlyList<StrategicCard> ownSpells, IReadOnlyList<StrategicCard> enemyMonsters, int enemyHand, int ownLp, int enemyLp)
        {
            var fieldMonsters = enemyMonsters.Where(card => card.FaceUp && card.Code > 0).ToList();
            var occupied = fieldMonsters.Select(card => (card.Zone, card.Code)).ToHashSet();
            var bottomless = ownSpells.Where(card => !card.FaceUp && SearchHost.Is(card.Code, BottomlessTrapHole))
                .Select(card => (card.Zone, card.Code)).ToHashSet();
            foreach (var key in _presentWhenSet.Keys.Where(key => !bottomless.Contains(key)).ToList())
                _presentWhenSet.Remove(key);
            foreach (var monsters in _presentWhenSet.Values)
                monsters.IntersectWith(occupied);
            foreach (var key in bottomless)
                if (!_presentWhenSet.ContainsKey(key))
                    _presentWhenSet.Add(key, new(occupied));

            if (fieldMonsters.Count == 0)
                return new(Array.Empty<StrategicCard>(), 0,
                    PressuringState.Applies(ownLp, enemyLp, enemyMonsters.Count,
                        ownMonsters.Where(card => card.FaceUp && card.AttackPosition).Select(card => card.Attack)));

            int deckAttack = SearchHost.Available ? deck.Select(SearchHost.Cards.Get)
                .Where(card => card is { Level: > 0 and <= 4 } && (card.Type & (int)CardType.Monster) != 0)
                .Select(card => card!.Attack).DefaultIfEmpty(0).Max() : 0;
            int boardAttack = ownMonsters.Select(card => !card.FaceUp && SearchHost.Available
                ? SearchHost.Cards.Get(card.Code)?.Attack ?? card.Attack : card.Attack).DefaultIfEmpty(0).Max();
            var context = new CardRuleRegistry.Context(deck, hand, enemyHand, ownLp, enemyLp, false,
                OpponentHasStardustDragon: fieldMonsters.Any(card => SearchHost.Is(card.Code, StardustDragon)),
                OwnMonsterIds: ownMonsters.Select(card => card.Code).ToArray());
            var threats = fieldMonsters.Where(card =>
                (CardRuleRegistry.TagsFor(card.Code, context) & TagRules.Tag.Threat) != 0 ||
                card.Attack > deckAttack && card.Attack > boardAttack).ToList();
            if (threats.Count == 0)
                return new(threats, 0);

            var eligibleThreats = ownMonsters.Where(card => card.FaceUp).Concat(ownSpells)
                .Where(answer => answer.Code > 0 && !answer.Disabled)
                .Select(answer => Enumerable.Range(0, threats.Count).Where(index =>
                {
                    var threat = threats[index];
                    return (CardRuleRegistry.TagsFor(answer.Code, context with
                    {
                        ThreatCardId = threat.Code,
                        ThreatAttackPosition = threat.AttackPosition,
                        ThreatWasAlreadyOnFieldWhenTrapWasSet = bottomless.Contains((answer.Zone, answer.Code)) &&
                            _presentWhenSet[(answer.Zone, answer.Code)].Contains((threat.Zone, threat.Code)),
                        ThreatTriggersWhenNormalSummoned = CardRules.ThreatTriggersOnNormalSummon(threat.Code)
                    }) & TagRules.Tag.AnswersAnyThreat) != 0;
                }).ToArray()).ToArray();

            // Each answer gets one target. Move a previously matched answer when it can cover another threat.
            var assignedAnswer = new int[threats.Count];
            Array.Fill(assignedAnswer, -1);
            bool Assign(int answer, bool[] visited)
            {
                foreach (int threat in eligibleThreats[answer])
                {
                    if (visited[threat])
                        continue;
                    visited[threat] = true;
                    if (assignedAnswer[threat] == -1 || Assign(assignedAnswer[threat], visited))
                    {
                        assignedAnswer[threat] = answer;
                        return true;
                    }
                }
                return false;
            }

            int covered = 0;
            for (int answer = 0; answer < eligibleThreats.Length && covered < threats.Count; answer++)
                if (eligibleThreats[answer].Length > 0 && Assign(answer, new bool[threats.Count]))
                    covered++;
            return new(threats, covered);
        }
    }
}
