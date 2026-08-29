using System.Net.Http.Json;
using System.Text.Json;

namespace ResonateConformance;

public sealed class Client(HttpClient http)
{
    private static readonly Uri Root = new("/", UriKind.Relative);

    public bool Trace { get; init; } = true;

    public Func<long?> DebugTimeProvider { get; set; } = () => null;

    private RequestHead NewHead(string? origin = null) =>
        new(Guid.NewGuid().ToString(), Protocol.Version) { DebugTime = DebugTimeProvider(), Origin = origin };

    public async Task<Response> SendAsync<TData>(string kind, TData data, RequestHead? head = null)
    {
        var env = new Envelope<TData>(kind, head ?? NewHead(), data);
        if (Trace) Console.WriteLine($"  → {kind}  {JsonSerializer.Serialize(env.Data, Json.Options)}");

        HttpResponseMessage httpResp;
        string body;
        try
        {

            var payload = JsonSerializer.Serialize(env, Json.Options);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            httpResp = await http.PostAsync(Root, content);
            body = await httpResp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {

            if (Trace) Console.WriteLine($"  ← (indefinite) transport failure: {ex.GetType().Name} — {ex.Message}");
            return Response.IndefiniteFailure();
        }
        using (httpResp)
        {

            if ((int)httpResp.StatusCode is >= 500 and < 600)
            {
                if (Trace) Console.WriteLine($"  ← (indefinite) HTTP {(int)httpResp.StatusCode}");
                return Response.IndefiniteFailure();
            }

            var resp = ParseResponse(body);

            if (Trace)
            {
                var payload = resp.Data.ValueKind == JsonValueKind.String
                    ? $"\"{resp.ErrorMessage}\""
                    : JsonSerializer.Serialize(resp.Data, Json.Options);
                Console.WriteLine($"  ← {resp.Status}  {payload}");
            }
            return resp;
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is System.Net.Sockets.SocketException
           or TaskCanceledException
           or OperationCanceledException
        || (ex is HttpRequestException hre &&
            hre.InnerException is System.Net.Sockets.SocketException or System.Net.WebException);

    private static Response ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString()!;
        var head = root.GetProperty("head").Deserialize<ResponseHead>(Json.Options)!;

        var data = root.GetProperty("data").Clone();
        return new Response(kind, head, data);
    }

    public Task<Response> PromiseCreate(string id, long timeoutAt, Value? param = null,
        Dictionary<string, string>? tags = null) =>
        SendAsync("promise.create", new
        {
            id,
            timeoutAt,
            param = param ?? Value.Of(null),
            tags = tags ?? new Dictionary<string, string>(),
        });

    public Task<Response> PromiseGet(string id) =>
        SendAsync("promise.get", new { id });

    public Task<Response> PromiseSettle(string id, string state, Value? value = null) =>
        SendAsync("promise.settle", new { id, state, value = value ?? Value.Of(null) });

    public Task<Response> PromiseRegisterCallback(string awaited, string awaiter) =>
        SendAsync("promise.register_callback", new { awaited, awaiter });

    public Task<Response> PromiseRegisterListener(string awaited, string address) =>
        SendAsync("promise.register_listener", new { awaited, address });

    public Task<Response> TaskCreate(string pid, long ttl, object promiseCreateAction) =>
        SendAsync("task.create", new { pid, ttl, action = promiseCreateAction });

    public Task<Response> TaskGet(string id) =>
        SendAsync("task.get", new { id });

    public Task<Response> TaskAcquire(string id, int version, string pid, long ttl) =>
        SendAsync("task.acquire", new { id, version, pid, ttl });

    public Task<Response> TaskRelease(string id, int version) =>
        SendAsync("task.release", new { id, version });

    public Task<Response> TaskFulfill(string id, int version, object promiseSettleAction) =>
        SendAsync("task.fulfill", new { id, version, action = promiseSettleAction });

    public Task<Response> TaskSuspend(string id, int version, object[] registerCallbackActions) =>
        SendAsync("task.suspend", new { id, version, actions = registerCallbackActions });

    public Task<Response> TaskFence(string id, int version, object promiseAction) =>
        SendAsync("task.fence", new { id, version, action = promiseAction });

    public Task<Response> TaskPoll(string group, string pid, long ttl, int limit) =>
        SendAsync("task.poll", new { group, pid, ttl, limit });

    public Task<Response> TaskHeartbeat(string pid, object[] tasks) =>
        SendAsync("task.heartbeat", new { pid, tasks });

    public Task<Response> DebugReset() => SendAsync("debug.reset", new { });

    public Task<Response> DebugTick(long time) => SendAsync("debug.tick", new { time });

    public Task<Response> DebugSnap() => SendAsync("debug.snap", new { });

    public Task<Response> DebugMessages() => SendAsync("debug.messages", new { });

    public static object PromiseCreateAction(string id, long timeoutAt, Value? param = null,
        Dictionary<string, string>? tags = null) => new
        {
            kind = "promise.create",
            head = new { corrId = Guid.NewGuid().ToString(), version = Protocol.Version },
            data = new
            {
                id,
                timeoutAt,
                param = param ?? Value.Of(null),
                tags = tags ?? new Dictionary<string, string>(),
            },
        };

    public static object PromiseSettleAction(string id, string state, Value? value = null) => new
    {
        kind = "promise.settle",
        head = new { corrId = Guid.NewGuid().ToString(), version = Protocol.Version },
        data = new { id, state, value = value ?? Value.Of(null) },
    };

    public static object RegisterCallbackAction(string awaited, string awaiter) => new
    {
        kind = "promise.register_callback",
        head = new { corrId = Guid.NewGuid().ToString(), version = Protocol.Version },
        data = new { awaited, awaiter },
    };
}
