using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 18 — block-on-possible-data-loss gate (#58, wired to the #54 risk levels). The gate refuses to
/// generate a script when a DataLoss change is present and the option is set; off (default) is unaffected.
/// </summary>
public class DataLossGateTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);

    // A narrowing type change classifies as DataLoss.
    private static System.Collections.Generic.IReadOnlyList<SchemaChange> NarrowingDiff() =>
        new SchemaComparer().Compare(
            Parse("CREATE TABLE app.t (id int);"),
            Parse("CREATE TABLE app.t (id bigint);"));

    [Fact]
    public void Default_options_generate_a_script_even_with_data_loss()
    {
        var script = new DeployScriptGenerator().Generate(NarrowingDiff());
        Assert.Contains("ALTER", script);
    }

    [Fact]
    public void Block_on_data_loss_refuses_when_a_data_loss_change_is_present()
    {
        var ex = Assert.Throws<DataLossBlockedException>(() =>
            new DeployScriptGenerator().Generate(NarrowingDiff(),
                new DeployOptions { BlockOnPossibleDataLoss = true }));
        Assert.NotEmpty(ex.Offending);
    }

    [Fact]
    public void Block_on_data_loss_allows_a_safe_only_plan()
    {
        // Add-nullable-column is Safe → the gate must NOT trip.
        var diff = new SchemaComparer().Compare(
            Parse("CREATE TABLE app.t (id int, note text);"),
            Parse("CREATE TABLE app.t (id int);"));
        var script = new DeployScriptGenerator().Generate(diff,
            new DeployOptions { BlockOnPossibleDataLoss = true });
        Assert.Contains("ADD COLUMN", script);
    }

    [Fact]
    public void Guard_helper_is_a_no_op_when_option_is_off()
    {
        // Should not throw regardless of content when the gate is disabled.
        DeployScriptGenerator.GuardAgainstDataLoss(NarrowingDiff(), new DeployOptions());
    }
}
