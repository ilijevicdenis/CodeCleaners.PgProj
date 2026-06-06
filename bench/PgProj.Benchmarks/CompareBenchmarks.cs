using BenchmarkDotNet.Attributes;
using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 4 — schema compare. Exercises <see cref="SchemaComparer.Compare"/> over two synthetic models
/// of <see cref="TableCount"/> tables each, scaled so the O(n·m) <c>FirstOrDefault</c> scans in
/// <see cref="DatabaseModel.FindTable"/> / index matching become visible as N grows: doubling the table
/// count should more than double the time if the matching is quadratic. Neither the Tokenize, Parse,
/// nor Build suites touch the comparer.
///
/// Both models hold the same tables (the steady-state "re-deploy an unchanged schema" case): every
/// source table is found in the target, so the comparer pays the full linear target scan AND the
/// per-table column/PK/FK/check compare for every table — the realistic worst case for the scan cost,
/// and the one that produces zero changes yet still does all the work.
/// </summary>
[MemoryDiagnoser]   // GC mode + iteration counts come from BenchConfig (shared across all suites)
public class CompareBenchmarks
{
    [Params(10, 100, 500)]
    public int TableCount;

    private DatabaseModel _source = null!;
    private DatabaseModel _target = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = BuildModel(TableCount);
        _target = BuildModel(TableCount);   // independent instance, identical content
    }

    [Benchmark]
    public int Compare() => new SchemaComparer().Compare(_source, _target).Count;

    // A realistic-ish table: a handful of typed columns, a PK, a self-FK, a CHECK, plus one index —
    // enough per-table compare work that the run is not pure list-scan, mirroring BuildBenchmarks.TableSql.
    private static DatabaseModel BuildModel(int n)
    {
        var m = new DatabaseModel();
        m.Schemas.Add(new SchemaDefinition("app"));
        for (var i = 0; i < n; i++)
        {
            var name = $"t{i:D4}";
            var table = new TableDefinition
            {
                Schema = "app",
                Name = name,
                Columns =
                {
                    new ColumnDefinition("id", "bigint", IsNullable: false),
                    new ColumnDefinition("parent_id", "bigint", IsNullable: true),
                    new ColumnDefinition("code", "character varying(32)", IsNullable: false),
                    new ColumnDefinition("name", "text", IsNullable: false, Default: "'unnamed'"),
                    new ColumnDefinition("amount", "numeric(18,4)", IsNullable: false, Default: "0"),
                    new ColumnDefinition("is_active", "boolean", IsNullable: false, Default: "true"),
                },
                PrimaryKey = new PrimaryKeyDefinition($"{name}_pkey", new[] { "id" }),
                ForeignKeys =
                {
                    new ForeignKeyDefinition($"{name}_parent_fkey", new[] { "parent_id" }, "app", "t0000", new[] { "id" }),
                },
                Checks =
                {
                    new CheckConstraintDefinition($"ck_{name}_amount", "amount >= 0"),
                },
            };
            m.Tables.Add(table);
            m.Indexes.Add(new IndexDefinition($"ix_{name}_code", "app", name, new[] { "code" }, IsUnique: false));
        }
        return m;
    }
}
