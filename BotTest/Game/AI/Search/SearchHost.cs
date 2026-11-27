using DuelSim;
using DuelSim.Data;
using DuelSim.Engine;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The bot's own mini simulator: the EDOPro rules engine (ocgcore.dll, 32-bit), its card database and scripts,
    /// shared with DuelSim (DuelSim/vendor). Omega only shows a bot the board, so the play search rebuilds positions
    /// here. Unavailable (search off) when the process isn't 32-bit or the files are missing.
    /// </summary>
    internal static class SearchHost
    {
        private sealed record Stores(CardStore? Cards, ScriptStore? Scripts, string? Problem);

        private static readonly Lazy<Stores> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly HashSet<int> Registered = new();
        private static readonly object RegisterLock = new();

        public static bool Available => Loaded.Value.Cards != null;

        /// <summary>Why the search can't run (for the log), or null.</summary>
        public static string? Problem => Loaded.Value.Problem;

        public static CardStore Cards => Loaded.Value.Cards ?? throw new InvalidOperationException(Problem);
        public static ScriptStore Scripts => Loaded.Value.Scripts ?? throw new InvalidOperationException(Problem);

        /// <summary>
        /// Makes sure the engine can play these ids: Omega's own ids (pre-errata copies, official + 20) play as the
        /// official card (see OmegaIds).
        /// </summary>
        public static void Register(IEnumerable<int> ids)
        {
            if (!Available)
                return;
            lock (RegisterLock)
            {
                var fresh = ids.Where(id => id > 0 && Registered.Add(id)).ToList();
                if (fresh.Count > 0)
                    OmegaIds.Resolve(fresh, Cards, Scripts);
            }
        }

        /// <summary>
        /// The official id behind an Omega id (an alternate art's alias, or official + 20 for Omega's own copies), 0 when it
        /// is the official id or unknown. Used by IsCode when a prompt leaves the alias empty.
        /// </summary>
        public static int CanonicalId(int code)
        {
            if (code <= 0 || !Available)
                return 0;
            var card = Cards.Get(code);
            if (card is { Alias: > 0 })
                return card.Alias;
            return card == null && code > 20 && Cards.Get(code - 20) != null ? code - 20 : 0;
        }

        /// <summary>Whether a card in a search duel is this card (Omega's own ids, official + 20, and alt arts count).</summary>
        public static bool Is(int code, int id) =>
            code == id || code == id + 20 || Available && Cards.Get(code)?.Alias == id;

        /// <summary>A card's attribute (ATTRIBUTE_*: 0x20 = DARK) and type flags (0x1 = monster), or 0 when unknown.</summary>
        public static int Attribute(int code) => Available ? Cards.Get(code)?.Attribute ?? 0 : 0;
        public static long Race(int code) => Available ? Cards.Get(code)?.Race ?? 0 : 0;
        public static int Type(int code) => Available ? Cards.Get(code)?.Type ?? 0 : 0;

        /// <summary>Whether the engine knows the card (data and script), after Register.</summary>
        public static bool Knows(int id) => Available && id > 0 && Cards.Get(id) != null;

        private static Stores Load()
        {
            try
            {
                // (32-bit or 64-bit: the ocgcore.dll next to the bot must match; a mismatch fails to load below.)
                var root = FindSimulatorRoot();
                if (root == null)
                    return new Stores(null, null, "DuelSim folder not found (set DUELBOT_SIM_ROOT to it)");
                var paths = new SimPaths(root);
                if (!File.Exists(paths.CardDb) || !Directory.Exists(paths.Scripts))
                    return new Stores(null, null, $"card database or scripts missing under {paths.Vendor}");
                OcgDuel.Version(); // loads ocgcore.dll (throws when it's missing)
                var cards = CardStore.Load(paths.CardDbs);
                var scripts = new ScriptStore(paths.Scripts, paths.CustomScripts);
                return new Stores(cards, scripts, null);
            }
            catch (Exception ex)
            {
                return new Stores(null, null, $"rules engine unavailable: {ex.Message}");
            }
        }

        private static string? FindSimulatorRoot()
        {
            var configured = Environment.GetEnvironmentVariable("DUELBOT_SIM_ROOT");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "DuelSim.csproj")))
                return configured;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DuelSim.csproj")))
                    return dir.FullName;
                var sibling = Path.Combine(dir.FullName, "DuelSim");
                if (File.Exists(Path.Combine(sibling, "DuelSim.csproj")))
                    return sibling;
            }
            return null;
        }
    }
}
