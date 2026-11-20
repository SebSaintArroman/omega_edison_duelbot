using System.Globalization;
using System.Text;

namespace OmegaBotWrapper.Logging
{
    /// <summary>
    /// The score breakdown of one move on offer at a decision (user, 2026-11-20: "a complete breakdown of what went into the main decision
    /// ... nice and visual in a table"; "the full 'best' line it's following, with a confidence interval on each score"; the alternative
    /// moves with the same breakdown), from the search's note
    /// "score breakdown|move|score|lines|loss rate|lethal exposure|name=value;...|role|score interval|step^step..." with each step
    /// "label~us/them~now/next~score~interval~lines" (card units, 95% intervals, invariant numbers). The played move comes first, then the
    /// others by score. The .log gets text tables; the AI Room page draws its own from the terminal's "breakdown:" lines.
    /// </summary>
    public sealed class ScoreBreakdown
    {
        public const string Prefix = "score breakdown|";

        /// <param name="Rollout">Past the search tree's end: the brain's own play in a line (no score of its own).</param>
        public sealed record Step(string Label, bool Ours, bool TheirTurn, double Score, double Interval, int Lines, bool Rollout = false);

        /// <summary>Joins a part and its sub-row in a part's name ("Our cards (hand, field, graveyard) :: Hand"): sub-rows split a part
        /// (how the two sides' cards are worked out) and aren't counted again in the totals.</summary>
        public const string SubPart = " :: ";

        public string Move { get; private init; } = "";
        public double Score { get; private init; }
        public double Interval { get; private init; }
        public int Lines { get; private init; }
        public double LossRate { get; private init; }
        public double Lethal { get; private init; }
        public bool Played { get; private init; }
        public List<(string Name, double Value)> Parts { get; } = new();
        public List<Step> Line { get; } = new();

        private IEnumerable<(string Name, double Value)> TopLevel => Parts.Where(p => !p.Name.Contains(SubPart));
        public IEnumerable<(string Name, double Value)> Positive => TopLevel.Where(p => p.Value > 0).OrderByDescending(p => p.Value);
        public IEnumerable<(string Name, double Value)> Negative => TopLevel.Where(p => p.Value < 0).OrderBy(p => p.Value);
        public double Net => TopLevel.Sum(p => p.Value);

        /// <summary>A part's sub-rows (its name before SubPart), largest first, by their own names.</summary>
        public IEnumerable<(string Name, double Value)> SubRows(string part) =>
            Parts.Where(p => p.Name.StartsWith(part + SubPart, StringComparison.Ordinal))
                .Select(p => (p.Name.Substring(part.Length + SubPart.Length), p.Value)).OrderByDescending(p => Math.Abs(p.Value));

        private static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public static ScoreBreakdown? Parse(string note)
        {
            if (!note.StartsWith(Prefix, StringComparison.Ordinal))
                return null;
            var fields = note.Substring(Prefix.Length).Split('|');
            if (fields.Length < 6)
                return null;
            var breakdown = new ScoreBreakdown
            {
                Move = fields[0],
                Score = Num(fields[1]),
                Lines = (int)Num(fields[2]),
                LossRate = Num(fields[3]),
                Lethal = Num(fields[4]),
                Played = fields.Length < 7 || fields[6] == "played",
                Interval = fields.Length > 7 ? Num(fields[7]) : 0,
            };
            foreach (var row in fields[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int at = row.LastIndexOf('=');
                if (at > 0)
                    breakdown.Parts.Add((row.Substring(0, at), Num(row.Substring(at + 1))));
            }
            if (fields.Length > 8)
                foreach (var step in fields[8].Split('^', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = step.Split('~');
                    if (f.Length >= 6)
                        breakdown.Line.Add(new Step(f[0], f[1] == "us", f[2] == "next", Num(f[3]), Num(f[4]), (int)Num(f[5]), f.Length > 6 && f[6] == "rollout"));
                }
            return breakdown;
        }

        private static string Signed(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        private static string Plain(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>A one-line summary: the move, its score with its interval, the net of its parts, its line.</summary>
        public string ToShortText(string first) =>
            $"{first}{(Played ? "played" : "alternative")}: {Move} (score {Signed(Score)} ±{Plain(Interval)}, net {Signed(Net)}, {Lines} line(s))" +
            (Line.Count > 1 ? $"; line: {string.Join(" -> ", Line.Select(s => s.Label))}" : "") + Environment.NewLine;

        /// <summary>The breakdown as an aligned text table and its line: a heading line (after <paramref name="first"/>), then rows indented by <paramref name="indent"/>.</summary>
        public string ToText(string first, string indent)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{first}score breakdown, {(Played ? "played" : "alternative")}: {Move} (score {Signed(Score)} ±{Plain(Interval)} at 95%; " +
                          $"parts averaged over {Lines} line(s), card units against the position now)");
            int width = Math.Max(36, TopLevel.Select(p => p.Name.Length).DefaultIfEmpty(0).Max()) + 2;
            void Section(string title, IEnumerable<(string Name, double Value)> rows)
            {
                var list = rows.ToList();
                sb.AppendLine($"{indent}{title.PadRight(width + 2)}{Signed(list.Sum(r => r.Value)),8}");
                if (list.Count == 0)
                    sb.AppendLine($"{indent}  (none)");
                foreach (var (name, value) in list)
                {
                    sb.AppendLine($"{indent}  {name.PadRight(width)}{Signed(value),8}");
                    // How the part is made up (the two sides' cards: life points, monsters, sets, hand, graveyard...).
                    foreach (var (sub, subValue) in SubRows(name).Where(r => Math.Abs(r.Value) >= 0.005))
                        sb.AppendLine($"{indent}      - {sub.PadRight(Math.Max(0, width - 6))}{Signed(subValue),8}");
                }
            }
            Section("Positive contributions", Positive);
            Section("Negative contributions", Negative);
            sb.AppendLine($"{indent}{"Net contribution".PadRight(width + 2)}{Signed(Net),8}");
            if (LossRate > 0 || Lethal > 0)
                sb.AppendLine($"{indent}(not in the table: {LossRate:P0} of its lines lost outright, scored 0" +
                              (Lethal > 0 ? $"; lethal risk from the board left {Lethal:P0} on average, which scales a line's score down" : "") + ")");
            if (Line.Count > 0)
            {
                sb.AppendLine($"{indent}Best line (score ±95% interval, lines behind each step):");
                int labelWidth = Math.Min(60, Line.Max(s => s.Label.Length)) + 2;
                for (int i = 0; i < Line.Count; i++)
                {
                    var s = Line[i];
                    string who = (s.Ours ? "us" : "them") + (s.TheirTurn ? ", their turn" : "");
                    sb.AppendLine(s.Rollout
                        ? $"{indent}  {i + 1,2}. {s.Label.PadRight(labelWidth)}{"",7}  {"",-6} {"",6}        ({who}; past the tree: the brain's play in its line, no score)"
                        : $"{indent}  {i + 1,2}. {s.Label.PadRight(labelWidth)}{Signed(s.Score),7} ±{Plain(s.Interval),-6} {s.Lines,6} lines  ({who})");
                }
            }
            return sb.ToString();
        }
    }
}
