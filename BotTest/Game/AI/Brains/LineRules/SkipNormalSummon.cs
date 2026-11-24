namespace BotTest.Game.AI.Brains
{
    internal static partial class LineRules
    {
        [Tunable("Cost for ending our turn against an empty monster field without using an available Normal Summon or monster set.")]
        internal static readonly Tuned<double> SkipNormalSummonCost = new(2);
    }
}
