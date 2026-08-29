using System.Text.Json;

namespace ResonateConformance;

public static class ResponseAccessors
{

    public static PromiseRecord? PromiseRecord(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("promise", out _)
            ? r.DataAs<PromiseEnvelope>()?.Promise
            : null;

    public static TaskRecord? TaskRecord(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("task", out _)
            ? r.DataAs<TaskAndPromiseEnvelope>()?.Task
            : null;

    public static List<ClaimedEntry> ClaimedTasks(this Response r) =>
        r.IsOk && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("tasks", out _)
            ? r.DataAs<TasksEnvelope>()?.Tasks ?? []
            : [];

    public static string? PromiseStatus(this Response r) => r.PromiseRecord()?.State;
    public static string? PromiseValue(this Response r) => r.PromiseRecord()?.ValueField?.AsString();
    public static string? TaskStatus(this Response r) => r.TaskRecord()?.State;
    public static int? TaskVersion(this Response r) => r.TaskRecord()?.Version;

    public static bool IsIndefiniteFailure(this Response r) =>
        r.Status == 0 || (r.Status >= 500 && r.Status < 600);
}

public static class Fake
{

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

    public static Response NotFound() =>
        new("promise.get", new ResponseHead("fake", 404, Protocol.Version),
            JsonSerializer.SerializeToElement("not found"));
}
