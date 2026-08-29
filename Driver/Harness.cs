using Microsoft.Accordant;

namespace ResonateConformance;

public sealed class Harness
{
    public static readonly Dictionary<string, string> TargetTags = new()
    {
        ["resonate:invoke"] = "poll://any@testgroup",
        ["resonate:target"] = "poll://any@testgroup",
    };

    public static readonly Dictionary<string, string> ExternalTags = new()
    {
        ["resonate:external"] = "true",
    };

    public static readonly Dictionary<string, string> TimerTags = new()
    {
        ["resonate:timer"] = "true",
    };

    public static Dictionary<string, string>? TagsFor(CreatePromise r)
    {
        var tags = new Dictionary<string, string>();
        if (r.WithTarget) foreach (var kv in TargetTags) tags[kv.Key] = kv.Value;
        if (r.External) foreach (var kv in ExternalTags) tags[kv.Key] = kv.Value;
        if (r.Timer) foreach (var kv in TimerTags) tags[kv.Key] = kv.Value;
        return tags.Count == 0 ? null : tags;
    }

    public Spec<ServerState> Spec { get; }
    public Client Client { get; }

    public long Now { get; set; } = 1_000_000;

    public Harness(HttpClient http, bool trace = false)
    {
        Client = new Client(http) { Trace = trace, DebugTimeProvider = () => Now };
        Spec = ResonateSpec.Build();

        var exec = Spec.ExecuteWith<Client>()
            .BindAsync<CreatePromise, Response>("CreatePromise",
                (c, r) => c.PromiseCreate(r.Id, r.TimeoutAt, Value.Of(r.Data), TagsFor(r)))
            .BindAsync<GetPromise, Response>("GetPromise",
                (c, r) => c.PromiseGet(r.Id))
            .BindAsync<SettlePromise, Response>("SettlePromise",
                (c, r) => c.PromiseSettle(r.Id, r.State, Value.Of(r.Data)))
            .BindAsync<CreateTask, Response>("CreateTask",
                (c, r) => c.TaskCreate("worker-self", 3_600_000,
                    Client.PromiseCreateAction(r.Id, r.TimeoutAt, Value.Of(r.Data),
                        r.WithTarget ? TargetTags : null)))
            .BindAsync<GetTask, Response>("GetTask",
                (c, r) => c.TaskGet(r.Id))
            .BindAsync<AcquireTask, Response>("AcquireTask",
                (c, r) => c.TaskAcquire(r.Id, r.Version, r.Pid, r.Ttl))
            .BindAsync<HeartbeatTask, Response>("HeartbeatTask",
                (c, r) => c.TaskHeartbeat(r.Pid, [new { id = r.Id, version = r.Version }]))
            .BindAsync<ReleaseTask, Response>("ReleaseTask",
                (c, r) => c.TaskRelease(r.Id, r.Version))
            .BindAsync<FulfillTask, Response>("FulfillTask",
                (c, r) => c.TaskFulfill(r.Id, r.Version,
                    Client.PromiseSettleAction(r.Id, r.State, Value.Of(r.Data))))
            .BindAsync<FenceTask, Response>("FenceTask",
                async (c, r) => UnwrapFence(await c.TaskFence(r.Id, r.Version,
                    r.Create is { } cr
                        ? Client.PromiseCreateAction(cr.Id, cr.TimeoutAt, Value.Of(cr.Data), TagsFor(cr))
                        : Client.PromiseSettleAction(r.Settle!.Id, r.Settle.State, Value.Of(r.Settle.Data)))))
            .BindAsync<RegisterCallback, Response>("RegisterCallback",
                (c, r) => c.PromiseRegisterCallback(r.Awaited, r.Awaiter))
            .BindAsync<RegisterListener, Response>("RegisterListener",
                (c, r) => c.PromiseRegisterListener(r.Awaited, r.Address))
            .BindAsync<SuspendTask, Response>("SuspendTask",
                (c, r) => c.TaskSuspend(r.Id, r.Version,
                    r.AwaitedIds.Select(a => Client.RegisterCallbackAction(a, r.Id)).ToArray()))
            .BindAsync<AdvanceClock, Response>("AdvanceClock", async (c, r) =>
            {
                Now = r.To;
                return await c.DebugTick(r.To);
            });

        if (Capabilities.Poll)
            exec = exec.BindAsync<PollTask, Response>("PollTask",
                (c, r) => c.TaskPoll(r.Group, r.Pid, r.Ttl, r.Limit));

        exec.Done();
    }

    private static Response UnwrapFence(Response r)
    {
        if (r.Status != 200
            || r.Data.ValueKind != System.Text.Json.JsonValueKind.Object
            || !r.Data.TryGetProperty("action", out var action))
            return r;
        var status = action.GetProperty("head").GetProperty("status").GetInt32();
        return new Response(r.Kind, new ResponseHead(r.Head.CorrId, status, r.Head.Version),
            action.GetProperty("data").Clone());
    }

    public ServerState InitialState() => new() { Now = Now };

    public TestingContext NewContext()
    {
        var ctx = Spec.CreateTestingContext(Path.GetTempPath());
        ctx.Register(Client);
        return ctx;
    }
}
