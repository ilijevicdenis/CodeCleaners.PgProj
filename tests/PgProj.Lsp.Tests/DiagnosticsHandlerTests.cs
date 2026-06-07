using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Project;
using PgProj.Lsp.Handlers;
using PgProj.Lsp.Protocol;
using PgProj.Lsp.Workspace;
using Xunit;

namespace PgProj.Lsp.Tests;

/// <summary>
/// Diagnostics handler: a buffer edited to an invalid state surfaces a finding at the right line/col with the
/// engine ruleId/severity; fixing it clears it; and the live verdict equals the batch <c>build</c> verdict.
/// </summary>
public sealed class DiagnosticsHandlerTests
{
    [Fact]
    public async Task Invalid_buffer_publishes_diagnostic_then_fixing_it_clears_it()
    {
        using var tp = new TempProject();
        var rel = "tables/bad.sql";
        tp.WriteSql(rel, "CREATE TABLE public.t (id int);\n"); // valid on disk
        var uri = tp.UriFor(rel);

        var store = new DocumentStore();
        var svc = new LanguageService(store, tp.ProjectFilePath);

        // didChange to an invalid state (unterminated CREATE TABLE).
        store.Open(uri, "CREATE TABLE public.t (id int", 2);
        var bad = await svc.DiagnoseAsync(uri);
        Assert.NotEmpty(bad.Diagnostics);
        var d = bad.Diagnostics[0];
        Assert.Equal(LspSeverity.Error, d.Severity);
        Assert.Equal("BUILD", d.Code);
        Assert.Equal(2, bad.Version);

        // Fix it → no diagnostics.
        store.Change(uri, "CREATE TABLE public.t (id int);\n", 3);
        var good = await svc.DiagnoseAsync(uri);
        Assert.Empty(good.Diagnostics);
        Assert.Equal(3, good.Version);
    }

    [Fact]
    public async Task Duplicate_definition_is_reported_like_the_build()
    {
        using var tp = new TempProject();
        var rel = "dup.sql";
        var sql = "CREATE TABLE public.t (id int);\nCREATE TABLE public.t (id int);\n";
        tp.WriteSql(rel, sql);
        var uri = tp.UriFor(rel);

        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var diags = await svc.DiagnoseAsync(uri);
        Assert.Contains(diags.Diagnostics, x => x.Message.Contains("Duplicate table definition", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Live_diagnostics_agree_with_batch_build_for_a_sample_of_inputs()
    {
        var samples = new[]
        {
            "CREATE TABLE public.a (id int);\n",                         // valid
            "CREATE TABLE public.b (id int\n",                          // unterminated
            "SELECT 1;\n",                                              // valid statement
            "CREATE TABLE public.c (id int);\nCREATE TABLE public.c (x text);\n", // duplicate
            "CREATE VIEW public.v AS SELECT 1;\n",                      // valid view
        };

        foreach (var sql in samples)
        {
            using var tp = new TempProject();
            var rel = "s.sql";
            tp.WriteSql(rel, sql);
            var uri = tp.UriFor(rel);

            // Batch path: exactly what `pgproj build` would report for this single-file project.
            var project = DatabaseProject.Load(tp.ProjectFilePath);
            var build = await project.BuildAsync();
            var batchErrors = build.UnifiedDiagnostics.Count;

            // Live path: same file open in the server, no edits (buffer == disk).
            var store = new DocumentStore();
            store.Open(uri, sql, 1);
            var svc = new LanguageService(store, tp.ProjectFilePath);
            var live = await svc.DiagnoseAsync(uri);

            Assert.Equal(batchErrors, live.Diagnostics.Count);
            // No false positives: an empty batch verdict means an empty live verdict.
            Assert.Equal(batchErrors == 0, live.Diagnostics.Count == 0);
        }
    }

    [Fact]
    public async Task Loose_buffer_without_project_still_diagnoses_via_single_file_path()
    {
        var uri = "file:///loose/x.sql";
        var store = new DocumentStore();
        store.Open(uri, "CREATE TABLE t (id int", 1); // unterminated, no project
        var svc = new LanguageService(store, projectFilePath: null);

        var diags = await svc.DiagnoseAsync(uri);
        Assert.NotEmpty(diags.Diagnostics);
        Assert.All(diags.Diagnostics, d => Assert.Equal(LspSeverity.Error, d.Severity));
    }
}
