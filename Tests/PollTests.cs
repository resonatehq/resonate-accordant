namespace ResonateConformance;

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

        await checker.Step(new CreatePromise("pl:pA", far, "a", External: true), "create pA (external)");
        await checker.Step(new CreatePromise("pl:pT", far, "w", WithTarget: true), "create pT +target");
        await checker.Step(new AcquireTask("pl:pT", 0, "w1"), "acquire pT → v1");
        await checker.Step(new SuspendTask("pl:pT", 1, "pl:pA"), "suspend pT awaiting pA → 200");
        await checker.Step(new SettlePromise("pl:pA", "resolved", "a!"), "settle pA");

        await checker.Step(new PollTask(Group, "w2"), "poll → claims pT at v2 (lazy resume fires here)");

        await checker.Step(new PollTask(Group, "w3"), "poll again → nothing claimable");

        await checker.Step(new ReleaseTask("pl:pT", 2), "release pT v2 → pending");
        await checker.Step(new PollTask(Group, "w4"), "poll → re-claims pT at v3");

        await checker.Step(new FulfillTask("pl:pT", 3, "resolved", "done"), "fulfill pT v3");
        await checker.Step(new PollTask(Group, "w5"), "poll → nothing (fulfilled task is not claimable)");

        Console.WriteLine("\n========================================");
        Console.WriteLine($"  task.poll: steps ok: {checker.Ok}   failed: {checker.Failed}");
        Console.WriteLine("========================================");
        return checker.Failed == 0 ? 0 : 1;
    }
}
