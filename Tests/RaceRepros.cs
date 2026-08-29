using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// Targeted race repros (sim/resumerace_test.go's idea, upgraded): hammer the
/// SPECIFIC races where a wake-up can be lost, and after every race PROVE the
/// task is not stranded — poll task.get until EVERY candidate state in the
/// profile has left `suspended`. A task left suspended while its awaited
/// promise is resolved is a lost resume: a real durability bug no per-response
/// check can see (every individual response is legal; the system is just stuck).
///
/// The headline race: suspend(T awaiting P) fired CONCURRENTLY with settle(P).
/// Legal outcomes: suspend wins → 200 + the settle's resume lands eventually
/// (T: suspended → pending); settle wins → suspend sees a settled awaited →
/// 300 resume-now, T stays acquired. Either way T must end NOT suspended.
/// </summary>
public static class RaceRepros
{
    public static async Task<int> Run(Harness harness, int iters = 100)
    {
        Console.WriteLine($"\n########## RACE REPROS (suspend ‖ settle × {iters}) ##########\n");
        var spec = harness.Spec;
        harness.Now = 1_000_000;
        long far = 9_000_000_000_000;

        int suspendWon = 0, settleWon = 0, stranded = 0, fails = 0;
        StateProfile profile = null!;

        var createP = spec.GetOperation("CreatePromise");
        var acquire = spec.GetOperation("AcquireTask");
        var suspend = spec.GetOperation("SuspendTask");
        var settle = spec.GetOperation("SettlePromise");
        var getTask = spec.GetOperation("GetTask");

        for (int i = 0; i < iters && fails == 0; i++)
        {
            // Reset every 50 iterations to keep the model state (and clone cost) bounded.
            if (i % 50 == 0)
            {
                await harness.Client.DebugReset();
                profile = SystemChecker.Validate(new List<IList<IStepFunction>>(),
                    new ServerState { Now = harness.Now }, null);
            }

            string P = $"rc{i}:P", T = $"rc{i}:T";

            // Sequential setup, spec-checked at every step. P is external —
            // awaited promises must be (the race is suspend vs settle on it).
            if (!await Step(createP, new CreatePromise(P, far, "x", External: true))) { fails++; break; }
            if (!await Step(createP, new CreatePromise(T, far, "w", WithTarget: true))) { fails++; break; }
            if (!await Step(acquire, new AcquireTask(T, 0, "w1"))) { fails++; break; }

            // THE RACE: suspend(T awaiting P)  ‖  settle(P).
            var suspendReq = new SuspendTask(T, 1, P);
            var settleReq = new SettlePromise(P, "resolved", "r");
            var tS = suspend.ExecuteAsync(harness.NewContext(), suspendReq);
            var tP = settle.ExecuteAsync(harness.NewContext(), settleReq);
            await Task.WhenAll(tS, tP);
            var rS = await tS; var rP = await tP;

            var calls = new List<(IOperation, object, object)>
                { (suspend, suspendReq, rS), (settle, settleReq, rP) };
            var (ok, msg, next) = spec.AllowsConcurrent(profile, calls);
            if (!ok)
            {
                fails++;
                Console.WriteLine($"  ❌ iter {i}: race not linearizable: {msg}");
                Console.WriteLine($"       suspend={Status(rS)} settle={Status(rP)}");
                break;
            }
            profile = next;
            if (Status(rS) == 200) suspendWon++; else if (Status(rS) == 300) settleWon++;

            // STRANDED CHECK: poll until every candidate has T out of `suspended`.
            bool landed = false;
            for (int k = 0; k < 50; k++)
            {
                var getReq = new GetTask(T);
                var obs = await getTask.ExecuteAsync(harness.NewContext(), getReq);
                var (gok, gmsg, gnext) = spec.Allows(getTask, getReq, obs, profile);
                if (!gok) { fails++; Console.WriteLine($"  ❌ iter {i}: poll rejected: {gmsg}"); break; }
                profile = gnext;
                if (profile.StatesAndStepFunctions.All(ssf =>
                        !((ServerState)ssf.State).Tasks.TryGetValue(T, out var t) || t.State != "suspended"))
                {
                    landed = true;
                    break;
                }
                await Task.Delay(50);
            }
            if (fails > 0) break;
            if (!landed)
            {
                stranded++;
                Console.WriteLine($"  🚨 iter {i}: STRANDED — {T} still suspended with {P} resolved (lost resume)");
            }

            async Task<bool> Step(IOperation op, object req)
            {
                var obs = await op.ExecuteAsync(harness.NewContext(), req);
                var (sok, smsg, snext) = spec.Allows(op, req, obs, profile);
                if (!sok) { Console.WriteLine($"  ❌ iter {i} setup {req}: {smsg}"); return false; }
                profile = snext;
                return true;
            }
        }

        Console.WriteLine($"\n========================================");
        Console.WriteLine($"  races: {iters}   suspend-won: {suspendWon}   settle-won: {settleWon}");
        Console.WriteLine($"  STRANDED tasks (lost resumes): {stranded}   conformance failures: {fails}");
        Console.WriteLine($"========================================");
        return stranded == 0 && fails == 0 ? 0 : 1;
    }

    private static int Status(object o) => (o as Response)?.Status ?? -1;
}
