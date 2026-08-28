using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Drives a hand-written op sequence against the live server and checks each
/// observed response with spec.Allows, threading the StateProfile forward.
///
/// spec.Allows returns (isValid, message, updatedStateProfile): given the current
/// set of possible model states, it asks "is this observed response permitted?"
/// and narrows the profile to the states consistent with it. A false = the server
/// did something the model forbids (a conformance failure), OR the model is wrong.
/// </summary>
public sealed class TraceRunner
{
    private readonly Spec<ServerState> _spec;
    private readonly TestingContext _ctx;
    private readonly Client _client;
    private StateProfile? _profile;
    private readonly ServerState _initial;
    private int _ok, _fail;

    /// <summary>Steps that conformed / diverged so far (for suites that report their own totals).</summary>
    public int Ok => _ok;
    public int Failed => _fail;
    private readonly List<string> _failures = [];

    public TraceRunner(Spec<ServerState> spec, Client client, long now)
    {
        _spec = spec;
        _client = client;
        _initial = new ServerState { Now = now };
        _ctx = spec.CreateTestingContext(Path.GetTempPath());
        _ctx.Register(client);
    }

    /// <summary>
    /// Silently drain the egress outbox (debug.messages), discarding the
    /// contents. Place immediately before the operation whose emissions the
    /// next <see cref="ExpectMessages"/> asserts, so the buffer holds ONLY
    /// that operation's messages.
    /// </summary>
    public Task DrainMessages() => _client.DebugMessages();

    /// <summary>
    /// Drain the egress outbox and assert its contents equal
    /// <paramref name="expected"/> as a multiset. Each expected entry is
    /// (kind, id, version): ("execute", taskId, version) — version must be
    /// the task's CURRENT version (bumped only on acquire) — or
    /// ("unblock", promiseId, ignored). This is the effect-channel
    /// counterpart of the record assertions: a message with a wrong version
    /// or a missing/spurious message fails even though every HTTP response
    /// is correct.
    /// </summary>
    public async Task ExpectMessages(string label, params (string Kind, string Id, long Version)[] expected)
    {
        var resp = await _client.DebugMessages();
        var observed = new List<string>();
        if (resp.Status == 200 && resp.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && resp.Data.TryGetProperty("messages", out var msgs))
        {
            foreach (var entry in msgs.EnumerateArray())
            {
                var m = entry.GetProperty("message");
                var kind = m.GetProperty("kind").GetString();
                observed.Add(kind switch
                {
                    "execute" => $"execute {m.GetProperty("taskId").GetString()} v{m.GetProperty("version").GetInt64()}",
                    "unblock" => $"unblock {m.GetProperty("promise").GetProperty("id").GetString()}",
                    _ => $"?{kind}",
                });
            }
        }
        else
        {
            _fail++;
            _failures.Add($"{label}: debug.messages unavailable (status {resp.Status})");
            Console.WriteLine($"  ❌ {label}\n       debug.messages unavailable (status {resp.Status})");
            return;
        }

        var want = expected
            .Select(e => e.Kind == "unblock" ? $"unblock {e.Id}" : $"execute {e.Id} v{e.Version}")
            .OrderBy(s => s).ToList();
        var got = observed.OrderBy(s => s).ToList();

        if (want.SequenceEqual(got))
        {
            _ok++;
            Console.WriteLine($"  ✅ {label}\n       messages: [{string.Join(", ", got)}]");
        }
        else
        {
            _fail++;
            var msg = $"expected [{string.Join(", ", want)}], observed [{string.Join(", ", got)}]";
            _failures.Add($"{label}: {msg}");
            Console.WriteLine($"  ❌ {label}\n       {msg}");
        }
    }

    public async Task Step<TReq>(TReq request, string label)
    {
        // Resolve the operation by request type → registered name.
        var opName = NameFor(request!);
        var op = _spec.GetOperation(opName);

        // Execute against the real server through the bound adapter.
        var observed = await op.ExecuteAsync(_ctx, request!);

        // Check + advance the model.
        var state = (object?)_profile ?? _initial;
        var (isValid, message, next) = state is StateProfile sp
            ? _spec.Allows(op, request!, observed, sp)
            : _spec.Allows(op, request!, observed, _initial);

        var resp = observed as Response;
        var summary = resp is null
            ? observed?.ToString()
            : $"status={resp.Status} promise={resp.PromiseStatus() ?? "-"} task={resp.TaskStatus() ?? "-"}"
              + (resp.TaskVersion() is { } v ? $"(v{v})" : "");

        if (isValid)
        {
            _ok++;
            _profile = next;
            Console.WriteLine($"  ✅ {label}\n       observed: {summary}");
        }
        else
        {
            _fail++;
            _failures.Add($"{label}: {message}");
            Console.WriteLine($"  ❌ {label}\n       observed: {summary}\n       reason: {message}");
        }
    }

    /// <summary>
    /// Liveness probe: repeatedly execute a read op, spec-checking EVERY polled
    /// response (each observation also collapses profile branches), until every
    /// candidate state in the profile satisfies <paramref name="terminal"/> —
    /// i.e. all in-flight background work the model armed has observably landed.
    /// Exhausting the retries is a LIVENESS violation: the server never returned
    /// a wrong response, it just isn't making progress.
    /// </summary>
    public async Task PollLiveness<TReq>(TReq request, Func<ServerState, bool> terminal, string label,
        int maxRetries = 50, int waitMs = 100)
    {
        var op = _spec.GetOperation(NameFor(request!));
        for (int i = 1; i <= maxRetries; i++)
        {
            var observed = await op.ExecuteAsync(_ctx, request!);
            var (isValid, message, next) = _profile is { } sp
                ? _spec.Allows(op, request!, observed, sp)
                : _spec.Allows(op, request!, observed, _initial);

            if (!isValid)
            {
                _fail++;
                _failures.Add($"{label}: poll #{i} rejected: {message}");
                Console.WriteLine($"  ❌ {label}\n       poll #{i} rejected: {message}");
                return;
            }
            _profile = next;

            if (_profile.StatesAndStepFunctions.All(ssf => terminal((ServerState)ssf.State)))
            {
                _ok++;
                Console.WriteLine($"  ✅ {label} — landed after {i} poll(s)");
                return;
            }
            await Task.Delay(waitMs);
        }

        _fail++;
        _failures.Add($"{label}: LIVENESS violation — not terminal after {maxRetries} polls");
        Console.WriteLine($"  ❌ {label} — LIVENESS violation: still in flight after {maxRetries} polls");
    }

    /// <summary>
    /// Negative self-test: feed a FABRICATED response the model must REJECT from
    /// the current profile. Proves spec.Allows has teeth (isn't vacuously true).
    /// Does NOT touch the server or advance state.
    /// </summary>
    public void ExpectRejected<TReq>(TReq request, Response fabricated, string label)
    {
        var op = _spec.GetOperation(NameFor(request!));
        var (isValid, _, _) = _profile is { } sp
            ? _spec.Allows(op, request!, fabricated, sp)
            : _spec.Allows(op, request!, fabricated, _initial);

        if (!isValid)
        {
            _ok++;
            Console.WriteLine($"  ✅ (neg) {label} — model correctly rejected a bogus response");
        }
        else
        {
            _fail++;
            _failures.Add($"(neg) {label}: model ACCEPTED a response it should reject");
            Console.WriteLine($"  ❌ (neg) {label} — model wrongly accepted a bogus response");
        }
    }

    private static string NameFor(object request) => request switch
    {
        CreatePromise => "CreatePromise",
        GetPromise => "GetPromise",
        SettlePromise => "SettlePromise",
        AdvanceClock => "AdvanceClock",
        CreateTask => "CreateTask",
        GetTask => "GetTask",
        AcquireTask => "AcquireTask",
        ReleaseTask => "ReleaseTask",
        FulfillTask => "FulfillTask",
        FenceTask => "FenceTask",
        PollTask => "PollTask",
        HeartbeatTask => "HeartbeatTask",
        RegisterCallback => "RegisterCallback",
        RegisterListener => "RegisterListener",
        SuspendTask => "SuspendTask",
        _ => throw new InvalidOperationException($"no op mapped for {request.GetType().Name}"),
    };

    public int Report()
    {
        Console.WriteLine($"\n========================================");
        Console.WriteLine($"  conformance steps ok: {_ok}   failed: {_fail}");
        foreach (var f in _failures) Console.WriteLine($"    ❌ {f}");
        Console.WriteLine($"========================================");
        return _fail == 0 ? 0 : 1;
    }
}
