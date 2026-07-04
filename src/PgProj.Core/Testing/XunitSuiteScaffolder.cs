using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Versioning;

namespace PgProj.Core.Testing;

/// <summary>Which test categories the xUnit suite generator emits. All default on; the CLI maps a CSV onto these.</summary>
public sealed record XunitSuiteOptions
{
    public bool Constraints { get; init; } = true;   // NOT NULL / UNIQUE / PK negatives (CHECK → skipped stub)
    public bool ForeignKeys { get; init; } = true;    // orphan-insert negatives
    public bool Crud { get; init; } = true;           // baseline insert round-trip
    public bool Views { get; init; } = true;          // view / matview queryability
    public bool UnitStubs { get; init; } = true;      // function / procedure / trigger skipped stubs
    public bool Existence { get; init; } = true;      // catalog existence smoke tests for other kinds

    public static XunitSuiteOptions All => new();

    /// <summary>Map a CSV like "constraints,fk,crud,view,unit,exists" onto the flags (unknown tokens throw).</summary>
    public static XunitSuiteOptions Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return All;
        bool c = false, fk = false, crud = false, v = false, u = false, e = false;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            switch (raw.ToLowerInvariant())
            {
                case "constraints": case "constraint": c = true; break;
                case "fk": case "foreignkeys": case "foreign-keys": fk = true; break;
                case "crud": crud = true; break;
                case "view": case "views": v = true; break;
                case "unit": case "stubs": u = true; break;
                case "exists": case "existence": e = true; break;
                default: throw new ArgumentException($"Unknown test category '{raw}' (use: constraints,fk,crud,view,unit,exists).");
            }
        return new XunitSuiteOptions { Constraints = c, ForeignKeys = fk, Crud = crud, Views = v, UnitStubs = u, Existence = e };
    }
}

/// <summary>How the emitted fixture obtains its PostgreSQL at test-run time.</summary>
public enum XunitDbMode
{
    /// <summary>Testcontainers by default; <c>PGPROJ_TEST_CONNECTION</c> (when set) switches to an existing server.</summary>
    Auto,
    /// <summary>Always spin a Docker container via Testcontainers (the env var is ignored).</summary>
    Testcontainers,
    /// <summary>Always use an existing server (<c>PGPROJ_TEST_CONNECTION</c> is required; Testcontainers is not referenced).</summary>
    ExistingConnection,
}

/// <summary>Knobs for the emitted project shape (namespace, csproj name, Docker image, target PG version).</summary>
public sealed record XunitSuiteSettings
{
    public string RootNamespace { get; init; } = "Database.Tests";
    public string TestProjectName { get; init; } = "Database.Tests";
    public string PostgresImage { get; init; } = "postgres:18";
    public XunitSuiteOptions Categories { get; init; } = XunitSuiteOptions.All;
    /// <summary>Version profile used to render <c>schema.sql</c> (defaults to <see cref="PostgresVersionProfile.Latest"/>).</summary>
    public PostgresVersionProfile? Profile { get; init; }
    /// <summary>How the emitted fixture picks its database at test-run time (baked into scaffold-once files).</summary>
    public XunitDbMode DbMode { get; init; } = XunitDbMode.Auto;
    /// <summary>Emit the never-overwritten <c>Seeds/*.Seed.cs</c> + <c>Seeds/SuiteSeed.cs</c> hook stubs.</summary>
    public bool GenerateSeedHooks { get; init; } = true;
    /// <summary>When set, a <c>*.local.runsettings</c> carrying this connection as <c>PGPROJ_TEST_CONNECTION</c>
    /// is emitted next to the csproj (git-ignored via the generated <c>.gitignore</c>; never in committed code).</summary>
    public string? TestConnection { get; init; }
}

/// <summary>A single file the generator emits. <see cref="Overwrite"/> distinguishes regenerated files
/// (deleted+rewritten on every run) from scaffold-once files (written only when absent — the csproj,
/// fixtures, and the user's <c>*.Seed.cs</c> hooks, which the generator must never clobber).</summary>
public sealed record GeneratedFile(string RelativePath, string Content, bool Overwrite);

/// <summary>The full emitted project: an ordered set of files rooted at the output directory.</summary>
public sealed record GeneratedTestProject(IReadOnlyList<GeneratedFile> Files);

/// <summary>
/// Generates a STANDALONE xUnit test project from a built <see cref="DatabaseModel"/> — a normal C#
/// project so <c>dotnet test</c> (and the VS Test Explorer) is the only tool anyone needs. It spins its
/// own PostgreSQL via Testcontainers, deploys the project's greenfield schema once, and runs every test in
/// its own transaction that is rolled back (full isolation). Each generated test reuses
/// <see cref="BaselineRowSynthesizer"/> to build a minimal valid INSERT and asserts either success (CRUD /
/// view / existence) or a specific <c>PostgresException.SqlState</c> (constraint / FK negatives). Behaviour
/// that cannot be auto-asserted (function/procedure/trigger semantics, CHECK inversion, a value that cannot
/// be synthesised) becomes a <c>[Fact(Skip=…)]</c> so it is visible-but-not-failing. Every generated file
/// carries the <see cref="Sentinel"/>; the user's data-injection hook lives in a separate never-overwritten
/// <c>partial void Seed(NpgsqlConnection, NpgsqlTransaction)</c> per table.
/// </summary>
public static class XunitSuiteScaffolder
{
    /// <summary>Marker placed in every regenerated file's header; regeneration replaces only files carrying it.</summary>
    public const string Sentinel = "@pgproj-generated";
    private const string GenHeader =
        "// " + Sentinel + ": do not edit — regenerated by 'pgproj test generate'.";

    /// <summary>
    /// The default directory <c>pgproj test generate</c> writes the suite to when no explicit output is
    /// given: a <b>sibling</b> of the project directory (its parent + <paramref name="testProjectName"/>),
    /// deliberately <b>outside</b> the <c>.pgproj</c> directory. A <c>.pgproj</c> globs <c>**/*.sql</c>
    /// rooted at its own folder, so a test project nested under it would have its regenerated
    /// <c>schema.sql</c> swept up as duplicate database objects and break the <c>.pgproj</c> build (#166).
    /// A sibling location keeps the generated SQL clear of that glob. Falls back to the project directory
    /// itself only when it has no parent (e.g. a drive root) — an edge that can't arise for a real project.
    /// This is the single source of truth; the VS "Generate Tests" dialog mirrors it (it can't link Core).
    /// </summary>
    public static string DefaultOutputDirectory(string projectDirectory, string testProjectName)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(projectDirectory))?.FullName;
        return Path.Combine(parent ?? projectDirectory, testProjectName);
    }

    public static GeneratedTestProject Generate(DatabaseModel model, XunitSuiteSettings settings)
    {
        var files = new List<GeneratedFile>();
        var ns = settings.RootNamespace;
        var opt = settings.Categories;

        // ---- scaffold-once infrastructure (never overwritten) ----
        files.Add(new GeneratedFile($"{settings.TestProjectName}.csproj", Csproj(settings), Overwrite: false));
        files.Add(new GeneratedFile("GlobalUsings.cs", GlobalUsings(), Overwrite: false));
        files.Add(new GeneratedFile("PgDatabaseFixture.cs", Fixture(ns, settings), Overwrite: false));
        files.Add(new GeneratedFile("PgTestBase.cs", TestBase(ns), Overwrite: false));
        files.Add(new GeneratedFile("README.md", Readme(settings), Overwrite: false));
        files.Add(new GeneratedFile(".gitignore", "*.local.runsettings\nbin/\nobj/\n", Overwrite: false));
        if (settings.GenerateSeedHooks)
            files.Add(new GeneratedFile("Seeds/SuiteSeed.cs", SuiteSeedStub(ns), Overwrite: false));

        // ---- the caller-supplied connection, as a git-ignored runsettings (regenerated when supplied) ----
        if (!string.IsNullOrWhiteSpace(settings.TestConnection))
            files.Add(new GeneratedFile($"{settings.TestProjectName}.local.runsettings",
                LocalRunSettings(settings.TestConnection!), Overwrite: true));

        // ---- the greenfield schema the fixture deploys (regenerated) ----
        files.Add(new GeneratedFile("schema.sql", SchemaSql(model, settings.Profile ?? PostgresVersionProfile.Latest), Overwrite: true));

        // ---- per-table generated test classes + per-table seed-hook stubs ----
        var baselineHelpers = new StringBuilder();
        foreach (var table in model.Tables.OrderBy(t => t.QualifiedName, StringComparer.Ordinal))
        {
            // Reusable baseline-insert helper so any Seed hook (per-table or SuiteSeed) can satisfy this
            // table's FK chain without hand-writing the parent INSERTs.
            if (TryInsert(model, table, null, null, out var hPrelude, out var hInsert, out _))
            {
                var hm = $"Insert_{Ident(table.Schema)}_{Ident(table.Name)}";
                baselineHelpers.Append($"    /// <summary>Insert one baseline valid row into {table.QualifiedName} (synthesised from the model,\n");
                baselineHelpers.Append("    /// including required depth-1 parent rows). Idempotent (ON CONFLICT DO NOTHING on fixed baseline\n");
                baselineHelpers.Append("    /// keys) — call from any Seed hook to make sure a valid row exists, e.g. to satisfy an FK.</summary>\n");
                baselineHelpers.Append($"    public static async Task {hm}(NpgsqlConnection conn, NpgsqlTransaction tx)\n    {{\n");
                foreach (var h in hPrelude) baselineHelpers.Append($"        await Exec(conn, tx, {CsLit(DoNothing(h))});\n");
                baselineHelpers.Append($"        await Exec(conn, tx, {CsLit(DoNothing(hInsert))});\n");
                baselineHelpers.Append("    }\n\n");
            }

            var cls = ClassName(table.Schema, table.Name, "Tests");
            var body = TableTests(model, table, cls, ns, opt, out var any);
            if (!any) continue;
            files.Add(new GeneratedFile($"Generated/{cls}.g.cs", body, Overwrite: true));
            if (settings.GenerateSeedHooks)
                files.Add(new GeneratedFile($"Seeds/{cls}.Seed.cs", SeedStub(cls, ns, table.QualifiedName), Overwrite: false));
        }
        if (baselineHelpers.Length > 0)
            files.Add(new GeneratedFile("Generated/BaselineRows.g.cs", BaselineRowsFile(ns, baselineHelpers.ToString()), Overwrite: true));

        if (opt.Views && model.Views.Count > 0)
            files.Add(new GeneratedFile("Generated/ViewTests.g.cs", ViewTests(model, ns), Overwrite: true));

        if (opt.UnitStubs)
        {
            var routines = RoutineTests(model, ns);
            if (routines is not null) files.Add(new GeneratedFile("Generated/RoutineTests.g.cs", routines, Overwrite: true));
        }

        if (opt.Existence)
        {
            var existence = ExistenceTests(model, ns);
            if (existence is not null) files.Add(new GeneratedFile("Generated/ExistenceTests.g.cs", existence, Overwrite: true));
        }

        return new GeneratedTestProject(files);
    }

    // ==== per-table tests ==================================================================================

    private static string TableTests(DatabaseModel model, TableDefinition table, string cls, string ns,
        XunitSuiteOptions opt, out bool any)
    {
        var methods = new StringBuilder();
        var used = new HashSet<string>(StringComparer.Ordinal);
        any = false;

        // NOT NULL negatives: baseline row with the target column forced NULL → 23502.
        if (opt.Constraints)
            foreach (var col in table.Columns)
            {
                if (col.IsNullable || col.Default is not null || col.IsIdentity || col.IsSerial || col.GeneratedExpression is not null)
                    continue;
                var over = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [col.Name] = "NULL" };
                if (TryInsert(model, table, over, null, out var prelude, out var insert, out _))
                {
                    EmitNegative(methods, Method(used, $"NotNull_{col.Name}_is_rejected"),
                        $"NOT NULL {table.QualifiedName}.{col.Name}", "23502", prelude, insert, repeatFirst: false);
                    any = true;
                }
                else
                    EmitSkip(methods, Method(used, $"NotNull_{col.Name}_is_rejected"),
                        $"could not synthesise a baseline row for {table.QualifiedName}");
            }

        // PRIMARY KEY: same key twice → 23505.
        if (opt.Constraints && table.PrimaryKey is { Columns.Count: > 0 } pk)
        {
            var force = new HashSet<string>(pk.Columns, StringComparer.OrdinalIgnoreCase);
            if (TryInsert(model, table, null, force, out var prelude, out var insert, out var reason))
            {
                EmitNegative(methods, Method(used, "PrimaryKey_rejects_duplicate"),
                    $"PRIMARY KEY {table.QualifiedName} ({string.Join(", ", pk.Columns)})", "23505", prelude, insert, repeatFirst: true);
                any = true;
            }
            else
                EmitSkip(methods, Method(used, "PrimaryKey_rejects_duplicate"), reason);
        }

        // UNIQUE: same key twice → 23505.
        if (opt.Constraints)
            foreach (var uq in table.Unique.Where(u => u.Columns.Count > 0))
            {
                var force = new HashSet<string>(uq.Columns, StringComparer.OrdinalIgnoreCase);
                var mname = Method(used, $"Unique_{string.Join("_", uq.Columns)}_rejects_duplicate");
                if (TryInsert(model, table, null, force, out var prelude, out var insert, out var reason))
                {
                    EmitNegative(methods, mname,
                        $"UNIQUE {table.QualifiedName} ({string.Join(", ", uq.Columns)})", "23505", prelude, insert, repeatFirst: true);
                    any = true;
                }
                else
                    EmitSkip(methods, mname, reason);
            }

        // CHECK: inverting an arbitrary predicate is undecidable → skipped authoring stub.
        if (opt.Constraints)
            foreach (var ck in table.Checks)
                EmitSkip(methods, Method(used, $"Check_{Ident(ck.Name ?? ck.Expression)}_is_enforced"),
                    $"author: insert a row violating CHECK ({ck.Expression}); expect 23514");

        // FOREIGN KEY orphan child → 23503.
        if (opt.ForeignKeys)
            foreach (var fk in table.ForeignKeys.Where(f => f.Columns.Count > 0))
            {
                var mname = Method(used, $"ForeignKey_{Ident(fk.Name ?? string.Join("_", fk.Columns))}_orphan_is_rejected");
                if (model.FindTable(fk.ReferencedSchema, fk.ReferencedTable) is null)
                {
                    EmitSkip(methods, mname, $"referenced table {fk.ReferencedSchema}.{fk.ReferencedTable} is outside the project model");
                    continue;
                }
                var over = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool ok = true;
                foreach (var cn in fk.Columns)
                {
                    var col = table.FindColumn(cn);
                    if (col is null || !BaselineRowSynthesizer.TryOrphanValue(col.DataType, out var ov)) { ok = false; break; }
                    over[cn] = ov;
                }
                var force = new HashSet<string>(fk.Columns, StringComparer.OrdinalIgnoreCase);
                if (!ok)
                    EmitSkip(methods, mname, "could not synthesise an orphan value for the FK column type");
                else if (TryInsert(model, table, over, force, out var prelude, out var insert, out var reason))
                {
                    EmitNegative(methods, mname,
                        $"FK orphan {table.QualifiedName} ({string.Join(", ", fk.Columns)}) -> {fk.ReferencedSchema}.{fk.ReferencedTable}",
                        "23503", prelude, insert, repeatFirst: false);
                    any = true;
                }
                else
                    EmitSkip(methods, mname, reason);
            }

        // CRUD round-trip: insert a baseline row, assert it landed.
        if (opt.Crud)
        {
            var mname = Method(used, "Crud_insert_roundtrip");
            if (TryInsert(model, table, null, null, out var prelude, out var insert, out var reason))
            {
                EmitCrud(methods, mname, $"CRUD round-trip {table.QualifiedName}", table.QualifiedName, prelude, insert);
                any = true;
            }
            else
                EmitSkip(methods, mname, reason);
        }

        var sb = new StringBuilder();
        sb.Append(GenHeader).Append('\n');
        sb.Append($"namespace {ns}.Generated;\n\n");
        sb.Append($"[Collection(PgDatabaseCollection.Name)]\n");
        sb.Append($"public partial class {cls} : PgTestBase\n{{\n");
        sb.Append($"    public {cls}(PgDatabaseFixture fixture) : base(fixture) {{ }}\n\n");
        sb.Append("    /// <summary>Inject your own reference/parent data before each generated INSERT.\n");
        sb.Append($"    /// Implement in <c>Seeds/{cls}.Seed.cs</c> — that file is never overwritten. Unimplemented = no-op.</summary>\n");
        sb.Append("    partial void Seed(NpgsqlConnection conn, NpgsqlTransaction tx);\n\n");
        sb.Append(methods);
        sb.Append("}\n");
        return sb.ToString();
    }

    // ==== emitters ========================================================================================

    private static void EmitNegative(StringBuilder sb, string method, string desc, string sqlState,
        IReadOnlyList<string> prelude, string insert, bool repeatFirst)
    {
        sb.Append($"    // {desc}\n");
        sb.Append($"    [Fact]\n");
        sb.Append($"    public Task {method}() => InTransactionAsync(async (conn, tx) =>\n    {{\n");
        sb.Append("        Seed(conn, tx);\n");
        // Setup statements tolerate rows already present (a Seed hook / SuiteSeed may have inserted the
        // same baseline values); only the ASSERTED statement stays strict.
        foreach (var p in prelude) sb.Append($"        await Exec(conn, tx, {CsLit(DoNothing(p))});\n");
        if (repeatFirst) sb.Append($"        await Exec(conn, tx, {CsLit(DoNothing(insert))});\n");
        sb.Append($"        await AssertSqlState({CsLit(sqlState)}, conn, tx, {CsLit(insert)});\n");
        sb.Append("    });\n\n");
    }

    private static void EmitCrud(StringBuilder sb, string method, string desc, string qualified,
        IReadOnlyList<string> prelude, string insert)
    {
        sb.Append($"    // {desc}\n");
        sb.Append($"    [Fact]\n");
        sb.Append($"    public Task {method}() => InTransactionAsync(async (conn, tx) =>\n    {{\n");
        sb.Append("        Seed(conn, tx);\n");
        foreach (var p in prelude) sb.Append($"        await Exec(conn, tx, {CsLit(DoNothing(p))});\n");
        sb.Append($"        await Exec(conn, tx, {CsLit(DoNothing(insert))});\n");
        sb.Append($"        await AssertNotEmpty(conn, tx, {CsLit($"SELECT 1 FROM {qualified} LIMIT 1")});\n");
        sb.Append("    });\n\n");
    }

    /// <summary>Make a synthesised baseline INSERT idempotent (`ON CONFLICT DO NOTHING`) so it composes
    /// with seed hooks that may have inserted the same fixed baseline values already. FK and NOT NULL
    /// violations still surface — ON CONFLICT only swallows unique/PK conflicts.</summary>
    private static string DoNothing(string insert) =>
        insert.TrimEnd().TrimEnd(';') + " ON CONFLICT DO NOTHING";

    private static void EmitSkip(StringBuilder sb, string method, string reason)
    {
        sb.Append($"    [Fact(Skip = {CsLit(reason)})]\n");
        sb.Append($"    public void {method}() {{ }}\n\n");
    }

    // ==== views / routines / existence ====================================================================

    private static string ViewTests(DatabaseModel model, string ns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var methods = new StringBuilder();
        foreach (var view in model.Views.OrderBy(v => $"{v.Schema}.{v.Name}", StringComparer.Ordinal))
        {
            var kind = view.IsMaterialized ? "materialized view" : "view";
            var m = Method(used, $"View_{Ident(view.Schema)}_{Ident(view.Name)}_is_queryable");
            methods.Append($"    // {kind} queryability {view.Schema}.{view.Name}\n");
            methods.Append($"    [Fact]\n");
            methods.Append($"    public Task {m}() => InTransactionAsync(async (conn, tx) =>\n    {{\n");
            // LIMIT 0 forces full bind/type resolution of the body with zero rows; a broken view raises here.
            methods.Append($"        await Exec(conn, tx, {CsLit($"SELECT * FROM {view.Schema}.{view.Name} LIMIT 0")});\n");
            methods.Append("    });\n\n");
        }
        return ClassFile(ns, "ViewTests", methods.ToString());
    }

    private static string? RoutineTests(DatabaseModel model, string ns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var methods = new StringBuilder();
        bool any = false;
        foreach (var fn in model.Functions.OrderBy(f => $"{f.Schema}.{f.Name}", StringComparer.Ordinal))
        {
            any = true;
            EmitSkip(methods, Method(used, $"Function_{Ident(fn.Schema)}_{Ident(fn.Name)}_behaviour"),
                $"author the behavioural test for {fn.Schema}.{fn.Name}: arrange, act, assert");
        }
        foreach (var trg in model.Objects.Where(o => o.Kind == ObjectKind.Trigger).OrderBy(o => $"{o.Schema}.{o.Name}", StringComparer.Ordinal))
        {
            any = true;
            var schema = string.IsNullOrEmpty(trg.Schema) ? "public" : trg.Schema;
            EmitSkip(methods, Method(used, $"Trigger_{Ident(schema)}_{Ident(trg.Name)}_behaviour"),
                $"author the behavioural test for trigger {schema}.{trg.Name}");
        }
        return any ? ClassFile(ns, "RoutineTests", methods.ToString()) : null;
    }

    private static string? ExistenceTests(DatabaseModel model, string ns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var methods = new StringBuilder();
        bool any = false;

        void Boolean(string schema, string name, string label, string boolExpr, string msg)
        {
            any = true;
            var m = Method(used, $"Exists_{label}_{Ident(schema)}_{Ident(name)}");
            methods.Append($"    [Fact]\n");
            methods.Append($"    public Task {m}() => InTransactionAsync(async (conn, tx) =>\n    {{\n");
            methods.Append($"        await AssertTrue(conn, tx, {CsLit($"SELECT {boolExpr}")}, {CsLit(msg)});\n");
            methods.Append("    });\n\n");
        }
        void Present(string schema, string name, string label, string query)
        {
            any = true;
            var m = Method(used, $"Exists_{label}_{Ident(schema)}_{Ident(name)}");
            methods.Append($"    [Fact]\n");
            methods.Append($"    public Task {m}() => InTransactionAsync(async (conn, tx) =>\n    {{\n");
            methods.Append($"        await AssertNotEmpty(conn, tx, {CsLit(query)});\n");
            methods.Append("    });\n\n");
        }

        foreach (var seq in model.Sequences.OrderBy(s => $"{s.Schema}.{s.Name}", StringComparer.Ordinal))
            Present(seq.Schema, seq.Name, "sequence",
                $"SELECT 1 FROM pg_sequences WHERE schemaname = {Sql(seq.Schema)} AND sequencename = {Sql(seq.Name)}");

        foreach (var o in model.Objects)
        {
            if (o.Kind == ObjectKind.Comment || string.IsNullOrEmpty(o.Name)) continue;
            var qn = string.IsNullOrEmpty(o.Schema) ? o.Name : $"{o.Schema}.{o.Name}";
            switch (o.Kind)
            {
                case ObjectKind.Type:
                case ObjectKind.Domain:
                    Boolean(o.Schema, o.Name, o.Kind.ToString().ToLowerInvariant(), $"to_regtype({Sql(qn)}) IS NOT NULL", $"type {qn} is missing");
                    break;
                case ObjectKind.Table:
                    Boolean(o.Schema, o.Name, "table", $"to_regclass({Sql(qn)}) IS NOT NULL", $"relation {qn} is missing");
                    break;
                case ObjectKind.Extension:
                    Present(o.Schema, o.Name, "extension", $"SELECT 1 FROM pg_extension WHERE extname = {Sql(o.Name)}");
                    break;
                case ObjectKind.Policy:
                    var tbl = string.IsNullOrEmpty(o.OnObject) ? "%" : BareTable(o.OnObject!);
                    Present(o.Schema, o.Name, "policy", $"SELECT 1 FROM pg_policies WHERE policyname = {Sql(o.Name)} AND tablename LIKE {Sql(tbl)}");
                    break;
                case ObjectKind.Rule:
                    Present(o.Schema, o.Name, "rule", $"SELECT 1 FROM pg_rules WHERE rulename = {Sql(o.Name)}");
                    break;
                case ObjectKind.Collation:
                    Present(o.Schema, o.Name, "collation", $"SELECT 1 FROM pg_collation WHERE collname = {Sql(o.Name)}");
                    break;
                case ObjectKind.EventTrigger:
                    Present(o.Schema, o.Name, "event_trigger", $"SELECT 1 FROM pg_event_trigger WHERE evtname = {Sql(o.Name)}");
                    break;
                case ObjectKind.Statistics:
                    Present(o.Schema, o.Name, "statistics", $"SELECT 1 FROM pg_statistic_ext WHERE stxname = {Sql(o.Name)}");
                    break;
                case ObjectKind.Publication:
                    Present(o.Schema, o.Name, "publication", $"SELECT 1 FROM pg_publication WHERE pubname = {Sql(o.Name)}");
                    break;
                default:
                    break; // tables/views/functions/triggers covered elsewhere; other kinds have no safe predicate
            }
        }
        return any ? ClassFile(ns, "ExistenceTests", methods.ToString()) : null;
    }

    /// <summary>The static helper class carrying one model-synthesised baseline INSERT per table,
    /// callable from any Seed hook (its own file so the xUnit analyzers don't see public non-test
    /// methods on a test class).</summary>
    private static string BaselineRowsFile(string ns, string methods)
    {
        var sb = new StringBuilder();
        sb.Append(GenHeader).Append('\n');
        sb.Append($"namespace {ns}.Generated;\n\n");
        sb.Append("/// <summary>Model-synthesised baseline rows — reusable building blocks for Seed hooks.</summary>\n");
        sb.Append("public static class BaselineRows\n{\n");
        sb.Append(methods);
        sb.Append("    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)\n    {\n");
        sb.Append("        await using var cmd = new NpgsqlCommand(sql, conn, tx);\n");
        sb.Append("        await cmd.ExecuteNonQueryAsync();\n    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>A generated non-table test class (no Seed hook) wrapping <paramref name="methods"/>.</summary>
    private static string ClassFile(string ns, string cls, string methods)
    {
        var sb = new StringBuilder();
        sb.Append(GenHeader).Append('\n');
        sb.Append($"namespace {ns}.Generated;\n\n");
        sb.Append($"[Collection(PgDatabaseCollection.Name)]\n");
        sb.Append($"public sealed class {cls} : PgTestBase\n{{\n");
        sb.Append($"    public {cls}(PgDatabaseFixture fixture) : base(fixture) {{ }}\n\n");
        sb.Append(methods);
        sb.Append("}\n");
        return sb.ToString();
    }

    // ==== scaffold-once infrastructure ====================================================================

    private static string Csproj(XunitSuiteSettings s) =>
$@"<Project Sdk=""Microsoft.NET.Sdk"">

  <!-- {Sentinel}: generated once by 'pgproj test generate'. Safe to edit — regeneration will not overwrite it. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>{s.RootNamespace}</RootNamespace>
    <!-- Auto-apply the git-ignored local run settings when present (carries PGPROJ_TEST_CONNECTION),
         so `dotnet test` and the VS Test Explorer pick it up with no extra flags. -->
    <RunSettingsFilePath Condition=""Exists('$(MSBuildProjectDirectory)\{s.TestProjectName}.local.runsettings')"">$(MSBuildProjectDirectory)\{s.TestProjectName}.local.runsettings</RunSettingsFilePath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.11.1"" />
    <PackageReference Include=""xunit"" Version=""2.9.2"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.8.2"" />
    <PackageReference Include=""Npgsql"" Version=""9.0.2"" />{(s.DbMode == XunitDbMode.ExistingConnection ? "" : @"
    <PackageReference Include=""Testcontainers.PostgreSql"" Version=""4.1.0"" />")}
  </ItemGroup>

  <ItemGroup>
    <!-- The greenfield schema the fixture deploys into the container. Regenerated each run. -->
    <None Include=""schema.sql"" CopyToOutputDirectory=""PreserveNewest"" />
  </ItemGroup>

</Project>
";

    private static string GlobalUsings() =>
        GenHeader + "\n" +
        "global using System.Threading.Tasks;\n" +
        "global using Npgsql;\n" +
        "global using Xunit;\n";

    private static string Fixture(string ns, XunitSuiteSettings s)
    {
        var image = s.PostgresImage;
        var useContainer = s.DbMode is XunitDbMode.Auto or XunitDbMode.Testcontainers;
        var useExisting = s.DbMode is XunitDbMode.Auto or XunitDbMode.ExistingConnection;
        var profileMajor = (s.Profile ?? PostgresVersionProfile.Latest).MajorVersion;

        var modeDoc = s.DbMode switch
        {
            XunitDbMode.Testcontainers =>
                "/// Pinned at generation time to TESTCONTAINERS: a throwaway PostgreSQL container is spun via\n" +
                "/// Testcontainers (needs a Docker daemon). Regenerate with `--db-mode existing --force` to run\n" +
                "/// against your own server instead.",
            XunitDbMode.ExistingConnection =>
                "/// Pinned at generation time to an EXISTING SERVER: the <c>PGPROJ_TEST_CONNECTION</c> environment\n" +
                "/// variable (an admin/server connection) is REQUIRED — a throwaway database is created on that\n" +
                "/// server, used, and dropped afterwards. Set it via the *.local.runsettings file next to the csproj\n" +
                "/// (picked up automatically) or in the shell. No Docker needed.",
            _ =>
                "/// TWO modes, chosen automatically:\n" +
                "/// <list type=\"bullet\">\n" +
                "/// <item>If the <c>PGPROJ_TEST_CONNECTION</c> environment variable is set (an admin/server connection),\n" +
                "/// a THROWAWAY database is created on that server, used, and dropped afterwards — no Docker needed. Point it\n" +
                "/// at your own PostgreSQL (local, CI service container, shared box).</item>\n" +
                "/// <item>Otherwise a throwaway PostgreSQL CONTAINER is spun via Testcontainers (needs a Docker daemon).</item>\n" +
                "/// </list>",
        };

        var existingBody =
$@"            // Use the caller's PostgreSQL: create a throwaway database so tests never touch real data.
            _adminConnectionString = existing;
            _throwawayDatabase = ""pgproj_test_"" + Guid.NewGuid().ToString(""N"");
            await using (var admin = new NpgsqlConnection(existing))
            {{
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($""CREATE DATABASE \""{{_throwawayDatabase}}\"""", admin);
                await create.ExecuteNonQueryAsync();
            }}
            connectionString = new NpgsqlConnectionStringBuilder(existing) {{ Database = _throwawayDatabase }}.ConnectionString;";

        var dockerAlternative = s.DbMode == XunitDbMode.Testcontainers
            ? @"""This suite is pinned to Testcontainers; regenerate it with 'pgproj test generate ... "" +
                    ""--db-mode existing --force' to run against an existing PostgreSQL instead."""
            : @"""Alternatively set the PGPROJ_TEST_CONNECTION environment variable to an admin/server "" +
                    ""connection string to run against an existing PostgreSQL (no Docker required).""";
        var containerBody =
$@"            try
            {{
                _container = new PostgreSqlBuilder().WithImage(""{image}"").Build();
                await _container.StartAsync();
                connectionString = _container.GetConnectionString();
            }}
            catch (Exception ex)
            {{
                throw new InvalidOperationException(
                    ""Could not start the PostgreSQL test container — is a Docker daemon running? "" +
                    {dockerAlternative}, ex);
            }}";

        string modeSelection;
        if (s.DbMode == XunitDbMode.Testcontainers)
            modeSelection = containerBody;
        else if (s.DbMode == XunitDbMode.ExistingConnection)
            modeSelection =
$@"            var existing = Environment.GetEnvironmentVariable(""PGPROJ_TEST_CONNECTION"");
            if (string.IsNullOrWhiteSpace(existing))
                throw new InvalidOperationException(
                    ""PGPROJ_TEST_CONNECTION is not set. This test project was generated for an existing "" +
                    ""PostgreSQL server: set the variable to an admin/server connection string (a throwaway "" +
                    ""database is created and dropped), e.g. via the *.local.runsettings file next to the csproj."");
{existingBody}";
        else
            modeSelection =
$@"            var existing = Environment.GetEnvironmentVariable(""PGPROJ_TEST_CONNECTION"");
            if (!string.IsNullOrWhiteSpace(existing))
            {{
{existingBody}
            }}
            else
            {{
{containerBody.Replace("            ", "                ")}
            }}";

        return
$@"// {Sentinel}: generated once. Safe to edit — regeneration will not overwrite it.
{(useContainer ? "using Testcontainers.PostgreSql;\n" : "")}
namespace {ns};

/// <summary>
/// Provides one PostgreSQL database for the whole test run and deploys the project's greenfield schema
/// (schema.sql) into it; every test then runs in its own rolled-back transaction, so no test sees another's
/// rows.
{modeDoc}
/// </summary>
public sealed partial class PgDatabaseFixture : IAsyncLifetime
{{
{(useContainer ? "    private PostgreSqlContainer? _container;\n" : "")}{(useExisting ? "    private string? _adminConnectionString;   // set in existing-server mode, to drop the throwaway DB\n    private string? _throwawayDatabase;\n" : "")}
    public NpgsqlDataSource DataSource {{ get; private set; }} = default!;

    /// <summary>Suite-level data hook: runs ONCE after the schema deploys, in a COMMITTED transaction, so
    /// every test (each in its own rolled-back transaction) sees the data. Implement it in
    /// <c>Seeds/SuiteSeed.cs</c> — that file is never overwritten. Unimplemented = no-op.</summary>
    partial void SeedSuite(NpgsqlConnection conn, NpgsqlTransaction tx);

    public async Task InitializeAsync()
    {{
        string connectionString;
        try
        {{
{modeSelection}

            DataSource = NpgsqlDataSource.Create(connectionString);

            var schemaPath = Path.Combine(AppContext.BaseDirectory, ""schema.sql"");
            var schema = await File.ReadAllTextAsync(schemaPath);
            var statements = schema
                .Split(""{StmtDelim}"", StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            await using var conn = await DataSource.OpenConnectionAsync();

            // Order-tolerant deploy: each object is applied on its own (autocommit). A statement that fails
            // because a dependency does not exist yet (e.g. a view created before the function it calls) is
            // deferred and retried, until a full pass makes no progress — then the real error is surfaced.
            var pending = statements;
            while (pending.Count > 0)
            {{
                var failed = new List<string>();
                PostgresException? last = null;
                foreach (var stmt in pending)
                {{
                    try
                    {{
                        await using var cmd = new NpgsqlCommand(stmt, conn);
                        await cmd.ExecuteNonQueryAsync();
                    }}
                    catch (PostgresException ex) when (IsMissingDependency(ex))
                    {{
                        failed.Add(stmt);
                        last = ex;
                    }}
                }}
                if (failed.Count == pending.Count)
                    throw new InvalidOperationException(
                        $""schema deploy could not resolve {{failed.Count}} statement(s) "" +
                        $""(schema.sql was rendered for PostgreSQL {profileMajor}; the server is {{conn.PostgreSqlVersion}}); "" +
                        $""last error: {{last?.MessageText}}"", last);
                pending = failed;
            }}

            // Suite-level seed: committed once, visible to every test.
            await using (var seedTx = await conn.BeginTransactionAsync())
            {{
                SeedSuite(conn, seedTx);
                await seedTx.CommitAsync();
            }}
        }}
        catch
        {{
            // xUnit does not call DisposeAsync when InitializeAsync throws — clean up eagerly so a failed
            // deploy never leaks the container or a pgproj_test_* database on a real server.
            await DisposeAsync();
            throw;
        }}
    }}

    /// <summary>A failure that a later pass might fix once other objects exist (missing table/function/type/schema).</summary>
    private static bool IsMissingDependency(PostgresException ex) => ex.SqlState is
        ""42883"" or ""42P01"" or ""42704"" or ""42P17"" or ""3F000"" or ""42846"";

    public async Task DisposeAsync()
    {{
        if (DataSource is not null) await DataSource.DisposeAsync();
{(useExisting ? $@"
        if (_throwawayDatabase is not null && _adminConnectionString is not null)
        {{
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($""DROP DATABASE IF EXISTS \""{{_throwawayDatabase}}\"" WITH (FORCE)"", admin);
            await drop.ExecuteNonQueryAsync();
            _throwawayDatabase = null;
        }}
" : "")}{(useContainer ? $@"
        if (_container is not null)
        {{
            await _container.DisposeAsync();
            _container = null;
        }}
" : "")}    }}
}}

/// <summary>The xUnit collection that shares the single database fixture across all generated test classes.</summary>
[CollectionDefinition(Name)]
public sealed class PgDatabaseCollection : ICollectionFixture<PgDatabaseFixture>
{{
    public const string Name = ""pgproj-database"";
}}
";
    }

    private static string TestBase(string ns) =>
$@"// {Sentinel}: generated once. Safe to edit — regeneration will not overwrite it.
namespace {ns};

/// <summary>
/// Base for every generated test class: hands out a connection from the shared fixture and runs the test body
/// inside a transaction that is always rolled back (isolation between tests and from any seeded data).
/// </summary>
public abstract class PgTestBase
{{
    protected PgDatabaseFixture Fixture {{ get; }}
    protected PgTestBase(PgDatabaseFixture fixture) => Fixture = fixture;

    /// <summary>Open a connection + transaction, run <paramref name=""body""/>, then ALWAYS roll back.</summary>
    protected async Task InTransactionAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task> body)
    {{
        await using var conn = await Fixture.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try {{ await body(conn, tx); }}
        finally {{ await tx.RollbackAsync(); }}
    }}

    protected static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {{
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }}

    /// <summary>Assert that running <paramref name=""sql""/> throws a PostgreSQL error with the given SQLSTATE.</summary>
    protected static async Task AssertSqlState(string expected, NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {{
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {{
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }});
        Assert.Equal(expected, ex.SqlState);
    }}

    /// <summary>Assert the query returns at least one row.</summary>
    protected static async Task AssertNotEmpty(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {{
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var scalar = await cmd.ExecuteScalarAsync();
        Assert.NotNull(scalar);
    }}

    /// <summary>Assert the boolean query evaluates to TRUE.</summary>
    protected static async Task AssertTrue(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string message)
    {{
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var value = await cmd.ExecuteScalarAsync();
        Assert.True(value is bool b && b, message);
    }}
}}
";

    private static string SeedStub(string cls, string ns, string qualified) =>
$@"// Data-injection hook for {qualified}. Inject reference/parent rows before each generated INSERT.
// Runs inside each test's own rolled-back transaction. For data shared across MANY tables, prefer the
// once-per-run Seeds/SuiteSeed.cs hook instead.
// THIS FILE IS NEVER OVERWRITTEN by 'pgproj test generate'. Leave the body empty for no seed data.
namespace {ns}.Generated;

public partial class {cls}
{{
    partial void Seed(NpgsqlConnection conn, NpgsqlTransaction tx)
    {{
        // Example:
        // using var cmd = new NpgsqlCommand(""INSERT INTO ref.currency (code) VALUES ('EUR')"", conn, tx);
        // cmd.ExecuteNonQuery();
        //
        // A generated baseline helper also works (satisfies the table's FK chain automatically):
        // BaselineRows.Insert_myschema_mytable(conn, tx).GetAwaiter().GetResult();
    }}
}}
";

    private static string SuiteSeedStub(string ns) =>
$@"// Suite-level data hook: runs ONCE after the schema deploys, in a COMMITTED transaction — every test
// (each in its own rolled-back transaction) sees this data. Use it for shared reference/lookup rows that
// many tables need; for per-test data use the per-table Seeds/*.Seed.cs hooks instead.
// THIS FILE IS NEVER OVERWRITTEN by 'pgproj test generate'. Leave the body empty for no seed data.
namespace {ns};

public sealed partial class PgDatabaseFixture
{{
    partial void SeedSuite(NpgsqlConnection conn, NpgsqlTransaction tx)
    {{
        // Example:
        // using var cmd = new NpgsqlCommand(""INSERT INTO ref.currency (code) VALUES ('EUR')"", conn, tx);
        // cmd.ExecuteNonQuery();
        //
        // Or reuse a generated baseline-insert helper (satisfies the table's FK chain automatically):
        // Generated.BaselineRows.Insert_myschema_mytable(conn, tx).GetAwaiter().GetResult();
    }}
}}
";

    /// <summary>The git-ignored runsettings that carries the caller-supplied connection into every test run
    /// (auto-applied via the csproj's conditional <c>RunSettingsFilePath</c>).</summary>
    private static string LocalRunSettings(string connection) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<!-- Contains a live connection string. DO NOT COMMIT (the generated .gitignore covers *.local.runsettings).
     Written by 'pgproj test generate' with the connection flag; picked up automatically by dotnet test / Test Explorer. -->
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <PGPROJ_TEST_CONNECTION>{Xml(connection)}</PGPROJ_TEST_CONNECTION>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
";

    private static string Readme(XunitSuiteSettings s)
    {
        var mode = s.DbMode switch
        {
            XunitDbMode.Testcontainers =>
                "This suite is pinned to **Testcontainers**: it always spins a throwaway PostgreSQL container\n" +
                $"(`{s.PostgresImage}`) — a running Docker daemon is required.",
            XunitDbMode.ExistingConnection =>
                "This suite is pinned to an **existing PostgreSQL server**: the `PGPROJ_TEST_CONNECTION`\n" +
                "environment variable (an admin/server connection string) is required. A throwaway\n" +
                "`pgproj_test_<guid>` database is created on that server, used, and dropped afterwards.\n" +
                "No Docker is needed.",
            _ =>
                "The database is chosen automatically at run time:\n\n" +
                "- **`PGPROJ_TEST_CONNECTION` set** (admin/server connection string) → a throwaway\n" +
                "  `pgproj_test_<guid>` database is created on that server, used, and dropped. No Docker needed.\n" +
                $"- **Otherwise** → a throwaway PostgreSQL container (`{s.PostgresImage}`) via Testcontainers\n" +
                "  (needs a Docker daemon).",
        };
        return
$@"# {s.TestProjectName}

Standalone xUnit test suite generated by `pgproj test generate` — run it with `dotnet test` or the
Visual Studio Test Explorer; no PgProj tooling is needed. The fixture deploys the project's greenfield
`schema.sql` once, then every test runs in its own rolled-back transaction.

## Database

{mode}

The easiest way to set `PGPROJ_TEST_CONNECTION` for `dotnet test`/Test Explorer is the
`{s.TestProjectName}.local.runsettings` file next to this csproj (auto-applied when present, and
git-ignored because it contains a live connection string). `pgproj test generate --connection ""…""`
writes it for you.

## Generated vs yours

| Files | Regenerated? |
|---|---|
| `Generated/*.g.cs`, `schema.sql` | **Overwritten on every regeneration** — never edit. |
| csproj, `PgDatabaseFixture.cs`, `PgTestBase.cs`, `GlobalUsings.cs`, this README | Written once; only `--force` overwrites. Safe to edit. |
| `Seeds/*.Seed.cs`, `Seeds/SuiteSeed.cs` | **Never overwritten** — your data hooks live here. |

## Seed hooks

- `Seeds/SuiteSeed.cs` → `SeedSuite(conn, tx)` runs **once** after schema deploy, **committed** —
  shared reference/lookup data every test can rely on.
- `Seeds/<Table>Tests.Seed.cs` → `Seed(conn, tx)` runs at the start of **each** of that table's tests,
  inside the test's rolled-back transaction.
- `Generated/BaselineRows.g.cs` exposes `Insert_<schema>_<table>(conn, tx)` per table — an idempotent,
  model-synthesised valid row (including required depth-1 FK parents) you can call from any hook to
  make sure a valid row exists, e.g. to satisfy an FK chain.

Generated setup INSERTs use `ON CONFLICT DO NOTHING`, so seeding the same baseline values is safe.
One caveat: a **committed** suite-seeded row that used an explicit identity value can leave the
identity sequence behind it, making a later `GENERATED ALWAYS` insert collide — prefer high explicit
keys (or `setval`) when suite-seeding tables that generated tests also insert into.

## Deploy notes

`schema.sql` is statement-delimited and deployed order-tolerantly: statements failing on a missing
dependency (SQLSTATE 42883/42P01/42704/42P17/3F000/42846) are retried until a pass makes no progress,
then the real error is surfaced.
";
    }

    /// <summary>Minimal XML text escaping for the runsettings value.</summary>
    private static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ==== schema.sql (greenfield DDL) =====================================================================

    /// <summary>The per-object delimiter in schema.sql; the fixture splits on it and applies each object
    /// independently (with retry), so a view emitted before the function it calls still deploys.</summary>
    private const string StmtDelim = "-- @pgproj-stmt";

    private static string SchemaSql(DatabaseModel model, PostgresVersionProfile profile)
    {
        var changes = new SchemaComparer(profile).Compare(model, new DatabaseModel());
        var sb = new StringBuilder();
        sb.Append("-- ").Append(Sentinel)
          .Append(": greenfield schema deployed by PgDatabaseFixture (statement-delimited, order-tolerant). Regenerated by 'pgproj test generate'.\n");
        foreach (var change in changes)
        {
            var sql = change.ToSql();
            if (string.IsNullOrWhiteSpace(sql)) continue;
            sql = sql.TrimEnd();
            if (!sql.EndsWith(";", StringComparison.Ordinal)) sql += ";";
            sb.Append('\n').Append(StmtDelim).Append('\n');
            sb.Append("-- ").Append(change.Describe()).Append('\n');
            sb.Append(sql).Append('\n');
        }
        return sb.ToString();
    }

    // ==== helpers =========================================================================================

    private static bool TryInsert(DatabaseModel model, TableDefinition table,
        IReadOnlyDictionary<string, string>? overrides, ISet<string>? forceEmit,
        out List<string> prelude, out string insert, out string reason) =>
        BaselineRowSynthesizer.TryBuildInsert(model, table, overrides, forceEmit, depth: 0, out prelude, out insert, out reason);

    /// <summary>A stable, unique C# method name (dedupe by appending _2, _3, …).</summary>
    private static string Method(HashSet<string> used, string baseName)
    {
        var name = Ident(baseName);
        if (used.Add(name)) return name;
        for (var n = 2; ; n++) { var c = $"{name}_{n}"; if (used.Add(c)) return c; }
    }

    /// <summary>A C# class name: PascalCase-ish, schema-qualified, always a valid identifier.</summary>
    private static string ClassName(string schema, string name, string suffix) =>
        $"{Ident(schema)}_{Ident(name)}_{suffix}";

    /// <summary>Reduce an arbitrary identifier fragment to a valid C# identifier token.</summary>
    private static string Ident(string s)
    {
        var sb = new StringBuilder(s.Length + 1);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        if (sb.Length == 0) return "_";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>A C# double-quoted string literal for an arbitrary (single-line) SQL statement.</summary>
    private static string CsLit(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    /// <summary>A SQL single-quoted string literal (for catalog predicates embedded in generated SQL).</summary>
    private static string Sql(string s) => "'" + s.Replace("'", "''") + "'";

    private static string BareTable(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }
}
