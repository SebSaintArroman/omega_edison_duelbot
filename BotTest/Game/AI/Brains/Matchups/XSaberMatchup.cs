using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains.Matchups
{
    /// <summary>
    /// Playing against X-Sabers (user's notes, 2026-11-10):
    ///  - XX-Saber Emmersblade on their field: a good chance of XX-Saber Hyunlei next turn, which destroys up to 3
    ///    spells/traps when Synchro Summoned. Like Heavy Storm, but signalled by their board: keep at most
    ///    <see cref="MaxSetsUnderHyunlei"/> exposed set card.
    /// </summary>
    public sealed class XSaberMatchup : Matchup
    {

        /// <summary>Most exposed set cards while Hyunlei threatens (it takes up to 3 at once).</summary>
        public static int MaxSetsUnderHyunlei { get; set; } = 1;

        public override string Deck => "X-Sabers";

        // "X-Saber ..." and "XX-Saber ..." cards (names from the card database / logs).
        public override bool Recognises(int theirCardId) =>
            theirCardId > 0 && CardNames.Get(theirCardId) is { } name &&
            (name.StartsWith("X-Saber", StringComparison.OrdinalIgnoreCase) || name.StartsWith("XX-Saber", StringComparison.OrdinalIgnoreCase));

        public override string? HoldSet(IReadOnlyList<DynamicCard> theirFaceUpMonsters, int ourExposedSets)
        {
            if (ourExposedSets < MaxSetsUnderHyunlei || !theirFaceUpMonsters.Any(card => Is(card.ID, CardId.XXSaberEmmersblade)))
                return null;
            return $"XX-Saber Emmersblade is on their field: XX-Saber Hyunlei (destroys up to 3 spells/traps) is likely next turn, " +
                   $"and we already have {ourExposedSets} set";
        }
    }
}
