using System.Net.Http.Json;
using System.Text.Json;

namespace ResonateConformance;

/// <summary>
/// Raw-HTTP client for the Resonate server. NO SDK. Every method builds a
/// `kind`-tagged envelope, POSTs it to `/`, and returns the parsed Response.
///
/// This is the object a future Accordant model would drive via
/// `spec.ExecuteWith<Client>().BindAsync(...)`.
/// </summary>
public sealed class Client(HttpClient http)
{
    private static readonly Uri Root = new("/", UriKind.Relative);

    /// <summary>Set to see every request/response envelope on the console.</summary>
    public bool Trace { get; init; } = true;

    /// <summary>
    /// Supplies the `resonate:debug_time` (epoch ms) injected into every request
    /// head. Set this to a controllable logical clock to make server timeout
    /// deterministic (no wall-clock `now`). Returns null → server uses wall clock.
    /// </summary>
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
            // Serialize first and send a LENGTH-DELIMITED body. PostAsJsonAsync
            // streams, which makes .NET use `Transfer-Encoding: chunked`; servers
            // that read only `content-length` then see an empty body. Chunked is
            // mandatory in HTTP/1.1, so that is their bug — but a known length is
            // what ordinary clients send, and it keeps the harness portable.
            var payload = JsonSerializer.Serialize(env, Json.Options);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            httpResp = await http.PostAsync(Root, content);
            body = await httpResp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // The request left, but we never got a usable reply back (socket
            // timeout, connection reset, DNS, cancellation). We CANNOT tell whether
            // the server applied it — this is a genuine INDEFINITE failure, so
            // return one instead of throwing. The model's indefinite-failure
            // branches (when enabled) accept it; with them off it's rejected loudly.
            if (Trace) Console.WriteLine($"  ← (indefinite) transport failure: {ex.GetType().Name} — {ex.Message}");
            return Response.IndefiniteFailure();
        }
        using (httpResp)
        {
            // A 5xx is a server-side failure where the write may or may not have
            // landed — indefinite by definition. Classify it by HTTP STATUS CODE
            // (not the envelope), because a crashing/proxied 5xx may not even carry
            // a valid envelope body. We deliberately do NOT parse it.
            if ((int)httpResp.StatusCode is >= 500 and < 600)
            {
                if (Trace) Console.WriteLine($"  ← (indefinite) HTTP {(int)httpResp.StatusCode}");
                return Response.IndefiniteFailure();
            }

            // Any other status (2xx/3xx/4xx) MUST be a well-formed envelope — this
            // server always envelopes, even for garbage input. If ParseResponse
            // throws here, our wire contract is genuinely violated: crash LOUD
            // rather than masquerading a protocol bug as an indefinite failure.
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

    /// <summary>
    /// A transport-level failure: the reply was lost, so the outcome is indefinite.
    /// Covers socket errors, cancellation/timeout, and the HttpRequestExceptions
    /// that wrap them. A malformed BODY is deliberately NOT here — that's a
    /// wire-contract violation we want to surface, not swallow.
    /// </summary>
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
        // Clone the data element so it survives disposal of the JsonDocument.
        var data = root.GetProperty("data").Clone();
        return new Response(kind, head, data);
    }

    // ---- Promise ops --------------------------------------------------------

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

    /// <summary>state ∈ { resolved, rejected, rejected_canceled }.</summary>
    public Task<Response> PromiseSettle(string id, string state, Value? value = null) =>
        SendAsync("promise.settle", new { id, state, value = value ?? Value.Of(null) });

    public Task<Response> PromiseRegisterCallback(string awaited, string awaiter) =>
        SendAsync("promise.register_callback", new { awaited, awaiter });

    public Task<Response> PromiseRegisterListener(string awaited, string address) =>
        SendAsync("promise.register_listener", new { awaited, address });

    // ---- Task ops -----------------------------------------------------------

    /// <summary>Atomic create+claim: promise + task, task starts `acquired`.</summary>
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

    /// <summary>task.poll — claim every claimable task of a group (capability "poll").</summary>
    public Task<Response> TaskPoll(string group, string pid, long ttl, int limit) =>
        SendAsync("task.poll", new { group, pid, ttl, limit });

    /// <summary>Refresh the lease on the given tasks for this pid.</summary>
    public Task<Response> TaskHeartbeat(string pid, object[] tasks) =>
        SendAsync("task.heartbeat", new { pid, tasks });

    // ---- Debug ops (require `resonate dev --debug`) -------------------------

    /// <summary>Clear ALL server state. Use in BeforeEach for a clean run.</summary>
    public Task<Response> DebugReset() => SendAsync("debug.reset", new { });

    /// <summary>Advance the server's driven clock to `time` and fire due timeouts.</summary>
    public Task<Response> DebugTick(long time) => SendAsync("debug.tick", new { time });

    /// <summary>Snapshot full server state (promises, tasks, callbacks, ...).</summary>
    public Task<Response> DebugSnap() => SendAsync("debug.snap", new { });

    /// <summary>
    /// Drain the server's egress outbox: every message decided since the last
    /// drain (or reset), in decision order. Messages are fire-and-forget in
    /// production — this debug tap is the ONLY way the harness can observe
    /// the effect channel (execute wake-ups, listener unblocks) and assert
    /// their contents against the model.
    /// </summary>
    public Task<Response> DebugMessages() => SendAsync("debug.messages", new { });

    // ---- action builders (inner envelopes carried by task ops) --------------

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

    /// <summary>Inner register_callback action carried in a task.suspend `actions[]`.</summary>
    public static object RegisterCallbackAction(string awaited, string awaiter) => new
    {
        kind = "promise.register_callback",
        head = new { corrId = Guid.NewGuid().ToString(), version = Protocol.Version },
        data = new { awaited, awaiter },
    };
}
