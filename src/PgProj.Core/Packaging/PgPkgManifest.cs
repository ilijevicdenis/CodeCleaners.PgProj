using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Core.Packaging;

/// <summary>
/// The self-describing header of a <c>.pgpkg</c> package — the analogue of a <c>.dacpac</c>'s
/// <c>Origin.xml</c>. It carries enough to detect build/deploy drift without opening the model:
/// the logical <see cref="Name"/>, the target <see cref="PgVersion"/>, the <see cref="ToolVersion"/>
/// that built it, the <see cref="CreatedUtc"/> stamp, and a <see cref="SourceChecksum"/> over the
/// normalized concatenation of every <c>.sql</c> source.
/// </summary>
/// <remarks>
/// <see cref="CreatedUtc"/> and <see cref="ToolVersion"/> are <em>injected by the caller</em> (the CLI),
/// never read from <c>DateTime.Now</c> inside deterministic build code — so two builds of the same
/// sources with the same injected stamp produce byte-identical packages.
/// </remarks>
public sealed record PgPkgManifest(
    string Name,
    string? PgVersion,
    string ToolVersion,
    string CreatedUtc,
    string SourceChecksum)
{
    /// <summary>The current package format version, bumped on breaking layout changes.</summary>
    public string FormatVersion { get; init; } = "1.0";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static PgPkgManifest FromJson(string json) =>
        JsonSerializer.Deserialize<PgPkgManifest>(json, Options)
        ?? throw new PgPkgFormatException("manifest.json is empty or invalid.");
}
