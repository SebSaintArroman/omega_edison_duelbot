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
    /// Allure of Darkness: which DARK to banish after the draw (kept clear of what Dark Armed Dragon needs).
    /// </summary>
    public abstract partial class DeckBotBrain
    {
        private static bool IsDarkMonsterId(int id) =>
            (Search.SearchHost.Type(id) & 0x1) != 0 && Search.SearchHost.Attribute(id) == 0x20;

        /// <summary>What Allure's banish pick looks at: our hand, field and graveyard.</summary>
        protected sealed record AllureView(List<DynamicCard> Hand, List<DynamicCard> OurMonsters, List<int> GraveIds);

        /// <summary>
        /// Allure of Darkness' banish from the hand, lower = banished first: the DARK least useful with the cards we have now,
        /// not a fixed order (user, 2026-11-13: "it should depend on which is most useful with the other current cards you
        /// have"; Allure is a staple, so this is shared):
        ///  - a spare copy first;
        ///  - Gorz needs an empty field: cheap with monsters out;
        ///  - Dark Armed Dragon: the right banish when the graveyard has too many DARKs to ever trim back to 3 (DadStuck), a
        ///    bad one at or near 3;
        ///  - Kalut (a boost) needs a Blackwing to boost; Blizzard (a revive) needs a Blackwing in the graveyard;
        ///  - otherwise by ATK. Decks override for their own engines (Machina's Jinzo pieces).
        /// </summary>
        protected virtual double AllureBanishRank(DynamicCard card, AllureView v)
        {
            bool Named(DynamicCard c, string name) => CardNames.Get(c.ID).Contains(name, StringComparison.OrdinalIgnoreCase);
            if (v.Hand.Count(other => other.IsCode(card.ID)) > 1)
                return 0;
            if (card.IsCode(GorzTheEmissaryOfDarkness))
                return v.OurMonsters.Count > 0 ? AllureRankGorzWithMonsters : AllureRankGorz;
            if (card.IsCode(DarkArmedDragon))
            {
                int darks = DadTrimRoom(v.GraveIds).Darks;
                return DadStuck(v.GraveIds) ? AllureRankDadStuck : Math.Abs(darks - 3) <= 1 ? AllureRankDadNearLive : darks == 1 ? AllureRankDadOneDark : AllureRankDadOther;
            }
            if (Named(card, "Kalut"))
                return v.OurMonsters.Any(c => c.IsFaceup() && Named(c, "Blackwing")) ||
                       v.Hand.Any(c => Named(c, "Blackwing") && !Named(c, "Kalut") && c.Level <= 4) ? AllureRankKalutUseful : AllureRankKalutIdle;
            if (Named(card, "Blizzard"))
                return v.GraveIds.Any(id => CardNames.Get(id).Contains("Blackwing") && (Search.SearchHost.Available && Search.SearchHost.Cards.Get(id) is { } revive ? revive.Level : 4) <= 4) ? AllureRankBlizzardUseful : AllureRankBlizzardIdle;
            if (Named(card, "Vayu"))
                return AllureRankVayu;
            return AllureRankOther - card.Attack / 10000.0;
        }


        /// <summary>
        /// DARK monsters in our graveyard, and how many our graveyard can trim off (a Vayu with a partner banishes 2,
        /// Plaguespreader leaving it 1, Necro Gardna banishing itself 1, a Blizzard revive target 1). Dark Armed Dragon is
        /// stuck when the DARKs minus the trims are still above 3 (user, 2026-11-13: then banishing DAD to Allure is right;
        /// at or near 3 it's a much worse choice).
        /// </summary>
        protected static (int Darks, int Trims) DadTrimRoom(IEnumerable<int> graveIds)
        {
            var ids = graveIds.ToList();
            bool Dark(int id) => (Search.SearchHost.Type(id) & 0x1) != 0 && Search.SearchHost.Attribute(id) == 0x20;
            bool Named(int id, string name) => CardNames.Get(id).Contains(name, StringComparison.OrdinalIgnoreCase);
            int darks = ids.Count(Dark);
            int vayus = ids.Count(id => Named(id, "Vayu"));
            int partners = ids.Count(id => Dark(id) && (Search.SearchHost.Type(id) & 0x1000) == 0 && !Named(id, "Vayu"));
            int trims = 2 * Math.Min(vayus, partners) + (ids.Any(id => Named(id, "Plaguespreader")) ? 1 : 0) +
                        (ids.Any(id => Named(id, "Necro Gardna")) ? 1 : 0) +
                        (ids.Any(id => Named(id, "Blackwing")) && ids.Any(id => Dark(id) && Named(id, "Blackwing")) ? 1 : 0);
            return (darks, trims);
        }

        /// <summary>Dark Armed Dragon can't get back to 3 DARKs from this graveyard (see DadTrimRoom).</summary>
        protected static bool DadStuck(IEnumerable<int> graveIds) => DadTrimRoom(graveIds) is var room && room.Darks - room.Trims > 3;

        protected async Task<IList<byte>?> AllureBanishPickAsync(DecisionContext<SelectCardData> context, bool force = false)
        {
            if (!force && _lastStapleActivated != AllureOfDarkness || (DHint)context.StateData.SelectionHint != DHint.REMOVE)
                return null;
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();
            if (cards.Length == 0 || cards.Any(card => card.CurrentCoordinates.Location != (byte)CardLocation.Hand))
                return null;
            _lastStapleActivated = 0;
            var view = new AllureView(await GetBotHandCardsAsync(), (await GetBotMonsterCardsAsync()).Where(c => c.ID > 0).ToList(),
                (await GetBotGraveyardCardsAsync()).Select(c => c.ID).ToList());
            var ranked = cards.Select((card, i) => (Card: card, Index: i, Rank: AllureBanishRank(card, view))).OrderBy(x => x.Rank).ToList();
            var pick = ranked.First();
            context.Bot.LogNote($"deck brain: Allure of Darkness banishes {CardNames.Get(pick.Card.ID)} (least useful now: " +
                                $"{string.Join(", ", ranked.Select(x => $"{CardNames.Get(x.Card.ID)} {x.Rank:0.#}"))})");
            return new List<byte> { (byte)pick.Index };
        }
    }
}
