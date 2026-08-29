using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResonateConformance;

public static class Protocol
{
    public const string Version = "2026-04-01";
}

public sealed record RequestHead(
    [property: JsonPropertyName("corrId")] string CorrId,
    [property: JsonPropertyName("version")] string Version)
{

    [JsonPropertyName("resonate:debug_time")]
    public long? DebugTime { get; init; }

    [JsonPropertyName("resonate:origin")]
    public string? Origin { get; init; }
}

public sealed record Envelope<TData>(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("head")] RequestHead Head,
    [property: JsonPropertyName("data")] TData Data);

public sealed record ResponseHead(
    [property: JsonPropertyName("corrId")] string CorrId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("version")] string Version);

public sealed record Response(string Kind, ResponseHead Head, JsonElement Data)
{
    public int Status => Head.Status;
    public bool IsOk => Status == 200;

    public string? ErrorMessage =>
        Data.ValueKind == JsonValueKind.String ? Data.GetString() : null;

    public T? DataAs<T>() =>
        Data.ValueKind == JsonValueKind.Object || Data.ValueKind == JsonValueKind.Array
            ? Data.Deserialize<T>(Json.Options)
            : default;

    public static Response IndefiniteFailure() =>
        new("indefinite", new ResponseHead("indefinite", 0, Protocol.Version),
            JsonSerializer.SerializeToElement("indefinite failure: response lost"));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
