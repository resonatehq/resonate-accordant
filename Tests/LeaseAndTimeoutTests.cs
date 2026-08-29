namespace ResonateConformance;

public static class LeaseAndTimeoutTests
{
    public static async Task<int> Run(Harness harness)
    {
        Console.WriteLine("\n########## LEASE / HEARTBEAT TRACE (driven clock) ##########\n");
        harness.Now = 1_000_000;
        await harness.Client.DebugReset();
        await harness.Client.DebugTick(harness.Now);

        var checker = new TraceRunner(harness.Spec, harness.Client, harness.Now);
        long far = 9_000_000_000_000;

        await checker.Step(new CreatePromise("L", far, "w", WithTarget: true), "create L +target");
        await checker.Step(new AcquireTask("L", 0, "wA", 5_000), "acquire L ttl=5000 → acquired v1 (lease→1005000)");
        await checker.Step(new AdvanceClock(1_004_000), "tick to 1004000 (within lease)");
        await checker.Step(new AcquireTask("L", 1, "wB", 5_000), "acquire L v1 while still held → 409");
        await checker.Step(new AdvanceClock(1_005_000), "tick to 1005000 (== lease expiry) → reclaim");

        await checker.Step(new GetTask("L"), "task.get L past expiry → acquired OR pending (reclaim is a choice)");
        await checker.Step(new AcquireTask("L", 1, "wB", 5_000), "re-acquire L v1 after reclaim → acquired v2");

        await checker.Step(new CreatePromise("H", far, "w", WithTarget: true), "create H +target");
        await checker.Step(new AcquireTask("H", 0, "wH", 5_000), "acquire H ttl=5000 at 1005000 (lease→1010000)");
        await checker.Step(new AdvanceClock(1_008_000), "tick to 1008000 (within lease)");
        await checker.Step(new HeartbeatTask("H", 1, "wH"), "heartbeat H (pid match) → lease resets to 1008000+5000=1013000");
        await checker.Step(new AdvanceClock(1_012_000), "tick to 1012000 (past ORIG lease, within extended)");
        await checker.Step(new AcquireTask("H", 1, "wX", 5_000), "acquire H v1 → 409 (heartbeat kept it alive)");

        await checker.Step(new HeartbeatTask("H", 1, "WRONG"), "heartbeat H wrong pid → 200 but no extension");
        await checker.Step(new AdvanceClock(1_014_000), "tick to 1014000 (past extended lease) → reclaim");
        await checker.Step(new AcquireTask("H", 1, "wX", 5_000), "re-acquire H v1 after reclaim → acquired v2");

        await checker.Step(new HeartbeatTask("H", 1, "wX"), "heartbeat H wrong VERSION (v1, holder v2) → 200 but no extension");
        await checker.Step(new AdvanceClock(1_019_000), "tick to 1019000 (== v2 lease expiry) → reclaim");
        await checker.Step(new AcquireTask("H", 2, "wY", 5_000), "re-acquire H v2 after reclaim → acquired v3");

        return checker.Report();
    }
}
