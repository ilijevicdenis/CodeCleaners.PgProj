using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Contracts;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Integration conformance for the EP-RPC JSON contract against a real Postgres (issue #17): the
/// <c>compare</c> and <c>publish --dry-run</c> JSON payloads must be schema-valid and their plan must
/// match what the human/text path computes. Follows the repo's established harness convention — gated on
/// <c>PGPROJ_TEST_CONNECTION</c> (a throwaway DB); a silent no-op skip when it is unset, so the suite
/// stays green on machines without Docker/Postgres. (NOTE: not run locally here — no container available;
/// these are written to compile and to run in CI where the connection is provided.)
/// </summary>
public sealed class JsonContractIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    [Fact]
    public async Task Compare_json_matches_text_plan_against_live_db()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return; // no live DB — treated as a skip

        var project = DatabaseProject.Load(JsonContractTests.FindSampleProject());
        var source = (await project.BuildAsync()).Model;

        // Deploy greenfield so the live target is the project, then compare → must report in-sync.
        var deployer = new DatabaseDeployer();
        var create = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(create, new DeployOptions { WrapInTransaction = true });
        await deployer.ExecuteAsync(conn, "DROP PUBLICATION IF EXISTS customer_pub; DROP SCHEMA IF EXISTS afd CASCADE; DROP SCHEMA IF EXISTS reporting CASCADE; DROP FOREIGN DATA WRAPPER IF EXISTS dummy_fdw CASCADE;");
        await deployer.ExecuteAsync(conn, script);

        var live = await new LiveDatabaseReader().ReadAsync(conn);

        // Text plan (the existing path) and JSON plan must agree on count + destructiveness.
        var textChanges = new SchemaComparer().Compare(source, live);
        var json = ContractBuilder.Compare(source, live, project.Name, allowDrops: false);

        Assert.Equal(textChanges.Count, json.ChangeCount);
        Assert.Equal(textChanges.Count(c => c.IsDestructive), json.DestructiveCount);
        Assert.Equal(textChanges.Count == 0, json.InSync);

        // Schema-valid: round-trips through the contract serializer and carries the version.
        var root = JsonDocument.Parse(JsonContract.Serialize(json)).RootElement;
        Assert.Equal(JsonContract.SchemaVersion, root.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public async Task PublishDryRun_json_script_matches_text_script_against_live_db()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return; // skip

        var project = DatabaseProject.Load(JsonContractTests.FindSampleProject());
        var source = (await project.BuildAsync()).Model;
        var live = await new LiveDatabaseReader().ReadAsync(conn);

        // The JSON dry-run plan must embed exactly the script the text dry-run prints.
        var textChanges = new SchemaComparer().Compare(source, live, new ComparerOptions { DropObjectsNotInSource = false });
        var textScript = new DeployScriptGenerator().Generate(textChanges, new DeployOptions { WrapInTransaction = true });

        var plan = ContractBuilder.PublishPlan(source, live, project.Name, allowDrops: false, wrapInTransaction: true);

        Assert.True(plan.DryRun);
        Assert.Equal(textChanges.Count, plan.ChangeCount);
        Assert.Equal(textScript, plan.Script);

        var root = JsonDocument.Parse(JsonContract.Serialize(plan)).RootElement;
        Assert.Equal(JsonContract.SchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("publish", root.GetProperty("verb").GetString());
    }
}
