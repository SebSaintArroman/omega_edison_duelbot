using OmegaBotWrapper;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Per-state parameters (user, 2026-11-25: "for all of these parameters, I want them separately tunable per state ... rather than having
    /// state dependant workarounds like heavy storm fear, the state will just flip to the state's set of parameters"). Every tunable may have
    /// a value per strategic state, written in the tuning file as <c>Key@State = value</c> next to its base line (<c>Key = value</c>):
    /// <c>Mcts.HeavyStormFear@Desperate = 0</c>, <c>DeckBotBrain.HoldCost.deck.x@Conservative = 2.25</c>, <c>DeckBotBrain.Rule.deck.y@Desperate = 0</c>.
    /// A state without its own value falls back along its chain: Desperate to Threatened (Desperate always acts Threatened), then the base.
    /// Which state applies:
    ///  - a brain's own numbers (its [Tunable] properties), rule probabilities and hold costs: that brain's strategic state (a search line's
    ///    brain: its state at that point of the line), switched at the start of every decision (DeckBotBrain.ApplyStateTuning);
    ///  - the shared static numbers (Tuned&lt;T&gt;): the ambient state: the live bot's state in its own flow, the root's state inside a
    ///    search (Mcts.Run / CheckLine), so every line of one search is scored with one set of numbers.
    /// </summary>
    internal static class StateTuning
    {
        public const char Separator = '@';

        /// <summary>The states a value can be given for (Base is the plain key).</summary>
        public static readonly TagRules.BotStrategicState[] States =
        {
            TagRules.BotStrategicState.Threatened, TagRules.BotStrategicState.Desperate,
            TagRules.BotStrategicState.Pressuring, TagRules.BotStrategicState.Conservative,
        };

        public const int StateCount = 5;

        /// <summary>Where a state's values come from, in order (the base value is the caller's last resort): Desperate, then Threatened.</summary>
        public static ReadOnlySpan<TagRules.BotStrategicState> Chain(TagRules.BotStrategicState state) => state switch
        {
            TagRules.BotStrategicState.Desperate => DesperateChain,
            TagRules.BotStrategicState.Threatened => ThreatenedChain,
            TagRules.BotStrategicState.Pressuring => PressuringChain,
            TagRules.BotStrategicState.Conservative => ConservativeChain,
            _ => ReadOnlySpan<TagRules.BotStrategicState>.Empty,
        };
        private static readonly TagRules.BotStrategicState[] DesperateChain = { TagRules.BotStrategicState.Desperate, TagRules.BotStrategicState.Threatened };
        private static readonly TagRules.BotStrategicState[] ThreatenedChain = { TagRules.BotStrategicState.Threatened };
        private static readonly TagRules.BotStrategicState[] PressuringChain = { TagRules.BotStrategicState.Pressuring };
        private static readonly TagRules.BotStrategicState[] ConservativeChain = { TagRules.BotStrategicState.Conservative };

        /// <summary>"Key@State" split into its base key and state; false for a plain key (or an unknown state name).</summary>
        public static bool TrySplit(string key, out string baseKey, out TagRules.BotStrategicState state)
        {
            int at = key.LastIndexOf(Separator);
            baseKey = key;
            state = TagRules.BotStrategicState.Base;
            if (at <= 0 || !Enum.TryParse(key[(at + 1)..].Trim(), ignoreCase: false, out state) || state == TagRules.BotStrategicState.Base)
            {
                state = TagRules.BotStrategicState.Base;
                return false;
            }
            baseKey = key[..at].Trim();
            return true;
        }

        public static string Key(string baseKey, TagRules.BotStrategicState state) => $"{baseKey}{Separator}{state}";

        // ------------------------------------------------------------------ the ambient state (shared static numbers)

        private sealed class Holder
        {
            public volatile byte State;
        }

        private static readonly AsyncLocal<Holder?> Ambient = new();

        /// <summary>Set once any static number has a value for some state: until then a read skips the ambient lookup.</summary>
        internal static volatile bool AnyStaticStateValues;

        /// <summary>The state whose static numbers apply in this flow (Base outside any bot or search).</summary>
        public static TagRules.BotStrategicState Current => (TagRules.BotStrategicState)(Ambient.Value?.State ?? 0);

        private static int _installed;

        /// <summary>
        /// Each bot's run (DuelBotWrapper.Start) gets its own ambient state, which its brain switches at every decision: everything that
        /// bot runs (its decisions, the searches it starts) sees that bot's state, and two bots in one process (DuelSim) never share it.
        /// </summary>
        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1)
                return;
            var previous = DuelBotWrapper.FlowStarting;
            DuelBotWrapper.FlowStarting = bot =>
            {
                previous?.Invoke(bot);
                Ambient.Value = new Holder();
            };
        }

        /// <summary>The live bot's state changed: its flow's static numbers follow (a no-op outside a bot's run).</summary>
        public static void SetBotState(TagRules.BotStrategicState state)
        {
            if (Ambient.Value is { } holder)
                holder.State = (byte)state;
        }

        /// <summary>A search's own ambient state for its duration (Mcts.Run, CheckLine): its lines all read the root's numbers. Synchronous
        /// callers only (dispose on the same flow).</summary>
        public static Scope Use(TagRules.BotStrategicState state)
        {
            var before = Ambient.Value;
            Ambient.Value = new Holder { State = (byte)state };
            return new Scope(before);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly object? _before;
            internal Scope(object? before) => _before = before;
            public void Dispose() => Ambient.Value = (Holder?)_before;
        }
    }
}
