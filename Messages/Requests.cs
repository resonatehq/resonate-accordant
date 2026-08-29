namespace ResonateConformance;

public sealed record CreatePromise(string Id, long TimeoutAt, string? Data,
    bool External = false, bool WithTarget = false, bool Timer = false);
public sealed record GetPromise(string Id);
public sealed record SettlePromise(string Id, string State, string? Data);

public sealed record CreateTask(string Id, long TimeoutAt, string? Data, bool WithTarget);

public sealed record GetTask(string Id);

public sealed record AcquireTask(string Id, int Version, string Pid, long Ttl = 60_000);
public sealed record ReleaseTask(string Id, int Version);
public sealed record FulfillTask(string Id, int Version, string State, string? Data);
public sealed record HeartbeatTask(string Id, int Version, string Pid);

public sealed record PollTask(string Group, string Pid, long Ttl = 60_000, int Limit = 100);

public sealed record FenceTask(string Id, int Version, CreatePromise? Create = null, SettlePromise? Settle = null)
{

    public string TargetId => Create?.Id ?? Settle!.Id;
}

public sealed record RegisterCallback(string Awaited, string Awaiter);
public sealed record RegisterListener(string Awaited, string Address);

public sealed record SuspendTask(string Id, int Version, string Awaited)
{
    public string[] AwaitedIds =>
        Awaited.Length == 0 ? [] : Awaited.Split(',');
}

public sealed record AdvanceClock(long To);
