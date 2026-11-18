namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// How explosive each Edison deck type is (0-1): how likely it is to have a big turn that blows us out, the longer
    /// the game goes. Starting values come from Format Library's deck-type categories (Combo 0.85, Ramp 0.75, Aggro 0.6,
    /// Midrange 0.45, Control 0.3), with the "Multiple" ones judged by hand. Tune these with experience.
    /// The bot weighs them by how likely the opponent is on each type, then by their resources (cards in hand and
    /// graveyard), to decide how hard to push (see DeckBotBrain.BigTurnThreatAsync). Types not listed here fall back to the
    /// value in Data/edison-decks.json, then to DeckBotBrain.DefaultExplosiveness.
    /// </summary>
    public static class DeckExplosiveness
    {
        // The built-in values; ByType is these as the tuning file gives them (DeckExplosiveness.<type>, user 2026-11-16: every number tweakable).
        private static readonly Dictionary<string, double> Defaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Frog OTK"] = 0.85,          // Combo
            ["Gigavise"] = 0.85,          // Combo
            ["Vayu Turbo"] = 0.75,        // Multiple: Dark Armed Dragon and synchro bursts
            ["Lightsworn"] = 0.75,        // Ramp: Judgment Dragon, snowballs late
            ["Dragon Turbo"] = 0.75,      // Ramp
            ["Lightsworn Fairy"] = 0.75,  // Ramp
            ["Blackwing"] = 0.6,          // Aggro
            ["Gladiator Beast"] = 0.6,    // Aggro
            ["Hero Beat"] = 0.6,          // Aggro
            ["Diva Hero Beat"] = 0.6,     // Aggro
            ["Dragon Beat"] = 0.6,        // Aggro
            ["Zombie TeleDAD"] = 0.6,     // Aggro
            ["Diva Zombie"] = 0.55,       // Multiple
            ["Diva Hero Zombie"] = 0.55,  // Multiple
            ["Diva Hero"] = 0.5,          // Multiple
            ["Frog Monarch"] = 0.45,      // Midrange
            ["Machina Gadget"] = 0.45,    // Midrange
            ["Hero Frog"] = 0.4,          // Multiple: grindy
            ["Machina Control"] = 0.3,    // Control
            ["Quickdraw Plant"] = 0.3,    // Control
            ["Fairy"] = 0.3,              // Control
            ["Diva Frog"] = 0.3,          // Control
        };

        private static readonly Lazy<IReadOnlyDictionary<string, double>> Tuned = new(() =>
        {
            var tuned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, value) in Defaults)
            {
                string text = Tuning.Entry($"DeckExplosiveness.{type.Replace(' ', '-')}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "how explosive this deck type is, 0-1");
                tuned[type] = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : value;
            }
            Tuning.Flush();
            return tuned;
        });

        public static IReadOnlyDictionary<string, double> ByType => Tuned.Value;
    }
}
