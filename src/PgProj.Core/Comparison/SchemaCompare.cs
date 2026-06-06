using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PgProj.Core.Cli;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// The single, two-way Schema Compare entry point (EP-SCHEMACOMPARE). It unifies what used to be two
/// fixed-direction verbs — <c>compare</c> (project→DB) and <c>drift</c> (DB→project) — into one operation
/// where the <em>source</em> and the <em>target</em> are each any endpoint: a <c>.pgproj</c> project, a
/// portable <c>.pgpkg</c> package, or a live PostgreSQL database. Resolution of every endpoint goes through
/// the shared <see cref="EndpointResolver"/>, so the full {project, package, live} × {project, package,
/// live} matrix is one code path, not nine.
/// </summary>
/// <remarks>
/// The result is a <see cref="SchemaChangeSet"/>: a structured, selectable diff. "Compare A to B" means
/// "the changes that would bring B (target) in line with A (source)"; swapping the two specs flips the
/// direction, which is all "apply in either direction" requires.
/// </remarks>
public static class SchemaCompare
{
    /// <summary>
    /// Compares two already-resolved models and returns a selectable change set. Pure (no I/O) so it is
    /// trivially unit-testable; the resolving overloads layer endpoint loading on top.
    /// </summary>
    /// <param name="source">The desired state (left side).</param>
    /// <param name="target">The actual state (right side) the changes would migrate toward the source.</param>
    /// <param name="options">Comparer options (e.g. allow destructive drops). Defaults to non-destructive.</param>
    /// <param name="excludeObjectTypes">Object-type tokens to mark excluded from the start (e.g. <c>extension</c>).</param>
    public static SchemaChangeSet Of(
        DatabaseModel source,
        DatabaseModel target,
        ComparerOptions? options = null,
        IEnumerable<string>? excludeObjectTypes = null) =>
        SchemaChangeSet.Build(source, target, options, excludeObjectTypes);

    /// <summary>
    /// Resolves a source spec and a target spec (each a project, package, or connection string) via the
    /// shared <see cref="EndpointResolver"/>, then diffs them into a selectable change set. Both resolutions
    /// are reported back so a caller can surface display names and build diagnostics.
    /// </summary>
    public static async Task<SchemaCompareResult> RunAsync(
        string sourceSpec,
        string targetSpec,
        ComparerOptions? options = null,
        IEnumerable<string>? excludeObjectTypes = null,
        CancellationToken ct = default)
    {
        var source = await EndpointResolver.ResolveAsync(sourceSpec, ct);
        var target = await EndpointResolver.ResolveAsync(targetSpec, ct);
        var changeSet = Of(source.Model, target.Model, options, excludeObjectTypes);
        return new SchemaCompareResult(source, target, changeSet);
    }
}

/// <summary>
/// The outcome of a resolving two-way <see cref="SchemaCompare.RunAsync"/>: the resolved source and target
/// endpoints (kind, display name, build diagnostics) plus the selectable <see cref="ChangeSet"/> between them.
/// </summary>
/// <param name="Source">The resolved left endpoint (the desired state).</param>
/// <param name="Target">The resolved right endpoint (the actual state).</param>
/// <param name="ChangeSet">The structured, selectable diff (source→target).</param>
public sealed record SchemaCompareResult(
    ResolvedEndpoint Source,
    ResolvedEndpoint Target,
    SchemaChangeSet ChangeSet);
