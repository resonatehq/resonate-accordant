using Microsoft.Accordant;

namespace ResonateConformance;

public static partial class ResonateSpec
{    /// <summary>The ttl the harness passes for task.create (must match the adapter's binding).</summary>
    private const long TaskCreateTtl = 3_600_000;

    private static bool IsTimedOut(PromiseState p, long now) =>
        p.Status == "pending" && now >= p.TimeoutAt;

    private static (string State, string? Value, long? SettledAt) Project(PromiseState p, long now)
    {
        var v = p.Project(now);
        return (v.Status, v.Status == "pending" ? null : v.Value, v.SettledAt);
    }

    private static void SettleAndFulfillTask(ServerState s, string id, string state, string? data, long settledAt) =>
        s.SetSettled(id, state, data, settledAt);

    private static bool TagsMatch(Dictionary<string, string>? observed, PromiseState p)
    {
        var expected = p.HasTarget ? Harness.TargetTags
            : p.ExternalTag ? Harness.ExternalTags
            : [];
        if ((observed?.Count ?? 0) != expected.Count) return false;
        return expected.All(kv => observed!.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    /// <summary>
    /// An id's <b>origin</b>: everything before the first ':'. Ids have the
    /// form <c>&lt;promiseId&gt;:&lt;lineage&gt;</c>, so every promise a single
    /// root spawned shares one, and an id with no ':' is its own.
    /// </summary>
    private static string Origin(string id)
    {
        var i = id.IndexOf(':');
        return i < 0 ? id : id[..i];
    }

    /// <summary>
    /// Two ids belong to the same call graph. An await may only cross promises
    /// that do: the awaited is a promise the awaiter's own lineage produced,
    /// never an unrelated one, so this is a property of the REQUEST and is
    /// decided before any state is consulted.
    /// </summary>
    private static bool SameOrigin(string a, string b) => Origin(a) == Origin(b);

    private static bool PromiseMatches(Response r, string id, PromiseState p,
        string expectedState, string? expectedValue, long? expectedSettledAt)
    {
        var rec = r.PromiseRecord();
        return rec is not null
            && rec.Id == id
            && rec.State == expectedState
            && rec.Param?.AsString() == p.ParamData
            && rec.ValueField?.AsString() == expectedValue
            && rec.TimeoutAt == p.TimeoutAt
            && rec.CreatedAt == p.CreatedAt
            && rec.SettledAt == expectedSettledAt
            && TagsMatch(rec.Tags, p);
    }

    private static bool TaskMatches(Response r, string id,
        string expectedState, int expectedVersion, string? expectedPid, long? expectedTtl)
    {
        var rec = r.TaskRecord();
        return rec is not null
            && rec.Id == id
            && rec.State == expectedState
            && rec.Version == expectedVersion
            && rec.Pid == expectedPid
            && rec.Ttl == expectedTtl;
    }

    private static bool AddressValid(string addr) =>
        addr.StartsWith("http://") || addr.StartsWith("https://") ||
        (addr.StartsWith("poll://") && addr.Contains('@'));
}

public sealed class Handler<TRequest>(string name, Func<TRequest, ServerState, ExpectedOutcomes> body)
    : ServerOperation<TRequest, Response>(name)
{
    protected override ExpectedOutcomes ApplyInternal(TRequest request, ServerState state) =>
        body(request, state);
}

public abstract class ServerOperation<TRequest, TResponse> : Operation<TRequest, TResponse, ServerState>
{
    protected ServerOperation(string name) : base(name) { }

    protected abstract ExpectedOutcomes ApplyInternal(TRequest request, ServerState state);

    public sealed override ExpectedOutcomes Apply(TRequest request, ServerState state)
    {
        var definite = ApplyInternal(request, state);

        if (!IndefiniteFailures.Enabled)
            return definite;

        var baseHash = ((IState)state).GetStateHash();
        var outcomes = definite.PossibleOutcomes.ToList();
        foreach (var o in definite.PossibleOutcomes)
        {
            outcomes.Add(new ExpectedOutcome(IndefiniteValidator, (object _, IState s) => new StateList(new[] { s }), IndefiniteMock));

            var next = o.NextStateGenerator(null!, state).FirstOrDefault();
            if (next is not null && next.GetStateHash() != baseHash)
                outcomes.Add(new ExpectedOutcome(IndefiniteValidator, o.NextStateGenerator, IndefiniteMock));
        }

        return new ExpectedOutcomes(outcomes.ToArray());
    }

    private static readonly ResponseValidator IndefiniteValidator =
        ResponseValidator.FromPredicate<Response>(r => r.IsIndefiniteFailure()
            ? ValidationResult.Valid()
            : ValidationResult.Invalid($"expected an indefinite failure (lost response / 5xx), got status {r.Status}"));

    private static readonly Func<object> IndefiniteMock = () => Response.IndefiniteFailure();
}

public static class Capabilities
{
    private static readonly HashSet<string> Declared =
        (Environment.GetEnvironmentVariable("RESONATE_CAPS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Has(string capability) => Declared.Contains(capability);

    public static bool Poll => Has("poll");

    public static bool Egress => Has("egress");

    public static string Summary =>
        Declared.Count == 0 ? "(base protocol only)" : string.Join(",", Declared.OrderBy(c => c));
}

public static class IndefiniteFailures
{
    private static readonly AsyncLocal<bool?> _enabled = new();

    public static bool Enabled
    {
        get => _enabled.Value ?? false;
        set => _enabled.Value = value;
    }

    public static void Enable(Action action) => WithFlag(true, action);

    public static T Enable<T>(Func<T> func) => WithFlag(true, func);

    public static void Suppress(Action action) => WithFlag(false, action);

    public static T Suppress<T>(Func<T> func) => WithFlag(false, func);

    private static void WithFlag(bool value, Action action)
    {
        var previous = Enabled;
        Enabled = value;
        try { action(); }
        finally { Enabled = previous; }
    }

    private static T WithFlag<T>(bool value, Func<T> func)
    {
        var previous = Enabled;
        Enabled = value;
        try { return func(); }
        finally { Enabled = previous; }
    }
}
