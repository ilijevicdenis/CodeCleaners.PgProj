using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Model;
using PgProj.Core.Versioning;

namespace PgProj.Core.Comparison;

/// <summary>
/// What counts as a difference when diffing a source against a target (Phase 18, issue #58). Every option
/// defaults to the value that <b>reproduces today's behaviour exactly</b>, so an existing parameterless
/// caller — and every golden/round-trip test — is unaffected. Opting in/out is an explicit, deliberate
/// choice surfaced by the CLI and by a serializable <see cref="ComparisonProfile"/>.
/// </summary>
public sealed class ComparerOptions
{
    /// <summary>
    /// When false (the default, matching SSDT's "block on data loss" instinct) objects present in
    /// the target but absent from the project are left alone. When true, they are dropped.
    /// </summary>
    public bool DropObjectsNotInSource { get; init; }

    /// <summary>
    /// When true, an object that is structurally unchanged but moved name (same <see cref="Model.Identity.StableId"/>
    /// + same <see cref="Model.Identity.CanonicalHash"/>, different FQN) is emitted as a single <em>Rename</em>
    /// (via <see cref="IdentityDiffEngine"/>) instead of a Drop+Create pair (Phase 11, issue #53). OFF by
    /// default so the greenfield diff — and the committed golden deploy script / model JSON — are unchanged.
    /// </summary>
    public bool DetectRenames { get; init; }

    /// <summary>
    /// The project's persisted refactor log (#136). When present and non-empty, its logged renames /
    /// schema-moves seed the rename pre-pass BY DEFAULT (no flag), so the deploy emits a data-preserving
    /// <c>ALTER … RENAME</c>/<c>SET SCHEMA</c>/<c>RENAME COLUMN</c> instead of DROP+CREATE. Null/empty ⇒
    /// today's behaviour. This is in addition to (and merges with) the structural <see cref="DetectRenames"/>
    /// heuristic.
    /// </summary>
    public Refactoring.RefactorLog? RefactorLog { get; init; }

    /// <summary>
    /// Optional source-project dependency graph (issues #50/#55, built by
    /// <see cref="DeploymentGraphFactory.TryBuild"/>). When present the change list is ordered by the
    /// <see cref="DeploymentPlanner"/> — the stable phase order refined by real Hard/Soft edges, so e.g. a
    /// view that CALLS a function deploys after that function even though the view's phase is lower
    /// (issue #160); a hard cycle gets a skeleton pass. With no binding edge the output is byte-identical
    /// to the phase sort (the golden-script equivalence guarantee). Null ⇒ the historical phase order.
    /// </summary>
    public Semantics.Dependencies.DependencyGraph? DependencyGraph { get; init; }

    /// <summary>
    /// When true, two tables whose columns are identical but declared in a different order compare EQUAL
    /// (no <see cref="ColumnOrderChange"/> is emitted). Defaults to <c>false</c>: Postgres column order is
    /// physically meaningful, so by default a pure reorder is reported (as a benign, non-destructive,
    /// no-SQL change). Wires to #51's <see cref="Model.Identity.CanonicalFormOptions.IgnoreColumnOrder"/>.
    /// </summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>
    /// When true (the <b>default</b> — preserving today's behaviour, where the comparer never looked at
    /// them) storage/physical params captured verbatim in <see cref="Model.TableDefinition.TrailingOptions"/>
    /// (<c>WITH (fillfactor=…)</c>, <c>TABLESPACE …</c>) are ignored. Set <c>false</c> to surface a
    /// <see cref="AlterTableStorageChange"/> when they differ.
    /// </summary>
    public bool IgnoreStorageParameters { get; init; } = true;

    /// <summary>
    /// When false (the <b>default</b>) identifiers are matched case-insensitively (Postgres folds unquoted
    /// names to lower case; the model's <see cref="Model.DatabaseModel.NameEquals"/> is case-insensitive).
    /// Set <c>true</c> to treat <c>"Foo"</c> and <c>foo</c> as different objects/columns (quoted-identifier
    /// sensitivity).
    /// </summary>
    public bool CaseSensitiveIdentifiers { get; init; }

    /// <summary>
    /// Whether ownership / role assignments are part of the diff. <b>Always read-only true</b>: the model
    /// does not capture object ownership or roles, so they are unconditionally ignored today. Exposed so a
    /// profile can record the intent (and a future ownership-aware diff can honour it) without lying about
    /// current behaviour.
    /// </summary>
    public bool IgnoreOwnershipAndRoles => true;

    /// <summary>
    /// Whether GRANT/REVOKE permissions are part of the diff. <b>Always read-only true</b>: permissions are
    /// not modelled, so they are unconditionally ignored today (same posture as ownership above).
    /// </summary>
    public bool IgnorePermissions => true;

    /// <summary>
    /// Whether <c>COMMENT ON</c> differences are diffed independently. Comments ARE modelled (as raw
    /// objects) and are compared; this flag is read-only <c>false</c> to document that today comments are
    /// NOT ignored. (Kept for profile symmetry / future toggling.)
    /// </summary>
    public bool IgnoreComments => false;

    /// <summary>Name equality honouring <see cref="CaseSensitiveIdentifiers"/>.</summary>
    internal bool NameEquals(string? a, string? b) =>
        CaseSensitiveIdentifiers
            ? string.Equals(a, b, System.StringComparison.Ordinal)
            : Model.DatabaseModel.NameEquals(a ?? string.Empty, b ?? string.Empty);

    /// <summary>
    /// The matching <see cref="Model.Identity.CanonicalFormOptions"/> (#51) for these comparer options, so an
    /// identity-/hash-based comparison and the field-level diff agree on whether column order is significant.
    /// </summary>
    public Model.Identity.CanonicalFormOptions ToCanonicalFormOptions() =>
        new() { IgnoreColumnOrder = IgnoreColumnOrder };
}

/// <summary>
/// Diffs a <em>source</em> model (the desired state — your project) against a <em>target</em>
/// model (the actual state — usually a live server) and produces the ordered set of changes that
/// would migrate the target to the source. This is the engine behind both <c>compare</c> and
/// <c>publish</c>.
/// </summary>
public sealed class SchemaComparer
{
    // The version profile's ObjectCapabilities decide ALTER-vs-recreate (e.g. whether a changed table
    // column can be migrated in place). Defaults to the latest profile so existing parameterless callers
    // are unaffected; pass a profile (selected from TargetPostgresVersion) to diff for an older target.
    // (Body/text canonicalization moved to Comparison/Canonicalizer in #42, so no Whitespace regex here.)
    private readonly ObjectCapabilities _capabilities;

    public SchemaComparer() : this(PostgresVersionProfile.Latest) { }

    public SchemaComparer(PostgresVersionProfile profile) => _capabilities = profile.ObjectCapabilities;

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

        // Rename pre-pass: the persisted refactor log (#136, consumed by default when present) and/or the
        // structural heuristic (#53, opt-in via DetectRenames) yield table-level renames/moves. Either makes
        // the per-kind walks skip the renamed object's create AND its drop. No log + no heuristic → no plan →
        // behaviour-preserving. Column renames from the log are applied inside CompareTables.
        IdentityDiffEngine.RenamePlan? plan = null;
        if (options.RefactorLog is { IsEmpty: false } log)
            plan = log.BuildTableRenamePlan(source, target);
        if (options.DetectRenames)
            plan = MergePlans(plan, new IdentityDiffEngine().DetectRenames(source, target));
        if (plan is not null) changes.AddRange(plan.Changes);

        CompareSchemas(source, target, changes);
        CompareSequences(source, target, changes, options, plan);
        CompareTables(source, target, changes, options, plan);
        CompareIndexes(source, target, changes, options, plan);
        CompareViews(source, target, changes, options, plan);
        CompareFunctions(source, target, changes, plan);
        CompareRawObjects(source, target, changes, options);

        // Ordering. With a dependency graph the DeploymentPlanner refines the stable phase order on real
        // Hard/Soft edges (function-before-dependent-view across phases — issue #160; skeleton pass for a
        // hard cycle) and is byte-identical to the plain phase sort when no edge binds. Without a graph:
        // the historical stable phase sort (OrderBy is stable, so same-phase changes keep insertion order —
        // the ordering the golden tests pin).
        if (options.DependencyGraph is { } graph)
            return new DeploymentPlanner().Plan(changes, graph).AllSteps;
        return changes.OrderBy(c => c.Phase).ToList();
    }

    // The explicit refactor log is authoritative: when it produced any rename it wins outright; otherwise the
    // structural heuristic stands. This avoids emitting two RENAMEs for the same object when both are active.
    private static IdentityDiffEngine.RenamePlan? MergePlans(IdentityDiffEngine.RenamePlan? log, IdentityDiffEngine.RenamePlan heuristic)
        => log is { Changes.Count: > 0 } ? log : heuristic;

    private static void CompareSchemas(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes)
    {
        foreach (var s in source.Schemas)
        {
            if (DatabaseModel.NameEquals(s.Name, "public")) continue; // always present
            if (!target.HasSchema(s.Name))
                changes.Add(new CreateSchemaChange(s.Name));
        }
    }

    private static void CompareSequences(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes,
        ComparerOptions options, IdentityDiffEngine.RenamePlan? plan)
    {
        var tgtByName = IndexByName(target.Sequences, t => (t.Schema, t.Name));
        foreach (var s in source.Sequences)
        {
            // A sequence the rename pre-pass already satisfied (it was renamed FROM a target sequence) is
            // not (re)created here — the ALTER SEQUENCE … RENAME TO already produced it.
            if (plan is not null && plan.NewSatisfied(IdentityDiffEngine.KindSequence, $"{s.Schema}.{s.Name}"))
                continue;

            tgtByName.TryGetValue((s.Schema, s.Name), out var tgt);
            if (tgt is null)
                changes.Add(new CreateSequenceChange(s));
            else if (SequenceOptionsDiffer(s, tgt) && SqlEmitter.SequenceOptions(s).Length > 0)
                changes.Add(new AlterSequenceChange(s));
        }

        // DropSequenceChange (issue #53): a sequence present in the target but absent from source is dropped
        // when --allow-drops is set — unless the rename pre-pass already consumed it as a rename source.
        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Sequences, t => (t.Schema, t.Name));
            foreach (var tgt in target.Sequences)
            {
                if (srcByName.ContainsKey((tgt.Schema, tgt.Name))) continue;
                if (plan is not null && plan.OldConsumed(IdentityDiffEngine.KindSequence, $"{tgt.Schema}.{tgt.Name}")) continue;
                changes.Add(new DropSequenceChange(tgt.Schema, tgt.Name));
            }
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

    private void CompareTables(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes,
        ComparerOptions options, IdentityDiffEngine.RenamePlan? plan)
    {
        var tgtByName = IndexByName(target.Tables, t => (t.Schema, t.Name));
        foreach (var src in source.Tables)
        {
            tgtByName.TryGetValue((src.Schema, src.Name), out var tgt);

            // A table the rename pre-pass produced (renamed FROM a target table): the structure/meaning are
            // identical (that's what made it a pure rename), so the only step is the ALTER … RENAME already
            // emitted — fall through to nothing here. We still resolve `tgt` to the OLD record so any
            // *parallel* alteration (there is none for a pure rename) would diff correctly.
            if (tgt is null && plan is not null
                && plan.NewSatisfied(IdentityDiffEngine.KindTable, src.QualifiedName))
                continue;

            if (tgt is null)
            {
                changes.Add(new CreateTableChange(src));
                foreach (var fk in src.ForeignKeys)
                    changes.Add(new AddForeignKeyChange(src, fk));
                continue;
            }

            // Logged column renames (#136): emit ALTER TABLE … RENAME COLUMN (data-preserving) and remember
            // the source NEW name and target OLD name so the add/drop walk below skips them — and so any
            // parallel facet change on a renamed column still diffs (source new col vs target old col).
            var colRenames = options.RefactorLog is { IsEmpty: false } rlog
                ? rlog.ColumnRenamesFor(source, target, src, tgt)
                : System.Array.Empty<(string Old, string New)>();
            var renamedNewCols = new HashSet<string>(colRenames.Select(r => r.New), StringComparer.OrdinalIgnoreCase);
            var renamedOldCols = new HashSet<string>(colRenames.Select(r => r.Old), StringComparer.OrdinalIgnoreCase);
            foreach (var (oldCol, newCol) in colRenames)
                changes.Add(new RenameColumnChange(src.Schema, src.Name, oldCol, newCol));

            // Columns present in source but not target -> add. A changed column is migrated in place
            // (AlterColumnChange) only when the version profile's ObjectCapabilities says every differing
            // facet (type / nullability / default) is ALTER-able on the target; otherwise the column must
            // be recreated (drop + re-add). The latest profile permits all three, so this is behaviour-
            // preserving by default — the decision is no longer an inline version branch.
            foreach (var col in src.Columns)
            {
                // A renamed column: pair the source NEW column with the target OLD column (the RENAME already
                // emitted) and diff only the remaining facets — never re-add it.
                if (renamedNewCols.Contains(col.Name))
                {
                    var oldName = colRenames.First(r => DatabaseModel.NameEquals(r.New, col.Name)).Old;
                    var renamedExisting = FindColumn(tgt, oldName, options);
                    if (renamedExisting is not null && !ColumnsEqual(renamedExisting, col))
                        changes.Add(new AlterColumnChange(src.Schema, src.Name, renamedExisting with { Name = col.Name }, col));
                    continue;
                }

                var existing = FindColumn(tgt, col.Name, options);
                if (existing is null)
                {
                    changes.Add(new AddColumnChange(src.Schema, src.Name, col));
                }
                else if (!ColumnsEqual(existing, col))
                {
                    var typeChanged = !string.Equals(existing.DataType, col.DataType, StringComparison.OrdinalIgnoreCase);
                    var nullabilityChanged = existing.IsNullable != col.IsNullable;
                    var defaultChanged = !DefaultsEqual(existing.Default, col.Default);

                    if (_capabilities.CanAlterColumn(typeChanged, nullabilityChanged, defaultChanged))
                        changes.Add(new AlterColumnChange(src.Schema, src.Name, existing, col));
                    else
                    {
                        // No in-place ALTER path on this target version → recreate the column.
                        changes.Add(new DropColumnChange(src.Schema, src.Name, existing.Name));
                        changes.Add(new AddColumnChange(src.Schema, src.Name, col));
                    }
                }
            }

            // Columns present in target but not source -> drop (guarded). A column consumed by a logged
            // rename (its OLD name) is NOT dropped — the RENAME COLUMN already accounted for it.
            if (options.DropObjectsNotInSource)
            {
                foreach (var col in tgt.Columns.Where(c => FindColumn(src, c.Name, options) is null
                                                           && !renamedOldCols.Contains(c.Name)))
                    changes.Add(new DropColumnChange(src.Schema, src.Name, col.Name));
            }

            CompareColumnOrder(src, tgt, changes, options);
            CompareStorageOptions(src, tgt, changes, options);
            CompareForeignKeys(src, tgt, changes, options);
            ComparePrimaryKey(src, tgt, changes, options);
            CompareUniqueConstraints(src, tgt, changes, options);
            CompareChecks(src, tgt, changes, options);
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Tables, t => (t.Schema, t.Name));
            foreach (var tgt in target.Tables)
            {
                if (srcByName.ContainsKey((tgt.Schema, tgt.Name))) continue;
                // A table renamed away (consumed by the rename pre-pass) must not also be dropped.
                if (plan is not null && plan.OldConsumed(IdentityDiffEngine.KindTable, tgt.QualifiedName)) continue;
                changes.Add(new DropTableChange(tgt.Schema, tgt.Name));
            }
        }
    }

    // Unique-constraint alteration on an existing table (issue #53): additions used to be emitted only as
    // part of CREATE TABLE; here we add a unique constraint present in source but not target, and (guarded)
    // drop one present in target but not source. Constraints are matched by their column set (order-
    // insensitive), the same signature the identity model uses.
    private static void CompareUniqueConstraints(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        static string Sig(UniqueConstraintDefinition u) =>
            string.Join(",", u.Columns.Select(c => c.ToLowerInvariant()).OrderBy(c => c, StringComparer.Ordinal))
            // Attributes are part of the constraint's shape — a NULLS NOT DISTINCT / INCLUDE / DEFERRABLE
            // flip must surface as a change instead of comparing equal on columns alone.
            + (u.NullsNotDistinct ? "|nnd" : "")
            + (u.Include is { Count: > 0 } inc ? "|inc:" + string.Join(",", inc.Select(c => c.ToLowerInvariant())) : "")
            + (u.Deferrable ? "|def" : "")
            + (u.InitiallyDeferred ? "|initdef" : "");

        // Skip the target signature set unless the source has unique constraints to match (no source ⇒ no add).
        if (src.Unique.Count > 0)
        {
            var targetSigs = tgt.Unique.Select(Sig).ToHashSet(StringComparer.Ordinal);
            foreach (var u in src.Unique)
                if (!targetSigs.Contains(Sig(u)))
                    changes.Add(new AddUniqueConstraintChange(src.Schema, src.Name, u));
        }

        if (options.DropObjectsNotInSource && tgt.Unique.Count > 0)
        {
            var sourceSigs = src.Unique.Select(Sig).ToHashSet(StringComparer.Ordinal);
            foreach (var u in tgt.Unique.Where(u => u.Name is not null && !sourceSigs.Contains(Sig(u))))
                changes.Add(new DropUniqueConstraintChange(src.Schema, src.Name, u.Name!));
        }
    }

    // Column lookup honouring CaseSensitiveIdentifiers: default is the model's case-insensitive FindColumn;
    // when sensitivity is on, an ordinal (case-exact) match so "Foo" and "foo" are distinct columns.
    private static ColumnDefinition? FindColumn(TableDefinition table, string name, ComparerOptions options)
    {
        if (!options.CaseSensitiveIdentifiers) return table.FindColumn(name);
        foreach (var c in table.Columns)
            if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
        return null;
    }

    // Emit a benign ColumnOrderChange when the two tables share the same columns but in a different order
    // and the caller did NOT opt into ignoring order. Off (IgnoreColumnOrder=true) => never emitted, so the
    // table compares equal on order. Default keeps order significant (matching the identity model / #51).
    private static void CompareColumnOrder(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        if (options.IgnoreColumnOrder) return;

        var cmp = options.CaseSensitiveIdentifiers ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        // Only a *pure reorder* counts: identical column SETS, differing only in sequence. Adds/drops are
        // already handled above and shouldn't double-report as an order change.
        var srcCols = src.Columns;
        var tgtCols = tgt.Columns;
        if (srcCols.Count != tgtCols.Count) return;

        // Fast path (the overwhelmingly common "same order" case): walk both column lists in lockstep
        // and bail the instant a position matches by name — no list/OrderBy allocations at all. Only when
        // a position genuinely differs do we materialize the name lists to test set-equality and (maybe)
        // emit. This preserves the exact verdict and the emitted ColumnOrderChange's name lists; it just
        // skips the two ToList()s and two OrderBy passes that the equal-order steady state paid every call.
        var sameOrder = true;
        for (var i = 0; i < srcCols.Count; i++)
        {
            if (!cmp.Equals(srcCols[i].Name, tgtCols[i].Name)) { sameOrder = false; break; }
        }
        if (sameOrder) return; // identical sequence → nothing to report

        var srcNames = new List<string>(srcCols.Count);
        foreach (var c in srcCols) srcNames.Add(c.Name);
        var tgtNames = new List<string>(tgtCols.Count);
        foreach (var c in tgtCols) tgtNames.Add(c.Name);

        // Same multiset of names but a different sequence ⇒ a pure reorder; a differing set means an
        // add/drop already reported above, so we must NOT double-report it here.
        if (!srcNames.OrderBy(n => n, cmp).SequenceEqual(tgtNames.OrderBy(n => n, cmp), cmp)) return;

        changes.Add(new ColumnOrderChange(src.Schema, src.Name, srcNames, tgtNames));
    }

    // Emit an AlterTableStorageChange when the verbatim trailing storage clause (WITH (...) / TABLESPACE)
    // differs and the caller opted IN to comparing storage params (IgnoreStorageParameters=false). Default
    // (true) ignores them entirely — exactly today's behaviour, where the comparer never read TrailingOptions.
    private static void CompareStorageOptions(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        if (options.IgnoreStorageParameters) return;

        var s = NormalizeStorage(src.TrailingOptions);
        var t = NormalizeStorage(tgt.TrailingOptions);
        if (!string.Equals(s, t, StringComparison.Ordinal))
            changes.Add(new AlterTableStorageChange(src.Schema, src.Name, src.TrailingOptions, tgt.TrailingOptions));
    }

    // Canonicalize the trailing clause for storage comparison: lower-case + collapse whitespace so
    // "WITH (fillfactor=70)" and "with ( fillfactor = 70 )" don't churn. Null/empty → "".
    private static string NormalizeStorage(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : NormalizeText(s).ToLowerInvariant();

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

        if (!srcPk.Columns.SequenceEqual(tgtPk.Columns, StringComparer.OrdinalIgnoreCase)
            || !ConstraintAttributesEqual(srcPk.Include, srcPk.Deferrable, srcPk.InitiallyDeferred,
                                          tgtPk.Include, tgtPk.Deferrable, tgtPk.InitiallyDeferred))
        {
            changes.Add(new DropPrimaryKeyChange(src.Schema, src.Name, tgtPk.Name ?? $"{src.Name}_pkey"));
            changes.Add(new AddPrimaryKeyChange(src.Schema, src.Name, srcPk));
        }
    }

    /// <summary>INCLUDE/DEFERRABLE attribute equality (null and empty INCLUDE are the same shape).</summary>
    private static bool ConstraintAttributesEqual(
        IReadOnlyList<string>? aInclude, bool aDeferrable, bool aInitiallyDeferred,
        IReadOnlyList<string>? bInclude, bool bDeferrable, bool bInitiallyDeferred)
    {
        var ai = aInclude ?? System.Array.Empty<string>();
        var bi = bInclude ?? System.Array.Empty<string>();
        return ai.SequenceEqual(bi, StringComparer.OrdinalIgnoreCase)
            && aDeferrable == bDeferrable
            && aInitiallyDeferred == bInitiallyDeferred;
    }

    private void CompareChecks(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        // Constraint expressions are canonicalized with NormalizeExpression (issue #64): it folds the
        // redundant outer parens, literal casts and operator spacing that pg_get_constraintdef adds, so a
        // source `CHECK (discount BETWEEN 0 AND 100)` round-trips against the catalog's
        // `CHECK (((discount >= 0) AND (discount <= 100)))`-style rendering without a phantom add — while a
        // genuinely different predicate still differs. (BETWEEN is preserved verbatim by both sides here;
        // the win is the paren/cast/spacing folding.)
        // CHECK adds: a source check whose normalized expression isn't present in the target. Only build the
        // membership set when the source actually has checks to test (an empty source emits nothing).
        if (src.Checks.Count > 0)
        {
            var targetExprs = tgt.Checks.Select(CheckSignature).ToHashSet();
            foreach (var c in src.Checks)
                if (!targetExprs.Contains(CheckSignature(c)))
                    changes.Add(new AddCheckConstraintChange(src.Schema, src.Name, c));
        }

        // Raw table-constraint adds: skip the whole set build when the source has none (the common case —
        // EXCLUDE/verbatim constraints are rare), since an empty source can never add one.
        if (src.OtherConstraints.Count > 0)
        {
            var targetOther = tgt.OtherConstraints.Select(NormalizeConstraint).ToHashSet();
            foreach (var clause in src.OtherConstraints)
                if (!targetOther.Contains(NormalizeConstraint(clause)))
                    changes.Add(new AddRawTableConstraintChange(src.Schema, src.Name, clause));
        }

        if (options.DropObjectsNotInSource && tgt.Checks.Count > 0)
        {
            var sourceExprs = src.Checks.Select(CheckSignature).ToHashSet();
            foreach (var c in tgt.Checks.Where(c => c.Name is not null && !sourceExprs.Contains(CheckSignature(c))))
                changes.Add(new DropConstraintChange(src.Schema, src.Name, c.Name!));
        }
    }

    /// <summary>A CHECK's matching key: the canonical predicate plus NO INHERIT (structural). NOT VALID is
    /// deliberately excluded — it is validation state, and matching on it would churn a validated live
    /// constraint against a project that declares NOT VALID.</summary>
    private string CheckSignature(CheckConstraintDefinition c) =>
        NormalizeConstraint(c.Expression) + (c.NoInherit ? "|noinherit" : "");

    private void CompareForeignKeys(TableDefinition src, TableDefinition tgt, List<SchemaChange> changes, ComparerOptions options)
    {
        // Only build the target signature set when the source has FKs to test against it — a source with no
        // foreign keys can never add one, so the set (and the per-target signature strings) are pure waste.
        if (src.ForeignKeys.Count > 0)
        {
            var targetSigs = tgt.ForeignKeys.Select(ForeignKeySignature).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in src.ForeignKeys)
            {
                if (!targetSigs.Contains(ForeignKeySignature(fk)))
                    changes.Add(new AddForeignKeyChange(src, fk));
            }
        }

        if (options.DropObjectsNotInSource && tgt.ForeignKeys.Count > 0)
        {
            var sourceSigs = src.ForeignKeys.Select(ForeignKeySignature).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in tgt.ForeignKeys.Where(f => !sourceSigs.Contains(ForeignKeySignature(f))))
                changes.Add(new DropForeignKeyChange(src.Schema, src.Name, fk.Name ?? $"{src.Name}_fkey"));
        }
    }

    private void CompareIndexes(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes,
        ComparerOptions options, IdentityDiffEngine.RenamePlan? plan)
    {
        // Relations (schema, name) of source materialized views — an index on one must deploy after it.
        var matviews = new HashSet<(string, string)>(QualifiedName);
        foreach (var v in source.Views)
            if (v.IsMaterialized) matviews.Add((v.Schema, v.Name));

        var tgtByName = IndexByName(target.Indexes, i => (i.Schema, i.Name));
        foreach (var src in source.Indexes)
        {
            if (plan is not null && plan.NewSatisfied(IdentityDiffEngine.KindIndex, $"{src.Schema}.{src.Name}"))
                continue;
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
            {
                if (srcByName.ContainsKey((tgt.Schema, tgt.Name))) continue;
                if (plan is not null && plan.OldConsumed(IdentityDiffEngine.KindIndex, $"{tgt.Schema}.{tgt.Name}")) continue;
                changes.Add(new DropIndexChange(tgt.Schema, tgt.Name));
            }
        }
    }

    private void CompareViews(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes,
        ComparerOptions options, IdentityDiffEngine.RenamePlan? plan)
    {
        var tgtByName = IndexByName(target.Views, v => (v.Schema, v.Name));
        foreach (var src in source.Views)
        {
            if (plan is not null && plan.NewSatisfied(IdentityDiffEngine.KindView, $"{src.Schema}.{src.Name}"))
                continue;
            tgtByName.TryGetValue((src.Schema, src.Name), out var tgt);
            if (tgt is null)
                changes.Add(new CreateOrReplaceViewChange(src));
            else if (NormalizeBody(src.Body) != NormalizeBody(tgt.Body))
                // A changed MATERIALIZED view must drop+recreate: its emitter falls back to
                // CREATE ... IF NOT EXISTS (no OR REPLACE exists), which is a silent no-op on an
                // existing view — the body change would never be applied.
                changes.Add(src.IsMaterialized
                    ? new RecreateMaterializedViewChange(src)
                    : new CreateOrReplaceViewChange(src));
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByName = IndexByName(source.Views, v => (v.Schema, v.Name));
            foreach (var tgt in target.Views)
            {
                if (srcByName.ContainsKey((tgt.Schema, tgt.Name))) continue;
                if (plan is not null && plan.OldConsumed(IdentityDiffEngine.KindView, $"{tgt.Schema}.{tgt.Name}")) continue;
                changes.Add(new DropViewChange(tgt.Schema, tgt.Name));
            }
        }
    }

    private void CompareFunctions(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes,
        IdentityDiffEngine.RenamePlan? plan)
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
            // A function the rename pre-pass produced (renamed FROM a target function, structure+meaning
            // identical) needs only the ALTER FUNCTION … RENAME already emitted.
            if (plan is not null && plan.NewSatisfied(IdentityDiffEngine.KindFunction, $"{src.Schema}.{src.Name}"))
                continue;

            // Match by schema.name (reliable for the common, non-overloaded case); when a name has
            // multiple overloads, disambiguate by normalized argument types.
            tgtByName.TryGetValue((src.Schema, src.Name), out var candidates);
            FunctionDefinition? tgt = candidates is null ? null
                : candidates.Count <= 1
                    ? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault(c => NormalizeText(c.ArgTypes) == NormalizeText(src.ArgTypes));

            if (tgt is null)
            {
                changes.Add(new CreateOrReplaceFunctionChange(src));
                continue;
            }

            if (NormalizeBody(src.Body) == NormalizeBody(tgt.Body)) continue; // identical → no change

            // Structured function delta (issue #53): when the bodies differ ONLY in volatility (the rest of
            // the body canonicalizes equal once the volatility keyword is neutralised), emit the precise
            // ALTER FUNCTION … <VOLATILITY> instead of replaying the whole body. Any other body difference
            // falls back to the existing CREATE OR REPLACE (Postgres can redefine a function in place).
            var srcVol = FunctionFacts.Volatility(src);
            var tgtVol = FunctionFacts.Volatility(tgt);
            if (srcVol != tgtVol
                && NormalizeBody(FunctionFacts.BodyWithoutVolatility(src.Body))
                   == NormalizeBody(FunctionFacts.BodyWithoutVolatility(tgt.Body)))
            {
                changes.Add(new AlterFunctionAttributesChange(src, srcVol));
                continue;
            }

            changes.Add(new CreateOrReplaceFunctionChange(src));
        }
    }

    private void CompareRawObjects(DatabaseModel source, DatabaseModel target, List<SchemaChange> changes, ComparerOptions options)
    {
        // Pair raw objects on a kind-canonical COMPARISON KEY (issue #61) rather than the verbatim stored
        // Identity string. For most kinds the key IS the identity; for kinds whose project-parse and live-
        // reconstruction historically built mismatching identities (cast, operator, operator-class) the key
        // strips that divergence (operator symbol only, no doubled schema, normalized cast spelling) so an
        // unchanged round-trip pairs them instead of emitting a phantom create. FindObject matched the first
        // occurrence case-insensitively — mirror that with a dict.
        var tgtByKey = new Dictionary<string, RawObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in target.Objects) tgtByKey.TryAdd(RawObjectMeta.ComparisonKey(o), o);

        // Target table names for the typed/partition-table presence check below, indexed ONCE on first
        // use instead of a linear Tables scan per raw table object — that scan was quadratic on a
        // heavily partitioned schema, where every partition child is a raw `table:` object (same fix
        // shape as Fix E). Lazy: models with no raw table objects never pay for the set.
        HashSet<(string, string)>? tgtTableNames = null;

        foreach (var src in source.Objects)
        {
            tgtByKey.TryGetValue(RawObjectMeta.ComparisonKey(src), out var tgt);
            if (tgt is null)
            {
                // A typed/partition table (CREATE TABLE … OF type / PARTITION OF) is modeled in the
                // project as a raw `table:` object, but the live reader returns it as a real
                // TableDefinition — so treat it as present when the catalog has that table.
                if (src.Kind == ObjectKind.Table)
                {
                    if (tgtTableNames is null)
                    {
                        tgtTableNames = new HashSet<(string, string)>(QualifiedName);
                        foreach (var t in target.Tables) tgtTableNames.Add((t.Schema, t.Name));
                    }
                    if (tgtTableNames.Contains((src.Schema, src.Name))) continue;
                }
                changes.Add(new CreateRawObjectChange(src));
            }
            else if (src.BodyComparable && tgt.BodyComparable
                     // Identity-only kinds (extension, text-search dict/config, FDW, server) reconstruct
                     // canonical DDL the source never matches textually — an identity hit means "same
                     // object exists", so never body-diff them or every round-trip churns a phantom recreate.
                     && !RawObjectMeta.ComparesByIdentityOnly(src.Kind)
                     && NormalizeRawObjectBody(src) != NormalizeRawObjectBody(tgt))
            {
                // Field-level delta (issue #53): an enum type that ONLY gained new labels (the existing
                // labels are unchanged and in order) is altered in place with ALTER TYPE … ADD VALUE
                // instead of the forbidden/destructive drop+recreate.
                if (src.Kind == ObjectKind.Type
                    && EnumLabelDelta(src, tgt) is { Count: > 0 } added
                    && EnumLabelsArePureAddition(src, tgt))
                {
                    changes.Add(new AddEnumValuesChange(src.Schema, src.Name, added));
                    continue;
                }

                // A destructive recreate (type/domain/foreign table can cascade-drop columns) is
                // only emitted when drops are allowed; in-place redefinitions always proceed.
                if (RawObjectMeta.IsDestructiveRecreate(src.Kind) && !options.DropObjectsNotInSource)
                    continue;
                changes.Add(new RecreateRawObjectChange(src));
            }
        }

        if (options.DropObjectsNotInSource)
        {
            var srcByKey = new Dictionary<string, RawObjectDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in source.Objects) srcByKey.TryAdd(RawObjectMeta.ComparisonKey(o), o);
            foreach (var tgt in target.Objects)
                if (tgt.Kind != ObjectKind.Comment && !srcByKey.ContainsKey(RawObjectMeta.ComparisonKey(tgt)))
                    changes.Add(new DropRawObjectChange(tgt));
        }
    }

    // Body normalization for raw objects, kind-aware (issue #61). Most kinds use NormalizeRawBody as before;
    // triggers additionally fold the redundant double-parens pg_get_triggerdef wraps the WHEN clause in
    // (`WHEN ((expr))` vs source `WHEN (expr)`) and the literal `EXECUTE PROCEDURE`/`EXECUTE FUNCTION`
    // synonym, so a semantically-identical trigger round-trips with no phantom recreate while a genuine
    // body change still diffs.
    private static string NormalizeRawObjectBody(RawObjectDefinition o) =>
        o.Kind == ObjectKind.Trigger ? Canonicalizer.NormalizeTriggerBody(o.Body) : NormalizeRawBody(o.Body);

    // ---- enum-label field delta helpers (issue #53) ------------------------------------------------

    // Labels added in source that are not in target, preserving source order. Returns empty when the kind
    // isn't an enum or labels can't be extracted from both sides.
    private static IReadOnlyList<string> EnumLabelDelta(RawObjectDefinition src, RawObjectDefinition tgt)
    {
        var s = EnumLabels(src.Body);
        var t = EnumLabels(tgt.Body);
        if (s is null || t is null) return System.Array.Empty<string>();
        var tset = new HashSet<string>(t, StringComparer.Ordinal);
        return s.Where(l => !tset.Contains(l)).ToList();
    }

    // A pure addition = every TARGET label is still present in SOURCE in the same relative order (no removal,
    // no reorder). ALTER TYPE … ADD VALUE can only append/insert labels, never drop or reorder, so anything
    // else must fall through to the (guarded, destructive) recreate.
    private static bool EnumLabelsArePureAddition(RawObjectDefinition src, RawObjectDefinition tgt)
    {
        var s = EnumLabels(src.Body);
        var t = EnumLabels(tgt.Body);
        if (s is null || t is null) return false;
        // t must be a subsequence of s (order-preserving).
        int i = 0;
        foreach (var label in s) { if (i < t.Count && string.Equals(label, t[i], StringComparison.Ordinal)) i++; }
        return i == t.Count;
    }

    private static readonly System.Text.RegularExpressions.Regex EnumBody =
        new(@"as\s+enum\s*\((?<labels>.*)\)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex EnumLabel =
        new(@"'(?<v>(?:[^']|'')*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Extract the ordered enum labels from a CREATE TYPE … AS ENUM ('a','b',…) body, or null when the body
    // is not an enum (so a non-enum composite/range type never misfires the ADD VALUE path).
    private static IReadOnlyList<string>? EnumLabels(string body)
    {
        var m = EnumBody.Match(body ?? "");
        if (!m.Success) return null;
        var labels = new List<string>();
        foreach (System.Text.RegularExpressions.Match lm in EnumLabel.Matches(m.Groups["labels"].Value))
            labels.Add(lm.Groups["v"].Value.Replace("''", "'"));
        return labels;
    }

    // ---- equality helpers ----------------------------------------------------------------

    private bool ColumnsEqual(ColumnDefinition a, ColumnDefinition b) =>
        string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)
        && a.IsNullable == b.IsNullable
        && a.IsSerial == b.IsSerial
        && (a.IsSerial || DefaultsEqual(a.Default, b.Default)) // serial's nextval default is implicit
        && a.IsIdentity == b.IsIdentity
        // Generated-column expressions canonicalize with NormalizeExpression (issue #64) so a source
        // `GENERATED ALWAYS AS (upper(full_name)) STORED` matches the catalog's parenthesised/cast rendering
        // `(upper(full_name))` without a phantom column recreate.
        && NormalizeExpression(a.GeneratedExpression ?? "") == NormalizeExpression(b.GeneratedExpression ?? "")
        // STORED vs VIRTUAL (PG18) are different physical shapes — a kind flip must surface as a change.
        && (a.GeneratedExpression is null || a.GeneratedIsStored == b.GeneratedIsStored);

    private bool DefaultsEqual(string? a, string? b)
    {
        var na = NormalizeDefault(a);
        var nb = NormalizeDefault(b);
        return na == nb;
    }

    // Canonicalization (NormalizeText/NormalizeDefault/NormalizeBody/NormalizeRawBody) now lives in
    // Canonicalizer — a single source of truth shared with Model/Identity/CanonicalHash (issue #42),
    // so the semantic hash hashes byte-for-byte the same canonical text the comparer diffs on. These
    // are thin delegates kept so the rest of this file reads unchanged.
    private static string NormalizeDefault(string? d) => Canonicalizer.NormalizeDefault(d);

    /// <summary>Body comparison for verbatim objects: case-, whitespace-, punctuation-spacing-, dollar-tag-, literal-cast- and trailing-`;`-agnostic.</summary>
    private static string NormalizeBody(string s) => Canonicalizer.NormalizeBody(s);

    /// <summary>Raw single-statement DDL additionally ignores identifier quoting — the catalog reader
    /// quotes names (e.g. <c>CREATE EXTENSION "btree_gist"</c>) that a project usually writes bare —
    /// and the <c>IF NOT EXISTS</c> idempotency hint.</summary>
    private static string NormalizeRawBody(string s) => Canonicalizer.NormalizeRawBody(s);

    private static bool IndexesEqual(IndexDefinition a, IndexDefinition b) =>
        a.IsUnique == b.IsUnique
        && string.Equals(a.Method ?? "btree", b.Method ?? "btree", StringComparison.OrdinalIgnoreCase)
        && a.Columns.Select(NormalizeIndexColumn).SequenceEqual(b.Columns.Select(NormalizeIndexColumn))
        && NormalizeText(a.WhereClause ?? "") == NormalizeText(b.WhereClause ?? "");

    // Index columns come quoted from the catalog (pg_get_indexdef) but usually bare from a project file;
    // strip quotes so "email" and email compare equal. Also drop the REDUNDANT sort/null-ordering modifiers
    // a project may spell out (ASC; NULLS LAST for ASC; NULLS FIRST for DESC) — pg_get_indexdef omits these
    // defaults, so without folding them an index like `(lower(x) text_pattern_ops ASC NULLS LAST)` churns a
    // phantom drop+recreate on every round-trip. Non-default ordering (DESC, NULLS FIRST on ASC, NULLS LAST
    // on DESC) is preserved, so a genuine ordering change still diffs (#101).
    private static readonly Regex IdxAsc = new(@"\basc\b", RegexOptions.Compiled);
    private static readonly Regex IdxNullsLast = new(@"\bnulls\s+last\b", RegexOptions.Compiled);
    private static readonly Regex IdxNullsFirst = new(@"\bnulls\s+first\b", RegexOptions.Compiled);
    private static readonly Regex IdxWs = new(@"\s+", RegexOptions.Compiled);
    private static string NormalizeIndexColumn(string c)
    {
        var s = NormalizeText(c).Replace("\"", "");
        var desc = Regex.IsMatch(s, @"\bdesc\b");
        s = IdxAsc.Replace(s, " ");
        s = desc ? IdxNullsFirst.Replace(s, " ")   // NULLS FIRST is the default under DESC
                 : IdxNullsLast.Replace(s, " ");    // NULLS LAST is the default under ASC
        return IdxWs.Replace(s, " ").Trim();
    }

    private static string ForeignKeySignature(ForeignKeyDefinition fk) =>
        string.Join(",", fk.Columns.Select(c => c.ToLowerInvariant()))
        + "->" + fk.ReferencedSchema.ToLowerInvariant() + "." + fk.ReferencedTable.ToLowerInvariant()
        + "(" + string.Join(",", fk.ReferencedColumns.Select(c => c.ToLowerInvariant())) + ")"
        // Referential actions and DEFERRABLE/MATCH are part of the FK's semantics — a flip must surface
        // as a change. NO ACTION ≡ absent (the parser may carry it explicitly; the catalog reader omits
        // it), and NOT VALID is deliberately EXCLUDED: it is validation state, not shape — matching on it
        // would churn a validated live FK against a project that declares NOT VALID.
        + RefActionSig("|del:", fk.OnDelete) + RefActionSig("|upd:", fk.OnUpdate)
        + (fk.Match is null ? "" : "|match:" + fk.Match.ToLowerInvariant())
        + (fk.Deferrable ? "|def" : "")
        + (fk.InitiallyDeferred ? "|initdef" : "");

    private static string RefActionSig(string prefix, string? action) =>
        action is null || action.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase)
            ? "" : prefix + action.ToLowerInvariant();

    private static string NormalizeText(string s) => Canonicalizer.NormalizeText(s);

    /// <summary>Canonical form of a scalar expression (CHECK predicate, generated-column expression):
    /// folds redundant parens, literal casts and operator spacing (issue #64). Thin delegate to the shared
    /// <see cref="Canonicalizer.NormalizeExpression"/> so the comparer and CanonicalHash agree.</summary>
    private static string NormalizeExpression(string s) => Canonicalizer.NormalizeExpression(s);

    /// <summary>Canonical form of a table constraint clause (CHECK body, EXCLUDE/other-constraint clause).
    /// Same folding as <see cref="NormalizeExpression"/>; named separately for intent at the call site.</summary>
    private static string NormalizeConstraint(string s) => Canonicalizer.NormalizeExpression(s);
}
