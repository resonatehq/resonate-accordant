using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResonateConformance;

/// <summary>A promise param/value: { headers, data }. data is opaque (any JSON).</summary>
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

/// <summary>The server's canonical promise record (mirrors PromiseRecord in the SDK).</summary>
public sealed record PromiseRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("param")] Value? Param,
    [property: JsonPropertyName("value")] Value? ValueField,
    [property: JsonPropertyName("tags")] Dictionary<string, string> Tags,
    [property: JsonPropertyName("timeoutAt")] long TimeoutAt,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("settledAt")] long? SettledAt);

/// <summary>The server's canonical task record (mirrors TaskRecord in the SDK).</summary>
public sealed record TaskRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("ttl")] long? Ttl,
    [property: JsonPropertyName("pid")] string? Pid);

// --- data payloads keyed under `data.<field>` of a 200 response ---

/// <summary>One claimed entry of a task.poll response.</summary>
public sealed record ClaimedEntry(
    [property: JsonPropertyName("task")] TaskRecord? Task,
    [property: JsonPropertyName("promise")] PromiseRecord? Promise);

/// <summary>task.poll success payload: { tasks: [ {task, promise} ] }.</summary>
public sealed record TasksEnvelope(
    [property: JsonPropertyName("tasks")] List<ClaimedEntry>? Tasks);

public sealed record PromiseEnvelope(
    [property: JsonPropertyName("promise")] PromiseRecord Promise);

public sealed record TaskAndPromiseEnvelope(
    [property: JsonPropertyName("task")] TaskRecord? Task,
    [property: JsonPropertyName("promise")] PromiseRecord? Promise,
    [property: JsonPropertyName("preload")] List<PromiseRecord>? Preload);
