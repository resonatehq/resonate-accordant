namespace ResonateConformance;

// Model-level request DTOs — one per operation, named after the wire op it drives.
// These are what Accordant generates test inputs from and what the harness's
// BindAsync adapter turns into raw-HTTP calls. Kept minimal and value-typed so
// Accordant can print and structurally compare them.

// --- Promise operations ------------------------------------------------------

/// <summary>promise.create — ONE op for the ONE wire request; the tags decide
/// everything else. WithTarget adds Harness.TargetTags (a paired task is
/// spawned); External adds the resonate:external tag (awaitable, durable
/// timeout armed, no task).</summary>
public sealed record CreatePromise(string Id, long TimeoutAt, string? Data,
    bool External = false, bool WithTarget = false);
public sealed record GetPromise(string Id);
public sealed record SettlePromise(string Id, string State, string? Data);

// --- Task operations ---------------------------------------------------------

/// <summary>task.create — atomic create+claim (creator = executor). WithTarget gates the 400 branch.</summary>
public sealed record CreateTask(string Id, long TimeoutAt, string? Data, bool WithTarget);

/// <summary>task.get — read a task's state/version (pure read; the liveness probe).</summary>
public sealed record GetTask(string Id);

public sealed record AcquireTask(string Id, int Version, string Pid, long Ttl = 60_000);
public sealed record ReleaseTask(string Id, int Version);
public sealed record FulfillTask(string Id, int Version, string State, string? Data);
public sealed record HeartbeatTask(string Id, int Version, string Pid);

/// <summary>task.poll — claim work by GROUP rather than by id (capability
/// "poll"). Returns every claimable task of the group, each already acquired
/// at version+1 with the lease armed. The model covers the FULL-DRAIN case
/// only, so Limit must be at least the claimable count.</summary>
public sealed record PollTask(string Group, string Pid, long Ttl = 60_000, int Limit = 100);

/// <summary>task.fence — run the inner action only if task Id is still acquired
/// at Version. Exactly one of Create/Settle is set: the C# spelling of Lean's
/// TaskFenceAction (.create CreatePromiseReq | .settle SettlePromiseReq).</summary>
public sealed record FenceTask(string Id, int Version, CreatePromise? Create = null, SettlePromise? Settle = null)
{
    /// <summary>TaskFenceAction.targetId — the promise id the fenced action operates on.</summary>
    public string TargetId => Create?.Id ?? Settle!.Id;
}

// --- Await / callback operations ---------------------------------------------

public sealed record RegisterCallback(string Awaited, string Awaiter);
public sealed record RegisterListener(string Awaited, string Address);

/// <summary>task.suspend — park task Id awaiting the given promises (wait-any).
/// Awaited is the COMMA-JOINED spelling of Lean's `req.actions` list, kept a
/// plain string so the record stays value-typed: "" = empty actions, "a,a" =
/// duplicates — both must 400 before any state is consulted.</summary>
public sealed record SuspendTask(string Id, int Version, string Awaited)
{
    public string[] AwaitedIds =>
        Awaited.Length == 0 ? [] : Awaited.Split(',');
}

// --- Harness operation (not a server op) -------------------------------------

/// <summary>Advance the injected logical clock to <see cref="To"/> (drives timeouts / lease expiry).</summary>
public sealed record AdvanceClock(long To);
