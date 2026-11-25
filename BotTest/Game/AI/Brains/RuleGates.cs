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
    /// A rule that holds a resource back (a spend: Machina Fortress's summon, Vayu, a synchro): its built-in hold cost in the Conservative
    /// strategic state is ConservativeHoldFactor times its base (user, 2026-11-24), written to the tuning file as its own
    /// <c>&lt;Brain&gt;.HoldCost.&lt;rule&gt;@Conservative</c> line (StateTuning). Declared on the rule's constant in any deck's rule set:
    /// <c>[ResourceHold] public const string X = "...";</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ResourceHoldAttribute : Attribute
    {
    }

    /// <summary>
    /// A rule that plays around Heavy Storm or their traps (holding sets for Storm, no chip into backrow, defence under Mirror Force, the
    /// Torrential test, a likely Bottomless): its built-in probability in the Desperate strategic state is 0 (user, 2026-11-24: "It should
    /// however remove the fear of heavy storm and traps"), written to the tuning file as <c>&lt;Brain&gt;.Rule.&lt;rule&gt;@Desperate = 0</c>
    /// (StateTuning). Declared on the rule's constant in any deck's rule set: <c>[TrapFear] public const string X = "...";</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TrapFearAttribute : Attribute
    {
    }

    /// <summary>
    /// A rule that only fires in these strategic states: its built-in probability is 0, and 1 in each state named (written to the tuning
    /// file as <c>&lt;Brain&gt;.Rule.&lt;rule&gt; = 0</c> and <c>...@State = 1</c>; StateTuning). <c>[InStates("Conservative")]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class InStatesAttribute : Attribute
    {
        public InStatesAttribute(params string[] states) => States = states;
        public string[] States { get; }
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
    /// Also each rule's hold cost for the search (see HoldCostAttribute). Both may differ per strategic state (StateTuning: the tuning
    /// file's <c>&lt;Brain&gt;.Rule.&lt;rule&gt;@State</c> and <c>&lt;Brain&gt;.HoldCost.&lt;rule&gt;@State</c>, or this brain's own Set / SetCost with
    /// a "rule@State" name); a state without its own value uses its chain (Desperate: Threatened), then the base.
    /// </summary>
    public sealed class RuleGates
    {
        // This brain's own values (tuning: a profile, an evolve variant, the twin's copy), by rule or "rule@State".
        private readonly Dictionary<string, double> _probabilities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _costs = new(StringComparer.Ordinal);

        private sealed record FileRule(string? Cost, string?[] StateCosts, string? Probability, string?[] StateProbabilities);

        // Rule name -> the tuning file's cost and probability, base and per state (with the built-in ones: its declared tier, else
        // Standard; [ResourceHold]'s Conservative cost, [TrapFear]'s Desperate 0, [InStates]). Every rule of the RuleSet classes gets a cost
        // line in the file (<Brain>.HoldCost.<rule>); per-state lines and probabilities only where they differ from the base.
        private static readonly Lazy<Dictionary<string, FileRule>> FileRules = new(() =>
        {
            var rules = new Dictionary<string, FileRule>(StringComparer.Ordinal);
            double conservativeFactor = Tuning.Existing("StandingState.ConservativeHoldFactor") is { } f &&
                double.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) ? factor : 1.5;
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
                    string costKey = $"{brain}.HoldCost.{rule}", probabilityKey = $"{brain}.Rule.{rule}";
                    string cost = Tuning.Entry(costKey, fallback, "");
                    var stateCosts = new string?[StateTuning.StateCount];
                    var stateProbabilities = new string?[StateTuning.StateCount];
                    string? probability = Tuning.Existing(probabilityKey);
                    var inStates = field.GetCustomAttribute<InStatesAttribute>()?.States ?? Array.Empty<string>();
                    if (probability == null && inStates.Length > 0)
                        probability = Tuning.Entry(probabilityKey, "0", "the rule's probability outside the states it's for");
                    foreach (var state in StateTuning.States)
                    {
                        string? costDefault = state == TagRules.BotStrategicState.Conservative && field.GetCustomAttribute<ResourceHoldAttribute>() != null &&
                                              HoldCosts.Resolve(cost) is double baseCost ? HoldCosts.Number(baseCost * conservativeFactor) : null;
                        string stateCostKey = StateTuning.Key(costKey, state);
                        stateCosts[(int)state] = Tuning.Existing(stateCostKey) ??
                            (costDefault != null ? Tuning.Entry(stateCostKey, costDefault, $"a resource hold, in the {state} state") : null);
                        string? probabilityDefault =
                            state == TagRules.BotStrategicState.Desperate && field.GetCustomAttribute<TrapFearAttribute>() != null ? "0"
                            : inStates.Contains(state.ToString()) ? "1" : null;
                        string stateProbabilityKey = StateTuning.Key(probabilityKey, state);
                        stateProbabilities[(int)state] = Tuning.Existing(stateProbabilityKey) ??
                            (probabilityDefault != null ? Tuning.Entry(stateProbabilityKey, probabilityDefault, $"the rule's probability in the {state} state") : null);
                    }
                    rules[rule] = new FileRule(cost, stateCosts, probability, stateProbabilities);
                }
            }
            Tuning.Flush();
            return rules;
        });

        private static double? Number(string? text) => text == null ? null : HoldCosts.Resolve(text);

        /// <summary>What a search line pays for a move this rule held back (card units), in the base state.</summary>
        public double Cost(string rule) => Cost(rule, TagRules.BotStrategicState.Base);

        /// <summary>What a search line pays for a move this rule held back (card units), in this strategic state: this brain's value for the
        /// state (along its chain), the file's for it, this brain's base value, the file's, the declared tier, Standard.</summary>
        internal double Cost(string rule, TagRules.BotStrategicState state)
        {
            FileRules.Value.TryGetValue(rule, out var file);
            foreach (var s in StateTuning.Chain(state))
            {
                if (_costs.TryGetValue(StateTuning.Key(rule, s), out double own))
                    return own;
                if (Number(file?.StateCosts[(int)s]) is double fromFile)
                    return fromFile;
            }
            return _costs.TryGetValue(rule, out double c) ? c : DefaultCost(rule);
        }

        /// <summary>This brain's own cost for a rule ("rule" or "rule@State").</summary>
        public void SetCost(string rule, double cost) => _costs[rule] = Math.Max(0, cost);

        /// <summary>A rule's hold cost from the tuning file (a tier or a number), else its declared tier, else HoldCosts.Standard.</summary>
        public static double DefaultCost(string rule) =>
            FileRules.Value.TryGetValue(rule, out var file) && Number(file.Cost) is double c ? c : HoldCosts.Standard;

        /// <summary>Rules whose probability isn't 1 by default (candidate rules written switched off).</summary>
        public static readonly Dictionary<string, double> DefaultOverrides = new(StringComparer.Ordinal);

        /// <summary>A rule's probability in the base state.</summary>
        public double Probability(string rule) => Probability(rule, TagRules.BotStrategicState.Base);

        /// <summary>A rule's probability in this strategic state: as Cost, this brain's then the file's for the state, then the base.</summary>
        internal double Probability(string rule, TagRules.BotStrategicState state)
        {
            FileRules.Value.TryGetValue(rule, out var file);
            foreach (var s in StateTuning.Chain(state))
            {
                if (_probabilities.TryGetValue(StateTuning.Key(rule, s), out double own))
                    return own;
                if (Number(file?.StateProbabilities[(int)s]) is double fromFile)
                    return Math.Clamp(fromFile, 0, 1);
            }
            return _probabilities.TryGetValue(rule, out double p) ? p : Default(rule);
        }

        /// <summary>This brain's own probability for a rule ("rule" or "rule@State").</summary>
        public void Set(string rule, double probability) => _probabilities[rule] = Math.Clamp(probability, 0, 1);

        /// <summary>Every value this brain set itself (a search twin copies its brain's).</summary>
        public void CopyFrom(RuleGates other)
        {
            foreach (var (rule, p) in other._probabilities)
                _probabilities[rule] = p;
            foreach (var (rule, c) in other._costs)
                _costs[rule] = c;
        }

        /// <summary>Rolls the rule in this strategic state: true when it fires. Rules at 1 never roll.</summary>
        internal bool Roll(string rule, TagRules.BotStrategicState state, out double probability)
        {
            probability = Probability(rule, state);
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

        /// <summary>The default probability of a rule: the tuning file's base line, else 1 unless it's a switched-off candidate.</summary>
        public static double Default(string rule) =>
            FileRules.Value.TryGetValue(rule, out var file) && Number(file.Probability) is double fromFile ? Math.Clamp(fromFile, 0, 1)
            : DefaultOverrides.TryGetValue(rule, out double d) ? d : 1.0;
    }
}
