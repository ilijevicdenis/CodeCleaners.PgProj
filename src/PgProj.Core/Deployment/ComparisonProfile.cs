using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core.Comparison;

namespace PgProj.Core.Deployment;

/// <summary>
/// A reusable, committable <c>.pgcompare.json</c> profile capturing the <em>comparison-equivalence</em>
/// options (Phase 18, issue #58) — what counts as a difference when diffing a source against a target.
/// Deliberately SEPARATE from <see cref="PublishProfile"/> (which carries publish/connection concerns):
/// a team pins "how we compare" once and reuses it across every target, so a profile + project + target
/// yields a deterministic result.
/// </summary>
/// <remarks>
/// <para>
/// JSON shape is camelCase; unknown members are ignored on load (forward-compat) and a malformed document
/// throws <see cref="ComparisonProfileException"/>. Like <see cref="PublishProfile"/>, this is Core code:
/// no clock, no timestamp — identical inputs serialize byte-identically.
/// </para>
/// <para>
/// Every field defaults to the <b>behaviour-preserving</b> value, so an empty/absent profile reproduces
/// today's comparison exactly. <see cref="ToComparerOptions"/> projects the profile onto a live
/// <see cref="ComparerOptions"/>.
/// </para>
/// </remarks>
public sealed record ComparisonProfile
{
    /// <summary>The conventional file extension for a comparison profile.</summary>
    public const string Extension = ".pgcompare.json";

    /// <summary>Drop objects present in the target but absent from the source. Off by default.</summary>
    public bool DropObjectsNotInSource { get; init; }

    /// <summary>Treat a pure column reorder as equal (ignore non-semantic column order). Off by default.</summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>Ignore physical storage params (fillfactor/tablespace). On by default (today's behaviour).</summary>
    public bool IgnoreStorageParameters { get; init; } = true;

    /// <summary>Make identifier comparison case-sensitive (quoted-identifier sensitivity). Off by default.</summary>
    public bool CaseSensitiveIdentifiers { get; init; }

    /// <summary>Projects this profile onto a live <see cref="ComparerOptions"/>.</summary>
    public ComparerOptions ToComparerOptions() => new()
    {
        DropObjectsNotInSource = DropObjectsNotInSource,
        IgnoreColumnOrder = IgnoreColumnOrder,
        IgnoreStorageParameters = IgnoreStorageParameters,
        CaseSensitiveIdentifiers = CaseSensitiveIdentifiers,
    };

    /// <summary>Builds a profile from a live <see cref="ComparerOptions"/> (the inverse of <see cref="ToComparerOptions"/>).</summary>
    public static ComparisonProfile FromComparerOptions(ComparerOptions options) => new()
    {
        DropObjectsNotInSource = options.DropObjectsNotInSource,
        IgnoreColumnOrder = options.IgnoreColumnOrder,
        IgnoreStorageParameters = options.IgnoreStorageParameters,
        CaseSensitiveIdentifiers = options.CaseSensitiveIdentifiers,
    };

    // ---- (de)serialization -------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never, // booleans: emit all so the profile is self-documenting
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>True when the path looks like a comparison profile (the <c>.pgcompare.json</c> suffix).</summary>
    public static bool IsProfilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Serializes this profile to its canonical JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Writes the profile to <paramref name="path"/> (creating the directory if needed).</summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ComparisonProfileException("A profile output path is required.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Parses a profile from JSON text. Throws <see cref="ComparisonProfileException"/> on malformed JSON.</summary>
    public static ComparisonProfile Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ComparisonProfile();
        try
        {
            return JsonSerializer.Deserialize<ComparisonProfile>(json, Json) ?? new ComparisonProfile();
        }
        catch (JsonException ex)
        {
            throw new ComparisonProfileException($"Malformed comparison profile: {ex.Message}", ex);
        }
    }

    /// <summary>Loads a profile from disk. Throws <see cref="ComparisonProfileException"/> if missing or malformed.</summary>
    public static ComparisonProfile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ComparisonProfileException("A profile path is required.");
        if (!File.Exists(path))
            throw new ComparisonProfileException($"Comparison profile not found: {path}");
        return Parse(File.ReadAllText(path));
    }
}

/// <summary>Thrown when a comparison profile is missing or its JSON cannot be parsed.</summary>
public sealed class ComparisonProfileException : Exception
{
    public ComparisonProfileException(string message) : base(message) { }
    public ComparisonProfileException(string message, Exception inner) : base(message, inner) { }
}
