using System.Linq;
using PgProj.Core.Comparison;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// A changed MATERIALIZED view body must deploy as drop + recreate (P0 audit finding, 2026-07-02):
/// Postgres has no CREATE OR REPLACE MATERIALIZED VIEW, and the emitter's CREATE … IF NOT EXISTS
/// fallback is a silent no-op on an existing view — the body change was never applied.
/// </summary>
public class MaterializedViewRecreateTests
{
    [Fact]
    public void Changed_matview_body_produces_a_recreate_not_a_silent_create_if_not_exists()
    {
        var source = TestModel.Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 2 AS x;");
        var target = TestModel.Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS x;");
        var change = Assert.Single(new SchemaComparer().Compare(source, target));
        var recreate = Assert.IsType<RecreateMaterializedViewChange>(change);
        Assert.False(recreate.IsDestructive);   // derived data — must not hide behind --allow-drops

        var sql = recreate.ToSql();
        Assert.Contains("DROP MATERIALIZED VIEW IF EXISTS \"public\".\"mv\";", sql);
        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS \"public\".\"mv\" AS SELECT 2 AS x;", sql);
    }

    [Fact]
    public void New_matview_still_creates_without_a_drop()
    {
        var source = TestModel.Build("CREATE MATERIALIZED VIEW public.mv AS SELECT 1 AS x;");
        var change = Assert.Single(new SchemaComparer().Compare(source, TestModel.Build("")));
        Assert.IsType<CreateOrReplaceViewChange>(change);
        Assert.DoesNotContain("DROP", change.ToSql());
    }

    [Fact]
    public void Changed_plain_view_still_uses_create_or_replace()
    {
        var source = TestModel.Build("CREATE VIEW public.v AS SELECT 2 AS x;");
        var target = TestModel.Build("CREATE VIEW public.v AS SELECT 1 AS x;");
        var change = Assert.Single(new SchemaComparer().Compare(source, target));
        Assert.IsType<CreateOrReplaceViewChange>(change);
        Assert.Contains("CREATE OR REPLACE VIEW", change.ToSql());
    }
}
