namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// What our real turn has done so far that the search's line penalties depend on (user, 2026-11-13: new lines start "as
    /// if they'd been around from the start", so they have parity with the lines the reused tree already holds). Every line
    /// starts its tally from here. Wiped, together with the reused tree, when the opponent activates a card or effect, we draw
    /// mid-turn, or we chain something outside the plan ("memory wipe conditions": the bot commits while it acts uninterrupted,
    /// and re-plans from scratch when something new happens).
    /// </summary>
    internal sealed record TurnRecord
    {
        public static readonly TurnRecord Empty = new();

        /// <summary>Cards we set in Main Phase 1 before entering the Battle Phase.</summary>
        public int SetsBeforeBattle { get; init; }

        /// <summary>We entered the Battle Phase this turn.</summary>
        public bool BattleEntered { get; init; }

        /// <summary>Monsters we summoned face-up this turn that are still on the field and haven't attacked or used an effect
        /// (card codes).</summary>
        public IReadOnlyList<int> IdleSummons { get; init; } = Array.Empty<int>();

        /// <summary>Lyla's effect used in Main Phase 1, before the Battle Phase.</summary>
        public bool LylaBeforeBattle { get; init; }

        /// <summary>Union equips (Machina Gearframe / Peacekeeper) made in Main Phase 1, before the Battle Phase.</summary>
        public int EquipsBeforeBattle { get; init; }

        /// <summary>Miracle Fusion activated this turn with no Battle Phase entered after it yet.</summary>
        public bool FusionWithoutBattle { get; init; }

        /// <summary>What the brain's holds charged for moves we really made this turn against them (card units).</summary>
        public double HoldCosts { get; init; }
    }
}
