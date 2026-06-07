using System;
using System.IO;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;

namespace PgProj.Core.Tests;

/// <summary>
/// Golden-file regression tests for issue #60 (phase 14/18 — deterministic generated artifacts).
///
/// These tests are DB-free: they load and build the AllFeaturesDb sample project,
/// generate the canonical greenfield deploy script and model JSON, and assert byte-reproducibility
/// against committed golden files.
///
/// Two guarantees are tested per artifact:
///   1. <b>Stability</b>: generate the artifact twice in-process → outputs must be identical
///      (cheap determinism guard, independent of the golden file).
///   2. <b>Golden regression</b>: generated output matches the committed golden file byte-for-byte
///      (modulo normalised line endings — both sides are normalised to LF so the golden is
///      platform-portable).
///
/// To regenerate the golden files after a deliberate change, set the environment variable
/// <c>PGPROJ_UPDATE_GOLDEN=1</c> and run this test class. The tests will overwrite the committed
/// golden files under <c>tests/PgProj.Core.Tests/golden/</c> instead of asserting.
///
/// NOTE: CanonicalHash-stability and StableId-under-rename tests are DEFERRED to issue #42 (M2).
/// Those concepts (CanonicalHash, StableId, ObjectId) do not exist in the codebase yet and will be
/// addressed in the Identity Model milestone.
/// </summary>
public sealed class GoldenFileTests
{
    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    private static bool UpdateGolden =>
        string.Equals(
            Environment.GetEnvironmentVariable("PGPROJ_UPDATE_GOLDEN"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> (the test binary output dir) to find
    /// the repo root by locating <c>tests/PgProj.Core.Tests/golden/</c>. Mirrors the
    /// <c>FindSampleProject</c> walk used throughout this test suite.
    /// </summary>
    private static string FindGoldenDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "tests", "PgProj.Core.Tests", "golden");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(
            "Could not locate tests/PgProj.Core.Tests/golden/ from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to locate
    /// <c>sample/AllFeaturesDb/AllFeaturesDb.pgproj</c>. Same logic as
    /// <see cref="LiveReaderTestSupport.FindSampleProject"/> — kept here so this class is self-contained.
    /// </summary>
    private static string FindSampleProject()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "sample", "AllFeaturesDb", "AllFeaturesDb.pgproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate sample/AllFeaturesDb/AllFeaturesDb.pgproj from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Normalises line endings to LF so comparisons are platform-portable and the golden files can
    /// be committed with Unix line endings without breaking on Windows (and vice-versa).
    /// </summary>
    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Asserts <paramref name="actual"/> equals the committed golden file at
    /// <paramref name="goldenFile"/>, or overwrites it when <c>PGPROJ_UPDATE_GOLDEN=1</c>.
    /// </summary>
    private static void AssertOrUpdate(string goldenFile, string actual, string artifactName)
    {
        var normActual = Normalise(actual);

        if (UpdateGolden)
        {
            // Regenerate mode: write with LF-only line endings for platform portability.
            File.WriteAllText(goldenFile, normActual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;  // test "passes" by definition — caller is the human deciding to accept the change
        }

        Assert.True(File.Exists(goldenFile),
            $"Golden file not found: {goldenFile}\n" +
            $"Run with PGPROJ_UPDATE_GOLDEN=1 to generate it.");

        var normGolden = Normalise(File.ReadAllText(goldenFile));
        Assert.Equal(normGolden, normActual);
    }

    // -------------------------------------------------------------------------------------------
    // Build the model once — shared across the tests in this class
    // -------------------------------------------------------------------------------------------

    private static (string DeployScript, string ModelJson) GenerateArtifacts()
    {
        var project = DatabaseProject.Load(FindSampleProject());
        var built = project.Build();

        Assert.False(built.HasErrors,
            "AllFeaturesDb sample project has build errors:\n" + string.Join("\n", built.Diagnostics));

        var changes = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var deployScript = new DeployScriptGenerator()
            .Generate(changes, new DeployOptions { WrapInTransaction = true });
        var modelJson = ModelJson.Serialize(built.Model);

        return (deployScript, modelJson);
    }

    // -------------------------------------------------------------------------------------------
    // Stability tests — two independent generations must be identical (no golden file needed)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DeployScript_is_stable_across_two_independent_generations()
    {
        var (first, _) = GenerateArtifacts();
        var (second, _) = GenerateArtifacts();

        Assert.Equal(
            Normalise(first),
            Normalise(second));
    }

    [Fact]
    public void ModelJson_is_stable_across_two_independent_generations()
    {
        var (_, first) = GenerateArtifacts();
        var (_, second) = GenerateArtifacts();

        Assert.Equal(
            Normalise(first),
            Normalise(second));
    }

    // -------------------------------------------------------------------------------------------
    // Golden-file regression tests — generated output matches committed golden files
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DeployScript_matches_golden_file()
    {
        var goldenDir = FindGoldenDir();
        var goldenFile = Path.Combine(goldenDir, "AllFeaturesDb.deploy.sql");

        var (deployScript, _) = GenerateArtifacts();

        AssertOrUpdate(goldenFile, deployScript, "deploy script");
    }

    [Fact]
    public void ModelJson_matches_golden_file()
    {
        var goldenDir = FindGoldenDir();
        var goldenFile = Path.Combine(goldenDir, "AllFeaturesDb.model.json");

        var (_, modelJson) = GenerateArtifacts();

        AssertOrUpdate(goldenFile, modelJson, "model JSON");
    }
}
