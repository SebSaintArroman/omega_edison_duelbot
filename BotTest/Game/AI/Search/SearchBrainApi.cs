using OmegaBotWrapper.States;
using DuelSim.Data;
using DuelSim.Engine;
using OmegaBotWrapper;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Search
{
    /// <summary>
    /// The server API as a brain sees it, answered straight from a search duel: the board and card views the server
    /// would give player 0 (us), with no network or JSON. A brain playing our side of an imagined line reads the
    /// imagined board through this. One per search thread; Host is the duel of the line being played.
    /// </summary>
    internal sealed class SearchBrainApi : OmegaApiClient
    {
        public DuelHost? Host { get; set; }

        /// <summary>Added to the search duel's turn number, so the brain sees the real turn (the rebuilt turn is turn 1).</summary>
        public int TurnOffset { get; set; }

        private DuelHost Duel => Host ?? throw new InvalidOperationException("search brain API used outside a line");

        public SearchBrainApi() : base("http://search.invalid") { }

        public BotStateHeader Header()
        {
            var header = Duel.Header(0);
            header.CurrentTurn += TurnOffset;
            return header;
        }

        public override Task<BotStateHeader> GetBotStateHeader(int botId, CancellationToken ct = default) => Task.FromResult(Header());

        // The board can't change while the brain makes one decision, but it re-reads the whole field before every card it looks at
        // (2026-11-17 profile: a third of the brain's time in the lines). Each view is read once per decision; Reset starts the next.
        private readonly Task<FieldInfo>?[] _fields = new Task<FieldInfo>?[2];
        private readonly Dictionary<(int Viewer, int Controller, int Location, int Sequence), Task<DynamicCard>> _cards = new();

        /// <summary>Forgets the board views read so far: call whenever the line's duel may have moved on (a new decision, a new line).</summary>
        public void Reset()
        {
            _fields[0] = _fields[1] = null;
            _cards.Clear();
        }

        private Task<FieldInfo> Field(int viewer) => _fields[viewer] ??= Task.FromResult(Duel.FieldView(viewer));

        private Task<DynamicCard> Card(int viewer, int controller, int location, int sequence)
        {
            var key = (viewer, controller, location, sequence);
            if (_cards.TryGetValue(key, out var task))
                return task;
            var card = Duel.CardAt(viewer, controller, location, sequence);
            return _cards[key] = card is { } found
                ? Task.FromResult(found)
                : Task.FromException<DynamicCard>(new HttpRequestException("No card found", null, System.Net.HttpStatusCode.NotFound));
        }

        public override Task<FieldInfo> GetBotFieldInfo(int botId, CancellationToken ct = default) => Field(0);

        public override Task<FieldInfo> GetPlayerFieldInfo(CancellationToken ct = default) => Field(1);

        public override Task<DynamicCard> GetBotFieldCardInfo(int botId, int controller, int location, int sequence, CancellationToken ct = default) =>
            Card(0, controller, location, sequence);

        public override Task<DynamicCard> GetPlayerFieldCardInfo(int controller, int location, int sequence, CancellationToken ct = default) =>
            Card(1, controller, location, sequence);

        public override Task<DBCardList> GetMatchingCards(int[]? filters = null, CancellationToken ct = default)
        {
            filters ??= Array.Empty<int>();
            var cards = SearchHost.Cards;
            IEnumerable<CardRecord> matches = filters.Length == 2 && filters[1] == 0x40000100 // the bots' "this exact id" lookup
                ? cards.Get(filters[0]) is { } one ? new[] { one } : Array.Empty<CardRecord>()
                : cards.All.Where(card => CardFilter.Matches(filters, card)).Take(500);
            return Task.FromResult(new DBCardList { Cards = matches.Select(c => c.ToDbCardInfo()).ToArray() });
        }
    }
}
