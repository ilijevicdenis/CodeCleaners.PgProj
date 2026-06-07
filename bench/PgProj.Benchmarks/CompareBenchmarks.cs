using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 4 — the schema compare / diff path (issue #10). Earlier this suite measured only
/// <see cref="SchemaComparer.Compare"/> over two <em>identical</em> models — the steady-state
/// "re-deploy an unchanged schema" case. That exercises the O(n) name-matching scans and the
/// per-object normalization, but produces <b>zero</b> changes, so it never touches the part of the
/// engine that grew in M3/M4: change-record construction across every <c>CompareX</c> method, the
/// selectable change set (signature → SHA-256 id), the risk analyzer, and deploy-script emission.
///
/// <para>The suite now spans the realistic compare path:</para>
/// <list type="bullet">
///   <item><see cref="CompareIdentical"/> — the original scan worst case (no changes), kept as the
///   lower bound and to keep the matching-scan cost visible as N grows.</item>
///   <item><see cref="CompareModified"/> — source vs a <em>modified</em> target (the typical
///   "publish my edits" diff): a fraction of tables have column type / nullability / default deltas,
///   added columns, changed views and function bodies, plus added/dropped objects under
///   <c>--allow-drops</c>. This is the path that actually builds <see cref="SchemaChange"/> records,
///   so the comparer's per-change LINQ/HashSet/closure churn shows up here.</item>
///   <item><see cref="BuildChangeSetModified"/> — the real product entry point
///   (<see cref="SchemaChangeSet.Build"/>): Compare + per-change stable id (signature string +
///   SHA-256) + object-type tagging. This is what <c>compare</c>/<c>publish</c> call.</item>
///   <item><see cref="ScriptModified"/> — Build + classify risk on every change + emit the deploy
///   script for the included subset: the full Diff→Risk→Script pipeline a publish runs end to end.</item>
/// </list>
///
/// The source/target shapes mirror a project (<c>source</c>) diffed against a live-server-shaped
/// model (<c>target</c>): same kinds, but the target carries the pre-edit state plus a handful of
/// server-only objects the project no longer declares.
/// </summary>
[MemoryDiagnoser]   // GC mode + iteration counts come from BenchConfig (shared across all suites)
public class CompareBenchmarks
{
    [Params(10, 100, 500)]
    public int TableCount;

    private DatabaseModel _source = null!;
    private DatabaseModel _identicalTarget = null!;
    private DatabaseModel _modifiedTarget = null!;

    // --allow-drops: the modified target has server-only objects the project dropped, so the drop
    // walks (the guarded branches in every CompareX) are exercised too — the realistic publish case.
    private static readonly ComparerOptions DropOpts = new() { DropObjectsNotInSource = true };

    [GlobalSetup]
    public void Setup()
    {
        _source = BuildModel(TableCount, modified: false, asTarget: false);
        _identicalTarget = BuildModel(TableCount, modified: false, asTarget: true);
        _modifiedTarget = BuildModel(TableCount, modified: true, asTarget: true);
    }

    /// <summary>Steady-state: identical models, zero changes — the scan/normalization lower bound.</summary>
    [Benchmark]
    public int CompareIdentical() => new SchemaComparer().Compare(_source, _identicalTarget).Count;

    /// <summary>The typical diff: a modified target → real changes built across every CompareX path.</summary>
    [Benchmark]
    public int CompareModified() => new SchemaComparer().Compare(_source, _modifiedTarget, DropOpts).Count;

    /// <summary>The product entry point: Compare + stable id (signature + SHA-256) + object-type tagging.</summary>
    [Benchmark]
    public int BuildChangeSetModified() => SchemaChangeSet.Build(_source, _modifiedTarget, DropOpts).Count;

    /// <summary>The full publish pipeline: Build + risk classification (via MaxIncludedRiskLevel) + script emit.</summary>
    [Benchmark]
    public int ScriptModified()
    {
        var set = SchemaChangeSet.Build(_source, _modifiedTarget, DropOpts);
        _ = set.MaxIncludedRiskLevel;        // forces RiskAnalyzer.Classify on every included change
        return set.ScriptIncluded().Length;
    }

    // ---- model construction --------------------------------------------------------------------

    // Build a project-shaped model of n tables, each with columns/PK/FK/CHECK + an index, plus a view,
    // a function and a sequence per ~10 tables (so the view/function/sequence/raw compare paths carry
    // real load, not just tables). When `modified` is set, the model is perturbed relative to the
    // unmodified build so a diff against it yields a representative MIX of change kinds rather than one.
    // `asTarget` marks the right-hand (server) side: it gets a few extra objects the source lacks, so
    // the guarded drop walks run.
    private static DatabaseModel BuildModel(int n, bool modified, bool asTarget)
    {
        var m = new DatabaseModel();
        m.Schemas.Add(new SchemaDefinition("app"));

        for (var i = 0; i < n; i++)
        {
            var name = $"t{i:D4}";

            // ~20% of tables carry an edit when `modified`: a widened column type, a flipped
            // nullability, a changed default, and one added column — the in-place ALTER path.
            var edited = modified && (i % 5 == 0);

            var amountType = edited ? "numeric(20,4)" : "numeric(18,4)";     // type change → AlterColumn
            var codeNullable = edited;                                       // nullability change
            var nameDefault = edited ? "'changed'" : "'unnamed'";           // default change

            var columns = new List<ColumnDefinition>
            {
                new("id", "bigint", IsNullable: false),
                new("parent_id", "bigint", IsNullable: true),
                new("code", "character varying(32)", IsNullable: codeNullable),
                new("name", "text", IsNullable: false, Default: nameDefault),
                new("amount", amountType, IsNullable: false, Default: "0"),
                new("is_active", "boolean", IsNullable: false, Default: "true"),
            };
            if (edited)
                columns.Add(new ColumnDefinition("note", "text", IsNullable: true));  // AddColumn

            var table = new TableDefinition
            {
                Schema = "app",
                Name = name,
                PrimaryKey = new PrimaryKeyDefinition($"{name}_pkey", new[] { "id" }),
            };
            foreach (var c in columns) table.Columns.Add(c);
            table.ForeignKeys.Add(new ForeignKeyDefinition($"{name}_parent_fkey", new[] { "parent_id" }, "app", "t0000", new[] { "id" }));
            table.Checks.Add(new CheckConstraintDefinition($"ck_{name}_amount", "amount >= 0"));
            m.Tables.Add(table);

            m.Indexes.Add(new IndexDefinition($"ix_{name}_code", "app", name, new[] { "code" }, IsUnique: false));

            // One view / function / sequence per 10 tables; bodies change on the target's modified build
            // so the view-body and function-body compare paths report a recreate.
            if (i % 10 == 0)
            {
                var viewBody = edited
                    ? $"SELECT id, code, name FROM app.{name} WHERE is_active"
                    : $"SELECT id, code FROM app.{name}";
                m.Views.Add(new ViewDefinition("app", $"v_{name}", viewBody));

                var fnBody = edited
                    ? $"CREATE FUNCTION app.f_{name}() RETURNS integer LANGUAGE sql AS $$ SELECT 2 $$"
                    : $"CREATE FUNCTION app.f_{name}() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$";
                m.Functions.Add(new FunctionDefinition("app", $"f_{name}", $"app.f_{name}()", fnBody));

                m.Sequences.Add(new SequenceDefinition("app", $"s_{name}", Start: 1, Increment: 1));
            }
        }

        // Source-only ADD: a table the project introduced that the server doesn't have yet
        // (drives the CreateTable + AddForeignKey path). Only on the source side.
        if (!asTarget)
            m.Tables.Add(NewTable("t_added"));

        // Target-only objects the project dropped (drives the guarded drop walks under --allow-drops).
        if (asTarget)
        {
            m.Tables.Add(NewTable("t_serveronly"));
            m.Indexes.Add(new IndexDefinition("ix_serveronly", "app", "t_serveronly", new[] { "id" }, IsUnique: false));
            m.Sequences.Add(new SequenceDefinition("app", "s_serveronly", Start: 1));
            m.Views.Add(new ViewDefinition("app", "v_serveronly", "SELECT 1"));
        }

        return m;
    }

    private static TableDefinition NewTable(string name)
    {
        var t = new TableDefinition
        {
            Schema = "app",
            Name = name,
            PrimaryKey = new PrimaryKeyDefinition($"{name}_pkey", new[] { "id" }),
        };
        t.Columns.Add(new ColumnDefinition("id", "bigint", IsNullable: false));
        t.Columns.Add(new ColumnDefinition("label", "text", IsNullable: true));
        t.ForeignKeys.Add(new ForeignKeyDefinition($"{name}_self_fkey", new[] { "id" }, "app", "t0000", new[] { "id" }));
        return t;
    }
}
