using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-TARGET: the target-platform enforcement analyzer. For each capability we assert it (a) is flagged
/// under an older target, (b) is allowed under a new-enough target, with the correct PGV### id and a
/// file:line anchor; that an unset target produces no findings; and that the project-level gate
/// (build/validate path) blocks/doesn't-block accordingly.
/// </summary>
public class TargetVersionTests
{
    // Analyze a single SQL snippet against a target version, with a synthetic file name + source text so
    // findings carry file:line:col exactly as the CLI gate produces them.
    private static IReadOnlyList<Diagnostic> A(string sql, string? target, string file = "x.sql")
        => TargetVersionAnalyzer.Analyze(new PgParser().Parse(sql), target, file, sql);

    private static bool Has(IReadOnlyList<Diagnostic> d, string ruleId) => d.Any(x => x.RuleId == ruleId);

    // ---- version parsing -----------------------------------------------------------------------

    [Theory]
    [InlineData("16", 16)]
    [InlineData("17", 17)]
    [InlineData("18", 18)]
    [InlineData("PostgreSQL 16", 16)]
    [InlineData("pg15", 15)]
    [InlineData("16.2", 16)]
    public void ParseMajorVersion_extracts_the_major(string input, int expected)
        => Assert.Equal(expected, TargetVersionAnalyzer.ParseMajorVersion(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    public void ParseMajorVersion_is_null_when_unset_or_unparseable(string? input)
        => Assert.Null(TargetVersionAnalyzer.ParseMajorVersion(input));

    // ---- unset target = no gating --------------------------------------------------------------

    [Fact]
    public void Unset_target_produces_no_findings_even_for_brand_new_syntax()
    {
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN DELETE RETURNING a.id;";
        Assert.Empty(A(sql, null));
        Assert.Empty(A(sql, ""));
        Assert.Empty(A(sql, "  "));
    }

    // ---- PGV001 MERGE (PG15) -------------------------------------------------------------------

    [Fact]
    public void PGV001_merge_flagged_on_pg14_allowed_on_pg15()
    {
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN UPDATE SET x=b.x;";
        Assert.True(Has(A(sql, "14"), PgVersionCapabilities.MergeStatement));
        Assert.False(Has(A(sql, "15"), PgVersionCapabilities.MergeStatement));
        Assert.False(Has(A(sql, "18"), PgVersionCapabilities.MergeStatement));
    }

    // ---- PGV002 MERGE … RETURNING (PG17) -------------------------------------------------------

    [Fact]
    public void PGV002_merge_returning_flagged_on_pg16_allowed_on_pg17()
    {
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN DELETE RETURNING a.id;";
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.MergeReturning));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.MergeReturning));
    }

    [Fact]
    public void PGV002_plain_merge_on_pg16_flags_merge_not_returning()
    {
        // MERGE without RETURNING under PG16: MERGE itself (PG15) is fine, but no RETURNING finding.
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN DELETE;";
        var d = A(sql, "16");
        Assert.False(Has(d, PgVersionCapabilities.MergeStatement));   // PG15 <= 16
        Assert.False(Has(d, PgVersionCapabilities.MergeReturning));   // no RETURNING present
    }

    // ---- PGV003 WHEN [NOT] MATCHED BY SOURCE/TARGET (PG17) -------------------------------------

    [Fact]
    public void PGV003_merge_by_source_flagged_on_pg16_allowed_on_pg17()
    {
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN NOT MATCHED BY SOURCE THEN DELETE;";
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.MergeByGuard));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.MergeByGuard));
    }

    [Fact]
    public void PGV003_not_raised_for_plain_when_matched()
    {
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN DELETE;";
        Assert.False(Has(A(sql, "16"), PgVersionCapabilities.MergeByGuard));
    }

    // ---- PGV004 NULLS NOT DISTINCT (PG15) -----------------------------------------------------

    [Fact]
    public void PGV004_table_constraint_flagged_on_pg14_allowed_on_pg15()
    {
        var sql = "CREATE TABLE s.t (a int, UNIQUE NULLS NOT DISTINCT (a));";
        Assert.True(Has(A(sql, "14"), PgVersionCapabilities.NullsNotDistinct));
        Assert.False(Has(A(sql, "15"), PgVersionCapabilities.NullsNotDistinct));
    }

    [Fact]
    public void PGV004_inline_constraint_flagged_on_pg14()
    {
        var sql = "CREATE TABLE s.t (a int UNIQUE NULLS NOT DISTINCT);";
        Assert.True(Has(A(sql, "14"), PgVersionCapabilities.NullsNotDistinct));
    }

    [Fact]
    public void PGV004_plain_unique_is_not_flagged()
    {
        Assert.False(Has(A("CREATE TABLE s.t (a int UNIQUE);", "10"), PgVersionCapabilities.NullsNotDistinct));
        Assert.False(Has(A("CREATE TABLE s.t (a int, UNIQUE (a));", "10"), PgVersionCapabilities.NullsNotDistinct));
    }

    // ---- PGV005 JSON_TABLE (PG17) -------------------------------------------------------------

    [Fact]
    public void PGV005_json_table_in_from_flagged_on_pg16_allowed_on_pg17()
    {
        var sql = "SELECT * FROM json_table('[]'::jsonb, '$[*]' COLUMNS (id int PATH '$.id')) jt;";
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.JsonTable));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.JsonTable));
    }

    [Fact]
    public void PGV005_json_table_as_expression_flagged_on_pg16()
    {
        Assert.True(Has(A("SELECT json_table('[]'::jsonb, '$');", "16"), PgVersionCapabilities.JsonTable));
    }

    // ---- PGV006 JSON_QUERY / JSON_VALUE / JSON_EXISTS (PG17) ----------------------------------

    [Theory]
    [InlineData("SELECT json_query(x, '$.a') FROM s.t;")]
    [InlineData("SELECT json_value(x, '$.a') FROM s.t;")]
    [InlineData("SELECT json_exists(x, '$.a') FROM s.t;")]
    public void PGV006_json_query_functions_flagged_on_pg16_allowed_on_pg17(string sql)
    {
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.JsonQueryFunctions));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.JsonQueryFunctions));
    }

    // ---- PGV007 JSON / JSON_SCALAR / JSON_SERIALIZE (PG16) ------------------------------------

    [Theory]
    [InlineData("SELECT json_scalar(1);")]
    [InlineData("SELECT json_serialize(x) FROM s.t;")]
    public void PGV007_json_constructors_flagged_on_pg15_allowed_on_pg16(string sql)
    {
        Assert.True(Has(A(sql, "15"), PgVersionCapabilities.JsonConstructors));
        Assert.False(Has(A(sql, "16"), PgVersionCapabilities.JsonConstructors));
    }

    // ---- PGV008 IS [NOT] JSON (PG16) ----------------------------------------------------------

    [Fact]
    public void PGV008_is_json_flagged_on_pg15_allowed_on_pg16()
    {
        var sql = "SELECT (x IS JSON) FROM s.t;";
        Assert.True(Has(A(sql, "15"), PgVersionCapabilities.IsJsonPredicate));
        Assert.False(Has(A(sql, "16"), PgVersionCapabilities.IsJsonPredicate));
    }

    [Fact]
    public void PGV008_is_not_json_flagged_on_pg15()
    {
        Assert.True(Has(A("SELECT (x IS NOT JSON) FROM s.t;", "15"), PgVersionCapabilities.IsJsonPredicate));
    }

    // ---- verbatim bodies (views + functions are kept as text, not expression trees) -----------

    [Fact]
    public void View_body_is_scanned_for_version_features()
    {
        var sql = "CREATE VIEW s.v AS SELECT (x IS JSON) AS ok FROM s.t;";
        Assert.True(Has(A(sql, "15"), PgVersionCapabilities.IsJsonPredicate));
        Assert.False(Has(A(sql, "16"), PgVersionCapabilities.IsJsonPredicate));
    }

    [Fact]
    public void View_body_json_table_is_scanned()
    {
        var sql = "CREATE VIEW s.v AS SELECT * FROM json_table('[]'::jsonb, '$') jt;";
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.JsonTable));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.JsonTable));
    }

    [Fact]
    public void Function_body_is_scanned_for_version_features()
    {
        var sql = "CREATE FUNCTION s.f() RETURNS json LANGUAGE sql IMMUTABLE AS $$ SELECT json_value(x,'$') FROM s.t $$;";
        Assert.True(Has(A(sql, "16"), PgVersionCapabilities.JsonQueryFunctions));
        Assert.False(Has(A(sql, "17"), PgVersionCapabilities.JsonQueryFunctions));
    }

    [Fact]
    public void Function_named_like_a_feature_does_not_trip_the_gate_from_its_header()
    {
        // A function merely NAMED json_value must not flag from the header; only its body matters.
        var sql = "CREATE FUNCTION s.json_value_helper() RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT 1 $$;";
        Assert.False(Has(A(sql, "16"), PgVersionCapabilities.JsonQueryFunctions));
    }

    // ---- diagnostics carry the correct id, severity, and file:line:col ------------------------

    [Fact]
    public void Findings_are_errors_with_the_pgv_id_and_file_line_col()
    {
        // CREATE TABLE carries a real source offset, so this verifies precise line:col tracking: the
        // offending table (NULLS NOT DISTINCT, PG15) starts on line 2, column 1.
        var sql = "-- header comment\nCREATE TABLE s.t (a int, UNIQUE NULLS NOT DISTINCT (a));";
        var d = A(sql, "14", file: "tables/t.sql").Single(x => x.RuleId == PgVersionCapabilities.NullsNotDistinct);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.StartsWith("PGV", d.RuleId);
        // file:line:col — the offending statement is on line 2 (the parser anchors at the object name).
        Assert.StartsWith("tables/t.sql:2:", d.Target);
        Assert.Contains("PostgreSQL 15", d.Message);   // states the required version
        Assert.Contains("PostgreSQL 14", d.Message);   // states the target
    }

    [Fact]
    public void Query_findings_are_anchored_to_their_file()
    {
        // Top-level query/DML statements don't carry a per-statement offset in the parser, so they anchor
        // to the file (line 1). The important contract is that the offending FILE is identified.
        var d = A("SELECT json_value(x, '$') FROM s.t;", "16", file: "queries/q.sql")
            .Single(x => x.RuleId == PgVersionCapabilities.JsonQueryFunctions);
        Assert.StartsWith("queries/q.sql:", d.Target);
    }

    [Fact]
    public void Same_feature_twice_in_one_statement_is_deduped_per_location()
    {
        // Two json_value calls in one SELECT → one finding (deduped by rule+location).
        var sql = "SELECT json_value(a,'$'), json_value(b,'$') FROM s.t;";
        var count = A(sql, "16").Count(x => x.RuleId == PgVersionCapabilities.JsonQueryFunctions);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Clean_old_syntax_under_old_target_yields_nothing()
    {
        var sql = "CREATE TABLE s.t (id int PRIMARY KEY, name text NOT NULL);\n" +
                  "UPDATE s.t SET name = 'x' WHERE id = 1;\n" +
                  "INSERT INTO s.t (id, name) VALUES (1, 'a');";
        Assert.Empty(A(sql, "12"));
    }

    [Fact]
    public void Capability_table_is_self_consistent()
    {
        // Every rule id constant resolves; every entry has a positive version and a message.
        foreach (var (id, cap) in PgVersionCapabilities.ByRuleId)
        {
            Assert.Equal(id, cap.RuleId);
            Assert.StartsWith("PGV", id);
            Assert.True(cap.MinMajorVersion >= 15);
            Assert.False(string.IsNullOrWhiteSpace(cap.Feature));
            Assert.False(string.IsNullOrWhiteSpace(cap.Detail));
        }
        Assert.Equal(8, PgVersionCapabilities.RuleCount);
        Assert.Equal(8, TargetVersionAnalyzer.RuleCount);
    }

    // ---- project-level gate (the build/validate gate's verdict) --------------------------------

    private sealed class TempProject : IDisposable
    {
        public readonly string Dir;
        public TempProject() { Dir = Path.Combine(Path.GetTempPath(), "pgproj_tv_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        public string Write(string rel, string content)
        {
            var p = Path.Combine(Dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            return p;
        }
    }

    private static string Manifest(string name, string? target) =>
        $"""
        <Project Sdk="PgProj.Sdk/0.1.0">
          <PropertyGroup>
            <Name>{name}</Name>
            <DefaultSchema>public</DefaultSchema>
            {(target is null ? "" : $"<TargetPostgresVersion>{target}</TargetPostgresVersion>")}
          </PropertyGroup>
          <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
        </Project>
        """;

    [Fact]
    public void Gate_blocks_when_project_uses_newer_syntax_than_target()
    {
        using var t = new TempProject();
        var proj = t.Write("App.pgproj", Manifest("App", "16"));
        t.Write("Tables/t.sql", "CREATE TABLE public.t (id int PRIMARY KEY);");
        t.Write("Queries/m.sql", "MERGE INTO public.t a USING public.t b ON a.id=b.id WHEN MATCHED THEN DELETE RETURNING a.id;");

        var project = DatabaseProject.Load(proj);
        Assert.True(TargetVersionAnalyzer.ProjectExceedsTarget(project));

        var findings = TargetVersionAnalyzer.AnalyzeProject(project);
        Assert.Contains(findings, f => f.RuleId == PgVersionCapabilities.MergeReturning);
        // The finding is anchored to the offending project-relative file.
        Assert.Contains(findings, f => f.Target.StartsWith("Queries/m.sql:"));
    }

    [Fact]
    public void Gate_does_not_block_when_target_is_new_enough()
    {
        using var t = new TempProject();
        var proj = t.Write("App.pgproj", Manifest("App", "17"));
        t.Write("Queries/m.sql", "MERGE INTO public.t a USING public.t b ON a.id=b.id WHEN MATCHED THEN DELETE RETURNING a.id;");

        var project = DatabaseProject.Load(proj);
        Assert.False(TargetVersionAnalyzer.ProjectExceedsTarget(project));
        Assert.Empty(TargetVersionAnalyzer.AnalyzeProject(project));
    }

    [Fact]
    public void Gate_does_not_block_when_no_target_is_declared()
    {
        using var t = new TempProject();
        var proj = t.Write("App.pgproj", Manifest("App", target: null));
        t.Write("Queries/m.sql", "MERGE INTO public.t a USING public.t b ON a.id=b.id WHEN MATCHED THEN DELETE RETURNING a.id;");

        var project = DatabaseProject.Load(proj);
        Assert.Null(project.TargetPostgresVersion);
        Assert.False(TargetVersionAnalyzer.ProjectExceedsTarget(project));
        Assert.Empty(TargetVersionAnalyzer.AnalyzeProject(project));
    }

    [Fact]
    public void Gate_reports_every_offending_file()
    {
        using var t = new TempProject();
        var proj = t.Write("App.pgproj", Manifest("App", "15"));
        t.Write("a.sql", "SELECT (x IS JSON) FROM public.t;");                    // PGV008 (PG16)
        t.Write("b.sql", "SELECT * FROM json_table('[]'::jsonb, '$') jt;");       // PGV005 (PG17)

        var findings = TargetVersionAnalyzer.AnalyzeProject(DatabaseProject.Load(proj));
        Assert.Contains(findings, f => f.RuleId == PgVersionCapabilities.IsJsonPredicate && f.Target.StartsWith("a.sql:"));
        Assert.Contains(findings, f => f.RuleId == PgVersionCapabilities.JsonTable && f.Target.StartsWith("b.sql:"));
    }
}
