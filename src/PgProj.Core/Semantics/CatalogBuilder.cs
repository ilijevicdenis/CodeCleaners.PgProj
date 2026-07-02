using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>Builds a <see cref="Catalog"/> from PgParser output — no legacy model involved.</summary>
public static class CatalogBuilder
{
    private static readonly ObjectIdentityComputer Identity = new();

    public static Catalog Build(ParseResult result, string defaultSchema = "public")
    {
        var c = new Catalog { DefaultSchema = defaultSchema };
        foreach (var stmt in result.Statements) AbsorbCore(c, stmt);
        return c;
    }

    public static Catalog Build(string sql, string defaultSchema = "public")
        => Build(new PgParser().Parse(sql), defaultSchema);

    /// <summary>
    /// Absorbs an already-built <see cref="DatabaseModel"/> into <paramref name="catalog"/> as EXTERNAL
    /// objects (EP-REF). This is how a referenced project/artifact becomes visible to validation: its
    /// schemas resolve and its relations/types/functions answer existence checks, but because the model is
    /// never added to the build's own <see cref="DatabaseModel"/>, the comparer never emits them.
    /// </summary>
    public static void AbsorbExternalModel(Catalog catalog, DatabaseModel model)
    {
        foreach (var s in model.Schemas) catalog.AddExternalSchema(s.Name);

        foreach (var t in model.Tables)
        {
            catalog.AddExternalSchema(t.Schema);
            catalog.AddRelation(t.Schema, t.Name,
                t.Columns.Select(col => new Catalog.ColumnInfo(col.Name, col.DataType)),
                Identity.StableIdOf(t), external: true);
        }
        foreach (var v in model.Views) { catalog.AddExternalSchema(v.Schema); catalog.AddRelation(v.Schema, v.Name, columns: null, Identity.StableIdOf(v), external: true); }
        foreach (var q in model.Sequences) { catalog.AddExternalSchema(q.Schema); catalog.AddRelation(q.Schema, q.Name, columns: null, Identity.StableIdOf(q), external: true); }
        foreach (var f in model.Functions)
        {
            if (!string.IsNullOrEmpty(f.Schema)) catalog.AddExternalSchema(f.Schema);
            catalog.AddFunction(string.IsNullOrEmpty(f.Schema) ? null : f.Schema, f.Name,
                new FunctionSignature(NormalizeArgTypes(f.ArgTypes)), Identity.StableIdOf(f), external: true);
        }
        foreach (var o in model.Objects)
        {
            if (!string.IsNullOrEmpty(o.Schema)) catalog.AddExternalSchema(o.Schema);
            switch (o.Kind)
            {
                case ObjectKind.Type or ObjectKind.Domain:
                    catalog.AddType(string.IsNullOrEmpty(o.Schema) ? null : o.Schema, o.Name); break;
                case ObjectKind.ForeignTable:
                    catalog.AddRelation(o.Schema, o.Name); break;
                case ObjectKind.Aggregate:
                    catalog.AddFunction(o.Name); break;
            }
        }
    }

    /// <summary>Absorbs one statement's defined object into <paramref name="c"/> (the unit of <see cref="Build"/>).</summary>
    public static void Absorb(Catalog c, SqlStatement stmt) => AbsorbCore(c, stmt);

    private static void AbsorbCore(Catalog c, SqlStatement stmt)
    {
        switch (stmt)
        {
            // partition/typed/inheriting tables have columns we cannot fully enumerate here → leave columns unknown
            case CreateTableStatement { IsPartitionOrTyped: true } t: c.AddRelation(t.Schema, t.Name); break;
            case CreateTableStatement t when t.TrailingText is { } tr && tr.Contains("inherits", System.StringComparison.OrdinalIgnoreCase):
                c.AddRelation(t.Schema, t.Name); break;
            case CreateTableStatement t:
                c.AddRelation(t.Schema, t.Name,
                    t.Columns.Select(col => new Catalog.ColumnInfo(col.Name, TypeNormalizer.Normalize(col.Type.Text))),
                    TableStableId(c, t));
                break;
            case CreateTableAsStatement ctas: c.AddRelation(ctas.Schema, ctas.Name); break;
            case CreateViewStatement v: c.AddRelation(v.Schema, v.Name); break;
            case CreateSequenceStatement sq: c.AddRelation(sq.Schema, sq.Name); break;
            case CreateFunctionStatement f:
                c.AddFunction(f.Schema, f.Name, new FunctionSignature(NormalizeArgTypes(f.ArgTypes)),
                    Identity.StableIdOf(new FunctionDefinition(f.Schema ?? c.DefaultSchema, f.Name,
                        $"{f.Schema ?? c.DefaultSchema}.{f.Name}({f.ArgTypes})", f.Body ?? "", f.ArgTypes)),
                    returnType: f.ReturnType is null ? null : TypeNormalizer.Normalize(f.ReturnType));
                break;
            case CreateSchemaStatement s when s.Name is not null:
                c.AddSchema(s.Name);
                break;
            // Standalone ALTER TABLE column changes fold into the catalog (audit P1) so binding sees the
            // post-ALTER shape — a view over an ALTER-added column no longer false-positives, and the
            // analyzers can keep validation ON for files whose ALTERs are all folded/binding-neutral
            // (AlterStatement.InvalidatesBinding is the remaining conservatism gate).
            case AlterStatement a when a.ObjectKind == "TABLE"
                                       && (a.AddedColumns.Count > 0 || a.DroppedColumns.Count > 0 || a.ColumnActions.Count > 0):
                c.AmendRelation(a.Schema, a.Name,
                    a.AddedColumns.Select(col => new Catalog.ColumnInfo(col.Name, TypeNormalizer.Normalize(col.Type.Text))),
                    a.DroppedColumns,
                    a.ColumnActions.Where(x => x.Kind == "TYPE" && x.Value is not null)
                                   .Select(x => (x.Column, TypeNormalizer.Normalize(x.Value!))));
                break;
            case RawCreateStatement r when r.Name is not null:
                switch (r.ObjectKind)
                {
                    case "VIEW" or "MATERIALIZED VIEW" or "SEQUENCE" or "FOREIGN TABLE":
                        c.AddRelation(r.Schema, r.Name); break;
                    case "TYPE" or "DOMAIN":
                        c.AddType(r.Schema, r.Name); break;
                    case "FUNCTION" or "PROCEDURE" or "AGGREGATE":
                        c.AddFunction(r.Name); break;
                }
                break;
        }
    }

    // Build a lightweight TableDefinition (columns + types + nullability) so the Identity Model can stamp a
    // name-independent StableId on the relation symbol. Only the structural skeleton matters for the StableId,
    // so we map the columns we can see; constraints we don't lower here simply make the id coarser, never wrong.
    private static StableId TableStableId(Catalog c, CreateTableStatement t)
    {
        var table = new TableDefinition { Schema = t.Schema ?? c.DefaultSchema, Name = t.Name };
        foreach (var col in t.Columns)
        {
            bool nullable = !col.Constraints.OfType<NotNullConstraint>().Any();
            table.Columns.Add(new ColumnDefinition(col.Name, TypeNormalizer.Normalize(col.Type.Text), nullable));
        }
        return Identity.StableIdOf(table);
    }

    // Canonicalize an argument-type list the SAME way the Identity Model does, so the catalog's overload key
    // matches a StableId-bearing function and "f(INT)" / "f(integer)" key identically.
    private static string NormalizeArgTypes(string argTypes)
    {
        if (string.IsNullOrWhiteSpace(argTypes)) return "";
        return string.Join(",", argTypes.Split(',').Select(a => TypeNormalizer.Normalize(a.Trim())));
    }
}
