using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PgProj.Core.Cli;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Guards the CI/CD exit-code contract (EP-CICD). These tests assert that the classified exit-code
/// taxonomy in <see cref="ExitCode"/> is complete and numerically stable, and that every constant is
/// documented in <c>docs/CICD.md</c> — so pipelines that branch on the codes can rely on them and so
/// the docs page never drifts from the source of truth. This is additive to (does not duplicate)
/// general CLI coverage.
/// </summary>
public class ExitCodeContractTests
{
    // The frozen contract: name -> numeric value. Adding a code here AND in ExitCode.cs is the
    // intended way to extend the taxonomy; changing an existing number is a breaking change and the
    // stability test below will fail loudly.
    private static readonly IReadOnlyDictionary<string, int> Expected = new Dictionary<string, int>
    {
        ["Success"] = 0,
        ["Error"] = 1,
        ["Usage"] = 2,
        ["BuildError"] = 3,
        ["AnalysisBlocked"] = 4,
        ["ReferenceError"] = 5,
        ["Drift"] = 6,
        ["DeployError"] = 7,
        ["ValidationFailed"] = 8,
        ["DataLossBlocked"] = 9,
    };

    private static IReadOnlyDictionary<string, int> ActualConstants() =>
        typeof(ExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);

    [Fact]
    public void Taxonomy_matches_the_frozen_contract_exactly()
    {
        var actual = ActualConstants();

        // No surprise additions (forces a deliberate update to Expected — and thus a contract review).
        var undeclared = actual.Keys.Except(Expected.Keys).ToList();
        Assert.True(undeclared.Count == 0,
            $"ExitCode has constants not in the frozen contract: {string.Join(", ", undeclared)}. " +
            "If intentional, add them to Expected here AND document them in docs/CICD.md.");

        // No silent removals.
        var missing = Expected.Keys.Except(actual.Keys).ToList();
        Assert.True(missing.Count == 0,
            $"ExitCode is missing contracted constants: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void Numeric_values_are_stable()
    {
        var actual = ActualConstants();
        foreach (var (name, value) in Expected)
        {
            Assert.True(actual.TryGetValue(name, out var got),
                $"ExitCode.{name} is missing.");
            Assert.True(value == got,
                $"ExitCode.{name} = {got}, but the stable contract pins it at {value}. " +
                "Exit-code numbers are a public contract and must not change.");
        }
    }

    [Fact]
    public void Codes_are_distinct()
    {
        var actual = ActualConstants();
        var dupes = actual.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList();
        Assert.True(dupes.Count == 0,
            "Exit codes must be distinct; collisions: " +
            string.Join("; ", dupes.Select(g => $"{g.Key} = {string.Join("/", g.Select(x => x.Key))}")));
    }

    [Fact]
    public void Success_is_zero()
    {
        Assert.Equal(0, ExitCode.Success);
    }

    [Fact]
    public void Every_constant_is_documented_in_CICD_md()
    {
        var doc = File.ReadAllText(CicdDocPath());
        foreach (var (name, value) in ActualConstants())
        {
            Assert.True(doc.Contains(name, StringComparison.Ordinal),
                $"docs/CICD.md does not mention ExitCode.{name}.");
            // The numeric value must appear too (the table uses backtick-wrapped numbers).
            Assert.True(doc.Contains($"`{value}`", StringComparison.Ordinal),
                $"docs/CICD.md does not document the numeric code {value} (for ExitCode.{name}).");
        }
    }

    [Fact]
    public void CICD_md_points_at_the_canonical_source_file()
    {
        var doc = File.ReadAllText(CicdDocPath());
        Assert.Contains("ExitCode.cs", doc);
    }

    private static string CicdDocPath()
    {
        var path = Path.Combine(CorpusData.RepoRoot, "docs", "CICD.md");
        Assert.True(File.Exists(path), $"docs/CICD.md not found at {path}");
        return path;
    }
}
