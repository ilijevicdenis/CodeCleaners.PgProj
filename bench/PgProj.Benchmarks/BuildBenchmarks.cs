using BenchmarkDotNet.Attributes;
using PgProj.Core.Project;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 3 — end-to-end build. The headline of audit recommendation #1: serial <see cref="DatabaseProject.Build"/>
/// vs the parallel <see cref="DatabaseProject.BuildAsync"/> over a real on-disk multi-file project.
/// Swept across file counts to show the speedup scale with project size (and to expose the small-N
/// regime where per-file work is too cheap to beat the parallel scheduling overhead).
///
/// Each generated file is a non-trivial CREATE TABLE (+ index) so the per-file parse cost is realistic;
/// a project of one-line tables would measure the scheduler, not the parser.
/// </summary>
[MemoryDiagnoser]
public class BuildBenchmarks
{
    [Params(1, 10, 50, 200)]
    public int FileCount;

    private string _dir = "";
    private DatabaseProject _project = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var proj = Path.Combine(_dir, "Bench.pgproj");
        File.WriteAllText(proj, """
            <Project><PropertyGroup><Name>Bench</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
            <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "00_schema.sql"), "CREATE SCHEMA app;");

        for (var i = 0; i < FileCount; i++)
            File.WriteAllText(Path.Combine(_dir, $"t{i:D4}.sql"), TableSql(i));

        _project = DatabaseProject.Load(proj);
    }

    [GlobalCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Benchmark(Baseline = true)]
    public int Build() => _project.Build().Model.Tables.Count;

    [Benchmark]
    public async Task<int> BuildParallel() => (await _project.BuildAsync()).Model.Tables.Count;

    // A realistic-ish table: several typed columns, a PK, a FK, a CHECK, and an index — enough parse
    // work per file that the parallel build has something to chew on.
    private static string TableSql(int i) => $$"""
        CREATE TABLE app.t{{i:D4}} (
            id          bigint PRIMARY KEY,
            parent_id   bigint REFERENCES app.t0000 (id),
            code        varchar(32) NOT NULL,
            name        text NOT NULL DEFAULT 'unnamed',
            amount      numeric(18,4) NOT NULL DEFAULT 0,
            is_active   boolean NOT NULL DEFAULT true,
            created_at  timestamptz NOT NULL DEFAULT now(),
            payload     jsonb,
            CONSTRAINT ck_t{{i:D4}}_amount CHECK (amount >= 0)
        );
        CREATE INDEX ix_t{{i:D4}}_code ON app.t{{i:D4}} (code);
        """;
}
