using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Parsing;

namespace PgProj.Core.Tests;

/// <summary>How the parser handled a statement.</summary>
public enum CorpusVerdict { Parsed, Error, Empty }

/// <summary>One corpus case (mirrors the JSONL schema in tests/corpus/CORPUS.md).</summary>
public sealed record CorpusCase(string Id, string Category, string Sql, string Expect,
                                string? Ref, string? Note, string? Txn);

/// <summary>
/// Shared access to the PostgreSQL test corpus (tests/corpus/*.jsonl) and the parser verdict.
/// Used by the per-case generated tests, the aggregate CorpusTests, and the test generator.
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
                catch (JsonException ex)
                {
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: invalid JSON: {ex.Message}");
                }
                if (c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Sql)
                    || string.IsNullOrWhiteSpace(c.Expect))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: case needs id, sql, expect");
                if (c.Expect is not ("ok" or "error"))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: expect must be ok|error (got {c.Expect})");
                cases.Add(c);
            }
        }
        return cases;
    }

    /// <summary>Legacy-parser 3-way classification (used by the informational coverage report).</summary>
    public static CorpusVerdict Verdict(string sql)
    {
        var p = new AstParser();
        var script = p.Parse(sql);
        if (p.Diagnostics.Count > 0) return CorpusVerdict.Error;
        return script.Statements.Count > 0 ? CorpusVerdict.Parsed : CorpusVerdict.Empty;
    }

    /// <summary>
    /// True when the active parser already does the right thing for this case — mirrors CorpusAssert:
    /// the new PgParser is authoritative for kinds it owns, otherwise the legacy parser decides.
    /// </summary>
    public static bool Passes(CorpusCase c)
    {
        var res = new Syntax.PgParser().Parse(c.Sql);
        bool parsedClean, hasError;
        if (res.FullyRecognized)
        {
            parsedClean = res.Diagnostics.Count == 0 && res.Statements.Count > 0;
            hasError = res.Diagnostics.Count > 0;
        }
        else
        {
            var p = new AstParser();
            var script = p.Parse(c.Sql);
            parsedClean = p.Diagnostics.Count == 0 && script.Statements.Count > 0;
            hasError = p.Diagnostics.Count > 0;
        }
        return c.Expect == "ok" ? parsedClean : hasError;
    }
}
