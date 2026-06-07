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

    /// <summary>
    /// The key two raw objects are paired on by the comparer (issue #61). For most kinds this is just the
    /// stored <see cref="RawObjectDefinition.Identity"/>. A few kinds historically built mismatching
    /// identities between a project parse and a live-catalog reconstruction; for those we derive a canonical
    /// key both sides agree on so an unchanged object round-trips without a phantom create:
    /// <list type="bullet">
    ///   <item><b>Cast</b> — <c>cast:&lt;src&gt;-&gt;&lt;tgt&gt;</c> from the <c>(src AS tgt)</c> name,
    ///     reconciling the parser's <c>cast:(src as tgt)</c> with the reader's <c>cast:src-&gt;tgt</c>.</item>
    ///   <item><b>Operator</b> — <c>operator:&lt;schema.symbol&gt;</c> (the text before the first <c>(</c>),
    ///     reconciling the parser capturing the whole options paren with the reader capturing arg types.</item>
    ///   <item><b>OperatorClass / OperatorFamily</b> — <c>&lt;kind&gt;:&lt;schema.name&gt; using &lt;method&gt;</c>
    ///     with any doubled schema (the parser's <c>afd.afd.name</c> bug) collapsed.</item>
    /// </list>
    /// The key is lower-cased and whitespace-collapsed so casing/spacing never splits a pair.
    /// </summary>
    public static string ComparisonKey(RawObjectDefinition def) => def.Kind switch
    {
        ObjectKind.Cast          => "cast:" + CastKey(def),
        ObjectKind.Operator      => "operator:" + SymbolBeforeParen(StripIdentityTag(def.Identity, "operator")),
        ObjectKind.OperatorClass => "operatorclass:" + CollapseDoubledSchema(StripIdentityTag(def.Identity, "operatorclass")),
        ObjectKind.OperatorFamily=> "operatorfamily:" + CollapseDoubledSchema(StripIdentityTag(def.Identity, "operatorfamily")),
        // Comments are paired on their canonical BODY rather than their stored identity (issue #61): both a
        // project parse and a live-catalog reconstruction emit a `COMMENT ON <target> IS '<text>'` statement,
        // and NormalizeRawBody folds the casing/whitespace/punct-spacing/quoting differences between the two
        // spellings of the same comment so an unchanged comment round-trips with no phantom create.
        ObjectKind.Comment       => "comment:" + Canonicalizer.NormalizeRawBody(def.Body),
        _ => def.Identity,
    };

    // Build a cast key from the object's name, which is "(src AS tgt)" on both sides, OR fall back to the
    // stored identity (handles the reader's `cast:src->tgt` form, which has no parsable name).
    private static string CastKey(RawObjectDefinition def)
    {
        var name = def.Name ?? "";
        var asIdx = name.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (name.StartsWith("(", StringComparison.Ordinal) && asIdx > 0)
        {
            var src = name[1..asIdx].Trim();
            var tgt = name[(asIdx + 4)..].TrimEnd(')').Trim();
            return Squash($"{src}->{tgt}");
        }
        // Identity already in `cast:src->tgt` form (reader) — drop the tag and squash.
        return Squash(StripIdentityTag(def.Identity, "cast"));
    }

    private static string StripIdentityTag(string identity, string tag)
    {
        var prefix = tag + ":";
        return identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? identity[prefix.Length..] : identity;
    }

    // The operator's "schema.symbol", i.e. everything before the first '(' (parser: options paren; reader:
    // arg-type paren) — both reduce to the same symbol token.
    private static string SymbolBeforeParen(string s)
    {
        var p = s.IndexOf('(');
        return Squash((p >= 0 ? s[..p] : s)).Replace(" ", "");
    }

    // Collapse a doubled leading schema ("afd.afd.name" -> "afd.name") produced by the historic operator-
    // class/family identity bug, so the parser and reader keys converge.
    private static string CollapseDoubledSchema(string s)
    {
        s = Squash(s);
        var dot = s.IndexOf('.');
        if (dot > 0)
        {
            var schema = s[..dot];
            var rest = s[(dot + 1)..];
            if (rest.StartsWith(schema + ".", StringComparison.OrdinalIgnoreCase))
                s = rest; // drop the duplicated schema segment
        }
        return s;
    }

    private static string Squash(string s) =>
        System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ").ToLowerInvariant();

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
