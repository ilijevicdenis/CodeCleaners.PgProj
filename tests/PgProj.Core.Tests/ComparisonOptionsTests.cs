using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 18 — comparison-equivalence options (#58). Each option must change a comparison only when set to
/// its non-default value; the defaults reproduce today's behaviour exactly.
/// </summary>
public class ComparisonOptionsTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);
    private static readonly SchemaComparer Comparer = new();

    // ---- ignore column order -------------------------------------------------------------------

    [Fact]
    public void Reordered_table_reports_a_column_order_change_by_default()
    {
        var source = Parse("CREATE TABLE app.t (a int, b int);");
        var target = Parse("CREATE TABLE app.t (b int, a int);");

        var changes = Comparer.Compare(source, target); // default: order significant
        Assert.Single(changes.OfType<ColumnOrderChange>());
        Assert.Empty(changes.OfType<AddColumnChange>());
        Assert.Empty(changes.OfType<DropColumnChange>());
    }

    [Fact]
    public void Ignore_column_order_makes_a_reordered_table_compare_equal()
    {
        var source = Parse("CREATE TABLE app.t (a int, b int);");
        var target = Parse("CREATE TABLE app.t (b int, a int);");

        var changes = Comparer.Compare(source, target, new ComparerOptions { IgnoreColumnOrder = true });
        Assert.Empty(changes);
    }

    [Fact]
    public void Same_order_table_never_reports_a_column_order_change()
    {
        var sql = "CREATE TABLE app.t (a int, b int);";
        Assert.Empty(Comparer.Compare(Parse(sql), Parse(sql)).OfType<ColumnOrderChange>());
    }

    // ---- ignore storage parameters -------------------------------------------------------------

    [Fact]
    public void Storage_param_difference_is_ignored_by_default()
    {
        var source = Parse("CREATE TABLE app.t (id int) WITH (fillfactor=70);");
        var target = Parse("CREATE TABLE app.t (id int) WITH (fillfactor=90);");

        Assert.Empty(Comparer.Compare(source, target).OfType<AlterTableStorageChange>());
        Assert.Empty(Comparer.Compare(source, target)); // default fully equal
    }

    [Fact]
    public void Storage_param_difference_is_reported_when_comparison_enabled()
    {
        var source = Parse("CREATE TABLE app.t (id int) WITH (fillfactor=70);");
        var target = Parse("CREATE TABLE app.t (id int) WITH (fillfactor=90);");

        var changes = Comparer.Compare(source, target, new ComparerOptions { IgnoreStorageParameters = false });
        Assert.Single(changes.OfType<AlterTableStorageChange>());
    }

    [Fact]
    public void Equal_storage_params_never_report_even_when_enabled()
    {
        var sql = "CREATE TABLE app.t (id int) WITH (fillfactor=70);";
        var changes = Comparer.Compare(Parse(sql), Parse(sql), new ComparerOptions { IgnoreStorageParameters = false });
        Assert.Empty(changes.OfType<AlterTableStorageChange>());
    }

    // ---- case-sensitive identifiers ------------------------------------------------------------

    [Fact]
    public void Column_case_difference_is_ignored_by_default()
    {
        var source = Parse("CREATE TABLE app.t (\"Id\" int);");
        var target = Parse("CREATE TABLE app.t (id int);");

        // Default: case-insensitive — "Id" matches "id", no add/drop.
        var changes = Comparer.Compare(source, target);
        Assert.Empty(changes.OfType<AddColumnChange>());
        Assert.Empty(changes.OfType<DropColumnChange>());
    }

    [Fact]
    public void Column_case_difference_is_a_diff_when_identifiers_case_sensitive()
    {
        var source = Parse("CREATE TABLE app.t (\"Id\" int);");
        var target = Parse("CREATE TABLE app.t (id int);");

        var changes = Comparer.Compare(source, target,
            new ComparerOptions { CaseSensitiveIdentifiers = true, DropObjectsNotInSource = true });
        // "Id" is now a new column; "id" is now unmatched → dropped.
        Assert.Single(changes.OfType<AddColumnChange>());
        Assert.Single(changes.OfType<DropColumnChange>());
    }

    // ---- read-only / always-ignored equivalences (honest surface) ------------------------------

    [Fact]
    public void Ownership_and_permissions_are_always_ignored_today()
    {
        var o = new ComparerOptions();
        Assert.True(o.IgnoreOwnershipAndRoles);
        Assert.True(o.IgnorePermissions);
        Assert.False(o.IgnoreComments); // comments ARE compared today
    }

    [Fact]
    public void Defaults_are_behaviour_preserving_for_a_plain_table_diff()
    {
        // The original ComparerTests expectations must still hold under the new option surface.
        var source = Parse("CREATE TABLE app.t (id int, name text);");
        var target = Parse("CREATE TABLE app.t (id int);");
        var add = Assert.Single(Comparer.Compare(source, target).OfType<AddColumnChange>());
        Assert.Equal("name", add.Column.Name);
    }
}
