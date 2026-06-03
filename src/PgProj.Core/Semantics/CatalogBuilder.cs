using System.Linq;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>Builds a <see cref="Catalog"/> from PgParser output — no legacy model involved.</summary>
public static class CatalogBuilder
{
    public static Catalog Build(ParseResult result, string defaultSchema = "public")
    {
        var c = new Catalog { DefaultSchema = defaultSchema };
        foreach (var stmt in result.Statements) Absorb(c, stmt);
        return c;
    }

    public static Catalog Build(string sql, string defaultSchema = "public")
        => Build(new PgParser().Parse(sql), defaultSchema);

    private static void Absorb(Catalog c, SqlStatement stmt)
    {
        switch (stmt)
        {
            case CreateTableStatement t:
                c.AddRelation(t.Schema, t.Name, t.Columns.Select(col => col.Name));
                break;
            case CreateTableAsStatement ctas:
                c.AddRelation(ctas.Schema, ctas.Name);
                break;
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
