using System.Text.Json;

namespace ResonateConformance;

/// <summary>
/// Readable accessors for the client-visible fields of a server <see cref="Response"/>,
/// used inside operation predicates. The server returns one envelope shape for
/// every op (<c>{ kind, head:{ status }, data }</c>) where <c>data</c> is an
/// opaque blob; these project it into the promise/task fields we assert on.
/// </summary>
public static class ResponseAccessors
{
    /// <summary>The promise record on a success response (at <c>data.promise</c>), else null.</summary>
    public static PromiseRecord? PromiseRecord(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("promise", out _)
            ? r.DataAs<PromiseEnvelope>()?.Promise
            : null;

    /// <summary>The task record on a task.acquire / task.create success (at <c>data.task</c>), else null.</summary>
    public static TaskRecord? TaskRecord(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("task", out _)
            ? r.DataAs<TaskAndPromiseEnvelope>()?.Task
            : null;

    /// <summary>The claimed entries of a task.poll success (at <c>data.tasks</c>).</summary>
    public static List<ClaimedEntry> ClaimedTasks(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("tasks", out _)
            ? r.DataAs<TasksEnvelope>()?.Tasks ?? []
            : [];

    public static string? PromiseStatus(this Response r) => r.PromiseRecord()?.State;
    public static string? PromiseValue(this Response r) => r.PromiseRecord()?.ValueField?.AsString();
    public static string? TaskStatus(this Response r) => r.TaskRecord()?.State;
    public static int? TaskVersion(this Response r) => r.TaskRecord()?.Version;

    /// <summary>
    /// True when the outcome is <b>indefinite</b>: the response was lost or the
    /// server hit an internal error, so we cannot tell whether the request took
    /// effect. <see cref="Client.SendAsync"/> maps both real causes onto a
    /// synthetic status-0 response (see <see cref="Fake.IndefiniteFailure"/>):
    /// a transport failure (socket timeout / reset / cancellation) and any HTTP
    /// 5xx. A 5xx carried inside a valid envelope (status &gt;= 500) is also
    /// covered directly. The definite protocol codes the model asserts on
    /// (2xx/3xx/4xx) are never indefinite.
    /// </summary>
    public static bool IsIndefiniteFailure(this Response r) =>
        r.Status == 0 || (r.Status >= 500 && r.Status < 600);
}

/// <summary>
/// Factories for fabricated responses — used by negative tests and the invariant
/// self-test to feed the model responses the server must never actually return.
/// </summary>
public static class Fake
{
    /// <summary>A 200 promise response with a chosen state and value.</summary>
    public static Response Promise(string id, string state, string? data)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            promise = new
            {
                id,
                state,
                param = new { headers = new { }, data = "" },
                value = new { headers = new { }, data = data ?? "" },
                tags = new { },
                timeoutAt = 0L,
                createdAt = 0L,
            },
        });
        return new Response("promise.get", new ResponseHead("fake", 200, Protocol.Version), json);
    }

    /// <summary>A 404 (absent) response — error string in <c>data</c>, as the server returns.</summary>
    public static Response NotFound() =>
        new("promise.get", new ResponseHead("fake", 404, Protocol.Version),
            JsonSerializer.SerializeToElement("not found"));
}
