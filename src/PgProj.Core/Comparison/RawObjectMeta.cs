using System;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Per-kind metadata for the generic raw-object mechanism: deploy phase (dependency ordering),
/// whether recreating is destructive, how to DROP it, and where <c>extract</c> files it.
/// </summary>
public static class RawObjectMeta
{
    /// <summary>Deploy ordering. Lower runs first. Aligned with the core change phases in SchemaChange.</summary>
    public static int Phase(ObjectKind kind) => kind switch
    {
        ObjectKind.Extension => 5,
        ObjectKind.Language => 6,
        ObjectKind.Collation => 12,
        ObjectKind.TextSearchParser => 12,
        ObjectKind.TextSearchTemplate => 12,
        ObjectKind.TextSearchDictionary => 13,
        ObjectKind.TextSearchConfiguration => 14,   // after its dictionaries (ADD MAPPING references them)
        ObjectKind.Type => 14,
        ObjectKind.Domain => 15,
        ObjectKind.Conversion => 16,
        ObjectKind.ForeignDataWrapper => 36,
        ObjectKind.Server => 37,
        ObjectKind.UserMapping => 38,
        ObjectKind.ForeignTable => 42,
        ObjectKind.Statistics => 66,
        ObjectKind.Aggregate => 82, // needs its state/final functions
        ObjectKind.Operator => 82,
        ObjectKind.OperatorFamily => 82,
        ObjectKind.OperatorClass => 83,   // after operators/functions/families it references in AS …
        ObjectKind.Cast => 82,
        ObjectKind.Transform => 82,
        ObjectKind.Trigger => 85,
        ObjectKind.Rule => 86,
        ObjectKind.Policy => 87,
        ObjectKind.EventTrigger => 88,
        ObjectKind.Publication => 84, // after the tables it lists (FOR TABLE …)
        ObjectKind.Table => 43, // after base tables (40), before foreign keys (70)
        ObjectKind.Comment => 99, // last — every referenced object exists by now
        _ => 50,
    };

    /// <summary>Recreating these drops dependent data/columns, so it requires --allow-drops.</summary>
    public static bool IsDestructiveRecreate(ObjectKind kind) =>
        kind is ObjectKind.Type or ObjectKind.Domain or ObjectKind.ForeignTable or ObjectKind.Table;

    /// <summary>
    /// Kinds whose live introspection reconstructs a <em>canonical</em> DDL that is semantically equal
    /// to the hand-written source but never textually matches it — and which no whitespace/case/quote
    /// normalization can reconcile — so they must be compared by <em>identity</em> only (presence ==
    /// equal), never by body. Comparing their bodies produces phantom non-destructive diffs on every
    /// round-trip (extract / drift / pull) for an unchanged object. Examples that motivate each:
    /// <list type="bullet">
    /// <item><c>Extension</c> — source <c>CREATE EXTENSION btree_gist</c> vs reader
    ///   <c>CREATE EXTENSION IF NOT EXISTS "btree_gist"</c>; also <c>VERSION</c>/<c>SCHEMA</c> clauses.</item>
    /// <item><c>TextSearchConfiguration</c> — source <c>(COPY = pg_catalog.english) … ALTER MAPPING</c>
    ///   vs reader <c>(PARSER = …) … one ADD MAPPING per token type</c>: a structurally different,
    ///   re-ordered statement set for the same end state.</item>
    /// <item><c>TextSearchDictionary</c> — option spelling/ordering (<c>dictinitoption</c> rendering).</item>
    /// <item><c>ForeignDataWrapper</c>/<c>Server</c> — handler/validator and OPTIONS(...) formatting.</item>
    /// </list>
    /// Identity already encodes the object's stable name, so an identity match means "the same object
    /// exists on both sides"; a genuine rename/drop is still caught (different identity → create/drop).
    /// </summary>
    public static bool ComparesByIdentityOnly(ObjectKind kind) => kind switch
    {
        ObjectKind.Extension
            or ObjectKind.TextSearchDictionary
            or ObjectKind.TextSearchConfiguration
            or ObjectKind.ForeignDataWrapper
            or ObjectKind.Server => true,
        _ => false,
    };

    private static string DropKeyword(ObjectKind kind) => kind switch
    {
        ObjectKind.OperatorClass => "OPERATOR CLASS",
        ObjectKind.OperatorFamily => "OPERATOR FAMILY",
        ObjectKind.EventTrigger => "EVENT TRIGGER",
        ObjectKind.ForeignDataWrapper => "FOREIGN DATA WRAPPER",
        ObjectKind.UserMapping => "USER MAPPING",
        ObjectKind.ForeignTable => "FOREIGN TABLE",
        ObjectKind.TextSearchConfiguration => "TEXT SEARCH CONFIGURATION",
        ObjectKind.TextSearchDictionary => "TEXT SEARCH DICTIONARY",
        ObjectKind.TextSearchParser => "TEXT SEARCH PARSER",
        ObjectKind.TextSearchTemplate => "TEXT SEARCH TEMPLATE",
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>Renders a DROP for the object (empty for comments, which are not dropped).</summary>
    public static string DropSql(RawObjectDefinition def)
    {
        if (def.Kind == ObjectKind.Comment) return string.Empty;
        return $"DROP {DropKeyword(def.Kind)} IF EXISTS {DropTarget(def)};";
    }

    private static string DropTarget(RawObjectDefinition def) => def.Kind switch
    {
        // table-scoped
        ObjectKind.Trigger or ObjectKind.Rule or ObjectKind.Policy =>
            $"{SqlEmitter.Quote(def.Name)} ON {QualifyString(def.OnObject ?? "")}",
        // schema-qualified
        ObjectKind.Type or ObjectKind.Domain or ObjectKind.Collation or ObjectKind.Conversion
            or ObjectKind.Statistics or ObjectKind.ForeignTable or ObjectKind.Table or ObjectKind.TextSearchConfiguration
            or ObjectKind.TextSearchDictionary or ObjectKind.TextSearchParser or ObjectKind.TextSearchTemplate =>
            SqlEmitter.Qualified(def.Schema, def.Name),
        // global name
        ObjectKind.Extension or ObjectKind.Language or ObjectKind.Server
            or ObjectKind.ForeignDataWrapper or ObjectKind.EventTrigger or ObjectKind.Publication =>
            SqlEmitter.Quote(def.Name),
        // signature / verbatim (aggregate, operator, cast, opclass/family, transform, user mapping)
        _ => def.Name,
    };

    private static string QualifyString(string schemaDotName)
    {
        var dot = schemaDotName.IndexOf('.');
        return dot > 0
            ? SqlEmitter.Qualified(schemaDotName[..dot], schemaDotName[(dot + 1)..])
            : SqlEmitter.Quote(schemaDotName);
    }

    public static string Folder(ObjectKind kind) => kind switch
    {
        ObjectKind.Extension => "Extensions",
        ObjectKind.Language => "Languages",
        ObjectKind.Type => "Types",
        ObjectKind.Domain => "Domains",
        ObjectKind.Collation => "Collations",
        ObjectKind.Conversion => "Conversions",
        ObjectKind.Cast => "Casts",
        ObjectKind.Operator or ObjectKind.OperatorClass or ObjectKind.OperatorFamily => "Operators",
        ObjectKind.Aggregate => "Aggregates",
        ObjectKind.Trigger => "Triggers",
        ObjectKind.Rule => "Rules",
        ObjectKind.Policy => "Policies",
        ObjectKind.EventTrigger => "EventTriggers",
        ObjectKind.Statistics => "Statistics",
        ObjectKind.ForeignDataWrapper or ObjectKind.Server or ObjectKind.UserMapping or ObjectKind.ForeignTable => "ForeignData",
        ObjectKind.TextSearchConfiguration or ObjectKind.TextSearchDictionary
            or ObjectKind.TextSearchParser or ObjectKind.TextSearchTemplate => "TextSearch",
        ObjectKind.Transform => "Transforms",
        ObjectKind.Publication => "Publications",
        ObjectKind.Comment => "Comments",
        ObjectKind.Table => "Tables",
        _ => "Other",
    };

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
