using System.Reflection;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Marks a static class whose public const string fields are rule names (e.g. VayuRules, DeckRules). Every rule a
    /// brain can apply is declared there, so tools (DuelSim's evolve) can list them all up front.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RuleSetAttribute : Attribute
    {
        public RuleSetAttribute(params Type[] appliesTo) => AppliesTo = appliesTo;

        /// <summary>The brain types (and their subclasses) these rules belong to.</summary>
        public Type[] AppliesTo { get; }
    }

    /// <summary>
    /// How expensive a rule is for the play search to break (card units a line pays for a move the rule held back; see
    /// DeckBotBrain.Hold). Declare it on the rule's constant: <c>[HoldCost(HoldCosts.Strong)] public const string X = "...";</c>.
    /// Rules without one cost HoldCosts.Standard. Raise a rule's tier when the user says it matters more ("this is a very
    /// important rule"), lower it for a mild preference. Tuning can also set a cost per brain (RuleGates.SetCost).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HoldCostAttribute : Attribute
    {
        public HoldCostAttribute(double cost) => Cost = cost;
        public double Cost { get; }
    }

    /// <summary>
    /// A rule that holds a resource back (a spend: Machina Fortress's summon, Vayu, a synchro): in the Conservative strategic state its
    /// hold costs StandingState.ConservativeHoldFactor times as much (user, 2026-11-24). Declared on the rule's constant in any deck's
    /// rule set, so the generic state turns them all up together: <c>[ResourceHold] public const string X = "...";</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ResourceHoldAttribute : Attribute
    {
    }

    /// <summary>
    /// A rule that plays around Heavy Storm or their traps (holding sets for Storm, no chip into backrow, defence under Mirror Force, the
    /// Torrential test, a likely Bottomless): it doesn't fire in the Desperate strategic state (user, 2026-11-24: "It should however remove
    /// the fear of heavy storm and traps"). Declared on the rule's constant in any deck's rule set: <c>[TrapFear] public const string X = "...";</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TrapFearAttribute : Attribute
    {
    }

    /// <summary>The tiers of HoldCost (card units; a card in hand is about 0.5-1, a 2000 body about 2.2).</summary>
    public static class HoldCosts
    {
        /// <summary>A mild preference: a line breaks it for about a card's worth of gain.</summary>
        public const double Light = 0.75;
        /// <summary>The default: a strong suggestion (SuggestionCost).</summary>
        public const double Standard = 1.5;
        /// <summary>An important rule: only a clearly better line (a kill, a big swing) breaks it.</summary>
        public const double Strong = 3;
        /// <summary>Nearly a ban: only lethal or avoiding a loss should break it.</summary>
        public const double Critical = 6;

        // The tiers are only a rule's starting cost: in the tuning file every rule has its own number (user, 2026-11-17: "give every
        // rule its own score ... I won't want to do big sweeping tier changes"). A tier word left in the file still reads as its value.

        /// <summary>A declared cost as the tuning file writes it.</summary>
        public static string Number(double declared) => declared.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>A number (or a tier word, from older files) from the tuning file, in card units (null when it's neither).</summary>
        public static double? Resolve(string text) => text.Trim() switch
        {
            "Light" => Light,
            "Standard" => Standard,
            "Strong" => Strong,
            "Critical" => Critical,
            var t => double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null,
        };
    }

    /// <summary>
    /// The probability each rule fires with when its conditions hold. Defaults to 1 (always, as written); tuning can
    /// lower it, which also makes the bot a little less predictable. A rule at 0 is switched off (candidate rules start there).
    /// Also each rule's hold cost for the search (see HoldCostAttribute).
    /// </summary>
    public sealed class RuleGates
    {
        private readonly Dictionary<string, double> _probabilities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _costs = new(StringComparer.Ordinal);
        private readonly Random _random = new();

        // Rule name -> its cost as the tuning file gives it (a tier name or a number), else its declared tier (HoldCostAttribute), else
        // Standard. Every rule of the RuleSet classes gets a line in the file (HoldCost.<rule>), its doc comment as the description.
        private static readonly Lazy<Dictionary<string, string>> RuleCosts = new(() =>
        {
            var costs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var type in typeof(RuleGates).Assembly.GetTypes().Where(t => t.GetCustomAttribute<RuleSetAttribute>() != null))
            {
                // Filed under the brain the rule set belongs to (user, 2026-11-17: generic rules under DeckBotBrain, a deck's own under
                // that deck's brain).
                var appliesTo = type.GetCustomAttribute<RuleSetAttribute>()!.AppliesTo;
                string brain = appliesTo.Length > 0 ? appliesTo[0].Name : nameof(DeckBotBrain);
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                {
                    string rule = (string)field.GetRawConstantValue()!;
                    double declared = field.GetCustomAttribute<HoldCostAttribute>()?.Cost ?? HoldCosts.Standard;
                    // A value set under the old key (HoldCost.<rule>, before 2026-11-17) carries over.
                    string fallback = Tuning.Existing($"HoldCost.{rule}") ?? HoldCosts.Number(declared);
                    costs[rule] = Tuning.Entry($"{brain}.HoldCost.{rule}", fallback, "");
                }
            }
            Tuning.Flush();
            return costs;
        });

        /// <summary>What a search line pays for a move this rule held back (card units): set per brain, else the tuning file's, else
        /// as declared, else Standard.</summary>
        public double Cost(string rule) =>
            _costs.TryGetValue(rule, out double c) ? c : DefaultCost(rule);

        public void SetCost(string rule, double cost) => _costs[rule] = Math.Max(0, cost);

        /// <summary>A rule's hold cost from the tuning file (a tier or a number), else its declared tier, else HoldCosts.Standard.</summary>
        public static double DefaultCost(string rule) =>
            RuleCosts.Value.TryGetValue(rule, out string? text) && HoldCosts.Resolve(text) is double c ? c : HoldCosts.Standard;

        // Rules tagged [ResourceHold] / [TrapFear], from every RuleSet class.
        private static HashSet<string> Tagged<TAttribute>() where TAttribute : Attribute =>
            typeof(RuleGates).Assembly.GetTypes().Where(t => t.GetCustomAttribute<RuleSetAttribute>() != null)
                .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.GetCustomAttribute<TAttribute>() != null)
                .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        private static readonly Lazy<HashSet<string>> ResourceHolds = new(Tagged<ResourceHoldAttribute>);
        private static readonly Lazy<HashSet<string>> TrapFears = new(Tagged<TrapFearAttribute>);

        /// <summary>Whether this rule holds a resource back ([ResourceHold]): it costs more in the Conservative strategic state.</summary>
        public static bool IsResourceHold(string rule) => ResourceHolds.Value.Contains(rule);

        /// <summary>Whether this rule plays around Heavy Storm or their traps ([TrapFear]): it doesn't fire in the Desperate strategic state.</summary>
        public static bool IsTrapFear(string rule) => TrapFears.Value.Contains(rule);

        /// <summary>Rules whose probability isn't 1 by default (candidate rules written switched off).</summary>
        public static readonly Dictionary<string, double> DefaultOverrides = new(StringComparer.Ordinal);

        public double Probability(string rule) =>
            _probabilities.TryGetValue(rule, out double p) ? p : DefaultOverrides.TryGetValue(rule, out double d) ? d : 1.0;

        public void Set(string rule, double probability) => _probabilities[rule] = Math.Clamp(probability, 0, 1);

        /// <summary>Rolls the rule: true when it fires. Rules at 1 never roll.</summary>
        public bool Roll(string rule, out double probability)
        {
            probability = Probability(rule);
            return probability >= 0.5; // no roll (user, 2026-11-17): a rule set to fire half the time or more fires, below that it does not
        }

        /// <summary>Every rule declared for this brain type (RuleSet classes in its assembly that apply to it).</summary>
        public static List<string> Declared(Type brainType)
        {
            var names = new List<string>();
            foreach (var type in brainType.Assembly.GetTypes())
            {
                if (type.GetCustomAttribute<RuleSetAttribute>() is not { } set || !set.AppliesTo.Any(t => t.IsAssignableFrom(brainType)))
                    continue;
                names.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                    .Select(f => (string)f.GetRawConstantValue()!));
            }
            return names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>The default probability of a rule (1, unless it's a switched-off candidate).</summary>
        public static double Default(string rule) => DefaultOverrides.TryGetValue(rule, out double d) ? d : 1.0;
    }
}
