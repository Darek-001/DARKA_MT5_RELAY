using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton<ControlStore>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<Mt5Tools>();
var app = builder.Build();
var token = Environment.GetEnvironmentVariable("DARKA_MONITOR_TOKEN") ?? "";
var store = app.Services.GetRequiredService<SnapshotStore>();
var controls = app.Services.GetRequiredService<ControlStore>();

bool Authorized(HttpRequest req) => !string.IsNullOrWhiteSpace(token) && req.Headers.Authorization.ToString() == $"Bearer {token}";

app.MapGet("/health", () => Results.Ok(new { ok=true, service="DARKA_MT5_RELAY", mcp=true, multiMarket=true, control=true, markets=store.Count }));

app.MapPost("/ingest", async (HttpRequest req) => {
    if (!Authorized(req)) return Results.Unauthorized();
    using var doc = await JsonDocument.ParseAsync(req.Body); var root=doc.RootElement; string terminalId="";
    if(root.TryGetProperty("terminal_id",out var tid)) terminalId=tid.GetString()??"";
    if(string.IsNullOrWhiteSpace(terminalId)) return Results.BadRequest(new {ok=false,message="missing terminal_id"});
    store.Set(terminalId,root); return Results.Ok(new {ok=true,terminal_id=terminalId});
});
app.MapGet("/latest",(HttpRequest req)=>{ if(!Authorized(req)) return Results.Unauthorized(); var s=store.GetLatest(); if(s.Data is null)return Results.NotFound(new{ok=false,message="no snapshot yet"}); return Results.Json(new{ok=true,terminal_id=s.TerminalId,receivedAtUtc=s.ReceivedAtUtc,data=s.Data});});
app.MapGet("/market/{terminalId}",(string terminalId,HttpRequest req)=>{if(!Authorized(req))return Results.Unauthorized();var s=store.Get(terminalId);if(s.Data is null)return Results.NotFound(new{ok=false,terminal_id=terminalId,message="no snapshot yet"});return Results.Json(new{ok=true,terminal_id=terminalId,receivedAtUtc=s.ReceivedAtUtc,data=s.Data});});
app.MapGet("/markets",(HttpRequest req)=>!Authorized(req)?Results.Unauthorized():Results.Json(new{ok=true,markets=store.GetMarkets()}));
app.MapGet("/all",(HttpRequest req)=>!Authorized(req)?Results.Unauthorized():Results.Json(new{ok=true,markets=store.GetAll()}));

// Connector polls these with the SAME existing bearer token. No new secret is required.
app.MapGet("/control/pull",(string terminal_id,HttpRequest req)=>{if(!Authorized(req))return Results.Unauthorized();if(string.IsNullOrWhiteSpace(terminal_id))return Results.BadRequest(new{ok=false,message="missing terminal_id"});var c=controls.Pull(terminal_id);return c is null?Results.NoContent():Results.Json(c);});
app.MapPost("/control/result",async(HttpRequest req)=>{if(!Authorized(req))return Results.Unauthorized();using var doc=await JsonDocument.ParseAsync(req.Body);var r=doc.RootElement;var id=r.TryGetProperty("id",out var x)?x.GetString()??"":"";if(string.IsNullOrWhiteSpace(id))return Results.BadRequest(new{ok=false,message="missing id"});return controls.SetResult(id,r)?Results.Ok(new{ok=true,id}):Results.NotFound(new{ok=false,id,message="unknown command id"});});


// CONTROL6 HTTP ingress: additive only. Existing CONTROL5/MCP endpoints remain unchanged.
app.MapPost("/control/send", async (HttpRequest req) => {
    if(!Authorized(req)) return Results.Unauthorized();
    using var doc=await JsonDocument.ParseAsync(req.Body); var r=doc.RootElement;
    string terminalId=r.TryGetProperty("terminal_id",out var t)?t.GetString()??"":"";
    string action=r.TryGetProperty("action",out var a)?a.GetString()??"":"";
    double lots=r.TryGetProperty("lots",out var l)&&l.TryGetDouble(out var lv)?lv:0;
    double sl=r.TryGetProperty("sl",out var se)&&se.TryGetDouble(out var sv)?sv:0;
    double tp=r.TryGetProperty("tp",out var te)&&te.TryGetDouble(out var tv)?tv:0;
    string ticket=r.TryGetProperty("ticket",out var ti)?ti.ToString():"";
    double closeLots=r.TryGetProperty("close_lots",out var cl)&&cl.TryGetDouble(out var cv)?cv:0;
    string reason=r.TryGetProperty("reason",out var re)?re.GetString()??"":"";
    return Results.Json(controls.Enqueue(terminalId,action,lots,sl,tp,ticket,closeLots,reason));
});
app.MapGet("/control/status/{id}",(string id,HttpRequest req)=>{if(!Authorized(req))return Results.Unauthorized();var x=controls.Status(id);return x is null?Results.NotFound(new{ok=false,id,message="unknown command id"}):Results.Json(x);});

// Browser/Work control panel. The bearer token is entered by the user and kept only in sessionStorage.
app.MapGet("/control", () => Results.Content("""
<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>DARKA CONTROL6 DEMO</title><style>body{font-family:system-ui;max-width:820px;margin:30px auto;padding:0 16px}input,select,button{font:inherit;padding:9px;margin:4px}input{min-width:260px}pre{background:#111;color:#eee;padding:12px;white-space:pre-wrap} .warn{font-weight:700}</style></head><body>
<h1>DARKA CONTROL6 — DEMO</h1><p class='warn'>DEMO control panel. Agent 3.28.1, CONTROL1 and V7 are not modified.</p>
<div><input id='token' type='password' placeholder='Relay bearer token'><button onclick='saveToken()'>Save for this tab</button></div>
<div><select id='terminal'><option>DARKA_MT5_BTCUSD</option><option>DARKA_MT5_ETHUSD</option><option>DARKA_MT5_XAUUSD</option><option>DARKA_MT5_USTEC</option><option>DARKA_MT5_EURUSD</option></select>
<select id='action'><option>PING</option><option>DRAW_MAP</option><option>CLEAR_MAP</option><option>BUY</option><option>SELL</option><option>CLOSE</option><option>CLOSE_PARTIAL</option><option>MODIFY</option><option>CLOSE_ALL</option></select></div>
<div><input id='lots' placeholder='lots (optional)'><input id='sl' placeholder='SL (optional)'><input id='tp' placeholder='TP (optional)'></div>
<div><input id='ticket' placeholder='ticket (optional)'><input id='close_lots' placeholder='close lots (optional)'></div>
<div><input id='reason' placeholder='reason' value='CONTROL6 browser test'><button onclick='send()'>SEND</button></div>
<div><input id='id' placeholder='command id'><button onclick='status()'>STATUS</button></div><pre id='out'>Ready.</pre>
<script>
const $=x=>document.getElementById(x); $('token').value=sessionStorage.getItem('darka_token')||'';
function saveToken(){sessionStorage.setItem('darka_token',$('token').value);$('out').textContent='Token saved only for this browser tab.'}
function hdr(){return {'Authorization':'Bearer '+$('token').value,'Content-Type':'application/json'}}
async function send(){saveToken();let b={terminal_id:$('terminal').value,action:$('action').value,reason:$('reason').value}; for(const k of ['lots','sl','tp','close_lots']){let v=$(k).value;if(v)b[k]=Number(v)} if($('ticket').value)b.ticket=$('ticket').value; let r=await fetch('/control/send',{method:'POST',headers:hdr(),body:JSON.stringify(b)});let t=await r.text();$('out').textContent=r.status+' '+t;try{let j=JSON.parse(t);if(j.id)$('id').value=j.id}catch{}}
async function status(){let r=await fetch('/control/status/'+encodeURIComponent($('id').value),{headers:{'Authorization':'Bearer '+$('token').value}});$('out').textContent=r.status+' '+await r.text()}
</script></body></html>
""","text/html; charset=utf-8"));

app.MapMcp("/mcp"); app.Run();

public sealed class SnapshotStore {
 readonly object g=new(); readonly Dictionary<string,SnapshotEntry> d=new(StringComparer.OrdinalIgnoreCase);
 public int Count{get{lock(g)return d.Count;}}
 public void Set(string id,JsonElement data){lock(g)d[id]=new(){Data=data.Clone(),ReceivedAtUtc=DateTimeOffset.UtcNow};}
 public (JsonElement? Data,DateTimeOffset? ReceivedAtUtc) Get(string id){lock(g){if(!d.TryGetValue(id,out var e))return(null,null);return(e.Data.Clone(),e.ReceivedAtUtc);}}
 public (string? TerminalId,JsonElement? Data,DateTimeOffset? ReceivedAtUtc) GetLatest(){lock(g){if(d.Count==0)return(null,null,null);var e=d.OrderByDescending(x=>x.Value.ReceivedAtUtc).First();return(e.Key,e.Value.Data.Clone(),e.Value.ReceivedAtUtc);}}
 public object[] GetMarkets(){lock(g)return d.OrderBy(x=>x.Key).Select(x=>(object)new{terminal_id=x.Key,receivedAtUtc=x.Value.ReceivedAtUtc,ageSeconds=(DateTimeOffset.UtcNow-x.Value.ReceivedAtUtc).TotalSeconds}).ToArray();}
 public object[] GetAll(){lock(g)return d.OrderBy(x=>x.Key).Select(x=>(object)new{terminal_id=x.Key,receivedAtUtc=x.Value.ReceivedAtUtc,data=x.Value.Data.Clone()}).ToArray();}
 sealed class SnapshotEntry{public JsonElement Data{get;init;}public DateTimeOffset ReceivedAtUtc{get;init;}}
}

public sealed class ControlStore {
 readonly object g=new(); readonly Dictionary<string,Entry> byId=new(StringComparer.OrdinalIgnoreCase); readonly Dictionary<string,Queue<string>> queues=new(StringComparer.OrdinalIgnoreCase);
 static readonly HashSet<string> Allowed=new(StringComparer.OrdinalIgnoreCase){"BUY","SELL","CLOSE","CLOSE_PARTIAL","MODIFY","CLOSE_ALL","DRAW_MAP","CLEAR_MAP","PING"};
 public object Enqueue(string terminalId,string action,double lots,double sl,double tp,string ticket,double closeLots,string reason){
   terminalId=(terminalId??"").Trim();action=(action??"").Trim().ToUpperInvariant();
   if(string.IsNullOrWhiteSpace(terminalId))return new{ok=false,error="missing_terminal_id"}; if(!Allowed.Contains(action))return new{ok=false,error="unsupported_action",action};
   var id="CTL-"+Guid.NewGuid().ToString("N"); var p=new Dictionary<string,object?>{{"id",id},{"terminal_id",terminalId},{"action",action},{"reason",reason??""}};
   if(lots>0)p["lots"]=lots;if(sl>0)p["sl"]=sl;if(tp>0)p["tp"]=tp;if(!string.IsNullOrWhiteSpace(ticket))p["ticket"]=ticket;if(closeLots>0)p["close_lots"]=closeLots;
   lock(g){byId[id]=new(){Id=id,TerminalId=terminalId,Action=action,Payload=p,Status="QUEUED"};if(!queues.TryGetValue(terminalId,out var q))queues[terminalId]=q=new();q.Enqueue(id);} return new{ok=true,id,terminal_id=terminalId,action,status="QUEUED"};
 }
 public object? Pull(string terminalId){lock(g){if(!queues.TryGetValue(terminalId,out var q))return null;while(q.Count>0){var id=q.Dequeue();if(byId.TryGetValue(id,out var e)&&e.Status=="QUEUED"){e.Status="CLAIMED";return e.Payload;}}return null;}}
 public bool SetResult(string id,JsonElement r){lock(g){if(!byId.TryGetValue(id,out var e))return false;e.Status="FINISHED";e.Result=r.Clone();return true;}}
 public object? Status(string id){lock(g){if(!byId.TryGetValue(id,out var e))return null;return new{ok=true,id=e.Id,terminal_id=e.TerminalId,action=e.Action,status=e.Status,result=e.Result};}}
 sealed class Entry{public string Id{get;init;}="";public string TerminalId{get;init;}="";public string Action{get;init;}="";public Dictionary<string,object?> Payload{get;init;}=new();public string Status{get;set;}="";public JsonElement? Result{get;set;}}
}

[McpServerToolType]
public sealed class Mt5Tools {
 readonly SnapshotStore s; readonly ControlStore c; public Mt5Tools(SnapshotStore store,ControlStore controls){s=store;c=controls;}
 [McpServerTool][Description("Returns the newest read-only DARKA MT5 market snapshot received by the relay.")] public object GetMt5Snapshot(){var x=s.GetLatest();return x.Data is null?new{ok=false,message="no snapshot yet"}:new{ok=true,terminal_id=x.TerminalId,receivedAtUtc=x.ReceivedAtUtc,data=x.Data};}
 [McpServerTool][Description("Returns all MT5 terminal IDs currently sending market snapshots.")] public object GetMt5Markets()=>new{ok=true,markets=s.GetMarkets()};
 [McpServerTool][Description("Returns the newest MT5 snapshot for a specific terminal ID.")] public object GetMt5MarketSnapshot([Description("MT5 terminal ID")]string terminalId){var x=s.Get(terminalId);return x.Data is null?new{ok=false,terminal_id=terminalId,message="no snapshot yet"}:new{ok=true,terminal_id=terminalId,receivedAtUtc=x.ReceivedAtUtc,data=x.Data};}
 [McpServerTool][Description("Returns the newest read-only MT5 snapshots for all currently connected markets.")] public object GetAllMt5Snapshots()=>new{ok=true,markets=s.GetAll()};
 [McpServerTool][Description("Queues a DEMO control command. Supported: BUY, SELL, CLOSE, CLOSE_PARTIAL, MODIFY, CLOSE_ALL, DRAW_MAP, CLEAR_MAP, PING.")] public object SendMt5ControlCommand(string terminalId,string action,double lots=0,double sl=0,double tp=0,string ticket="",double closeLots=0,string reason="")=>c.Enqueue(terminalId,action,lots,sl,tp,ticket,closeLots,reason);
 [McpServerTool][Description("Returns status/result of a CONTROL command.")] public object GetMt5ControlStatus(string id)=>c.Status(id)??new{ok=false,id,message="unknown command id"};
}
