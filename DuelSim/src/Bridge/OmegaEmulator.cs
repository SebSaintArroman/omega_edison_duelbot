using System.Net;
using System.Reflection;
using System.Text;
using DuelSim.Data;
using DuelSim.Engine;
using Newtonsoft.Json;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace DuelSim.Bridge;

/// <summary>
/// Which seat (engine team) the current bot task plays. Set before starting a bot; flows through its awaits, so
/// every API call the bot makes, including the static ones on OmegaApiClient.Default, is routed to its seat.
/// </summary>
internal static class SimContext
{
    public static readonly AsyncLocal<int?> Seat = new();
}

/// <summary>
/// In-memory stand-in for Omega's bot API (http://127.0.0.1:19999). It is installed as the HttpClient handler of
/// OmegaApiClient.Default, so the bots run unmodified: same endpoints, same JSON, no network or ports involved.
/// </summary>
internal sealed class OmegaEmulator : HttpMessageHandler
{
    private readonly CardStore _cards;
    private readonly bool[] _ready = new bool[2];
    private DuelHost? _host;

    public OmegaEmulator(CardStore cards) => _cards = cards;

    /// <summary>Points OmegaApiClient.Default (used by every bot) at this emulator.</summary>
    public void Install()
    {
        var field = typeof(OmegaApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(OmegaApiClient), "_http");
        field.SetValue(OmegaApiClient.Default, new HttpClient(this) { Timeout = TimeSpan.FromSeconds(30) });
    }

    public void Attach(DuelHost host)
    {
        _host = host;
        _ready[0] = _ready[1] = false;
    }

    public void Detach() => _host = null;

    public bool BothReady => _ready[0] && _ready[1];

    /// <summary>
    /// Longest a bot's state poll is held waiting for its next prompt (long-poll). The bots then poll without sleeping
    /// (DuelBotWrapper.HeartbeatDelayMs = 0) and see each decision the moment it exists.
    /// </summary>
    public static readonly TimeSpan LongPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>Per seat: how long its answers are held back (ms), to play like a human thinking (0 = at once).</summary>
    public static readonly int[] ThinkMs = new int[2];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Long-poll: a bot asking for its state with nothing pending waits for its next prompt instead of being told
        // "nothing" and sleeping before asking again.
        var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (path.Length > 1 && path[0] == "get-bot-state-header" && _host is { } host && int.TryParse(path[1], out int bot) && bot is 0 or 1)
            await host.WaitForPromptAsync(bot, LongPoll, cancellationToken);
        // A seat that answers like a human (--think-b): its answers arrive this much later, the other bot's dead time.
        if (path.Length > 1 && path[0] == "set-bot-response" && int.TryParse(path[1], out int answering) && answering is 0 or 1 && ThinkMs[answering] > 0)
            await Task.Delay(ThinkMs[answering], cancellationToken);
        return Respond(request);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        HttpResponseMessage response;
        try
        {
            response = Route(request.RequestUri!);
        }
        catch (Exception ex)
        {
            response = Text(HttpStatusCode.InternalServerError, ex.Message);
        }
        return response;
    }

    private HttpResponseMessage Route(Uri uri)
    {
        var parts = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        int seat = SimContext.Seat.Value ?? 0;
        var host = _host;
        int Arg(int i) => int.Parse(parts[i]);

        switch (parts[0])
        {
            case "bot-list":
                return Json(new BotList { BotCount = 1, Bots = new[] { new BotInfo { BotID = (byte)seat, DeckIndex = 0, DeckName = string.Empty, DeckHash = string.Empty } } });

            case "get-bot-state-header":
                return Json(HeaderFor(Arg(1)));

            case "get-bot-state":
            {
                int bot = Arg(1);
                var header = HeaderFor(bot);
                object data = host?.Pending(bot) is { } prompt && (byte)prompt.State == header.CurrentStateCode
                    ? prompt.Data
                    : new BotGeneralStateData { IsReady = _ready[bot] };
                return Json(new Dictionary<string, object> { ["CurrentStateHeader"] = header, ["CurrentStateData"] = data });
            }

            case "set-bot-data":
                _ready[Arg(1)] = true;
                return Json(new ResponseReason { Code = 200, Reason = "OK" });

            case "set-bot-response":
            {
                var packet = FromSafeBase64(parts.Length > 2 ? parts[2] : string.Empty);
                bool accepted = host?.Submit(Arg(1), packet) ?? false;
                return Json(new ResponseReason { Code = accepted ? 200 : 409, Reason = accepted ? "OK" : "nothing pending" });
            }

            case "bot-field-info":
                return host == null ? Text(HttpStatusCode.NotFound, "No duel") : Json(host.FieldView(Arg(1)));

            case "field-info":
                // The human player's view on Omega; here, the opponent of the calling bot.
                return host == null ? Text(HttpStatusCode.NotFound, "No duel") : Json(host.FieldView(1 - seat));

            case "bot-field-card-info":
                return CardInfo(host, Arg(1), Arg(2), Arg(3), Arg(4));

            case "field-card-info":
                return CardInfo(host, 1 - seat, Arg(1), Arg(2), Arg(3));

            case "card-list":
            {
                var filters = parts.Length > 1 && parts[1].Length > 0
                    ? parts[1].Split(',').Select(s => (int)long.Parse(s)).ToArray()
                    : Array.Empty<int>();
                var matches = CardsMatching(filters);
                return Json(new DBCardList { Cards = matches.Select(c => c.ToDbCardInfo()).ToArray() });
            }

            case "current-deck":
                return Json(new DeckData { Name = string.Empty, Hash = string.Empty, Main = Array.Empty<int>(), Extra = Array.Empty<int>(), Side = Array.Empty<int>() });

            case "rematch-duel":
                return Text(HttpStatusCode.OK, "OK");

            default:
                return Text(HttpStatusCode.NotFound, $"Unknown endpoint {uri.AbsolutePath}");
        }
    }

    private BotStateHeader HeaderFor(int bot)
    {
        if (_host is { } host && _ready[bot])
            return host.Header(bot);
        return new BotStateHeader
        {
            CurrentStateCode = (byte)(_ready[bot] ? BotState.None : BotState.WaitingForDeck),
            CurrentStateName = (_ready[bot] ? BotState.None : BotState.WaitingForDeck).ToString(),
            LastChainPlayer = -1,
            LastSummonPlayer = -1,
        };
    }

    private HttpResponseMessage CardInfo(DuelHost? host, int viewer, int controller, int location, int sequence)
    {
        var card = host?.CardAt(viewer, controller, location, sequence);
        return card == null ? Text(HttpStatusCode.NotFound, "No card found") : Json(card.Value);
    }

    private IEnumerable<CardRecord> CardsMatching(int[] filters)
    {
        // Fast path for the bots' "this exact id" lookups: [id, ISCODE].
        if (filters.Length == 2 && filters[1] == 0x40000100)
            return _cards.Get(filters[0]) is { } card ? new[] { card } : Array.Empty<CardRecord>();
        return _cards.All.Where(card => CardFilter.Matches(filters, card)).Take(500);
    }

    private static byte[] FromSafeBase64(string safe)
    {
        if (safe.Length == 0)
            return Array.Empty<byte>();
        var b64 = safe.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Convert.FromBase64String(b64);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(HttpStatusCode status, string text) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
}
