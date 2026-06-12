using System.Collections.Generic;
using PgProj.Core.Syntax;

namespace PgProj.Core.Analysis;

/// <summary>
/// A custom static-analysis rule shipped in an <b>external rule pack</b> (EP-ANALYSIS+ #79) — the
/// DacFx contributor-model analogue. Implement this in your own assembly, reference <c>PgProj.Core</c>,
/// build the DLL, and point a project's <c>.pgproj.analysis.json</c> <c>rulePacks</c> array at it; pgproj
/// discovers every public, parameterless-constructible implementation and runs it alongside the built-in
/// <c>PG0xx</c>/<c>PGV0xx</c> rules. Findings honour the same per-rule enable/severity config as built-ins
/// (keyed on <see cref="Id"/>), so a project can disable or re-severity a pack rule just like a built-in.
/// </summary>
/// <remarks>
/// Implementations must be deterministic and side-effect-free (the analyzer runs them per file, possibly in
/// parallel across files). Keep <see cref="Id"/> stable and distinct from the built-in ids (PG/PGV prefixes
/// are reserved); on a duplicate id the first-loaded rule wins and the rest are dropped.
/// </remarks>
public interface IPgRule
{
    /// <summary>Stable, unique rule id (e.g. <c>ORG001</c>). Used for config targeting and de-duplication.</summary>
    string Id { get; }

    /// <summary>The severity findings carry unless a project's analysis config overrides it.</summary>
    DiagnosticSeverity DefaultSeverity { get; }

    /// <summary>One-line human description (used in listings and SARIF rule descriptors).</summary>
    string Title { get; }

    /// <summary>Analyze one parsed source file and yield findings (empty when clean).</summary>
    IEnumerable<Diagnostic> Analyze(ParseResult result);
}
