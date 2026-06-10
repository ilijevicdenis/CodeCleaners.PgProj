using System.Collections.Generic;
using PgProj.Core.Model;

namespace PgProj.Core.Analysis;

/// <summary>
/// A custom static-analysis rule that runs over the <b>merged project model</b> rather than a single
/// parsed file — the cross-object counterpart of <see cref="IPgRule"/>. Per-file rules cannot see
/// relationships that span files (a foreign key in one file, its covering index in another); a model
/// rule receives the whole <see cref="DatabaseModel"/> after every file has been lowered and merged,
/// so it can reason about the project as a unit. Ship implementations in the same rule-pack DLLs as
/// <see cref="IPgRule"/>s — the loader discovers both — and configure them per id exactly like any
/// other rule (<c>.pgproj.analysis.json</c> / <c>--rule</c>).
/// </summary>
/// <remarks>
/// Implementations must be deterministic and side-effect-free. Keep <see cref="Id"/> stable and
/// distinct from the built-in ids (PG/PGV prefixes are reserved); on a duplicate id the first-loaded
/// rule wins and the rest are dropped.
/// </remarks>
public interface IModelRule
{
    /// <summary>Stable, unique rule id (e.g. <c>ORG101</c>). Used for config targeting and de-duplication.</summary>
    string Id { get; }

    /// <summary>The severity findings carry unless a project's analysis config overrides it.</summary>
    DiagnosticSeverity DefaultSeverity { get; }

    /// <summary>One-line human description (used in listings and SARIF rule descriptors).</summary>
    string Title { get; }

    /// <summary>Analyze the merged project model and yield findings (empty when clean).</summary>
    IEnumerable<Diagnostic> Analyze(DatabaseModel model);
}
