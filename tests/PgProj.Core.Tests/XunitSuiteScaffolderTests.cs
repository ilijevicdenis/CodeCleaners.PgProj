using System.IO;
using System.Linq;
using PgProj.Core.Testing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>Unit coverage for the standalone-xUnit test-project generator (#153, replaces the .test.sql path).</summary>
public class XunitSuiteScaffolderTests
{
    private const string Schema = @"
        CREATE TABLE sales.customers (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, email text NOT NULL UNIQUE);
        CREATE TABLE sales.orders (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id int NOT NULL REFERENCES sales.customers(id), note text);
        CREATE VIEW sales.v_orders AS SELECT id FROM sales.orders;";

    private static GeneratedTestProject Generate(XunitSuiteOptions? categories = null, XunitSuiteSettings? settings = null) =>
        XunitSuiteScaffolder.Generate(
            TestModel.Build(Schema, defaultSchema: "sales"),
            (settings ?? new XunitSuiteSettings()) with
            {
                RootNamespace = "Demo.Tests",
                TestProjectName = "Demo.Tests",
                Categories = categories ?? XunitSuiteOptions.All,
            });

    private static string File(GeneratedTestProject p, string endsWith) =>
        p.Files.Single(f => f.RelativePath.EndsWith(endsWith)).Content;

    [Fact]
    public void DefaultOutputDirectory_is_a_sibling_outside_the_project_glob_root()
    {
        // #166: a .pgproj globs **/*.sql rooted at its own directory. If the generated suite (whose
        // schema.sql is a bare, non-underscored file) lands under that directory, it's swept up as
        // duplicate database objects and breaks the build. The default must be a sibling, not a child.
        var projectDir = Path.Combine(Path.GetTempPath(), "mydb");
        var outDir = XunitSuiteScaffolder.DefaultOutputDirectory(projectDir, "mydb.Tests");

        Assert.Equal("mydb.Tests", Path.GetFileName(outDir));
        // Same parent as the project dir → a sibling, and NOT nested inside the project (glob) root.
        Assert.Equal(Directory.GetParent(projectDir)!.FullName, Directory.GetParent(outDir)!.FullName);
        Assert.StartsWith("..", Path.GetRelativePath(projectDir, outDir));
    }

    [Fact]
    public void Emits_the_project_scaffold_once_files()
    {
        var p = Generate();
        // csproj + fixture + base + usings are scaffold-once (never overwritten on regen).
        foreach (var name in new[] { "Demo.Tests.csproj", "PgDatabaseFixture.cs", "PgTestBase.cs", "GlobalUsings.cs" })
            Assert.False(p.Files.Single(f => f.RelativePath == name).Overwrite, name);

        var csproj = File(p, ".csproj");
        Assert.Contains("Testcontainers.PostgreSql", csproj);
        Assert.Contains("xunit.runner.visualstudio", csproj);   // Test Explorer discovery, no special tooling
        Assert.Contains("Npgsql", csproj);
    }

    [Fact]
    public void Fixture_spins_a_container_and_deploys_order_tolerant()
    {
        var fixture = File(Generate(), "PgDatabaseFixture.cs");
        Assert.Contains("PostgreSqlBuilder", fixture);
        Assert.Contains("ICollectionFixture<PgDatabaseFixture>", fixture);
        Assert.Contains("IsMissingDependency", fixture);         // the deploy retry loop
    }

    [Fact]
    public void Fixture_falls_back_to_an_existing_connection_when_set()
    {
        var fixture = File(Generate(), "PgDatabaseFixture.cs");
        // env-var escape hatch: use a real Postgres (throwaway DB) instead of Docker when provided.
        Assert.Contains("PGPROJ_TEST_CONNECTION", fixture);
        Assert.Contains("CREATE DATABASE", fixture);
        Assert.Contains("DROP DATABASE IF EXISTS", fixture);
    }

    [Fact]
    public void Per_table_class_has_a_regen_safe_seed_hook()
    {
        var p = Generate();
        var gen = p.Files.Single(f => f.RelativePath.EndsWith("sales_customers_Tests.g.cs"));
        Assert.True(gen.Overwrite);                              // regenerated
        Assert.Contains("partial void Seed(NpgsqlConnection conn, NpgsqlTransaction tx);", gen.Content);
        Assert.Contains("[Collection(PgDatabaseCollection.Name)]", gen.Content);

        var seed = p.Files.Single(f => f.RelativePath.EndsWith("sales_customers_Tests.Seed.cs"));
        Assert.False(seed.Overwrite);                            // never overwritten
        Assert.Contains("partial void Seed(NpgsqlConnection conn, NpgsqlTransaction tx)", seed.Content);
    }

    [Fact]
    public void Constraint_negatives_assert_the_expected_sqlstate()
    {
        var customers = File(Generate(), "sales_customers_Tests.g.cs");
        Assert.Contains("AssertSqlState(\"23502\"", customers);  // NOT NULL email
        Assert.Contains("AssertSqlState(\"23505\"", customers);  // PK or UNIQUE duplicate

        var orders = File(Generate(), "sales_orders_Tests.g.cs");
        Assert.Contains("AssertSqlState(\"23503\"", orders);     // FK orphan customer_id
        Assert.Contains("[Fact]", orders);
    }

    [Fact]
    public void Crud_seeds_the_depth_one_fk_parent_before_inserting()
    {
        var orders = File(Generate(), "sales_orders_Tests.g.cs");
        Assert.Contains("Crud_insert_roundtrip", orders);
        // the order row needs a customer parent first — synthesised via a seeded parent insert
        Assert.Contains("INSERT INTO sales.customers", orders);
        Assert.Contains("OVERRIDING SYSTEM VALUE", orders);      // identity-always parent key
    }

    [Fact]
    public void Schema_sql_is_statement_delimited_for_order_tolerant_deploy()
    {
        var p = Generate();
        var schemaFile = p.Files.Single(f => f.RelativePath == "schema.sql");
        Assert.True(schemaFile.Overwrite);                        // regenerated each run
        Assert.Contains("-- @pgproj-stmt", schemaFile.Content);
        Assert.Contains("CREATE TABLE", schemaFile.Content);
    }

    [Fact]
    public void Category_filter_limits_the_emitted_tests()
    {
        // Only CRUD → no constraint/fk negatives in the table class.
        var p = Generate(new XunitSuiteOptions { Constraints = false, ForeignKeys = false, Crud = true, Views = false, UnitStubs = false, Existence = false });
        var orders = File(p, "sales_orders_Tests.g.cs");
        Assert.Contains("Crud_insert_roundtrip", orders);
        Assert.DoesNotContain("AssertSqlState(\"23503\"", orders);
        Assert.DoesNotContain("AssertSqlState(\"23502\"", orders);
        // views off → no ViewTests file
        Assert.DoesNotContain(p.Files, f => f.RelativePath.EndsWith("ViewTests.g.cs"));
    }

    [Fact]
    public void Views_are_queried_with_limit_zero()
    {
        var views = File(Generate(), "ViewTests.g.cs");
        Assert.Contains("SELECT * FROM sales.v_orders LIMIT 0", views);
    }

    // ==== db-mode selection (#161) =====================================================================

    [Fact]
    public void Auto_mode_fixture_supports_both_container_and_env_var()
    {
        var fixture = File(Generate(), "PgDatabaseFixture.cs");
        Assert.Contains("PostgreSqlBuilder", fixture);
        Assert.Contains("PGPROJ_TEST_CONNECTION", fixture);
        // Docker failure is translated into a message naming the escape hatch, not a raw stack trace.
        Assert.Contains("is a Docker daemon running", fixture);
    }

    [Fact]
    public void Container_mode_pins_docker_and_drops_the_env_var_branch()
    {
        var p = Generate(settings: new XunitSuiteSettings { DbMode = XunitDbMode.Testcontainers });
        var fixture = File(p, "PgDatabaseFixture.cs");
        Assert.Contains("PostgreSqlBuilder", fixture);
        Assert.DoesNotContain("CREATE DATABASE", fixture);      // no throwaway-DB branch
        Assert.DoesNotContain("GetEnvironmentVariable", fixture);
    }

    [Fact]
    public void Existing_mode_requires_the_env_var_and_drops_testcontainers()
    {
        var p = Generate(settings: new XunitSuiteSettings { DbMode = XunitDbMode.ExistingConnection });
        var fixture = File(p, "PgDatabaseFixture.cs");
        Assert.DoesNotContain("PostgreSqlBuilder", fixture);
        Assert.DoesNotContain("Testcontainers", fixture);        // no using — the package isn't referenced
        Assert.Contains("PGPROJ_TEST_CONNECTION is not set", fixture);
        Assert.Contains("CREATE DATABASE", fixture);

        var csproj = File(p, ".csproj");
        Assert.DoesNotContain("Testcontainers.PostgreSql", csproj);
    }

    [Fact]
    public void Fixture_cleans_up_eagerly_when_initialize_fails()
    {
        // xUnit v2 may skip DisposeAsync after a failed InitializeAsync — the fixture must clean up itself.
        var fixture = File(Generate(), "PgDatabaseFixture.cs");
        Assert.Contains("await DisposeAsync();", fixture);
        Assert.Contains("throw;", fixture);
    }

    [Fact]
    public void Connection_is_emitted_as_a_gitignored_local_runsettings()
    {
        var p = Generate(settings: new XunitSuiteSettings
        {
            DbMode = XunitDbMode.ExistingConnection,
            TestConnection = "Host=box;Username=u;Password=a&b<c",
        });
        var rs = p.Files.Single(f => f.RelativePath == "Demo.Tests.local.runsettings");
        Assert.True(rs.Overwrite);                               // rewritten whenever a connection is supplied
        Assert.Contains("<PGPROJ_TEST_CONNECTION>Host=box;Username=u;Password=a&amp;b&lt;c</PGPROJ_TEST_CONNECTION>", rs.Content);
        // '--' inside an XML comment is a hard vstest parse error ("cannot contain '--'") — keep the file loadable.
        Assert.DoesNotContain("--", rs.Content.Replace("<!--", "").Replace("-->", ""));

        // the csproj auto-applies it, and the .gitignore keeps the secret out of the repo
        Assert.Contains("RunSettingsFilePath", File(p, ".csproj"));
        Assert.Contains("*.local.runsettings", p.Files.Single(f => f.RelativePath == ".gitignore").Content);
    }

    [Fact]
    public void No_runsettings_is_emitted_without_a_connection()
    {
        Assert.DoesNotContain(Generate().Files, f => f.RelativePath.EndsWith(".local.runsettings"));
    }

    // ==== seed hooks (#161) ============================================================================

    [Fact]
    public void Suite_seed_hook_runs_once_committed_and_is_never_overwritten()
    {
        var p = Generate();
        var fixture = File(p, "PgDatabaseFixture.cs");
        Assert.Contains("partial void SeedSuite(NpgsqlConnection conn, NpgsqlTransaction tx);", fixture);
        Assert.Contains("SeedSuite(conn, seedTx);", fixture);
        Assert.Contains("await seedTx.CommitAsync();", fixture); // committed, unlike the per-test hooks
        Assert.Contains("partial class PgDatabaseFixture", fixture);

        var stub = p.Files.Single(f => f.RelativePath == "Seeds/SuiteSeed.cs");
        Assert.False(stub.Overwrite);
        Assert.Contains("partial void SeedSuite(NpgsqlConnection conn, NpgsqlTransaction tx)", stub.Content);
    }

    [Fact]
    public void No_seeds_skips_the_hook_stubs_but_keeps_the_partial_declarations()
    {
        var p = Generate(settings: new XunitSuiteSettings { GenerateSeedHooks = false });
        Assert.DoesNotContain(p.Files, f => f.RelativePath.StartsWith("Seeds/"));
        // partial declarations stay: with no implementation the compiler elides the call sites.
        Assert.Contains("partial void Seed(NpgsqlConnection conn, NpgsqlTransaction tx);",
            File(p, "sales_customers_Tests.g.cs"));
        Assert.Contains("partial void SeedSuite(NpgsqlConnection conn, NpgsqlTransaction tx);",
            File(p, "PgDatabaseFixture.cs"));
    }

    [Fact]
    public void Tables_expose_a_reusable_baseline_insert_helper()
    {
        var p = Generate();
        var helpers = p.Files.Single(f => f.RelativePath == "Generated/BaselineRows.g.cs");
        Assert.True(helpers.Overwrite);                          // regenerated with the model
        Assert.Contains("public static async Task Insert_sales_orders(NpgsqlConnection conn, NpgsqlTransaction tx)", helpers.Content);
        Assert.Contains("public static async Task Insert_sales_customers(NpgsqlConnection conn, NpgsqlTransaction tx)", helpers.Content);
        Assert.Contains("INSERT INTO sales.customers", helpers.Content);  // orders' helper seeds the FK parent too
    }

    [Fact]
    public void Readme_documents_the_suite_and_is_scaffold_once()
    {
        var readme = Generate().Files.Single(f => f.RelativePath == "README.md");
        Assert.False(readme.Overwrite);
        Assert.Contains("PGPROJ_TEST_CONNECTION", readme.Content);
        Assert.Contains("SuiteSeed", readme.Content);
    }
}
