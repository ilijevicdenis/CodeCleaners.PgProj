using System.Collections.Generic;
using PgProj.Core.Project;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Dependencies;
using PgProj.Core.Syntax;

namespace PgProj.Core.Comparison;

/// <summary>
/// Builds the source project's <see cref="DependencyGraph"/> for deploy ordering (issues #50/#55/#160):
/// parse every project file, absorb all statements into ONE catalog (two passes, so cross-file references
/// resolve regardless of file order), collect the resolved references, and invert them into edges.
/// Follows the same read discipline as the reference gate (<c>ReadEffectiveText</c> — overlay/substitution
/// aware). Best-effort by design: the graph only REFINES change order, so any failure here must degrade to
/// the historical phase order, never block a publish.
/// </summary>
public static class DeploymentGraphFactory
{
    /// <summary>The project's dependency graph, or null when it cannot be built (degrade to phase order).</summary>
    public static DependencyGraph? TryBuild(DatabaseProject project)
    {
        try
        {
            var catalog = new Catalog { DefaultSchema = project.DefaultSchema };
            var parsed = new List<ParseResult>();
            foreach (var file in project.ResolveSqlFiles())
            {
                var p = new PgParser().Parse(project.ReadEffectiveText(file));
                parsed.Add(p);
                foreach (var stmt in p.Statements)
                    CatalogBuilder.Absorb(catalog, stmt);
            }

            foreach (var p in parsed)
            {
                ReferenceCollector.Collect(catalog, p);
                p.ReleaseTokens();
            }

            return DependencyGraphBuilder.Build(catalog.Symbols);
        }
        catch
        {
            return null;
        }
    }
}
