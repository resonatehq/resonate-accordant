using Microsoft.Accordant;

namespace ResonateConformance;

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

    private static async Task<int> HandCraftedRaces(Harness harness)
    {
        Console.WriteLine("--- hand-crafted races (spec.AllowsConcurrent) ---");
        var spec = harness.Spec;
        int fails = 0;

        {
            harness.Now = 1_000_000;
            await harness.Client.DebugReset();
            long far = harness.Now + 1_000_000;
            await harness.Client.PromiseCreate("race1", far, Value.Of("w"), Harness.TargetTags);

            var acqOp = spec.GetOperation("AcquireTask");
            var reqA = new AcquireTask("race1", 0, "wA");
            var reqB = new AcquireTask("race1", 0, "wB");

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
                        State = "pending", TimeoutAt = far,
                        ParamData = "w", HasTarget = true, CreatedAt = harness.Now,
                    },
                },
                Tasks = { ["race1"] = new TaskState { State = "pending", Version = 0 } },
            });

            var calls = new List<(IOperation, object, object)>
            {
                (acqOp, reqA, respA),
                (acqOp, reqB, respB),
            };
            var (ok, msg, _) = spec.AllowsConcurrent(profile, calls);
            Report("race1: concurrent double-acquire is linearizable", ok, msg, ref fails);

            var oneWinner = (sA == 200) ^ (sB == 200);
            Report("race1: exactly one acquire won (one-winner)", oneWinner, "both or neither won", ref fails);
        }

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
                        State = "pending", TimeoutAt = far,
                        ParamData = "w", HasTarget = true, CreatedAt = harness.Now,
                    },
                },
                Tasks = { ["race2"] = new TaskState { State = "pending", Version = 0 } },
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

    private static async Task<int> Generated(Harness harness)
    {
        Console.WriteLine("\n--- generated concurrent cases (GenerateConcurrentTests + RunTests) ---");
        var spec = harness.Spec;
        long far = harness.Now + 1_000_000;

        var create = spec.GetOperation<CreatePromise, Response>("CreatePromise");
        var acquire = spec.GetOperation<AcquireTask, Response>("AcquireTask");
        var get = spec.GetOperation<GetPromise, Response>("GetPromise");

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

        return SystemChecker.Validate(new List<IList<IStepFunction>>(), s, null);
    }

    private static void Report(string label, bool ok, string msg, ref int fails)
    {
        if (ok) Console.WriteLine($"  ✅ {label}");
        else { fails++; Console.WriteLine($"  ❌ {label}: {msg}"); }
    }
}
