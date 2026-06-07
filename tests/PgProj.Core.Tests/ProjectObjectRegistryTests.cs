using System.Linq;
using PgProj.Core.Extensibility;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using PgProj.Core.Versioning;

namespace PgProj.Core.Tests;

/// <summary>
/// Tests for the issue #44 extensibility contract (`IProjectObject`) + `ProjectObjectRegistry`: every
/// kind is reached through one contract, identity/hash/diff delegate to the #42 foundation (no behavior
/// change), and generated DDL is valid. Driven off the AllFeaturesDb sample model — DB-free.
/// </summary>
public sealed class ProjectObjectRegistryTests
{
    private static DatabaseModel SampleModel()
    {
        var built = DatabaseProject.Load(LiveReaderTestSupport.FindSampleProject()).Build();
        Assert.False(built.HasErrors, "sample build: " + string.Join("\n", built.Diagnostics));
        return built.Model;
    }

    [Fact]
    public void Registry_enumerates_every_object_in_the_model()
    {
        var m = SampleModel();
        var expected = m.Schemas.Count + m.Tables.Count + m.Indexes.Count + m.Views.Count +
                       m.Sequences.Count + m.Functions.Count + m.Objects.Count;
        var registry = new ProjectObjectRegistry(m);
        Assert.Equal(expected, registry.All.Count);
        Assert.All(registry.All, o => Assert.False(string.IsNullOrEmpty(o.Kind)));
    }

    [Fact]
    public void Contract_identity_and_hash_match_the_direct_computer()
    {
        // The contract must not change identity/hash — it delegates to ObjectIdentityComputer (#42).
        var t = new TableDefinition { Schema = "app", Name = "customer" };
        t.Columns.Add(new ColumnDefinition("id", "integer", false));
        var model = new DatabaseModel();
        model.Tables.Add(t);

        var computer = new ObjectIdentityComputer();
        var obj = new ProjectObjectRegistry(model, computer).OfKind("table").Single();

        Assert.Equal(computer.CanonicalHashOf(t), obj.Hash());
        Assert.Equal(computer.StableIdOf(t), obj.Identity().StableId);
        Assert.Equal("app.customer", obj.QualifiedName);
    }

    [Fact]
    public void Diff_classifies_unchanged_rename_and_recreate_through_the_contract()
    {
        IProjectObject TableObj(string name, string colType)
        {
            var t = new TableDefinition { Schema = "app", Name = name };
            t.Columns.Add(new ColumnDefinition("id", colType, false));
            var m = new DatabaseModel();
            m.Tables.Add(t);
            return new ProjectObjectRegistry(m).OfKind("table").Single();
        }

        var baseline = TableObj("customer", "integer");
        var same = TableObj("customer", "integer");
        var renamed = TableObj("client", "integer");      // same structure, different name → rename
        var restructured = TableObj("customer", "text");  // different column type → drop+create

        Assert.Equal(IdentityChangeKind.Unchanged, baseline.Diff(same).Kind);
        Assert.Equal(IdentityChangeKind.Rename, renamed.Diff(baseline).Kind);
        Assert.True(renamed.Diff(baseline).FqnChanged);
        Assert.Equal(IdentityChangeKind.DropAndCreate, restructured.Diff(baseline).Kind);
        Assert.Equal(IdentityChangeKind.DropAndCreate, baseline.Diff(null).Kind); // create (no target)
    }

    [Fact]
    public void GenerateSql_produces_reparseable_ddl_for_finely_modelled_kinds()
    {
        var registry = new ProjectObjectRegistry(SampleModel());
        var parser = new PgParser();
        foreach (var token in new[] { "table", "view", "sequence", "function", "index" })
        {
            foreach (var obj in registry.OfKind(token))
            {
                var sql = obj.GenerateSql(PostgresVersionProfile.Latest);
                Assert.False(string.IsNullOrWhiteSpace(sql), $"{token} {obj.QualifiedName} produced empty DDL");
                var diags = parser.Parse(sql).Diagnostics;
                Assert.True(diags.Count == 0, $"{token} {obj.QualifiedName} DDL did not re-parse: {string.Join(" | ", diags)}");
            }
        }
    }

    [Fact]
    public void Raw_object_kinds_are_exposed_through_the_contract()
    {
        // AllFeaturesDb has extensions/types/etc. — they reach the registry as raw contract objects,
        // and their GenerateSql is the captured body (no special-casing in the registry).
        var registry = new ProjectObjectRegistry(SampleModel());
        var rawTokens = registry.KindTokens().ToHashSet();
        Assert.Contains("type", rawTokens);  // enum/composite/range types in the sample

        var aType = registry.OfKind("type").First();
        Assert.False(string.IsNullOrWhiteSpace(aType.GenerateSql(PostgresVersionProfile.Latest)));
        Assert.NotEqual(default, aType.Identity().StableId);
    }
}
