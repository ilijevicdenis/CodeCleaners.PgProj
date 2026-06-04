using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>Builds a <see cref="Catalog"/> from PgParser output — no legacy model involved.</summary>
public static class CatalogBuilder
{
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
            catalog.AddRelation(t.Schema, t.Name, t.Columns.Select(col => col.Name));
        }
        foreach (var v in model.Views) { catalog.AddExternalSchema(v.Schema); catalog.AddRelation(v.Schema, v.Name); }
        foreach (var q in model.Sequences) { catalog.AddExternalSchema(q.Schema); catalog.AddRelation(q.Schema, q.Name); }
        foreach (var f in model.Functions)
        {
            if (!string.IsNullOrEmpty(f.Schema)) catalog.AddExternalSchema(f.Schema);
            catalog.AddFunction(f.Name);
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
                c.AddRelation(t.Schema, t.Name, t.Columns.Select(col => col.Name));
                break;
            case CreateTableAsStatement ctas: c.AddRelation(ctas.Schema, ctas.Name); break;
            case CreateViewStatement v: c.AddRelation(v.Schema, v.Name); break;
            case CreateSequenceStatement sq: c.AddRelation(sq.Schema, sq.Name); break;
            case CreateFunctionStatement f: c.AddFunction(f.Name); break;
            case CreateSchemaStatement s when s.Name is not null:
                c.AddSchema(s.Name);
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
}
