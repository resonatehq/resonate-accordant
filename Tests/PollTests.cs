namespace ResonateConformance;

/// <summary>
/// task.poll — the liveness property task.get cannot express  [capability "poll"].
///
/// A task parks on an external promise; the promise settles. On a server whose
/// internal rules are lazy nothing is pushed and nothing is written, so
/// <c>task.get</c> reports `suspended` indefinitely — correctly, because waking
/// an awaiter is a scheduling CHOICE and the machine only ever projects FACTS.
/// The property that actually matters to a worker is not "does a read say
/// pending" but "can I eventually claim this", and <c>task.poll</c> is a
/// transition, so it is entitled to fire the rule and answer it.
///
/// This runs against ANY server declaring the capability, eager or lazy: an
/// eager server has already resumed the task, a lazy one resumes it as it
/// claims. Either way the poll must hand it over exactly once.
/// </summary>
public static class PollTests
{
    private const string Group = "testgroup";

    public static async Task<int> Run(Harness harness)
    {
        if (!Capabilities.Poll)
        {
            Console.WriteLine("\n########## TASK.POLL — skipped (target does not declare `poll`) ##########");
            return 0;
        }

        Console.WriteLine("\n########## TASK.POLL (claim by group) ##########\n");
        await harness.Client.DebugReset();
        harness.Now = 1_000_000;
        var checker = new TraceRunner(harness.Spec, harness.Client, harness.Now);
        long far = harness.Now + 100_000;

        // A parked worker whose awaited promise then settles.
        await checker.Step(new CreatePromise("pl:pA", far, "a", External: true), "create pA (external)");
        await checker.Step(new CreatePromise("pl:pT", far, "w", WithTarget: true), "create pT +target");
        await checker.Step(new AcquireTask("pl:pT", 0, "w1"), "acquire pT → v1");
        await checker.Step(new SuspendTask("pl:pT", 1, "pl:pA"), "suspend pT awaiting pA → 200");
        await checker.Step(new SettlePromise("pl:pA", "resolved", "a!"), "settle pA");

        // The claim: whatever a read reports, the poll hands pT over, acquired
        // at v2 with the new pid's lease. On a lazy server this is the ONLY way
        // the resume becomes observable.
        await checker.Step(new PollTask(Group, "w2"), "poll → claims pT at v2 (lazy resume fires here)");

        // Claimed exactly once: an immediately following poll must not hand the
        // same work to a second worker.
        await checker.Step(new PollTask(Group, "w3"), "poll again → nothing claimable");

        // A released task becomes claimable again, at the version it kept.
        await checker.Step(new ReleaseTask("pl:pT", 2), "release pT v2 → pending");
        await checker.Step(new PollTask(Group, "w4"), "poll → re-claims pT at v3");

        // A task whose own promise has settled is not work: it is fulfilled.
        await checker.Step(new FulfillTask("pl:pT", 3, "resolved", "done"), "fulfill pT v3");
        await checker.Step(new PollTask(Group, "w5"), "poll → nothing (fulfilled task is not claimable)");

        Console.WriteLine("\n========================================");
        Console.WriteLine($"  task.poll: steps ok: {checker.Ok}   failed: {checker.Failed}");
        Console.WriteLine("========================================");
        return checker.Failed == 0 ? 0 : 1;
    }
}
