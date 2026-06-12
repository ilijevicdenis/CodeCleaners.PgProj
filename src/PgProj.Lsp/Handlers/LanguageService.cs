using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Diagnostics;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Project.References;
using PgProj.Core.Syntax;
using PgProj.Lsp.Protocol;
using PgProj.Lsp.Workspace;
using Diagnostic = PgProj.Core.Diagnostics.Diagnostic;

namespace PgProj.Lsp.Handlers;

/// <summary>
/// The PURE language-service core: open documents + (optional) workspace project → diagnostics / definition
/// / hover / completion. It has NO transport, NO stdin/stdout, NO timers — every method takes a snapshot and
/// returns a result — so it is driven directly from synthetic payloads in unit tests without spawning a
/// process. The thin STDIO loop (<see cref="Server.LspServer"/>) and the debounce scheduler call into here.
///
/// Diagnostics reuse the EXACT engine path: when a <c>.pgproj</c> is present we run
/// <see cref="DatabaseProject.BuildAsync"/> with an open-buffer overlay and project the build's
/// <see cref="ProjectBuildResult.UnifiedDiagnostics"/> (so the verdict — ruleId/severity/line/col, including
/// duplicate-definition findings — is identical to <c>pgproj build</c>). With no project (a loose buffer),
/// we run the same single-file pieces the build runs (<see cref="PgParser"/> + <see cref="ModelBuilder"/>),
/// so a parser reject here is the same reject the batch path would produce.
/// Definition/hover/completion are backed by the model tree (<see cref="ModelTreeBuilder"/> over the built
/// model, carrying <see cref="SourcePositionIndex"/> anchors).
/// </summary>
public sealed class LanguageService
{
    private readonly DocumentStore _store;
    private readonly string? _projectFilePath;

    public LanguageService(DocumentStore store, string? projectFilePath = null)
    {
        _store = store;
        _projectFilePath = projectFilePath;
    }

    public DocumentStore Documents => _store;
    public string? ProjectFilePath => _projectFilePath;

    // ---- diagnostics ---------------------------------------------------------------------

    /// <summary>
    /// Computes the diagnostics to publish for <paramref name="uri"/>. Returns the LSP-shaped findings AND the
    /// document version they were computed against (so a stale publish can be dropped). Findings anchored at an
    /// unknown position (line 0) are placed at the top of the file so the editor still surfaces them.
    /// </summary>
    public async Task<DiagnosticsResult> DiagnoseAsync(string uri, CancellationToken ct = default)
    {
        var doc = _store.Get(uri);
        if (doc is null) return new DiagnosticsResult(uri, null, Array.Empty<LspDiagnostic>());

        var version = doc.Version;
        var engineDiags = await ComputeEngineDiagnosticsForFileAsync(doc, ct).ConfigureAwait(false);
        var lines = doc.Lines;
        var lsp = engineDiags.Select(d => ToLsp(d, lines)).ToList();
        return new DiagnosticsResult(uri, version, lsp);
    }

    /// <summary>
    /// The engine diagnostics that pertain to one open file. With a workspace project we build the whole
    /// project (overlaying open buffers) and keep the findings whose file anchor is this document (plus any
    /// file-less build findings). Without a project we parse the single buffer exactly as the build's per-file
    /// loop does. This is the one method that guarantees live-vs-batch agreement.
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>> ComputeEngineDiagnosticsForFileAsync(LiveDocument doc, CancellationToken ct)
    {
        if (_projectFilePath is not null && File.Exists(_projectFilePath))
        {
            var project = WorkspaceProject.LoadWithOverlay(_projectFilePath, _store);
            var result = await project.BuildAsync(ct).ConfigureAwait(false);
            var rel = RelativePathOf(project, doc.Uri);
            var diags = result.UnifiedDiagnostics
                .Where(d => d.File is null || PathEquals(d.File, rel))
                .ToList();

            // The reference/semantic gate `pgproj build` runs AFTER the model build (unresolved
            // relations, bad columns, type errors). It is not part of UnifiedDiagnostics, so without
            // this the live verdict misses exactly the findings SSDT users expect as they type.
            // The validator reads through ReadEffectiveText, so the open-buffer overlay applies.
            var resolution = new ReferenceResolver().Resolve(project);
            foreach (var rd in resolution.Diagnostics)
            {
                // Same severity split as the CLI gate: a not-yet-restored PackageReference is a
                // documented follow-up (warning); every other resolution failure is an error.
                var severity = rd.Code == ReferenceErrorCodes.PackageRestoreNotImplemented
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error;
                diags.Add(new Diagnostic { Severity = severity, Code = rd.Code, Message = rd.Message });
            }
            foreach (var rv in ReferenceValidator.Validate(project, resolution))
            {
                if (!PathEquals(rv.RelativePath, rel)) continue;
                diags.Add(Diagnostic.FromSemantic(rv.Message, rv.RelativePath, rv.Line, rv.Column));
            }
            return diags;
        }

        // Loose buffer: mirror the build's single-file path (parser diagnostics + a single-file dup scan).
        return SingleFileDiagnostics(doc.Text);
    }

    /// <summary>
    /// The build's per-file work for one buffer: parser diagnostics mapped via <see cref="Diagnostic.FromParser"/>,
    /// plus the same duplicate-definition scan the build runs after the merge (here over a one-file model). The
    /// file anchor is left null (a loose buffer has no project-relative path) — the caller anchors it.
    /// </summary>
    public static IReadOnlyList<Diagnostic> SingleFileDiagnostics(string text)
    {
        var diags = new List<Diagnostic>();
        var parsed = new PgParser().Parse(text);
        foreach (var d in parsed.Diagnostics)
            diags.Add(Diagnostic.FromParser(d.Message, null, d.Line, d.Column));

        var model = new DatabaseModel();
        try { new ModelBuilder().Build(parsed, model); } catch { /* parse already reported the problem */ }
        diags.AddRange(DuplicateDiagnostics(model));
        return diags;
    }

    /// <summary>The build's post-merge duplicate scan, reproduced (the engine's copy is private to the build).</summary>
    private static IEnumerable<Diagnostic> DuplicateDiagnostics(DatabaseModel model)
    {
        foreach (var g in model.Tables.GroupBy(t => $"{t.Schema}.{t.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return Diagnostic.FromBuild($"Duplicate table definition: {g.Key} (defined {g.Count()} times).");
        foreach (var g in model.Views.GroupBy(v => $"{v.Schema}.{v.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return Diagnostic.FromBuild($"Duplicate view definition: {g.Key} (defined {g.Count()} times).");
        foreach (var g in model.Functions.GroupBy(f => f.Signature.ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return Diagnostic.FromBuild($"Duplicate function definition: {g.Key} (defined {g.Count()} times).");
        foreach (var g in model.Indexes.GroupBy(i => $"{i.Schema}.{i.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return Diagnostic.FromBuild($"Duplicate index definition: {g.Key} (defined {g.Count()} times).");
    }

    private static LspDiagnostic ToLsp(Diagnostic d, LineIndex lines)
    {
        // Engine line/col are 1-based, 0 when unknown → place unknown anchors at the top of the file.
        var line0 = d.Line > 0 ? d.Line - 1 : 0;
        var startOffset = lines.OffsetOfOneBased(d.Line > 0 ? d.Line : 1, d.Column > 0 ? d.Column : 1);
        var word = lines.WordAt(startOffset);
        var (sl, sc) = lines.PositionOf(word.Start);
        var (el, ec) = word.End > word.Start ? lines.PositionOf(word.End) : (sl, sc + 1);
        var range = word.End > word.Start
            ? new Protocol.Range(new Position(sl, sc), new Position(el, ec))
            : new Protocol.Range(new Position(line0, 0), new Position(line0, 1));

        return new LspDiagnostic
        {
            Range = range,
            Severity = d.Severity switch
            {
                DiagnosticSeverity.Error => LspSeverity.Error,
                DiagnosticSeverity.Warning => LspSeverity.Warning,
                _ => LspSeverity.Information,
            },
            Code = d.Code,
            Message = d.Message,
        };
    }

    // ---- definition / hover / completion (model-tree backed) -----------------------------

    /// <summary>
    /// Go-to-definition, caret-segment aware. The cursor's dotted chain is split and resolution
    /// follows WHICH segment the caret is on:
    ///   * on an alias (<c>o</c> in <c>FROM sales.orders o … o.id</c>) → the aliased relation's CREATE;
    ///   * on a relation (<c>orders</c> in <c>sales.orders</c>) → that relation's CREATE;
    ///   * on a COLUMN qualified by either (<c>customer_id</c> in <c>o.customer_id</c> /
    ///     <c>sales.orders.customer_id</c>) → the column's own line INSIDE the CREATE TABLE.
    /// </summary>
    public async Task<Location?> DefinitionAsync(string uri, Position position, CancellationToken ct = default)
    {
        var doc = _store.Get(uri);
        if (doc is null) return null;
        var tree = await BuildModelTreeAsync(ct).ConfigureAwait(false);
        if (tree is null) return null;

        var offset = doc.Lines.OffsetOf(position.Line, position.Character);
        var w = doc.Lines.WordAt(offset);
        if (string.IsNullOrWhiteSpace(w.Word)) return null;
        var chain = w.Word.Trim().TrimEnd('.');
        var segments = chain.Split('.');

        // which segment is the caret on?
        var caretSeg = segments.Length - 1;
        var segStart = w.Start;
        for (var i = 0; i < segments.Length; i++)
        {
            if (offset <= segStart + segments[i].Length) { caretSeg = i; break; }
            segStart += segments[i].Length + 1;
        }

        // 1) the chain UP TO the caret as a relation/object (alias first when the caret is on it)
        var caretPrefix = string.Join(".", segments.Take(caretSeg + 1));
        var node = caretSeg == 0 ? ResolveAliasNode(tree, doc.Text, segments[0]) : null;
        node ??= ResolveNode(tree, caretPrefix, defaultSchemaOf(tree));
        if (node?.File is not null && node.Line > 0)
            return LocationOf(node);

        // 2) caret on a column segment: the chain BEFORE it names the relation (alias or qualified)
        if (caretSeg > 0)
        {
            var relPrefix = string.Join(".", segments.Take(caretSeg));
            var rel = ResolveAliasNode(tree, doc.Text, segments[0]);
            if (rel is null || caretSeg > 1) rel = ResolveNode(tree, relPrefix, defaultSchemaOf(tree)) ?? rel;
            var column = segments[caretSeg];
            if (rel is not null && rel.Children.Any(c => c.Kind == "column" && NameEq(c.Name, column)))
                return ColumnLocation(rel, column) ?? (rel.File is not null && rel.Line > 0 ? LocationOf(rel) : null);
        }

        return null;
    }

    private Location LocationOf(ModelTreeNodeDto node)
    {
        var pos = new Position(Math.Max(0, node.Line - 1), Math.Max(0, node.Col - 1));
        return new Location(ResolveNodeUri(node), new Protocol.Range(pos, pos));
    }

    /// <summary>
    /// The column's own line inside its relation's CREATE statement: scan the defining file's
    /// EFFECTIVE text (open buffer wins over disk) for the first identifier occurrence of the
    /// column name at/after the relation's definition anchor.
    /// </summary>
    private Location? ColumnLocation(ModelTreeNodeDto relation, string column)
    {
        if (relation.File is null || relation.Line <= 0) return null;
        var targetUri = ResolveNodeUri(relation);
        var text = _store.Get(targetUri)?.Text;
        if (text is null)
        {
            try { text = File.ReadAllText(DocumentUri.ToPath(targetUri)); }
            catch { return null; }
        }

        var lines = new LineIndex(text);
        var defOffset = lines.OffsetOfOneBased(relation.Line, Math.Max(1, relation.Col));
        foreach (var (start, end) in IdentifierOccurrences(text, column))
        {
            if (start < defOffset) continue;
            var (sl, sc) = lines.PositionOf(start);
            var (el, ec) = lines.PositionOf(end);
            return new Location(targetUri, new Protocol.Range(new Position(sl, sc), new Position(el, ec)));
        }
        return null;
    }

    /// <summary>
    /// Find-all-references: resolve the identifier at the cursor to a model object, then scan every
    /// project file (open buffers take precedence over disk) for identifier occurrences of its name.
    /// Matching is lexical-but-token-aware: plain and <c>"quoted"</c> identifiers match case-insensitively,
    /// line/block comments (nested, per PostgreSQL) and single-quoted string literals are skipped, and
    /// dollar-quoted bodies are scanned (function bodies are exactly where references live). When
    /// <paramref name="includeDeclaration"/> is false the occurrence on the object's defining line is dropped.
    /// </summary>
    public async Task<IReadOnlyList<Location>> ReferencesAsync(string uri, Position position, bool includeDeclaration = true, CancellationToken ct = default)
    {
        var doc = _store.Get(uri);
        if (doc is null) return Array.Empty<Location>();
        var tree = await BuildModelTreeAsync(ct).ConfigureAwait(false);
        if (tree is null) return Array.Empty<Location>();

        var word = WordUnder(doc, position);
        var node = ResolveNode(tree, word, defaultSchemaOf(tree));
        if (node is null || string.IsNullOrEmpty(node.Name)) return Array.Empty<Location>();

        var locations = new List<Location>();
        foreach (var (fileUri, text, relPath) in EnumerateWorkspaceTexts())
        {
            ct.ThrowIfCancellationRequested();
            LineIndex? lines = null;
            foreach (var (start, end) in IdentifierOccurrences(text, node.Name))
            {
                lines ??= new LineIndex(text);
                var (sl, sc) = lines.PositionOf(start);
                if (!includeDeclaration && node.File is not null && node.Line > 0
                    && PathEquals(relPath ?? "", node.File) && sl == node.Line - 1)
                    continue;
                var (el, ec) = lines.PositionOf(end);
                locations.Add(new Location(fileUri, new Protocol.Range(new Position(sl, sc), new Position(el, ec))));
            }
        }
        return locations;
    }

    /// <summary>
    /// Every text in scope for a workspace-wide scan: with a project, its resolved .sql files (an open
    /// buffer's live text wins over disk); without one, just the open buffers. Yields the document URI,
    /// the text, and the project-relative path (null in loose mode).
    /// </summary>
    private IEnumerable<(string Uri, string Text, string? RelativePath)> EnumerateWorkspaceTexts()
    {
        if (_projectFilePath is not null && File.Exists(_projectFilePath))
        {
            var project = DatabaseProject.Load(_projectFilePath);
            foreach (var abs in project.ResolveSqlFiles())
            {
                var fileUri = DocumentUri.FromPath(abs);
                var text = _store.Get(fileUri)?.Text;
                if (text is null)
                {
                    try { text = File.ReadAllText(abs); }
                    catch { continue; } // unreadable file → not a reference source
                }
                yield return (fileUri, text, Path.GetRelativePath(project.ProjectDirectory, abs).Replace('\\', '/'));
            }
            yield break;
        }

        foreach (var d in _store.All)
            yield return (d.Uri, d.Text, null);
    }

    /// <summary>
    /// Yields the [start, end) spans where <paramref name="name"/> occurs as a whole identifier — bare
    /// (<c>name</c>) or quoted (<c>"name"</c>), case-insensitive — skipping <c>--</c> line comments,
    /// nested <c>/* */</c> block comments, and single-quoted literals (with <c>''</c> escapes).
    /// </summary>
    internal static IEnumerable<(int Start, int End)> IdentifierOccurrences(string text, string name)
    {
        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { i += 2; continue; } // '' escape
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '"')
            {
                var start = i + 1;
                var j = start;
                while (j < text.Length && text[j] != '"') j++;
                if (j < text.Length)
                {
                    if (j - start == name.Length && string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                        yield return (start, j);
                    i = j + 1;
                    continue;
                }
                i = j;
                continue;
            }
            if (IsIdent(c))
            {
                var start = i;
                while (i < text.Length && IsIdent(text[i])) i++;
                if (i - start == name.Length && string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    yield return (start, i);
                continue;
            }
            i++;
        }
    }

    /// <summary>Hover: a markdown card describing the object the cursor's identifier resolves to.</summary>
    public async Task<Hover?> HoverAsync(string uri, Position position, CancellationToken ct = default)
    {
        var doc = _store.Get(uri);
        if (doc is null) return null;
        var tree = await BuildModelTreeAsync(ct).ConfigureAwait(false);
        if (tree is null) return null;

        var word = WordUnder(doc, position);
        var node = ResolveAliasNode(tree, doc.Text, word) ?? ResolveNode(tree, word, defaultSchemaOf(tree));
        if (node is null) return null;

        var where = node.File is not null && node.Line > 0 ? $"\n\n_Defined in `{node.File}:{node.Line}`_" : "";
        var value = $"**{node.Kind}** `{node.QualifiedName}`{where}";
        return new Hover { Contents = new MarkupContent { Value = value } };
    }

    /// <summary>
    /// Completion from the project model. After a <c>schema.</c> or <c>table.</c> dotted prefix we list that
    /// container's members (a schema's objects, a table's columns); otherwise we list every top-level object
    /// (schemas, tables, views, sequences, functions, raw objects) plus a small set of SQL keywords.
    /// </summary>
    public async Task<CompletionList> CompletionAsync(string uri, Position position, CancellationToken ct = default)
    {
        var doc = _store.Get(uri);
        if (doc is null) return new CompletionList();
        var tree = await BuildModelTreeAsync(ct).ConfigureAwait(false);
        if (tree is null) return new CompletionList();

        var prefix = DottedPrefixBefore(doc, position);
        var items = new List<CompletionItem>();

        if (prefix is { } container)
        {
            // schema.  → that schema's objects;  table.  → that table's columns.
            foreach (var n in tree.Nodes.Where(n => NameEq(n.Schema, container)))
                items.Add(ItemFor(n));
            foreach (var t in tree.Nodes.Where(n => n.Kind == "table" && (NameEq(n.Name, container) || NameEq(n.QualifiedName, container))))
                foreach (var c in t.Children.Where(c => c.Kind == "column"))
                    items.Add(new CompletionItem { Label = c.Name, Kind = CompletionItemKind.Field, Detail = c.QualifiedName });

            // alias.  → the aliased relation's columns (FROM sales.orders o … o.<here>)
            if (items.Count == 0 && ResolveAliasNode(tree, doc.Text, container) is { } aliased)
                foreach (var c in aliased.Children.Where(c => c.Kind == "column"))
                    items.Add(new CompletionItem { Label = c.Name, Kind = CompletionItemKind.Field, Detail = c.QualifiedName });

            return new CompletionList { Items = Dedupe(items) };
        }

        foreach (var n in tree.Nodes) items.Add(ItemFor(n));
        foreach (var kw in Keywords) items.Add(new CompletionItem { Label = kw, Kind = CompletionItemKind.Keyword });
        return new CompletionList { Items = Dedupe(items) };
    }

    // ---- model-tree helpers --------------------------------------------------------------

    private async Task<ModelTreeDto?> BuildModelTreeAsync(CancellationToken ct)
    {
        if (_projectFilePath is not null && File.Exists(_projectFilePath))
        {
            var project = WorkspaceProject.LoadWithOverlay(_projectFilePath, _store);
            var result = await project.BuildAsync(ct).ConfigureAwait(false);
            return ModelTreeBuilder.Build(result.Model, project.Name, result.Positions);
        }

        // Loose buffers: union every open document into one in-memory model so completion/definition still work.
        var model = new DatabaseModel();
        var positions = new SourcePositionIndex();
        foreach (var d in _store.All)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = new PgParser().Parse(d.Text);
            var rel = DocumentUri.ToPath(d.Uri);
            try { new ModelBuilder().Build(parsed, model, positions, d.Text, rel); } catch { /* keep going */ }
        }
        return ModelTreeBuilder.Build(model, "(workspace)", positions);
    }

    private static string defaultSchemaOf(ModelTreeDto _) => "public";

    // ---- query-alias resolution ------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="word"/> as a QUERY ALIAS declared anywhere in the document
    /// (<c>FROM sales.orders o</c>, JOINs, UPDATE/DELETE targets, CTE bodies, view bodies) to the
    /// aliased relation's model node — the basis for alias completion, definition and hover.
    /// </summary>
    private static ModelTreeNodeDto? ResolveAliasNode(ModelTreeDto tree, string documentText, string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        word = word.Trim().TrimEnd('.');
        var aliases = CollectAliases(documentText);
        // WordAt returns the whole dotted chain ("o.customer_id") — the alias is its first segment.
        if (!aliases.TryGetValue(word, out var target))
        {
            var dot = word.IndexOf('.');
            if (dot <= 0 || !aliases.TryGetValue(word[..dot], out target)) return null;
        }
        return tree.Nodes.FirstOrDefault(n =>
            (n.Kind is "table" or "view" or "materializedView")
            && NameEq(n.Name, target.Table)
            && (target.Schema is null || NameEq(n.Schema, target.Schema)));
    }

    /// <summary>
    /// Every <c>alias → relation</c> pair declared in the document: the parser-backed walk over
    /// well-formed statements, UNIONED with a lexical scan — the statement being typed right now
    /// is usually incomplete and unparsable, and that is exactly when alias completion matters.
    /// Parser results win on conflicts.
    /// </summary>
    internal static Dictionary<string, (string? Schema, string Table)> CollectAliases(string text)
    {
        var map = new Dictionary<string, (string?, string)>(StringComparer.OrdinalIgnoreCase);

        // lexical first (lowest fidelity): FROM/JOIN/UPDATE/INTO/USING <relation> [AS] <alias>
        foreach (System.Text.RegularExpressions.Match m in AliasPattern.Matches(text))
        {
            var alias = m.Groups["alias"].Value;
            if (NotAnAliasKeyword.Contains(alias)) continue;
            var rel = m.Groups["rel"].Value;
            var dot = rel.IndexOf('.');
            map[alias] = dot > 0 ? (rel[..dot], rel[(dot + 1)..]) : (null, rel);
        }

        try
        {
            var parsed = new PgParser().Parse(text);
            foreach (var stmt in parsed.Statements) CollectAliasesFromStatement(stmt, map);
        }
        catch { /* mid-typing text → the lexical pass already contributed what it could */ }
        return map;
    }

    private static readonly System.Text.RegularExpressions.Regex AliasPattern = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO|USING)\s+(?<rel>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> NotAnAliasKeyword = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "join", "inner", "left", "right", "full", "cross", "natural", "lateral",
        "group", "order", "having", "limit", "offset", "union", "intersect", "except", "set",
        "using", "returning", "as", "values", "when", "then", "and", "or", "not",
    };

    private static void CollectAliasesFromStatement(SqlStatement stmt, Dictionary<string, (string?, string)> map)
    {
        switch (stmt)
        {
            case QueryStatement q: CollectAliasesFromQuery(q.Query, map); break;
            case CreateTableAsStatement ctas: CollectAliasesFromQuery(ctas.Source, map); break;
            case CreateViewStatement v:
                try
                {
                    foreach (var s in new PgParser().Parse(v.BodyText).Statements)
                        CollectAliasesFromStatement(s, map);
                }
                catch { /* exotic body → no aliases from it */ }
                break;
            case InsertStatement ins:
                if (ins.Alias is { } ia && ins.Table is not null) map[ia] = (ins.Schema, ins.Table);
                CollectAliasesFromQuery(ins.Source, map);
                break;
            case UpdateStatement up:
                if (up.Alias is { } ua && up.Table is not null) map[ua] = (up.Schema, up.Table);
                CollectAliasesFromFrom(up.From, map);
                break;
            case DeleteStatement del:
                if (del.Alias is { } da && del.Table is not null) map[da] = (del.Schema, del.Table);
                CollectAliasesFromFrom(del.Using, map);
                break;
        }
    }

    private static void CollectAliasesFromQuery(SelectQuery? q, Dictionary<string, (string?, string)> map)
    {
        if (q is null) return;
        foreach (var cte in q.With) CollectAliasesFromQuery(cte.Query, map);
        if (q.SetOp is not null)
        {
            CollectAliasesFromQuery(q.SetOp.Left, map);
            CollectAliasesFromQuery(q.SetOp.Right, map);
        }
        CollectAliasesFromFrom(q.From, map);
    }

    private static void CollectAliasesFromFrom(FromClause? from, Dictionary<string, (string?, string)> map)
    {
        if (from is null) return;
        foreach (var rel in from.Relations)
        {
            CollectAliasesFromTableRef(rel, map);
            foreach (var j in rel.Joins) CollectAliasesFromTableRef(j.Right, map);
        }
    }

    private static void CollectAliasesFromTableRef(TableRef rel, Dictionary<string, (string?, string)> map)
    {
        if (rel.Subquery is not null)
        {
            CollectAliasesFromQuery(rel.Subquery, map);
            return;
        }
        if (rel.Alias is { } alias && rel.TableName is { } table)
            map[alias] = (rel.Schema, table);
    }

    /// <summary>Resolve a (possibly dotted) word to a model-tree node: exact qualified, then by bare name.</summary>
    private static ModelTreeNodeDto? ResolveNode(ModelTreeDto tree, string word, string defaultSchema)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        word = word.Trim().TrimEnd('.');

        // qualified match (schema.name or function signature label) first
        var byQualified = tree.Nodes.FirstOrDefault(n =>
            NameEq(n.QualifiedName, word) || NameEq($"{n.Schema}.{n.Name}", word));
        if (byQualified is not null) return byQualified;

        // schema.name where word may itself be schema.name; otherwise bare name (any schema, default first)
        var bare = word.Contains('.') ? word[(word.LastIndexOf('.') + 1)..] : word;
        return tree.Nodes
            .Where(n => NameEq(n.Name, bare))
            .OrderByDescending(n => NameEq(n.Schema, defaultSchema))
            .FirstOrDefault();
    }

    private string ResolveNodeUri(ModelTreeNodeDto node)
    {
        if (node.File is null) return "";
        if (_projectFilePath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_projectFilePath))!;
            return DocumentUri.FromPath(Path.Combine(dir, node.File));
        }
        // loose mode: node.File is already an absolute path
        return DocumentUri.FromPath(node.File);
    }

    private static CompletionItem ItemFor(ModelTreeNodeDto n) => new()
    {
        Label = n.Name,
        Kind = n.Kind switch
        {
            "schema" => CompletionItemKind.Module,
            "function" => CompletionItemKind.Function,
            "table" or "view" or "materializedView" => CompletionItemKind.Class,
            "type" or "domain" => CompletionItemKind.Struct,
            _ => CompletionItemKind.Field,
        },
        Detail = $"{n.Kind} {n.QualifiedName}",
    };

    private static IReadOnlyList<CompletionItem> Dedupe(IEnumerable<CompletionItem> items) =>
        items.GroupBy(i => (i.Label, i.Kind)).Select(g => g.First()).ToList();

    private static string WordUnder(LiveDocument doc, Position position)
    {
        var offset = doc.Lines.OffsetOf(position.Line, position.Character);
        return doc.Lines.WordAt(offset).Word;
    }

    /// <summary>The dotted container immediately before the cursor (the part before a trailing <c>.</c>), or null.</summary>
    private static string? DottedPrefixBefore(LiveDocument doc, Position position)
    {
        var offset = doc.Lines.OffsetOf(position.Line, position.Character);
        var text = doc.Text;
        var i = offset - 1;
        // skip the partial identifier currently being typed (after the dot)
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i--;
        if (i < 0 || text[i] != '.') return null;
        var end = i; // exclusive of the dot
        var s = end;
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        var container = text[s..end];
        return string.IsNullOrWhiteSpace(container) ? null : container;
    }

    private static string RelativePathOf(DatabaseProject project, string uri)
    {
        var abs = DocumentUri.ToPath(uri);
        return Path.GetRelativePath(project.ProjectDirectory, abs).Replace('\\', '/');
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] Keywords =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "VIEW",
        "INDEX", "FUNCTION", "SCHEMA", "FROM", "WHERE", "JOIN", "RETURNS", "PRIMARY KEY", "REFERENCES",
    };
}

/// <summary>The outcome of a diagnose pass: the URI, the document version it ran against, and the findings.</summary>
public sealed record DiagnosticsResult(string Uri, int? Version, IReadOnlyList<LspDiagnostic> Diagnostics);
