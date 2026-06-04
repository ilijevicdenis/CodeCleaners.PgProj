using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>EP-VARS: $(Var) substitution, precedence, unresolved diagnostics, escaping, banner.</summary>
public class SqlCmdVariableTests
{
    private static IReadOnlyDictionary<string, string> Map(params (string K, string V)[] kv) =>
        kv.ToDictionary(p => p.K, p => p.V, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Substitutes_token_with_resolved_value()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("EnvSuffix", "dev")));
        var result = r.Substitute("CREATE SCHEMA app_$(EnvSuffix);", "x.sql");
        Assert.Equal("CREATE SCHEMA app_dev;", result);
    }

    [Fact]
    public void Cli_override_beats_project_default()
    {
        var r = SqlCmdVariableResolver.Build(
            defaults: Map(("EnvSuffix", "dev")),
            cliOverrides: Map(("EnvSuffix", "prod")));
        Assert.Equal("prod", r.Values["EnvSuffix"]);
        Assert.Equal("app_prod", r.Substitute("app_$(EnvSuffix)", "x.sql"));
    }

    [Fact]
    public void Precedence_is_default_then_profile_then_cli()
    {
        var r = SqlCmdVariableResolver.Build(
            defaults: Map(("V", "a")),
            profile: Map(("V", "b")),
            cliOverrides: Map(("V", "c")));
        Assert.Equal("c", r.Values["V"]);

        // No CLI → profile wins over default.
        var r2 = SqlCmdVariableResolver.Build(defaults: Map(("V", "a")), profile: Map(("V", "b")));
        Assert.Equal("b", r2.Values["V"]);
    }

    [Fact]
    public void Variable_names_are_case_insensitive()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("EnvSuffix", "dev")));
        Assert.Equal("app_dev", r.Substitute("app_$(envsuffix)", "x.sql"));
    }

    [Fact]
    public void Unresolved_token_throws_with_line_and_column()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("Known", "1")));
        var body = "line one\nok $(Missing) tail";   // token starts at line 2, column 4
        var ex = Assert.Throws<SqlCmdVariableException>(() => r.Substitute(body, "Post.sql"));
        Assert.Contains("Post.sql(2,4)", ex.Message);
        Assert.Contains("Missing", ex.Message);
    }

    [Fact]
    public void Unresolved_token_reports_known_variables()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("Alpha", "1"), ("Beta", "2")));
        var ex = Assert.Throws<SqlCmdVariableException>(() => r.Substitute("$(Gamma)", "f.sql"));
        Assert.Contains("Alpha", ex.Message);
        Assert.Contains("Beta", ex.Message);
    }

    [Fact]
    public void Unterminated_token_is_an_error()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("V", "1")));
        var ex = Assert.Throws<SqlCmdVariableException>(() => r.Substitute("oops $(V no-close\nnext", "f.sql"));
        Assert.Contains("unterminated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dollar_escape_emits_literal_token_without_substitution()
    {
        // "$$(" -> literal "$(" and the name is NOT substituted (even though Foo is undeclared).
        var r = SqlCmdVariableResolver.Build(defaults: Map(("Foo", "should-not-be-used")));
        Assert.Equal("emit $(Foo) verbatim", r.Substitute("emit $$(Foo) verbatim", "f.sql"));
    }

    [Fact]
    public void Single_dollar_paren_in_plpgsql_is_left_alone()
    {
        // A lone "$(" that is not a known token still errors (it looks like a token) — but a dollar that
        // is not followed by "(" (e.g. dollar-quoting $$ or $1) passes through untouched.
        var r = SqlCmdVariableResolver.Build();
        Assert.Equal("body $$ x $1 end", r.Substitute("body $$ x $1 end", "f.sql"));
    }

    [Fact]
    public void Banner_contains_the_resolved_map()
    {
        var r = SqlCmdVariableResolver.Build(defaults: Map(("EnvSuffix", "prod"), ("Owner", "ops")));
        var banner = string.Join("\n", r.BannerLines());
        Assert.Contains("SQLCMD variables:", banner);
        Assert.Contains("EnvSuffix = prod", banner);
        Assert.Contains("Owner = ops", banner);
    }

    [Fact]
    public void Empty_resolver_banner_says_none()
    {
        var banner = string.Join("\n", SqlCmdVariableResolver.Build().BannerLines());
        Assert.Contains("(none)", banner);
    }

    [Fact]
    public void Generator_substitutes_in_deploy_scripts_and_echoes_banner()
    {
        var vars = SqlCmdVariableResolver.Build(
            defaults: Map(("EnvSuffix", "dev")),
            cliOverrides: Map(("EnvSuffix", "prod")));
        var script = new DeployScriptGenerator().Generate(Array.Empty<SchemaChange>(), new DeployOptions
        {
            IncludeHeader = true,
            Variables = vars,
            Scripts = new DeployScriptBundle(Post: new DeployScript("Post.sql", "CREATE SCHEMA app_$(EnvSuffix);")),
        });

        Assert.Contains("CREATE SCHEMA app_prod;", script);     // substituted
        Assert.Contains("EnvSuffix = prod", script);            // banner echo
        Assert.DoesNotContain("$(EnvSuffix)", script);          // never emitted verbatim
    }

    [Fact]
    public void Generator_throws_on_unresolved_token_in_deploy_script()
    {
        var vars = SqlCmdVariableResolver.Build();
        Assert.Throws<SqlCmdVariableException>(() =>
            new DeployScriptGenerator().Generate(Array.Empty<SchemaChange>(), new DeployOptions
            {
                Variables = vars,
                Scripts = new DeployScriptBundle(Post: new DeployScript("Post.sql", "SELECT $(Nope);")),
            }));
    }
}
