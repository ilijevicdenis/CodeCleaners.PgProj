using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Core.Deployment;

/// <summary>
/// The publish-options half of a <see cref="PublishProfile"/> — the SSDT *publish properties* analogue.
/// Each is nullable so "absent in the profile" is distinguishable from "explicitly set", letting the CLI
/// apply precedence (CLI &gt; profile &gt; built-in default) without a profile silently re-asserting a
/// default the user never wrote.
/// </summary>
public sealed record PublishProfileOptions
{
    /// <summary>Allow destructive changes (drop objects not in the source) — the <c>--allow-drops</c> flag.</summary>
    public bool? AllowDrops { get; init; }

    /// <summary>Wrap the whole deploy in one BEGIN/COMMIT — the inverse of the <c>--no-transaction</c> flag.</summary>
    public bool? WrapInTransaction { get; init; }
}

/// <summary>
/// A reusable <c>.pgpublish.json</c> publish profile: the SSDT publish-profile (<c>.publish.xml</c>)
/// analogue. It captures everything needed to repeat a publish to one environment <em>except the secret</em>:
/// a target PostgreSQL version, SQLCMD-variable overrides, publish options (allow-drops, transactional),
/// and an optional <em>non-secret</em> connection name/hint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secrets rule (hard):</b> the connection string is NEVER persisted in a profile. The live target comes
/// from <c>--connection</c> / <c>PGPROJ_CONNECTION</c> at run time; <see cref="ConnectionName"/> may carry a
/// non-secret label/hint (e.g. <c>"prod"</c>) only. <see cref="Save"/> writes only the whitelisted fields,
/// and <see cref="Load"/> ignores any stray <c>connection</c>/<c>connectionString</c>/<c>password</c> key, so
/// a leaked secret can neither be written by this type nor resurrected when the profile is re-read.
/// </para>
/// <para>
/// <b>Determinism:</b> this is Core code, so it stamps no timestamp and reads no clock — a profile written
/// from identical inputs is byte-identical. Any "createdUtc"-style metadata must be injected at the CLI
/// boundary, never here.
/// </para>
/// <para>
/// JSON shape is camelCase (matching the editor contract); unknown fields are ignored on load so a newer
/// profile stays loadable by an older tool. A malformed document throws <see cref="PublishProfileException"/>.
/// </para>
/// </remarks>
public sealed record PublishProfile
{
    /// <summary>The conventional file extension for a publish profile.</summary>
    public const string Extension = ".pgpublish.json";

    /// <summary>
    /// Target PostgreSQL major version (e.g. <c>"16"</c>/<c>"17"</c>/<c>"18"</c>), or null when the profile
    /// does not pin one (the project's <c>TargetPostgresVersion</c> then stands).
    /// </summary>
    public string? TargetPostgresVersion { get; init; }

    /// <summary>
    /// A <em>non-secret</em> connection name/hint (e.g. an environment label). NEVER a connection string —
    /// the secret comes from <c>--connection</c>/<c>PGPROJ_CONNECTION</c> at run time. Informational only.
    /// </summary>
    public string? ConnectionName { get; init; }

    /// <summary>
    /// SQLCMD-variable overrides applied above the project defaults and below CLI <c>--var</c>. Names are
    /// case-insensitive (SQLCMD semantics); fed verbatim into <see cref="SqlCmdVariableResolver.Build"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publish options (allow-drops, transactional). Each sub-option is null when unset by the profile.</summary>
    public PublishProfileOptions Options { get; init; } = new();

    // ---- (de)serialization -------------------------------------------------------------------

    /// <summary>
    /// camelCase, omit-null, indented (a human edits these) — and crucially case-insensitive on read so a
    /// hand-edited <c>TargetPostgresVersion</c> still binds. Unknown members are ignored (forward-compat).
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // No UnmappedMemberHandling.Disallow → unknown JSON members are ignored, not an error.
    };

    /// <summary>True when this path looks like a publish profile (the <c>.pgpublish.json</c> suffix).</summary>
    public static bool IsProfilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes this profile to its canonical JSON. Only the whitelisted, non-secret fields are emitted —
    /// there is no code path that can write a connection string, so a profile is safe to commit.
    /// </summary>
    public string ToJson()
    {
        // Project to a DTO whose ONLY connection-ish field is the non-secret name — defense in depth against
        // ever serializing a secret, independent of what callers stuff onto the record.
        var dto = new ProfileDto
        {
            TargetPostgresVersion = TargetPostgresVersion,
            ConnectionName = ConnectionName,
            Variables = Variables.Count == 0
                ? null
                : Variables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal),
            Options = Options is { AllowDrops: null, WrapInTransaction: null } ? null : Options,
        };
        return JsonSerializer.Serialize(dto, Json);
    }

    /// <summary>Writes the profile to <paramref name="path"/> (creating the directory if needed).</summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PublishProfileException("A profile output path is required.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Parses a profile from JSON text. Throws <see cref="PublishProfileException"/> on malformed JSON.</summary>
    public static PublishProfile Parse(string json)
    {
        // An absent/blank document is a valid, all-defaults profile (System.Text.Json treats "" as malformed).
        if (string.IsNullOrWhiteSpace(json))
            return new PublishProfile();

        ProfileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProfileDto>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new PublishProfileException($"Malformed publish profile: {ex.Message}", ex);
        }

        // An empty document ("{}", "", "null") is a valid, all-defaults profile.
        dto ??= new ProfileDto();

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dto.Variables is not null)
            foreach (var kv in dto.Variables)
                vars[kv.Key] = kv.Value;

        return new PublishProfile
        {
            TargetPostgresVersion = string.IsNullOrWhiteSpace(dto.TargetPostgresVersion) ? null : dto.TargetPostgresVersion,
            ConnectionName = string.IsNullOrWhiteSpace(dto.ConnectionName) ? null : dto.ConnectionName,
            Variables = vars,
            Options = dto.Options ?? new PublishProfileOptions(),
        };
    }

    /// <summary>Loads a profile from disk. Throws <see cref="PublishProfileException"/> if the file is missing or malformed.</summary>
    public static PublishProfile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PublishProfileException("A profile path is required.");
        if (!File.Exists(path))
            throw new PublishProfileException($"Publish profile not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// The wire DTO — the single source of truth for the JSON shape. Deliberately carries NO connection-string
    /// member: a secret has nowhere to land on serialize, and any <c>connection</c>/<c>password</c> key in an
    /// input file maps to no member and is dropped by the (unknown-members-ignored) deserializer on read.
    /// </summary>
    private sealed class ProfileDto
    {
        public string? TargetPostgresVersion { get; set; }
        public string? ConnectionName { get; set; }
        public Dictionary<string, string>? Variables { get; set; }
        public PublishProfileOptions? Options { get; set; }
    }
}

/// <summary>Thrown when a publish profile is missing or its JSON cannot be parsed.</summary>
public sealed class PublishProfileException : Exception
{
    public PublishProfileException(string message) : base(message) { }
    public PublishProfileException(string message, Exception inner) : base(message, inner) { }
}
