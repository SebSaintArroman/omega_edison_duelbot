using System.Globalization;

namespace BotTest.Game.AI.Brains
{
    /// <summary>A static tunable as Tuning sees it, whatever its value type.</summary>
    internal interface ITuned
    {
        /// <summary>Its place in a TuningProfile's overrides (set when Tuning registers it).</summary>
        int Slot { get; set; }
        Type ValueType { get; }
        object BaseValue { get; set; }
        /// <summary>The tuning file's value for a strategic state (StateTuning), or null to drop it.</summary>
        void SetStateValue(TagRules.BotStrategicState state, object? value);
        /// <summary>Set when any profile gives it a value for some state.</summary>
        bool ProfileStates { get; set; }
    }

    /// <summary>
    /// A static tunable (a [Tunable] static field) that each side of a DuelSim duel can see with its own value (self-play tuning, user
    /// 2026-11-24), and each strategic state with its own (StateTuning, user 2026-11-25). Reads as a plain double / int / bool:
    /// <c>Weight * 2</c>, <c>Math.Max(Weight, 0)</c>, <c>if (Flag)</c>. The value, first found: the reading side's profile for the ambient
    /// state, the tuning file's for that state (each along the state's chain: Desperate, then Threatened), the side's profile, the file's
    /// (Base). Live without per-state values, a read is two flag checks plus Base.
    /// </summary>
    public sealed class Tuned<T> : ITuned, IFormattable where T : struct
    {
        public Tuned(T value) => Base = value;

        /// <summary>The value from the tuning file (or the code's default): what every side sees without a profile or a state value.</summary>
        public T Base;

        public int Slot { get; set; } = -1;

        // The tuning file's value per state (index: the state), and whether any state has one.
        private readonly T?[] _stateValues = new T?[StateTuning.StateCount];
        private volatile bool _hasStateValues;
        public bool ProfileStates { get; set; }

        /// <summary>The value the side reading it plays with, in the state it's read in.</summary>
        public T Value
        {
            get
            {
                if ((_hasStateValues || ProfileStates) && StateTuning.AnyStaticStateValues)
                {
                    var state = StateTuning.Current;
                    if (state != TagRules.BotStrategicState.Base)
                        foreach (var s in StateTuning.Chain(state))
                        {
                            if (ProfileStates && TuningProfile.Current is { } p && p.TryGet(Slot, s, out double fromProfile))
                                return From(fromProfile);
                            if (_stateValues[(int)s] is { } fromFile)
                                return fromFile;
                        }
                }
                if (TuningProfile.AnyInUse && TuningProfile.Current is { } profile && profile.TryGet(Slot, out double value))
                    return From(value);
                return Base;
            }
        }

        /// <summary>The value in a given strategic state (where the caller knows the state, rather than the ambient one).</summary>
        internal T In(TagRules.BotStrategicState state)
        {
            if (_hasStateValues || ProfileStates)
                foreach (var s in StateTuning.Chain(state))
                {
                    if (ProfileStates && TuningProfile.Current is { } p && p.TryGet(Slot, s, out double fromProfile))
                        return From(fromProfile);
                    if (_stateValues[(int)s] is { } fromFile)
                        return fromFile;
                }
            if (TuningProfile.AnyInUse && TuningProfile.Current is { } profile && profile.TryGet(Slot, out double value))
                return From(value);
            return Base;
        }

        private static T From(double value)
        {
            if (typeof(T) == typeof(int))
                return (T)(object)(int)Math.Round(value);
            if (typeof(T) == typeof(bool))
                return (T)(object)(value != 0);
            return (T)(object)value;
        }

        public static implicit operator T(Tuned<T> tuned) => tuned.Value;

        // By value, never by reference: "A == B" between two tunables compares their values.
        public static bool operator ==(Tuned<T>? a, Tuned<T>? b) => a is null ? b is null : b is not null && a.Value.Equals(b.Value);
        public static bool operator !=(Tuned<T>? a, Tuned<T>? b) => !(a == b);
        public override bool Equals(object? obj) => obj is Tuned<T> other ? this == other : obj is T value && Value.Equals(value);
        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString() ?? "";
        public string ToString(string? format) => ToString(format, null);
        public string ToString(string? format, IFormatProvider? provider) =>
            Value is IFormattable f ? f.ToString(format, provider) : Value.ToString() ?? "";
        public string ToString(IFormatProvider? provider) => ToString(null, provider);

        Type ITuned.ValueType => typeof(T);

        object ITuned.BaseValue
        {
            get => Base;
            set => Base = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }

        void ITuned.SetStateValue(TagRules.BotStrategicState state, object? value)
        {
            _stateValues[(int)state] = value == null ? null : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            _hasStateValues = _stateValues.Any(v => v.HasValue);
            if (_hasStateValues)
                StateTuning.AnyStaticStateValues = true;
        }
    }
}
