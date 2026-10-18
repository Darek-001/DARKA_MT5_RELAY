using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Osobny najnowszy snapshot dla KAŻDEGO terminal_id
builder.Services.AddSingleton<SnapshotStore>();

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
        mcp = true,
        multiMarket = true,
        markets = store.Count
    }));


// ====================================================
// INGEST
// V7 wysyła tutaj dowolny rynek
// ====================================================

app.MapPost("/ingest", async (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(req.Body);

    var root = doc.RootElement;

    string terminalId = "";

    if (root.TryGetProperty("terminal_id", out var tid))
        terminalId = tid.GetString() ?? "";

    if (string.IsNullOrWhiteSpace(terminalId))
    {
        return Results.BadRequest(new
        {
            ok = false,
            message = "missing terminal_id"
        });
    }

    store.Set(terminalId, root);

    return Results.Ok(new
    {
        ok = true,
        terminal_id = terminalId
    });
});


// ====================================================
// LATEST
// Zachowujemy zgodność ze starym odczytem:
// zwraca najnowszy snapshot ze wszystkich rynków
// ====================================================

app.MapGet("/latest", (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    var snapshot = store.GetLatest();

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
        terminal_id = snapshot.TerminalId,
        receivedAtUtc = snapshot.ReceivedAtUtc,
        data = snapshot.Data
    });
});


// ====================================================
// MARKET
// Odczyt konkretnego terminal_id
// ====================================================

app.MapGet("/market/{terminalId}", (
    string terminalId,
    HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    var snapshot = store.Get(terminalId);

    if (snapshot.Data is null)
    {
        return Results.NotFound(new
        {
            ok = false,
            terminal_id = terminalId,
            message = "no snapshot yet"
        });
    }

    return Results.Json(new
    {
        ok = true,
        terminal_id = terminalId,
        receivedAtUtc = snapshot.ReceivedAtUtc,
        data = snapshot.Data
    });
});


// ====================================================
// MARKETS
// Lista wszystkich aktywnych rynków
// ====================================================

app.MapGet("/markets", (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    return Results.Json(new
    {
        ok = true,
        markets = store.GetMarkets()
    });
});


// ====================================================
// ALL
// Pełne snapshoty wszystkich rynków
// ====================================================

app.MapGet("/all", (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    return Results.Json(new
    {
        ok = true,
        markets = store.GetAll()
    });
});


// ====================================================
// MCP
// ====================================================

app.MapMcp("/mcp");

app.Run();


// ====================================================
// SNAPSHOT STORE - MULTI MARKET
// ====================================================

public sealed class SnapshotStore
{
    private readonly object _gate = new();

    private readonly Dictionary<string, SnapshotEntry> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_gate)
                return _snapshots.Count;
        }
    }

    public void Set(string terminalId, JsonElement data)
    {
        lock (_gate)
        {
            _snapshots[terminalId] = new SnapshotEntry
            {
                Data = data.Clone(),
                ReceivedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public (JsonElement? Data, DateTimeOffset? ReceivedAtUtc) Get(
        string terminalId)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(terminalId, out var entry))
                return (null, null);

            return (entry.Data.Clone(), entry.ReceivedAtUtc);
        }
    }

    public (
        string? TerminalId,
        JsonElement? Data,
        DateTimeOffset? ReceivedAtUtc
    ) GetLatest()
    {
        lock (_gate)
        {
            if (_snapshots.Count == 0)
                return (null, null, null);

            var latest = _snapshots
                .OrderByDescending(x => x.Value.ReceivedAtUtc)
                .First();

            return (
                latest.Key,
                latest.Value.Data.Clone(),
                latest.Value.ReceivedAtUtc
            );
        }
    }

    public object[] GetMarkets()
    {
        lock (_gate)
        {
            return _snapshots
                .OrderBy(x => x.Key)
                .Select(x => (object)new
                {
                    terminal_id = x.Key,
                    receivedAtUtc = x.Value.ReceivedAtUtc,
                    ageSeconds =
                        (DateTimeOffset.UtcNow -
                         x.Value.ReceivedAtUtc).TotalSeconds
                })
                .ToArray();
        }
    }

    public object[] GetAll()
    {
        lock (_gate)
        {
            return _snapshots
                .OrderBy(x => x.Key)
                .Select(x => (object)new
                {
                    terminal_id = x.Key,
                    receivedAtUtc = x.Value.ReceivedAtUtc,
                    data = x.Value.Data.Clone()
                })
                .ToArray();
        }
    }

    private sealed class SnapshotEntry
    {
        public JsonElement Data { get; init; }
        public DateTimeOffset ReceivedAtUtc { get; init; }
    }
}


// ====================================================
// MCP TOOLS - MULTI MARKET / READ ONLY
// ====================================================

[McpServerToolType]
public sealed class Mt5Tools
{
    private readonly SnapshotStore _store;

    public Mt5Tools(SnapshotStore store)
    {
        _store = store;
    }

    // Zachowujemy stare narzędzie - niczego nie psujemy
    [McpServerTool]
    [Description(
        "Returns the newest read-only DARKA MT5 market snapshot received by the relay."
    )]
    public object GetMt5Snapshot()
    {
        var snapshot = _store.GetLatest();

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
            terminal_id = snapshot.TerminalId,
            receivedAtUtc = snapshot.ReceivedAtUtc,
            data = snapshot.Data
        };
    }

    // NOWE: lista wszystkich rynków
    [McpServerTool]
    [Description(
        "Returns all MT5 terminal IDs currently sending market snapshots."
    )]
    public object GetMt5Markets()
    {
        return new
        {
            ok = true,
            markets = _store.GetMarkets()
        };
    }

    // NOWE: wybór konkretnego rynku
    [McpServerTool]
    [Description(
        "Returns the newest MT5 snapshot for a specific terminal ID, for example DARKA_MT5_BTCUSD, DARKA_MT5_XAUUSD, DARKA_MT5_USTEC or DARKA_MT5_EURUSD."
    )]
    public object GetMt5MarketSnapshot(
        [Description("MT5 terminal ID")] string terminalId)
    {
        var snapshot = _store.Get(terminalId);

        if (snapshot.Data is null)
        {
            return new
            {
                ok = false,
                terminal_id = terminalId,
                message = "no snapshot yet"
            };
        }

        return new
        {
            ok = true,
            terminal_id = terminalId,
            receivedAtUtc = snapshot.ReceivedAtUtc,
            data = snapshot.Data
        };
    }

    // NOWE: wszystkie snapshoty naraz
    [McpServerTool]
    [Description(
        "Returns the newest read-only MT5 snapshots for all currently connected markets."
    )]
    public object GetAllMt5Snapshots()
    {
        return new
        {
            ok = true,
            markets = _store.GetAll()
        };
    }
}
