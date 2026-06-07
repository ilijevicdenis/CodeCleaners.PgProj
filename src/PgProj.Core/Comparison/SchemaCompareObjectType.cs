using System;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Classifies every <see cref="SchemaChange"/> into a coarse, user-facing <em>object type</em> — the unit
/// the Schema Compare include/exclude filters operate on (<c>--exclude extension,permission</c>). It is
/// deliberately coarser than the change-record type (<c>CreateTableChange</c>): a UI shows "Tables",
/// "Indexes", "Extensions" — not one bucket per DDL verb — and a DBA wants to "skip permissions" or "skip
/// extensions" with one token, irrespective of whether the change is a create, alter, or drop.
/// </summary>
/// <remarks>
/// Names are stable, lower-case, and singular (the form a CLI token / JSON field uses). They are the
/// <em>filter vocabulary</em>: <see cref="Parse"/> turns a user token back into the canonical name and
/// rejects anything unknown, so a typo in <c>--exclude</c> fails loudly rather than silently matching
/// nothing.
/// </remarks>
public static class SchemaCompareObjectType
{
    /// <summary>The canonical object-type token for a change (stable, lower-case, singular).</summary>
    public static string Of(SchemaChange change) => change switch
    {
        CreateSchemaChange => "schema",

        CreateSequenceChange or AlterSequenceChange or RenameSequenceChange or DropSequenceChange => "sequence",

        CreateTableChange or DropTableChange or RenameTableChange => "table",
        AddColumnChange or AlterColumnChange or DropColumnChange => "column",

        AddPrimaryKeyChange or DropPrimaryKeyChange => "primarykey",
        AddForeignKeyChange or DropForeignKeyChange => "foreignkey",
        AddUniqueConstraintChange or DropUniqueConstraintChange => "constraint",
        AddCheckConstraintChange or AddRawTableConstraintChange or DropConstraintChange => "constraint",

        CreateIndexChange or DropIndexChange or RenameIndexChange => "index",

        CreateOrReplaceViewChange or DropViewChange or RenameViewChange => "view",
        CreateOrReplaceFunctionChange or AlterFunctionAttributesChange or RenameFunctionChange => "function",

        // An enum-label add is a structured delta on a `type` object.
        AddEnumValuesChange => "type",

        CreateRawObjectChange raw => OfKind(raw.Def.Kind),
        RecreateRawObjectChange raw => OfKind(raw.Def.Kind),
        DropRawObjectChange raw => OfKind(raw.Def.Kind),

        _ => "other",
    };

    /// <summary>The object-type token for a raw-object <see cref="ObjectKind"/>.
    /// Reads the single <see cref="Extensibility.ObjectKindRegistry"/> (issue #44) instead of a switch.</summary>
    public static string OfKind(ObjectKind kind) => Extensibility.ObjectKindRegistry.Get(kind).TypeToken;

    /// <summary>
    /// Canonicalizes a user-supplied filter token (case-insensitive, with a few friendly aliases such as
    /// <c>permissions</c>→<c>permission</c> and <c>fk</c>→<c>foreignkey</c>). An empty token is a usage
    /// error; an unknown one is returned lower-cased as-is so the caller can still reject it explicitly.
    /// </summary>
    public static string Parse(string token)
    {
        var t = (token ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0) throw new ArgumentException("Empty object-type token.", nameof(token));
        return t switch
        {
            "schemas" => "schema",
            "sequences" => "sequence",
            "tables" => "table",
            "columns" => "column",
            "primarykeys" or "pk" or "pkey" => "primarykey",
            "foreignkeys" or "fk" => "foreignkey",
            "constraints" or "check" or "checks" => "constraint",
            "indexes" or "indices" => "index",
            "views" => "view",
            "functions" or "procedure" or "procedures" => "function",
            "extensions" => "extension",
            "languages" => "language",
            "types" => "type",
            "domains" => "domain",
            "policies" or "rls" => "policy",
            "triggers" => "trigger",
            "rules" => "rule",
            "permissions" or "grants" or "grant" or "privilege" or "privileges" => "permission",
            "comments" => "comment",
            _ => t,
        };
    }
}
