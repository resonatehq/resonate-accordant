using Microsoft.Accordant;

namespace ResonateConformance;

[State]
public partial class PromiseState
{
    public string Status { get; set; } = "pending";
    public string? Value { get; set; }
    public long TimeoutAt { get; set; }

    public string? ParamData { get; set; }
    public bool HasTarget { get; set; }
    public bool ExternalTag { get; set; }
    public bool TimerTag { get; set; }

    /// <summary>
    /// Awaitable from outside its own call graph — what gates register_callback,
    /// register_listener and suspend, and what makes a timeout DURABLE rather
    /// than a read-time projection.
    ///
    /// THREE disjuncts, per spec/02-abstract/state.lean:92
    /// (`external = externalTag || targeted || isTimer`). The timer case is the
    /// one that is easy to lose: a timer promise carries neither of the other
    /// two tags, and awaiting one is the whole point of having it.
    /// </summary>
    public bool IsExternal => HasTarget || ExternalTag || TimerTag;
    public long CreatedAt { get; set; }
    public long? SettledAt { get; set; }

    public bool IsTerminal =>
        Status is "resolved" or "rejected" or "rejected_canceled" or "rejected_timedout";

    public List<string> Callbacks { get; set; } = new();

    public void AddCallback(string awaiterId)
    {
        if (!Callbacks.Contains(awaiterId))
            Callbacks.Add(awaiterId);
    }

    public PromiseState Project(long now)
    {
        if (!(Status == "pending" && TimeoutAt <= now))
            return this;

        var projected = (PromiseState)Clone();
        // A timer's deadline is its SUCCESS: it resolves. Only a non-timer's
        // deadline rejects it (state.lean:111).
        projected.Status = TimerTag ? "resolved" : "rejected_timedout";
        projected.SettledAt = TimeoutAt;
        return projected;
    }
}

[State]
public partial class TaskState
{
    public string Status { get; set; } = "pending";
    public int Version { get; set; }

    public long AcquiredAt { get; set; }
    public long? Ttl { get; set; }
    public string? Pid { get; set; }

    /// <summary>
    /// When the lease runs out — spec/02-abstract `TaskObject.leaseTimeoutAt`.
    /// The spec stores it; this model derives it from the acquire instant and
    /// the ttl, which is the same instant by a different route.
    /// </summary>
    public long LeaseTimeoutAt => AcquiredAt + (Ttl ?? 0);

    public void Fulfill()
    {
        Status = "fulfilled";
        Pid = null;
        Ttl = null;
    }

    public (string Status, int Version, string? Pid, long? Ttl) View(PromiseState projected) =>
        projected.Status != "pending" && Status != "fulfilled"
            ? ("fulfilled", Version, null, null)
            : (Status, Version, Pid, Ttl);
}

[State]
public partial class ServerState
{
    public Dictionary<string, PromiseState> Promises { get; set; } = new();

    public Dictionary<string, TaskState> Tasks { get; set; } = new();

    public long Now { get; set; }

    public PromiseState? ViewPromise(string id, long now) =>
        Promises.TryGetValue(id, out var p) ? p.Project(now) : null;

    public (TaskState Task, PromiseState Promise)? ViewTask(string id, long now) =>
        Tasks.TryGetValue(id, out var t) && Promises.TryGetValue(id, out var p)
            ? (t, p.Project(now))
            : null;

    public void SetSettled(string id, string state, string? value, long settledAt)
    {
        var p = Promises[id];
        p.Status = state;
        p.Value = value;
        p.SettledAt = settledAt;

        if (Tasks.TryGetValue(id, out var t) && t.Status != "fulfilled")
            t.Fulfill();
    }
}
