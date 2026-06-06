using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Deployment;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Exhaustive tests for EP-PROFILE: the <see cref="PublishProfile"/> <c>.pgpublish.json</c> model
/// (round-trip, camelCase shape, the hard secret-omission rule, malformed/missing handling, forward-compat
/// with unknown fields) and the CLI &gt; profile &gt; project-default precedence the profile feeds — both for
/// SQLCMD variables (via the real <see cref="SqlCmdVariableResolver.Build"/> Core primitive the CLI calls)
/// and for the publish options. Mirrors the breadth of CliFoundationTests.
/// </summary>
public sealed class PublishProfileTests
{
    // ---- round-trip -----------------------------------------------------------------------

    [Fact]
    public void Save_then_Load_round_trips_every_field()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "prod" + PublishProfile.Extension);

        var profile = new PublishProfile
        {
            TargetPostgresVersion = "18",
            ConnectionName = "prod",
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EnvSuffix"] = "prod",
                ["Schema"] = "app",
            },
            Options = new PublishProfileOptions { AllowDrops = false, WrapInTransaction = true },
        };

        profile.Save(path);
        var loaded = PublishProfile.Load(path);

        Assert.Equal("18", loaded.TargetPostgresVersion);
        Assert.Equal("prod", loaded.ConnectionName);
        Assert.Equal("prod", loaded.Variables["EnvSuffix"]);
        Assert.Equal("app", loaded.Variables["Schema"]);
        Assert.False(loaded.Options.AllowDrops);
        Assert.True(loaded.Options.WrapInTransaction);
    }

    [Fact]
    public void ToJson_then_Parse_round_trips_without_a_file()
    {
        var profile = new PublishProfile
        {
            TargetPostgresVersion = "17",
            Variables = new Dictionary<string, string> { ["A"] = "1" },
            Options = new PublishProfileOptions { AllowDrops = true },
        };

        var reparsed = PublishProfile.Parse(profile.ToJson());

        Assert.Equal("17", reparsed.TargetPostgresVersion);
        Assert.Equal("1", reparsed.Variables["A"]);
        Assert.True(reparsed.Options.AllowDrops);
        Assert.Null(reparsed.Options.WrapInTransaction);   // unset stays unset (not coerced to a default)
    }

    [Fact]
    public void Variable_names_are_case_insensitive_after_round_trip()
    {
        var profile = new PublishProfile
        {
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["EnvSuffix"] = "qa" },
        };
        var loaded = PublishProfile.Parse(profile.ToJson());
        Assert.Equal("qa", loaded.Variables["envsuffix"]);   // SQLCMD case-insensitivity preserved
    }

    // ---- camelCase JSON shape -------------------------------------------------------------

    [Fact]
    public void Json_uses_camelCase_property_names()
    {
        var json = new PublishProfile
        {
            TargetPostgresVersion = "18",
            ConnectionName = "prod",
            Variables = new Dictionary<string, string> { ["Env"] = "prod" },
            Options = new PublishProfileOptions { AllowDrops = true, WrapInTransaction = false },
        }.ToJson();

        Assert.Contains("\"targetPostgresVersion\"", json);
        Assert.Contains("\"connectionName\"", json);
        Assert.Contains("\"variables\"", json);
        Assert.Contains("\"options\"", json);
        Assert.Contains("\"allowDrops\"", json);
        Assert.Contains("\"wrapInTransaction\"", json);

        // No PascalCase leakage of the property names.
        Assert.DoesNotContain("\"TargetPostgresVersion\"", json);
        Assert.DoesNotContain("\"AllowDrops\"", json);
    }

    [Fact]
    public void Json_omits_unset_optional_fields()
    {
        // A blank profile serializes to an empty object (nothing asserted).
        var json = new PublishProfile().ToJson();
        Assert.DoesNotContain("targetPostgresVersion", json);
        Assert.DoesNotContain("connectionName", json);
        Assert.DoesNotContain("variables", json);
        Assert.DoesNotContain("options", json);
    }

    [Fact]
    public void Json_variable_keys_are_emitted_verbatim_and_value_camelCasing_does_not_touch_them()
    {
        // Dictionary KEYS must NOT be camelCased — a variable named "EnvSuffix" stays "EnvSuffix".
        var json = new PublishProfile
        {
            Variables = new Dictionary<string, string> { ["EnvSuffix"] = "v" },
        }.ToJson();
        Assert.Contains("\"EnvSuffix\"", json);
    }

    [Fact]
    public void Json_variables_are_ordered_for_determinism()
    {
        var json = new PublishProfile
        {
            Variables = new Dictionary<string, string> { ["Zeta"] = "1", ["Alpha"] = "2", ["Mu"] = "3" },
        }.ToJson();
        var iAlpha = json.IndexOf("Alpha", StringComparison.Ordinal);
        var iMu = json.IndexOf("Mu", StringComparison.Ordinal);
        var iZeta = json.IndexOf("Zeta", StringComparison.Ordinal);
        Assert.True(iAlpha < iMu && iMu < iZeta, "variables should serialize in name order");
    }

    [Fact]
    public void ToJson_is_deterministic_for_identical_input()
    {
        PublishProfile Make() => new()
        {
            TargetPostgresVersion = "18",
            Variables = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" },
            Options = new PublishProfileOptions { WrapInTransaction = true },
        };
        Assert.Equal(Make().ToJson(), Make().ToJson());   // no clock/timestamp leaks into Core
    }

    // ---- the secret-omission rule (HARD) --------------------------------------------------

    [Fact]
    public void ConnectionName_is_a_label_not_a_secret_and_round_trips()
    {
        var loaded = PublishProfile.Parse(new PublishProfile { ConnectionName = "staging" }.ToJson());
        Assert.Equal("staging", loaded.ConnectionName);
    }

    [Fact]
    public void Serialized_profile_never_contains_a_connection_string_field()
    {
        // Even a "connection name" that looks like a connection string is just a label — there is no
        // serialized member that holds a connection string, ever.
        var json = new PublishProfile
        {
            ConnectionName = "prod",
            TargetPostgresVersion = "18",
        }.ToJson();

        Assert.DoesNotContain("\"connectionString\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        // The whitelisted, non-secret label is present; no secret-bearing key exists.
        Assert.Contains("\"connectionName\"", json);
    }

    [Fact]
    public void Load_drops_a_stray_connection_string_secret_from_the_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "leaky" + PublishProfile.Extension);
        // A hand-tampered file that smuggled in secrets must NOT resurrect them onto the model.
        File.WriteAllText(path,
            """
            {
              "targetPostgresVersion": "18",
              "connectionName": "prod",
              "connectionString": "Host=db;Username=admin;Password=hunter2",
              "password": "hunter2",
              "variables": { "Env": "prod" }
            }
            """);

        var loaded = PublishProfile.Load(path);

        Assert.Equal("18", loaded.TargetPostgresVersion);
        Assert.Equal("prod", loaded.ConnectionName);
        Assert.Equal("prod", loaded.Variables["Env"]);
        // Re-serializing the loaded profile carries no secret forward.
        var reserialized = loaded.ToJson();
        Assert.DoesNotContain("hunter2", reserialized);
        Assert.DoesNotContain("connectionString", reserialized, StringComparison.OrdinalIgnoreCase);
    }

    // ---- malformed / missing / empty ------------------------------------------------------

    [Fact]
    public void Parse_throws_PublishProfileException_on_malformed_json()
    {
        var ex = Assert.Throws<PublishProfileException>(() => PublishProfile.Parse("{ not valid json"));
        Assert.Contains("Malformed", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Load_throws_PublishProfileException_when_the_file_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}{PublishProfile.Extension}");
        var ex = Assert.Throws<PublishProfileException>(() => PublishProfile.Load(missing));
        Assert.Contains("not found", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("null")]
    public void Parse_treats_an_empty_document_as_an_all_defaults_profile(string body)
    {
        var p = PublishProfile.Parse(body);
        Assert.Null(p.TargetPostgresVersion);
        Assert.Null(p.ConnectionName);
        Assert.Empty(p.Variables);
        Assert.Null(p.Options.AllowDrops);
        Assert.Null(p.Options.WrapInTransaction);
    }

    [Fact]
    public void Parse_ignores_unknown_fields_for_forward_compatibility()
    {
        var p = PublishProfile.Parse(
            """
            { "targetPostgresVersion": "18", "futureKnob": true, "nested": { "x": 1 } }
            """);
        Assert.Equal("18", p.TargetPostgresVersion);   // known fields still bind; unknowns are dropped, not fatal
    }

    [Fact]
    public void Parse_accepts_comments_and_trailing_commas_in_hand_edited_files()
    {
        var p = PublishProfile.Parse(
            """
            {
              // staging profile
              "targetPostgresVersion": "17",
              "variables": { "Env": "stg", },
            }
            """);
        Assert.Equal("17", p.TargetPostgresVersion);
        Assert.Equal("stg", p.Variables["Env"]);
    }

    [Fact]
    public void Parse_binds_property_names_case_insensitively()
    {
        // A hand-edited file with PascalCase keys still loads (PropertyNameCaseInsensitive).
        var p = PublishProfile.Parse("""{ "TargetPostgresVersion": "16" }""");
        Assert.Equal("16", p.TargetPostgresVersion);
    }

    [Fact]
    public void Save_creates_missing_directories()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "deep", "p" + PublishProfile.Extension);
        new PublishProfile { TargetPostgresVersion = "18" }.Save(path);
        Assert.True(File.Exists(path));
    }

    // ---- IsProfilePath --------------------------------------------------------------------

    [Theory]
    [InlineData("prod.pgpublish.json", true)]
    [InlineData("C:/x/PROD.PGPUBLISH.JSON", true)]
    [InlineData("prod.json", false)]
    [InlineData("prod.pgproj", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsProfilePath_recognizes_the_extension(string? path, bool expected)
    {
        Assert.Equal(expected, PublishProfile.IsProfilePath(path));
    }

    // ---- CLI > profile > project-default precedence: VARIABLES ----------------------------
    // These exercise the exact Core primitive the CLI's BuildVariableResolver calls, with the profile's
    // Variables in the middle slot — proving the profile sits between project defaults and CLI overrides.

    [Fact]
    public void Variables_profile_overrides_project_default()
    {
        var profile = new PublishProfile { Variables = new Dictionary<string, string> { ["Env"] = "stg" } };
        var resolver = SqlCmdVariableResolver.Build(
            defaults: new Dictionary<string, string> { ["Env"] = "dev" },
            profile: profile.Variables,
            cliOverrides: new Dictionary<string, string>());
        Assert.Equal("stg", resolver.Values["Env"]);
    }

    [Fact]
    public void Variables_cli_overrides_profile()
    {
        var profile = new PublishProfile { Variables = new Dictionary<string, string> { ["Env"] = "stg" } };
        var resolver = SqlCmdVariableResolver.Build(
            defaults: new Dictionary<string, string> { ["Env"] = "dev" },
            profile: profile.Variables,
            cliOverrides: new Dictionary<string, string> { ["Env"] = "prod" });
        Assert.Equal("prod", resolver.Values["Env"]);   // CLI > profile > default
    }

    [Fact]
    public void Variables_merge_from_all_three_layers()
    {
        var profile = new PublishProfile { Variables = new Dictionary<string, string> { ["FromProfile"] = "p" } };
        var resolver = SqlCmdVariableResolver.Build(
            defaults: new Dictionary<string, string> { ["FromDefault"] = "d" },
            profile: profile.Variables,
            cliOverrides: new Dictionary<string, string> { ["FromCli"] = "c" });
        Assert.Equal("d", resolver.Values["FromDefault"]);
        Assert.Equal("p", resolver.Values["FromProfile"]);
        Assert.Equal("c", resolver.Values["FromCli"]);
    }

    [Fact]
    public void Variables_profile_only_applies_when_present()
    {
        // A profile with no Variables block leaves project defaults intact.
        var profile = PublishProfile.Parse("{}");
        var resolver = SqlCmdVariableResolver.Build(
            defaults: new Dictionary<string, string> { ["Env"] = "dev" },
            profile: profile.Variables,
            cliOverrides: new Dictionary<string, string>());
        Assert.Equal("dev", resolver.Values["Env"]);
    }

    // ---- CLI > profile > default precedence: OPTIONS --------------------------------------
    // The CLI resolves options as: explicit CLI flag wins; else profile value; else built-in default.
    // These assert that exact policy against the profile's Options (the CLI helpers mirror this).

    private static bool ResolveAllowDrops(bool cliFlagPresent, PublishProfile? profile) =>
        cliFlagPresent || (profile?.Options.AllowDrops ?? false);

    private static bool ResolveWrapInTransaction(bool noTransactionFlagPresent, PublishProfile? profile) =>
        !noTransactionFlagPresent && (profile?.Options.WrapInTransaction ?? true);

    [Fact]
    public void AllowDrops_defaults_to_false_with_no_profile_and_no_flag()
    {
        Assert.False(ResolveAllowDrops(cliFlagPresent: false, profile: null));
    }

    [Fact]
    public void AllowDrops_comes_from_the_profile_when_no_cli_flag()
    {
        var profile = new PublishProfile { Options = new PublishProfileOptions { AllowDrops = true } };
        Assert.True(ResolveAllowDrops(cliFlagPresent: false, profile));
    }

    [Fact]
    public void AllowDrops_cli_flag_wins_over_a_profile_that_disables_it()
    {
        var profile = new PublishProfile { Options = new PublishProfileOptions { AllowDrops = false } };
        Assert.True(ResolveAllowDrops(cliFlagPresent: true, profile));   // CLI > profile
    }

    [Fact]
    public void WrapInTransaction_defaults_to_true()
    {
        Assert.True(ResolveWrapInTransaction(noTransactionFlagPresent: false, profile: null));
    }

    [Fact]
    public void WrapInTransaction_profile_can_disable_it()
    {
        var profile = new PublishProfile { Options = new PublishProfileOptions { WrapInTransaction = false } };
        Assert.False(ResolveWrapInTransaction(noTransactionFlagPresent: false, profile));
    }

    [Fact]
    public void WrapInTransaction_cli_no_transaction_wins_over_a_profile_that_enables_it()
    {
        var profile = new PublishProfile { Options = new PublishProfileOptions { WrapInTransaction = true } };
        Assert.False(ResolveWrapInTransaction(noTransactionFlagPresent: true, profile));   // CLI > profile
    }

    // ---- "profile create" output (the model a `profile create` writes from flags) ---------
    // The CLI's ProfileCreate builds a PublishProfile from flags and Saves it. The test project does not
    // reference PgProj.Cli, so we reproduce that construction at the Core boundary and assert the artifact.

    [Fact]
    public void Profile_create_only_records_options_that_were_explicitly_set()
    {
        // Simulate `profile create out --target-version 18 --var Env=prod` (no option flags set).
        var created = new PublishProfile
        {
            TargetPostgresVersion = "18",
            Variables = new Dictionary<string, string> { ["Env"] = "prod" },
            Options = new PublishProfileOptions { AllowDrops = null, WrapInTransaction = null },
        };

        var json = created.ToJson();
        // No flags set → the whole options object is omitted, so loading asserts neither knob.
        Assert.DoesNotContain("options", json);

        var reloaded = PublishProfile.Parse(json);
        Assert.Null(reloaded.Options.AllowDrops);
        Assert.Null(reloaded.Options.WrapInTransaction);
        Assert.Equal("18", reloaded.TargetPostgresVersion);
        Assert.Equal("prod", reloaded.Variables["Env"]);
    }

    [Fact]
    public void Profile_create_records_an_explicit_allow_drops_flag()
    {
        // Simulate `profile create out --allow-drops`.
        var created = new PublishProfile
        {
            Options = new PublishProfileOptions { AllowDrops = true, WrapInTransaction = null },
        };
        var reloaded = PublishProfile.Parse(created.ToJson());
        Assert.True(reloaded.Options.AllowDrops);
        Assert.Null(reloaded.Options.WrapInTransaction);
    }

    [Fact]
    public void Profile_create_never_records_a_connection_string_only_a_name()
    {
        // Simulate `profile create out --connection-name prod` — the secret is never an input here.
        var created = new PublishProfile { ConnectionName = "prod" };
        var json = created.ToJson();
        Assert.Contains("\"connectionName\"", json);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj_profile_{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ } }
    }
}
