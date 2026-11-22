using DuelSim.Engine;
using BotTest.Game.AI.Search;

namespace BotTest.Game.AI.Brains
{
    internal static partial class CardRules
    {
        internal static bool PlaguespreaderMayRevive(SideSummary own, SideSummary enemy, IEnumerable<int> extraDeck,
            bool battleAhead)
        {
            if (enemy.LifePoints < 400)
                return true;

            var defenders = enemy.Monsters.Select(monster => new BoardAssessment.Defender(
                monster.FaceUp ? monster.AttackPosition ? monster.Attack : monster.Defense : BoardAssessment.AssumedFaceDownDefense,
                monster.FaceUp && monster.AttackPosition, !monster.FaceUp, monster.Code)).ToList();
            var attackers = own.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition)
                .Select(monster => monster.Attack).ToList();
            if (battleAhead && BoardAssessment.TurnsToWin(attackers, defenders, enemy.LifePoints - 399, maxTurns: 1) == 1)
                return true;
            if (own.Monsters.Count >= 5)
                return false;

            var materials = own.Monsters.Where(monster => monster.FaceUp && monster.Level > 0 &&
                (monster.Type & (0x1000 | 0x800000)) == 0).ToArray();
            var threats = BoardAssessment.Threats(attackers, defenders.Where(defender => !defender.FaceDown));
            var synchros = extraDeck.Select(code => SearchHost.Cards.Get(code))
                .Where(card => card != null && (card.Type & 0x2000) != 0).ToArray();

            // Main Monster Zones limit this to at most four possible non-Tuners.
            for (int mask = 1; mask < 1 << materials.Length; mask++)
            {
                var selected = materials.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                int level = 2 + selected.Sum(material => material.Level);
                var remainingAttackers = new List<int>(attackers);
                foreach (var material in selected.Where(material => material.AttackPosition))
                    remainingAttackers.Remove(material.Attack);

                foreach (var synchro in synchros.Where(card => card!.Level == level))
                {
                    // Recognize generic, Zombie, and Plaguespreader-specific Tuner requirements.
                    string materialText = (synchro!.Text ?? "").Split('\n', 2)[0];
                    if (!materialText.StartsWith("1 Tuner +", StringComparison.OrdinalIgnoreCase) &&
                        !materialText.StartsWith("1 Zombie Tuner +", StringComparison.OrdinalIgnoreCase) &&
                        !materialText.StartsWith("\"Plaguespreader Zombie\" +", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if ((materialText.Contains("2 or more", StringComparison.OrdinalIgnoreCase) ||
                         materialText.Contains("2+", StringComparison.Ordinal)) && selected.Length < 2 ||
                        materialText.Contains("non-Tuner Zombie", StringComparison.OrdinalIgnoreCase) &&
                        selected.Any(material => SearchHost.Race(material.Code) != 0x10) ||
                        materialText.Contains("non-Tuner DARK", StringComparison.OrdinalIgnoreCase) &&
                        selected.Any(material => (SearchHost.Attribute(material.Code) & 0x20) == 0))
                        continue;

                    bool brionac = SearchHost.Is(synchro.Id, 50321796) && own.Hand > 1;
                    bool darkEnd = SearchHost.Is(synchro.Id, 88643579);
                    bool blackRose = SearchHost.Is(synchro.Id, 73580471) &&
                        !enemy.Monsters.Any(monster => monster.FaceUp && !monster.Negated && SearchHost.Is(monster.Code, 44508094));
                    if (threats.Any(threat => brionac || darkEnd || blackRose || synchro.Attack > threat.Power ||
                        synchro.Attack == threat.Power && threat.AttackPosition ||
                        SearchHost.Is(synchro.Id, 26593852) && (SearchHost.Attribute(threat.Id) & 0x20) == 0))
                        return true;
                    if (battleAhead && (BoardAssessment.TurnsToWin(remainingAttackers.Append(synchro.Attack),
                            defenders, enemy.LifePoints, maxTurns: 1) == 1 ||
                        brionac && own.Hand - 1 >= defenders.Count &&
                        remainingAttackers.Sum() + synchro.Attack >= enemy.LifePoints))
                        return true;
                }
            }
            return false;
        }
    }
}
