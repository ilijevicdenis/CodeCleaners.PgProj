using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Core.Contracts;

/// <summary>
/// The editor-backend JSON contract (epic EP-RPC). Every <c>pgproj &lt;verb&gt; --format json</c> payload
/// is one of the DTOs in this namespace, each carrying a top-level <see cref="DiagnosticDto"/> stream
/// and/or a <c>schemaVersion</c>. This is the single stable surface a VS Code / VS extension binds to;
/// keep it serialization-stable (explicit property names, no leaked internal enums) and bump
/// <see cref="SchemaVersion"/> only on a deliberate, documented change.
/// </summary>
public static class JsonContract
{
    /// <summary>
    /// The contract version. Stamped onto every payload as <c>schemaVersion</c>. Semantics: additive,
    /// backwards-compatible changes (new optional fields) keep the major; a breaking change (renamed or
    /// removed field, changed meaning) bumps it. Editors should refuse a major they do not understand.
    /// </summary>
    public const string SchemaVersion = "1.0";

    /// <summary>
    /// The canonical serializer for every contract payload: camelCase, omit-null, non-indented by default
    /// (an editor parses it; a human can pretty-print). Enums are emitted as their declared string names
    /// (never integers) so the wire format is stable against enum reordering.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialize a contract payload to its stable JSON form.</summary>
    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);
}
