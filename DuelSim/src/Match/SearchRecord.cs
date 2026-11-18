using System.Globalization;
using System.Text;

namespace DuelSim.Match;

/// <summary>
/// The search record of a run: every play search verdict (DeckBotBrain.Search: kept or overrode the brain's choice)
/// with the rules that shaped the brain's choice, in search-events.tsv, and every override listed for reading in
/// overrides.md. Each override points at a rule worth a closer look: the search played the position forward and found
/// the rule's pick leads somewhere worse.
/// </summary>
public static class SearchRecord
{
    public const string FileName = "search-events.tsv";
    public const string OverridesName = "overrides.md";
    private const string Header = "duel\tdeck\topponent\tseat\twon\tversion\tkind\tturn\tphase\trules\tbrain\tbrainMean\tbrainLines\tsearch\tsearchMean\tsearchLines\tlines";
    private const int EventFields = 11;

    public static void AppendDuel(string runFolder, int number, string firstDeck, string secondDeck, int winner, List<string>[] eventsBySeat, int[] versions)
    {
        string file = Path.Combine(runFolder, FileName);
        var lines = new List<string>();
        for (int seat = 0; seat < 2; seat++)
        {
            string deck = seat == 0 ? firstDeck : secondDeck, opponent = seat == 0 ? secondDeck : firstDeck;
            List<string> events;
            lock (eventsBySeat[seat])
                events = eventsBySeat[seat].ToList();
            foreach (var evt in events)
            {
                // kind|turn|phase|rules|brain|brainMean|brainLines|search|searchMean|searchLines|lines
                var parts = evt.Split('|');
                if (parts.Length < EventFields)
                    continue;
                lines.Add(string.Join('\t', new[] { number.ToString(), deck, opponent, seat == 0 ? "first" : "second",
                    winner < 0 ? "" : seat == winner ? "1" : "0", versions[seat].ToString() }.Concat(parts.Take(EventFields))));
            }
        }
        if (lines.Count == 0)
            return;
        if (!File.Exists(file))
            lines.Insert(0, Header);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.AppendAllLines(file, lines);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>Lists every override of the run (overrides.md), duel by duel, with the rules behind the brain's choice.</summary>
    public static void WriteOverrides(string runFolder, TextWriter? console)
    {
        string file = Path.Combine(runFolder, FileName);
        if (!File.Exists(file))
            return;
        var rows = File.ReadLines(file).Skip(1).Select(l => l.Split('\t')).Where(c => c.Length >= 6 + EventFields).ToList();
        var overrides = rows.Where(c => c[6] == "override").ToList();
        static string Pct(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? $"score {v * 100:0}" : s;

        var md = new StringBuilder();
        md.AppendLine("# Search overrides");
        md.AppendLine();
        md.AppendLine($"{overrides.Count} overrides in {rows.Count} searched decisions. Each line: where it happened, the rules that fired");
        md.AppendLine("while the brain chose (the last one usually picked the action), and the brain's choice against the search's,");
        md.AppendLine("with the search's position score for each (0-100, 50 = even: its yardstick for comparing the choices, not a measured");
        md.AppendLine("win chance) and the lines behind it. The duel folder's log has the full decision.");
        md.AppendLine();
        foreach (var duel in overrides.GroupBy(c => c[0]))
        {
            md.AppendLine($"## Duel {duel.Key} (duel-{int.Parse(duel.Key):000})");
            md.AppendLine();
            foreach (var c in duel)
            {
                string won = c[4] == "1" ? "won" : c[4] == "0" ? "lost" : "no result";
                md.AppendLine($"- **{c[1]}** v{c[5]} (went {c[3]}, {won}), turn {c[7]} {c[8]}: brain rules **{(c[9] == "(none)" ? "none (generic scoring)" : c[9].Replace(",", ", "))}**");
                md.AppendLine($"  - brain: {c[10]} {Pct(c[11])} ({c[12]} lines) → search: {c[13]} {Pct(c[14])} ({c[15]} lines), {c[16]} lines in all");
            }
            md.AppendLine();
        }
        File.WriteAllText(Path.Combine(runFolder, OverridesName), md.ToString());
        console?.WriteLine($"Search overrides: {overrides.Count} in {rows.Count} decisions ({Path.Combine(runFolder, OverridesName)})");
    }
}
