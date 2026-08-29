using Microsoft.Accordant;

namespace ResonateConformance;

public static partial class ResonateSpec
{
    private static void RegisterExternalSteps(Spec<ServerState> spec)
    {
        spec.Add(new Handler<GetPromise>("GetPromise", PromiseGet));
        spec.Add(new Handler<CreatePromise>("CreatePromise", PromiseCreate));
        spec.Add(new Handler<SettlePromise>("SettlePromise", PromiseSettle));
        spec.Add(new Handler<RegisterCallback>("RegisterCallback", PromiseRegisterCallback));
        spec.Add(new Handler<RegisterListener>("RegisterListener", PromiseRegisterListener));

        spec.Add(new Handler<GetTask>("GetTask", TaskGet));
        spec.Add(new Handler<CreateTask>("CreateTask", TaskCreate));
        spec.Add(new Handler<AcquireTask>("AcquireTask", TaskAcquire));
        spec.Add(new Handler<FenceTask>("FenceTask", TaskFence));
        spec.Add(new Handler<HeartbeatTask>("HeartbeatTask", TaskHeartbeat));
        spec.Add(new Handler<SuspendTask>("SuspendTask", TaskSuspend));
        spec.Add(new Handler<FulfillTask>("FulfillTask", TaskFulfill));
        spec.Add(new Handler<ReleaseTask>("ReleaseTask", TaskRelease));

        if (Capabilities.Poll)
            spec.Add(new Handler<PollTask>("PollTask", TaskPoll));
    }

    internal static ExpectedOutcomes PromiseCreate(CreatePromise req, ServerState state)
    {
        var now = state.Now;

        if (req.Timer && req.WithTarget)
        {
            return Expect.That<Response>(r => r.Status == 400,
                    "P-02: resonate:timer with resonate:target → 400 (malformed)")
                .SameState();
        }

        if (state.Promises.TryGetValue(req.Id, out var existing))
        {
            var (st, val, settledAt) = Project(existing, now);
            return Expect.That<Response>(
                    r => r.Status == 200 && PromiseMatches(r, req.Id, existing, st, val, settledAt),
                    $"P-02: re-create → projected echo ({st})")
                .SameState();
        }

        if (req.TimeoutAt > now)
        {
            var fresh = new PromiseState
            {
                State = "pending", Value = null, TimeoutAt = req.TimeoutAt,
                ParamData = req.Data, CreatedAt = now,
                HasTarget = req.WithTarget, ExternalTag = req.External, TimerTag = req.Timer,
            };
            return Expect.That<Response>(
                    r => r.Status == 200 && PromiseMatches(r, req.Id, fresh, "pending", null, null),
                    "P-02: fresh create → pending" + (req.WithTarget ? " (task spawned pending v0)" : ""))
                .ThenState<ServerState>(s =>
                {
                    s.Promises[req.Id] = new PromiseState
                    {
                        State = "pending", Value = null, TimeoutAt = req.TimeoutAt,
                        ParamData = req.Data, CreatedAt = s.Now,
                        HasTarget = req.WithTarget, ExternalTag = req.External, TimerTag = req.Timer,
                    };
                    if (req.WithTarget)
                        s.Tasks[req.Id] = new TaskState { State = "pending", Version = 0, RetryTimeoutAt = s.Now };
                });
        }

        var bornState = req.Timer ? "resolved" : "rejected_timedout";
        var born = new PromiseState
        {
            State = bornState, Value = null, TimeoutAt = req.TimeoutAt,
            ParamData = req.Data, CreatedAt = req.TimeoutAt, SettledAt = req.TimeoutAt,
            HasTarget = req.WithTarget, ExternalTag = req.External, TimerTag = req.Timer,
        };
        return Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Id, born, bornState, null, req.TimeoutAt),
                $"P-02: fresh create past timeout → born {bornState} (created/settled = deadline)")
            .ThenState<ServerState>(s =>
            {
                s.Promises[req.Id] = new PromiseState
                {
                    State = bornState, Value = null, TimeoutAt = req.TimeoutAt,
                    ParamData = req.Data, CreatedAt = req.TimeoutAt, SettledAt = req.TimeoutAt,
                    HasTarget = req.WithTarget, ExternalTag = req.External, TimerTag = req.Timer,
                };
                if (req.WithTarget)
                    s.Tasks[req.Id] = new TaskState { State = "fulfilled", Version = 0 };
            });
    }

    internal static ExpectedOutcomes PromiseGet(GetPromise req, ServerState state)
    {
        var now = state.Now;

        if (!state.Promises.TryGetValue(req.Id, out var p))
        {
            return Expect.That<Response>(r => r.Status == 404, "P-01: missing → 404")
                .SameState();
        }

        var (st, val, settledAt) = Project(p, now);
        return Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Id, p, st, val, settledAt),
                $"P-01: → projected record ({st})")
            .SameState();
    }

    internal static ExpectedOutcomes PromiseSettle(SettlePromise req, ServerState state)
    {
        var now = state.Now;

        if (req.State is not ("resolved" or "rejected" or "rejected_canceled"))
        {
            return Expect.That<Response>(r => r.Status == 400, "P-03: state not settable → 400")
                .SameState();
        }

        if (!state.Promises.TryGetValue(req.Id, out var p))
        {
            return Expect.That<Response>(r => r.Status == 404, "P-03: missing → 404")
                .SameState();
        }

        if (p.State == "pending" && p.TimeoutAt > now)
        {
            var outcome = Expect.That<Response>(
                    r => r.Status == 200 && PromiseMatches(r, req.Id, p, req.State, req.Data, now),
                    $"P-03: settle pending → {req.State} (full record; settledAt = now)")
                .ThenState<ServerState>(s => SettleAndFulfillTask(s, req.Id, req.State, req.Data, now));

            var resumes = ResumeTriggers(state, req.Id);
            return resumes.Length > 0 ? outcome.Triggers(resumes) : outcome;
        }

        var (st, val, settledAt) = Project(p, now);
        return Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Id, p, st, val, settledAt),
                $"P-03: not settleable → projected echo ({st})")
            .SameState();
    }

    internal static ExpectedOutcomes PromiseRegisterCallback(RegisterCallback req, ServerState state)
    {
        var now = state.Now;

        if (req.Awaited == req.Awaiter)
        {
            return Expect.That<Response>(r => r.Status == 400, "P-04: awaited == awaiter → 400")
                .SameState();
        }

        if (!SameOrigin(req.Awaited, req.Awaiter))
        {
            return Expect.That<Response>(r => r.Status == 400, "P-04: awaited/awaiter cross-origin → 400")
                .SameState();
        }

        if (!state.Promises.TryGetValue(req.Awaited, out var awaited))
        {
            return Expect.That<Response>(r => r.Status == 404, "P-04: awaited missing → 404")
                .SameState();
        }

        if (!state.Promises.TryGetValue(req.Awaiter, out var awaiter))
        {
            return Expect.That<Response>(r => r.Status == 422, "P-04: awaiter missing → 422")
                .SameState();
        }

        if (!state.Tasks.ContainsKey(req.Awaiter))
        {
            return Expect.That<Response>(r => r.Status == 422, "P-04: awaiter has no task/target → 422")
                .SameState();
        }

        if (!awaited.IsExternal)
        {
            return Expect.That<Response>(r => r.Status == 422, "P-04: awaited is INTERNAL → 422 (not awaitable)")
                .SameState();
        }

        if (awaited.State == "pending" && awaited.TimeoutAt > now)
        {
            var outcome = Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Awaited, awaited, "pending", null, null),
                "P-04: awaited pending → 200 echo");
            if (awaiter.State != "pending" || awaiter.TimeoutAt <= now)
                return outcome.SameState();
            return outcome.ThenState<ServerState>(s => s.Promises[req.Awaited].AddCallback(req.Awaiter));
        }

        var (st, val, settledAt) = Project(awaited, now);
        return Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Awaited, awaited, st, val, settledAt),
                $"P-04: awaited not pending-and-live → projected echo ({st})")
            .SameState();
    }

    internal static ExpectedOutcomes PromiseRegisterListener(RegisterListener req, ServerState state)
    {
        var now = state.Now;

        if (!AddressValid(req.Address))
        {
            return Expect.That<Response>(r => r.Status == 400, "P-05: invalid address → 400")
                .SameState();
        }

        if (!state.Promises.TryGetValue(req.Awaited, out var awaited))
        {
            return Expect.That<Response>(r => r.Status == 404, "P-05: awaited missing → 404")
                .SameState();
        }

        if (!awaited.IsExternal)
        {
            return Expect.That<Response>(r => r.Status == 422, "P-05: awaited is INTERNAL → 422 (not awaitable)")
                .SameState();
        }

        if (awaited.State == "pending" && awaited.TimeoutAt > now)
        {
            return Expect.That<Response>(
                    r => r.Status == 200 && PromiseMatches(r, req.Awaited, awaited, "pending", null, null),
                    "P-05: awaited pending → 200 echo")
                .SameState();
        }

        var (st, val, settledAt) = Project(awaited, now);
        return Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Awaited, awaited, st, val, settledAt),
                $"P-05: awaited not pending-and-live → projected echo ({st})")
            .SameState();
    }

    internal static ExpectedOutcomes TaskHeartbeat(HeartbeatTask req, ServerState state) =>
        Expect.That<Response>(r => r.Status == 200, "T-05: heartbeat → 200")
            .ThenState<ServerState>(s =>
            {
                if (s.Tasks.TryGetValue(req.Id, out var t)
                    && t.State == "acquired" && t.Version == req.Version && t.Pid == req.Pid
                    && s.Promises.TryGetValue(req.Id, out var p)
                    && p.State == "pending" && p.TimeoutAt > s.Now)
                    t.AcquiredAt = s.Now;
            });

    internal static ExpectedOutcomes TaskSuspend(SuspendTask req, ServerState state)
    {
        var now = state.Now;
        var ids = req.AwaitedIds;

        if (ids.Length == 0)
        {
            return Expect.That<Response>(r => r.Status == 400, "T-06: empty actions → 400")
                .SameState();
        }

        if (ids.Contains(req.Id))
        {
            return Expect.That<Response>(r => r.Status == 400, "T-06: awaiting self → 400")
                .SameState();
        }

        if (ids.Distinct().Count() != ids.Length)
        {
            return Expect.That<Response>(r => r.Status == 400, "T-06: duplicate awaited ids → 400")
                .SameState();
        }

        if (ids.FirstOrDefault(a => !SameOrigin(a, req.Id)) is { } foreign)
        {
            return Expect.That<Response>(r => r.Status == 400, $"T-06: awaited {foreign} cross-origin → 400")
                .SameState();
        }

        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-06: task missing → 404")
                .SameState();
        }

        var tp = state.Promises[req.Id];

        if (task.State != "acquired")
        {
            return Expect.That<Response>(r => r.Status == 409, "T-06: not acquired → 409")
                .SameState();
        }

        if (tp.State != "pending" || tp.TimeoutAt <= now)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-06: own promise not pending-and-live → 409")
                .SameState();
        }

        if (req.Version != task.Version)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-06: version mismatch → 409")
                .SameState();
        }

        var settled = false;
        foreach (var a in ids)
        {
            if (!state.Promises.TryGetValue(a, out var pa))
            {
                return Expect.That<Response>(r => r.Status == 422, $"T-06: awaited {a} missing → 422")
                    .SameState();
            }
            if (!pa.IsExternal)
            {
                return Expect.That<Response>(r => r.Status == 422, $"T-06: awaited {a} is INTERNAL → 422 (not awaitable)")
                    .SameState();
            }
            if (pa.State != "pending" || pa.TimeoutAt <= now)
                settled = true;
        }

        if (settled)
        {
            return Expect.That<Response>(r => r.Status == 300, "T-06: an awaited settled → 300 resume-now (task stays acquired)")
                .SameState();
        }

        return Expect.That<Response>(r => r.Status == 200, "T-06: all awaited pending → 200 suspended")
            .ThenState<ServerState>(s =>
            {
                var t = s.Tasks[req.Id];
                t.State = "suspended"; t.Pid = null; t.Ttl = null;
                t.RetryTimeoutAt = null;
                foreach (var a in ids)
                    s.Promises[a].AddCallback(req.Id);
            });
    }

    internal static ExpectedOutcomes TaskCreate(CreateTask req, ServerState state)
    {
        var now = state.Now;

        if (!req.WithTarget)
        {
            return Expect.That<Response>(r => r.Status == 400, "T-02: action has no target → 400")
                .SameState();
        }

        if (state.Promises.TryGetValue(req.Id, out var p))
        {
            if (!p.HasTarget)
            {
                return Expect.That<Response>(r => r.Status == 422, "T-02: existing plain promise → 422")
                    .SameState();
            }

            var existing = state.Tasks[req.Id];

            if (existing.State == "fulfilled")
            {
                return Expect.That<Response>(
                        r => r.Status == 200
                             && TaskMatches(r, req.Id, "fulfilled", existing.Version, null, null),
                        "T-02: over fulfilled task → 200 idempotent")
                    .SameState();
            }

            if (existing.State == "pending")
            {
                var claimedVersion = existing.Version + 1;
                return Expect.That<Response>(
                        r => r.Status == 200
                             && TaskMatches(r, req.Id, "acquired", claimedVersion, "worker-self", TaskCreateTtl),
                        $"T-02: claims pending task v{existing.Version} → acquired v{claimedVersion}")
                    .ThenState<ServerState>(s =>
                    {
                        var t = s.Tasks[req.Id];
                        t.State = "acquired"; t.Version = claimedVersion;
                        t.AcquiredAt = s.Now; t.Ttl = TaskCreateTtl; t.Pid = "worker-self";
                        t.RetryTimeoutAt = null;
                    });
            }

            return Expect.That<Response>(r => r.Status == 409, "T-02: over active task → 409 Already exists")
                .SameState();
        }

        var fresh = new PromiseState
        {
            State = "pending", Value = null, TimeoutAt = req.TimeoutAt,
            ParamData = req.Data, HasTarget = true, CreatedAt = now,
        };
        return Expect.That<Response>(
                r => r.Status == 200
                     && PromiseMatches(r, req.Id, fresh, "pending", null, null)
                     && TaskMatches(r, req.Id, "acquired", 1, "worker-self", TaskCreateTtl),
                "T-02: fresh → promise pending + task acquired v1 (full records)")
            .ThenState<ServerState>(s =>
            {
                s.Promises[req.Id] = new PromiseState
                {
                    State = "pending", Value = null, TimeoutAt = req.TimeoutAt,
                    ParamData = req.Data, HasTarget = true, CreatedAt = s.Now,
                };
                s.Tasks[req.Id] = new TaskState { State = "acquired", Version = 1, AcquiredAt = s.Now, Ttl = TaskCreateTtl, Pid = "worker-self" };
            });
    }

    internal static ExpectedOutcomes TaskGet(GetTask req, ServerState state)
    {
        var now = state.Now;

        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-01: missing → 404")
                .SameState();
        }

        var p = state.Promises[req.Id];

        if (p.State == "pending" && p.TimeoutAt > now)
        {
            var st = task.State;
            var pid = task.Pid;
            var ttl = task.Ttl;
            var version = task.Version;
            return Expect.That<Response>(
                    r => r.Status == 200 && TaskMatches(r, req.Id, st, version, pid, ttl),
                    $"T-01: → {st} v{version} (current record)")
                .SameState();
        }

        var v = task.Version;
        return Expect.That<Response>(
                r => r.Status == 200 && TaskMatches(r, req.Id, "fulfilled", v, null, null),
                $"T-01: promise not pending-and-live → projected fulfilled v{v}")
            .SameState();
    }

    internal static ExpectedOutcomes TaskAcquire(AcquireTask req, ServerState state)
    {
        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-03: missing → 404")
                .SameState();
        }

        if (task.State != "pending")
        {
            return Expect.That<Response>(r => r.Status == 409, "T-03: not pending → 409")
                .SameState();
        }

        var p = state.Promises[req.Id];
        if (p.State != "pending" || p.TimeoutAt <= state.Now)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-03: promise not pending-and-live → 409")
                .SameState();
        }

        if (req.Version != task.Version)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-03: version mismatch → 409")
                .SameState();
        }

        var newVersion = task.Version + 1;
        return Expect.That<Response>(
                r => r.Status == 200
                     && TaskMatches(r, req.Id, "acquired", newVersion, req.Pid, req.Ttl),
                $"T-03: pending v{task.Version} → acquired v{newVersion}")
            .ThenState<ServerState>(s =>
            {
                var t = s.Tasks[req.Id];
                t.State = "acquired"; t.Version = newVersion;
                t.AcquiredAt = s.Now; t.Ttl = req.Ttl; t.Pid = req.Pid;
                t.RetryTimeoutAt = null;
            });
    }

    internal static ExpectedOutcomes TaskRelease(ReleaseTask req, ServerState state)
    {
        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-08: missing → 404")
                .SameState();
        }

        if (task.State != "acquired")
        {
            return Expect.That<Response>(r => r.Status == 409, "T-08: not acquired → 409")
                .SameState();
        }

        var p = state.Promises[req.Id];
        if (p.State != "pending" || p.TimeoutAt <= state.Now)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-08: promise not pending-and-live → 409")
                .SameState();
        }

        if (req.Version != task.Version)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-08: version mismatch → 409")
                .SameState();
        }

        return Expect.That<Response>(r => r.Status == 200, "T-08: acquired → pending (version unchanged)")
            .ThenState<ServerState>(s =>
            {
                var t = s.Tasks[req.Id];
                t.State = "pending"; t.Pid = null; t.Ttl = null;
                t.RetryTimeoutAt = s.Now;
            });
    }

    internal static ExpectedOutcomes TaskFulfill(FulfillTask req, ServerState state)
    {
        var now = state.Now;

        if (req.State is not ("resolved" or "rejected" or "rejected_canceled"))
        {
            return Expect.That<Response>(r => r.Status == 400, "T-07: state not settable → 400")
                .SameState();
        }

        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-07: missing → 404")
                .SameState();
        }

        if (task.State != "acquired")
        {
            return Expect.That<Response>(r => r.Status == 409, "T-07: not acquired → 409 (no settle)")
                .SameState();
        }

        var p = state.Promises[req.Id];
        if (p.State != "pending" || p.TimeoutAt <= now)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-07: promise not pending-and-live → 409 (no settle)")
                .SameState();
        }

        if (req.Version != task.Version)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-07: version mismatch → 409 (no settle)")
                .SameState();
        }

        var outcome = Expect.That<Response>(
                r => r.Status == 200 && PromiseMatches(r, req.Id, p, req.State, req.Data, now),
                $"T-07: fulfill → task fulfilled + promise {req.State} (settledAt = now)")
            .ThenState<ServerState>(s => SettleAndFulfillTask(s, req.Id, req.State, req.Data, now));

        var resumes = ResumeTriggers(state, req.Id);
        return resumes.Length > 0 ? outcome.Triggers(resumes) : outcome;
    }

    internal static ExpectedOutcomes TaskFence(FenceTask req, ServerState state)
    {
        if (req.TargetId == req.Id)
        {
            return Expect.That<Response>(r => r.Status == 400, "T-04: action targets own id → 400")
                .SameState();
        }

        if (!state.Tasks.TryGetValue(req.Id, out var task))
        {
            return Expect.That<Response>(r => r.Status == 404, "T-04: missing → 404")
                .SameState();
        }

        if (task.State != "acquired")
        {
            return Expect.That<Response>(r => r.Status == 409, "T-04: not acquired → 409 (no side effect)")
                .SameState();
        }

        var own = state.Promises[req.Id];
        if (own.State != "pending" || own.TimeoutAt <= state.Now)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-04: promise not pending-and-live → 409 (no side effect)")
                .SameState();
        }

        if (req.Version != task.Version)
        {
            return Expect.That<Response>(r => r.Status == 409, "T-04: version mismatch → 409 (no side effect)")
                .SameState();
        }

        if (req.Create is { } create)
            return PromiseCreate(create, state);

        return PromiseSettle(req.Settle!, state);
    }

    internal static ExpectedOutcomes TaskPoll(PollTask req, ServerState state)
    {
        var now = state.Now;
        var claimable = Claimable(state, now);

        if (claimable.Count > req.Limit)
        {
            return Expect.That<Response>(r => r.Status == 200 && r.ClaimedTasks().Count == req.Limit,
                    $"task.poll: partial drain ({claimable.Count} claimable, limit {req.Limit})")
                .SameState();
        }

        return Expect.That<Response>(
                r => r.Status == 200 && PollMatches(r, claimable, state, req),
                $"task.poll: claims {claimable.Count} task(s) [{string.Join(",", claimable)}] at version+1")
            .ThenState<ServerState>(s =>
            {
                foreach (var id in claimable)
                {
                    var t = s.Tasks[id];
                    t.State = "acquired";
                    t.Version += 1;
                    t.Pid = req.Pid;
                    t.Ttl = req.Ttl;
                    t.AcquiredAt = s.Now;
                    t.RetryTimeoutAt = null;

                    foreach (var p in s.Promises.Values) p.Callbacks.Remove(id);
                }
            });
    }

    private static List<string> Claimable(ServerState state, long now) =>
        state.Tasks
            .Where(kv =>
            {
                var (id, t) = (kv.Key, kv.Value);
                if (!state.Promises.TryGetValue(id, out var p)) return false;
                if (!p.HasTarget) return false;
                if (p.Project(now).IsTerminal) return false;
                if (t.State == "pending") return true;
                if (t.State == "acquired" && now >= t.LeaseTimeoutAt) return true;
                if (t.State != "suspended") return false;
                return state.Promises.Any(kv2 =>
                    kv2.Value.Callbacks.Contains(id) && kv2.Value.Project(now).IsTerminal);
            })
            .Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static bool PollMatches(Response r, List<string> claimable, ServerState state, PollTask req)
    {
        var claimed = r.ClaimedTasks();
        if (claimed.Count != claimable.Count) return false;
        var byId = claimed.Where(c => c.Task is not null).ToDictionary(c => c.Task!.Id, c => c.Task!);
        if (byId.Count != claimable.Count) return false;
        return claimable.All(id =>
            byId.TryGetValue(id, out var rec)
            && rec.State == "acquired"
            && rec.Version == state.Tasks[id].Version + 1
            && rec.Pid == req.Pid
            && rec.Ttl == req.Ttl);
    }

}
