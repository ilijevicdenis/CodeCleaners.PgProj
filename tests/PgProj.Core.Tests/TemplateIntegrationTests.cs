using System;
using System.IO;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Templates;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Integration coverage for EP-TEMPLATES: scaffold a project + a table + a function purely from the
/// templates, then validate the result against a real PostgreSQL server using the same
/// <see cref="ShadowValidator"/> path the <c>validate</c> verb uses (apply to a throwaway DB inside a
/// transaction, then roll back and drop it). Env-var gated on <c>PGPROJ_TEST_CONNECTION</c> — like
/// <see cref="LiveReaderIntegrationTests"/>, this is the repo's real harness; with no DB it skips.
/// </summary>
public sealed class TemplateIntegrationTests : IDisposable
{
    private static string? Conn => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private readonly string _dir;

    public TemplateIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_tmpl_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Scaffolded_table_and_function_validate_against_postgres()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB available — treated as a skip

        // Scaffold a real project from the templates, exactly as `new project` + `add` would.
        var created = Scaffolder.NewProject("TmplIt", _dir, defaultSchema: "tmpl_it");
        Scaffolder.Add(created.ProjectFilePath, "schema", "tmpl_it");
        Scaffolder.Add(created.ProjectFilePath, "table", "tmpl_it.customer");
        Scaffolder.Add(created.ProjectFilePath, "function", "tmpl_it.noop");

        var project = DatabaseProject.Load(created.ProjectFilePath);
        var built = project.Build();
        Assert.False(built.HasErrors, "scaffolded project build: " + string.Join("\n", built.Diagnostics));

        // Full create script, no BEGIN/COMMIT — ShadowValidator wraps it in its own transaction.
        var changes = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = false });

        var outcome = await new ShadowValidator().ValidateAsync(conn, script);
        Assert.True(outcome.Ok,
            $"scaffolded project failed validation: {outcome.Error} [{outcome.SqlState}] @ {outcome.Position}");
    }
}
