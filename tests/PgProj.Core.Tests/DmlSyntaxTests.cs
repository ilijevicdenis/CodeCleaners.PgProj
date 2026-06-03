using System.Linq;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>Focused unit tests for INSERT / UPDATE / DELETE / MERGE / TRUNCATE in PgParser.</summary>
public class DmlSyntaxTests
{
    private static T One<T>(string sql) where T : SqlStatement
    {
        var res = new PgParser().Parse(sql);
        Assert.True(res.FullyRecognized);
        Assert.Empty(res.Diagnostics);
        return Assert.IsType<T>(res.Statements.Single());
    }

    private static ParseResult Parse(string sql) => new PgParser().Parse(sql);

    [Fact]
    public void Insert_values_on_conflict_returning()
    {
        var ins = One<InsertStatement>("INSERT INTO s.t (a, b) VALUES (1, 'x'), (2, DEFAULT) ON CONFLICT (a) DO UPDATE SET b = excluded.b WHERE t.a > 0 RETURNING id, b AS nb");
        Assert.Equal(new[] { "a", "b" }, ins.Columns);
        Assert.NotNull(ins.Source);
        Assert.True(ins.Source!.IsValues);
        Assert.NotNull(ins.OnConflict);
        Assert.False(ins.OnConflict!.DoNothing);
        Assert.Equal(2, ins.Returning.Count);
        Assert.Equal("nb", ins.Returning[1].Alias);
    }

    [Fact]
    public void Insert_select_and_default_values_and_do_nothing()
    {
        Assert.NotNull(One<InsertStatement>("INSERT INTO s.t SELECT * FROM s.u").Source);
        Assert.True(One<InsertStatement>("INSERT INTO s.t DEFAULT VALUES").DefaultValues);
        Assert.True(One<InsertStatement>("INSERT INTO s.t (a) VALUES (1) ON CONFLICT DO NOTHING").OnConflict!.DoNothing);
    }

    [Fact]
    public void Update_set_forms_from_and_returning()
    {
        var up = One<UpdateStatement>("UPDATE s.t t SET a = 1, (b, c) = (2, 3), d = DEFAULT FROM s.u WHERE t.id = u.id RETURNING *");
        Assert.Equal(3, up.Set.Count);
        Assert.True(up.Set[1].Multi);
        Assert.True(up.Set[2].Default);
        Assert.NotNull(up.From);
        Assert.True(up.ReturningStar);
    }

    [Fact]
    public void Update_multicolumn_subselect_and_where_current_of()
    {
        var up = One<UpdateStatement>("UPDATE s.t SET (a, b) = (SELECT x, y FROM s.u LIMIT 1) WHERE CURRENT OF mycur");
        Assert.NotNull(up.Set.Single().SubSelect);
        Assert.Equal("mycur", up.WhereCurrentOf);
    }

    [Fact]
    public void Delete_using_where_returning()
    {
        var del = One<DeleteStatement>("DELETE FROM s.t t USING s.u WHERE t.id = u.id RETURNING t.id");
        Assert.NotNull(del.Using);
        Assert.NotNull(del.Where);
        Assert.Single(del.Returning);
    }

    [Fact]
    public void Merge_all_actions()
    {
        var m = One<MergeStatement>(
            "MERGE INTO s.t t USING s.u u ON t.id = u.id " +
            "WHEN MATCHED AND u.v > 0 THEN UPDATE SET v = u.v " +
            "WHEN MATCHED THEN DELETE " +
            "WHEN NOT MATCHED THEN INSERT (id, v) VALUES (u.id, u.v) " +
            "WHEN NOT MATCHED BY SOURCE THEN DO NOTHING");
        Assert.Equal(4, m.Whens.Count);
        Assert.Equal("UPDATE", m.Whens[0].Action);
        Assert.Equal("DELETE", m.Whens[1].Action);
        Assert.Equal("INSERT", m.Whens[2].Action);
        Assert.Equal("SOURCE", m.Whens[3].By);
    }

    [Fact]
    public void Truncate_and_cte_insert()
    {
        var tr = One<TruncateStatement>("TRUNCATE TABLE s.t, s.u RESTART IDENTITY CASCADE");
        Assert.Equal(2, tr.Tables.Count);
        Assert.Equal("RESTART IDENTITY", tr.IdentityOption);

        var ins = One<InsertStatement>("WITH x AS (SELECT 1 AS n) INSERT INTO s.t (a) SELECT n FROM x");
        Assert.Single(ins.With);
    }

    [Theory]
    [InlineData("INSERT INTO s.t")]                                  // no VALUES/SELECT/DEFAULT VALUES
    [InlineData("UPDATE s.t WHERE a = 1")]                           // missing SET
    [InlineData("DELETE s.t")]                                       // missing FROM
    [InlineData("MERGE INTO s.t USING s.u ON true")]                 // no WHEN
    [InlineData("INSERT INTO s.t (a) VALUES (1) ON CONFLICT DO")]    // DO without action
    public void Rejects_malformed(string sql)
    {
        var res = Parse(sql);
        Assert.True(res.FullyRecognized);
        Assert.NotEmpty(res.Diagnostics);
    }
}
