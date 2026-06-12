using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Project;
using PgProj.Core.Publishing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-DEPLOYREPORT (#141): the planned-change report against a real PostgreSQL — counts, per-change
/// risk, the data-loss gate flag, deploy-script presence, apply strategy — and the hard guarantee
/// that producing the report NEVER modifies the target. Skipped (no-op) when PGPROJ_TEST_CONNECTION
/// is unset, like every DB-backed class in this suite.
/// </summary>
public sealed class DeployReportIntegrationTests : IClassFixture<ThrowawayDatabaseFixture>, IDisposable
{
    private readonly ThrowawayDatabaseFixture _fixture;
    private readonly string _dir;

    public DeployReportIntegrationTests(ThrowawayDatabaseFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "pgproj-deployreport-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ScaffoldProject(string widgetsSql)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "app", "Tables"));
        var proj = Path.Combine(_dir, "Db.pgproj");
        File.WriteAllText(proj, """
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <Name>ReportDb</Name>
                <DefaultSchema>public</DefaultSchema>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "app", "Tables", "widgets.sql"), widgetsSql);
        return proj;
    }

    private async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountRowsAsync(string conn, string table)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Report_carries_risked_operations_and_never_touches_the_target()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB — treated as a skip

        // Target: a table with a column the project no longer has (drop = data loss) and data in it.
        await ExecAsync(conn,
            "CREATE SCHEMA app; " +
            "CREATE TABLE app.widgets (id integer NOT NULL, label text, obsolete_col text); " +
            "INSERT INTO app.widgets VALUES (1, 'a', 'x'), (2, 'b', 'y');");

        // Source: drops obsolete_col, adds a new nullable column (safe).
        var projPath = ScaffoldProject(
            "CREATE TABLE app.widgets (id integer NOT NULL, label text, fresh_col integer);\n");
        var project = DatabaseProject.Load(projPath);
        var source = (await project.BuildAsync()).Model;

        var plan = await new PublishService().PlanAsync(project, source, conn, new PublishPlanOptions
        {
            AllowDrops = true,
        });
        var report = DeployReportBuilder.Build(plan,
            source: new SchemaCompareEndpointDto { Kind = "project", DisplayName = "ReportDb" },
            target: new SchemaCompareEndpointDto { Kind = "liveDatabase", DisplayName = "(database)" });

        Assert.False(report.InSync);
        Assert.Equal(plan.ChangeCount, report.ChangeCount);
        Assert.Equal(report.Operations.Count, report.ChangeCount);
        Assert.True(report.DestructiveCount >= 1);

        // the data-loss gate: dropping a populated column must classify as DataLoss and flip the flag
        var drop = Assert.Single(report.Operations, o => o.Kind == "DropColumnChange");
        Assert.Equal("DataLoss", drop.RiskLevel);
        Assert.True(drop.Destructive);
        Assert.NotEmpty(drop.RiskRationale);
        Assert.True(report.BlocksOnDataLoss);

        // the safe add rides alongside, position-ordered
        var add = Assert.Single(report.Operations, o => o.Kind == "AddColumnChange");
        Assert.Equal("Safe", add.RiskLevel);
        Assert.Equal(report.Operations.OrderBy(o => o.Position).Select(o => o.Position),
            Enumerable.Range(1, report.Operations.Count));

        // no deploy scripts in this project; whole-script strategy by default, phased when asked
        Assert.False(report.HasPreDeployScript);
        Assert.False(report.HasPostDeployScript);
        Assert.Equal("wholeScript", report.ApplyStrategy);
        var phased = DeployReportBuilder.Build(plan,
            report.Source, report.Target, parallelRequested: true);
        Assert.Equal("phased", phased.ApplyStrategy);

        // JSON and XML serialize with the agreed conventions
        var json = DeployReportBuilder.Serialize(report);
        Assert.Contains("\"blocksOnDataLoss\": true", json);
        Assert.Contains("\"riskLevel\": \"DataLoss\"", json);
        var xml = DeployReportBuilder.SerializeXml(report);
        Assert.Contains("<blocksOnDataLoss>true</blocksOnDataLoss>", xml);
        Assert.Contains("riskLevel=\"DataLoss\"", xml);

        // and the iron guarantee: the target was only ever READ
        Assert.Equal(2, await CountRowsAsync(conn, "app.widgets"));
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var probe = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema='app' AND table_name='widgets'", c);
        Assert.Equal(3L, (long)(await probe.ExecuteScalarAsync())!); // obsolete_col still there
    }
}
