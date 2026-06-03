using System;
using System.IO;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using PgProj.Core.Sync;

namespace PgProj.Core.Tests;

/// <summary>
/// Reverse-sync (scenario 3, "pull"/"drift") planning. Self-contained: builds a throwaway project on
/// disk and an in-memory "live" model, with no database — the engine is pure (PgProj.Core), which is
/// what lets the future Visual Studio tooling drive it too.
/// </summary>
public sealed class ReverseSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgproj_rsync_" + Guid.NewGuid().ToString("N")[..10]);

    public ReverseSyncTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private DatabaseProject Project(params (string rel, string sql)[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "P.pgproj"),
            "<Project><PropertyGroup><Name>P</Name><DefaultSchema>public</DefaultSchema></PropertyGroup>" +
            "<ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
        foreach (var (rel, sql) in files)
        {
            var path = Path.Combine(_dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sql);
        }
        return DatabaseProject.Load(Path.Combine(_dir, "P.pgproj"));
    }

    private static DatabaseModel Live(string sql) => new ModelBuilder("public").Build(new PgParser().Parse(sql));

    [Fact]
    public void No_drift_when_project_matches_database()
    {
        var project = Project(("Tables/public.t.sql", "CREATE TABLE public.t (id int);"));
        var plan = ReverseSync.Plan(project, Live("CREATE TABLE public.t (id int);"));
        Assert.False(plan.HasDrift);
        Assert.Empty(plan.FileChanges);
    }

    [Fact]
    public void Captures_a_prod_hotfix_column_into_the_owning_file()
    {
        // Project has a 1-column table; the DB has gained a column (the "urgent production fix").
        var project = Project(("Tables/public.t.sql", "CREATE TABLE public.t (id int);"));
        var plan = ReverseSync.Plan(project, Live("CREATE TABLE public.t (id int, name text);"));

        var edit = Assert.Single(plan.FileChanges);
        Assert.Equal(ProjectFileChangeKind.Update, edit.Kind);
        Assert.Equal(Path.Combine("Tables", "public.t.sql"), edit.RelativePath);
        Assert.Contains("name", edit.NewContent);
        Assert.False(edit.IsDestructive);

        // Applying it makes the project build the new shape.
        ReverseSync.Apply(project, plan);
        var rebuilt = project.Build().Model.Tables.Single(t => DatabaseModel.NameEquals(t.Name, "t"));
        Assert.Contains(rebuilt.Columns, c => DatabaseModel.NameEquals(c.Name, "name"));
    }

    [Fact]
    public void Creates_a_file_for_an_object_new_in_the_database()
    {
        var project = Project(("Tables/public.t.sql", "CREATE TABLE public.t (id int);"));
        var plan = ReverseSync.Plan(project, Live("CREATE TABLE public.t (id int); CREATE TABLE public.t2 (id int);"));

        var created = Assert.Single(plan.FileChanges, f => f.Kind == ProjectFileChangeKind.Create);
        Assert.Equal("Tables/public.t2.sql", created.RelativePath.Replace('\\', '/'));

        ReverseSync.Apply(project, plan);
        Assert.True(File.Exists(Path.Combine(_dir, "Tables", "public.t2.sql")));
        Assert.Contains(project.Build().Model.Tables, t => DatabaseModel.NameEquals(t.Name, "t2"));
    }

    [Fact]
    public void Drop_in_database_deletes_the_file_only_with_AllowDeletes()
    {
        var files = new[]
        {
            ("Tables/public.keep.sql", "CREATE TABLE public.keep (id int);"),
            ("Tables/public.gone.sql", "CREATE TABLE public.gone (id int);"),
        };

        // Default: no deletes — the file survives even though the object is gone from the DB.
        var p1 = Project(files);
        var safe = ReverseSync.Plan(p1, Live("CREATE TABLE public.keep (id int);"));
        Assert.DoesNotContain(safe.FileChanges, f => f.Kind == ProjectFileChangeKind.Delete);

        // Opt in: the orphaned file is deleted.
        var p2 = Project(files);
        var plan = ReverseSync.Plan(p2, Live("CREATE TABLE public.keep (id int);"), new DriftOptions { AllowDeletes = true });
        var del = Assert.Single(plan.FileChanges, f => f.Kind == ProjectFileChangeKind.Delete);
        Assert.Equal(Path.Combine("Tables", "public.gone.sql"), del.RelativePath);
        Assert.True(del.IsDestructive);

        ReverseSync.Apply(p2, plan);
        Assert.False(File.Exists(Path.Combine(_dir, "Tables", "public.gone.sql")));
        Assert.True(File.Exists(Path.Combine(_dir, "Tables", "public.keep.sql")));
    }

    [Fact]
    public void Edit_preserves_a_nonconventional_file_name()
    {
        // The object lives in a file NOT named by the canonical convention; pull must rewrite THAT file,
        // not scatter a second canonical copy (which would double-define the object).
        var project = Project(("schema/my_table.sql", "CREATE TABLE public.t (id int);"));
        var plan = ReverseSync.Plan(project, Live("CREATE TABLE public.t (id int, extra text);"));

        var edit = Assert.Single(plan.FileChanges);
        Assert.Equal(ProjectFileChangeKind.Update, edit.Kind);
        Assert.Equal(Path.Combine("schema", "my_table.sql"), edit.RelativePath);   // same file, preserved

        ReverseSync.Apply(project, plan);
        Assert.False(File.Exists(Path.Combine(_dir, "Tables", "public.t.sql")));    // no stray canonical copy
        Assert.Single(project.Build().Model.Tables);                                // still exactly one table
    }
}
