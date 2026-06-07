using System;
using System.IO;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Determinism regression for issue #62: source text is normalised to LF at LOAD time
/// (<see cref="SourceReader"/>), so the parsed model — and every artifact that embeds source verbatim
/// (<c>model.json</c>, the greenfield deploy script) — is byte-identical regardless of whether the
/// working tree was checked out with CRLF or LF. The body-bearing kinds (function, view, raw object)
/// are the ones that previously leaked an escaped <c>\r\n</c> into <c>model.json</c>.
/// </summary>
public sealed class LineEndingDeterminismTests : IDisposable
{
    private readonly string _root;

    public LineEndingDeterminismTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pgproj_eol_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A body-rich source whose multi-line content is where CRLF vs LF would otherwise diverge.
    private const string Lf =
        "CREATE SCHEMA app;\n" +
        "CREATE TABLE app.t (\n  id int PRIMARY KEY,\n  name text NOT NULL\n);\n" +
        "CREATE VIEW app.v AS\n  SELECT id, name\n  FROM app.t\n  WHERE id > 0;\n" +
        "CREATE FUNCTION app.f(x int) RETURNS int LANGUAGE sql AS $$\n  SELECT x + 1;\n$$;\n" +
        "CREATE TRIGGER trg\n  BEFORE INSERT ON app.t\n  FOR EACH ROW\n  EXECUTE FUNCTION app.f(1);\n";

    private string BuildProjectWith(string newline)
    {
        var sub = Path.Combine(_root, newline == "\r\n" ? "crlf" : "lf");
        Directory.CreateDirectory(sub);
        var projPath = Path.Combine(sub, "Eol.pgproj");
        File.WriteAllText(projPath, """
            <Project>
              <PropertyGroup><Name>Eol</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        // Write the SAME logical source with the requested line endings (bytes differ on disk).
        File.WriteAllText(Path.Combine(sub, "schema.sql"), Lf.Replace("\n", newline));
        return projPath;
    }

    private static (string ModelJson, string DeployScript) Artifacts(string projectFile)
    {
        var project = DatabaseProject.Load(projectFile);
        var built = project.Build();
        Assert.False(built.HasErrors, string.Join("\n", built.Diagnostics));

        var modelJson = ModelJson.Serialize(built.Model);
        var changes = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var deploy = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = true });
        return (modelJson, deploy);
    }

    [Fact]
    public void Crlf_and_lf_sources_produce_identical_model_json_and_deploy_script()
    {
        var (crlfJson, crlfDeploy) = Artifacts(BuildProjectWith("\r\n"));
        var (lfJson, lfDeploy) = Artifacts(BuildProjectWith("\n"));

        Assert.Equal(lfJson, crlfJson);
        Assert.Equal(lfDeploy, crlfDeploy);
    }

    [Fact]
    public void Model_json_from_crlf_source_contains_no_carriage_return_escape()
    {
        var (crlfJson, _) = Artifacts(BuildProjectWith("\r\n"));
        // The escaped CR ("\r" → JSON "\\r") is exactly what the old GoldenFileTests stopgap had to fold.
        Assert.DoesNotContain("\\r", crlfJson);
    }

    [Fact]
    public void SourceReader_folds_crlf_and_lone_cr_to_lf()
    {
        Assert.Equal("a\nb\nc", SourceReader.NormalizeLineEndings("a\r\nb\rc"));
        // Already-LF text is returned unchanged (the no-CR fast path).
        var lf = "a\nb\n";
        Assert.Same(lf, SourceReader.NormalizeLineEndings(lf));
    }
}
