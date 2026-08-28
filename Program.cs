using ResonateConformance;

// Conformance-model driver. Modes (arg 1): trace | gen | concurrent | lease |
// races [iters] | fuzz [seed] [ops] | invariant-selftest | all (default).
//
//   trace       hand-written example traces, step-checked via spec.Allows
//   gen         Accordant-generated sequential cases (GenerateTests + RunTests)
//   concurrent  races + generated concurrent cases via spec.AllowsConcurrent
//   lease       lease / timeout behavior, driven by the injected clock
//   races       targeted suspend‖settle repros + stranded-task liveness check
//   reorder     shuffled guided sequences replayed lockstep (divergence witness)
//   linz        free-form multi-client histories checked for linearizability
//   fuzz        continuous guided/random mix of sequential + concurrent ops
//   invariant-selftest  prove the server-bug detector actually fires
//   indefinite-selftest prove the optional indefinite-failure modeling works
//
// All modes share one spec + bound raw-HTTP adapter (Harness). No SDK.

var mode = args.Length > 0 ? args[0] : "all";
var baseUrl = Environment.GetEnvironmentVariable("RESONATE_URL") ?? "http://localhost:8001";
Console.WriteLine($"Resonate conformance model — {mode} — {baseUrl}\n");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
var harness = new Harness(http);

int rc = 0;
if (mode is "trace" or "all") rc |= await ExampleTraces.Run(harness);
if (mode is "gen" or "all") rc |= await GeneratedTests.RunSequential(harness);
if (mode is "concurrent" or "all") rc |= await ConcurrencyTests.Run(harness);
if (mode is "lease" or "all") rc |= await LeaseAndTimeoutTests.Run(harness);
if (mode is "poll" or "all") rc |= await PollTests.Run(harness);
if (mode is "races") rc |= await RaceRepros.Run(harness, args.Length > 1 ? int.Parse(args[1]) : 300);
if (mode is "all") rc |= await RaceRepros.Run(harness, 100);
if (mode is "reorder") rc |= await ReorderTests.Run(harness, args.Length > 1 ? int.Parse(args[1]) : 100);
if (mode is "all") rc |= await ReorderTests.Run(harness, 50);
if (mode is "linz") rc |= await HistoryTests.Run(harness, args.Length > 1 ? int.Parse(args[1]) : 10);
if (mode is "all") rc |= await HistoryTests.Run(harness, 5);
if (mode is "fuzz")
{
    int seed = args.Length > 1 ? int.Parse(args[1]) : 12345;
    int ops = args.Length > 2 ? int.Parse(args[2]) : 500;
    rc |= await new Fuzzer(harness, seed).Run(ops);
}
if (mode is "invariant-selftest") rc |= Invariants.SelfTest();
if (mode is "indefinite-selftest") rc |= IndefiniteFailureTests.Run();
return rc;
