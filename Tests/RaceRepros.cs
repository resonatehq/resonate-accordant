using Microsoft.Accordant;

namespace ResonateConformance;

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

            if (i % 50 == 0)
            {
                await harness.Client.DebugReset();
                profile = SystemChecker.Validate(new List<IList<IStepFunction>>(),
                    new ServerState { Now = harness.Now }, null);
            }

            string P = $"rc{i}:P", T = $"rc{i}:T";

            if (!await Step(createP, new CreatePromise(P, far, "x", External: true))) { fails++; break; }
            if (!await Step(createP, new CreatePromise(T, far, "w", WithTarget: true))) { fails++; break; }
            if (!await Step(acquire, new AcquireTask(T, 0, "w1"))) { fails++; break; }

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
