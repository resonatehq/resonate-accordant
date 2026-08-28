using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Free-form multi-client linearizability (sim's Porcupine idea, over the
/// Accordant model): C clients fire contended ops with NO barriers, recording
/// call/return intervals on a logical clock. Offline, Accordant searches for a
/// sequential order that the spec allows, constrained by the recorded intervals
/// as happens-before edges — threading StateProfiles, so the model's
/// nondeterminism (in-flight resumes, projections) is handled natively, which a
/// deterministic-model checker like Porcupine cannot do.
///
/// Honesty rules ported from sim/checker.go: the search runs under a wall-clock
/// budget; exhausting it yields UNKNOWN (inconclusive), never a failure. A
/// negative control (one corrupted response) proves the checker discriminates.
/// </summary>
public static class HistoryTests
{
    private sealed record HistOp(int Client, string OpName, object Req, string Label,
        object Resp, long Call, long Return);

    public static async Task<int> Run(Harness harness, int rounds = 10, int clients = 3, int perClient = 8)
    {
        Console.WriteLine($"\n########## HISTORY LINEARIZABILITY ({rounds} rounds × {clients} clients × {perClient} ops) ##########\n");
        var spec = harness.Spec;
        int violations = 0, unknown = 0, linearizable = 0;

        for (int round = 1; round <= rounds; round++)
        {
            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            var (hist, initial) = await Record(harness, round, clients, perClient);
            var (ok, conclusive) = CheckLinearizable(spec, hist, initial, TimeSpan.FromSeconds(20));
            if (!conclusive) { unknown++; Console.WriteLine($"  ⏱ round {round}: UNKNOWN (budget exhausted)"); }
            else if (ok) linearizable++;
            else
            {
                violations++;
                Console.WriteLine($"  ❌ round {round}: NOT linearizable ({hist.Count} ops):");
                foreach (var h in hist)
                    Console.WriteLine($"       c{h.Client} [{h.Call},{h.Return}] {h.Label} → {Status(h.Resp)}");
            }
        }

        // Negative control: corrupt one successful response; the checker must reject.
        {
            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            var (hist, initial) = await Record(harness, seed: 424242, clients, perClient);
            var idx = hist.FindIndex(h => Status(h.Resp) == 200);
            bool controlOk = false;
            if (idx >= 0)
            {
                var corrupted = new List<HistOp>(hist)
                {
                    [idx] = hist[idx] with
                    {
                        Resp = new Response(((Response)hist[idx].Resp).Kind,
                            new ResponseHead("corrupt", 409, Protocol.Version),
                            System.Text.Json.JsonSerializer.SerializeToElement("corrupted")),
                    },
                };
                var (ok, conclusive) = CheckLinearizable(spec, corrupted, initial, TimeSpan.FromSeconds(20));
                controlOk = conclusive && !ok;
            }
            Console.WriteLine(controlOk
                ? "  ✅ negative control: corrupted history correctly rejected"
                : "  ❌ negative control: corrupted history NOT rejected");
            if (!controlOk) violations++;
        }

        Console.WriteLine($"\nhistory linearizability: {linearizable} linearizable, {violations} violations, {unknown} unknown");
        return violations == 0 ? 0 : 1;
    }

    // ---- recording: C clients, no barriers, logical call/return stamps ---------
    // Returns the history AND the post-seed profile: the seeds run sequentially
    // and are threaded through spec.Allows, so the search starts from the state
    // the concurrent clients actually saw.
    private static async Task<(List<HistOp>, StateProfile)> Record(
        Harness harness, int seed, int clients, int perClient)
    {
        long clock = 0;
        var results = new System.Collections.Concurrent.ConcurrentBag<HistOp>();
        var spec = harness.Spec;

        // Seed contended state sequentially: two plain promises + two task-backed.
        var profile = SystemChecker.Validate(new List<IList<IStepFunction>>(),
            new ServerState { Now = harness.Now }, null);
        foreach (var (opName, req) in Seed())
        {
            var op = spec.GetOperation(opName);
            var observed = await op.ExecuteAsync(harness.NewContext(), req);
            var (ok, msg, next) = spec.Allows(op, req, observed, profile);
            if (!ok) throw new InvalidOperationException($"history seed rejected: {msg}");
            profile = next;
        }

        var tasks = Enumerable.Range(0, clients).Select(c => Task.Run(async () =>
        {
            var rng = new Random(seed * 100 + c);
            for (int k = 0; k < perClient; k++)
            {
                var (opName, req, label) = NextOp(rng);
                var op = spec.GetOperation(opName);
                var call = Interlocked.Increment(ref clock);
                var resp = await op.ExecuteAsync(harness.NewContext(), req);
                var ret = Interlocked.Increment(ref clock);
                results.Add(new HistOp(c, opName, req, label, resp, call, ret));
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        return (results.OrderBy(h => h.Call).ToList(), profile);

        static IEnumerable<(string, object)> Seed() =>
        [
            // A/B external so awaits on them are legal (internal → 422).
            ("CreatePromise", new CreatePromise("hx:A", 9_000_000_000_000, "a", External: true)),
            ("CreatePromise", new CreatePromise("hx:B", 9_000_000_000_000, "b", External: true)),
            ("CreatePromise", new CreatePromise("hx:TA", 9_000_000_000_000, "w", WithTarget: true)),
            ("CreatePromise", new CreatePromise("hx:TB", 9_000_000_000_000, "w", WithTarget: true)),
        ];

        static (string, object, string) NextOp(Random rng)
        {
            string[] proms = ["hx:A", "hx:B"];
            string[] tsks = ["hx:TA", "hx:TB"];
            var p = proms[rng.Next(2)];
            var t = tsks[rng.Next(2)];
            var v = rng.Next(3);
            return rng.Next(7) switch
            {
                0 => ("AcquireTask", new AcquireTask(t, v, "w" + rng.Next(2)), $"acquire {t} v{v}"),
                1 => ("SettlePromise", new SettlePromise(p, "resolved", "r"), $"settle {p}"),
                2 => ("FulfillTask", new FulfillTask(t, v, "resolved", "d"), $"fulfill {t} v{v}"),
                3 => ("ReleaseTask", new ReleaseTask(t, v), $"release {t} v{v}"),
                4 => ("SuspendTask", new SuspendTask(t, v, p), $"suspend {t} v{v} awaiting {p}"),
                5 => ("GetTask", new GetTask(t), $"task.get {t}"),
                _ => ("GetPromise", new GetPromise(p), $"get {p}"),
            };
        }
    }

    // ---- linearizability, via Accordant's happens-before AllowsConcurrent ------
    // The WHOLE history goes to the checker as one concurrent batch, with
    // real-time precedence expressed as happens-before edges: op i must be
    // linearized before op j exactly when i RETURNED before j was CALLED.
    // Overlapping ops carry no edge and stay freely reorderable. Accordant
    // searches the interleavings itself — gating each step on its predecessors
    // and interning state-graph nodes — which replaces the hand-rolled DFS and
    // its deliberately-imperfect profile hash this used to carry.
    //
    // Accordant's search has no budget of its own, so sim/checker.go's honesty
    // rule stays here: run it on a worker and report UNKNOWN when the budget
    // expires, never a failure. An abandoned search keeps running to completion
    // (it cannot be cancelled) but writes nothing this code reads.
    private static (bool ok, bool conclusive) CheckLinearizable(
        Spec<ServerState> spec, List<HistOp> hist, StateProfile initial, TimeSpan budget)
    {
        var calls = hist
            .Select(h => (spec.GetOperation(h.OpName), h.Req, h.Resp))
            .ToList();

        // Disjoint intervals are ordered; overlapping ones are not. Call/return
        // stamps come from one Interlocked counter, so every stamp is distinct
        // and the relation is a strict partial order (acyclic by construction).
        var edges = new List<(int before, int after)>();
        for (int i = 0; i < hist.Count; i++)
            for (int j = 0; j < hist.Count; j++)
                if (i != j && hist[i].Return < hist[j].Call)
                    edges.Add((i, j));

        var search = Task.Run(() => spec.AllowsConcurrent(initial, calls, edges));
        if (!search.Wait(budget)) return (ok: false, conclusive: false);
        return (search.Result.IsValid, true);
    }

    private static int Status(object o) => (o as Response)?.Status ?? -1;
}
