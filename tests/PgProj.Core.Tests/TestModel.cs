using PgProj.Core.Model;
using PgProj.Core.Syntax;

namespace PgProj.Core.Tests;

/// <summary>Builds a <see cref="DatabaseModel"/> from SQL via the (sole) PgParser + ModelBuilder.</summary>
public static class TestModel
{
    public static DatabaseModel Build(string sql, string defaultSchema = "public")
        => new ModelBuilder(defaultSchema).Build(new PgParser().Parse(sql));
}
