using Microsoft.Accordant;

namespace ResonateConformance;

/// <summary>
/// SELF-TEST for the optional indefinite-failure modeling (see
/// <see cref="IndefiniteFailures"/> and <see cref="ServerOperation{TReq,TResp}"/>).
///
/// No live server: it drives <c>spec.Allows</c> with fabricated responses to prove
/// the on/off switch and the branch semantics —
/// <list type="number">
///   <item><b>Gated off</b> — with the flag OFF, an indefinite response to a
///         mutating op is REJECTED (default runs stay strict).</item>
///   <item><b>Accepted on, both branches live</b> — with the flag ON, an indefinite
///         response to a STATE-CHANGING op is ACCEPTED and the profile splits into
///         TWO states ("it happened" and "it didn't"); a NON-changing op yields
///         just ONE branch (no redundant duplicate).</item>
///   <item><b>Profile collapse</b> — a later definite read narrows that two-state
///         profile back to one, and EITHER observation (the mutation landed, or it
///         didn't) is legal — proving both possibilities were genuinely modeled.</item>
/// </list>
/// Mirrors <see cref="Invariants.SelfTest"/>: fabricated inputs, asserts the
/// machinery has teeth, returns 0/1.
/// </summary>
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

        // A fresh state where "p" does not exist yet; a create is legal and mutating.
        StateProfile Fresh() => new(new ServerState { Now = 1_000_000 });
        var req = new CreatePromise("p", 9_999_999_999, null);

        // (1) Gated OFF: an indefinite response to create must be REJECTED.
        IndefiniteFailures.Suppress(() =>
        {
            var (valid, _, _) = spec.Allows(create, req, lost, Fresh());
            fails += Check("flag OFF → indefinite response REJECTED", !valid);
        });

        // (2) Gated ON: the same response is ACCEPTED, and the profile has 2 states.
        StateProfile? afterLost = null;
        IndefiniteFailures.Enable(() =>
        {
            var (valid, msg, next) = spec.Allows(create, req, lost, Fresh());
            fails += Check("flag ON → indefinite response ACCEPTED", valid, msg);
            afterLost = next;
            var branches = next?.StatesAndStepFunctions.Count ?? 0;
            fails += Check($"flag ON → profile splits into 2 branches (got {branches})", branches == 2);
        });

        // (2b) A NON-state-changing outcome (here: GET on a missing promise, a
        // SameState 404 guard) adds only ONE indefinite branch — the "it happened"
        // branch would be byte-identical to "it didn't", so it's skipped as a
        // redundant duplicate (matches the sample's GetStateHash guard).
        IndefiniteFailures.Enable(() =>
        {
            var (valid, _, next) = spec.Allows(get, new GetPromise("absent"), lost, Fresh());
            var branches = next?.StatesAndStepFunctions.Count ?? 0;
            fails += Check($"flag ON → no-op outcome yields 1 branch, not a duplicate (got {branches})",
                valid && branches == 1);
        });

        // (3) Profile collapse. From the two-state profile (p exists pending | p absent),
        // a follow-up GetPromise is checked with the flag OFF (a normal read):
        //   - observing "pending"  → legal, collapses to the "it happened" branch;
        //   - observing 404        → legal, collapses to the "it didn't" branch.
        // Both being legal proves both possibilities were genuinely live.
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

        // (4) Sanity: with the flag ON, a DEFINITE (normal) response is still accepted —
        // enabling indefinite failures ADDS branches, it doesn't remove the happy path.
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
