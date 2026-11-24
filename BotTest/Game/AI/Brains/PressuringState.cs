using DuelSim.Engine;

namespace BotTest.Game.AI.Brains
{
    internal static class PressuringState
    {
        public static bool Applies(SideSummary ours, SideSummary theirs) =>
            Applies(ours.LifePoints, theirs.LifePoints, theirs.Monsters.Count,
                ours.Monsters.Where(monster => monster.FaceUp && monster.AttackPosition).Select(monster => monster.Attack));

        public static bool Applies(int ourLifePoints, int theirLifePoints, int opponentMonsters, IEnumerable<int> attackers) =>
            ourLifePoints >= 6000 && theirLifePoints > 0 && opponentMonsters == 0 &&
            attackers.Any(attack => attack >= 1000 && attack * 6L >= theirLifePoints);
    }
}
