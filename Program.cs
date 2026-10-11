using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var token = Environment.GetEnvironmentVariable("DARKA_MONITOR_TOKEN") ?? "";
var gate = new object();
JsonElement? latest = null;
DateTimeOffset? latestAt = null;

bool Authorized(HttpRequest req)
{
    if (string.IsNullOrWhiteSpace(token)) return false;
    var auth = req.Headers.Authorization.ToString();
    return auth == $"Bearer {token}";
}

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "DARKA_MT5_RELAY" }));

app.MapPost("/ingest", async (HttpRequest req) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    using var doc = await JsonDocument.ParseAsync(req.Body);
    lock (gate)
    {
        latest = doc.RootElement.Clone();
        latestAt = DateTimeOffset.UtcNow;
    }
    return Results.Ok(new { ok = true });
});

app.MapGet("/latest", (HttpRequest req) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    lock (gate)
    {
        if (latest is null) return Results.NotFound(new { ok = false, message = "no snapshot yet" });
        return Results.Json(new { ok = true, receivedAtUtc = latestAt, data = latest });
    }
});

app.Run();
