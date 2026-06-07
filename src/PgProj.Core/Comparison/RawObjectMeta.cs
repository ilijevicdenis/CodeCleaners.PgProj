using System;
using System.Linq;
using PgProj.Core.Extensibility;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Per-kind metadata for the generic raw-object mechanism: deploy phase (dependency ordering),
/// whether recreating is destructive, how to DROP it, and where <c>extract</c> files it.
///
/// The per-kind data now lives in the single <see cref="ObjectKindRegistry"/> (issue #44) — the methods
/// below are thin accessors over it, replacing the parallel per-kind <c>switch</c> statements this class
/// used to carry. Adding a kind = one registry row, not six switch edits. Values are unchanged (the
/// golden-file deploy-script tests prove it).
/// </summary>
public static class RawObjectMeta
{
    /// <summary>Deploy ordering. Lower runs first. Aligned with the core change phases in SchemaChange.</summary>
    public static int Phase(ObjectKind kind) => ObjectKindRegistry.Get(kind).Phase;

    /// <summary>Recreating these drops dependent data/columns, so it requires --allow-drops.</summary>
    public static bool IsDestructiveRecreate(ObjectKind kind) => ObjectKindRegistry.Get(kind).IsDestructiveRecreate;

    /// <summary>
    /// Kinds whose live introspection reconstructs a <em>canonical</em> DDL that is semantically equal
    /// to the hand-written source but never textually matches it — and which no whitespace/case/quote
    /// normalization can reconcile — so they must be compared by <em>identity</em> only (presence ==
    /// equal), never by body. Comparing their bodies produces phantom non-destructive diffs on every
    /// round-trip (extract / drift / pull) for an unchanged object. (Per-kind flag in
    /// <see cref="ObjectKindRegistry"/>; motivating cases: Extension, text-search dict/config, FDW/Server,
    /// Statistics, Aggregate — canonical reconstruction never textually matches hand-written source.)
    /// Identity already encodes the object's stable name, so an identity match means "the same object
    /// exists on both sides"; a genuine rename/drop is still caught (different identity → create/drop).
    /// </summary>
    public static bool ComparesByIdentityOnly(ObjectKind kind) => ObjectKindRegistry.Get(kind).ComparesByIdentityOnly;

    /// <summary>Renders a DROP for the object (empty for comments, which are not dropped).</summary>
    public static string DropSql(RawObjectDefinition def)
    {
        if (def.Kind == ObjectKind.Comment) return string.Empty;
        var d = ObjectKindRegistry.Get(def.Kind);
        return $"DROP {d.DropKeyword(def.Kind)} IF EXISTS {DropTarget(def, d.DropStyle)};";
    }

    // The only per-kind variation left is the DROP target shape; it switches on the descriptor's
    // DropStyle (4 cases), not on each ObjectKind.
    private static string DropTarget(RawObjectDefinition def, DropTargetStyle style) => style switch
    {
        DropTargetStyle.TableScoped => $"{SqlEmitter.Quote(def.Name)} ON {QualifyString(def.OnObject ?? "")}",
        DropTargetStyle.SchemaQualified => SqlEmitter.Qualified(def.Schema, def.Name),
        DropTargetStyle.GlobalName => SqlEmitter.Quote(def.Name),
        _ => def.Name, // Signature / verbatim
    };

    private static string QualifyString(string schemaDotName)
    {
        var dot = schemaDotName.IndexOf('.');
        return dot > 0
            ? SqlEmitter.Qualified(schemaDotName[..dot], schemaDotName[(dot + 1)..])
            : SqlEmitter.Quote(schemaDotName);
    }

    public static string Folder(ObjectKind kind) => ObjectKindRegistry.Get(kind).Folder;

    /// <summary>A filesystem-safe file name derived from the object's identity.</summary>
    public static string FileName(RawObjectDefinition def)
    {
        var basis = string.IsNullOrEmpty(def.Name)
            ? def.Identity
            : (string.IsNullOrEmpty(def.Schema) ? def.Name : $"{def.Schema}.{def.Name}");
        var safe = new string(basis.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_').ToArray());
        return safe.Trim('_') is { Length: > 0 } s ? s + ".sql" : "object.sql";
    }
}
