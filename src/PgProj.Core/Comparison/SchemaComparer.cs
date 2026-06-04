using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

public sealed class ComparerOptions
{
    /// <summary>
    /// When false (the default, matching SSDT's "block on data loss" instinct) objects present in
    /// the target but absent from the project are left alone. When true, they are dropped.
    /// </summary>
    public bool DropObjectsNotInSource { get; init; }
}

/// <summary>
/// Diffs a <em>source</em> model (the desired state — your project) against a <em>target</em>
/// model (the actual state — usually a live server) and produces the ordered set of changes that
/// would migrate the target to the source. This is the engine behind both <c>compare</c> and
/// <c>publish</c>.
/// </summary>
public sealed class SchemaComparer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Schema-qualified-name key with the model's identifier semantics (OrdinalIgnoreCase, mirroring
    // DatabaseModel.NameEquals). Used to pre-index target/source collections so the per-object lookups
    // are O(1) instead of a linear FirstOrDefault scan — the comparer was O(n·m) over object counts.
    private static readonly IEqualityComparer<(string, string)> QualifiedName = new QualifiedNameComparer();

    private sealed class QualifiedNameComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            DatabaseModel.NameEquals(a.Item1, b.Item1) && DatabaseModel.NameEquals(a.Item2, b.Item2);

        public int GetHashCode((string, string) v) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Item1 ?? ""),
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Item2 ?? ""));
    }

    // Index a collection by (schema, name). TryAdd keeps the FIRST occurrence, matching the old
    // FirstOrDefault/Find behavior when a model contains duplicate-named objects.
    private static Dictionary<(string, string), T> IndexByName<T>(IReadOnlyList<T> items, Func<T, (string, string)> key)
    {
        var d = new Dictionary<(string, string), T>(items.Count, QualifiedName);
        foreach (var it in items) d.TryAdd(key(it), it);
        return d;
    }

    public IReadOnlyList<SchemaChange> Compare(DatabaseModel source, DatabaseModel target, ComparerOptions? options = null)
    {
        options ??= new ComparerOptions();
        var changes = new List<SchemaChange>();

        CompareSchemas(source, target, changes);
        CompareSequences(source, target, changes);
        CompareTables(source, target, changes, options);
        CompareIndexes(source, target, changes, options);
        CompareViews(source, target, changes, options);
        CompareFunctions(source, target, changes);
        CompareRawObjects(source, target, changes, options);

        return changes.OrderBy(c => c.Phase).ToList();
    }

    private static void CompareSchemas(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes)
    {
        foreach (var s in source.Schemas)
        {
            if (DatabaseModel.NameEquals(s.Name, "public")) continue; // always present
            if (!target.HasSchema(s.Name))
                changes.Add(new CreateSchemaChange(s.Name));
        }
    }

    private static void CompareSequences(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes)
    {
        var tgtByName = IndexByName(target.Sequences, t => (t.Schema, t.Name));
        foreach (var s in source.Sequences)
        {
            tgtByName.TryGetValue((s.Schema, s.Name), out var tgt);
            if (tgt is null)
                changes.Add(new CreateSequenceChange(s));
            else if (SequenceOptionsDiffer(s, tgt) && SqlEmitter.SequenceOptions(s).Length > 0)
                changes.Add(new AlterSequenceChange(s));
        }
    }

    // Only options the source explicitly set are compared, so an introspected sequence (which
    // reports every option with its default) doesn't churn an ALTER on every deploy.
    private static bool SequenceOptionsDiffer(SequenceDefinition s, SequenceDefinition t)
    {
        if (s.DataType is not null && !string.Equals(s.DataType, t.DataType, StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Increment is not null && s.Increment != t.Increment) return true;
        if (s.MinValue is not null && s.MinValue != t.MinValue) return true;
        if (s.MaxValue is not null && s.MaxValue != t.MaxValue) return true;
        if (s.Start is not null && s.Start != t.Start) return true;
        if (s.Cache is not null && s.Cache != t.Cache) return true;
        return s.Cycle != t.Cycle;
    }

    private void CompareTables(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes, ComparerOptions options)
    {
        var tgtByName = IndexByName(target.Tables, t => (t.Schema, t.Name));
        foreach (var src in source.Tables)
        {
            tgtByName.TryGetValue((src.Schema, src.Name), out var tgt);
            if (tgt is null)
            {
                changes.Add(new CreateTableChange(src));
                foreach (var fk in src.ForeignKeys)
                    changes.Add(new AddForeignKeyChange(src, fk));
                continue;
            }

            // Columns present in source but not target -> add.
            foreach (var col in src.Columns)
            {
                var existing = tgt.FindColumn(col.Name);
                if (existing is null)
                    changes.Add(new AddColumnChange(src.Schema, src.Name, col));
                else if (!ColumnsEqual(existing, col))
                    changes.Add(new AlterColumnChange(src.Schema, src.Name, existing, col));
            }

            // Columns present in target but not source -> drop (guarded).
            if (options.DropObjectsNotInSource)
            {
                foreach (var col in tgt.Columns.Where(c => src.FindColumn(c.Name) is null))
                    changes.Add(new DropColumnChange(src.Schema, src.Name, col.Name));
            }

            CompareForeignKeys(src, tgt, changes, options);
            ComparePrimaryKey(src, tgt, changes, options);
            CompareChecks(src, tgt, changes, options);
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Tables, t => (t.Schema, t.Name));
            foreach (var tgt in target.Tables)
                if (!srcByName.ContainsKey((tgt.Schema, tgt.Name)))
                    changes.Add(new DropTableChange(tgt.Schema, tgt.Name));
        }
    }

    private static void ComparePrimaryKey(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        var srcPk = src.PrimaryKey;
        var tgtPk = tgt.PrimaryKey;

        if (srcPk is null)
        {
            if (tgtPk is not null && options.DropObjectsNotInSource)
                changes.Add(new DropPrimaryKeyChange(src.Schema, src.Name, tgtPk.Name ?? $"{src.Name}_pkey"));
            return;
        }

        if (tgtPk is null)
        {
            changes.Add(new AddPrimaryKeyChange(src.Schema, src.Name, srcPk));
            return;
        }

        if (!srcPk.Columns.SequenceEqual(tgtPk.Columns, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add(new DropPrimaryKeyChange(src.Schema, src.Name, tgtPk.Name ?? $"{src.Name}_pkey"));
            changes.Add(new AddPrimaryKeyChange(src.Schema, src.Name, srcPk));
        }
    }

    private void CompareChecks(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        var targetExprs = tgt.Checks.Select(c => NormalizeText(c.Expression)).ToHashSet();
        foreach (var c in src.Checks)
            if (!targetExprs.Contains(NormalizeText(c.Expression)))
                changes.Add(new AddCheckConstraintChange(src.Schema, src.Name, c));

        var targetOther = tgt.OtherConstraints.Select(NormalizeText).ToHashSet();
        foreach (var clause in src.OtherConstraints)
            if (!targetOther.Contains(NormalizeText(clause)))
                changes.Add(new AddRawTableConstraintChange(src.Schema, src.Name, clause));

        if (options.DropObjectsNotInSource)
        {
            var sourceExprs = src.Checks.Select(c => NormalizeText(c.Expression)).ToHashSet();
            foreach (var c in tgt.Checks.Where(c => c.Name is not null && !sourceExprs.Contains(NormalizeText(c.Expression))))
                changes.Add(new DropConstraintChange(src.Schema, src.Name, c.Name!));
        }
    }

    private void CompareForeignKeys(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        var targetSigs = tgt.ForeignKeys.Select(ForeignKeySignature).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in src.ForeignKeys)
        {
            if (!targetSigs.Contains(ForeignKeySignature(fk)))
                changes.Add(new AddForeignKeyChange(src, fk));
        }

        if (options.DropObjectsNotInSource)
        {
            var sourceSigs = src.ForeignKeys.Select(ForeignKeySignature).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in tgt.ForeignKeys.Where(f => !sourceSigs.Contains(ForeignKeySignature(f))))
                changes.Add(new DropForeignKeyChange(src.Schema, src.Name, fk.Name ?? $"{src.Name}_fkey"));
        }
    }

    private void CompareIndexes(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes, ComparerOptions options)
    {
        // Relations (schema, name) of source materialized views — an index on one must deploy after it.
        var matviews = new HashSet<(string, string)>(QualifiedName);
        foreach (var v in source.Views)
            if (v.IsMaterialized) matviews.Add((v.Schema, v.Name));

        var tgtByName = IndexByName(target.Indexes, i => (i.Schema, i.Name));
        foreach (var src in source.Indexes)
        {
            var onMv = matviews.Contains((src.Schema, src.Table));
            tgtByName.TryGetValue((src.Schema, src.Name), out var tgt);
            if (tgt is null)
            {
                changes.Add(new CreateIndexChange(src, onMv));
            }
            else if (!IndexesEqual(src, tgt))
            {
                changes.Add(new DropIndexChange(src.Schema, src.Name));
                changes.Add(new CreateIndexChange(src, onMv));
            }
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Indexes, i => (i.Schema, i.Name));
            foreach (var tgt in target.Indexes)
                if (!srcByName.ContainsKey((tgt.Schema, tgt.Name)))
                    changes.Add(new DropIndexChange(tgt.Schema, tgt.Name));
        }
    }

    private void CompareViews(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes, ComparerOptions options)
    {
        var tgtByName = IndexByName(target.Views, v => (v.Schema, v.Name));
        foreach (var src in source.Views)
        {
            tgtByName.TryGetValue((src.Schema, src.Name), out var tgt);
            if (tgt is null || NormalizeBody(src.Body) != NormalizeBody(tgt.Body))
                changes.Add(new CreateOrReplaceViewChange(src));
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Views, v => (v.Schema, v.Name));
            foreach (var tgt in target.Views)
                if (!srcByName.ContainsKey((tgt.Schema, tgt.Name)))
                    changes.Add(new DropViewChange(tgt.Schema, tgt.Name));
        }
    }

    private void CompareFunctions(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes)
    {
        // Group target overloads by schema.name once, preserving target order within each group so
        // FirstOrDefault still picks the same candidate as the old linear Where(...).ToList().
        var tgtByName = new Dictionary<(string, string), List<FunctionDefinition>>(QualifiedName);
        foreach (var f in target.Functions)
        {
            if (!tgtByName.TryGetValue((f.Schema, f.Name), out var group)) { group = new List<FunctionDefinition>(); tgtByName[(f.Schema, f.Name)] = group; }
            group.Add(f);
        }

        foreach (var src in source.Functions)
        {
            // Match by schema.name (reliable for the common, non-overloaded case); when a name has
            // multiple overloads, disambiguate by normalized argument types.
            tgtByName.TryGetValue((src.Schema, src.Name), out var candidates);
            FunctionDefinition? tgt = candidates is null ? null
                : candidates.Count <= 1
                    ? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault(c => NormalizeText(c.ArgTypes) == NormalizeText(src.ArgTypes));

            if (tgt is null || NormalizeBody(src.Body) != NormalizeBody(tgt.Body))
                changes.Add(new CreateOrReplaceFunctionChange(src));
        }
    }

    private void CompareRawObjects(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes, ComparerOptions options)
    {
        // FindObject matches Identity case-insensitively (first occurrence) — mirror that with a dict.
        var tgtByIdentity = new Dictionary<string, RawObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in target.Objects) tgtByIdentity.TryAdd(o.Identity, o);

        foreach (var src in source.Objects)
        {
            tgtByIdentity.TryGetValue(src.Identity, out var tgt);
            if (tgt is null)
            {
                // A typed/partition table (CREATE TABLE … OF type / PARTITION OF) is modeled in the
                // project as a raw `table:` object, but the live reader returns it as a real
                // TableDefinition — so treat it as present when the catalog has that table.
                if (src.Kind == ObjectKind.Table
                    && target.Tables.Any(t => string.Equals(t.Schema, src.Schema, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(t.Name, src.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                changes.Add(new CreateRawObjectChange(src));
            }
            else if (src.BodyComparable && tgt.BodyComparable && NormalizeRawBody(src.Body) != NormalizeRawBody(tgt.Body))
            {
                // A destructive recreate (type/domain/foreign table can cascade-drop columns) is
                // only emitted when drops are allowed; in-place redefinitions always proceed.
                if (RawObjectMeta.IsDestructiveRecreate(src.Kind) && !options.DropObjectsNotInSource)
                    continue;
                changes.Add(new RecreateRawObjectChange(src));
            }
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByIdentity = new Dictionary<string, RawObjectDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in source.Objects) srcByIdentity.TryAdd(o.Identity, o);
            foreach (var tgt in target.Objects)
                if (tgt.Kind != ObjectKind.Comment && !srcByIdentity.ContainsKey(tgt.Identity))
                    changes.Add(new DropRawObjectChange(tgt));
        }
    }

    // ---- equality helpers ----------------------------------------------------------------

    private bool ColumnsEqual(ColumnDefinition a, ColumnDefinition b) =>
        string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)
        && a.IsNullable == b.IsNullable
        && a.IsSerial == b.IsSerial
        && (a.IsSerial || DefaultsEqual(a.Default, b.Default)) // serial's nextval default is implicit
        && a.IsIdentity == b.IsIdentity
        && NormalizeText(a.GeneratedExpression ?? "") == NormalizeText(b.GeneratedExpression ?? "");

    private bool DefaultsEqual(string? a, string? b)
    {
        var na = NormalizeDefault(a);
        var nb = NormalizeDefault(b);
        return na == nb;
    }

    // Strip explicit casts so a project default ('active') matches the catalog's
    // ('active'::character varying), and collapse to a canonical form.
    private static readonly Regex CastSuffix = new(@"::\s*""?[A-Za-z][A-Za-z0-9_ ]*""?(\[\])?", RegexOptions.Compiled);

    private string NormalizeDefault(string? d) =>
        string.IsNullOrWhiteSpace(d) ? string.Empty : NormalizeText(CastSuffix.Replace(d, string.Empty));

    // Canonicalize dollar-quote tags ($function$ -> $$) so a hand-written function body matches the
    // catalog's pg_get_functiondef rendering, which picks its own tag.
    private static readonly Regex DollarTag = new(@"\$[A-Za-z0-9_]*\$", RegexOptions.Compiled);

    // pg_get_viewdef adds a result-type cast to literals (0 -> 0::bigint). Strip casts on numeric/
    // string LITERALS only (not column/expression casts), so a view round-trips with zero diff.
    private static readonly Regex LiteralCast = new(@"(\b\d+(?:\.\d+)?|'[^']*')::[a-z0-9_]+", RegexOptions.Compiled);
    // Reconcile punctuation spacing: our Token.Render is tight ("a,b" / "x=y") while pg_get_viewdef
    // is spaced ("a, b" / "x = y"). A space is only meaningful between two word characters.
    private static readonly Regex PunctSpace = new(@"\s*([^\w\s])\s*", RegexOptions.Compiled);

    /// <summary>Body comparison for verbatim objects: case-, whitespace-, punctuation-spacing-, dollar-tag-, literal-cast- and trailing-`;`-agnostic.</summary>
    private static string NormalizeBody(string s)
        => PunctSpace.Replace(LiteralCast.Replace(NormalizeText(DollarTag.Replace(s, "$$$$")), "$1"), "$1").TrimEnd(';', ' ');

    /// <summary>Raw single-statement DDL additionally ignores identifier quoting — the catalog reader
    /// quotes names (e.g. <c>CREATE EXTENSION "btree_gist"</c>) that a project usually writes bare.</summary>
    private static string NormalizeRawBody(string s) => NormalizeBody(s.Replace("\"", ""));

    private static bool IndexesEqual(IndexDefinition a, IndexDefinition b) =>
        a.IsUnique == b.IsUnique
        && string.Equals(a.Method ?? "btree", b.Method ?? "btree", StringComparison.OrdinalIgnoreCase)
        && a.Columns.Select(NormalizeIndexColumn).SequenceEqual(b.Columns.Select(NormalizeIndexColumn))
        && NormalizeText(a.WhereClause ?? "") == NormalizeText(b.WhereClause ?? "");

    // Index columns come quoted from the catalog (pg_get_indexdef) but usually bare from a
    // project file; strip quotes so "email" and email compare equal.
    private static string NormalizeIndexColumn(string c) => NormalizeText(c).Replace("\"", "");

    private static string ForeignKeySignature(ForeignKeyDefinition fk) =>
        string.Join(",", fk.Columns.Select(c => c.ToLowerInvariant()))
        + "->" + fk.ReferencedSchema.ToLowerInvariant() + "." + fk.ReferencedTable.ToLowerInvariant()
        + "(" + string.Join(",", fk.ReferencedColumns.Select(c => c.ToLowerInvariant())) + ")";

    private static string NormalizeText(string s) =>
        Whitespace.Replace(s.Trim(), " ").ToLowerInvariant();
}
