using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using UnifiedDiagnostic = PgProj.Core.Diagnostics.Diagnostic;

namespace PgProj.Core.Contracts;

/// <summary>
/// Pure mapping from Core types (model, analyzer diagnostics, build diagnostic strings, schema changes)
/// into the wire DTOs. Kept separate from the verb orchestration so it is trivially unit-testable.
/// </summary>
public static class ContractMappers
{
    public static ModelSummaryDto SummaryOf(DatabaseModel m) => new()
    {
        Schemas = m.Schemas.Count,
        Tables = m.Tables.Count,
        Indexes = m.Indexes.Count,
        Views = m.Views.Count,
        Sequences = m.Sequences.Count,
        Functions = m.Functions.Count,
        Objects = m.Objects.Count,
    };

    public static DiagnosticSummaryDto SummaryOf(IEnumerable<DiagnosticDto> diags)
    {
        int e = 0, w = 0, i = 0;
        foreach (var d in diags)
            switch (d.Severity)
            {
                case ContractSeverity.Error: e++; break;
                case ContractSeverity.Warning: w++; break;
                default: i++; break;
            }
        return new DiagnosticSummaryDto { Errors = e, Warnings = w, Infos = i };
    }

    // ---- unified diagnostic → wire shape --------------------------------------------------------

    /// <summary>
    /// The single mapping from the unified <see cref="UnifiedDiagnostic"/> onto the wire
    /// <see cref="DiagnosticDto"/>. Every other diagnostic source funnels through the unified type and
    /// then this method, so no field (code/severity/file/line/col/target/related) is ever dropped in
    /// the contract layer.
    /// </summary>
    public static DiagnosticDto ToDto(UnifiedDiagnostic d) => new()
    {
        RuleId = d.Code,
        Severity = (ContractSeverity)(int)d.Severity,
        Message = d.Message,
        Target = d.Target,
        File = d.File,
        Line = d.Line,
        Col = d.Column,
        Related = d.Related.Count > 0 ? d.Related.Select(ToDto).ToList() : null,
    };

    /// <summary>Maps a <see cref="PgProj.Core.Diagnostics.RelatedLocation"/> to the wire DTO.</summary>
    private static RelatedLocationDto ToDto(PgProj.Core.Diagnostics.RelatedLocation r) => new()
    {
        File = r.File,
        Line = r.Line,
        Col = r.Column,
        Message = r.Message,
    };

    // ---- analyzer findings (RuleId/Severity/Message/Target) -------------------------------------

    /// <summary>
    /// Maps an analyzer <see cref="Diagnostic"/> to the wire shape, resolving its source anchor via the
    /// position index when the target is a schema-qualified object the index knows about. Routes through
    /// the unified <see cref="UnifiedDiagnostic"/> so the analyzer and contract layers share one shape.
    /// </summary>
    public static DiagnosticDto ToDto(Diagnostic d, SourcePositionIndex? positions)
    {
        var pos = ResolveAnalyzerTarget(d.Target, positions);
        return ToDto(UnifiedDiagnostic.FromAnalyzer(d, pos?.File, pos?.Line ?? 0, pos?.Col ?? 0));
    }

    /// <summary>An analyzer target is a schema-qualified object name; try every kind the index holds.</summary>
    private static SourcePosition? ResolveAnalyzerTarget(string target, SourcePositionIndex? positions)
    {
        if (positions is null || string.IsNullOrWhiteSpace(target)) return null;
        // Analyzer targets are functions (sig has no parens in PG001/PG005), views, or table names.
        foreach (var prefix in new[] { "function:", "view:", "table:" })
        {
            // functions: the analyzer target is "schema.name" without arg types — try a 0-arg signature first.
            if (prefix == "function:")
            {
                var hit = positions.Find($"function:{target}()".ToLowerInvariant());
                if (hit is not null) return hit;
            }
            var p = positions.Find($"{prefix}{target}".ToLowerInvariant());
            if (p is not null) return p;
        }
        return positions.FindRaw("", target);
    }

    // ---- build diagnostics (free-form strings) --------------------------------------------------

    private static readonly Regex BuildDiag =
        new(@"^(?<file>[^:]+\.sql):\s*(?:(?<line>\d+):(?<col>\d+):\s*)?(?<msg>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a project build-diagnostic string into the wire shape. Build diagnostics are strings of the
    /// form <c>"rel/file.sql: line:col: message"</c> (parser diagnostics) or a free-form message
    /// (duplicate/parse-failure). Build problems are always errors (the build gate fails on any of them).
    /// </summary>
    public static DiagnosticDto ToBuildDto(string raw) => ToDto(ToUnifiedBuild(raw));

    /// <summary>
    /// Parses a project build-diagnostic string into the unified <see cref="UnifiedDiagnostic"/> (file/line/col
    /// when the parser-style prefix is present, otherwise a file-or-bare build error). Shared by the contract
    /// mapper and any consumer that wants the structured form rather than the DTO.
    /// </summary>
    public static UnifiedDiagnostic ToUnifiedBuild(string raw)
    {
        var m = BuildDiag.Match(raw);
        if (m.Success)
        {
            var line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0;
            var col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0;
            return UnifiedDiagnostic.FromParser(
                m.Groups["msg"].Value.Trim(), m.Groups["file"].Value.Replace('\\', '/'), line, col);
        }
        return UnifiedDiagnostic.FromBuild(raw);
    }

    // ---- schema changes -------------------------------------------------------------------------

    public static ChangeDto ToDto(SchemaChange c) => new()
    {
        Kind = c.GetType().Name,
        Description = c.Describe(),
        Destructive = c.IsDestructive,
        Phase = c.Phase,
    };
}
