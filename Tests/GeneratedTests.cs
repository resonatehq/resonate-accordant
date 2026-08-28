using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Let Accordant GENERATE test cases from an InputSet by exploring the model's
/// state graph, then run them against the live server. debug.reset before each
/// case gives the clean initial state the generated sequence assumes.
/// </summary>
public static class GeneratedTests
{
    public static async Task<int> RunSequential(Harness harness)
    {
        Console.WriteLine("\n########## GENERATED SEQUENTIAL TESTS ##########\n");
        var spec = harness.Spec;
        // Wall-clock-era clock + far-future timeout so the server's BACKGROUND
        // timeout loop (which uses wall clock, not injected debug_time) never fires
        // mid-case. Timeout is tested deterministically in trace mode via Tick.
        harness.Now = 3_000_000_000_000;  // ~2065
        long far = 9_000_000_000_000; // ~2255

        // The pool of operation+value inputs the generator draws from. Fixed ids
        // (p, q, tk) are reused across cases; BeforeEach(debug.reset) isolates them.
        var get = spec.GetOperation<GetPromise, Response>("GetPromise");
        var create = spec.GetOperation<CreatePromise, Response>("CreatePromise");
        var settle = spec.GetOperation<SettlePromise, Response>("SettlePromise");
        var acquire = spec.GetOperation<AcquireTask, Response>("AcquireTask");
        var release = spec.GetOperation<ReleaseTask, Response>("ReleaseTask");
        var fulfill = spec.GetOperation<FulfillTask, Response>("FulfillTask");
        var regcb = spec.GetOperation<RegisterCallback, Response>("RegisterCallback");
        var reglisten = spec.GetOperation<RegisterListener, Response>("RegisterListener");
        var suspend = spec.GetOperation<SuspendTask, Response>("SuspendTask");

        var inputs = new InputSet
        {
            create.With(new CreatePromise("g:p", far, "v1"), "create p"),
            settle.With(new SettlePromise("g:p", "resolved", "r1"), "resolve p"),
            settle.With(new SettlePromise("g:p", "rejected", "e1"), "reject p"),
            get.With(new GetPromise("g:p"), "get p"),
            get.With(new GetPromise("g:q"), "get q (missing)"),

            create.With(new CreatePromise("g:tk", far, "work", WithTarget: true), "create tk +target"),
            // A timer: external by its third disjunct alone, and the generator
            // can now walk it into every await the pool reaches.
            create.With(new CreatePromise("g:tm", far, "ring", Timer: true), "create tm (timer)"),
            regcb.With(new RegisterCallback("g:tm", "g:tk"), "register_callback tm←tk"),
            spec.GetOperation<CreateTask, Response>("CreateTask").With(new CreateTask("g:tc", far, "self", true), "task.create tc"),
            acquire.With(new AcquireTask("g:tk", 0, "w1"), "acquire tk v0"),
            acquire.With(new AcquireTask("g:tk", 1, "w1"), "acquire tk v1"),
            release.With(new ReleaseTask("g:tk", 1), "release tk v1"),
            fulfill.With(new FulfillTask("g:tk", 1, "resolved", "done"), "fulfill tk v1"),

            regcb.With(new RegisterCallback("g:p", "g:tk"), "register_callback p←tk"),
            reglisten.With(new RegisterListener("g:p", "http://localhost/cb"), "register_listener p"),
            suspend.With(new SuspendTask("g:tk", 1, "g:p"), "suspend tk awaiting p"),
            spec.GetOperation<FenceTask, Response>("FenceTask").With(
                new FenceTask("g:tk", 1, Settle: new SettlePromise("g:p", "resolved", "fenced")), "fence tk ⇒ settle p"),
            spec.GetOperation<FenceTask, Response>("FenceTask").With(
                new FenceTask("g:tk", 1, Create: new CreatePromise("g:fc", far, "kid")), "fence tk ⇒ create fc"),
        };

        int rc = await RunPool(harness, "broad pool", inputs, maxDepth: 4);

        // A FOCUSED pool, deep enough to reach the await/resume lattice the broad
        // pool cannot (arming a resume takes 5 steps): create p → create tk →
        // acquire v0 → suspend awaiting p → resolve p [resume armed] → task.get
        // [collapse the in-flight/landed branch] → acquire v1 [re-acquirability].
        // Kept to 7 inputs so depth 7 stays tractable.
        var gettask = spec.GetOperation<GetTask, Response>("GetTask");
        var deepInputs = new InputSet
        {
            // p is EXTERNAL: awaited promises must be (internal → suspend 422).
            create.With(new CreatePromise("g:p", far, "v1", External: true), "create p (external)"),
            // WithoutPolling: when this settle arms in-flight resumes, the executor
            // would otherwise demand a PollingSetup — but the awaiter isn't derivable
            // from the settle request/response. Generated cases instead check SAFETY
            // across both branches (the profile keeps carrying the armed triggers);
            // LIVENESS is asserted dedicatedly by the trace's PollLiveness.
            settle.With(new SettlePromise("g:p", "resolved", "r1"), "resolve p").WithoutPolling(),
            create.With(new CreatePromise("g:tk", far, "work", WithTarget: true), "create tk +target"),
            acquire.With(new AcquireTask("g:tk", 0, "w1"), "acquire tk v0"),
            acquire.With(new AcquireTask("g:tk", 1, "w1"), "acquire tk v1"),
            suspend.With(new SuspendTask("g:tk", 1, "g:p"), "suspend tk awaiting p"),
            gettask.With(new GetTask("g:tk"), "task.get tk"),
        };

        rc |= await RunPool(harness, "deep await/resume pool", deepInputs, maxDepth: 7);
        return rc;
    }

    private static async Task<int> RunPool(Harness harness, string label, InputSet inputs, int maxDepth)
    {
        var spec = harness.Spec;
        var initial = harness.InitialState();
        var cases = spec.GenerateTests(initial, inputs, new TestGenerationOptions { MaxDepth = maxDepth });
        Console.WriteLine($"generated {cases.Count} sequential test cases ({label}, MaxDepth={maxDepth})\n");

        var ctx = harness.NewContext();
        var options = new TestExecutionOptions { StopOnFirstFailure = false }
            .WithBeforeEachAsync(async _ =>
            {
                harness.Now = 3_000_000_000_000;  // reset logical clock (wall-clock era)
                await harness.Client.DebugReset(); // reset server state
            });

        var results = await spec.RunTests(ctx, initial, cases, options);

        int passed = results.Count(r => r.Success);
        int failed = results.Count - passed;
        Console.WriteLine($"\nsequential generated ({label}): {passed}/{results.Count} passed, {failed} failed");
        foreach (var r in results.Where(r => !r.Success))
            Console.WriteLine($"  ❌ {r.LastFailureMessage}");
        return failed == 0 ? 0 : 1;
    }
}
