using System.Globalization;

namespace BotTest.Game.AI.Brains
{
    /// <summary>A static tunable as Tuning sees it, whatever its number type.</summary>
    internal interface ITuned
    {
        /// <summary>Its place in a TuningProfile's overrides (set when Tuning registers it).</summary>
        int Slot { get; set; }
        Type ValueType { get; }
        object BaseValue { get; set; }
    }

    /// <summary>
    /// A static tunable number (a [Tunable] static field) that each side of a DuelSim duel can see with its own value (self-play tuning,
    /// user 2026-11-24). Reads as a plain double / int: <c>Weight * 2</c>, <c>Math.Max(Weight, 0)</c>. The value is the tuning file's
    /// (Base), unless the side reading it plays with a TuningProfile that overrides it. Only DuelSim sets a profile: live, a read is
    /// one flag check plus Base.
    /// </summary>
    public sealed class Tuned<T> : ITuned, IFormattable where T : struct, IFormattable
    {
        public Tuned(T value) => Base = value;

        /// <summary>The value from the tuning file (or the code's default): what every side sees without a profile.</summary>
        public T Base;

        public int Slot { get; set; } = -1;

        /// <summary>The value the side reading it plays with.</summary>
        public T Value
        {
            get
            {
                if (TuningProfile.AnyInUse && TuningProfile.Current is { } profile && profile.TryGet(Slot, out double value))
                {
                    if (typeof(T) == typeof(int))
                        return (T)(object)(int)Math.Round(value);
                    if (typeof(T) == typeof(double))
                        return (T)(object)value;
                }
                return Base;
            }
        }

        public static implicit operator T(Tuned<T> tuned) => tuned.Value;

        // By value, never by reference: "A == B" between two tunables compares their numbers.
        public static bool operator ==(Tuned<T>? a, Tuned<T>? b) => a is null ? b is null : b is not null && a.Value.Equals(b.Value);
        public static bool operator !=(Tuned<T>? a, Tuned<T>? b) => !(a == b);
        public override bool Equals(object? obj) => obj is Tuned<T> other ? this == other : obj is T value && Value.Equals(value);
        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString() ?? "";
        public string ToString(string? format) => Value.ToString(format, null);
        public string ToString(string? format, IFormatProvider? provider) => Value.ToString(format, provider);
        public string ToString(IFormatProvider? provider) => Value.ToString(null, provider);

        Type ITuned.ValueType => typeof(T);

        object ITuned.BaseValue
        {
            get => Base;
            set => Base = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
    }
}
