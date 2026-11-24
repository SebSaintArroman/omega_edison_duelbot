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
    /// Their cards that punish our backrow (spell/trap removal), and how many of our set cards they can reach.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        /// <summary>
        /// Whether their deck (the lists they're likely on) still punishes our backrow after Heavy Storm: cards other than Heavy
        /// Storm / MST that destroy spells/traps or "cards" (Icarus Attack, Bestiari, Gyzarus...), at least BackrowPunisherCount
        /// of them. Returns a note naming them, or null. Null without list data.
        /// </summary>
        protected async Task<string?> OpponentBackrowPunisherAsync()
        {
            if (_backrowPunisher is { } cached && cached.Turn == _currentHeader.CurrentTurn)
                return cached.Note;
            var names = new List<string>();
            foreach (int id in OpponentDeck.LikelyCards(0.2))
                if (id != HeavyStorm && id != MysticalSpaceTyphoon && await SpellTrapRemovalReachAsync(id) > 0)
                    names.Add(CardNames.Get(id));
            string? note = names.Count >= BackrowPunisherCount ? $"their deck still hits backrow ({string.Join(", ", names.Take(4))})" : null;
            _backrowPunisher = (_currentHeader.CurrentTurn, note);
            return note;
        }

        private (int Turn, string? Note)? _backrowPunisher;

        /// <summary>Backrow-hitting cards (besides Heavy Storm / MST) in their deck from which we stay careful after their Storm.</summary>
        [Tunable("Backrow-hitting cards (besides Heavy Storm / MST) in their deck from which we stay careful after their Storm.")]
        public int BackrowPunisherCount { get; set; } = 2;

        /// <summary>How many spells/traps this card's effect can remove (0 if none, or unknown).</summary>
        protected async Task<int> SpellTrapRemovalReachAsync(int cardId)
        {
            if (KnownSpellTrapRemovers.TryGetValue(cardId, out int known))
                return known;
            if (await CardDatabase.GetAsync(cardId) is not DBCardInfo info)
                return 0;
            string text = info.CardText ?? "";
            var match = SpellTrapRemovalCount.Match(text);
            if (!match.Success)
                return MassBackrowRemoval.IsMatch(text) ? 5 : 0;
            // Its own cards only ("return 1 card you control to the hand": Mist Valley Falcon's attack cost): no threat to ours
            // (seen 2026-11-10: Jar of Greed chained as bait "against" Falcon).
            string after = text.Substring(match.Index, Math.Min(text.Length - match.Index, match.Length + 30));
            if (after.Contains("you control", StringComparison.OrdinalIgnoreCase) && !after.Contains("opponent", StringComparison.OrdinalIgnoreCase) &&
                !after.Contains("on the field", StringComparison.OrdinalIgnoreCase))
                return 0;
            string n = match.Groups[1].Success && match.Groups[1].Value != "" ? match.Groups[1].Value : match.Groups[5].Value;
            return n.ToLowerInvariant() switch { "one" => 1, "two" => 2, "three" => 3, _ => int.TryParse(n, out int v) ? v : 1 };
        }
    }
}
