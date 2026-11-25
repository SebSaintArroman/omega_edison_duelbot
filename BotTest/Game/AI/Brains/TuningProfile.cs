using System.Globalization;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// One side's own tuning values in a DuelSim duel (self-play tuning, user 2026-11-24): a file in the bot-tuning.txt format
    /// ("Key = value   # comment") that lists only the numbers it changes. Keys are the tuning file's: a static number
    /// (BoardEvaluator.SetTrapBase), a brain's own (VayuBotBrain.X), a rule's hold cost (DeckBotBrain.HoldCost.&lt;rule&gt;).
    /// The side's async flow carries it (Use), so its brain, its search twins and its search threads all read these values while the
    /// other side reads its own; numbers the profile doesn't list stay the tuning file's.
    /// </summary>
    public sealed class TuningProfile
    {
        private static readonly AsyncLocal<TuningProfile?> CurrentLocal = new();

        /// <summary>Set once any profile is in use: until then a tunable's read skips the async-flow lookup.</summary>
        internal static volatile bool AnyInUse;

        /// <summary>The profile of the side running this code, or null (the tuning file's values).</summary>
        public static TuningProfile? Current => AnyInUse ? CurrentLocal.Value : null;

        /// <summary>This async flow (and every task and thread it starts from now on) plays with <paramref name="profile"/>.</summary>
        public static void Use(TuningProfile? profile)
        {
            if (profile != null)
                AnyInUse = true;
            CurrentLocal.Value = profile;
        }

        private readonly double[] _slots;
        private readonly bool[] _has;
        // Per strategic state (index: the state): "Key@State" lines (StateTuning).
        private readonly Dictionary<int, double>[] _stateSlots = Enumerable.Range(0, StateTuning.StateCount).Select(_ => new Dictionary<int, double>()).ToArray();

        private TuningProfile(string name, Dictionary<string, double> values)
        {
            Name = name;
            Values = values;
            var statics = Tuning.Statics();
            _slots = new double[statics.Count];
            _has = new bool[statics.Count];
            foreach (var (key, value) in values)
            {
                if (StateTuning.TrySplit(key, out string baseKey, out var state))
                {
                    if (statics.TryGetValue(baseKey, out var stateTuned) && stateTuned.Slot >= 0)
                    {
                        _stateSlots[(int)state][stateTuned.Slot] = value;
                        stateTuned.ProfileStates = true;
                        StateTuning.AnyStaticStateValues = true;
                    }
                }
                else if (statics.TryGetValue(key, out var tuned) && tuned.Slot >= 0)
                {
                    _slots[tuned.Slot] = value;
                    _has[tuned.Slot] = true;
                }
            }
        }

        /// <summary>The file's name, for logs.</summary>
        public string Name { get; }

        /// <summary>Every value the profile sets, by tuning key.</summary>
        public IReadOnlyDictionary<string, double> Values { get; }

        internal bool TryGet(int slot, out double value)
        {
            if (slot >= 0 && slot < _has.Length && _has[slot])
            {
                value = _slots[slot];
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>This profile's value of a static number for one strategic state ("Key@State"), if it sets one.</summary>
        internal bool TryGet(int slot, TagRules.BotStrategicState state, out double value) =>
            _stateSlots[(int)state].TryGetValue(slot, out value);

        /// <summary>Reads a profile file (bot-tuning.txt format; lines without a number are reported in <paramref name="warn"/>).</summary>
        public static TuningProfile Load(string path, Action<string>? warn = null)
        {
            Tuning.EnsureLoaded();
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line[..hash];
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line[..eq].Trim(), text = line[(eq + 1)..].Trim().Replace("_", "");
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    values[key] = value;
                else if (bool.TryParse(text, out bool flag))
                    values[key] = flag ? 1 : 0;
                else
                    warn?.Invoke($"{Path.GetFileName(path)}: '{key}' has no number ('{text}') - ignored");
            }
            return new TuningProfile(Path.GetFileNameWithoutExtension(path), values);
        }

        /// <summary>The keys this profile sets that no tunable has (renamed or removed?), once every brain has been tuned.</summary>
        public IEnumerable<string> UnknownKeys() =>
            Values.Keys.Where(k => !Tuning.IsKnownKey(StateTuning.TrySplit(k, out string baseKey, out _) ? baseKey : k));
    }
}
