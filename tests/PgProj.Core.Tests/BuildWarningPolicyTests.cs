using System;
using System.IO;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-BUILD (#135): the project-level build-warning policy — SuppressWarnings +
/// TreatWarningsAsErrors, the SSDT SuppressTSqlWarnings/TreatTSqlWarningsAsErrors analogue.
/// Exercised through ContractBuilder.Analyze (the gate implementation the CLI and the in-proc
/// editor path share), so the verdicts here ARE the build-gate verdicts.
/// </summary>
public sealed class BuildWarningPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pgproj-warnpol-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A project whose single function reliably produces the PG001 WARNING (security definer, no search_path).</summary>
    private DatabaseProject Scaffold(string extraProperties = "")
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        File.WriteAllText(Path.Combine(dir, "Db.pgproj"), $"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <Name>WarnDb</Name>
                <DefaultSchema>public</DefaultSchema>
                {extraProperties}
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "app", "f.sql"),
            "CREATE FUNCTION app.f() RETURNS int LANGUAGE sql STABLE SECURITY DEFINER AS $$ SELECT 1 $$;\n");
        return DatabaseProject.Load(Path.Combine(dir, "Db.pgproj"));
    }

    [Fact]
    public void Project_properties_parse_lists_and_booleans()
    {
        var p = Scaffold("<SuppressWarnings>PG001, PGV001; pg002</SuppressWarnings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
        Assert.Equal(new[] { "PG001", "PGV001", "pg002" }, p.SuppressedWarnings);
        Assert.True(p.TreatWarningsAsErrors);

        var none = Scaffold();
        Assert.Empty(none.SuppressedWarnings);
        Assert.False(none.TreatWarningsAsErrors);
    }

    [Fact]
    public void Warning_is_emitted_but_does_not_block_the_build_by_default()
    {
        var report = ContractBuilder.Analyze(Scaffold(), strict: false);
        Assert.Contains(report.Diagnostics, d => d.RuleId == "PG001");
        Assert.False(report.Blocked);
    }

    [Fact]
    public void TreatWarningsAsErrors_promotes_the_same_warning_to_a_blocking_verdict()
    {
        var report = ContractBuilder.Analyze(
            Scaffold("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"), strict: false);
        Assert.Contains(report.Diagnostics, d => d.RuleId == "PG001");
        Assert.True(report.Blocked);
    }

    [Fact]
    public void SuppressWarnings_silences_the_code_entirely_and_never_breaks_the_build()
    {
        // suppressed even WITH promotion enabled — a suppressed code can never break the build
        var report = ContractBuilder.Analyze(
            Scaffold("<SuppressWarnings>PG001</SuppressWarnings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"),
            strict: false);
        Assert.DoesNotContain(report.Diagnostics, d => d.RuleId == "PG001");
        Assert.False(report.Blocked);
        Assert.Equal(0, report.Summary.Warnings);
    }

    [Fact]
    public void Strict_flag_and_project_promotion_are_independent_paths_to_the_same_verdict()
    {
        var plain = Scaffold();
        Assert.True(ContractBuilder.Analyze(plain, strict: true).Blocked);          // CLI --strict
        Assert.False(ContractBuilder.Analyze(plain, strict: false).Blocked);        // neither
    }

    [Fact]
    public void Diagnostics_carry_file_line_provenance_for_the_verbose_channel()
    {
        var report = ContractBuilder.Analyze(Scaffold(), strict: false);
        var d = report.Diagnostics.First(x => x.RuleId == "PG001");
        Assert.False(string.IsNullOrEmpty(d.File));
        Assert.True(d.Line >= 1);
        Assert.Contains("app.f", d.Target);
    }

    [Fact]
    public void Policy_apply_and_blocking_helpers_behave_standalone()
    {
        var policy = new BuildWarningPolicy(new[] { "PG001" }, TreatWarningsAsErrors: true);
        var findings = new[]
        {
            new Diagnostic("PG001", DiagnosticSeverity.Warning, "suppressed", "t"),
            new Diagnostic("PG002", DiagnosticSeverity.Warning, "kept", "t"),
        };
        var filtered = policy.Apply(findings);
        Assert.Single(filtered);
        Assert.Equal("PG002", filtered[0].RuleId);
        Assert.True(policy.IsBlocking(filtered));                       // promoted by the project
        Assert.False(BuildWarningPolicy.None.IsBlocking(filtered));     // plain warnings pass
        Assert.Contains("PG001", policy.Describe());
    }
}
