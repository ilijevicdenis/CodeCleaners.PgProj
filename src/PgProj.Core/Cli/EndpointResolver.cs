using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Snapshot;

namespace PgProj.Core.Cli;

/// <summary>What kind of thing a source/target spec resolved to.</summary>
public enum EndpointKind
{
    /// <summary>A <c>.pgproj</c> project, built into a model on resolution.</summary>
    Project,

    /// <summary>A portable <c>.pgpkg</c> package; its embedded model is loaded (no re-parse).</summary>
    Package,

    /// <summary>A live PostgreSQL database, introspected via its connection string.</summary>
    LiveDatabase,

    /// <summary>A <c>.schema.snapshot</c> capture of a database; its model is loaded offline (no DB connection).</summary>
    Snapshot,
}

/// <summary>The outcome of resolving a source/target spec to a comparable model.</summary>
/// <param name="Kind">How the spec was classified.</param>
/// <param name="Model">The resolved schema model (the unit the comparer diffs).</param>
/// <param name="Project">The loaded project — non-null only for <see cref="EndpointKind.Project"/>.</param>
/// <param name="DisplayName">A human label for banners/reports (project/package name, or "(database)").</param>
/// <param name="BuildDiagnostics">Build problems for a <see cref="EndpointKind.Project"/> source (empty otherwise).</param>
/// <param name="SnapshotManifest">The snapshot's manifest — non-null only for <see cref="EndpointKind.Snapshot"/>
/// (carries the captured source PG version / format version used for staleness checks).</param>
public sealed record ResolvedEndpoint(
    EndpointKind Kind,
    DatabaseModel Model,
    DatabaseProject? Project,
    string DisplayName,
    IReadOnlyList<string> BuildDiagnostics,
    SchemaSnapshotManifest? SnapshotManifest = null);

/// <summary>
/// Resolves a single source/target <em>spec</em> — a <c>.pgproj</c> path, a <c>.pgpkg</c> path, or a
/// connection string — to a comparable <see cref="DatabaseModel"/>. This is the shared primitive behind
/// every verb that loads "a model from somewhere": <c>compare</c>/<c>publish</c>/<c>script</c>/
/// <c>validate</c> resolve a source, and a two-way Schema Compare (EP-SCHEMACOMPARE) resolves both a
/// source <em>and</em> a target through the very same path, so the matrix
/// {project, package, live} × {project, package, live} is one code path, not nine.
/// </summary>
/// <remarks>
/// Deliberately <b>console-free and policy-free</b>: it loads models, nothing more. CLI concerns layered
/// on top — the static-analysis gate, reference resolution, <c>--substitute-objects</c>, printing — stay
/// in the verb. Classification is purely syntactic, with one safety rule: an <em>existing file</em> is
/// never treated as a connection string.
/// </remarks>
public static class EndpointResolver
{
    /// <summary>
    /// Classifies a spec without touching it: a <c>*.schema.snapshot</c> → <see cref="EndpointKind.Snapshot"/>;
    /// a <c>*.pgpkg</c> → <see cref="EndpointKind.Package"/>; a <c>*.pgproj</c> (by extension, or any existing
    /// file) → <see cref="EndpointKind.Project"/>; anything else → <see cref="EndpointKind.LiveDatabase"/>
    /// (a connection string).
    /// </summary>
    public static EndpointKind Classify(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new CliUsageException("Expected a source/target (a .pgproj, .pgpkg, .schema.snapshot, or a connection string).");

        // The compound .schema.snapshot suffix is checked before the generic File.Exists → Project rule.
        if (SchemaSnapshot.IsSnapshotPath(spec)) return EndpointKind.Snapshot;
        if (PgPkg.IsPackagePath(spec)) return EndpointKind.Package;
        if (spec.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase)) return EndpointKind.Project;
        // A real file on disk is never a connection string (covers extension-less or oddly-named projects).
        if (File.Exists(spec)) return EndpointKind.Project;
        return EndpointKind.LiveDatabase;
    }

    /// <summary>
    /// Resolves <paramref name="spec"/> to a model. A project is built (parallel build); a package's
    /// embedded model is loaded (integrity-checked on read); a connection string is introspected.
    /// </summary>
    public static async Task<ResolvedEndpoint> ResolveAsync(string spec, CancellationToken ct = default)
    {
        switch (Classify(spec))
        {
            case EndpointKind.Package:
            {
                var pkg = PgPkg.Read(Path.GetFullPath(spec));   // verifies the integrity checksum on read
                return new ResolvedEndpoint(EndpointKind.Package, pkg.Model, null, pkg.Manifest.Name, Array.Empty<string>());
            }
            case EndpointKind.Snapshot:
            {
                // Offline: the snapshot's model is loaded straight from the file (integrity-checked on read).
                // NO database connection is made on the compare step — this is the whole point of a snapshot.
                var snap = SchemaSnapshot.Read(Path.GetFullPath(spec));
                var display = snap.Manifest.SourceName is { Length: > 0 } n ? n : "(snapshot)";
                return new ResolvedEndpoint(EndpointKind.Snapshot, snap.Model, null, display,
                    Array.Empty<string>(), snap.Manifest);
            }
            case EndpointKind.Project:
            {
                var project = DatabaseProject.Load(spec);
                var build = await project.BuildAsync(ct);
                return new ResolvedEndpoint(EndpointKind.Project, build.Model, project, project.Name, build.Diagnostics);
            }
            default:
            {
                var model = await new LiveDatabaseReader().ReadAsync(spec, ct);
                return new ResolvedEndpoint(EndpointKind.LiveDatabase, model, null, "(database)", Array.Empty<string>());
            }
        }
    }
}
