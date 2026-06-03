using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Semantics;
using PgProj.Core.Syntax;

namespace PgProj.Core.Tests;

/// <summary>One corpus case (mirrors the JSONL schema in tests/corpus/CORPUS.md).</summary>
public sealed record CorpusCase(string Id, string Category, string Sql, string Expect,
                                string? Ref, string? Note, string? Txn);

/// <summary>
/// Shared access to the corpus and the toolchain's accept/reject decision. Greenfield: the only
/// engine is the hand-written <see cref="PgParser"/> + <see cref="SemanticAnalyzer"/> — no legacy parser.
/// </summary>
public static class CorpusData
{
    public static string RepoRoot => _repoRoot.Value;
    private static readonly Lazy<string> _repoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgProj.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root (PgProj.slnx) not found");
        return dir.FullName;
    });

    public static string CorpusDir => Path.Combine(RepoRoot, "tests", "corpus");

    private static readonly Lazy<Catalog> _fixtureCatalog = new(() =>
    {
        var fixture = Path.Combine(CorpusDir, "_fixture.sql");
        return CatalogBuilder.Build(File.Exists(fixture) ? File.ReadAllText(fixture) : "");
    });

    public static IReadOnlyList<CorpusCase> LoadAll()
    {
        var cases = new List<CorpusCase>();
        if (!Directory.Exists(CorpusDir)) return cases;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var file in Directory.EnumerateFiles(CorpusDir, "*.jsonl")
                     .Where(f => !Path.GetFileName(f).StartsWith('_'))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            int n = 0;
            foreach (var raw in File.ReadLines(file))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
                CorpusCase? c;
                try { c = JsonSerializer.Deserialize<CorpusCase>(line, opts); }
                catch (JsonException ex) { throw new FormatException($"{Path.GetFileName(file)}:{n}: invalid JSON: {ex.Message}"); }
                if (c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Sql) || string.IsNullOrWhiteSpace(c.Expect))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: case needs id, sql, expect");
                if (c.Expect is not ("ok" or "error"))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: expect must be ok|error (got {c.Expect})");
                cases.Add(c);
            }
        }
        return cases;
    }

    /// <summary>
    /// The accept/reject decision: PgParser parses the statement, then (if syntactically clean) the
    /// semantic analyzer checks it against the fixture catalog. ParsedClean = "valid, would deploy";
    /// HasError = a syntactic OR semantic problem was found.
    /// </summary>
    public static (bool ParsedClean, bool HasError) Evaluate(string sql)
    {
        var res = new PgParser().Parse(sql);
        bool parsedClean = res.Diagnostics.Count == 0 && res.Statements.Count > 0;
        bool hasError = res.Diagnostics.Count > 0;

        if (res.Diagnostics.Count == 0)
        {
            try
            {
                var caseCatalog = _fixtureCatalog.Value.Extend(CatalogBuilder.Build(res));
                if (new SemanticAnalyzer(caseCatalog, _fixtureCatalog.Value).Analyze(res).Count > 0)
                { hasError = true; parsedClean = false; }
            }
            catch { /* the analyzer must never break the build */ }
        }
        return (parsedClean, hasError);
    }

    /// <summary>True when the toolchain does the PostgreSQL-correct thing for this case.</summary>
    public static bool Passes(CorpusCase c)
    {
        var (parsedClean, hasError) = Evaluate(c.Sql);
        return c.Expect == "ok" ? parsedClean : hasError;
    }
}
