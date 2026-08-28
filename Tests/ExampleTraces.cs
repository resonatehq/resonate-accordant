
namespace ResonateConformance;

/// <summary>
/// Hand-written example traces, checked step-by-step via spec.Allows. Example
/// traces are documentation you can run: each Step drives the live server and
/// asserts the observed response is one the model permits from the current state.
/// </summary>
public static class ExampleTraces
{
    public static async Task<int> Run(Harness harness)
    {
        Console.WriteLine("########## HAND-WRITTEN EXAMPLE TRACES ##########\n");
        // Wall-clock-era base so the server's background (wall-clock) timeout loop
        // never prematurely sweeps our short-relative-timeout promises mid-run.
        // The explicit timeout test below still fires deterministically via Tick.
        harness.Now = 3_000_000_000_000;
        var reset = await harness.Client.DebugReset();
        await harness.Client.DebugTick(harness.Now);
        Console.WriteLine($"debug.reset → {reset.Status}\n");

        var checker = new TraceRunner(harness.Spec, harness.Client, harness.Now);
        long now = harness.Now;

        // --- promise layer ---
        await checker.Step(new CreatePromise("ex:p1", now + 10_000, "hello"), "create p1 pending");
        await checker.Step(new GetPromise("ex:p1"), "get p1 → pending");
        await checker.Step(new SettlePromise("ex:p1", "resolved", "42"), "settle p1 → resolved(42)");
        await checker.Step(new GetPromise("ex:p1"), "get p1 → resolved echo");
        await checker.Step(new SettlePromise("ex:p1", "resolved", "99"), "re-settle p1 → sticky (still 42)");

        await checker.Step(new CreatePromise("ex:p2", now + 10_000, "A"), "create p2");
        await checker.Step(new CreatePromise("ex:p2", now + 10_000, "B"), "re-create p2 → idempotent (echoes A)");

        await checker.Step(new GetPromise("ex:ghost"), "get missing → 404");

        // cancel path + invalid state
        await checker.Step(new CreatePromise("ex:pc", now + 10_000, "z"), "create pc");
        await checker.Step(new SettlePromise("ex:pc", "rejected_canceled", "gone"), "cancel pc → rejected_canceled");
        await checker.Step(new SettlePromise("ex:pc", "resolved", "x"), "re-settle cancelled → sticky");
        await checker.Step(new SettlePromise("ex:pc", "bogus", "x"), "settle invalid state → 400");

        // Negative self-tests — prove spec.Allows discriminates.
        checker.ExpectRejected(new GetPromise("ex:p1"), Fake.Promise("ex:p1", "resolved", "999"),
            "get p1 claiming value 999 (real is 42)");
        checker.ExpectRejected(new GetPromise("ex:p1"), Fake.Promise("ex:p1", "pending", null),
            "get p1 claiming pending (it's resolved)");

        // Timeout nondeterminism.
        await checker.Step(new CreatePromise("ex:p3", now + 5_000, "x"), "create p3 (5s ttl)");
        await checker.Step(new AdvanceClock(now + 6_000), "tick past p3 deadline");
        await checker.Step(new GetPromise("ex:p3"), "get p3 → (pending|timedout) nondeterministic");

        // --- task layer ---
        Console.WriteLine("\n--- task layer ---");
        await checker.DrainMessages();
        await checker.Step(new CreatePromise("ex:t1", now + 100_000, "work", WithTarget: true), "create t1 +target (task pending v0)");
        await checker.Step(new AcquireTask("ex:t1", 0, "worker-1"), "acquire t1 v0 → acquired v1");
        await checker.ExpectMessages("egress: create t1 emitted execute t1 v0 (acquire emits nothing)",
            ("execute", "ex:t1", 0));
        await checker.Step(new AcquireTask("ex:t1", 0, "worker-2"), "acquire t1 v0 again → 409 (one winner)");
        await checker.Step(new FulfillTask("ex:t1", 1, "resolved", "done"), "fulfill t1 v1 → resolved(done)");
        await checker.Step(new GetPromise("ex:t1"), "get t1 → resolved echo");

        await checker.Step(new CreatePromise("ex:t2", now + 100_000, "w", WithTarget: true), "create t2 +target");
        await checker.Step(new AcquireTask("ex:t2", 0, "worker-1"), "acquire t2 → v1");
        await checker.Step(new ReleaseTask("ex:t2", 1), "release t2 v1 → pending (version unchanged)");
        await checker.Step(new FulfillTask("ex:t2", 1, "resolved", "zombie"), "stale fulfill t2 v1 → 409 (no settle)");
        await checker.Step(new GetPromise("ex:t2"), "get t2 → still pending (zombie blocked)");

        await checker.Step(new AcquireTask("ex:t2", 9, "worker-1"), "acquire t2 wrong version → 409");
        await checker.Step(new AcquireTask("ex:ghostTask", 0, "worker-1"), "acquire missing task → 404");

        // task.create atomic path
        await checker.Step(new CreateTask("ex:tc1", now + 100_000, "self", true), "task.create tc1 → acquired v1");
        await checker.Step(new CreateTask("ex:tc1", now + 100_000, "self", true), "task.create tc1 dup → 409 Already exists");
        await checker.Step(new CreateTask("ex:tc2", now + 100_000, "self", false), "task.create no-target → 400");
        await checker.Step(new FulfillTask("ex:tc1", 1, "resolved", "ok"), "fulfill tc1 v1 → resolved");
        await checker.Step(new CreateTask("ex:p1", now + 100_000, "self", true), "task.create over plain promise p1 → 422");

        // task.fence: fenced inner action (settle OR create of another promise).
        await checker.Step(new CreatePromise("ex:fp", now + 100_000, "w", WithTarget: true), "create fp +target");
        await checker.Step(new AcquireTask("ex:fp", 0, "worker-1"), "acquire fp → v1");
        await checker.Step(new CreatePromise("ex:fchild", now + 100_000, "c"), "create fchild (plain, pending)");
        await checker.Step(new FenceTask("ex:fp", 1, Settle: new SettlePromise("ex:fp", "resolved", "x")), "fence fp with action id==task id → 400");
        await checker.Step(new FenceTask("ex:fp", 9, Settle: new SettlePromise("ex:fchild", "resolved", "x")), "fence fp wrong version → 409 (no settle)");
        await checker.Step(new FenceTask("ex:fp", 1, Settle: new SettlePromise("ex:fchild", "resolved", "sealed")), "fence fp v1 → settles fchild");
        await checker.Step(new GetPromise("ex:fchild"), "get fchild → resolved(sealed)");

        // fence-CREATE: the fenced arm of P-02 — fresh, dedup, born-expired, +target.
        await checker.Step(new FenceTask("ex:fp", 1, Create: new CreatePromise("ex:fnew", now + 100_000, "kid")), "fence-create fnew → born pending (fenced P-02)");
        await checker.Step(new GetPromise("ex:fnew"), "get fnew → pending");
        await checker.Step(new FenceTask("ex:fp", 1, Create: new CreatePromise("ex:fnew", now + 999, "dup")), "fence-create fnew dup → projected echo (param ignored)");
        await checker.Step(new FenceTask("ex:fp", 1, Create: new CreatePromise("ex:fexp", now + 1_000, "old")), "fence-create past deadline → born rejected_timedout");
        await checker.Step(new FenceTask("ex:fp", 1, Create: new CreatePromise("ex:ftask", now + 100_000, "w", WithTarget: true)), "fence-create +target → task spawned pending v0");
        await checker.Step(new AcquireTask("ex:ftask", 0, "worker-1"), "acquire ftask → v1 (fence-spawned task claimable)");

        await checker.Step(new ReleaseTask("ex:fp", 1), "release fp v1");
        await checker.Step(new CreatePromise("ex:fchild2", now + 100_000, "c"), "create fchild2");
        await checker.Step(new FenceTask("ex:fp", 1, Settle: new SettlePromise("ex:fchild2", "resolved", "late")), "stale fence after release → 409 (fchild2 untouched)");
        await checker.Step(new FenceTask("ex:fp", 1, Create: new CreatePromise("ex:fzomb", now + 100_000, "z")), "stale fence-create → 409 (fzomb NOT created)");
        await checker.Step(new GetPromise("ex:fchild2"), "get fchild2 → still pending (zombie fence blocked)");
        await checker.Step(new GetPromise("ex:fzomb"), "get fzomb → 404 (zombie create blocked)");

        // --- await / callback layer ---
        // Awaited promises must be EXTERNAL (tagged external / targeted / timer);
        // a plain internal promise must not have awaiters — 422.
        Console.WriteLine("\n--- await/callback layer ---");
        await checker.Step(new CreatePromise("ex:awaited", now + 100_000, "x", External: true), "create awaited (external, pending)");
        await checker.Step(new RegisterCallback("ex:awaited", "ex:t1"), "register_callback on pending awaited → 200 echo");
        await checker.Step(new RegisterCallback("ex:ghost", "ex:t1"), "register_callback on missing awaited → 404");
        await checker.Step(new RegisterCallback("ex:p2", "ex:t1"), "register_callback on INTERNAL awaited → 422");
        // An await is confined to ONE call graph. Ids read "<origin>:<lineage>",
        // so `other:*` belongs to a different root than `ex:*`, and the server
        // rejects the pair before it looks at either promise — the awaited here
        // is external, pending and live, so the ONLY thing wrong is the origin.
        await checker.Step(new CreatePromise("other:x", now + 100_000, "x", External: true), "create other:x (external, foreign origin)");
        await checker.Step(new RegisterCallback("other:x", "ex:t1"), "register_callback across origins → 400");
        await checker.Step(new RegisterCallback("ex:awaited", "other:t"), "register_callback with foreign awaiter → 400 (before the 422)");
        await checker.Step(new RegisterListener("ex:awaited", "http://localhost/cb"), "register_listener valid addr → 200");
        // Well-formed URI, malformed for its scheme: admitted. The server knows
        // only that an address is a URI — `poll://`'s own syntax belongs to the
        // poll worker, which rejects this at delivery, not at admission.
        await checker.Step(new RegisterListener("ex:awaited", "poll://bad"), "register_listener scheme-malformed addr → 200 (admitted)");
        // No scheme at all — the one thing admission does check.
        await checker.Step(new RegisterListener("ex:awaited", "not a url"), "register_listener invalid addr → 400");

        // suspend: root awaiting a PENDING promise → 200 suspended.
        await checker.Step(new CreatePromise("ex:root", now + 100_000, "r", WithTarget: true), "create root +target");
        await checker.Step(new AcquireTask("ex:root", 0, "worker-1"), "acquire root → v1");
        await checker.Step(new SuspendTask("ex:root", 1, "ex:awaited"), "suspend root awaiting pending → 200 suspended");
        await checker.Step(new GetTask("ex:root"), "task.get root → suspended v1");
        await checker.Step(new SuspendTask("ex:root", 1, "other:x"), "suspend awaiting a foreign origin → 400");
        await checker.Step(new SuspendTask("ex:root", 1, "ex:awaited,other:x"), "suspend where ONE action is foreign → 400 (whole request)");

        // Settling the awaited arms an IN-FLIGHT resume of root (async trigger):
        // a read may now legally see root suspended OR pending, until observed.
        // The liveness poll demands the resume observably LANDS; the re-acquire
        // then proves re-acquirability at the SAME version (resume ≠ version bump).
        await checker.DrainMessages();
        await checker.Step(new SettlePromise("ex:awaited", "resolved", "v"), "settle awaited → arms resume of root");
        await checker.PollLiveness(new GetTask("ex:root"),
            s => !s.Tasks.TryGetValue("ex:root", out var t) || t.Status != "suspended",
            "resume of root lands (liveness)");
        // The resume's execute is a wake-up hint carrying root's CURRENT
        // version (v1 — resume never bumps); the settle also unblocks the
        // registered listener with awaited's record.
        await checker.ExpectMessages("egress: settle awaited emitted execute root v1 + unblock awaited",
            ("execute", "ex:root", 1), ("unblock", "ex:awaited", 0));
        await checker.Step(new AcquireTask("ex:root", 1, "worker-1"), "re-acquire root v1 after resume → acquired v2");

        // A P-04 callback registered while the awaiter is RUNNING resumes it
        // even when it later suspends on a DIFFERENT promise: reg_cb(cbA ← cbT)
        // with cbT acquired, cbT suspends awaiting cbB, then settling cbA
        // discharges the stored callback — cbT resumes without cbB settling.
        await checker.Step(new CreatePromise("ex:cbA", now + 100_000, "a", External: true), "create cbA (external)");
        await checker.Step(new CreatePromise("ex:cbB", now + 100_000, "b", External: true), "create cbB (external)");
        await checker.Step(new CreatePromise("ex:cbT", now + 100_000, "w", WithTarget: true), "create cbT +target");
        await checker.Step(new AcquireTask("ex:cbT", 0, "worker-1"), "acquire cbT → v1");
        await checker.Step(new RegisterCallback("ex:cbA", "ex:cbT"), "register_callback cbA←cbT while cbT RUNNING (stored)");
        await checker.Step(new SuspendTask("ex:cbT", 1, "ex:cbB"), "suspend cbT awaiting cbB → 200 suspended");
        await checker.DrainMessages();
        await checker.Step(new SettlePromise("ex:cbA", "resolved", "a!"), "settle cbA → arms resume of cbT via the P-04 callback");
        await checker.PollLiveness(new GetTask("ex:cbT"),
            s => !s.Tasks.TryGetValue("ex:cbT", out var t) || t.Status != "suspended",
            "P-04 callback resume of cbT lands (liveness)");
        await checker.ExpectMessages("egress: settle cbA emitted execute cbT v1 (stored P-04 callback)",
            ("execute", "ex:cbT", 1));
        await checker.Step(new GetTask("ex:cbT"), "task.get cbT → pending v1 (resumed by cbA, not cbB)");

        // suspend: root2 awaiting an ALREADY-SETTLED (external) promise → 300;
        // and awaiting an INTERNAL promise → 422, task stays acquired.
        await checker.Step(new CreatePromise("ex:done", now + 100_000, "d", External: true), "create done (external)");
        await checker.Step(new SettlePromise("ex:done", "resolved", "d"), "settle done");
        await checker.Step(new CreatePromise("ex:root2", now + 100_000, "r", WithTarget: true), "create root2 +target");
        await checker.Step(new AcquireTask("ex:root2", 0, "worker-1"), "acquire root2 → v1");
        await checker.Step(new SuspendTask("ex:root2", 1, "ex:done"), "suspend root2 awaiting SETTLED → 300 resume-now");

        // multi-action suspend (wait-any). The malformed-request 400s (empty
        // actions, duplicate awaited) precede EVERY state consult.
        await checker.Step(new SuspendTask("ex:root2", 1, ""), "suspend with EMPTY actions → 400");
        await checker.Step(new SuspendTask("ex:ghostTask", 0, ""), "empty actions on MISSING task → 400 (precedes 404)");
        await checker.Step(new CreatePromise("ex:wA", now + 100_000, "a", External: true), "create wA (external)");
        await checker.Step(new CreatePromise("ex:wB", now + 100_000, "b", External: true), "create wB (external)");
        await checker.Step(new SuspendTask("ex:root2", 1, "ex:wA,ex:wB"), "suspend root2 awaiting {wA,wB} → 200 (wait-any park)");
        await checker.Step(new GetTask("ex:root2"), "task.get root2 → suspended v1");
        await checker.Step(new SettlePromise("ex:wB", "resolved", "b!"), "settle wB → resumes root2 (any one of the set)");
        await checker.PollLiveness(new GetTask("ex:root2"),
            s => !s.Tasks.TryGetValue("ex:root2", out var t) || t.Status != "suspended",
            "wait-any resume of root2 lands (liveness)");
        await checker.Step(new AcquireTask("ex:root2", 1, "worker-1"), "re-acquire root2 v1 → v2");
        await checker.Step(new SuspendTask("ex:root2", 2, "ex:wA,ex:wB"), "suspend awaiting {pending wA, SETTLED wB} → 300 (any settled wins)");

        // A duplicate awaited id is a malformed request → 400, before any state
        // is consulted. Its own task, for the same reason as rInt below: a
        // server that parks instead would poison what follows.
        await checker.Step(new CreatePromise("ex:rDup", now + 100_000, "r", WithTarget: true), "create rDup +target");
        await checker.Step(new AcquireTask("ex:rDup", 0, "worker-1"), "acquire rDup → v1");
        await checker.Step(new SuspendTask("ex:rDup", 1, "ex:wA,ex:wA"), "suspend rDup with DUPLICATE awaited → 400");

        // Awaiting an INTERNAL promise → 422, task stays acquired. On its OWN
        // task: a server that instead parks it would otherwise carry that
        // divergence into every assertion below.
        await checker.Step(new CreatePromise("ex:rInt", now + 100_000, "r", WithTarget: true), "create rInt +target");
        await checker.Step(new AcquireTask("ex:rInt", 0, "worker-1"), "acquire rInt → v1");
        await checker.Step(new SuspendTask("ex:rInt", 1, "ex:p2"), "suspend rInt awaiting INTERNAL → 422 (stays acquired)");

        // --- timeout cascade ---
        // A promise timing out at a tick is a SETTLEMENT: it commits durably,
        // fulfills its own task, and resumes its suspended awaiters. Durable
        // timeouts fire for EXTERNAL promises exactly — and only external
        // promises may have awaiters, so nothing can be stranded (protocol
        // decision 2026-07-15, closing the liveness hole this harness found).
        // (The clock is at now+6_000 from the p3 test; deadlines are beyond it.)
        Console.WriteLine("\n--- timeout cascade ---");
        await checker.Step(new CreatePromise("ex:late", now + 20_000, "x", WithTarget: true), "create late +target (times out at +20s)");
        await checker.Step(new CreatePromise("ex:root3", now + 100_000, "r", WithTarget: true), "create root3 +target");
        await checker.Step(new AcquireTask("ex:root3", 0, "worker-1"), "acquire root3 → v1");
        await checker.Step(new SuspendTask("ex:root3", 1, "ex:late"), "suspend root3 awaiting late");
        await checker.Step(new AdvanceClock(now + 21_000), "tick past late's deadline → timeout fires");
        await checker.Step(new GetPromise("ex:late"), "get late → rejected_timedout (durable)");
        await checker.PollLiveness(new GetTask("ex:root3"),
            s => !s.Tasks.TryGetValue("ex:root3", out var t3) || t3.Status != "suspended",
            "timeout resumes root3 (liveness)");
        await checker.Step(new AcquireTask("ex:root3", 1, "worker-1"), "re-acquire root3 v1 after timeout-resume → v2");

        // A targeted task's own promise timing out fulfills the task.
        await checker.Step(new CreatePromise("ex:tt", now + 25_000, "w", WithTarget: true), "create tt +target (times out at +25s)");
        await checker.Step(new AdvanceClock(now + 26_000), "tick past tt's deadline");
        await checker.Step(new GetTask("ex:tt"), "task.get tt → fulfilled (own promise timed out)");
        await checker.Step(new AcquireTask("ex:tt", 0, "worker-1"), "acquire tt after timeout → 409 (fulfilled)");

        // Born expired WITH target (P-02's past-deadline target arm): the
        // promise is created already settled and the task is born FULFILLED v0.
        await checker.Step(new CreatePromise("ex:bx", now + 20_000, "b", WithTarget: true),
            "create bx +target past deadline → born rejected_timedout");
        await checker.Step(new GetTask("ex:bx"), "task.get bx → fulfilled v0 (born fulfilled)");

        // HUMAN-IN-THE-LOOP: an explicitly-external promise (no task, no target)
        // is awaitable AND fires its deadline durably — the awaiter wakes to
        // handle the missed approval instead of sleeping forever.
        await checker.Step(new CreatePromise("ex:appr", now + 30_000, "approve?", External: true),
            "create appr (external tag, times out at +30s)");
        await checker.Step(new CreatePromise("ex:root4", now + 100_000, "r", WithTarget: true), "create root4 +target");
        await checker.Step(new AcquireTask("ex:root4", 0, "worker-1"), "acquire root4 → v1");
        await checker.Step(new SuspendTask("ex:root4", 1, "ex:appr"), "suspend root4 awaiting appr (external) → 200");
        await checker.Step(new AdvanceClock(now + 31_000), "tick past appr's deadline → external timeout fires");
        await checker.Step(new GetPromise("ex:appr"), "get appr → rejected_timedout (durable, settledAt = deadline)");
        await checker.PollLiveness(new GetTask("ex:root4"),
            s => !s.Tasks.TryGetValue("ex:root4", out var t4) || t4.Status != "suspended",
            "appr's timeout resumes root4 (liveness)");
        await checker.Step(new AcquireTask("ex:root4", 1, "worker-1"), "re-acquire root4 v1 → acquired v2");

        return checker.Report();
    }
}
