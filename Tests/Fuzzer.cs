using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Continuous fuzz/stress runner. Generates a random mix of SEQUENTIAL and
/// CONCURRENT operations, drives them against the live server, and validates
/// every observed response through the spec — spec.Allows for sequential steps
/// and spec.AllowsConcurrent for concurrent bursts — threading ONE StateProfile
/// forward across the whole run.
///
/// Extras requested:
///  - when a promise reaches a terminal state, it is polled a few extra times to
///    confirm it does NOT flip-flop (stickiness), each poll checked via the spec;
///  - periodically rolls to a fresh id "epoch" so state doesn't grow unbounded
///    and settled promises get replaced by new work;
///  - fully deterministic given a seed, so any failure reproduces.
///
/// A single conformance failure stops the run and prints the reproducing step.
/// </summary>
public sealed class Fuzzer
{
    private readonly Harness _harness;
    private readonly Spec<ServerState> _spec;
    private readonly Random _rng;
    private readonly int _seed;

    private StateProfile _profile = null!;
    // Use a large, realistic epoch-ms clock (~2065): all logical timestamps sit far
    // beyond the real wall clock, so the server's BACKGROUND (wall-clock) timeout
    // loop never interferes — time moves ONLY when we tick. Deadlines are a mix:
    // far-future (never fire) and NEAR (a few seconds past _now), so sequential
    // ticks genuinely fire timeouts mid-fuzz — durable settle, task fulfillment,
    // and awaiter resumes all under random interleaving.
    private const long FuzzNow = 3_000_000_000_000; // ~2065
    private const long FuzzFar = 9_000_000_000_000; // ~2255, always future
    private long _now = FuzzNow;

    private long NextDeadline() =>
        _rng.NextDouble() < 0.30 ? _now + 3_000 + _rng.Next(15_000) : FuzzFar;

    // Shadow bookkeeping so we can generate MEANINGFUL ops referencing live ids.
    private int _epoch;
    private readonly List<string> _plainPromises = [];   // created without target
    private readonly List<string> _taskPromises = [];     // created with target (has a task)
    private int _idCounter;

    private int _seqOk, _concOk, _pollOk, _fails;
    private readonly List<string> _history = [];
    private readonly Invariants _inv = new();

    // GUIDANCE (sim/guided.go's IJON idea): read the CURRENT model state to
    // satisfy version fences and steer toward cascades. Reading any candidate
    // state is fine — guidance only shapes generation; correctness stays with
    // the checker. A (1 - guided) fraction stays pure-random so the workload
    // keeps its adversarial edges (wrong versions, ghosts, bogus states).
    private readonly double _guidedProb = 0.85;
    private ServerState? ModelState =>
        _profile?.StatesAndStepFunctions is { Count: > 0 } s ? (ServerState)s[0].State : null;

    // MILESTONES (sim/baseline_test.go's idea): measure how often the run
    // actually reaches the deep states, so coverage is a number, not a hope.
    private int _msAcquired, _msSuspended, _msSettledWithAwaiters, _msResumesArmed,
        _msTimeoutsWithAwaiters, _msGuided;

    public Fuzzer(Harness harness, int seed)
    {
        _harness = harness;
        _spec = harness.Spec;
        _seed = seed;
        _rng = new Random(seed);
        var p = Environment.GetEnvironmentVariable("FUZZ_CONC_PROB");
        if (p is not null && double.TryParse(p, out var pv)) _concProb = pv;
        var g = Environment.GetEnvironmentVariable("FUZZ_GUIDED");
        if (g is not null && double.TryParse(g, out var gv)) _guidedProb = gv;
    }

    private readonly double _concProb = 0.30;

    public async Task<int> Run(int ops)
    {
        Console.WriteLine($"\n########## FUZZ / STRESS ({ops} ops, seed={_seed}) ##########\n");
        _harness.Now = _now;
        await _harness.Client.DebugReset();
        _profile = SingleState(new ServerState { Now = _now });
        NewEpoch();

        for (int i = 0; i < ops && _fails == 0; i++)
        {
            // Roll to a fresh id namespace every ~40 ops (replaces settled work).
            if (i > 0 && i % 40 == 0) NewEpoch();

            // 30% concurrent burst, 70% sequential. Ticks are sequential-only:
            // a tick concurrent with another op would race the single injected
            // clock both the model and the other request's debug_time share.
            if (_rng.NextDouble() < _concProb)
                await ConcurrentBurst(i);
            else
                await SequentialStep(i);
        }

        Console.WriteLine($"\n========================================");
        Console.WriteLine($"  seq ok: {_seqOk}   concurrent ok: {_concOk}   stickiness polls ok: {_pollOk}   model FAILURES: {_fails}");
        Console.WriteLine($"  milestones: acquired {_msAcquired}  suspended {_msSuspended}  " +
            $"settled-with-awaiters {_msSettledWithAwaiters}  timeouts-with-awaiters {_msTimeoutsWithAwaiters}  " +
            $"resumes armed {_msResumesArmed}  (guided ops: {_msGuided})");
        Console.WriteLine($"  INVARIANT violations (candidate SERVER bugs): {_inv.Violations.Count}");
        foreach (var v in _inv.Violations) Console.WriteLine($"    🚨 [{v.Rule}] {v.Detail}");
        if (_inv.Violations.Count > 0)
        {
            var vids = _inv.Violations
                .SelectMany(v => System.Text.RegularExpressions.Regex.Matches(v.Detail, @"[pt]\.\d+\.\d+")
                    .Select(m => m.Value))
                .Distinct().ToList();
            Console.WriteLine($"  history touching {string.Join(",", vids)}:");
            foreach (var line in _history.Where(h => vids.Any(h.Contains)))
                Console.WriteLine($"    {line}");
        }
        Console.WriteLine($"  (reproduce with: dotnet run fuzz {_seed} {ops})");
        Console.WriteLine($"========================================");
        return _fails == 0 && _inv.Violations.Count == 0 ? 0 : 1;
    }

    // ---- sequential step: one op, checked with spec.Allows ---------------------
    private async Task SequentialStep(int i)
    {
        var before = ModelState;
        var (op, req, label) = NextOp();
        var observed = await op.ExecuteAsync(_harness.NewContext(), req);
        _history.Add($"#{i} seq {label} → {Serialize(observed)}");
        if (observed is Response sr) _inv.Observe(req, sr);
        var (ok, msg, next) = _spec.Allows(op, req, observed, _profile);
        if (!ok) { Fail(i, "seq", label, req, observed, msg); return; }
        _profile = next;
        _seqOk++;
        TrackEffect(req);
        CountMilestones(req, observed, before);
        await MaybePollTerminal(i, req);
    }

    // ---- concurrent burst: 2-4 ops fired together, via AllowsConcurrent --------
    private async Task ConcurrentBurst(int i)
    {
        // Wide bursts only from a SINGLE clean state: the linearization search is
        // exponential in (candidates × armed triggers × burst size), so when
        // in-flight resumes are pending we stay at pairs and let reads collapse
        // the profile first.
        var clean = _profile.StatesAndStepFunctions.Count == 1
                    && _profile.StatesAndStepFunctions[0].StepFunctions is not { Count: > 0 };
        int k = clean ? 2 + _rng.Next(3) : 2;
        var before = ModelState;
        var picks = new List<(IOperation op, object req, string label)>();
        for (int j = 0; j < k; j++) picks.Add(NextOp(allowTick: false));

        var tasks = picks.Select(p => p.op.ExecuteAsync(_harness.NewContext(), p.req)).ToList();
        await Task.WhenAll(tasks);
        var results = tasks.Select(t => t.Result).ToList();

        var burstLabel = "[" + string.Join(" || ", picks.Select(p => p.label)) + "]";
        _history.Add($"#{i} conc {burstLabel} → {string.Join(" || ", results.Select(Serialize))}");
        _inv.ObserveBatch(Enumerable.Range(0, k)
            .Where(j => results[j] is Response)
            .Select(j => (picks[j].req, (Response)results[j])));

        var calls = picks.Select((p, j) => (p.op, p.req, results[j])).ToList();
        var (ok, msg, next) = _spec.AllowsConcurrent(_profile, calls);
        if (!ok)
        {
            Fail(i, "concurrent", burstLabel,
                string.Join(" || ", picks.Select(p => Serialize(p.req))),
                string.Join(" || ", results.Select(Serialize)), msg);
            return;
        }
        _profile = next;
        _concOk++;
        foreach (var p in picks) TrackEffect(p.req);
        for (int j = 0; j < k; j++) CountMilestones(picks[j].req, results[j], before);
        await CollapseOneResume(i, picks[0].req);
    }

    // ---- branch collapse: observe suspended tasks while triggers are armed -----
    // Each task.get prunes profile candidates to what the server actually reports,
    // keeping the linearization search tractable (and, incidentally, WITNESSING
    // resumes land under fuzz).
    private async Task CollapseOneResume(int i, object req)
    {
        var anyArmed = _profile.StatesAndStepFunctions.Count > 1
            || _profile.StatesAndStepFunctions.Any(ssf => ssf.StepFunctions is { Count: > 0 });
        if (!anyArmed) return;

        var getOp = _spec.GetOperation("GetTask");
        var ids = _profile.StatesAndStepFunctions
            .SelectMany(ssf => ((ServerState)ssf.State).Tasks
                .Where(kv => kv.Value.State == "suspended").Select(kv => kv.Key))
            .Distinct().OrderBy(k => k, StringComparer.Ordinal).Take(2).ToList();

        foreach (var tid in ids)
        {
            var getReq = new GetTask(tid);
            var observed = await getOp.ExecuteAsync(_harness.NewContext(), getReq);
            if (observed is Response pr) _inv.Observe(getReq, pr);
            var (ok, msg, next) = _spec.Allows(getOp, getReq, observed, _profile);
            if (!ok) { Fail(i, "poll", $"collapse poll {tid}", getReq, observed, msg); return; }
            _profile = next;
            _pollOk++;
        }
    }

    // ---- milestone counting (deep-state coverage as a number) ------------------
    private void CountMilestones(object req, object resp, ServerState? before)
    {
        var r = resp as Response;
        switch (req)
        {
            case AcquireTask when r?.Status == 200:
                _msAcquired++;
                break;
            case SuspendTask when r?.Status == 200:
                _msSuspended++;
                break;
            case SettlePromise s when r?.Status == 200
                && before?.Promises.TryGetValue(s.Id, out var aw) == true && aw.Callbacks.Count > 0:
                _msSettledWithAwaiters++; _msResumesArmed += aw.Callbacks.Count;
                break;
            case FulfillTask f when r?.Status == 200
                && before?.Promises.TryGetValue(f.Id, out var aw2) == true && aw2.Callbacks.Count > 0:
                _msSettledWithAwaiters++; _msResumesArmed += aw2.Callbacks.Count;
                break;
            case AdvanceClock a when before is not null:
                foreach (var kv in before.Promises)
                    if (kv.Value.State == "pending" && a.To >= kv.Value.TimeoutAt
                        && kv.Value.Callbacks.Count > 0)
                    {
                        _msTimeoutsWithAwaiters++;
                        _msResumesArmed += kv.Value.Callbacks.Count;
                    }
                break;
        }
    }

    // ---- stickiness: poll a terminal promise a few times, expect no flip-flop --
    private async Task MaybePollTerminal(int i, object req)
    {
        // First: if this op armed in-flight resumes, poll ONE awaiter's task.get —
        // this both observes the resume landing AND collapses profile branches so
        // the linearization search stays tractable.
        await CollapseOneResume(i, req);
        if (_fails > 0) return;

        string? id = req switch
        {
            SettlePromise s => s.Id,
            FulfillTask f => f.Id,
            FenceTask ft => ft.TargetId,
            _ => null,
        };
        if (id is null || _rng.NextDouble() > 0.5) return;

        var getOp = _spec.GetOperation("GetPromise");
        int polls = 1 + _rng.Next(3);
        for (int k = 0; k < polls && _fails == 0; k++)
        {
            var getReq = new GetPromise(id);
            var observed = await getOp.ExecuteAsync(_harness.NewContext(), getReq);
            if (observed is Response pr) _inv.Observe(getReq, pr);
            var (ok, msg, next) = _spec.Allows(getOp, getReq, observed, _profile);
            if (!ok) { Fail(i, "poll", $"stickiness poll {id}", getReq, observed, msg); return; }
            _profile = next;
            _pollOk++;
        }
    }

    // ---- random op generation, referencing live ids ---------------------------
    private (IOperation op, object req, string label) NextOp(bool allowTick = true)
    {
        long far = FuzzFar;
        int roll = _rng.Next(100);

        // Sequential-only: advance the clock, firing everything due (timeouts
        // cascade: durable settle + task fulfillment + in-flight awaiter resumes).
        if (allowTick && roll < 6 && _rng.NextDouble() < 0.9)
        {
            var to = _now + 2_000 + _rng.Next(20_000);
            return (_spec.GetOperation("AdvanceClock"), new AdvanceClock(to), $"tick →{to - FuzzNow}");
        }

        // Bias toward creates early so there's state to act on. Plain creates
        // split three ways: the external tag, the timer tag (also awaitable,
        // but its deadline RESOLVES it — the arm that puts a timer under random
        // interleaving, where a deadline can land mid-burst), and neither,
        // which stays internal and feeds the not-awaitable 422 paths.
        if (roll < 18 || _plainPromises.Count + _taskPromises.Count == 0)
        {
            var id = FreshId("p");
            var tag = _rng.NextDouble();
            var external = tag < 0.4;
            var timer = !external && tag < 0.6;
            var how = external ? " (external)" : timer ? " (timer)" : "";
            return (_spec.GetOperation("CreatePromise"),
                new CreatePromise(id, NextDeadline(), "v", external, Timer: timer), $"create {id}{how}");
        }
        if (roll < 30)
        {
            var id = FreshId("t");
            return (_spec.GetOperation("CreatePromise"),
                new CreatePromise(id, NextDeadline(), "w", WithTarget: true), $"create+target {id}");
        }
        if (roll < 34)
        {
            // task.create: atomic; usually fresh id, sometimes a live one → 409/422.
            var id = _rng.NextDouble() < 0.7 ? FreshId("tc") : AnyTask();
            var withTarget = _rng.NextDouble() < 0.9;
            var req = new CreateTask(id, far, "self", withTarget);
            return (_spec.GetOperation("CreateTask"), req, $"task.create {id} tgt={withTarget}");
        }
        if (roll < 50)
        {
            // Reads: promise or task (task.get also collapses in-flight resume branches).
            if (_rng.NextDouble() < 0.35)
            {
                var tid = AnyTask();
                return (_spec.GetOperation("GetTask"), new GetTask(tid), $"task.get {tid}");
            }
            var id = AnyPromise();
            return (_spec.GetOperation("GetPromise"), new GetPromise(id), $"get {id}");
        }
        if (roll < 62)
        {
            // Guided: prefer settling a promise that HAS awaiters — the cascade.
            var id = TryGuidedPromise(p => p.State == "pending", preferAwaited: true) ?? AnyPromise();
            // Mix all valid terminal states + occasionally a bogus one (→ 400).
            var st = _rng.Next(10) switch
            {
                < 6 => "resolved",
                < 8 => "rejected",
                < 9 => "rejected_canceled",
                _ => "bogus", // invalid → exercises the 400 branch
            };
            return (_spec.GetOperation("SettlePromise"),
                new SettlePromise(id, st, "r"), $"settle {id} {st}");
        }
        if (roll < 74)
        {
            // Guided: a PENDING task at its CURRENT version — the fence satisfied.
            if (TryGuidedTask(t => t.State == "pending") is var (gid, gv) && gid is not null)
                return (_spec.GetOperation("AcquireTask"),
                    new AcquireTask(gid, gv, "w" + _rng.Next(3)), $"acquire {gid} v{gv}");
            var id = AnyTask();
            var v = _rng.Next(3); // sometimes-wrong version → exercises 409
            return (_spec.GetOperation("AcquireTask"),
                new AcquireTask(id, v, "w" + _rng.Next(3)), $"acquire {id} v{v}");
        }
        if (roll < 82)
        {
            if (TryGuidedTask(t => t.State == "acquired") is var (gid, gv) && gid is not null)
                return (_spec.GetOperation("FulfillTask"),
                    new FulfillTask(gid, gv, "resolved", "done"), $"fulfill {gid} v{gv}");
            var id = AnyTask();
            var v = _rng.Next(3);
            return (_spec.GetOperation("FulfillTask"),
                new FulfillTask(id, v, "resolved", "done"), $"fulfill {id} v{v}");
        }
        if (roll < 88)
        {
            if (TryGuidedTask(t => t.State == "acquired") is var (gid, gv) && gid is not null)
                return (_spec.GetOperation("ReleaseTask"),
                    new ReleaseTask(gid, gv), $"release {gid} v{gv}");
            var id = AnyTask();
            var v = _rng.Next(3);
            return (_spec.GetOperation("ReleaseTask"),
                new ReleaseTask(id, v), $"release {id} v{v}");
        }
        if (roll < 90)
        {
            var awaited = AnyPromise();
            var awaiter = AnyTask();
            return (_spec.GetOperation("RegisterCallback"),
                new RegisterCallback(awaited, awaiter), $"reg_cb {awaited}←{awaiter}");
        }
        if (roll < 95)
        {
            // fence: run an inner action on ANOTHER promise (target != task id).
            // The CREATE arm mints a fresh child (or dedups an existing id);
            // the SETTLE arm settles one.
            if (TryGuidedTask(t => t.State == "acquired") is var (gid, gv) && gid is not null)
            {
                if (_rng.NextDouble() < 0.4)
                {
                    var cid = _rng.NextDouble() < 0.6 ? FreshId("fk") : AnyPromise();
                    if (cid != gid)
                        return (_spec.GetOperation("FenceTask"),
                            new FenceTask(gid, gv, Create: new CreatePromise(cid, NextDeadline(), "kid")),
                            $"fence {gid} v{gv} ⇒ create {cid}");
                }
                var gchild = TryGuidedPromise(p => p.State == "pending") ?? AnyPromise();
                if (gchild != gid)
                    return (_spec.GetOperation("FenceTask"),
                        new FenceTask(gid, gv, Settle: new SettlePromise(gchild, "resolved", "fenced")),
                        $"fence {gid} v{gv} ⇒ settle {gchild}");
            }
            var id = AnyTask();
            var child = AnyPromise();
            var v = _rng.Next(3);
            // Occasionally force child==id to exercise the 400 branch.
            if (_rng.NextDouble() < 0.1) child = id;
            return (_spec.GetOperation("FenceTask"),
                new FenceTask(id, v, Settle: new SettlePromise(child, "resolved", "fenced")),
                $"fence {id} v{v} ⇒ settle {child}");
        }
        // suspend
        {
            // Guided: an ACQUIRED task at its version, awaiting a PENDING
            // EXTERNAL promise that isn't itself — the cascade's first half.
            if (TryGuidedTask(t => t.State == "acquired") is var (gid, gv) && gid is not null)
            {
                var gawaited = TryGuidedPromise(p => p.State == "pending" && p.IsExternal) ?? AnyPromise();
                if (gawaited != gid)
                {
                    // Sometimes wait-any over TWO distinct externals.
                    if (_rng.NextDouble() < 0.25
                        && TryGuidedPromise(p => p.State == "pending" && p.IsExternal) is { } second
                        && second != gawaited && second != gid)
                        gawaited = $"{gawaited},{second}";
                    return (_spec.GetOperation("SuspendTask"),
                        new SuspendTask(gid, gv, gawaited), $"suspend {gid} v{gv} awaiting {gawaited}");
                }
            }
            var id = AnyTask();
            var awaited = AnyPromise();
            var v = _rng.Next(3);
            // Occasionally malformed: empty actions or a duplicated awaited → 400.
            if (_rng.NextDouble() < 0.05) awaited = "";
            else if (_rng.NextDouble() < 0.05) awaited = $"{awaited},{awaited}";
            return (_spec.GetOperation("SuspendTask"),
                new SuspendTask(id, v, awaited), $"suspend {id} v{v} awaiting {awaited}");
        }
    }

    // ---- guided pickers: satisfy the version fence from live model state -------
    // Deterministic (ordered iteration + seeded rng). Returning null falls back
    // to the adversarial random path.
    private (string?, int) TryGuidedTask(Func<TaskState, bool> pred)
    {
        if (_rng.NextDouble() >= _guidedProb || ModelState is not { } s) return (null, 0);
        var hits = s.Tasks.Where(kv => pred(kv.Value))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (hits.Count == 0) return (null, 0);
        var id = hits[_rng.Next(hits.Count)];
        _msGuided++;
        return (id, s.Tasks[id].Version);
    }

    private string? TryGuidedPromise(Func<PromiseState, bool> pred, bool preferAwaited = false)
    {
        if (_rng.NextDouble() >= _guidedProb || ModelState is not { } s) return null;
        var pool = s.Promises.Where(kv => pred(kv.Value)).Select(kv => kv.Key);
        if (preferAwaited)
        {
            var awaited = pool.Where(id => s.Promises[id].Callbacks.Count > 0)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (awaited.Count > 0) { _msGuided++; return awaited[_rng.Next(awaited.Count)]; }
        }
        var hits = pool.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (hits.Count == 0) return null;
        _msGuided++;
        return hits[_rng.Next(hits.Count)];
    }

    // Occasionally reference a NON-existent id to exercise 404/422 paths.
    // CONTENTION (sim's tiny-pool idea): bias toward a small hot set of ids so
    // clients genuinely collide instead of spreading over the whole namespace.
    private string Pick(List<string> pool) =>
        pool.Count > 3 && _rng.NextDouble() < 0.6 ? pool[_rng.Next(3)] : pool[_rng.Next(pool.Count)];

    private string AnyPromise()
    {
        var all = _plainPromises.Concat(_taskPromises).ToList();
        if (all.Count == 0 || _rng.NextDouble() < 0.1) return $"ghost.{_epoch}.{_rng.Next(1000)}";
        return Pick(all);
    }

    private string AnyTask()
    {
        if (_taskPromises.Count == 0 || _rng.NextDouble() < 0.1) return $"ghostTask.{_epoch}.{_rng.Next(1000)}";
        return Pick(_taskPromises);
    }

    // One origin for the whole run: an await may only cross promises of the
// same call graph, and everything the fuzzer mints belongs to one.
    private string FreshId(string kind) => $"fz:{kind}.{_epoch}.{_idCounter++}";

    private void TrackEffect(object req)
    {
        switch (req)
        {
            case CreatePromise c when c.WithTarget && !_taskPromises.Contains(c.Id): _taskPromises.Add(c.Id); break;
            case CreatePromise c when !c.WithTarget && !_plainPromises.Contains(c.Id): _plainPromises.Add(c.Id); break;
            case CreateTask c when c.WithTarget && !_taskPromises.Contains(c.Id): _taskPromises.Add(c.Id); break;
            case AdvanceClock a when a.To > _now: _now = a.To; break;
        }
    }

    private void NewEpoch()
    {
        _epoch++;
        _plainPromises.Clear();
        _taskPromises.Clear();
    }

    private static StateProfile SingleState(ServerState s) =>
        SystemChecker.Validate(new List<IList<IStepFunction>>(), s, null);

    private void Fail(int i, string kind, string label, object req, object resp, string msg)
    {
        _fails++;
        Console.WriteLine($"\n  ❌ FAILURE at op #{i} ({kind}): {label}");
        Console.WriteLine($"       request:  {Serialize(req)}");
        Console.WriteLine($"       observed: {Serialize(resp)}");
        Console.WriteLine($"       reason:   {msg}");
        // Dump the history lines that mention any id in the failing request.
        var ids = System.Text.RegularExpressions.Regex.Matches(Serialize(req) + " " + label, @"[pt]\.\d+\.\d+")
            .Select(m => m.Value).Distinct().ToList();
        Console.WriteLine($"       history touching {string.Join(",", ids)}:");
        foreach (var line in _history.Where(h => ids.Any(h.Contains)))
            Console.WriteLine($"         {line}");
    }

    private static string Serialize(object o)
    {
        if (o is Response r) return $"status={r.Status} promise={r.PromiseStatus() ?? "-"} task={r.TaskStatus() ?? "-"}";
        if (o is ValueTuple<object, object> pair) return $"{Serialize(pair.Item1)}  ||  {Serialize(pair.Item2)}";
        return o.ToString() ?? "?";
    }
}
