using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Semantics;
using PgProj.Core.Syntax;
using Xunit;
using UnifiedDiagnostic = PgProj.Core.Diagnostics.Diagnostic;
using RelatedLocation = PgProj.Core.Diagnostics.RelatedLocation;

namespace PgProj.Core.Tests;

/// <summary>
/// Guard rails for the one compiler-style diagnostic type (issue #49). Proves the unified
/// <see cref="UnifiedDiagnostic"/> carries every field (severity/code/message/file/line/column/related)
/// and that each producer (parser, analyzer, semantic, build strings) populates it without losing a field.
/// </summary>
public class UnifiedDiagnosticTests
{
    // ---- the unified type carries every field ---------------------------------------------------

    [Fact]
    public void Unified_diagnostic_carries_all_fields_including_related()
    {
        var related = new RelatedLocation("Tables/other.sql", 3, 4, "first defined here");
        var d = new UnifiedDiagnostic
        {
            Severity = DiagnosticSeverity.Warning,
            Code = "PG001",
            Message = "missing search_path",
            Target = "afd.f",
            File = "Funcs/f.sql",
            Line = 10,
            Column = 5,
            Related = new[] { related },
        };

        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("PG001", d.Code);
        Assert.Equal("missing search_path", d.Message);
        Assert.Equal("afd.f", d.Target);
        Assert.Equal("Funcs/f.sql", d.File);
        Assert.Equal(10, d.Line);
        Assert.Equal(5, d.Column);
        var r = Assert.Single(d.Related);
        Assert.Equal("Tables/other.sql", r.File);
        Assert.Equal(3, r.Line);
        Assert.Equal(4, r.Column);
        Assert.Equal("first defined here", r.Message);
    }

    [Fact]
    public void Related_defaults_to_empty_not_null()
    {
        var d = UnifiedDiagnostic.FromBuild("boom");
        Assert.NotNull(d.Related);
        Assert.Empty(d.Related);
    }

    // ---- producer: analyzer (Analysis.Diagnostic) -----------------------------------------------

    [Fact]
    public void Analyzer_diagnostic_lifts_code_severity_target_and_anchor()
    {
        var analyzer = new Diagnostic("PG003", DiagnosticSeverity.Warning, "UPDATE without WHERE", "afd.t");
        var u = analyzer.ToUnified("Tables/t.sql", 7, 2);

        Assert.Equal("PG003", u.Code);
        Assert.Equal(DiagnosticSeverity.Warning, u.Severity);
        Assert.Equal("UPDATE without WHERE", u.Message);
        Assert.Equal("afd.t", u.Target);
        Assert.Equal("Tables/t.sql", u.File);
        Assert.Equal(7, u.Line);
        Assert.Equal(2, u.Column);
    }

    [Fact]
    public void Analyzer_diagnostic_without_anchor_has_zero_position()
    {
        var u = new Diagnostic("PG005", DiagnosticSeverity.Info, "no volatility", "afd.g").ToUnified();
        Assert.Null(u.File);
        Assert.Equal(0, u.Line);
        Assert.Equal(0, u.Column);
        Assert.Equal("afd.g", u.Target);
    }

    // ---- producer: parser (ParseDiagnostic) -----------------------------------------------------

    [Fact]
    public void Parser_emits_diagnostic_that_lifts_to_unified_with_line_col()
    {
        var result = new PgParser().Parse("CREATE TABLE afd.broken (id int,;");
        Assert.NotEmpty(result.Diagnostics);

        var u = result.Diagnostics[0].ToUnified("Tables/broken.sql");
        Assert.Equal(DiagnosticSeverity.Error, u.Severity);
        Assert.Equal("BUILD", u.Code);
        Assert.Equal("Tables/broken.sql", u.File);
        Assert.True(u.Line >= 1, "a parser diagnostic must carry a 1-based line");
        Assert.False(string.IsNullOrEmpty(u.Message));
    }

    // ---- producer: semantic (SemanticDiagnostic) ------------------------------------------------

    [Fact]
    public void Semantic_analyzer_unified_findings_carry_supplied_anchor()
    {
        var catalog = new Catalog { DefaultSchema = "afd" };
        catalog.AddSchema("afd");
        var parsed = new PgParser().Parse("SELECT * FROM afd.does_not_exist;");

        var found = new SemanticAnalyzer(catalog, new Catalog { DefaultSchema = "afd" })
            .AnalyzeUnified(parsed, "Views/v.sql", 12, 1);

        var d = Assert.Single(found);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("SEM", d.Code);
        Assert.Equal("Views/v.sql", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(1, d.Column);
        Assert.Contains("does not exist", d.Message);
    }

    // ---- contract layer drops no field ----------------------------------------------------------

    [Fact]
    public void Contract_mapper_preserves_all_fields_from_unified()
    {
        var u = new UnifiedDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Code = "PG004",
            Message = "schema mutation",
            Target = "afd.f",
            File = "Funcs/f.sql",
            Line = 9,
            Column = 3,
        };
        var dto = ContractMappers.ToDto(u);

        Assert.Equal("PG004", dto.RuleId);
        Assert.Equal(ContractSeverity.Error, dto.Severity);
        Assert.Equal("schema mutation", dto.Message);
        Assert.Equal("afd.f", dto.Target);
        Assert.Equal("Funcs/f.sql", dto.File);
        Assert.Equal(9, dto.Line);
        Assert.Equal(3, dto.Col);
    }

    [Fact]
    public void Build_string_parses_into_unified_with_file_line_col()
    {
        var u = ContractMappers.ToUnifiedBuild("Tables/bad.sql: 12:5: unexpected token");
        Assert.Equal("BUILD", u.Code);
        Assert.Equal(DiagnosticSeverity.Error, u.Severity);
        Assert.Equal("Tables/bad.sql", u.File);
        Assert.Equal(12, u.Line);
        Assert.Equal(5, u.Column);
        Assert.Equal("unexpected token", u.Message);
    }
}
