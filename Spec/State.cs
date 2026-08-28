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

    public bool IsExternal => HasTarget || ExternalTag;
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
        projected.Status = "rejected_timedout";
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

    public long LeaseExpiry => AcquiredAt + (Ttl ?? 0);

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
