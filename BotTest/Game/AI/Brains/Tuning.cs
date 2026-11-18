using System.Globalization;
using System.Reflection;
using System.Text;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Marks a number the user can tweak in the tuning file (bot-tuning.txt): a weight, a bonus, a penalty, a threshold. Static fields
    /// are set once at startup; instance properties of a brain are set when the brain is built (Tuning.ApplyTo).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class TunableAttribute : Attribute
    {
        public TunableAttribute(string description = "") => Description = description;
        public string Description { get; }

        /// <summary>The file section it's listed under, instead of its class: "Search" for how the bot thinks (line counts, timings,
        /// tree limits), as opposed to rules of play (user, 2026-11-17).</summary>
        public string? Section { get; init; }
    }

    /// <summary>
    /// Every tunable number in one file the user edits by hand (user, 2026-11-16: "I want ALL of these moved to one location that I can
    /// manually tweak"): BotTest/bot-tuning.txt, one line per number, "Class.Name = value   # what it does". The file is found by
    /// walking up from the program's folder (the live bot and DuelSim read the same one); a number missing from it is appended with its
    /// built-in value, so the file always lists everything. Restart the bot (or a DuelSim run) after editing it.
    /// </summary>
    public static class Tuning
    {
        public const string FileName = "bot-tuning.txt";
        private static readonly object Gate = new();
        private static Dictionary<string, string>? _values;
        // The values being loaded (EnsureLoaded in progress on this thread, under Gate).
        private static Dictionary<string, string>? _loading;
        private static readonly HashSet<string> Listed = new(StringComparer.Ordinal);
        private static readonly HashSet<Type> AppliedTypes = new();
        // Old keys of numbers moved into a section (dropped by Tidy).
        private static readonly HashSet<string> Legacy = new(StringComparer.Ordinal);

        /// <summary>The tuning file in use (null when none was found and none could be created).</summary>
        public static string? FilePath { get; private set; }

        /// <summary>Numbers the file set at startup (for the log).</summary>
        public static int Overridden { get; private set; }

        /// <summary>Reads the file and sets every static tunable (once per process).</summary>
        public static void EnsureLoaded()
        {
            if (_values != null)
                return;
            lock (Gate)
            {
                if (_values != null)
                    return;
                if (_loading != null)
                    return; // re-entered while loading (a type whose initializer reads a tunable): it uses _loading
                var values = ReadFile();
                _loading = values;
                var missing = new List<string>();
                foreach (var type in typeof(Tuning).Assembly.GetTypes())
                {
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    foreach (var field in type.GetFields(flags))
                        if (field.GetCustomAttribute<TunableAttribute>() is { } tunable && !field.IsLiteral && !field.IsInitOnly)
                            Apply(type, field.Name, field.FieldType, () => field.GetValue(null), v => field.SetValue(null, v), tunable, values, missing);
                    foreach (var prop in type.GetProperties(flags))
                        if (prop.GetCustomAttribute<TunableAttribute>() is { } tunable && prop.CanWrite)
                            Apply(type, prop.Name, prop.PropertyType, () => prop.GetValue(null), v => prop.SetValue(null, v), tunable, values, missing);
                }
                Append(missing);
                _values = values;
                _loading = null;
            }
        }

        private static readonly List<string> Pending = new();

        /// <summary>A named entry that isn't a field (a rule's hold cost): the file's text for it, else <paramref name="fallback"/>,
        /// listed in the file at the next Flush when it's missing.</summary>
        public static string Entry(string key, string fallback, string description)
        {
            EnsureLoaded();
            lock (Gate)
            {
                if ((_values ?? _loading!).TryGetValue(key, out string? text))
                    return text;
                if (Listed.Add(key))
                    Pending.Add($"{key} = {fallback}" + (description.Length > 0 ? $"   # {description}" : ""));
                return fallback;
            }
        }

        /// <summary>Rewrites the file grouped by class and sorted, every value and description kept (DuelSim tuning).</summary>
        public static void Tidy()
        {
            lock (Gate)
            {
                if (FilePath == null || !File.Exists(FilePath))
                    return;
                // Every file's lines, regrouped: each section into its own file (a deck's into bot-tuning-<deck>.txt), the Search section
                // last in the shared file. (Lines under old keys, "HoldCost.<rule>" or a number since moved to a section, were carried
                // over and are dropped.)
                var files = AllFiles().ToList();
                var entries = files.SelectMany(ReadShared)
                    .Where(l => !l.TrimStart().StartsWith('#') && l.Contains('=') && !l.TrimStart().StartsWith("HoldCost.") &&
                                !l.TrimStart().StartsWith("HoldCosts.")) // the tier settings, gone since every rule has its own number
                    .Select(l => l.Trim()).Where(l => !Legacy.Contains(l[..l.IndexOf('=')].Trim()))
                    .GroupBy(l => l[..l.IndexOf('=')].Trim()).Select(g => g.Last())
                    // A rule's cost as a tier word (older files) becomes that tier's number: every rule has its own score now.
                    .Select(l => l.Contains(".HoldCost.") ? System.Text.RegularExpressions.Regex.Replace(l, @"=\s*(Light|Standard|Strong|Critical)\b",
                        m => "= " + HoldCosts.Number(HoldCosts.Resolve(m.Groups[1].Value)!.Value)) : l)
                    .ToList();
                static bool IsRule(string line) => line.Contains(".HoldCost.");
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in entries.GroupBy(l => PathFor(l[..l.IndexOf('.')].Trim())))
                {
                    var text = new StringBuilder().Append(Header(file.Key == FilePath));
                    foreach (var group in file.GroupBy(l => l[..l.IndexOf('.')].Trim()).OrderBy(g => g.Key == "Search" ? 1 : 0).ThenBy(g => g.Key, StringComparer.Ordinal))
                    {
                        text.AppendLine().AppendLine(group.Key == "Search" ? "# --- Search (how the bot thinks: line counts, timings, tree limits; not rules of play)" : $"# --- {group.Key}");
                        foreach (string line in group.OrderBy(l => IsRule(l) ? 1 : 0).ThenBy(l => l, StringComparer.Ordinal))
                            text.AppendLine(line);
                    }
                    File.WriteAllText(file.Key, text.ToString());
                    written.Add(file.Key);
                }
                foreach (string stale in files.Where(f => !written.Contains(f) && f != FilePath))
                    File.Delete(stale);
            }
        }

        private static string Header(bool shared) => shared
            ? "# Bot tuning: every weight, bonus, penalty and threshold the bot uses, one per line (Section.Name = value).\n" +
              "# Edit a value and restart the bot (or a DuelSim run). Card units: 1 = about one card's worth.\n" +
              "# This file: rules of play every deck shares (DeckBotBrain), the search's scoring, and how the search thinks (Search, at the end).\n" +
              "# Each deck's own numbers and rules are in bot-tuning-<deck>.txt next to this file.\n" +
              "# <Brain>.HoldCost.<rule>: what a search line pays for breaking that rule (card units; each rule its own number).\n" +
              "# A line deleted here comes back with its built-in value the next time the bot starts.\n"
            : "# One deck's own tuning: its numbers and its rules' hold costs (card units; each rule its own number).\n" +
              "# The shared numbers are in bot-tuning.txt. Edit a value and restart the bot (or a DuelSim run).\n";

        /// <summary>The file's text for a key, or null (no listing).</summary>
        public static string? Existing(string key)
        {
            EnsureLoaded();
            lock (Gate)
                return (_values ?? _loading!).TryGetValue(key, out string? text) ? text : null;
        }

        /// <summary>Writes the entries Entry found missing.</summary>
        public static void Flush()
        {
            lock (Gate)
            {
                Append(Pending.ToList());
                Pending.Clear();
            }
        }

        /// <summary>Sets a brain's (or any object's) tunable instance properties from the file; lists the ones the file lacks.</summary>
        /// <param name="sharedDefaults">The shared numbers as they stood before the instance's own constructor ran (Snapshot): a deck brain
        /// that set one of them itself (Blackwing's PushSetTrapFactor) gets it under its own name, in its own file, so the shared line
        /// doesn't overwrite it.</param>
        public static void ApplyTo(object instance, IReadOnlyDictionary<string, object?>? sharedDefaults = null)
        {
            EnsureLoaded();
            lock (Gate)
            {
                var missing = new List<string>();
                var own = instance.GetType();
                bool firstOwn = AppliedTypes.Add(own);
                for (var type = own; type != null && type != typeof(object); type = type.BaseType)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    bool first = type == own ? firstOwn : AppliedTypes.Add(type);
                    foreach (var prop in type.GetProperties(flags))
                    {
                        if (prop.GetCustomAttribute<TunableAttribute>() is not { } tunable || !prop.CanWrite)
                            continue;
                        bool deckOwn = type != own && tunable.Section == null &&
                                       (_values!.ContainsKey($"{own.Name}.{prop.Name}") ||
                                        sharedDefaults != null && sharedDefaults.TryGetValue(prop.Name, out var shared) && !Equals(shared, prop.GetValue(instance)));
                        Apply(deckOwn ? own : type, prop.Name, prop.PropertyType, () => prop.GetValue(instance), v => prop.SetValue(instance, v), tunable,
                            _values!, (deckOwn ? firstOwn : first) ? missing : null);
                    }
                }
                Append(missing);
            }
        }

        /// <summary>An object's tunable instance properties declared on <paramref name="declaredOn"/> and its bases, by name: their values now.</summary>
        public static Dictionary<string, object?> Snapshot(object instance, Type declaredOn)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var type = declaredOn; type != null && type != typeof(object); type = type.BaseType)
                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (prop.GetCustomAttribute<TunableAttribute>() != null && prop.CanRead)
                        values[prop.Name] = prop.GetValue(instance);
            return values;
        }

        private static void Apply(Type type, string name, Type valueType, Func<object?> get, Action<object> set, TunableAttribute tunable,
            Dictionary<string, string> values, List<string>? missing)
        {
            string key = $"{tunable.Section ?? type.Name}.{name}";
            // Moved to a section: a value set under its old key (Class.Name) carries over, and Tidy drops the old line.
            if (tunable.Section != null)
            {
                string legacy = $"{type.Name}.{name}";
                Legacy.Add(legacy);
                if (!values.ContainsKey(key) && values.TryGetValue(legacy, out string? old))
                {
                    values[key] = old;
                    if (missing != null && Listed.Add(key))
                    {
                        string d = tunable.Description.Replace("\r", " ").Replace("\n", " ").Trim();
                        missing.Add($"{key} = {old}" + (d.Length > 0 ? $"   # {d}" : ""));
                    }
                }
            }
            if (values.TryGetValue(key, out string? text))
            {
                if (Parse(text, valueType) is { } parsed)
                {
                    if (!Equals(parsed, get()) && Listed.Add(key))
                        Overridden++;
                    set(parsed);
                }
                return;
            }
            if (missing != null && Listed.Add(key))
            {
                string description = tunable.Description.Replace("\r", " ").Replace("\n", " ").Trim();
                missing.Add($"{key} = {Format(get())}" + (description.Length > 0 ? $"   # {description}" : ""));
            }
        }

        private static object? Parse(string text, Type type)
        {
            text = text.Trim().Replace("_", "");
            try
            {
                if (type == typeof(double))
                    return double.Parse(text, CultureInfo.InvariantCulture);
                if (type == typeof(int))
                    return int.Parse(text, CultureInfo.InvariantCulture);
                if (type == typeof(bool))
                    return bool.Parse(text);
                if (type == typeof(double[]))
                    return text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => double.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
                if (type == typeof(int[]))
                    return text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
            }
            catch (FormatException) { }
            return null;
        }

        private static string Format(object? value) => value switch
        {
            double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
            double[] a => string.Join(", ", a.Select(d => d.ToString("0.#####", CultureInfo.InvariantCulture))),
            int[] a => string.Join(", ", a),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? "",
        };

        // The deck brains (DeckBotBrain's subclasses): each one's section goes to its own file (user, 2026-11-17: "move the deck specific
        // tuning factors into their own files"), bot-tuning-<deck>.txt next to the shared one.
        private static readonly Lazy<HashSet<string>> DeckBrains = new(() =>
            typeof(Tuning).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(DeckBotBrain))).Select(t => t.Name).ToHashSet(StringComparer.Ordinal));

        /// <summary>The file a section's lines belong in: a deck brain's own file, else the shared one.</summary>
        private static string PathFor(string section)
        {
            if (!DeckBrains.Value.Contains(section))
                return FilePath!;
            string deck = section.EndsWith("BotBrain") ? section[..^"BotBrain".Length] : section;
            return Path.Combine(Path.GetDirectoryName(FilePath!)!, $"bot-tuning-{deck}.txt");
        }

        /// <summary>The shared file and every deck file found next to it.</summary>
        private static IEnumerable<string> AllFiles()
        {
            if (FilePath == null)
                yield break;
            if (File.Exists(FilePath))
                yield return FilePath;
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null && Directory.Exists(dir))
                foreach (string deckFile in Directory.GetFiles(dir, "bot-tuning-*.txt").OrderBy(p => p, StringComparer.Ordinal))
                    yield return deckFile;
        }

        private static Dictionary<string, string> ReadFile()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            FilePath = Find();
            foreach (string file in AllFiles())
            foreach (string raw in ReadShared(file))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line[..hash];
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return values;
        }

        private static IEnumerable<string> ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }

        /// <summary>The tuning file: DUELBOT_TUNING, else the first BotTest/bot-tuning.txt (or bot-tuning.txt) found walking up from the
        /// program's folder; else a new one next to the first BotTest folder found (or the program's folder).</summary>
        private static string? Find()
        {
            if (Environment.GetEnvironmentVariable("DUELBOT_TUNING") is { Length: > 0 } fromEnv)
                return fromEnv;
            string? botTest = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string here = Path.Combine(dir.FullName, FileName), inBotTest = Path.Combine(dir.FullName, "BotTest", FileName);
                if (File.Exists(inBotTest))
                    return inBotTest;
                if (File.Exists(here))
                    return here;
                if (botTest == null && Directory.Exists(Path.Combine(dir.FullName, "BotTest")) && File.Exists(Path.Combine(dir.FullName, "BotTest", "BotTest.csproj")))
                    botTest = inBotTest;
            }
            return botTest ?? Path.Combine(AppContext.BaseDirectory, FileName);
        }

        // Adds the numbers the file doesn't list yet (all of them the first time), under a dated header. Two processes (DuelSim runs
        // side by side) may append at once: a named mutex keeps their lines apart.
        private static void Append(List<string> missing)
        {
            if (missing.Count == 0 || FilePath == null)
                return;
            try
            {
                using var mutex = new Mutex(false, "DuelBotTuningFile");
                bool held = mutex.WaitOne(TimeSpan.FromSeconds(5));
                try
                {
                    foreach (var file in missing.GroupBy(l => PathFor(l[..l.IndexOf('.')])))
                    {
                        var text = new StringBuilder();
                        if (!File.Exists(file.Key))
                            text.Append(Header(file.Key == FilePath)).AppendLine();
                        else
                            text.AppendLine().AppendLine($"# added {DateTime.Now:yyyy-MM-dd HH:mm} (new in the code since the file was written)");
                        foreach (var group in file.GroupBy(l => l[..l.IndexOf('.')]).OrderBy(g => g.Key, StringComparer.Ordinal))
                        {
                            text.AppendLine($"# --- {group.Key}");
                            foreach (string line in group.OrderBy(l => l, StringComparer.Ordinal))
                                text.AppendLine(line);
                        }
                        File.AppendAllText(file.Key, text.ToString());
                    }
                }
                finally
                {
                    if (held)
                        mutex.ReleaseMutex();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
