using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Comparison.Risk;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 12 — Deployment Risk Analyzer (#54). DB-free classification cases proving the representative
/// mapping and that the verdict reads the field-level deltas off <see cref="AlterColumnChange"/>.
/// </summary>
public class RiskAnalyzerTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);
    private static readonly SchemaComparer Comparer = new();
    private static readonly RiskAnalyzer Analyzer = RiskAnalyzer.Default;

    private static ChangeRisk RiskOfSingleChange(string sourceSql, string targetSql)
    {
        var changes = Comparer.Compare(Parse(sourceSql), Parse(targetSql));
        var change = changes.Single(c => c is AlterColumnChange or AddColumnChange or DropColumnChange);
        return Analyzer.Classify(change);
    }

    [Fact]
    public void Add_nullable_column_is_safe()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id int, note text);", "CREATE TABLE app.t (id int);");
        Assert.Equal(RiskLevel.Safe, risk.Level);
        Assert.False(risk.RequiresTableRewrite);
    }

    [Fact]
    public void Add_not_null_column_without_default_is_dangerous()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id int, note text NOT NULL);", "CREATE TABLE app.t (id int);");
        Assert.Equal(RiskLevel.Dangerous, risk.Level);
    }

    [Fact]
    public void Widen_integer_to_bigint_is_warning_and_rewrites()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id bigint);", "CREATE TABLE app.t (id int);");
        Assert.Equal(RiskLevel.Warning, risk.Level);
        Assert.True(risk.RequiresTableRewrite);
        Assert.True(risk.RequiresExclusiveLock);
    }

    [Fact]
    public void Narrow_bigint_to_integer_is_data_loss()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id int);", "CREATE TABLE app.t (id bigint);");
        Assert.Equal(RiskLevel.DataLoss, risk.Level);
        Assert.True(risk.RequiresTableRewrite);
    }

    [Fact]
    public void Shrink_varchar_length_is_data_loss()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (c varchar(50));", "CREATE TABLE app.t (c varchar(100));");
        Assert.Equal(RiskLevel.DataLoss, risk.Level);
    }

    [Fact]
    public void Grow_varchar_length_is_warning()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (c varchar(100));", "CREATE TABLE app.t (c varchar(50));");
        Assert.Equal(RiskLevel.Warning, risk.Level);
    }

    [Fact]
    public void Cross_family_type_change_is_dangerous_and_rewrites()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (c integer);", "CREATE TABLE app.t (c text);");
        Assert.Equal(RiskLevel.Dangerous, risk.Level);
        Assert.True(risk.RequiresTableRewrite);
    }

    [Fact]
    public void Set_not_null_is_warning_with_lock()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id int NOT NULL);", "CREATE TABLE app.t (id int);");
        Assert.Equal(RiskLevel.Warning, risk.Level);
        Assert.True(risk.RequiresExclusiveLock);
    }

    [Fact]
    public void Drop_not_null_is_safe()
    {
        var risk = RiskOfSingleChange("CREATE TABLE app.t (id int);", "CREATE TABLE app.t (id int NOT NULL);");
        Assert.Equal(RiskLevel.Safe, risk.Level);
    }

    [Fact]
    public void Drop_column_is_data_loss()
    {
        var changes = Comparer.Compare(
            Parse("CREATE TABLE app.t (id int);"),
            Parse("CREATE TABLE app.t (id int, legacy text);"),
            new ComparerOptions { DropObjectsNotInSource = true });
        var risk = Analyzer.Classify(changes.OfType<DropColumnChange>().Single());
        Assert.Equal(RiskLevel.DataLoss, risk.Level);
    }

    [Fact]
    public void Drop_table_is_data_loss()
    {
        var risk = Analyzer.Classify(new DropTableChange("app", "t"));
        Assert.Equal(RiskLevel.DataLoss, risk.Level);
    }

    [Fact]
    public void Drop_sequence_via_raw_object_is_data_loss()
    {
        // DropRawObjectChange (the generic drop path) is data loss regardless of kind.
        var def = new RawObjectDefinition(ObjectKind.Type, "app", "mood", "type:app.mood", "CREATE TYPE app.mood AS ENUM ('a');");
        Assert.Equal(RiskLevel.DataLoss, Analyzer.Classify(new DropRawObjectChange(def)).Level);
    }

    [Fact]
    public void Create_table_and_schema_are_safe()
    {
        Assert.Equal(RiskLevel.Safe, Analyzer.Classify(new CreateSchemaChange("app")).Level);
        Assert.Equal(RiskLevel.Safe, Analyzer.Classify(
            Comparer.Compare(Parse("CREATE TABLE app.t (id int);"), new DatabaseModel())
                    .OfType<CreateTableChange>().Single()).Level);
    }

    [Fact]
    public void RiskLevel_surfaces_on_selectable_change()
    {
        var set = SchemaChangeSet.Build(
            Parse("CREATE TABLE app.t (id int);"),
            Parse("CREATE TABLE app.t (id bigint);")); // narrowing → DataLoss
        var alter = set.Changes.Single(c => c.Change is AlterColumnChange);
        Assert.Equal(RiskLevel.DataLoss, alter.RiskLevel);
        Assert.NotEqual("", alter.Risk.Rationale);
    }

    [Fact]
    public void MaxIncludedRiskLevel_reflects_the_worst_included_change()
    {
        var set = SchemaChangeSet.Build(
            Parse("CREATE TABLE app.t (id int);"),
            Parse("CREATE TABLE app.t (id bigint);"));
        Assert.Equal(RiskLevel.DataLoss, set.MaxIncludedRiskLevel);
        Assert.Equal(1, set.IncludedDataLossCount);
    }
}
