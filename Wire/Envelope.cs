using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResonateConformance;

/// <summary>
/// Wire-level DTOs for the Resonate server RPC protocol.
///
/// The server speaks a single `POST /` endpoint with a `kind`-tagged JSON
/// envelope. Shapes here are transcribed from the SDK's authoritative
/// `network/types.d.ts` (v0.10.2) and verified against the live v0.9.8 server.
///
/// We deliberately keep `data` loosely typed on the response side (a
/// JsonElement) so a single Client can round-trip every op without a giant
/// discriminated union; helpers in Records.cs project it into typed records.
/// </summary>
public static class Protocol
{
    public const string Version = "2026-04-01";
}

/// <summary>Request head. corrId is a per-call uuid; version pins the protocol.</summary>
public sealed record RequestHead(
    [property: JsonPropertyName("corrId")] string CorrId,
    [property: JsonPropertyName("version")] string Version)
{
    /// <summary>Optional injected `now` (epoch ms) for deterministic timeout tests.</summary>
    [JsonPropertyName("resonate:debug_time")]
    public long? DebugTime { get; init; }

    [JsonPropertyName("resonate:origin")]
    public string? Origin { get; init; }
}

/// <summary>A generic request envelope: { kind, head, data }.</summary>
public sealed record Envelope<TData>(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("head")] RequestHead Head,
    [property: JsonPropertyName("data")] TData Data);

/// <summary>Response head as returned by the server. status is the result code.</summary>
public sealed record ResponseHead(
    [property: JsonPropertyName("corrId")] string CorrId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// A parsed response. `Kind` and `Head` are always present; `Data` is the raw
/// JSON of the `data` field (an object on success, a bare string on error).
/// </summary>
public sealed record Response(string Kind, ResponseHead Head, JsonElement Data)
{
    public int Status => Head.Status;
    public bool IsOk => Status == 200;

    /// <summary>Error responses put a human string in `data`; success puts an object.</summary>
    public string? ErrorMessage =>
        Data.ValueKind == JsonValueKind.String ? Data.GetString() : null;

    public T? DataAs<T>() =>
        Data.ValueKind == JsonValueKind.Object || Data.ValueKind == JsonValueKind.Array
            ? Data.Deserialize<T>(Json.Options)
            : default;

    /// <summary>
    /// An <b>indefinite failure</b>: the request left but no usable reply came back
    /// (transport failure, or a body-less 5xx), so we cannot tell whether the
    /// server applied it. A real outcome the client synthesizes at runtime — not a
    /// fabrication. Carried as synthetic status 0;
    /// <see cref="ResponseAccessors.IsIndefiniteFailure"/> recognizes it.
    /// </summary>
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
