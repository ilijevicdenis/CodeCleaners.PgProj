using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Benchmarks;

/// <summary>
/// Loads the repo's PG18 corpus (tests/corpus/*.jsonl) and buckets the *valid* (expect=ok) statements
/// by kind, so each benchmark can be attributed to a workload shape:
///   - <see cref="Table"/>  : CREATE/ALTER TABLE — the structured-model hot path.
///   - <see cref="Raw"/>    : COMMENT/GRANT/EXTENSION/TRIGGER/POLICY/… — the raw/unsupported bucket
///                            where the redundant re-tokenization (audit §1c) is concentrated.
///   - <see cref="Select"/> : SELECT + expression-heavy statements — where the params/ToUpper
///                            expression-grammar allocations live.
///   - <see cref="All"/>    : every valid statement, the representative mixed file.
/// Each bucket is the statements joined with ";\n" so a single string is one realistic .sql file.
/// </summary>
public static class CorpusWorkload
{
    public sealed record Case(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("sql")] string Sql,
        [property: JsonPropertyName("expect")] string Expect);

    public static string Table { get; }
    public static string Raw { get; }
    public static string Select { get; }
    public static string All { get; }

    /// <summary>Bucket name → its concatenated SQL (used by the <c>[Params]</c> selector in the benchmarks).</summary>
    public static IReadOnlyDictionary<string, string> Buckets { get; }

    static CorpusWorkload()
    {
        var table = new List<string>();
        var raw = new List<string>();
        var select = new List<string>();
        var all = new List<string>();

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var file in Directory.EnumerateFiles(CorpusDir, "*.jsonl")
                     .Where(f => !Path.GetFileName(f).StartsWith('_'))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("//")) continue;
                Case? c;
                try { c = JsonSerializer.Deserialize<Case>(t, opts); }
                catch (JsonException) { continue; }
                if (c is null || string.IsNullOrWhiteSpace(c.Sql) || c.Expect != "ok") continue;

                all.Add(c.Sql);
                switch (Classify(c.Category))
                {
                    case Kind.Table: table.Add(c.Sql); break;
                    case Kind.Select: select.Add(c.Sql); break;
                    default: raw.Add(c.Sql); break;
                }
            }
        }

        Table = Join(table);
        Raw = Join(raw);
        Select = Join(select);
        All = Join(all);
        Buckets = new Dictionary<string, string>
        {
            ["Table"] = Table,
            ["Raw"] = Raw,
            ["Select"] = Select,
            ["All"] = All,
        };
    }

    private enum Kind { Table, Select, Raw }

    private static Kind Classify(string category)
    {
        var c = category.ToLowerInvariant();
        if (c.StartsWith("create-table") || c == "alter-table") return Kind.Table;
        if (c.StartsWith("select") || c.Contains("expr") || c.Contains("subquery")
            || c.Contains("join") || c.Contains("window") || c.Contains("cte")
            || c.Contains("operators") || c.Contains("json") || c.Contains("func"))
            return Kind.Select;
        return Kind.Raw;   // comment-on, grant-revoke, create-extension, triggers, policies, types, …
    }

    private static string Join(IReadOnlyCollection<string> stmts) =>
        stmts.Count == 0 ? "" : string.Join(";\n", stmts) + ";\n";

    private static string CorpusDir => Path.Combine(RepoRoot, "tests", "corpus");

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgProj.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (PgProj.slnx) not found");
        }
    }
}
