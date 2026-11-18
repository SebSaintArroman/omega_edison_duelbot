using BotTest.Web;

// The AI Room shows the full terminal output (each decision with its reasoning): copy the console from the start.
TerminalCapture.Install();

var builder = WebApplication.CreateBuilder(args);
// Only warnings and errors from ASP.NET itself: its per-request "info" lines (every /api/state poll) flooded the terminal.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.AddSingleton<BotControlService>();

var app = builder.Build();

app.UseDefaultFiles();
// The AI Room's files change often while the bots are worked on: the browser must check for a new copy every time
// (without this it kept an old styles.css for days and the page showed unstyled).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapGet("/api/options", (BotControlService controlService) =>
{
    return Results.Ok(controlService.GetOptions());
});

app.MapGet("/api/state", (BotControlService controlService) =>
{
    return Results.Ok(controlService.GetState());
});

app.MapPost("/api/selection", (SelectionRequest request, BotControlService controlService) =>
{
    var result = controlService.UpdateSelection(request.Format, request.DeckId);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/start", (BotControlService controlService) =>
{
    var result = controlService.StartDuel();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/auto", (bool enabled, BotControlService controlService) => Results.Ok(controlService.SetAuto(enabled)));

app.MapPost("/api/duel/rematch", (BotControlService controlService) =>
{
    var result = controlService.RequestRematch();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/reset", (BotControlService controlService) =>
{
    var result = controlService.ResetSelection();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/logs/clear", (BotControlService controlService) =>
{
    var result = controlService.ClearRecentEvents();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

// The AI Room page polls the terminal every second (nothing else does): the launcher asks whether a page is open
// (/api/page-open) before opening another one.
long lastPagePoll = 0;
app.MapGet("/api/terminal", (long? after) =>
{
    Interlocked.Exchange(ref lastPagePoll, Environment.TickCount64);
    return Results.Ok(TerminalCapture.After(after ?? 0));
});
// withinMs: how recent a poll counts (a background tab's timers are throttled, up to a minute between polls).
app.MapGet("/api/page-open", (long? withinMs) =>
{
    long last = Interlocked.Read(ref lastPagePoll);
    return Results.Ok(new { open = last != 0 && Environment.TickCount64 - last < (withinMs ?? 5000) });
});

app.MapGet("/api/comments", (BotControlService controlService) => Results.Ok(controlService.GetComments()));

app.MapPost("/api/comments", (CommentRequest request, BotControlService controlService) =>
{
    var result = controlService.AddComment(request.Text);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapFallbackToFile("index.html");

app.Run();
