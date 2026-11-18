using OmegaBotWrapper.Logging;
using OmegaBotWrapper.Struct;
using System.Collections.Concurrent;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Cached lookups of card data (name, effect text, stats) from Omega's card database.
    /// Uses the /card-list endpoint with the YGOPro announce filter "push code, ISCODE".
    /// </summary>
    public static class CardDatabase
    {
        private const int OpcodeIsCode = 0x40000100;
        private static readonly ConcurrentDictionary<int, DBCardInfo?> _cache = new();

        /// <summary>Card data for an id, or null if unknown. Each id is fetched from Omega at most once.</summary>
        public static async Task<DBCardInfo?> GetAsync(int id)
        {
            if (id <= 0)
                return null;
            if (_cache.TryGetValue(id, out var cached))
                return cached;

            try
            {
                var list = await DuelBotWrapper.GetMatchingCards(id, OpcodeIsCode);
                DBCardInfo? info = list.Cards?.FirstOrDefault(card => card.ID == id) is { ID: > 0 } match ? match : null;
                // Server-only ids (e.g. pre-errata versions, 80604092 for Ultimate Offering) may be missing from the
                // database: fall back to the official card the server reports as its alias.
                if (info == null && _aliases.TryGetValue(id, out int alias) && alias != id)
                    info = await GetAsync(alias);
                _cache[id] = info;
                if (info is DBCardInfo found && !string.IsNullOrWhiteSpace(found.Name))
                    CardNames.Set(id, found.Name);
                return info;
            }
            catch
            {
                // Omega not reachable: don't cache, try again next time.
                return null;
            }
        }

        /// <summary>
        /// Adds a card from another source (the play search's own card database) when the cache doesn't have it yet. Lets
        /// card-text code (EffectEvaluator) run without asking the server; the server's data wins when it's already cached.
        /// </summary>
        public static void Seed(DBCardInfo info)
        {
            if (info.ID <= 0)
                return;
            // A cached miss (null: the server didn't know the id) is filled in too.
            var kept = _cache.AddOrUpdate(info.ID, info, (_, old) => old ?? info);
            if (kept is DBCardInfo k && k.ID == info.ID && !string.IsNullOrWhiteSpace(k.Name))
                CardNames.Set(info.ID, k.Name);
        }

        private static readonly ConcurrentDictionary<int, int> _aliases = new();

        /// <summary>
        /// Remembers the official card behind a server id (DynamicCard.Alias), so lookups of ids the database doesn't
        /// know still find the card's name and text. Only call it for cards whose id is visible (face-up / ours).
        /// </summary>
        public static void RegisterAlias(int id, int alias)
        {
            if (id <= 0 || alias <= 0 || id == alias || !_aliases.TryAdd(id, alias))
                return;
            // A miss cached before we knew the alias: look it up again next time.
            if (_cache.TryGetValue(id, out var cached) && cached == null)
                _cache.TryRemove(id, out _);
        }

        public static bool TryGetCached(int id, out DBCardInfo info)
        {
            if (_cache.TryGetValue(id, out var cached) && cached is DBCardInfo found)
            {
                info = found;
                return true;
            }
            info = default;
            return false;
        }

        /// <summary>Fetches (and registers names for) any ids not seen before. Safe to call often.</summary>
        public static async Task EnsureAsync(IEnumerable<int> ids, int maxNewLookups = 20)
        {
            int lookups = 0;
            foreach (int id in ids.Where(id => id > 0).Distinct())
            {
                if (_cache.ContainsKey(id))
                    continue;
                if (lookups++ >= maxNewLookups)
                    break;
                await GetAsync(id);
            }
        }
    }
}
