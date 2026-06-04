using System;
using System.Collections.Generic;
using System.IO;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Contracts;

/// <summary>A resolved source anchor: project-relative file plus 1-based line/column.</summary>
public readonly record struct SourcePosition(string File, int Line, int Col);

/// <summary>
/// Maps a model object's stable identity to where its CREATE statement lives in the project sources,
/// so the model-tree and diagnostics can carry file:line:col without threading positions through the
/// (perf-sensitive, shared) model layer. Built by re-parsing each .sql file and reading the statement
/// <see cref="SqlStatement.Position"/> offset — the same parser the build uses, so identities line up.
/// </summary>
public sealed class SourcePositionIndex
{
    private readonly Dictionary<string, SourcePosition> _byIdentity = new(StringComparer.OrdinalIgnoreCase);

    private SourcePositionIndex() { }

    /// <summary>Builds the index for a project (one pass over its resolved .sql files).</summary>
    public static SourcePositionIndex Build(DatabaseProject project)
    {
        var idx = new SourcePositionIndex();
        foreach (var file in project.ResolveSqlFiles())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; } // unreadable file → simply contributes no positions
            var rel = Path.GetRelativePath(project.ProjectDirectory, file).Replace('\\', '/');
            var parsed = new PgParser().Parse(text);
            foreach (var stmt in parsed.Statements)
            {
                var (line, col) = LineCol(text, stmt.Position);
                var pos = new SourcePosition(rel, line, col);
                var key = IdentityOf(stmt, project.DefaultSchema);
                if (key is not null)
                {
                    // First occurrence wins, mirroring the build's first-definition-wins merge.
                    if (!idx._byIdentity.ContainsKey(key)) idx._byIdentity[key] = pos;
                }
                else if (stmt is RawCreateStatement raw && raw.Name is not null)
                {
                    idx.AddRawByName(raw.Schema ?? "", raw.Name, pos);
                    if (!string.IsNullOrEmpty(raw.Schema)) idx.AddRawByName("", raw.Name, pos);
                }
            }
        }
        return idx;
    }

    /// <summary>Look up a position by the object identity produced by <see cref="ModelTreeBuilder"/>.</summary>
    public SourcePosition? Find(string identity) =>
        _byIdentity.TryGetValue(identity, out var p) ? p : null;

    /// <summary>Look up a raw object by schema+name (trigger/type/policy/…), then by bare name.</summary>
    public SourcePosition? FindRaw(string schema, string name)
    {
        if (_byIdentity.TryGetValue($"raw:{schema}.{name}".ToLowerInvariant(), out var p)) return p;
        if (_byIdentity.TryGetValue($"raw:.{name}".ToLowerInvariant(), out var q)) return q;
        return null;
    }

    // ---- statement → identity (must match the keys ModelTreeBuilder computes) --------------------

    internal static string? IdentityOf(SqlStatement stmt, string defaultSchema)
    {
        string Sch(string? s) => string.IsNullOrEmpty(s) ? defaultSchema : s!;
        return stmt switch
        {
            CreateSchemaStatement s when s.Name is not null => $"schema:{s.Name}".ToLowerInvariant(),
            CreateTableStatement s => $"table:{Sch(s.Schema)}.{s.Name}".ToLowerInvariant(),
            CreateViewStatement s => $"view:{Sch(s.Schema)}.{s.Name}".ToLowerInvariant(),
            CreateSequenceStatement s => $"sequence:{Sch(s.Schema)}.{s.Name}".ToLowerInvariant(),
            CreateIndexStatement s => $"index:{Sch(s.Schema)}.{s.Name ?? $"{s.Table}_idx"}".ToLowerInvariant(),
            CreateFunctionStatement s => $"function:{Sch(s.Schema)}.{s.Name}({s.ArgTypes})".ToLowerInvariant(),
            _ => null, // raw objects are anchored by name below (looked up leniently)
        };
    }

    /// <summary>Adds a lenient by-name fallback key so raw objects (trigger/type/policy/…) can resolve.</summary>
    internal void AddRawByName(string schema, string name, SourcePosition pos)
    {
        var key = $"raw:{schema}.{name}".ToLowerInvariant();
        if (!_byIdentity.ContainsKey(key)) _byIdentity[key] = pos;
    }

    /// <summary>Translate a 0-based character offset into 1-based (line, column).</summary>
    internal static (int Line, int Col) LineCol(string text, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;
        int line = 1, col = 1;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}
