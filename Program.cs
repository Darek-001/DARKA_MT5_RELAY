using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Wspólny magazyn najnowszego snapshotu MT5
builder.Services.AddSingleton<SnapshotStore>();

// MCP - dodatkowy kanał tylko do odczytu
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<Mt5Tools>();

var app = builder.Build();

var token = Environment.GetEnvironmentVariable("DARKA_MONITOR_TOKEN") ?? "";
var store = app.Services.GetRequiredService<SnapshotStore>();

bool Authorized(HttpRequest req)
{
    if (string.IsNullOrWhiteSpace(token))
        return false;

    var auth = req.Headers.Authorization.ToString();

    return auth == $"Bearer {token}";
}


// ====================================================
// HEALTH
// ====================================================

app.MapGet("/health", () =>
    Results.Ok(new
    {
        ok = true,
        service = "DARKA_MT5_RELAY",
        mcp = true
    }));


// ====================================================
// DOTYCHCZASOWY INGEST
// Watcher wysyła tutaj dane z MT5
// ====================================================

app.MapPost("/ingest", async (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(req.Body);

    store.Set(doc.RootElement);

    return Results.Ok(new
    {
        ok = true
    });
});


// ====================================================
// DOTYCHCZASOWY LATEST
// ====================================================

app.MapGet("/latest", (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    var snapshot = store.Get();

    if (snapshot.Data is null)
    {
        return Results.NotFound(new
        {
            ok = false,
            message = "no snapshot yet"
        });
    }

    return Results.Json(new
    {
        ok = true,
        receivedAtUtc = snapshot.ReceivedAtUtc,
        data = snapshot.Data
    });
});


// ====================================================
// MCP
// ====================================================

app.MapMcp("/mcp");


// ====================================================
// START
// ====================================================

app.Run();


// ====================================================
// SNAPSHOT STORE
// ====================================================

public sealed class SnapshotStore
{
    private readonly object _gate = new();

    private JsonElement? _latest;
    private DateTimeOffset? _latestAt;

    public void Set(JsonElement data)
    {
        lock (_gate)
        {
            _latest = data.Clone();
            _latestAt = DateTimeOffset.UtcNow;
        }
    }

    public (JsonElement? Data, DateTimeOffset? ReceivedAtUtc) Get()
    {
        lock (_gate)
        {
            return (_latest, _latestAt);
        }
    }
}


// ====================================================
// MCP TOOLS
// TYLKO ODCZYT - BRAK KOMEND DO MT5
// ====================================================

[McpServerToolType]
public sealed class Mt5Tools
{
    private readonly SnapshotStore _store;

    public Mt5Tools(SnapshotStore store)
    {
        _store = store;
    }

    [McpServerTool]
    [Description(
        "Returns the newest read-only DARKA MT5 market snapshot received by the relay."
    )]
    public object GetMt5Snapshot()
    {
        var snapshot = _store.Get();

        if (snapshot.Data is null)
        {
            return new
            {
                ok = false,
                message = "no snapshot yet"
            };
        }

        return new
        {
            ok = true,
            receivedAtUtc = snapshot.ReceivedAtUtc,
            data = snapshot.Data
        };
    }
}
