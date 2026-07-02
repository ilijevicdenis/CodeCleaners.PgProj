using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// GENERATED ALWAYS AS (expr) STORED vs VIRTUAL (PG18) must stay distinct end-to-end (P0 audit finding,
/// 2026-07-02: both forms collapsed into one AST node and every VIRTUAL column deployed as STORED).
/// </summary>
public class GeneratedVirtualColumnTests
{
    private static ColumnDefinition Column(string ddl, string column)
    {
        var model = TestModel.Build(ddl);
        return model.Tables.Single().Columns.Single(c => c.Name == column);
    }

    [Fact]
    public void Stored_virtual_and_omitted_fold_their_kind_into_the_model()
    {
        var t = TestModel.Build(@"CREATE TABLE public.t (
            a int,
            s int GENERATED ALWAYS AS (a * 2) STORED,
            v int GENERATED ALWAYS AS (a * 3) VIRTUAL,
            d int GENERATED ALWAYS AS (a * 4));").Tables.Single();

        Assert.True(t.Columns.Single(c => c.Name == "s").GeneratedIsStored);
        Assert.False(t.Columns.Single(c => c.Name == "v").GeneratedIsStored);
        // PG18: storage kind omitted defaults to VIRTUAL.
        Assert.False(t.Columns.Single(c => c.Name == "d").GeneratedIsStored);
        Assert.NotNull(t.Columns.Single(c => c.Name == "v").GeneratedExpression);
    }

    [Fact]
    public void Emitter_round_trips_the_storage_kind()
    {
        var stored = Column("CREATE TABLE public.t (a int, s int GENERATED ALWAYS AS (a) STORED);", "s");
        var virt = Column("CREATE TABLE public.t (a int, v int GENERATED ALWAYS AS (a) VIRTUAL);", "v");
        Assert.Contains(" STORED", SqlEmitter.Column(stored));
        Assert.Contains(" VIRTUAL", SqlEmitter.Column(virt));
    }

    [Fact]
    public void Comparer_flags_a_stored_to_virtual_flip_as_a_change()
    {
        var source = TestModel.Build("CREATE TABLE public.t (a int, g int GENERATED ALWAYS AS (a) VIRTUAL);");
        var target = TestModel.Build("CREATE TABLE public.t (a int, g int GENERATED ALWAYS AS (a) STORED);");
        var changes = new SchemaComparer().Compare(source, target);
        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Comparer_sees_same_kind_as_equal()
    {
        var source = TestModel.Build("CREATE TABLE public.t (a int, g int GENERATED ALWAYS AS (a) VIRTUAL);");
        var target = TestModel.Build("CREATE TABLE public.t (a int, g int GENERATED ALWAYS AS (a) VIRTUAL);");
        Assert.Empty(new SchemaComparer().Compare(source, target));
    }
}
