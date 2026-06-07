using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Project;
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

    // ---- producer: duplicate-definition (issue #63) --------------------------------------------

    /// <summary>
    /// A duplicate-table definition must emit a <see cref="UnifiedDiagnostic"/> whose
    /// <see cref="UnifiedDiagnostic.Related"/> list contains exactly one entry pointing at the
    /// file and line of the FIRST (prior) definition.
    /// Files are sorted alphabetically by <c>ResolveSqlFiles</c>; <c>a_original.sql</c> sorts before
    /// <c>b_duplicate.sql</c> so the first-seen position map records <c>a_original.sql</c> as the prior def.
    /// </summary>
    [Fact]
    public void Duplicate_table_diagnostic_carries_related_location_pointing_at_first_definition()
    {
        using var tmp = new DupProject(
            ("Tables/a_original.sql",  "CREATE TABLE app.widgets (id int PRIMARY KEY);"),  // parsed first
            ("Tables/b_duplicate.sql", "CREATE TABLE app.widgets (id int);"));              // duplicate

        var result = tmp.Project.Build();

        // Exactly one duplicate diagnostic.
        var dup = Assert.Single(result.UnifiedDiagnostics);
        Assert.Equal("BUILD", dup.Code);
        Assert.Contains("Duplicate", dup.Message, StringComparison.OrdinalIgnoreCase);

        // The related location must point at the FIRST (alphabetically earliest) definition.
        var related = Assert.Single(dup.Related);
        Assert.Equal("Tables/a_original.sql", related.File);
        Assert.True(related.Line >= 1, "prior-definition line must be 1-based");
        Assert.Equal("first defined here", related.Message);
    }

    /// <summary>
    /// A duplicate-table diagnostic with a related location must round-trip through the contract
    /// without losing the related field: Diagnostic → DTO → JSON → deserialized DTO → related present.
    /// </summary>
    [Fact]
    public void Related_locations_survive_contract_round_trip()
    {
        using var tmp = new DupProject(
            ("Tables/a_original.sql",  "CREATE TABLE app.widgets (id int PRIMARY KEY);"),
            ("Tables/b_duplicate.sql", "CREATE TABLE app.widgets (id int);"));

        var result = tmp.Project.Build();
        var dup = Assert.Single(result.UnifiedDiagnostics);

        // Unified → DTO
        var dto = ContractMappers.ToDto(dup);
        Assert.NotNull(dto.Related);
        var relDto = Assert.Single(dto.Related!);
        Assert.Equal("Tables/a_original.sql", relDto.File);
        Assert.True(relDto.Line >= 1);
        Assert.Equal("first defined here", relDto.Message);

        // DTO → JSON → back
        var json = JsonContract.Serialize(dto);
        Assert.Contains("\"related\"", json, StringComparison.OrdinalIgnoreCase);

        var round = JsonSerializer.Deserialize<DiagnosticDto>(json, JsonContract.Options)!;
        Assert.NotNull(round.Related);
        var roundRel = Assert.Single(round.Related!);
        Assert.Equal(relDto.File, roundRel.File);
        Assert.Equal(relDto.Line, roundRel.Line);
        Assert.Equal(relDto.Message, roundRel.Message);
    }

    /// <summary>
    /// <see cref="ContractMappers.ToDto(UnifiedDiagnostic)"/> must map every related location and
    /// omit the field (null) when there are none.
    /// </summary>
    [Fact]
    public void Contract_mapper_maps_related_locations_to_dto()
    {
        // With related
        var withRelated = new UnifiedDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Code = "BUILD",
            Message = "Duplicate table definition: app.t (defined 2 times).",
            Related = new[] { new RelatedLocation("Tables/t.sql", 5, 1, "first defined here") },
        };
        var dto = ContractMappers.ToDto(withRelated);
        Assert.NotNull(dto.Related);
        var r = Assert.Single(dto.Related!);
        Assert.Equal("Tables/t.sql", r.File);
        Assert.Equal(5, r.Line);
        Assert.Equal(1, r.Col);
        Assert.Equal("first defined here", r.Message);

        // Without related — field must be null (omit-null on the wire)
        var noRelated = UnifiedDiagnostic.FromBuild("some build error");
        var dtoNoRel = ContractMappers.ToDto(noRelated);
        Assert.Null(dtoNoRel.Related);
    }

    // ---- helper: a throwaway on-disk project with two SQL files, default schema "app" --------

    private sealed class DupProject : IDisposable
    {
        private readonly string _dir;
        public DatabaseProject Project { get; }

        public DupProject(params (string Rel, string Sql)[] files)
        {
            _dir = Path.Combine(Path.GetTempPath(), "pgproj_dup_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "Dup.pgproj"),
                "<Project Sdk=\"PgProj.Sdk/0.1.0\"><PropertyGroup><Name>Dup</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>" +
                "<ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
            foreach (var (rel, sql) in files)
            {
                var path = Path.Combine(_dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, sql);
            }
            Project = DatabaseProject.Load(Path.Combine(_dir, "Dup.pgproj"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
