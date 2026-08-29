using Microsoft.Accordant;

namespace ResonateConformance;

public static class IndefiniteFailureTests
{
    public static int Run()
    {
        Console.WriteLine("\n########## INDEFINITE-FAILURE SELF-TEST (fabricated, no server) ##########\n");
        int fails = 0;

        var spec = ResonateSpec.Build();
        var create = spec.GetOperation("CreatePromise");
        var get = spec.GetOperation("GetPromise");

        var lost = Response.IndefiniteFailure();
        var okPending = Fake.Promise("p", "pending", null);

        StateProfile Fresh() => new(new ServerState { Now = 1_000_000 });
        var req = new CreatePromise("p", 9_999_999_999, null);

        IndefiniteFailures.Suppress(() =>
        {
            var (valid, _, _) = spec.Allows(create, req, lost, Fresh());
            fails += Check("flag OFF → indefinite response REJECTED", !valid);
        });

        StateProfile? afterLost = null;
        IndefiniteFailures.Enable(() =>
        {
            var (valid, msg, next) = spec.Allows(create, req, lost, Fresh());
            fails += Check("flag ON → indefinite response ACCEPTED", valid, msg);
            afterLost = next;
            var branches = next?.StatesAndStepFunctions.Count ?? 0;
            fails += Check($"flag ON → profile splits into 2 branches (got {branches})", branches == 2);
        });

        IndefiniteFailures.Enable(() =>
        {
            var (valid, _, next) = spec.Allows(get, new GetPromise("absent"), lost, Fresh());
            var branches = next?.StatesAndStepFunctions.Count ?? 0;
            fails += Check($"flag ON → no-op outcome yields 1 branch, not a duplicate (got {branches})",
                valid && branches == 1);
        });

        IndefiniteFailures.Suppress(() =>
        {
            var getReq = new GetPromise("p");

            var (vPending, mPending, nPending) = spec.Allows(get, getReq, okPending, afterLost!);
            fails += Check("collapse: GET pending is legal (the mutation landed)", vPending, mPending);
            fails += Check("collapse: → single state after disambiguation",
                nPending?.IsSingleState() == true);

            var (v404, m404, n404) = spec.Allows(get, getReq, Fake.NotFound(), afterLost!);
            fails += Check("collapse: GET 404 is legal (the mutation was lost)", v404, m404);
            fails += Check("collapse: → single state after disambiguation",
                n404?.IsSingleState() == true);
        });

        IndefiniteFailures.Enable(() =>
        {
            var (valid, msg, _) = spec.Allows(create, req, okPending, Fresh());
            fails += Check("flag ON → normal 'pending' response still ACCEPTED", valid, msg);
        });

        Console.WriteLine($"\n  self-test: {(fails == 0 ? "indefinite-failure modeling works ✅" : $"{fails} FAILED ❌")}");
        return fails == 0 ? 0 : 1;
    }

    private static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {label}{(ok || detail is null ? "" : $"  — {detail}")}");
        return ok ? 0 : 1;
    }
}
