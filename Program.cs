using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton<ControlStore>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<Mt5Tools>();

var app = builder.Build();

var token = Environment.GetEnvironmentVariable("DARKA_MONITOR_TOKEN") ?? "";

var store = app.Services.GetRequiredService<SnapshotStore>();
var controls = app.Services.GetRequiredService<ControlStore>();

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
        control = true,
        markets = store.Count
    }));


// ====================================================
// INGEST
// V7 wysyla tutaj dowolny rynek
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
// CONTROL PULL
//
// Lokalny CONTROL CONNECTOR odpytuje ten endpoint.
// Port 8787 pozostaje lokalny i NIE jest wystawiany
// do Internetu.
// ====================================================

app.MapGet("/control/pull", (
    string terminal_id,
    HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(terminal_id))
    {
        return Results.BadRequest(new
        {
            ok = false,
            message = "missing terminal_id"
        });
    }

    var command = controls.Pull(terminal_id);

    if (command is null)
        return Results.NoContent();

    return Results.Json(command);
});


// ====================================================
// CONTROL RESULT
//
// Connector odsyla tutaj wynik wykonania polecenia.
// ====================================================

app.MapPost("/control/result", async (HttpRequest req) =>
{
    if (!Authorized(req))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(req.Body);

    var root = doc.RootElement;

    string id = "";

    if (root.TryGetProperty("id", out var idElement))
        id = idElement.GetString() ?? "";

    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest(new
        {
            ok = false,
            message = "missing id"
        });
    }

    if (!controls.SetResult(id, root))
    {
        return Results.NotFound(new
        {
            ok = false,
            id,
            message = "unknown command id"
        });
    }

    return Results.Ok(new
    {
        ok = true,
        id
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

    public (
        JsonElement? Data,
        DateTimeOffset? ReceivedAtUtc
    ) Get(string terminalId)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(
                terminalId,
                out var entry))
            {
                return (null, null);
            }

            return (
                entry.Data.Clone(),
                entry.ReceivedAtUtc
            );
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
                .OrderByDescending(
                    x => x.Value.ReceivedAtUtc)
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

                    receivedAtUtc =
                        x.Value.ReceivedAtUtc,

                    ageSeconds =
                        (DateTimeOffset.UtcNow -
                         x.Value.ReceivedAtUtc)
                        .TotalSeconds
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

                    receivedAtUtc =
                        x.Value.ReceivedAtUtc,

                    data =
                        x.Value.Data.Clone()
                })
                .ToArray();
        }
    }

    private sealed class SnapshotEntry
    {
        public JsonElement Data { get; init; }

        public DateTimeOffset ReceivedAtUtc
        {
            get;
            init;
        }
    }
}


// ====================================================
// CONTROL STORE
// Osobna kolejka dla kazdego terminal_id.
// ====================================================

public sealed class ControlStore
{
    private readonly object _gate = new();

    private readonly Dictionary<string, ControlEntry>
        _byId =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Queue<string>>
        _queues =
            new(StringComparer.OrdinalIgnoreCase);


    private static readonly HashSet<string>
        AllowedActions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "BUY",
                "SELL",
                "CLOSE",
                "CLOSE_PARTIAL",
                "MODIFY",
                "CLOSE_ALL",
                "DRAW_MAP",
                "CLEAR_MAP",
                "PING"
            };


    public object Enqueue(
        string terminalId,
        string action,
        double lots,
        double sl,
        double tp,
        string ticket,
        double closeLots,
        string reason)
    {
        terminalId =
            (terminalId ?? "").Trim();

        action =
            (action ?? "")
            .Trim()
            .ToUpperInvariant();


        if (string.IsNullOrWhiteSpace(
            terminalId))
        {
            return new
            {
                ok = false,
                error = "missing_terminal_id"
            };
        }


        if (!AllowedActions.Contains(action))
        {
            return new
            {
                ok = false,
                error = "unsupported_action",
                action
            };
        }


        var id =
            "CTL-" +
            Guid.NewGuid()
                .ToString("N");


        var payload =
            new Dictionary<string, object?>
            {
                ["id"] = id,

                ["terminal_id"] =
                    terminalId,

                ["action"] =
                    action,

                ["reason"] =
                    reason ?? ""
            };


        if (lots > 0)
            payload["lots"] = lots;

        if (sl > 0)
            payload["sl"] = sl;

        if (tp > 0)
            payload["tp"] = tp;

        if (!string.IsNullOrWhiteSpace(ticket))
            payload["ticket"] = ticket;

        if (closeLots > 0)
            payload["close_lots"] = closeLots;


        lock (_gate)
        {
            _byId[id] =
                new ControlEntry
                {
                    Id = id,
                    TerminalId = terminalId,
                    Action = action,
                    Payload = payload,
                    Status = "QUEUED"
                };


            if (!_queues.TryGetValue(
                terminalId,
                out var queue))
            {
                queue =
                    new Queue<string>();

                _queues[terminalId] =
                    queue;
            }


            queue.Enqueue(id);
        }


        return new
        {
            ok = true,
            id,
            terminal_id = terminalId,
            action,
            status = "QUEUED"
        };
    }


    public object? Pull(
        string terminalId)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(
                terminalId,
                out var queue))
            {
                return null;
            }


            while (queue.Count > 0)
            {
                var id =
                    queue.Dequeue();


                if (!_byId.TryGetValue(
                    id,
                    out var entry))
                {
                    continue;
                }


                if (entry.Status != "QUEUED")
                    continue;


                entry.Status =
                    "CLAIMED";


                return entry.Payload;
            }


            return null;
        }
    }


    public bool SetResult(
        string id,
        JsonElement result)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(
                id,
                out var entry))
            {
                return false;
            }


            entry.Status =
                "FINISHED";

            entry.Result =
                result.Clone();


            return true;
        }
    }


    public object? Status(
        string id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(
                id,
                out var entry))
            {
                return null;
            }


            return new
            {
                ok = true,

                id =
                    entry.Id,

                terminal_id =
                    entry.TerminalId,

                action =
                    entry.Action,

                status =
                    entry.Status,

                result =
                    entry.Result
            };
        }
    }


    private sealed class ControlEntry
    {
        public string Id
        {
            get;
            init;
        } = "";


        public string TerminalId
        {
            get;
            init;
        } = "";


        public string Action
        {
            get;
            init;
        } = "";


        public Dictionary<string, object?>
            Payload
        {
            get;
            init;
        } = new();


        public string Status
        {
            get;
            set;
        } = "";


        public JsonElement? Result
        {
            get;
            set;
        }
    }
}


// ====================================================
// MCP TOOLS
//
// 4 stare narzedzia pozostaja.
// Dodajemy 2 narzedzia CONTROL.
// ====================================================

[McpServerToolType]
public sealed class Mt5Tools
{
    private readonly SnapshotStore _store;

    private readonly ControlStore _controls;


    public Mt5Tools(
        SnapshotStore store,
        ControlStore controls)
    {
        _store = store;
        _controls = controls;
    }


    // =================================================
    // 1. STARE NARZEDZIE
    // =================================================

    [McpServerTool]
    [Description(
        "Returns the newest read-only DARKA MT5 market snapshot received by the relay."
    )]
    public object GetMt5Snapshot()
    {
        var snapshot =
            _store.GetLatest();


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

            terminal_id =
                snapshot.TerminalId,

            receivedAtUtc =
                snapshot.ReceivedAtUtc,

            data =
                snapshot.Data
        };
    }


    // =================================================
    // 2. STARE NARZEDZIE
    // =================================================

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


    // =================================================
    // 3. STARE NARZEDZIE
    // =================================================

    [McpServerTool]
    [Description(
        "Returns the newest MT5 snapshot for a specific terminal ID."
    )]
    public object GetMt5MarketSnapshot(
        [Description(
            "MT5 terminal ID"
        )]
        string terminalId)
    {
        var snapshot =
            _store.Get(terminalId);


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

            terminal_id =
                terminalId,

            receivedAtUtc =
                snapshot.ReceivedAtUtc,

            data =
                snapshot.Data
        };
    }


    // =================================================
    // 4. STARE NARZEDZIE
    // =================================================

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


    // =================================================
    // 5. NOWE - CONTROL
    // =================================================

    [McpServerTool]
    [Description(
        "Queues a DEMO control command for a DARKA MT5 terminal. Supported actions: BUY, SELL, CLOSE, CLOSE_PARTIAL, MODIFY, CLOSE_ALL, DRAW_MAP, CLEAR_MAP, PING."
    )]
    public object SendMt5ControlCommand(
        [Description(
            "Target terminal ID, for example DARKA_MT5_BTCUSD"
        )]
        string terminalId,

        [Description(
            "BUY, SELL, CLOSE, CLOSE_PARTIAL, MODIFY, CLOSE_ALL, DRAW_MAP, CLEAR_MAP or PING"
        )]
        string action,

        [Description(
            "Lot size for BUY or SELL. Use 0 when unused."
        )]
        double lots = 0,

        [Description(
            "Stop loss price. Use 0 when unused."
        )]
        double sl = 0,

        [Description(
            "Take profit price. Use 0 when unused."
        )]
        double tp = 0,

        [Description(
            "Position ticket for CLOSE or MODIFY. Blank when unused."
        )]
        string ticket = "",

        [Description(
            "Lots to close for CLOSE_PARTIAL. Use 0 when unused."
        )]
        double closeLots = 0,

        [Description(
            "Audit reason for the command."
        )]
        string reason = "")
    {
        return _controls.Enqueue(
            terminalId,
            action,
            lots,
            sl,
            tp,
            ticket,
            closeLots,
            reason
        );
    }


    // =================================================
    // 6. NOWE - CONTROL STATUS
    // =================================================

    [McpServerTool]
    [Description(
        "Returns the current relay status or result of a previously queued MT5 CONTROL command."
    )]
    public object GetMt5ControlStatus(
        [Description(
            "CONTROL command ID"
        )]
        string id)
    {
        return
            _controls.Status(id)
            ??
            new
            {
                ok = false,
                id,
                message =
                    "unknown command id"
            };
    }
}
