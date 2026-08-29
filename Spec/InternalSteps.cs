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

    /// <summary>
    /// The redispatch cadence — the server's `tasks.retry_timeout` dial, whose
    /// default is 30s (the specification's own default is 5s; either way the
    /// value is unobservable here, see RetryTriggers).
    /// </summary>
    private const long RetryTimeout = 30_000;

    /// <summary>R6's guard: a pending task, its dispatch due, its promise still pending.</summary>
    private static bool RetryDue(ServerState s, string taskId) =>
        s.Tasks.TryGetValue(taskId, out var t) && t.State == "pending"
        && t.RetryTimeoutAt is { } due && s.Now >= due
        && s.Promises.TryGetValue(taskId, out var p) && p.State == "pending" && s.Now < p.TimeoutAt;

    /// <summary>
    /// R6 processRetryTimeout — redispatch a pending task whose dispatch clock
    /// is due, and re-arm that clock a dial's width out.
    ///
    /// The machine makes firing a CHOICE, and R4 and R5 are modelled that way,
    /// as triggers. R6 CANNOT be, while the egress tap is missing. Its two
    /// effects are the re-arm and an `execute` on the outbox, and neither is
    /// observable here: `TaskRecord` carries no dispatch clock, and
    /// `debug.messages` is an op the server does not implement. A choice
    /// nothing can observe is a branch nothing can ever collapse — every tick
    /// would double the profile per pending task, and the trace hung on its
    /// first tick when this was written as a trigger.
    ///
    /// So: as a trigger when the egress capability says the emission is
    /// observable (and a read can therefore rejoin the branches), and folded
    /// into the clock step otherwise. Folding asserts the fired state where the
    /// server may not have fired yet — sound precisely BECAUSE no prediction
    /// depends on the difference.
    /// </summary>
    private static IStepFunction[] RetryTriggers(ServerState state, long now) =>
        !Capabilities.Egress ? [] :
        state.Tasks
            .Where(kv => RetryDue(WithNow(state, now), kv.Key))
            .Select(IStepFunction (kv) => AsyncOperation.Create<ServerState>(
                isTerminal: s => !RetryDue(s, kv.Key),
                transition: s => s.Tasks[kv.Key].RetryTimeoutAt = s.Now + RetryTimeout,
                name: $"retry-timeout:{kv.Key}"))
            .ToArray();

    /// <summary>R6 folded: every due dispatch re-armed, in one state.</summary>
    internal static void FoldRetryTimeouts(ServerState s)
    {
        if (Capabilities.Egress) return;
        foreach (var id in s.Tasks.Keys.ToList())
            if (RetryDue(s, id))
                s.Tasks[id].RetryTimeoutAt = s.Now + RetryTimeout;
    }

    /// <summary>RetryDue reads `s.Now`; the trigger list is built for a FUTURE now.</summary>
    private static ServerState WithNow(ServerState s, long now)
    {
        if (s.Now >= now) return s;
        var view = (ServerState)s.Clone();
        view.Now = now;
        return view;
    }

    /// <summary>
    /// R5 processLeaseTimeout's body: the lease runs out, the task returns to
    /// pending at the SAME version — a reclaim, not a new fence — and the
    /// dispatch clock takes over from the lease.
    /// </summary>
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
                name: $"lease-timeout:{kv.Key}"))   // R5 processLeaseTimeout
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
