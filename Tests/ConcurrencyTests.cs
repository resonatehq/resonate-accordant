using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Concurrency conformance. Two complementary approaches:
///  (A) HAND-CRAFTED RACES: fire two ops concurrently at the live server, collect
///      both observed responses, and ask spec.AllowsConcurrent whether SOME
///      sequential ordering explains both. This is where the version fence earns
///      its keep — exactly one acquire wins (200), the other loses (409), and the
///      model must admit that interleaving.
///  (B) GENERATED: GenerateConcurrentTests + RunTests explore interleavings.
/// </summary>
public static class ConcurrencyTests
{
    public static async Task<int> Run(Harness harness)
    {
        Console.WriteLine("\n########## CONCURRENCY TESTS ##########\n");
        int rc = 0;
        rc |= await HandCraftedRaces(harness);
        rc |= await Generated(harness);
        return rc;
    }

    // ---- (A) hand-crafted races via spec.AllowsConcurrent ----------------------
    private static async Task<int> HandCraftedRaces(Harness harness)
    {
        Console.WriteLine("--- hand-crafted races (spec.AllowsConcurrent) ---");
        var spec = harness.Spec;
        int fails = 0;

        // RACE 1: two workers acquire the SAME freshly-created task at v0.
        // Exactly one must win (200 acquired v1), the other must lose (409).
        {
            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            long far = harness.Now + 1_000_000;
            await harness.Client.PromiseCreate("race1", far, Value.Of("w"), Harness.TargetTags);

            var acqOp = spec.GetOperation("AcquireTask");
            var reqA = new AcquireTask("race1", 0, "wA");
            var reqB = new AcquireTask("race1", 0, "wB");

            // Fire both concurrently.
            var tA = acqOp.ExecuteAsync(harness.NewContext(), reqA);
            var tB = acqOp.ExecuteAsync(harness.NewContext(), reqB);
            await Task.WhenAll(tA, tB);
            var respA = await tA; var respB = await tB;

            var sA = ((Response)respA).Status;
            var sB = ((Response)respB).Status;
            Console.WriteLine($"  race1 acquire results: wA={sA} wB={sB} (expect one 200, one 409)");

            var profile = MakeProfile(new ServerState
            {
                Now = harness.Now,
                Promises =
                {
                    ["race1"] = new PromiseState
                    {
                        Status = "pending", TimeoutAt = far,
                        ParamData = "w", HasTarget = true, CreatedAt = harness.Now,
                    },
                },
                Tasks = { ["race1"] = new TaskState { Status = "pending", Version = 0 } },
            });

            var calls = new List<(IOperation, object, object)>
            {
                (acqOp, reqA, respA),
                (acqOp, reqB, respB),
            };
            var (ok, msg, _) = spec.AllowsConcurrent(profile, calls);
            Report("race1: concurrent double-acquire is linearizable", ok, msg, ref fails);

            // Sanity: exactly one winner.
            var oneWinner = (sA == 200) ^ (sB == 200);
            Report("race1: exactly one acquire won (one-winner)", oneWinner, "both or neither won", ref fails);
        }

        // RACE 2: acquire vs get. Whatever order, get sees pending or acquired-promise
        // (promise stays pending through acquire), acquire sees 200. Always linearizable.
        {
            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            long far = harness.Now + 1_000_000;
            await harness.Client.PromiseCreate("race2", far, Value.Of("w"), Harness.TargetTags);

            var acqOp = spec.GetOperation("AcquireTask");
            var getOp = spec.GetOperation("GetPromise");
            var acqReq = new AcquireTask("race2", 0, "wA");
            var getReq = new GetPromise("race2");

            var tAcq = acqOp.ExecuteAsync(harness.NewContext(), acqReq);
            var tGet = getOp.ExecuteAsync(harness.NewContext(), getReq);
            await Task.WhenAll(tAcq, tGet);
            var acqResp = await tAcq; var getResp = await tGet;
            Console.WriteLine($"  race2: acquire={((Response)acqResp).Status} get={((Response)getResp).PromiseStatus()}");

            var profile = MakeProfile(new ServerState
            {
                Now = harness.Now,
                Promises =
                {
                    ["race2"] = new PromiseState
                    {
                        Status = "pending", TimeoutAt = far,
                        ParamData = "w", HasTarget = true, CreatedAt = harness.Now,
                    },
                },
                Tasks = { ["race2"] = new TaskState { Status = "pending", Version = 0 } },
            });
            var calls = new List<(IOperation, object, object)>
            {
                (acqOp, acqReq, acqResp),
                (getOp, getReq, getResp),
            };
            var (ok, msg, _) = spec.AllowsConcurrent(profile, calls);
            Report("race2: acquire || get is linearizable", ok, msg, ref fails);
        }

        Console.WriteLine($"  hand-crafted races: {(fails == 0 ? "all linearizable ✅" : $"{fails} failed ❌")}");
        return fails == 0 ? 0 : 1;
    }

    // ---- (B) generated concurrent cases ---------------------------------------
    private static async Task<int> Generated(Harness harness)
    {
        Console.WriteLine("\n--- generated concurrent cases (GenerateConcurrentTests + RunTests) ---");
        var spec = harness.Spec;
        long far = harness.Now + 1_000_000;

        var create = spec.GetOperation<CreatePromise, Response>("CreatePromise");
        var acquire = spec.GetOperation<AcquireTask, Response>("AcquireTask");
        var get = spec.GetOperation<GetPromise, Response>("GetPromise");

        // A small pool: create a task, then two racing acquires + a read.
        var inputs = new InputSet
        {
            create.With(new CreatePromise("tk", far, "work", WithTarget: true), "create tk +target"),
            acquire.With(new AcquireTask("tk", 0, "wA"), "acquire tk wA"),
            acquire.With(new AcquireTask("tk", 0, "wB"), "acquire tk wB"),
            get.With(new GetPromise("tk"), "get tk"),
        };

        var initial = harness.InitialState();
        var cases = spec.GenerateConcurrentTests(initial, inputs,
            new TestGenerationOptions { MaxDepth = 3, MaxConcurrencyLevel = 2 });
        Console.WriteLine($"generated {cases.Count} concurrent test cases\n");

        var ctx = harness.NewContext();
        var options = new TestExecutionOptions { StopOnFirstFailure = false }
            .WithBeforeEachAsync(async _ =>
            {
                harness.Now = 1_000_000;
                await harness.Client.DebugReset();
            });

        var results = await spec.RunTests(ctx, initial, cases, options);
        int passed = results.Count(r => r.Success);
        int failed = results.Count - passed;
        Console.WriteLine($"\nconcurrent generated: {passed}/{results.Count} passed, {failed} failed");
        foreach (var r in results.Where(r => !r.Success))
            Console.WriteLine($"  ❌ {r.LastFailureMessage}");
        return failed == 0 ? 0 : 1;
    }

    private static StateProfile MakeProfile(ServerState s)
    {
        // Build a single-state StateProfile by validating an empty concurrent step
        // set from the given state (SystemChecker returns a profile wrapping it).
        return SystemChecker.Validate(new List<IList<IStepFunction>>(), s, null);
    }

    private static void Report(string label, bool ok, string msg, ref int fails)
    {
        if (ok) Console.WriteLine($"  ✅ {label}");
        else { fails++; Console.WriteLine($"  ❌ {label}: {msg}"); }
    }
}
