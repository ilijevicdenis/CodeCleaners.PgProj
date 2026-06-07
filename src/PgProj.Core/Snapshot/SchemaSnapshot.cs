using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core.Model;
using PgProj.Core.Versioning;

namespace PgProj.Core.Snapshot;

/// <summary>
/// A <c>.schema.snapshot</c> artifact — a portable, point-in-time capture of the canonical
/// <see cref="DatabaseModel"/> of a <em>live database</em>. It lets <c>compare</c> run against a database's
/// schema without re-introspecting it every time (large DBs; offline / CI compare). Distinct from a
/// <c>.pgpkg</c> (which is a build artifact of a <em>project</em>): a snapshot captures a <em>database</em>.
/// <para>
/// On disk it is a single UTF-8 JSON document with two parts: a <see cref="SchemaSnapshotManifest"/>
/// header (format version + source PG version + created stamp + a checksum) and the model payload,
/// serialized verbatim with <see cref="ModelJson"/> and carried as an embedded JSON string so the
/// snapshot's model is byte-identical to the loose <c>model.json</c>. The <see cref="SchemaSnapshotManifest.ModelChecksum"/>
/// is a SHA-256 over that exact payload, verified on read.
/// </para>
/// </summary>
public sealed class SchemaSnapshot
{
    /// <summary>The conventional file extension/suffix for a snapshot artifact.</summary>
    public const string Extension = ".schema.snapshot";

    /// <summary>The current snapshot format version, bumped on a breaking layout change.</summary>
    public const string CurrentFormatVersion = "1.0";

    public required SchemaSnapshotManifest Manifest { get; init; }
    public required DatabaseModel Model { get; init; }

    /// <summary>True if a path carries the <c>.schema.snapshot</c> suffix (case-insensitive).</summary>
    public static bool IsSnapshotPath(string path) =>
        path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assembles a snapshot from an introspected model and the volatile, caller-injected stamp fields
    /// (<paramref name="toolVersion"/>, <paramref name="createdUtc"/>). The model checksum is computed
    /// here over the canonical <see cref="ModelJson"/> payload — the same bytes <see cref="Write(string)"/>
    /// emits — so two captures of an identical model with identical stamps are byte-identical.
    /// </summary>
    public static SchemaSnapshot Create(
        DatabaseModel model,
        string sourcePgVersion,
        int sourcePgMajorVersion,
        string toolVersion,
        string createdUtc,
        string? sourceName = null)
    {
        var modelJson = ModelJson.Serialize(model);
        var manifest = new SchemaSnapshotManifest(
            SourcePgVersion: sourcePgVersion,
            SourcePgMajorVersion: sourcePgMajorVersion,
            ToolVersion: toolVersion,
            CreatedUtc: createdUtc,
            ModelChecksum: ChecksumOf(modelJson))
        {
            SourceName = sourceName,
        };
        return new SchemaSnapshot { Manifest = manifest, Model = model };
    }

    /// <summary>Writes the snapshot to <paramref name="path"/>, overwriting any existing file.</summary>
    public void Write(string path) => File.WriteAllText(path, ToJson(), Utf8NoBom);

    /// <summary>Serializes the snapshot to its on-disk JSON form (manifest + embedded model payload).</summary>
    public string ToJson()
    {
        var modelJson = ModelJson.Serialize(Model);
        var doc = new SnapshotDocument(Manifest, modelJson);
        return JsonSerializer.Serialize(doc, DocumentOptions);
    }

    /// <summary>Reads and validates a snapshot from disk.</summary>
    public static SchemaSnapshot Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Snapshot not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses and validates a snapshot from its JSON text. Verifies the embedded <c>modelChecksum</c>
    /// against a freshly recomputed digest of the carried model payload — a tampered/corrupt snapshot
    /// fails fast with a <see cref="SchemaSnapshotFormatException"/>.
    /// </summary>
    public static SchemaSnapshot Parse(string json)
    {
        SnapshotDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<SnapshotDocument>(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new SchemaSnapshotFormatException($"Not a valid .schema.snapshot (malformed JSON): {ex.Message}", ex);
        }

        if (doc is null || doc.Manifest is null || doc.Model is null)
            throw new SchemaSnapshotFormatException("Snapshot is missing its manifest or model payload.");

        var actual = ChecksumOf(doc.Model);
        if (!string.Equals(actual, doc.Manifest.ModelChecksum, StringComparison.Ordinal))
            throw new SchemaSnapshotFormatException(
                $"Snapshot integrity check failed: manifest modelChecksum is '{doc.Manifest.ModelChecksum}' " +
                $"but the carried model hashes to '{actual}'. The snapshot may be corrupt or tampered with.");

        var model = ModelJson.Deserialize(doc.Model);
        return new SchemaSnapshot { Manifest = doc.Manifest, Model = model };
    }

    /// <summary>Loads only the manifest (cheap — does not decode the model or verify the checksum).</summary>
    public static SchemaSnapshotManifest ReadManifest(string path)
    {
        var doc = JsonSerializer.Deserialize<SnapshotDocument>(File.ReadAllText(path), DocumentOptions)
                  ?? throw new SchemaSnapshotFormatException("Snapshot is empty or invalid.");
        return doc.Manifest
               ?? throw new SchemaSnapshotFormatException("Snapshot is missing its manifest.");
    }

    /// <summary>
    /// Evaluates whether this snapshot is stale for a consumer expecting a given PostgreSQL major version
    /// (e.g. a project's <c>TargetPostgresVersion</c>). Stale when (a) the snapshot's
    /// <see cref="SchemaSnapshotManifest.FormatVersion"/> is not the one this build understands, or (b) a
    /// non-null <paramref name="expectedMajorVersion"/> differs from the snapshot's captured source major
    /// version. A null <paramref name="expectedMajorVersion"/> skips the version check (format check still runs).
    /// </summary>
    public SchemaSnapshotStaleness CheckStaleness(int? expectedMajorVersion)
    {
        var reasons = new System.Collections.Generic.List<string>();

        if (!string.Equals(Manifest.FormatVersion, CurrentFormatVersion, StringComparison.Ordinal))
            reasons.Add(
                $"snapshot format version '{Manifest.FormatVersion}' is not the version this tool understands " +
                $"('{CurrentFormatVersion}') — re-capture the snapshot.");

        if (expectedMajorVersion is { } expected && expected != Manifest.SourcePgMajorVersion)
            reasons.Add(
                $"snapshot was captured from PostgreSQL {Manifest.SourcePgMajorVersion} " +
                $"but the comparison expects PostgreSQL {expected} — re-capture against a PostgreSQL {expected} server.");

        return SchemaSnapshotStaleness.From(reasons);
    }

    /// <summary>Convenience overload: staleness against a <see cref="PostgresVersionProfile"/>'s major version.</summary>
    public SchemaSnapshotStaleness CheckStaleness(PostgresVersionProfile profile) =>
        CheckStaleness(profile?.MajorVersion);

    // ---- internals ----------------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string ChecksumOf(string modelJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(modelJson));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>The on-disk shape: the manifest header plus the model carried as an embedded JSON string.</summary>
    private sealed record SnapshotDocument(
        [property: JsonPropertyName("manifest")] SchemaSnapshotManifest Manifest,
        [property: JsonPropertyName("model")] string Model);
}
