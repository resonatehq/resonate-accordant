using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// The lockstep reorder test (sim/conformance_test.go's idea): generate a
/// cascade-rich GUIDED sequence, SHUFFLE it (reaching the reordered deep states
/// concurrency produces), then replay it SEQUENTIALLY against the live server,
/// spec-checking every response with spec.Allows. Sequential + lockstep means
/// any failure is a definitive model-vs-server conformance gap with the exact
/// op and state as witness — cleanly separated from concurrency artifacts.
/// </summary>
public static class ReorderTests
{
    public static async Task<int> Run(Harness harness, int seeds = 100, int len = 30)
    {
        Console.WriteLine($"\n########## REORDER LOCKSTEP ({seeds} seeds × {len} ops) ##########\n");
        var spec = harness.Spec;
        int failed = 0;

        for (int seed = 1; seed <= seeds && failed == 0; seed++)
        {
            var ops = GenerateGuided(len, seed);
            var rng = new Random(seed * 7);
            // Fisher–Yates shuffle: the reordering is the whole point.
            for (int i = ops.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (ops[i], ops[j]) = (ops[j], ops[i]);
            }

            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            var profile = SystemChecker.Validate(new List<IList<IStepFunction>>(),
                new ServerState { Now = harness.Now }, null);

            for (int i = 0; i < ops.Count; i++)
            {
                var (opName, req, label) = ops[i];
                var op = spec.GetOperation(opName);
                var observed = await op.ExecuteAsync(harness.NewContext(), req);
                var (ok, msg, next) = spec.Allows(op, req, observed, profile);
                if (!ok)
                {
                    failed++;
                    Console.WriteLine($"  ❌ seed {seed} op #{i} ({label}): {msg}");
                    break;
                }
                profile = next;
            }
        }

        Console.WriteLine($"\nreorder lockstep: {(failed == 0 ? $"{seeds}/{seeds} shuffled sequences conform ✅" : $"{failed} DIVERGENCE(S) ❌")}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Generate a guided, cascade-rich op sequence against a tiny hand-rolled
    /// shadow state (generation only — correctness stays with the checker at
    /// replay time). ~85% of ops advance the cascade with satisfied fences;
    /// the rest stay adversarial-random.
    /// </summary>
    internal static List<(string op, object req, string label)> GenerateGuided(int len, int seed)
    {
        var rng = new Random(seed);
        long far = 9_000_000_000_000;
        string[] promises = ["rd:A", "rd:B", "rd:C"];
        string[] tasks = ["rd:TA", "rd:TB"];

        var pState = new Dictionary<string, string>();          // promise → pending|settled
        var tState = new Dictionary<string, (string st, int v)>(); // task → (state, version)
        var awaits = new Dictionary<string, List<string>>();     // promise → suspended awaiters

        var ops = new List<(string, object, string)>();
        while (ops.Count < len)
        {
            bool guided = rng.NextDouble() < 0.85;
            int roll = rng.Next(6);
            switch (roll)
            {
                case 0: // create a promise (external: the awaited pool must be awaitable)
                {
                    var id = promises[rng.Next(promises.Length)];
                    ops.Add(("CreatePromise", new CreatePromise(id, far, "v", External: true), $"create {id} (external)"));
                    pState.TryAdd(id, "pending");
                    break;
                }
                case 1: // create a task-backed promise
                {
                    var id = tasks[rng.Next(tasks.Length)];
                    ops.Add(("CreatePromise", new CreatePromise(id, far, "w", WithTarget: true), $"create+target {id}"));
                    if (tState.TryAdd(id, ("pending", 0))) pState.TryAdd(id, "pending");
                    break;
                }
                case 2: // acquire
                {
                    var live = tState.Where(kv => kv.Value.st == "pending").Select(kv => kv.Key).ToList();
                    if (guided && live.Count > 0)
                    {
                        var id = live[rng.Next(live.Count)];
                        var v = tState[id].v;
                        ops.Add(("AcquireTask", new AcquireTask(id, v, "w1"), $"acquire {id} v{v}"));
                        tState[id] = ("acquired", v + 1);
                    }
                    else
                    {
                        var id = tasks[rng.Next(tasks.Length)];
                        var v = rng.Next(3);
                        ops.Add(("AcquireTask", new AcquireTask(id, v, "w1"), $"acquire {id} v{v} (random)"));
                    }
                    break;
                }
                case 3: // suspend an acquired task awaiting a pending promise
                {
                    var acq = tState.Where(kv => kv.Value.st == "acquired").Select(kv => kv.Key).ToList();
                    var pend = pState.Where(kv => kv.Value == "pending").Select(kv => kv.Key)
                        .Where(p => !tasks.Contains(p)).ToList();
                    if (guided && acq.Count > 0 && pend.Count > 0)
                    {
                        var id = acq[rng.Next(acq.Count)];
                        var awaited = pend[rng.Next(pend.Count)];
                        var v = tState[id].v;
                        ops.Add(("SuspendTask", new SuspendTask(id, v, awaited), $"suspend {id} v{v} awaiting {awaited}"));
                        tState[id] = ("suspended", v);
                        (awaits.TryGetValue(awaited, out var l) ? l : awaits[awaited] = []).Add(id);
                    }
                    else
                    {
                        var id = tasks[rng.Next(tasks.Length)];
                        ops.Add(("SuspendTask", new SuspendTask(id, rng.Next(3), promises[rng.Next(promises.Length)]),
                            $"suspend {id} (random)"));
                    }
                    break;
                }
                case 4: // settle — prefer a promise with awaiters (the cascade)
                {
                    var awaited = awaits.Where(kv => kv.Value.Count > 0 && pState.GetValueOrDefault(kv.Key) == "pending")
                        .Select(kv => kv.Key).ToList();
                    var pend = pState.Where(kv => kv.Value == "pending" && !tasks.Contains(kv.Key))
                        .Select(kv => kv.Key).ToList();
                    var id = guided && awaited.Count > 0 ? awaited[rng.Next(awaited.Count)]
                        : pend.Count > 0 ? pend[rng.Next(pend.Count)]
                        : promises[rng.Next(promises.Length)];
                    ops.Add(("SettlePromise", new SettlePromise(id, "resolved", "r"), $"settle {id}"));
                    if (pState.ContainsKey(id)) pState[id] = "settled";
                    if (awaits.TryGetValue(id, out var aw))
                    {
                        foreach (var t in aw)
                            if (tState.TryGetValue(t, out var ts) && ts.st == "suspended")
                                tState[t] = ("pending", ts.v); // resume lands (simulation)
                        aw.Clear();
                    }
                    break;
                }
                default: // observe
                {
                    var id = tasks[rng.Next(tasks.Length)];
                    ops.Add(("GetTask", new GetTask(id), $"task.get {id}"));
                    break;
                }
            }
        }
        return ops;
    }
}
