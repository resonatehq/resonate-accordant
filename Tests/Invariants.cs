
namespace ResonateConformance;

/// <summary>
/// SERVER-BUG DETECTOR — independent of the model's op-logic.
///
/// These are PRESCRIPTIVE safety invariants that must hold no matter what the
/// spec says. They observe the raw stream of (request, response) pairs and
/// remember facts across the whole run. A violation here is a CANDIDATE SERVER
/// BUG, not a model gap — the whole point is that it can't be quietly "fixed" by
/// editing an operation's expected outcome, because this checker never looks at
/// the model.
///
/// Deliberately conservative: only fires when the server itself is internally
/// inconsistent (a terminal value changed, two winners for one version, a
/// version went backwards, a settled promise got re-settled to a new value).
/// </summary>
public sealed class Invariants
{
    public sealed record Violation(string Rule, string Detail);

    private readonly List<Violation> _violations = [];

    // Last observed terminal (state,value) per promise id — must never change.
    private readonly Dictionary<string, (string State, string? Value)> _terminal = new();
    // For each (taskId, version) presented to acquire: how many 200s were seen.
    private readonly Dictionary<(string, int), int> _acquireWinners = new();
    // Highest task version ever observed per task id — must be monotonic.
    private readonly Dictionary<string, int> _maxVersion = new();
    // Non-null while observing a CONCURRENT batch: the pre-batch max snapshot.
    // Ops inside one batch are unordered, so a version may legally read below
    // a sibling's write — only a read below the PRE-batch max is a regression.
    private Dictionary<string, int>? _batchPreMax;

    public IReadOnlyList<Violation> Violations => _violations;

    /// <summary>
    /// Feed one observed op. `req` and `resp` are the real request/response;
    /// nothing here consults the model. Call for every op the fuzzer runs.
    /// </summary>
    public void Observe(object req, Response resp)
    {
        var promise = resp.PromiseRecord();
        var task = resp.TaskRecord();

        // --- (1) terminal-once / value-immutable -------------------------------
        // Any observation of a promise in a terminal state pins (state,value).
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

        // --- (2) one-winner-per-version ---------------------------------------
        // At most one acquire(id, version) may return 200 acquired.
        if (req is AcquireTask acq && resp.Status == 200 && task?.State == "acquired")
        {
            var key = (acq.Id, acq.Version);
            _acquireWinners[key] = _acquireWinners.GetValueOrDefault(key) + 1;
            if (_acquireWinners[key] > 1)
                Flag("one-winner", $"task {acq.Id} version {acq.Version} acquired {_acquireWinners[key]} times — multiple winners");
        }

        // --- (3) no-double-side-effect (zombie fence) -------------------------
        // A fulfill/fence that returns 200 must not re-settle an ALREADY-terminal
        // promise to a DIFFERENT value.
        if (req is FulfillTask f && resp.Status == 200 && promise is not null)
        {
            if (_terminal.TryGetValue(f.Id, out var prev) && prev.Value is not null
                && prev.Value != f.Data)
                Flag("no-double-side-effect",
                    $"fulfill {f.Id} returned 200 and changed terminal value {prev.Value} → {f.Data}");
        }

        // --- (4) monotonic task version ---------------------------------------
        if (task is not null)
        {
            var floor = _batchPreMax ?? _maxVersion;
            if (floor.TryGetValue(task.Id, out var mx) && task.Version < mx)
                Flag("monotonic-version",
                    $"task {task.Id} version went {mx} → {task.Version} (decreased) on {req}");
            _maxVersion[task.Id] = Math.Max(_maxVersion.GetValueOrDefault(task.Id), task.Version);
        }
    }

    /// <summary>
    /// Feed one CONCURRENT batch of observed ops. The results of a batch are
    /// unordered (any linearization is legal), so the monotonic-version floor
    /// is the max observed strictly BEFORE the batch — a sibling's version
    /// bump must not turn a concurrent read of the older version into a
    /// violation.
    /// </summary>
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

    /// <summary>
    /// Self-test: feed FABRICATED bad histories and assert each rule fires. Proves
    /// the detector isn't a no-op (so a green real run actually means something).
    /// </summary>
    public static int SelfTest()
    {
        Console.WriteLine("\n########## INVARIANT SELF-TEST (fabricated bad histories) ##########\n");
        int fails = 0;

        // (1) terminal-once: same promise resolves to two different values.
        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "99"));
            fails += Expect("terminal-once fires on value change", inv, "terminal-once");
        }
        // terminal-once: state flips (resolved → rejected).
        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "x"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "rejected", "x"));
            fails += Expect("terminal-once fires on state flip", inv, "terminal-once");
        }
        // (2) one-winner: two acquires of the same version both 200.
        {
            var inv = new Invariants();
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 1));
            inv.Observe(new AcquireTask("t", 0, "b"), FakeTask("t", "acquired", 1));
            fails += Expect("one-winner fires on double-acquire", inv, "one-winner");
        }
        // (3) no-double-side-effect: fulfill re-settles a terminal promise to a new value.
        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("t"), Fake.Promise("t", "resolved", "orig"));
            inv.Observe(new FulfillTask("t", 1, "resolved", "hijack"), Fake.Promise("t", "resolved", "hijack"));
            fails += Expect("no-double-side-effect fires on re-settle", inv, "no-double-side-effect");
        }
        // (4) monotonic-version: version decreases.
        {
            var inv = new Invariants();
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 5));
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 2));
            fails += Expect("monotonic-version fires on decrease", inv, "monotonic-version");
        }
        // (4b) monotonic-version is BATCH-tolerant: inside one concurrent batch
        // an acquire's bump and a stale read are unordered — no violation; the
        // same stale read in a LATER batch is a real regression.
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
        // NEGATIVE: a legal history must produce NO violations.
        {
            var inv = new Invariants();
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "pending", null));
            inv.Observe(new SettlePromise("p", "resolved", "42"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new GetPromise("p"), Fake.Promise("p", "resolved", "42"));
            inv.Observe(new AcquireTask("t", 0, "a"), FakeTask("t", "acquired", 1));
            if (inv.Violations.Count != 0) { fails++; Console.WriteLine("  ❌ legal history wrongly flagged"); }
            else Console.WriteLine("  ✅ legal history produced no violations");
        }

        Console.WriteLine($"\n  self-test: {(fails == 0 ? "all rules fire correctly ✅" : $"{fails} FAILED ❌")}");
        return fails == 0 ? 0 : 1;
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
