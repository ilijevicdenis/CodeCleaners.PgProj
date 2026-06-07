using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

// Structured, identity-aware change types for Phase 11 (issue #53). These live in their own file so the
// rename pre-pass and the field-level deltas can be added without rewriting the existing CompareX methods
// in SchemaComparer — the classic change types (Create/Alter/Drop…) keep their IDs, ordering and SQL byte-
// identical. Every type here is only ever emitted when the new ComparerOptions.DetectRenames switch is on
// (or, for DropSequenceChange, when DropObjectsNotInSource is set), so the default greenfield diff — and
// therefore the golden deploy script / model JSON — is unchanged.

// ---- renames (same StableId + same CanonicalHash, FQN moved) ---------------------------------------

/// <summary>Renames a table in place via <c>ALTER TABLE … RENAME TO</c> — a structure-preserving move that
/// replaces the old Drop+Create pair for a pure rename.</summary>
public sealed record RenameTableChange(string Schema, string OldName, string NewName) : SchemaChange
{
    public override int Phase => 39; // just before CreateTable (40) so a later ADD COLUMN/constraint lands on the renamed table
    public override bool IsDestructive => false;
    public override string Describe() => $"Rename table {Schema}.{OldName} -> {Schema}.{NewName}";
    public override string ToSql() =>
        $"ALTER TABLE {SqlEmitter.Qualified(Schema, OldName)} RENAME TO {SqlEmitter.Quote(NewName)};";
}

/// <summary>Renames a sequence via <c>ALTER SEQUENCE … RENAME TO</c>.</summary>
public sealed record RenameSequenceChange(string Schema, string OldName, string NewName) : SchemaChange
{
    public override int Phase => 19;
    public override bool IsDestructive => false;
    public override string Describe() => $"Rename sequence {Schema}.{OldName} -> {Schema}.{NewName}";
    public override string ToSql() =>
        $"ALTER SEQUENCE {SqlEmitter.Qualified(Schema, OldName)} RENAME TO {SqlEmitter.Quote(NewName)};";
}

/// <summary>Renames an index via <c>ALTER INDEX … RENAME TO</c>.</summary>
public sealed record RenameIndexChange(string Schema, string OldName, string NewName) : SchemaChange
{
    public override int Phase => 64;
    public override bool IsDestructive => false;
    public override string Describe() => $"Rename index {Schema}.{OldName} -> {Schema}.{NewName}";
    public override string ToSql() =>
        $"ALTER INDEX {SqlEmitter.Qualified(Schema, OldName)} RENAME TO {SqlEmitter.Quote(NewName)};";
}

/// <summary>Renames a view via <c>ALTER VIEW/MATERIALIZED VIEW … RENAME TO</c>.</summary>
public sealed record RenameViewChange(string Schema, string OldName, string NewName, bool IsMaterialized) : SchemaChange
{
    public override int Phase => 74;
    public override bool IsDestructive => false;
    public override string Describe() => $"Rename view {Schema}.{OldName} -> {Schema}.{NewName}";
    public override string ToSql() =>
        $"ALTER {(IsMaterialized ? "MATERIALIZED VIEW" : "VIEW")} {SqlEmitter.Qualified(Schema, OldName)} RENAME TO {SqlEmitter.Quote(NewName)};";
}

/// <summary>Renames a function via <c>ALTER FUNCTION …(args) RENAME TO</c> (the arg signature is part of
/// the function's identity, so it must be spelled out in the rename).</summary>
public sealed record RenameFunctionChange(string Schema, string OldName, string NewName, string ArgTypes) : SchemaChange
{
    public override int Phase => 79;
    public override bool IsDestructive => false;
    public override string Describe() => $"Rename function {Schema}.{OldName} -> {Schema}.{NewName}";
    public override string ToSql() =>
        $"ALTER FUNCTION {SqlEmitter.Qualified(Schema, OldName)}({ArgTypes}) RENAME TO {SqlEmitter.Quote(NewName)};";
}

// ---- structured function delta (same StableId, body/attribute meaning changed) ---------------------

/// <summary>The volatility class of a function as seen in its source body.</summary>
public enum FunctionVolatility { Unknown, Immutable, Stable, Volatile }

/// <summary>
/// A precise, attribute-only function delta: when the ONLY thing that changed between two otherwise
/// body-identical functions is a settable attribute Postgres can <c>ALTER FUNCTION</c> in place
/// (volatility today; leakproof/strict/parallel/cost trivially extendable), emit the targeted
/// <c>ALTER FUNCTION … VOLATILE</c> rather than replaying the whole body with CREATE OR REPLACE.
/// </summary>
public sealed record AlterFunctionAttributesChange(
    FunctionDefinition Function,
    FunctionVolatility? Volatility) : SchemaChange
{
    public override int Phase => 80;
    public override bool IsDestructive => false;
    public override string Describe() => $"Alter function attributes {Function.Signature}";

    public override string ToSql()
    {
        var parts = new List<string>();
        if (Volatility is { } v && v != FunctionVolatility.Unknown)
            parts.Add(v.ToString().ToUpperInvariant());
        if (parts.Count == 0) return SqlEmitter.Function(Function); // defensive: nothing precise -> full replace
        var args = FunctionFacts.ArgTypeList(Function);
        return $"ALTER FUNCTION {SqlEmitter.Qualified(Function.Schema, Function.Name)}({args}) {string.Join(" ", parts)};";
    }
}

// ---- unique-constraint alteration ------------------------------------------------------------------

/// <summary>Adds a unique constraint that exists in source but not target (the alteration counterpart to a
/// table already present — additions were previously only emitted as part of a CREATE TABLE).</summary>
public sealed record AddUniqueConstraintChange(string Schema, string Table, UniqueConstraintDefinition Unique) : SchemaChange
{
    public override int Phase => 51;
    public override bool IsDestructive => false;
    public override string Describe() => $"Add unique constraint on {Schema}.{Table}";

    public override string ToSql()
    {
        var prefix = string.IsNullOrEmpty(Unique.Name) ? string.Empty : $"CONSTRAINT {SqlEmitter.Quote(Unique.Name!)} ";
        return $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} ADD {prefix}UNIQUE ({SqlEmitter.Cols(Unique.Columns)});";
    }
}

/// <summary>Drops a unique constraint present in target but absent from source (guarded by --allow-drops).</summary>
public sealed record DropUniqueConstraintChange(string Schema, string Table, string Name) : SchemaChange
{
    public override int Phase => 32;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop unique constraint {Name} on {Schema}.{Table}";
    public override string ToSql() =>
        $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} DROP CONSTRAINT {SqlEmitter.Quote(Name)};";
}

// ---- sequence drop (drop-not-in-source) ------------------------------------------------------------

/// <summary>Drops a sequence present in target but absent from source (only when --allow-drops is set).</summary>
public sealed record DropSequenceChange(string Schema, string Name) : SchemaChange
{
    public override int Phase => 93;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop sequence {Schema}.{Name}";
    public override string ToSql() => $"DROP SEQUENCE IF EXISTS {SqlEmitter.Qualified(Schema, Name)};";
}

// ---- raw-object field deltas (enum-label add, domain constraint change) -----------------------------

/// <summary>
/// Adds one or more new labels to an existing enum type with <c>ALTER TYPE … ADD VALUE</c> — the precise
/// delta for the common "append an enum label" change, instead of the destructive drop+recreate the type
/// would otherwise need (and which Postgres forbids while the type is in use).
/// </summary>
public sealed record AddEnumValuesChange(string Schema, string Name, IReadOnlyList<string> NewLabels) : SchemaChange
{
    public override int Phase => RawObjectMeta.Phase(ObjectKind.Type);
    public override bool IsDestructive => false;
    public override string Describe() => $"Add enum value(s) to type {Schema}.{Name}: {string.Join(", ", NewLabels)}";

    public override string ToSql() =>
        string.Join("\n", NewLabels.Select(l =>
            $"ALTER TYPE {SqlEmitter.Qualified(Schema, Name)} ADD VALUE IF NOT EXISTS '{l.Replace("'", "''")}';"));
}
