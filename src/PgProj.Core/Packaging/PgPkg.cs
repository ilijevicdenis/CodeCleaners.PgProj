using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PgProj.Core.Model;

namespace PgProj.Core.Packaging;

/// <summary>
/// A portable PgProj build artifact — the <c>.dacpac</c> analogue. A <c>.pgpkg</c> is a zip
/// containing:
/// <list type="bullet">
///   <item><c>manifest.json</c> — <see cref="PgPkgManifest"/> (name, pgVersion, toolVersion, createdUtc, sourceChecksum).</item>
///   <item><c>model.json</c> — the built <see cref="DatabaseModel"/>, byte-identical to the in-memory <see cref="ModelJson"/> serialization.</item>
///   <item><c>sources/&lt;relative path&gt;</c> — every original <c>.sql</c> source.</item>
///   <item><c>scripts/</c> — placeholder for pre/post-deploy scripts (EP-DEPLOYSCRIPTS).</item>
/// </list>
/// "Build once, deploy many": compare/publish/script/validate can load a package's model without
/// re-parsing the sources.
/// </summary>
public sealed class PgPkg
{
    public const string Extension = ".pgpkg";

    private const string ManifestEntry = "manifest.json";
    private const string ModelEntry = "model.json";
    private const string SourcesPrefix = "sources/";
    private const string ScriptsPrefix = "scripts/";
    private const string ScriptsPlaceholder = "scripts/.keep";

    public required PgPkgManifest Manifest { get; init; }
    public required DatabaseModel Model { get; init; }

    /// <summary>The original sources, keyed by their forward-slashed relative path.</summary>
    public required IReadOnlyList<PgPkgSource> Sources { get; init; }

    /// <summary>True if a file has the <c>.pgpkg</c> extension (case-insensitive).</summary>
    public static bool IsPackagePath(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assembles a package from a built model and its sources. <paramref name="manifest"/> carries the
    /// caller-injected stamp (createdUtc/toolVersion); its <c>sourceChecksum</c> must already be set by
    /// the caller (see <see cref="SourceChecksum"/>). The model is serialized with the same
    /// <see cref="ModelJson"/> path the loose <c>model.json</c> build output uses, so the embedded model
    /// is byte-identical.
    /// </summary>
    public static PgPkg Create(PgPkgManifest manifest, DatabaseModel model, IEnumerable<PgPkgSource> sources) =>
        new()
        {
            Manifest = manifest,
            Model = model,
            Sources = sources.OrderBy(s => s.RelativePath, StringComparer.Ordinal).ToList(),
        };

    /// <summary>Writes the package to <paramref name="path"/>, overwriting any existing file.</summary>
    public void Write(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(stream);
    }

    /// <summary>
    /// Writes the package to a stream. Entries are added in a fixed order with
    /// <see cref="CompressionLevel.SmallestSize"/> and a pinned last-write timestamp so that two builds
    /// of the same sources with the same injected manifest stamp are byte-identical.
    /// </summary>
    public void Write(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, ManifestEntry, Manifest.ToJson());
        WriteEntry(archive, ModelEntry, ModelJson.Serialize(Model));
        foreach (var src in Sources.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
            WriteEntry(archive, SourcesPrefix + src.RelativePath, src.Content);
        // Reserve the scripts/ folder for EP-DEPLOYSCRIPTS (pre/post scripts travel inside the package).
        WriteEntry(archive, ScriptsPlaceholder, string.Empty);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        // Pin the timestamp — zip stores per-entry mtime; without this the bytes drift build-to-build.
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        w.Write(content);
    }

    /// <summary>Reads and validates a package from disk.</summary>
    public static PgPkg Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Package not found: {path}", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return Read(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new PgPkgFormatException($"'{Path.GetFileName(path)}' is not a valid .pgpkg (corrupt zip): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads and validates a package from a stream. Verifies the embedded <c>sourceChecksum</c> against a
    /// freshly recomputed digest of the carried sources — a tampered package fails fast with a
    /// <see cref="PgPkgFormatException"/>.
    /// </summary>
    public static PgPkg Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestText = ReadRequired(archive, ManifestEntry);
        var modelText = ReadRequired(archive, ModelEntry);
        var manifest = PgPkgManifest.FromJson(manifestText);

        var sources = new List<PgPkgSource>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(SourcesPrefix, StringComparison.Ordinal)) continue;
            if (entry.FullName.EndsWith('/')) continue; // directory entry
            var rel = entry.FullName[SourcesPrefix.Length..];
            if (rel.Length == 0) continue;
            sources.Add(new PgPkgSource(rel, ReadEntry(entry)));
        }
        sources.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        // Integrity check: the carried sources must hash to the manifest's recorded checksum.
        var actual = SourceChecksum.Compute(sources.Select(s => (s.RelativePath, s.Content)));
        if (!string.Equals(actual, manifest.SourceChecksum, StringComparison.Ordinal))
            throw new PgPkgFormatException(
                $"Package integrity check failed: manifest sourceChecksum is '{manifest.SourceChecksum}' " +
                $"but the carried sources hash to '{actual}'. The package may be corrupt or tampered with.");

        var model = ModelJson.Deserialize(modelText);
        return new PgPkg { Manifest = manifest, Model = model, Sources = sources };
    }

    /// <summary>
    /// Loads only the manifest (cheap — does not decode the model or verify the checksum). Used by
    /// <c>pkg inspect</c> when the integrity result is reported separately.
    /// </summary>
    public static PgPkgManifest ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return PgPkgManifest.FromJson(ReadRequired(archive, ManifestEntry));
    }

    private static string ReadRequired(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
                    ?? throw new PgPkgFormatException($"Package is missing required entry '{name}'.");
        return ReadEntry(entry);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }
}

/// <summary>An original <c>.sql</c> source carried inside a package, keyed by forward-slashed relative path.</summary>
public sealed record PgPkgSource(string RelativePath, string Content);
