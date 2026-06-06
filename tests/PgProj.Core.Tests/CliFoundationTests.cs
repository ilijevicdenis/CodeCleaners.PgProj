using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Cli;
using PgProj.Core.Packaging;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Exhaustive tests for the shared CLI foundation (PgProj.Core.Cli) that every SSDT-parity wave-2 epic
/// builds on: the <see cref="CliArgs"/> grammar, <see cref="OutputFormats"/> parsing, the
/// <see cref="ExitCode"/> taxonomy contract, and <see cref="EndpointResolver"/> classification. If these
/// hold, an epic adding a new option/format/exit-class inherits correct parsing for free.
/// </summary>
public sealed class CliFoundationTests
{
    // ---- CliArgs: options -----------------------------------------------------------------

    [Fact]
    public void GetOption_returns_value_following_the_flag()
    {
        var args = new CliArgs(new[] { "build", "proj.pgproj", "-o", "out.json" });
        Assert.Equal("out.json", args.GetOption("-o", "--output"));
    }

    [Fact]
    public void GetOption_matches_any_alias_case_insensitively()
    {
        var args = new CliArgs(new[] { "build", "--OUTPUT", "x" });
        Assert.Equal("x", args.GetOption("-o", "--output"));
    }

    [Fact]
    public void GetOption_returns_null_when_absent()
    {
        var args = new CliArgs(new[] { "build", "proj.pgproj" });
        Assert.Null(args.GetOption("-o", "--output"));
    }

    [Fact]
    public void GetOption_returns_first_when_repeated()
    {
        var args = new CliArgs(new[] { "build", "-o", "first", "-o", "second" });
        Assert.Equal("first", args.GetOption("-o"));
    }

    [Fact]
    public void GetOption_ignores_a_trailing_option_with_no_value()
    {
        // "--output" is the very last token → there is no value after it.
        var args = new CliArgs(new[] { "build", "proj.pgproj", "--output" });
        Assert.Null(args.GetOption("--output"));
    }

    // ---- CliArgs: flags -------------------------------------------------------------------

    [Theory]
    [InlineData("--strict", true)]
    [InlineData("--STRICT", true)]
    [InlineData("--no-analyze", false)]
    public void HasFlag_detects_presence_case_insensitively(string present, bool _)
    {
        var args = new CliArgs(new[] { "build", "proj.pgproj", present });
        Assert.True(args.HasFlag("--strict") || args.HasFlag("--no-analyze"));
    }

    [Fact]
    public void HasFlag_is_false_when_absent()
    {
        var args = new CliArgs(new[] { "build", "proj.pgproj" });
        Assert.False(args.HasFlag("--strict"));
    }

    // ---- CliArgs: repeatable Name=Value ---------------------------------------------------

    [Fact]
    public void GetOptionValues_collects_every_occurrence_in_order()
    {
        var args = new CliArgs(new[] { "publish", "--var", "A=1", "--var", "B=2" });
        Assert.Equal(new[] { "A=1", "B=2" }, args.GetOptionValues("--var").ToArray());
    }

    [Fact]
    public void GetKeyValues_parses_pairs_into_a_case_insensitive_map()
    {
        var args = new CliArgs(new[] { "publish", "--var", "Env=prod", "--var", "Schema=app" });
        var map = args.GetKeyValues("--var");
        Assert.Equal("prod", map["env"]);   // case-insensitive lookup
        Assert.Equal("app", map["Schema"]);
    }

    [Fact]
    public void GetKeyValues_later_value_wins()
    {
        var args = new CliArgs(new[] { "publish", "--var", "Env=dev", "--var", "Env=prod" });
        Assert.Equal("prod", args.GetKeyValues("--var")["Env"]);
    }

    [Fact]
    public void GetKeyValues_preserves_equals_signs_inside_the_value()
    {
        // A connection-string-ish value may contain '='; only the first '=' splits name from value.
        var args = new CliArgs(new[] { "publish", "--var", "Conn=Host=db;Port=5432" });
        Assert.Equal("Host=db;Port=5432", args.GetKeyValues("--var")["Conn"]);
    }

    [Theory]
    [InlineData("NoEquals")]
    [InlineData("=NoName")]
    public void GetKeyValues_rejects_malformed_pairs(string bad)
    {
        var args = new CliArgs(new[] { "publish", "--var", bad });
        Assert.Throws<CliUsageException>(() => args.GetKeyValues("--var"));
    }

    // ---- CliArgs: positionals -------------------------------------------------------------

    [Fact]
    public void Positional_skips_the_verb_and_indexes_non_option_tokens()
    {
        var args = new CliArgs(new[] { "add", "table", "app.Customer", "--force" });
        Assert.Equal("table", args.Positional(0));
        Assert.Equal("app.Customer", args.Positional(1));
        Assert.Null(args.Positional(2));            // --force is an option, not a positional
    }

    [Fact]
    public void RequirePositional_throws_a_usage_error_when_missing()
    {
        var args = new CliArgs(new[] { "build" });
        var ex = Assert.Throws<CliUsageException>(() => args.RequirePositional("project file"));
        Assert.Contains("project file", ex.Message);
    }

    [Fact]
    public void Verb_is_lowercased()
    {
        Assert.Equal("build", new CliArgs(new[] { "BUILD", "x" }).Verb);
        Assert.Equal("", new CliArgs(Array.Empty<string>()).Verb);
    }

    // ---- CliArgs: connection --------------------------------------------------------------

    [Fact]
    public void RequireConnection_prefers_the_explicit_option()
    {
        var args = new CliArgs(new[] { "compare", "proj.pgproj", "--connection", "Host=local" });
        Assert.Equal("Host=local", args.RequireConnection());
    }

    [Fact]
    public void RequireConnection_falls_back_to_the_environment_variable()
    {
        var prior = Environment.GetEnvironmentVariable("PGPROJ_CONNECTION");
        try
        {
            Environment.SetEnvironmentVariable("PGPROJ_CONNECTION", "Host=from-env");
            var args = new CliArgs(new[] { "compare", "proj.pgproj" });
            Assert.Equal("Host=from-env", args.RequireConnection());
        }
        finally { Environment.SetEnvironmentVariable("PGPROJ_CONNECTION", prior); }
    }

    [Fact]
    public void RequireConnection_throws_when_neither_is_present()
    {
        var prior = Environment.GetEnvironmentVariable("PGPROJ_CONNECTION");
        try
        {
            Environment.SetEnvironmentVariable("PGPROJ_CONNECTION", null);
            var args = new CliArgs(new[] { "compare", "proj.pgproj" });
            Assert.Throws<CliUsageException>(() => args.RequireConnection());
        }
        finally { Environment.SetEnvironmentVariable("PGPROJ_CONNECTION", prior); }
    }

    // ---- OutputFormat ---------------------------------------------------------------------

    [Theory]
    [InlineData(null, OutputFormat.Text)]
    [InlineData("", OutputFormat.Text)]
    [InlineData("text", OutputFormat.Text)]
    [InlineData("TEXT", OutputFormat.Text)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("Json", OutputFormat.Json)]
    [InlineData("sarif", OutputFormat.Sarif)]
    [InlineData("  sarif  ", OutputFormat.Sarif)]
    public void OutputFormats_Parse_maps_known_values(string? input, OutputFormat expected)
    {
        Assert.Equal(expected, OutputFormats.Parse(input));
    }

    [Fact]
    public void OutputFormats_Parse_rejects_an_unknown_format()
    {
        Assert.Throws<CliUsageException>(() => OutputFormats.Parse("yaml"));
    }

    [Fact]
    public void CliArgs_Format_and_WantsJson_read_the_format_option()
    {
        Assert.Equal(OutputFormat.Sarif, new CliArgs(new[] { "analyze", "p", "--format", "sarif" }).Format);
        Assert.True(new CliArgs(new[] { "build", "p", "--format", "json" }).WantsJson);
        Assert.False(new CliArgs(new[] { "build", "p" }).WantsJson);
    }

    // ---- ExitCode: the CI contract --------------------------------------------------------

    [Fact]
    public void ExitCode_success_is_zero_and_all_failure_classes_are_distinct_and_nonzero()
    {
        Assert.Equal(0, ExitCode.Success);

        var failureCodes = new[]
        {
            ExitCode.Error, ExitCode.Usage, ExitCode.BuildError, ExitCode.AnalysisBlocked,
            ExitCode.ReferenceError, ExitCode.Drift, ExitCode.DeployError, ExitCode.ValidationFailed,
        };
        Assert.All(failureCodes, c => Assert.NotEqual(0, c));
        Assert.Equal(failureCodes.Length, failureCodes.Distinct().Count());
    }

    [Fact]
    public void ExitCode_values_are_pinned_so_pipelines_do_not_silently_shift()
    {
        // Append-only contract: changing any of these is a breaking change for CI gates (EP-CICD).
        Assert.Equal(0, ExitCode.Success);
        Assert.Equal(1, ExitCode.Error);
        Assert.Equal(2, ExitCode.Usage);
        Assert.Equal(3, ExitCode.BuildError);
        Assert.Equal(4, ExitCode.AnalysisBlocked);
        Assert.Equal(5, ExitCode.ReferenceError);
        Assert.Equal(6, ExitCode.Drift);
        Assert.Equal(7, ExitCode.DeployError);
        Assert.Equal(8, ExitCode.ValidationFailed);
    }

    // ---- EndpointResolver: classification -------------------------------------------------

    [Theory]
    [InlineData("db.pgpkg", EndpointKind.Package)]
    [InlineData("C:/a/b/My.PGPKG", EndpointKind.Package)]
    [InlineData("MyProject.pgproj", EndpointKind.Project)]
    [InlineData("rel/path/App.pgproj", EndpointKind.Project)]
    [InlineData("Host=localhost;Port=5432;Database=app", EndpointKind.LiveDatabase)]
    [InlineData("postgres://user@host/db", EndpointKind.LiveDatabase)]
    public void Classify_routes_specs_by_shape(string spec, EndpointKind expected)
    {
        Assert.Equal(expected, EndpointResolver.Classify(spec));
    }

    [Fact]
    public void Classify_treats_an_existing_file_as_a_project_even_without_a_pgproj_extension()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pgproj_cls_{Guid.NewGuid():N}.proj");
        File.WriteAllText(tmp, "<Project/>");
        try { Assert.Equal(EndpointKind.Project, EndpointResolver.Classify(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Classify_rejects_an_empty_spec()
    {
        Assert.Throws<CliUsageException>(() => EndpointResolver.Classify("   "));
    }

    // ---- EndpointResolver: resolution (no live DB needed) ---------------------------------

    [Fact]
    public async Task ResolveAsync_builds_a_project_into_a_model()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "App.pgproj"),
            """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup><Name>App</Name><DefaultSchema>public</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "customer.sql"),
            "CREATE TABLE public.customer (id int PRIMARY KEY, name text NOT NULL);");

        var resolved = await EndpointResolver.ResolveAsync(Path.Combine(dir.Path, "App.pgproj"));

        Assert.Equal(EndpointKind.Project, resolved.Kind);
        Assert.NotNull(resolved.Project);
        Assert.Equal("App", resolved.DisplayName);
        Assert.Empty(resolved.BuildDiagnostics);
        Assert.Contains(resolved.Model.Tables, t => t.Name.Equals("customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_surfaces_build_diagnostics_without_throwing()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Bad.pgproj"),
            """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup><Name>Bad</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "broken.sql"), "CREATE TABLE ;;; not valid sql");

        var resolved = await EndpointResolver.ResolveAsync(Path.Combine(dir.Path, "Bad.pgproj"));

        Assert.Equal(EndpointKind.Project, resolved.Kind);
        Assert.NotEmpty(resolved.BuildDiagnostics);   // a bad source reports, it does not crash the resolver
    }

    [Fact]
    public async Task ResolveAsync_loads_a_package_model_without_a_project()
    {
        using var dir = new TempDir();
        // Build a project, then build a .pgpkg from it via the same pipeline the CLI uses.
        File.WriteAllText(Path.Combine(dir.Path, "Pkg.pgproj"),
            """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup><Name>Pkg</Name><DefaultSchema>public</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "t.sql"), "CREATE TABLE public.t (id int PRIMARY KEY);");

        var project = PgProj.Core.Project.DatabaseProject.Load(Path.Combine(dir.Path, "Pkg.pgproj"));
        var build = await project.BuildAsync();
        var pkgPath = Path.Combine(dir.Path, "Pkg.pgpkg");
        var pkg = PgPkgBuilder.FromBuild(project, build.Model, build.Files, "0.0.0-test", "2026-01-01T00:00:00Z");
        pkg.Write(pkgPath);

        var resolved = await EndpointResolver.ResolveAsync(pkgPath);

        Assert.Equal(EndpointKind.Package, resolved.Kind);
        Assert.Null(resolved.Project);
        Assert.Equal("Pkg", resolved.DisplayName);
        Assert.Contains(resolved.Model.Tables, t => t.Name.Equals("t", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj_ep_{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ } }
    }
}
