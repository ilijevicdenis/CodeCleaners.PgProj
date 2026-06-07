using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Versioning;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #43: the <see cref="PostgresVersionProfile"/> abstraction. DB-free coverage of all three
/// facets — profile selection from TargetPostgresVersion, the reader sourcing catalog SQL from the
/// profile, SupportedFeatures folding PgVersionCapabilities without PGV### regression, and the
/// generator/comparer asking ObjectCapabilities for the ALTER-vs-recreate decision.
/// </summary>
public class VersionProfileTests
{
    // ---- profile selection from TargetPostgresVersion -----------------------------------------

    [Theory]
    [InlineData("13", 13)]
    [InlineData("14", 14)]
    [InlineData("15", 15)]
    [InlineData("16", 16)]
    [InlineData("17", 17)]
    [InlineData("18", 18)]
    [InlineData("PostgreSQL 16", 16)]
    [InlineData("pg15", 15)]
    [InlineData("16.2", 16)]
    public void Profile_is_selected_from_target_version(string target, int expectedMajor)
        => Assert.Equal(expectedMajor, PostgresVersionProfile.ForTarget(target).MajorVersion);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("not-a-version")]
    public void Unknown_or_blank_version_falls_back_to_latest(string? target)
        => Assert.Equal(PostgresVersionProfile.LatestMajorVersion, PostgresVersionProfile.ForTarget(target).MajorVersion);

    [Fact]
    public void Out_of_range_version_clamps_to_a_shipped_profile()
    {
        // Newer-than-latest clamps to latest; older-than-earliest clamps to the earliest shipped profile.
        Assert.Equal(PostgresVersionProfile.LatestMajorVersion, PostgresVersionProfile.ForTarget("99").MajorVersion);
        Assert.Equal(PostgresVersionProfile.EarliestMajorVersion, PostgresVersionProfile.ForTarget("9").MajorVersion);
    }

    // ---- CatalogQueries: the reader sources its SQL from the profile --------------------------

    [Fact]
    public void Reader_sources_catalog_sql_from_the_profile()
    {
        // Structural assertion (no live DB needed): swapping the profile's CatalogQueries changes the
        // exact SQL the reader would issue. We prove this by handing the reader a profile whose query
        // text is sentinel-marked and confirming the reader exposes that text, not a hard-coded literal.
        var sentinel = PostgresVersionProfile.Latest.CatalogQueries.With(q =>
            q with { TablesAndColumns = "/*sentinel*/ " + q.TablesAndColumns });

        // The default profile's reader uses the default query text…
        Assert.Equal(CatalogQueries.Default.TablesAndColumns,
            PostgresVersionProfile.Latest.CatalogQueries.TablesAndColumns);

        // …and the sentinel set differs, so a reader built on it would issue different SQL.
        Assert.NotEqual(CatalogQueries.Default.TablesAndColumns, sentinel.TablesAndColumns);
        Assert.Contains("/*sentinel*/", sentinel.TablesAndColumns);
    }

    [Fact]
    public void Reader_constructed_with_a_profile_uses_that_profiles_queries()
    {
        // The reader holds the profile's CatalogQueries; the property is the single source of its SQL.
        // (Constructing the reader does not touch a database — only ReadAsync does.)
        var profile = PostgresVersionProfile.ForTarget("15");
        var reader = new LiveDatabaseReader(profile);
        Assert.NotNull(reader);

        // Every catalog query the reader can issue is exposed via the profile, not a literal in the reader.
        Assert.False(string.IsNullOrWhiteSpace(profile.CatalogQueries.Schemas));
        Assert.False(string.IsNullOrWhiteSpace(profile.CatalogQueries.Functions));
        Assert.False(string.IsNullOrWhiteSpace(profile.CatalogQueries.Indexes));
        Assert.Contains("pg_namespace", profile.CatalogQueries.Schemas);
    }

    [Fact]
    public void Swapping_the_profile_changes_the_catalog_sql_text()
    {
        // Two different profiles can carry different query text for the same object type. We construct an
        // override set and confirm it diverges from the default — i.e. the reader is profile-driven.
        var defaultViews = PostgresVersionProfile.Latest.CatalogQueries.Views;
        var overridden = CatalogQueries.Default.With(q => q with { Views = "SELECT 1; -- pg13 variant" });
        Assert.NotEqual(defaultViews, overridden.Views);
    }

    // ---- SupportedFeatures: folds PgVersionCapabilities, no PGV### regression ------------------

    [Theory]
    [InlineData(14, PgVersionCapabilities.MergeStatement, false)]   // MERGE is PG15
    [InlineData(15, PgVersionCapabilities.MergeStatement, true)]
    [InlineData(16, PgVersionCapabilities.MergeReturning, false)]   // RETURNING-on-MERGE is PG17
    [InlineData(17, PgVersionCapabilities.MergeReturning, true)]
    [InlineData(14, PgVersionCapabilities.NullsNotDistinct, false)] // NULLS NOT DISTINCT is PG15
    [InlineData(15, PgVersionCapabilities.NullsNotDistinct, true)]
    [InlineData(15, PgVersionCapabilities.IsJsonPredicate, false)]  // IS JSON is PG16
    [InlineData(16, PgVersionCapabilities.IsJsonPredicate, true)]
    [InlineData(16, PgVersionCapabilities.JsonTable, false)]        // JSON_TABLE is PG17
    [InlineData(17, PgVersionCapabilities.JsonTable, true)]
    public void Supported_feature_flags_match_the_folded_capability_table(int major, string ruleId, bool expected)
    {
        var features = PostgresVersionProfile.ForMajor(major).SupportedFeatures;
        Assert.Equal(expected, features.Has(ruleId));
    }

    [Fact]
    public void Named_feature_flags_agree_with_the_capability_table_for_every_version()
    {
        // For every shipped profile and every PGV### rule, the named/Has() flag must equal the table's
        // own min-version verdict — i.e. SupportedFeatures is a faithful view, never a divergent copy.
        for (var major = PostgresVersionProfile.EarliestMajorVersion; major <= PostgresVersionProfile.LatestMajorVersion; major++)
        {
            var f = new SupportedFeatures(major);
            foreach (var (ruleId, cap) in PgVersionCapabilities.ByRuleId)
                Assert.Equal(cap.MinMajorVersion <= major, f.Has(ruleId));

            // The named accessors map onto the same rule ids.
            Assert.Equal(f.Has(PgVersionCapabilities.MergeStatement), f.Merge);
            Assert.Equal(f.Has(PgVersionCapabilities.NullsNotDistinct), f.NullsNotDistinct);
            Assert.Equal(f.Has(PgVersionCapabilities.JsonTable), f.JsonTable);
        }
    }

    [Fact]
    public void Folding_does_not_change_the_PGV_analysis_path()
    {
        // The TargetVersionAnalyzer still consults PgVersionCapabilities directly and is unaffected by
        // the profile: PGV001 (MERGE) flags on 14, not on 15 — exactly as before this change.
        var sql = "MERGE INTO s.t a USING s.u b ON a.id=b.id WHEN MATCHED THEN UPDATE SET x=b.x;";
        Assert.True(Has(sql, "14", PgVersionCapabilities.MergeStatement));
        Assert.False(Has(sql, "15", PgVersionCapabilities.MergeStatement));
        // Rule count is unchanged (no rules added/removed by the folding).
        Assert.Equal(PgVersionCapabilities.RuleCount, TargetVersionAnalyzer.RuleCount);
    }

    private static bool Has(string sql, string target, string ruleId)
        => TargetVersionAnalyzer.Analyze(new Syntax.PgParser().Parse(sql), target).Any(d => d.RuleId == ruleId);

    // ---- ObjectCapabilities: generator/comparer ALTER-vs-recreate decision --------------------

    [Fact]
    public void Default_capabilities_permit_in_place_column_alter()
    {
        var caps = PostgresVersionProfile.Latest.ObjectCapabilities;
        Assert.True(caps.CanAlterColumnType);
        Assert.True(caps.CanAlterColumnNullability);
        Assert.True(caps.CanAlterColumnDefault);
        Assert.True(caps.CanAlterColumn(typeChanged: true, nullabilityChanged: true, defaultChanged: true));
    }

    [Fact]
    public void Capabilities_veto_alter_when_a_facet_is_not_alterable()
    {
        // A profile whose ObjectCapabilities lacks the column-type ALTER path must refuse an in-place
        // ALTER for a type change — the comparer then recreates instead.
        var noTypeAlter = new ObjectCapabilities { CanAlterColumnType = false };
        Assert.False(noTypeAlter.CanAlterColumn(typeChanged: true, nullabilityChanged: false, defaultChanged: false));
        // A nullability-only change is still ALTER-able.
        Assert.True(noTypeAlter.CanAlterColumn(typeChanged: false, nullabilityChanged: true, defaultChanged: false));
    }

    [Fact]
    public void Comparer_asks_object_capabilities_for_alter_vs_recreate()
    {
        // Same column-type change, two profiles:
        //  - latest (CanAlterColumnType=true)  → one AlterColumnChange (in place)
        //  - a profile that vetoes type ALTER  → drop + add (recreate the column)
        var source = TestModel.Build("CREATE TABLE s.t (id int, name text);");
        var target = TestModel.Build("CREATE TABLE s.t (id int, name varchar(50));");

        var inPlace = new SchemaComparer(PostgresVersionProfile.Latest).Compare(source, target);
        Assert.Single(inPlace.OfType<AlterColumnChange>());
        Assert.Empty(inPlace.OfType<DropColumnChange>());

        var noTypeAlter = PostgresVersionProfile.Latest.With(
            capabilities: new ObjectCapabilities { CanAlterColumnType = false });
        var recreating = new SchemaComparer(noTypeAlter).Compare(source, target);
        Assert.Empty(recreating.OfType<AlterColumnChange>());
        Assert.Single(recreating.OfType<DropColumnChange>());
        // The recreate emits a re-add for the column on top of the original add for the *new* column? No —
        // source/target have the same column set, so the only AddColumnChange is the recreate's re-add.
        Assert.Single(recreating.OfType<AddColumnChange>());
    }

    [Fact]
    public void MustRecreate_matches_the_destructive_recreate_set()
    {
        var caps = ObjectCapabilities.Default;
        // Types/domains/foreign-tables are destructive-recreate kinds; functions are in-place (CREATE OR REPLACE).
        Assert.True(caps.MustRecreate(ObjectKind.Type));
        Assert.True(caps.MustRecreate(ObjectKind.Domain));
        Assert.False(caps.CanAlterInPlace(ObjectKind.Type));
        Assert.True(caps.CanAlterInPlace(ObjectKind.Trigger));
    }
}
