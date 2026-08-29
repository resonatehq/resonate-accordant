using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResonateConformance;

public sealed record Value(
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
    [property: JsonPropertyName("data")] JsonElement? Data)
{
    public static Value Of(string? data) => new(
        new Dictionary<string, string>(),
        data is null ? null : JsonSerializer.SerializeToElement(data));

    public string? AsString() =>
        Data is { ValueKind: JsonValueKind.String } d ? d.GetString() : Data?.ToString();
}

public sealed record PromiseRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("param")] Value? Param,
    [property: JsonPropertyName("value")] Value? ValueField,
    [property: JsonPropertyName("tags")] Dictionary<string, string> Tags,
    [property: JsonPropertyName("timeoutAt")] long TimeoutAt,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("settledAt")] long? SettledAt);

public sealed record TaskRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("ttl")] long? Ttl,
    [property: JsonPropertyName("pid")] string? Pid);

public sealed record ClaimedEntry(
    [property: JsonPropertyName("task")] TaskRecord? Task,
    [property: JsonPropertyName("promise")] PromiseRecord? Promise);

public sealed record TasksEnvelope(
    [property: JsonPropertyName("tasks")] List<ClaimedEntry>? Tasks);

public sealed record PromiseEnvelope(
    [property: JsonPropertyName("promise")] PromiseRecord Promise);

public sealed record TaskAndPromiseEnvelope(
    [property: JsonPropertyName("task")] TaskRecord? Task,
    [property: JsonPropertyName("promise")] PromiseRecord? Promise,
    [property: JsonPropertyName("preload")] List<PromiseRecord>? Preload);
