using System.Collections.Generic;
using PgProj.Core.Model;

namespace PgProj.Core.Extensibility;

/// <summary>How an object's DROP target is rendered (the only per-kind variation left in DropSql).</summary>
public enum DropTargetStyle
{
    /// <summary><c>name ON schema.table</c> — trigger / rule / policy.</summary>
    TableScoped,
    /// <summary><c>schema.name</c> — type / domain / collation / foreign-table / text-search / …</summary>
    SchemaQualified,
    /// <summary><c>name</c> — extension / language / server / FDW / event-trigger / publication.</summary>
    GlobalName,
    /// <summary>The stored signature verbatim — aggregate / operator / cast / opclass / transform / user-mapping.</summary>
    Signature,
}

/// <summary>All per-kind metadata for one <see cref="ObjectKind"/>, in one place.</summary>
public readonly record struct ObjectKindDescriptor(
    string TypeToken,
    int Phase,
    string Folder,
    string? DropKeywordOverride,
    DropTargetStyle DropStyle,
    bool ComparesByIdentityOnly,
    bool IsDestructiveRecreate)
{
    /// <summary>The DROP keyword; defaults to the enum name upper-cased when not overridden.</summary>
    public string DropKeyword(ObjectKind kind) => DropKeywordOverride ?? kind.ToString().ToUpperInvariant();
}

/// <summary>
/// The single object-kind registry (issue #44): one row per <see cref="ObjectKind"/> carrying every
/// per-kind fact — compare-filter token, deploy phase, extract folder, DROP keyword/target style, and the
/// compare flags. It replaces the parallel per-kind <c>switch</c> statements that used to live in
/// <see cref="PgProj.Core.Comparison.RawObjectMeta"/> and
/// <see cref="PgProj.Core.Comparison.SchemaCompareObjectType"/>; those now read from here, so adding a kind
/// is one new <see cref="Table"/> row instead of editing eight switches. Values are preserved exactly —
/// the golden-file deploy-script tests prove behaviour is unchanged.
/// </summary>
public static class ObjectKindRegistry
{
    /// <summary>Fallback for any kind not explicitly registered (mirrors the old <c>_ =&gt;</c> switch arms).</summary>
    public static readonly ObjectKindDescriptor Default =
        new("other", 50, "Other", null, DropTargetStyle.Signature, false, false);

    private static readonly Dictionary<ObjectKind, ObjectKindDescriptor> Table = new()
    {
        // kind                              token          phase folder          dropKeyword override         dropStyle                     idOnly destructive
        [ObjectKind.Extension]             = new("extension",      5, "Extensions",   null,                         DropTargetStyle.GlobalName,      true,  false),
        [ObjectKind.Language]              = new("language",       6, "Languages",    null,                         DropTargetStyle.GlobalName,      false, false),
        [ObjectKind.Type]                  = new("type",          14, "Types",        null,                         DropTargetStyle.SchemaQualified, false, true),
        [ObjectKind.Domain]                = new("domain",        15, "Domains",      null,                         DropTargetStyle.SchemaQualified, false, true),
        [ObjectKind.Collation]             = new("collation",     12, "Collations",   null,                         DropTargetStyle.SchemaQualified, false, false),
        [ObjectKind.Conversion]            = new("conversion",    16, "Conversions",  null,                         DropTargetStyle.SchemaQualified, false, false),
        [ObjectKind.Cast]                  = new("cast",          82, "Casts",        null,                         DropTargetStyle.Signature,       false, false),
        [ObjectKind.Operator]              = new("operator",      82, "Operators",    null,                         DropTargetStyle.Signature,       false, false),
        [ObjectKind.OperatorClass]         = new("operator",      83, "Operators",    "OPERATOR CLASS",             DropTargetStyle.Signature,       false, false),
        [ObjectKind.OperatorFamily]        = new("operator",      82, "Operators",    "OPERATOR FAMILY",            DropTargetStyle.Signature,       false, false),
        [ObjectKind.Aggregate]             = new("aggregate",     82, "Aggregates",   null,                         DropTargetStyle.Signature,       true,  false),
        [ObjectKind.Trigger]               = new("trigger",       85, "Triggers",     null,                         DropTargetStyle.TableScoped,     false, false),
        [ObjectKind.Rule]                  = new("rule",          86, "Rules",        null,                         DropTargetStyle.TableScoped,     false, false),
        [ObjectKind.Policy]                = new("policy",        87, "Policies",     null,                         DropTargetStyle.TableScoped,     false, false),
        [ObjectKind.EventTrigger]          = new("eventtrigger",  88, "EventTriggers","EVENT TRIGGER",              DropTargetStyle.GlobalName,      false, false),
        [ObjectKind.Statistics]            = new("statistics",    66, "Statistics",   null,                         DropTargetStyle.SchemaQualified, true,  false),
        [ObjectKind.ForeignDataWrapper]    = new("foreigndata",   36, "ForeignData",  "FOREIGN DATA WRAPPER",       DropTargetStyle.GlobalName,      true,  false),
        [ObjectKind.Server]                = new("foreigndata",   37, "ForeignData",  null,                         DropTargetStyle.GlobalName,      true,  false),
        [ObjectKind.UserMapping]           = new("foreigndata",   38, "ForeignData",  "USER MAPPING",               DropTargetStyle.Signature,       false, false),
        [ObjectKind.ForeignTable]          = new("foreigndata",   42, "ForeignData",  "FOREIGN TABLE",              DropTargetStyle.SchemaQualified, false, true),
        [ObjectKind.TextSearchConfiguration] = new("textsearch", 14, "TextSearch",   "TEXT SEARCH CONFIGURATION",  DropTargetStyle.SchemaQualified, true,  false),
        [ObjectKind.TextSearchDictionary]  = new("textsearch",    13, "TextSearch",   "TEXT SEARCH DICTIONARY",     DropTargetStyle.SchemaQualified, true,  false),
        [ObjectKind.TextSearchParser]      = new("textsearch",    12, "TextSearch",   "TEXT SEARCH PARSER",         DropTargetStyle.SchemaQualified, false, false),
        [ObjectKind.TextSearchTemplate]    = new("textsearch",    12, "TextSearch",   "TEXT SEARCH TEMPLATE",       DropTargetStyle.SchemaQualified, false, false),
        [ObjectKind.Transform]             = new("transform",     82, "Transforms",   null,                         DropTargetStyle.Signature,       false, false),
        [ObjectKind.Publication]           = new("publication",   84, "Publications", null,                         DropTargetStyle.GlobalName,      false, false),
        [ObjectKind.Comment]               = new("comment",       99, "Comments",     null,                         DropTargetStyle.Signature,       false, false),  // never dropped (DropSql short-circuits)
        [ObjectKind.Table]                 = new("table",         43, "Tables",       null,                         DropTargetStyle.SchemaQualified, false, true),
    };

    /// <summary>The descriptor for a kind, or <see cref="Default"/> for an unregistered one.</summary>
    public static ObjectKindDescriptor Get(ObjectKind kind) =>
        Table.TryGetValue(kind, out var d) ? d : Default;

    /// <summary>Every registered kind (for registry-driven iteration / conformance checks).</summary>
    public static IEnumerable<ObjectKind> Kinds => Table.Keys;
}
