using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.Logging;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.CardId;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// A Vayu and a compatible non-Tuner Blackwing in the graveyard count as one live summon for search.
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private readonly record struct CardBits(string Name, int Type, int Level, int Attack);

        internal Func<DuelSim.Engine.SideSummary, Action<int>?, int>? SearchGraveSetup =>
            VayuPartnerIds.Length > 0 ? CountVayuPairs : null;

        /// <summary>Count live pairs and optionally report the two cards reserved for each pair.</summary>
        private static int CountVayuPairs(DuelSim.Engine.SideSummary own, Action<int>? reserveCard)
        {
            if (!Search.SearchHost.Available)
                return default;
            static CardBits? Card(int code) => Search.SearchHost.Cards.Get(code) is { } r ? new CardBits(r.Name ?? "", r.Type, r.Level, r.Attack) : null;
            var vayus = own.GraveCodes.Where(code => Search.SearchHost.Is(code, BlackwingVayuTheEmblemOfHonor)).ToList();
            if (vayus.Count == 0)
                return default;
            var partners = own.GraveCodes.Where(code => !Search.SearchHost.Is(code, BlackwingVayuTheEmblemOfHonor))
                .Select(code => (Code: code, Card: Card(code)))
                .Where(c => c.Card is { } p && p.Name.StartsWith("Blackwing ", StringComparison.Ordinal) && (p.Type & 0x1) != 0 && (p.Type & 0x1000) == 0)
                .ToList();
            var synchros = own.ExtraCodes.Select(Card).Where(c => c is { } s && s.Name.StartsWith("Blackwing ", StringComparison.Ordinal) && (s.Type & 0x2000) != 0)
                .Select(c => c!.Value).ToList();
            int pairs = 0;
            foreach (int vayu in vayus)
            {
                // The best synchro any remaining partner makes with this Vayu (Level 1 + the partner's Level).
                var best = partners
                    .SelectMany(partner => synchros.Where(s => s.Level == 1 + partner.Card!.Value.Level).Select(s => (Partner: partner, Synchro: s)))
                    .OrderByDescending(x => x.Synchro.Attack).FirstOrDefault();
                if (best.Synchro.Name == null)
                    break;
                partners.Remove(best.Partner);
                synchros.Remove(best.Synchro);
                pairs++;
                reserveCard?.Invoke(vayu);
                reserveCard?.Invoke(best.Partner.Code);
            }
            return pairs;
        }

        /// <summary>The non-Tuners a Vayu in our graveyard pairs with for its synchro (the deck's Blackwings). Default: none.</summary>
        protected virtual int[] VayuPartnerIds => Array.Empty<int>();
    }
}
