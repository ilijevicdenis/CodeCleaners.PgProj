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

    public static string Column(ColumnDefinition c)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(c.Name)).Append(' ').Append(c.DataType);
        if (!string.IsNullOrWhiteSpace(c.GeneratedExpression))
            sb.Append(" GENERATED ALWAYS AS ").Append(c.GeneratedExpression).Append(" STORED");
        else if (c.IsIdentity)
            sb.Append(" GENERATED ").Append(c.IdentityKind ?? "BY DEFAULT").Append(" AS IDENTITY");
        if (!c.IsNullable) sb.Append(" NOT NULL");
        if (string.IsNullOrWhiteSpace(c.GeneratedExpression) && !string.IsNullOrWhiteSpace(c.Default))
            sb.Append(" DEFAULT ").Append(c.Default);
        return sb.ToString();
    }

    public static string Check(CheckConstraintDefinition c) =>
        $"{ConstraintPrefix(c.Name)}CHECK {c.Expression}";

    public static string CreateTable(TableDefinition t)
    {
        var lines = new List<string>();
        lines.AddRange(t.Columns.Select(c => "    " + Column(c)));

        if (t.PrimaryKey is { Columns.Count: > 0 } pk)
            lines.Add($"    {ConstraintPrefix(pk.Name)}PRIMARY KEY ({Cols(pk.Columns)})");

        foreach (var u in t.Unique)
            lines.Add($"    {ConstraintPrefix(u.Name)}UNIQUE ({Cols(u.Columns)})");

        foreach (var c in t.Checks)
            lines.Add($"    {Check(c)}");

        foreach (var other in t.OtherConstraints)
            lines.Add($"    {other}");

        return $"CREATE TABLE {Qualified(t.Schema, t.Name)} (\n{string.Join(",\n", lines)}\n);";
    }

    public static string ForeignKey(string schema, string table, ForeignKeyDefinition fk)
    {
        var sb = new StringBuilder();
        sb.Append($"ALTER TABLE {Qualified(schema, table)} ADD ");
        if (!string.IsNullOrEmpty(fk.Name)) sb.Append($"CONSTRAINT {Quote(fk.Name)} ");
        sb.Append($"FOREIGN KEY ({Cols(fk.Columns)}) REFERENCES {Qualified(fk.ReferencedSchema, fk.ReferencedTable)}");
        if (fk.ReferencedColumns.Count > 0) sb.Append($" ({Cols(fk.ReferencedColumns)})");
        if (!string.IsNullOrEmpty(fk.OnDelete)) sb.Append($" ON DELETE {fk.OnDelete}");
        if (!string.IsNullOrEmpty(fk.OnUpdate)) sb.Append($" ON UPDATE {fk.OnUpdate}");
        sb.Append(';');
        return sb.ToString();
    }

    public static string CreateIndex(IndexDefinition ix)
    {
        var sb = new StringBuilder("CREATE ");
        if (ix.IsUnique) sb.Append("UNIQUE ");
        sb.Append($"INDEX {Quote(ix.Name)} ON {Qualified(ix.Schema, ix.Table)}");
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
