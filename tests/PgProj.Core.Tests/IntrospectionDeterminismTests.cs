using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;

namespace PgProj.Core.Tests;

/// <summary>
/// Regression tests for issue #59 — the raw-object section of an introspected model (and therefore
/// every artifact derived from it) must be byte-reproducible across runs and machines, even though
/// <see cref="LiveDatabaseReader"/> reads the catalog with ~25 parallel tasks that complete in a
/// non-deterministic order.
/// </summary>
public sealed class IntrospectionDeterminismTests
{
    // ---- DB-free: the canonical ordering itself -------------------------------------------------

    [Fact]
    public void CompareCanonical_imposes_a_total_kind_schema_name_identity_order()
    {
        // Two differently-shuffled merges of the SAME raw objects must serialize identically once
        // sorted — this is exactly what the parallel reader needs to guarantee.
        var objects = SampleObjects();

        var a = SortedModel(Shuffle(objects, seed: 1));
        var b = SortedModel(Shuffle(objects, seed: 2));

        Assert.Equal(ModelJson.Serialize(a), ModelJson.Serialize(b));

        // And the order is the documented (kind, schema, name, identity) order, not merely stable.
        for (var i = 1; i < a.Objects.Count; i++)
            Assert.True(LiveDatabaseReader.CompareCanonical(a.Objects[i - 1], a.Objects[i]) <= 0,
                $"objects out of canonical order at index {i}: '{a.Objects[i - 1].Identity}' then '{a.Objects[i].Identity}'");
    }

    [Fact]
    public void CompareCanonical_orders_by_kind_first_then_schema_then_name()
    {
        var type = new RawObjectDefinition(ObjectKind.Type, "afd", "status", "type:afd.status", "");
        var domainSameNames = new RawObjectDefinition(ObjectKind.Domain, "afd", "status", "domain:afd.status", "");
        var typeOtherSchema = new RawObjectDefinition(ObjectKind.Type, "zzz", "status", "type:zzz.status", "");
        var typeOtherName = new RawObjectDefinition(ObjectKind.Type, "afd", "zzz", "type:afd.zzz", "");

        Assert.True(LiveDatabaseReader.CompareCanonical(type, domainSameNames) < 0);   // Type kind < Domain kind
        Assert.True(LiveDatabaseReader.CompareCanonical(type, typeOtherSchema) < 0);   // same kind, schema afd < zzz
        Assert.True(LiveDatabaseReader.CompareCanonical(type, typeOtherName) < 0);     // same kind+schema, name status < zzz
        Assert.Equal(0, LiveDatabaseReader.CompareCanonical(type, type));              // reflexive
    }

    // ---- live: two introspections of the same DB are byte-identical -----------------------------

    private static string? Conn => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    [Fact]
    public async Task Two_introspections_of_the_same_database_are_byte_identical()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB available — treated as a skip

        var built = DatabaseProject.Load(LiveReaderTestSupport.FindSampleProject()).Build();
        Assert.False(built.HasErrors, "sample project build: " + string.Join("\n", built.Diagnostics));

        var create = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(create, new DeployOptions { WrapInTransaction = true });

        var deployer = new DatabaseDeployer();
        await deployer.ExecuteAsync(conn, LiveReaderTestSupport.DropSampleSql);
        await deployer.ExecuteAsync(conn, script);

        // Introspect the same, untouched database twice with independent readers.
        var first = await new LiveDatabaseReader().ReadAsync(conn);
        var second = await new LiveDatabaseReader().ReadAsync(conn);

        // Acceptance: identical serialized models AND identical generated deploy scripts.
        Assert.Equal(ModelJson.Serialize(first), ModelJson.Serialize(second));

        var s1 = new DeployScriptGenerator().Generate(
            new SchemaComparer().Compare(first, new DatabaseModel()), new DeployOptions { WrapInTransaction = true });
        var s2 = new DeployScriptGenerator().Generate(
            new SchemaComparer().Compare(second, new DatabaseModel()), new DeployOptions { WrapInTransaction = true });
        Assert.Equal(s1, s2);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DatabaseModel SortedModel(IEnumerable<RawObjectDefinition> objects)
    {
        var model = new DatabaseModel();
        model.Objects.AddRange(objects);
        model.Objects.Sort(LiveDatabaseReader.CompareCanonical);
        return model;
    }

    private static List<RawObjectDefinition> Shuffle(IReadOnlyList<RawObjectDefinition> src, int seed)
    {
        // Deterministic Fisher–Yates (seeded) — varies the input order without nondeterminism in the test.
        var list = new List<RawObjectDefinition>(src);
        var rng = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static List<RawObjectDefinition> SampleObjects() => new()
    {
        new(ObjectKind.Extension, "", "btree_gist", "extension:btree_gist", "CREATE EXTENSION IF NOT EXISTS \"btree_gist\";"),
        new(ObjectKind.Extension, "", "citext", "extension:citext", "CREATE EXTENSION IF NOT EXISTS \"citext\";"),
        new(ObjectKind.Type, "afd", "mood", "type:afd.mood", "CREATE TYPE afd.mood AS ENUM ('a', 'b');"),
        new(ObjectKind.Type, "afd", "addr", "type:afd.addr", "CREATE TYPE afd.addr AS (street text);"),
        new(ObjectKind.Type, "reporting", "mood", "type:reporting.mood", "CREATE TYPE reporting.mood AS ENUM ('x');"),
        new(ObjectKind.Domain, "afd", "pos_int", "domain:afd.pos_int", "CREATE DOMAIN afd.pos_int AS integer CHECK (VALUE > 0);"),
        new(ObjectKind.Trigger, "afd", "t_audit", "trigger:t_audit on afd.orders", "CREATE TRIGGER t_audit ...", "afd.orders"),
        new(ObjectKind.Trigger, "afd", "t_audit", "trigger:t_audit on afd.items", "CREATE TRIGGER t_audit ...", "afd.items"),
        new(ObjectKind.Comment, "", "", "comment:table afd.orders", "COMMENT ON TABLE afd.orders IS 'x';"),
        new(ObjectKind.Aggregate, "afd", "mysum", "aggregate:afd.mysum(integer)", "CREATE AGGREGATE afd.mysum (integer) (...);"),
        new(ObjectKind.Aggregate, "afd", "mysum", "aggregate:afd.mysum(bigint)", "CREATE AGGREGATE afd.mysum (bigint) (...);"),
    };
}

/// <summary>Shared bits between the live introspection tests so the drop/locate logic lives in one place.</summary>
internal static class LiveReaderTestSupport
{
    public const string DropSampleSql =
        "DROP PUBLICATION IF EXISTS customer_pub; DROP SCHEMA IF EXISTS afd CASCADE; " +
        "DROP SCHEMA IF EXISTS reporting CASCADE; DROP FOREIGN DATA WRAPPER IF EXISTS dummy_fdw CASCADE; " +
        "DROP LANGUAGE IF EXISTS afd_plpgsql CASCADE;";   // global object (not in a dropped schema)

    public static string FindSampleProject()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = System.IO.Path.GetDirectoryName(dir))
        {
            var candidate = System.IO.Path.Combine(dir, "sample", "AllFeaturesDb", "AllFeaturesDb.pgproj");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        throw new System.IO.FileNotFoundException("Could not locate sample/AllFeaturesDb/AllFeaturesDb.pgproj from " + AppContext.BaseDirectory);
    }
}
