using System;
using System.IO;
using System.Linq;
using PgProj.Core.Project;
using PgProj.Core.Refactoring;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #152 — the <c>expand-wildcards</c> refactor. Rewrites <c>SELECT *</c> / <c>alias.*</c> in a view to
/// an explicit, model-resolved column list, touching only the star tokens (the rest of the file stays
/// byte-identical) and recording the operation in <c>.pgrefactorlog</c>.
/// </summary>
public sealed class ExpandWildcardsTests
{
    private static string NewProject(string dir, params (string File, string Sql)[] files)
    {
        Directory.CreateDirectory(dir);
        var proj = Path.Combine(dir, "App.pgproj");
        File.WriteAllText(proj,
            """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup><Name>App</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        foreach (var (file, sql) in files) File.WriteAllText(Path.Combine(dir, file), sql);
        return proj;
    }

    private static RefactorResult Run(string dir, string view, params (string, string)[] files)
    {
        var proj = NewProject(dir, files);
        return RefactorEngine.ExpandWildcards(DatabaseProject.Load(proj), view);
    }

    [Fact]
    public void Single_table_star_expands_unqualified()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            var r = Run(dir, "app.v_orders",
                ("orders.sql", "CREATE SCHEMA app;\nCREATE TABLE app.orders (id int PRIMARY KEY, customer_id int, amount numeric);"),
                ("v.sql", "CREATE VIEW app.v_orders AS SELECT * FROM app.orders;"));

            var body = File.ReadAllText(Path.Combine(dir, "v.sql"));
            Assert.Contains("SELECT id, customer_id, amount FROM app.orders", body);
            Assert.DoesNotContain("SELECT *", body);
            Assert.Equal(1, r.Replacements);
            Assert.Equal("expand-wildcards", r.Entry.Operation);
            Assert.Equal("view", r.Entry.ObjectType);

            var log = RefactorLog.Load(RefactorLog.PathFor(Path.Combine(dir, "App.pgproj")));
            Assert.Single(log.Entries);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Qualified_star_expands_and_keeps_other_items_byte_identical()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            Run(dir, "app.v_join",
                ("t.sql", "CREATE SCHEMA app;\nCREATE TABLE app.orders (id int PRIMARY KEY, total numeric);\nCREATE TABLE app.cust (id int PRIMARY KEY, name text);"),
                ("v.sql", "CREATE VIEW app.v_join AS SELECT o.*, c.name AS who FROM app.orders o JOIN app.cust c ON c.id = o.id;"));

            var body = File.ReadAllText(Path.Combine(dir, "v.sql"));
            // o.* expanded with the alias; the explicit c.name AS who item is untouched.
            Assert.Contains("SELECT o.id, o.total, c.name AS who FROM", body);
            Assert.Contains("c.name AS who", body);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Bare_star_over_two_sources_qualifies_each_column()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            Run(dir, "app.v_two",
                ("t.sql", "CREATE SCHEMA app;\nCREATE TABLE app.a (x int PRIMARY KEY);\nCREATE TABLE app.b (y int PRIMARY KEY);"),
                ("v.sql", "CREATE VIEW app.v_two AS SELECT * FROM app.a JOIN app.b ON a.x = b.y;"));

            var body = File.ReadAllText(Path.Combine(dir, "v.sql"));
            Assert.Contains("SELECT a.x, b.y FROM", body);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Count_star_and_string_literals_are_not_touched()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            Run(dir, "app.v_agg",
                ("t.sql", "CREATE SCHEMA app;\nCREATE TABLE app.orders (id int PRIMARY KEY, kind text);"),
                ("v.sql", "CREATE VIEW app.v_agg AS SELECT count(*) AS n, '*' AS lit, id FROM app.orders GROUP BY id;"));

            var body = File.ReadAllText(Path.Combine(dir, "v.sql"));
            // No top-level star item → nothing changes; count(*) and '*' survive verbatim.
            Assert.Contains("count(*) AS n", body);
            Assert.Contains("'*' AS lit", body);
        }
        catch (RefactorException)
        {
            // Acceptable: the view has no expandable top-level star, so the engine reports "nothing to expand".
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void View_without_a_star_reports_nothing_to_expand()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            var ex = Assert.Throws<RefactorException>(() => Run(dir, "app.v_explicit",
                ("t.sql", "CREATE SCHEMA app;\nCREATE TABLE app.orders (id int PRIMARY KEY);"),
                ("v.sql", "CREATE VIEW app.v_explicit AS SELECT id FROM app.orders;")));
            Assert.Contains("no SELECT *", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Missing_view_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_xw_{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<RefactorException>(() => Run(dir, "app.nope",
                ("t.sql", "CREATE SCHEMA app;\nCREATE TABLE app.orders (id int PRIMARY KEY);")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
