using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Testing;

/// <summary>
/// Synthesises a minimal *valid* INSERT for a table from the semantic model (the crux of <see
/// cref="SuiteScaffolder"/>). It emits a value only for the columns it must — mandatory columns with no
/// auto-source — letting identity / serial / generated / defaulted / nullable columns fill themselves, which
/// keeps the row small and side-effect free. Mandatory foreign keys are satisfied by recursively inserting a
/// depth-1 parent row first (bounded to one level — deeper or cyclic chains downgrade). Values are deterministic
/// (no randomness; <c>gen_random_uuid()</c>/<c>now()</c> evaluate at run time). When a required value cannot be
/// synthesised (enum/domain/UDT/unknown type, a generated key, an unsatisfiable parent), it returns <c>false</c>
/// with a human reason so the caller can emit an inconclusive stub instead of a false assertion.
/// </summary>
internal static class BaselineRowSynthesizer
{
    /// <summary>
    /// Build an INSERT for <paramref name="table"/>. <paramref name="overrides"/> pins specific columns to a
    /// literal (e.g. <c>NULL</c> for a NOT NULL test, an orphan value for an FK test); <paramref name="forceEmit"/>
    /// forces columns to be emitted even when they would normally be auto-filled (e.g. a PK/unique key). On
    /// success, <paramref name="prelude"/> holds the parent INSERTs that must run first and <paramref name="insert"/>
    /// is the table's own INSERT statement.
    /// </summary>
    public static bool TryBuildInsert(
        DatabaseModel model, TableDefinition table,
        IReadOnlyDictionary<string, string>? overrides, ISet<string>? forceEmit, int depth,
        out List<string> prelude, out string insert, out string reason)
    {
        prelude = new List<string>();
        insert = "";
        reason = "";
        overrides ??= EmptyMap;
        forceEmit ??= EmptySet;

        if (depth > 1)
        {
            reason = "foreign-key chain is deeper than one level — cannot seed a valid parent automatically";
            return false;
        }

        // Pass 1 — satisfy mandatory/forced FKs by seeding a parent row, recording the value each child FK
        // column must carry. A column the caller overrode (an orphan/NULL value) is deliberately NOT satisfied.
        var fkValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in table.ForeignKeys)
        {
            // Only satisfy when at least one child column will be emitted with a synthesised value.
            bool needed = fk.Columns.Any(cn =>
            {
                var col = table.FindColumn(cn);
                return col is not null && !overrides.ContainsKey(cn) && (forceEmit.Contains(cn) || IsMandatory(col));
            });
            if (!needed) continue;

            var parent = model.FindTable(fk.ReferencedSchema, fk.ReferencedTable);
            if (parent is null)
            {
                reason = $"foreign key references {fk.ReferencedSchema}.{fk.ReferencedTable}, which is outside the project model";
                return false;
            }

            var parentOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fk.Columns.Count; i++)
            {
                var childCol = table.FindColumn(fk.Columns[i]);
                if (childCol is null) { reason = $"foreign key column '{fk.Columns[i]}' not found on the table"; return false; }
                if (overrides.ContainsKey(fk.Columns[i])) continue; // caller pinned it (orphan/NULL) — don't seed a parent for it
                if (!TryLiteral(childCol.DataType, out var v))
                {
                    reason = $"cannot synthesise a value for foreign-key column '{childCol.Name}' of type '{childCol.DataType}'";
                    return false;
                }
                fkValues[fk.Columns[i]] = v;
                if (i < fk.ReferencedColumns.Count) parentOverrides[fk.ReferencedColumns[i]] = v;
            }

            if (parentOverrides.Count > 0)
            {
                if (!TryBuildInsert(model, parent, parentOverrides,
                        new HashSet<string>(parentOverrides.Keys, StringComparer.OrdinalIgnoreCase), depth + 1,
                        out var parentPrelude, out var parentInsert, out reason))
                    return false;
                prelude.AddRange(parentPrelude);
                prelude.Add(parentInsert);
            }
        }

        // Pass 2 — build the table's own row.
        var cols = new List<string>();
        var vals = new List<string>();
        bool needOverriding = false;

        foreach (var col in table.Columns)
        {
            string value;
            if (overrides.TryGetValue(col.Name, out var ov))
            {
                value = ov;
            }
            else if (fkValues.TryGetValue(col.Name, out var fv))
            {
                value = fv;
            }
            else if (forceEmit.Contains(col.Name) || IsMandatory(col))
            {
                if (col.GeneratedExpression is not null)
                {
                    reason = $"column '{col.Name}' is GENERATED — a key/value cannot be supplied for it";
                    return false;
                }
                if (!TryLiteral(col.DataType, out value!))
                {
                    reason = $"cannot synthesise a value for column '{col.Name}' of type '{col.DataType}'";
                    return false;
                }
            }
            else
            {
                continue; // nullable / defaulted / identity / serial / generated → let Postgres fill it.
            }

            cols.Add(col.Name);
            vals.Add(value);
            // Any explicit value for a GENERATED ALWAYS identity column (a forced key OR a seeded parent key)
            // requires OVERRIDING SYSTEM VALUE — but NULL is the absence of a value, so it doesn't.
            if (IsIdentityAlways(col) && !string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
                needOverriding = true;
        }

        insert = BuildInsertStatement(table.QualifiedName, cols, vals, needOverriding);
        return true;
    }

    /// <summary>A deterministic value for an FK column guaranteed absent in a freshly-deployed (empty) DB.</summary>
    public static bool TryOrphanValue(string dataType, out string value)
    {
        var (baseType, _, array) = Decompose(dataType);
        if (array.Length > 0) { value = ""; return false; } // array FK columns are vanishingly rare — downgrade
        switch (baseType)
        {
            case "smallint": case "integer": case "bigint": case "numeric":
            case "real": case "double precision":
                value = "987654321"; return true;
            case "text": case "character varying": case "character": case "name":
                value = "'__pgproj_orphan__'"; return true;
            case "uuid":
                value = "'00000000-0000-0000-0000-0000pgproj'"; return false; // not a valid uuid → downgrade
            default:
                value = ""; return false;
        }
    }

    // ---- value synthesis --------------------------------------------------------------------------------

    /// <summary>Map a canonical (<see cref="TypeNormalizer"/>) data type to a deterministic literal; false → unknown.</summary>
    internal static bool TryLiteral(string dataType, out string value)
    {
        var (baseType, argSpec, array) = Decompose(dataType);

        if (array.Length > 0)
        {
            if (!TryLiteral(baseType + argSpec, out var elem)) { value = ""; return false; }
            value = $"ARRAY[{elem}]::{dataType}";
            return true;
        }

        switch (baseType)
        {
            case "smallint": case "integer": case "bigint": value = "1"; return true;
            case "numeric": case "real": case "double precision": value = "1"; return true;
            case "money": value = "1::money"; return true;
            case "boolean": value = "true"; return true;
            case "text": case "character varying": case "character": case "name":
                value = "'x'"; return true;
            case "uuid": value = "gen_random_uuid()"; return true;
            case "date": value = "CURRENT_DATE"; return true;
            case "timestamp without time zone": case "timestamp with time zone": value = "now()"; return true;
            case "time without time zone": case "time with time zone": value = "CURRENT_TIME"; return true;
            case "interval": value = "interval '1 second'"; return true;
            case "bytea": value = @"'\x00'::bytea"; return true;
            case "json": value = "'{}'::json"; return true;
            case "jsonb": value = "'{}'::jsonb"; return true;
            case "xml": value = "xml('<x/>')"; return true;
            case "inet": value = "'127.0.0.1'::inet"; return true;
            case "cidr": value = "'127.0.0.0/24'::cidr"; return true;
            case "macaddr": value = "'08:00:2b:01:02:03'::macaddr"; return true;
            case "macaddr8": value = "'08:00:2b:01:02:03:04:05'::macaddr8"; return true;
            case "tsvector": value = "to_tsvector('x')"; return true;
            case "tsquery": value = "to_tsquery('x')"; return true;
            case "bit varying": value = "B'0'"; return true;
            case "bit": value = string.IsNullOrEmpty(argSpec) ? "B'0'" : ""; return string.IsNullOrEmpty(argSpec);
            case "point": value = "'(0,0)'::point"; return true;
            case "oid": value = "1::oid"; return true;
            default:
                value = ""; return false; // enum / domain / composite / user-defined / unknown → caller downgrades
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static string BuildInsertStatement(string qualified, List<string> cols, List<string> vals, bool overriding)
    {
        if (cols.Count == 0) return $"INSERT INTO {qualified} DEFAULT VALUES;";
        var ov = overriding ? " OVERRIDING SYSTEM VALUE" : "";
        return $"INSERT INTO {qualified} ({string.Join(", ", cols)}){ov} VALUES ({string.Join(", ", vals)});";
    }

    /// <summary>A column that must be given a value: NOT NULL with no default, identity, serial, or generated source.</summary>
    private static bool IsMandatory(ColumnDefinition c) =>
        !c.IsNullable && c.Default is null && !c.IsIdentity && !c.IsSerial && c.GeneratedExpression is null;

    private static bool IsIdentityAlways(ColumnDefinition c) =>
        c.IsIdentity && string.Equals(c.IdentityKind, "ALWAYS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Split a canonical type into (base, argSpec, arraySuffix) e.g. "numeric(12, 2)[]" → ("numeric","(12, 2)","[]").</summary>
    private static (string Base, string Arg, string Array) Decompose(string dataType)
    {
        var text = (dataType ?? "").Trim();
        var array = "";
        while (text.EndsWith("[]", StringComparison.Ordinal)) { array += "[]"; text = text[..^2].TrimEnd(); }
        var arg = "";
        var open = text.IndexOf('(');
        if (open >= 0 && text.EndsWith(")", StringComparison.Ordinal))
        {
            arg = text[open..];
            text = text[..open].TrimEnd();
        }
        return (text.ToLowerInvariant(), arg, array);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly ISet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
