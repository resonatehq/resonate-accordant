namespace ResonateConformance;

public sealed class Invariants
{
    public sealed record Violation(string Rule, string Detail);

    private readonly List<Violation> _violations = [];

    private readonly Dictionary<string, (string State, string? Value)> _terminal = new();

    private readonly Dictionary<(string, int), int> _acquireWinners = new();

    private readonly Dictionary<string, int> _maxVersion = new();

    private Dictionary<string, int>? _batchPreMax;

    public IReadOnlyList<Violation> Violations => _violations;

    public void Observe(object req, Response resp)
    {
        var promise = resp.PromiseRecord();
        var task = resp.TaskRecord();

        if (promise is not null && IsTerminal(promise.State))
        {
            var seen = (promise.State, promise.ValueField?.AsString());
            if (_terminal.TryGetValue(promise.Id, out var prev))
            {
                if (prev != seen)
                    Flag("terminal-once", $"promise {promise.Id} was {prev}, now {seen} — terminal state/value changed");
            }
            else _terminal[promise.Id] = seen;
        }

        if (req is AcquireTask acq && resp.Status == 200 && task?.State == "acquired")
        {
            var key = (acq.Id, acq.Version);
            _acquireWinners[key] = _acquireWinners.GetValueOrDefault(key) + 1;
            if (_acquireWinners[key] > 1)
                Flag("one-winner", $"task {acq.Id} version {acq.Version} acquired {_acquireWinners[key]} times — multiple winners");
        }

        if (req is FulfillTask f && resp.Status == 200 && promise is not null)
        {
            if (_terminal.TryGetValue(f.Id, out var prev) && prev.Value is not null
                && prev.Value != f.Data)
                Flag("no-double-side-effect",
                    $"fulfill {f.Id} returned 200 and changed terminal value {prev.Value} → {f.Data}");
        }

        if (task is not null)
        {
            var floor = _batchPreMax ?? _maxVersion;
            if (floor.TryGetValue(task.Id, out var mx) && task.Version < mx)
                Flag("monotonic-version",
                    $"task {task.Id} version went {mx} → {task.Version} (decreased) on {req}");
            _maxVersion[task.Id] = Math.Max(_maxVersion.GetValueOrDefault(task.Id), task.Version);
        }
    }

    public void ObserveBatch(IEnumerable<(object Req, Response Resp)> pairs)
    {
        _batchPreMax = new Dictionary<string, int>(_maxVersion);
        try
        {
            foreach (var (req, resp) in pairs) Observe(req, resp);
        }
        finally
        {
            _batchPreMax = null;
        }
    }

    private void Flag(string rule, string detail)
    {
        var v = new Violation(rule, detail);
        _violations.Add(v);
        Console.WriteLine($"  🚨 INVARIANT VIOLATION [{rule}] (candidate SERVER bug): {detail}");
    }

    private static bool IsTerminal(string s) =>
        s is "resolved" or "rejected" or "rejected_canceled" or "rejected_timedout";

    public static int SelfTest()
    {
        Console.WriteLine("\n########## INVARIANT SELF-TEST (fabricated bad histories) ##########\n");
        int fails = 0;

        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "99"));
            fails += Expect("terminal-once fires on value change", inv, "terminal-once");
        }

        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "x"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "rejected", "x"));
            fails += Expect("terminal-once fires on state flip", inv, "terminal-once");
        }

        {
            var inv = new Invariants();
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 1));
            inv.Observe(new AcquireTask("t", 0, "b"), FakeTask("t", "acquired", 1));
            fails += Expect("one-winner fires on double-acquire", inv, "one-winner");
        }

        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("t"), Fake.Promise("t", "resolved", "orig"));
            inv.Observe(new FulfillTask("t", 1, "resolved", "hijack"), Fake.Promise("t", "resolved", "hijack"));
            fails += Expect("no-double-side-effect fires on re-settle", inv, "no-double-side-effect");
        }

        {
            var inv = new Invariants();
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 5));
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 2));
            fails += Expect("monotonic-version fires on decrease", inv, "monotonic-version");
        }

        {
            var inv = new Invariants();
            inv.ObserveBatch([
                ((object)new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 1)),
                (new GetTask("t"), FakeTask("t", "pending", 0)),
            ]);
            if (inv.Violations.Count != 0) { fails++; Console.WriteLine("  ❌ intra-batch stale read wrongly flagged"); }
            else Console.WriteLine("  ✅ intra-batch stale read tolerated");

            inv.ObserveBatch([((object)new GetTask("t"), FakeTask("t", "pending", 0))]);
            fails += Expect("monotonic-version fires on cross-batch decrease", inv, "monotonic-version");
        }

        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "pending", null));
            inv.Observe(new SettlePromise("p", "resolved", "42"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 1));
            if (inv.Violations.Count != 0) { fails++; Console.WriteLine("  ❌ legal history wrongly flagged"); }
            else Console.WriteLine("  ✅ legal history produced no violations");
        }

        fails += RetryClockSelfTest();

        Console.WriteLine($"\n  self-test: {(fails == 0 ? "all rules fire correctly ✅" : $"{fails} FAILED ❌")}");
        return fails == 0 ? 0 : 1;
    }

    private static int RetryClockSelfTest()
    {
        Console.WriteLine("\n  --- dispatch clock (model-only: nothing observes it) ---");
        int fails = 0;
        const long now = 1_000_000, far = 9_000_000_000_000;

        int Pairing(ServerState s, string label)
        {
            foreach (var (id, t) in s.Tasks)
            {
                var armed = t.RetryTimeoutAt is not null;
                if (t.State == "pending" != armed)
                {
                    Console.WriteLine($"  ❌ {label}: task {id} is {t.State} with dispatch {(armed ? "armed" : "unarmed")}");
                    return 1;
                }
            }
            Console.WriteLine($"  ✅ {label}");
            return 0;
        }

        ServerState Step<TReq>(ServerState s, string op, TReq req)
        {
            var outcome = ResonateSpec.Build().GetOperation<TReq, Response>(op).Apply(req, s);
            var next = outcome.PossibleOutcomes[0].NextStateGenerator(null!, s).First();
            return (ServerState)next;
        }

        var st = new ServerState { Now = now };
        st = Step(st, "CreatePromise", new CreatePromise("r", far, "w", WithTarget: true));
        fails += Pairing(st, "born with a target → pending, dispatch armed");
        st = Step(st, "AcquireTask", new AcquireTask("r", 0, "w1"));
        fails += Pairing(st, "acquired → the lease takes over, dispatch cleared");
        st = Step(st, "ReleaseTask", new ReleaseTask("r", 1));
        fails += Pairing(st, "released → pending again, dispatch re-armed");
        st = Step(st, "AcquireTask", new AcquireTask("r", 1, "w1"));
        st = Step(st, "SuspendTask", new SuspendTask("r", 2, "a"));
        fails += Pairing(st, "suspended → parked, neither clock runs");

        var leased = new ServerState { Now = now };
        leased = Step(leased, "CreatePromise", new CreatePromise("L", far, "w", WithTarget: true));
        leased = Step(leased, "AcquireTask", new AcquireTask("L", 0, "w1", 5_000));
        leased.Now = now + 6_000;
        ResonateSpec.ReclaimLease(leased, "L");
        var lt = leased.Tasks["L"];
        if (lt.State == "pending" && lt.RetryTimeoutAt is not null)
            Console.WriteLine("  ✅ lease expiry → pending, dispatch armed");
        else { Console.WriteLine($"  ❌ lease expiry left {lt.State} / {lt.RetryTimeoutAt?.ToString() ?? "unarmed"}"); fails++; }

        var due = leased.Tasks["L"].RetryTimeoutAt!.Value;
        leased.Now = due + 1;
        ResonateSpec.FoldRetryTimeouts(leased);
        var after = leased.Tasks["L"].RetryTimeoutAt;
        if (after > due)
            Console.WriteLine($"  ✅ dispatch due → re-armed a dial out ({due} → {after})");
        else { Console.WriteLine($"  ❌ dispatch not re-armed: still {after?.ToString() ?? "unarmed"}"); fails++; }

        return fails;
    }

    private static int Expect(string label, Invariants inv, string rule)
    {
        var hit = inv.Violations.Any(v => v.Rule == rule);
        Console.WriteLine($"  {(hit ? "✅" : "❌")} {label}");
        return hit ? 0 : 1;
    }

    private static Response FakeTask(string id, string state, int version)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            task = new { id, state, version, resumes = 0 },
            promise = new { id, state = "pending", param = new { headers = new { }, data = "" },
                value = new { headers = new { }, data = "" }, tags = new { }, timeoutAt = 0L, createdAt = 0L },
        });
        return new Response("task.acquire", new ResponseHead("x", 200, Protocol.Version), json);
    }
}
