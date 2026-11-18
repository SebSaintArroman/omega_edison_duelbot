using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmegaBotWrapper;
using OmegaBotWrapper.Heuristics;

namespace DuelSim.Tuning;

/// <summary>
/// The tunable numbers of a deck brain: its public read/write int and double properties (e.g. PushHandFactorPerCard,
/// SetCardIsMirrorForceChance, HoldWhenClockAtMost). Their values in the code are the defaults; a variant overrides some.
/// </summary>
public static class BrainParams
{
    /// <summary>The brain inside a bot (DuelBotWrapper -> adapter -> IBotBrain), found by reflection.</summary>
    public static IBotBrain? FindBrain(DuelBotWrapper bot) => Find(bot, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static IBotBrain? Find(object obj, int depth, HashSet<object> seen)
    {
        if (obj is IBotBrain brain)
            return brain;
        if (depth > 3 || !seen.Add(obj))
            return null;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.GetValue(obj) is not { } value)
                    continue;
                if (Find(value, depth + 1, seen) is { } found)
                    return found;
            }
        }
        return null;
    }

    /// <summary>The tunable properties of a brain type (public instance int/double with a public setter).</summary>
    public static List<PropertyInfo> Tunables(Type brainType) =>
        brainType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.SetMethod!.IsPublic && (p.PropertyType == typeof(int) || p.PropertyType == typeof(double)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Rule probabilities are tuned as parameters named "rule:&lt;rule name&gt;" (0-1).</summary>
    public const string RulePrefix = "rule:";

    public static bool IsRule(string name) => name.StartsWith(RulePrefix, StringComparison.Ordinal);

    /// <summary>The current values (for a fresh brain: the code's defaults), properties and rule probabilities.</summary>
    public static SortedDictionary<string, double> Read(IBotBrain brain)
    {
        var values = new SortedDictionary<string, double>(
            Tunables(brain.GetType()).ToDictionary(p => p.Name, p => Convert.ToDouble(p.GetValue(brain), CultureInfo.InvariantCulture)), StringComparer.Ordinal);
        if (brain is BotTest.Game.AI.Brains.DeckBotBrain deck)
            foreach (var rule in BotTest.Game.AI.Brains.RuleGates.Declared(brain.GetType()))
                values[RulePrefix + rule] = deck.Rules.Probability(rule);
        return values;
    }

    /// <summary>The type of each tunable value (rules are doubles).</summary>
    public static Dictionary<string, Type> Types(IBotBrain brain)
    {
        var types = Tunables(brain.GetType()).ToDictionary(p => p.Name, p => p.PropertyType);
        foreach (var rule in BotTest.Game.AI.Brains.RuleGates.Declared(brain.GetType()))
            types[RulePrefix + rule] = typeof(double);
        return types;
    }

    /// <summary>Sets the given values on the brain (unknown names are reported, not fatal).</summary>
    public static void Apply(IBotBrain brain, IReadOnlyDictionary<string, double> values, Action<string>? warn = null)
    {
        var props = Tunables(brain.GetType()).ToDictionary(p => p.Name);
        var rules = BotTest.Game.AI.Brains.RuleGates.Declared(brain.GetType()).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (IsRule(name))
            {
                string rule = name[RulePrefix.Length..];
                if (brain is BotTest.Game.AI.Brains.DeckBotBrain deck && rules.Contains(rule))
                    deck.Rules.Set(rule, value);
                else
                    warn?.Invoke($"unknown rule '{rule}' (renamed or removed?) - ignored");
                continue;
            }
            if (!props.TryGetValue(name, out var prop))
            {
                warn?.Invoke($"unknown parameter '{name}' (renamed or removed from the brain?) - ignored");
                continue;
            }
            prop.SetValue(brain, prop.PropertyType == typeof(int) ? (object)(int)Math.Round(value) : value);
        }
    }

    /// <summary>
    /// A mutated copy: <paramref name="count"/> random parameters changed. Doubles are scaled by e^N(0, strength)
    /// (a value of 0 moves by ±0.1 instead); ints move by ±1 when small (under 10), else scale like doubles.
    /// Probability-like values (names with Chance, Residual, Factor, Min, Max, Share, Weight under 1) stay in [0, 1].
    /// </summary>
    public static (SortedDictionary<string, double> Values, List<string> Changes) Mutate(
        IReadOnlyDictionary<string, double> current, IReadOnlyDictionary<string, Type> types, int count, double strength, Random rng)
    {
        var result = new SortedDictionary<string, double>(current.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        // Shuffle, then change parameters until `count` have actually changed (a rule already at 1 can't go higher).
        var names = current.Keys.OrderBy(_ => rng.Next()).ToList();
        var changes = new List<string>();
        foreach (var name in names)
        {
            if (changes.Count >= count)
                break;
            double old = current[name];
            bool isInt = types.TryGetValue(name, out var t) && t == typeof(int);
            double value;
            if (IsRule(name))
                // Rule probabilities move additively: e.g. 1 -> 0.85, or a switched-off candidate 0 -> 0.15.
                value = Math.Clamp(old + Gaussian(rng) * strength * 0.5, 0, 1);
            else if (isInt && Math.Abs(old) < 10)
                value = Math.Max(0, old + (rng.Next(2) == 0 ? -1 : 1));
            else if (old == 0)
                value = Math.Max(0, (rng.Next(2) == 0 ? -0.1 : 0.1));
            else
                value = old * Math.Exp(Gaussian(rng) * strength);
            if (IsProbability(name, old))
                value = Math.Clamp(value, 0, 1);
            if (isInt)
                value = Math.Round(value);
            else
                value = Math.Round(value, 4);
            if (value == old)
                continue;
            result[name] = value;
            changes.Add($"{name}: {Format(old)} -> {Format(value)}");
        }
        return (result, changes);
    }

    private static bool IsProbability(string name, double value) =>
        value is >= 0 and <= 1 &&
        new[] { "Chance", "Residual", "Factor", "Min", "Max", "Share", "Weight" }.Any(word => name.Contains(word, StringComparison.Ordinal));

    private static double Gaussian(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The differences between two parameter sets, for logs ("name: a -> b").</summary>
    public static List<string> Diff(IReadOnlyDictionary<string, double> from, IReadOnlyDictionary<string, double> to) =>
        to.Where(kv => !from.TryGetValue(kv.Key, out var old) || Math.Abs(old - kv.Value) > 1e-9)
          .Select(kv => $"{kv.Key}: {(from.TryGetValue(kv.Key, out var o) ? Format(o) : "?")} -> {Format(kv.Value)}")
          .ToList();
}

/// <summary>
/// A tuned variant of a deck bot, stored as DuelSim/tuning/&lt;name&gt;.json: the deck it's based on and the parameter
/// values it plays with (the full set, so the file shows everything). The base deck itself always plays the code's defaults.
/// </summary>
public sealed class Variant
{
    [JsonProperty("base")] public string Base { get; set; } = "";
    [JsonProperty("note")] public string? Note { get; set; }
    [JsonProperty("params")] public SortedDictionary<string, double> Params { get; set; } = new(StringComparer.Ordinal);

    public static string PathFor(SimPaths paths, string name) => System.IO.Path.Combine(paths.Tuning, $"{name}.json");

    public static Variant? TryLoad(SimPaths paths, string name)
    {
        var file = PathFor(paths, name);
        return File.Exists(file) ? JsonConvert.DeserializeObject<Variant>(File.ReadAllText(file)) : null;
    }

    public void Save(SimPaths paths, string name)
    {
        Directory.CreateDirectory(paths.Tuning);
        File.WriteAllText(PathFor(paths, name), JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
