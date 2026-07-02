using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Turns model objects into Postgres DDL. Every identifier is double-quoted (and internal quotes
/// doubled) so that reserved words, mixed case, and odd characters all deploy safely — the same
/// defensive posture SSDT takes when it scripts a deployment.
/// </summary>
public static class SqlEmitter
{
    public static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);

    public static string Cols(IEnumerable<string> cols) => string.Join(", ", cols.Select(Quote));

    private static string ConstraintPrefix(string? name) =>
        string.IsNullOrEmpty(name) ? string.Empty : $"CONSTRAINT {Quote(name)} ";

    private static string SerialType(string canonical) => canonical switch
    {
        "bigint" => "bigserial",
        "smallint" => "smallserial",
        _ => "serial",
    };

    public static string Column(ColumnDefinition c)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(c.Name)).Append(' ');

        // serial carries its own NOT NULL + owned sequence; emit the pseudo-type and stop.
        if (c.IsSerial)
        {
            sb.Append(SerialType(c.DataType));
            return sb.ToString();
        }

        sb.Append(c.DataType);
        if (!string.IsNullOrWhiteSpace(c.GeneratedExpression))
            sb.Append(" GENERATED ALWAYS AS ").Append(WrapParens(c.GeneratedExpression!))
              .Append(c.GeneratedIsStored ? " STORED" : " VIRTUAL");
        else if (c.IsIdentity)
            sb.Append(" GENERATED ").Append(c.IdentityKind ?? "BY DEFAULT").Append(" AS IDENTITY");
        if (!c.IsNullable) sb.Append(" NOT NULL");
        if (string.IsNullOrWhiteSpace(c.GeneratedExpression) && !string.IsNullOrWhiteSpace(c.Default))
            sb.Append(" DEFAULT ").Append(c.Default);
        return sb.ToString();
    }

    // A generated/CHECK expression needs exactly one surrounding parenthesis group. The project parser
    // stores the inner expression (no outer parens) while the catalog reader wraps it — normalise both.
    private static string WrapParens(string expr)
    {
        var e = expr.Trim();
        return IsSingleBalancedGroup(e) ? e : $"({e})";
    }

    private static bool IsSingleBalancedGroup(string e)
    {
        if (e.Length < 2 || e[0] != '(' || e[^1] != ')') return false;
        int depth = 0;
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i] == '(') depth++;
            else if (e[i] == ')') { depth--; if (depth == 0) return i == e.Length - 1; }
        }
        return false;
    }

    public static string Check(CheckConstraintDefinition c) =>
        $"{ConstraintPrefix(c.Name)}CHECK {WrapParens(c.Expression)}{(c.NoInherit ? " NO INHERIT" : "")}";
        // NOT VALID is an ALTER-only suffix — the change-level ToSql appends it (a CREATE TABLE's
        // brand-new empty table validates trivially, and PG rejects NOT VALID inline anyway).

    /// <summary>The PRIMARY KEY constraint body (after the optional CONSTRAINT name prefix), including
    /// the INCLUDE / DEFERRABLE attributes when set — attributes only render when non-default, so
    /// attribute-free constraints emit byte-identically to before.</summary>
    public static string PrimaryKeyBody(PrimaryKeyDefinition pk) =>
        $"PRIMARY KEY ({Cols(pk.Columns)}){IncludeSuffix(pk.Include)}{DeferrableSuffix(pk.Deferrable, pk.InitiallyDeferred)}";

    /// <summary>The UNIQUE constraint body, including NULLS NOT DISTINCT / INCLUDE / DEFERRABLE when set.</summary>
    public static string UniqueBody(UniqueConstraintDefinition u) =>
        $"UNIQUE{(u.NullsNotDistinct ? " NULLS NOT DISTINCT" : "")} ({Cols(u.Columns)}){IncludeSuffix(u.Include)}{DeferrableSuffix(u.Deferrable, u.InitiallyDeferred)}";

    private static string IncludeSuffix(IReadOnlyList<string>? include) =>
        include is { Count: > 0 } ? $" INCLUDE ({Cols(include)})" : "";

    private static string DeferrableSuffix(bool deferrable, bool initiallyDeferred) =>
        (deferrable ? " DEFERRABLE" : "") + (initiallyDeferred ? " INITIALLY DEFERRED" : "");

    public static string CreateTable(TableDefinition t)
    {
        var lines = new List<string>();
        lines.AddRange(t.Columns.Select(c => "    " + Column(c)));

        if (t.PrimaryKey is { Columns.Count: > 0 } pk)
            lines.Add($"    {ConstraintPrefix(pk.Name)}{PrimaryKeyBody(pk)}");

        foreach (var u in t.Unique)
            lines.Add($"    {ConstraintPrefix(u.Name)}{UniqueBody(u)}");

        foreach (var c in t.Checks)
            lines.Add($"    {Check(c)}");

        foreach (var other in t.OtherConstraints)
            lines.Add($"    {other}");

        var trailing = string.IsNullOrWhiteSpace(t.TrailingOptions) ? "" : " " + t.TrailingOptions.Trim();
        return $"CREATE TABLE {Qualified(t.Schema, t.Name)} (\n{string.Join(",\n", lines)}\n){trailing};";
    }

    public static string ForeignKey(string schema, string table, ForeignKeyDefinition fk)
    {
        var sb = new StringBuilder();
        sb.Append($"ALTER TABLE {Qualified(schema, table)} ADD ");
        if (!string.IsNullOrEmpty(fk.Name)) sb.Append($"CONSTRAINT {Quote(fk.Name)} ");
        sb.Append($"FOREIGN KEY ({Cols(fk.Columns)}) REFERENCES {Qualified(fk.ReferencedSchema, fk.ReferencedTable)}");
        if (fk.ReferencedColumns.Count > 0) sb.Append($" ({Cols(fk.ReferencedColumns)})");
        if (!string.IsNullOrEmpty(fk.Match)) sb.Append($" MATCH {fk.Match}");
        if (!string.IsNullOrEmpty(fk.OnDelete)) sb.Append($" ON DELETE {fk.OnDelete}");
        if (!string.IsNullOrEmpty(fk.OnUpdate)) sb.Append($" ON UPDATE {fk.OnUpdate}");
        sb.Append(DeferrableSuffix(fk.Deferrable, fk.InitiallyDeferred));
        if (fk.NotValid) sb.Append(" NOT VALID");
        sb.Append(';');
        return sb.ToString();
    }

    public static string CreateIndex(IndexDefinition ix, bool concurrent = false)
    {
        var sb = new StringBuilder("CREATE ");
        if (ix.IsUnique) sb.Append("UNIQUE ");
        sb.Append("INDEX ");
        if (concurrent) sb.Append("CONCURRENTLY ");
        sb.Append($"{Quote(ix.Name)} ON {Qualified(ix.Schema, ix.Table)}");
        if (!string.IsNullOrEmpty(ix.Method)) sb.Append($" USING {ix.Method}");
        sb.Append($" ({string.Join(", ", ix.Columns)})");
        if (!string.IsNullOrEmpty(ix.WhereClause)) sb.Append($" WHERE {ix.WhereClause}");
        sb.Append(';');
        return sb.ToString();
    }

    public static string CreateOrReplaceView(ViewDefinition v)
    {
        var body = v.Body.TrimEnd().TrimEnd(';');
        // Materialized views do not support OR REPLACE, so guard with IF NOT EXISTS instead.
        return v.IsMaterialized
            ? $"CREATE MATERIALIZED VIEW IF NOT EXISTS {Qualified(v.Schema, v.Name)} AS {body};"
            : $"CREATE OR REPLACE VIEW {Qualified(v.Schema, v.Name)} AS {body};";
    }

    public static string SequenceOptions(SequenceDefinition s)
    {
        var sb = new StringBuilder();
        if (s.DataType is not null) sb.Append(" AS ").Append(s.DataType);
        if (s.Increment is not null) sb.Append(" INCREMENT BY ").Append(s.Increment);
        if (s.MinValue is not null) sb.Append(" MINVALUE ").Append(s.MinValue);
        if (s.MaxValue is not null) sb.Append(" MAXVALUE ").Append(s.MaxValue);
        if (s.Start is not null) sb.Append(" START WITH ").Append(s.Start);
        if (s.Cache is not null) sb.Append(" CACHE ").Append(s.Cache);
        if (s.Cycle) sb.Append(" CYCLE");
        return sb.ToString();
    }

    public static string CreateSequence(SequenceDefinition s) =>
        $"CREATE SEQUENCE IF NOT EXISTS {Qualified(s.Schema, s.Name)}{SequenceOptions(s)};";

    public static string Function(FunctionDefinition f)
    {
        var body = f.Body.TrimEnd();
        return body.EndsWith(";") ? body : body + ";";
    }
}
