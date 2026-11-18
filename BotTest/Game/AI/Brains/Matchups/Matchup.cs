using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains.Matchups
{
    /// <summary>
    /// Rules for playing against one specific deck (the user's "playing against X" sections). A matchup recognises its
    /// deck from the opponent's cards seen during the duel (field, graveyard, banished), and answers the shared brain's
    /// questions with deck-specific knowledge: e.g. against X-Sabers, Emmersblade on their field means XX-Saber Hyunlei
    /// (destroys up to 3 spells/traps) is likely next turn, so don't overexpose backrow. Add a class per deck in this
    /// folder and list it in <see cref="All"/>; each rule is gated like the others (see MatchupRules).
    /// </summary>
    public abstract class Matchup
    {
        /// <summary>The opponent deck, for the log ("X-Sabers").</summary>
        public abstract string Deck { get; }

        /// <summary>Whether this card of theirs shows they're on this deck.</summary>
        public abstract bool Recognises(int theirCardId);

        /// <summary>
        /// Why not to set another spell/trap now (their deck threatens our backrow), or null. Called with their face-up
        /// monsters and how many exposed set cards we already have.
        /// </summary>
        public virtual string? HoldSet(IReadOnlyList<DynamicCard> theirFaceUpMonsters, int ourExposedSets) => null;

        /// <summary>Every known matchup, checked in order.</summary>
        public static IReadOnlyList<Matchup> All { get; } = new Matchup[]
        {
            new XSaberMatchup(),
        };

        /// <summary>A card id or Omega's own copy of it (official + 20).</summary>
        protected static bool Is(int id, int official) => id == official || id == official + 20;
    }
}
