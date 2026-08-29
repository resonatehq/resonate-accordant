using Microsoft.Accordant;

namespace ResonateConformance;

public static partial class ResonateSpec
{
    private static void RegisterInternalSteps(Spec<ServerState> spec)
    {
        spec.Add(new Handler<AdvanceClock>("AdvanceClock", AdvanceClockStep));
    }

    private static bool Fireable(ServerState s, string awaited, string awaiter) =>
        s.Promises.TryGetValue(awaited, out var p) && p.Callbacks.Contains(awaiter)
        && s.Tasks.TryGetValue(awaiter, out var t) && t.State == "suspended"
        && s.Promises.TryGetValue(awaiter, out var ap) && s.Now < ap.TimeoutAt;

    private static IStepFunction[] ResumeTriggers(ServerState state, string id) =>
        state.Promises.TryGetValue(id, out var p)
            ? p.Callbacks
                .Where(a => Fireable(state, id, a))
                .Select(IStepFunction (awaiterId) => AsyncOperation.Create<ServerState>(
                    isTerminal: s => !Fireable(s, id, awaiterId),
                    transition: s =>
                    {
                        s.Promises[id].Callbacks.Remove(awaiterId);
                        s.Tasks[awaiterId].State = "pending";
                        s.Tasks[awaiterId].RetryTimeoutAt = s.Now;
                    },
                    name: $"resume:{id}->{awaiterId}"))
                .ToArray()
            : [];

    private static bool LeaseReclaimable(ServerState s, string taskId) =>
        s.Tasks.TryGetValue(taskId, out var t) && t.State == "acquired" && s.Now >= t.LeaseTimeoutAt;

    private const long RetryTimeout = 30_000;

    private static bool RetryDue(ServerState s, string taskId) =>
        s.Tasks.TryGetValue(taskId, out var t) && t.State == "pending"
        && t.RetryTimeoutAt is { } due && s.Now >= due
        && s.Promises.TryGetValue(taskId, out var p) && p.State == "pending" && s.Now < p.TimeoutAt;

    private static IStepFunction[] RetryTriggers(ServerState state, long now) =>
        !Capabilities.Egress ? [] :
        state.Tasks
            .Where(kv => RetryDue(WithNow(state, now), kv.Key))
            .Select(IStepFunction (kv) => AsyncOperation.Create<ServerState>(
                isTerminal: s => !RetryDue(s, kv.Key),
                transition: s => s.Tasks[kv.Key].RetryTimeoutAt = s.Now + RetryTimeout,
                name: $"retry-timeout:{kv.Key}"))
            .ToArray();

    internal static void FoldRetryTimeouts(ServerState s)
    {
        if (Capabilities.Egress) return;
        foreach (var id in s.Tasks.Keys.ToList())
            if (RetryDue(s, id))
                s.Tasks[id].RetryTimeoutAt = s.Now + RetryTimeout;
    }

    private static ServerState WithNow(ServerState s, long now)
    {
        if (s.Now >= now) return s;
        var view = (ServerState)s.Clone();
        view.Now = now;
        return view;
    }

    internal static void ReclaimLease(ServerState s, string taskId)
    {
        var t = s.Tasks[taskId];
        t.State = "pending";
        t.Ttl = null; t.Pid = null;
        t.RetryTimeoutAt = s.Now;
    }

    private static IStepFunction[] LeaseTriggers(ServerState state, long now) =>
        state.Tasks
            .Where(kv => kv.Value.State == "acquired" && now >= kv.Value.LeaseTimeoutAt)
            .Select(IStepFunction (kv) => AsyncOperation.Create<ServerState>(
                isTerminal: s => !LeaseReclaimable(s, kv.Key),
                transition: s => ReclaimLease(s, kv.Key),
                name: $"lease-timeout:{kv.Key}"))
            .ToArray();

    internal static ExpectedOutcomes AdvanceClockStep(AdvanceClock req, ServerState state)
    {
        var outcome = Expect.That<Response>(r => r.Status is 200 or 400 or 501, "advance-clock ok (or declined)")
            .ThenState<ServerState>(s =>
            {
                if (req.To > s.Now) s.Now = req.To;

                foreach (var id in s.Promises.Keys.ToList())
                    if (s.Promises[id].IsExternal && IsTimedOut(s.Promises[id], s.Now))
                        SettleAndFulfillTask(s, id,
                            s.Promises[id].TimerTag ? "resolved" : "rejected_timedout",
                            null, s.Promises[id].TimeoutAt);

                FoldRetryTimeouts(s);
            });

        var now = Math.Max(req.To, state.Now);
        var triggers = state.Promises
            .Where(kv => kv.Value.IsExternal && IsTimedOut(kv.Value, now))
            .SelectMany(kv => ResumeTriggers(state, kv.Key))
            .Concat(LeaseTriggers(state, now))
            .Concat(RetryTriggers(state, now))
            .ToArray();
        return triggers.Length > 0 ? outcome.Triggers(triggers) : outcome;
    }
}
