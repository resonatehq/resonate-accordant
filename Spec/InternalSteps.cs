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
        && s.Tasks.TryGetValue(awaiter, out var t) && t.Status == "suspended"
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
                        s.Tasks[awaiterId].Status = "pending";
                    },
                    name: $"resume:{id}->{awaiterId}"))
                .ToArray()
            : [];

    private static bool LeaseReclaimable(ServerState s, string taskId) =>
        s.Tasks.TryGetValue(taskId, out var t) && t.Status == "acquired" && s.Now >= t.LeaseExpiry;

    private static IStepFunction[] LeaseTriggers(ServerState state, long now) =>
        state.Tasks
            .Where(kv => kv.Value.Status == "acquired" && now >= kv.Value.LeaseExpiry)
            .Select(IStepFunction (kv) => AsyncOperation.Create<ServerState>(
                isTerminal: s => !LeaseReclaimable(s, kv.Key),
                transition: s =>
                {
                    var t = s.Tasks[kv.Key];
                    t.Status = "pending";          // version UNCHANGED — reclaim, not a new fence
                    t.Ttl = null; t.Pid = null;
                },
                name: $"lease-expiry:{kv.Key}"))
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
            });

        var now = Math.Max(req.To, state.Now);
        var triggers = state.Promises
            .Where(kv => kv.Value.IsExternal && IsTimedOut(kv.Value, now))
            .SelectMany(kv => ResumeTriggers(state, kv.Key))
            .Concat(LeaseTriggers(state, now))
            .ToArray();
        return triggers.Length > 0 ? outcome.Triggers(triggers) : outcome;
    }
}
