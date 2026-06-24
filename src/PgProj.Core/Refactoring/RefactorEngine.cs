using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Refactoring;

/// <summary>The outcome of a refactor command: the log entry recorded and the .sql edits made.</summary>
public sealed record RefactorResult(RefactorEntry Entry, IReadOnlyList<string> ChangedFiles, int Replacements);

/// <summary>
/// Performs SSDT-style refactors (#136): rewrites the project's <c>.sql</c> source AND appends to the
/// committed <c>.pgrefactorlog</c> so the next deploy emits a data-preserving <c>ALTER</c> rather than
/// DROP+CREATE. The <c>.sql</c> rewrite is a word-boundary replacement of the fully-qualified object name
/// (which matches the definition and qualified references — FK <c>REFERENCES</c>, view <c>FROM</c>, …),
/// leaving unqualified mentions for the author to review. Object-level only (table rename / schema move);
/// column-rename authoring rewrites are a follow-up (the deploy planner already consumes logged column
/// renames).
/// </summary>
public static class RefactorEngine
{
    /// <summary>Rename a table within its schema: rewrite <c>schema.old</c> → <c>schema.new</c> and log it.</summary>
    public static RefactorResult RenameTable(DatabaseProject project, string oldQualified, string newName)
    {
        var (schema, oldName) = RequireQualified(oldQualified);
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('.'))
            throw new RefactorException($"The new name must be a bare identifier (got '{newName}').");

        var newQualified = $"{schema}.{newName}";
        var (changed, count) = RewriteQualifiedName(project, oldQualified, newQualified);
        if (count == 0)
            throw new RefactorException($"No occurrence of '{oldQualified}' found in the project's .sql files.");

        var entry = new RefactorEntry(RefactorLog.OpRename, RefactorLog.TypeTable, oldQualified, newQualified);
        AppendLog(project, entry);
        return new RefactorResult(entry, changed, count);
    }

    /// <summary>Move a table to another schema: rewrite <c>oldSchema.name</c> → <c>newSchema.name</c> and log it.</summary>
    public static RefactorResult MoveTableToSchema(DatabaseProject project, string oldQualified, string newSchema)
    {
        var (oldSchema, name) = RequireQualified(oldQualified);
        if (string.IsNullOrWhiteSpace(newSchema) || newSchema.Contains('.'))
            throw new RefactorException($"The new schema must be a bare identifier (got '{newSchema}').");

        var newQualified = $"{newSchema}.{name}";
        var (changed, count) = RewriteQualifiedName(project, oldQualified, newQualified);
        if (count == 0)
            throw new RefactorException($"No occurrence of '{oldQualified}' found in the project's .sql files.");

        var entry = new RefactorEntry(RefactorLog.OpMoveSchema, RefactorLog.TypeTable, oldQualified, newQualified);
        AppendLog(project, entry);
        return new RefactorResult(entry, changed, count);
    }

    /// <summary>
    /// Expand <c>SELECT *</c> (and <c>alias.*</c>) in a view's projection to an explicit column list, resolved
    /// from the semantic model, rewriting the <c>.sql</c> source in place and logging the operation. Mirrors
    /// SSDT's "Expand Wildcards" refactor. Only the star tokens are rewritten — the rest of the file stays
    /// byte-identical. Supported shape: a plain top-level <c>SELECT</c> over table sources; WITH/UNION/
    /// subquery-or-function sources raise a clear <see cref="RefactorException"/>.
    /// </summary>
    public static RefactorResult ExpandWildcards(DatabaseProject project, string viewQualified)
    {
        var (schema, name) = RequireQualified(viewQualified);

        var build = project.BuildAsync().GetAwaiter().GetResult();
        var view = build.Model.Views.FirstOrDefault(v =>
            DatabaseModel.NameEquals(v.Schema, schema) && DatabaseModel.NameEquals(v.Name, name));
        if (view is null)
            throw new RefactorException($"No view '{viewQualified}' found in the project (only views can be expanded).");

        var sources = ResolveSources(view, build.Model);

        var changed = new List<string>();
        var total = 0;
        foreach (var file in project.ResolveSqlFiles())
        {
            var text = File.ReadAllText(file);
            var (rewritten, count) = WildcardExpander.Rewrite(text, schema, name, sources);
            if (count > 0)
            {
                File.WriteAllText(file, rewritten, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                changed.Add(file);
                total += count;
            }
        }
        if (total == 0)
            throw new RefactorException($"Could not locate the definition of view '{viewQualified}' to rewrite.");

        var entry = new RefactorEntry(RefactorLog.OpExpandWildcards, RefactorLog.TypeView, viewQualified, viewQualified);
        AppendLog(project, entry);
        return new RefactorResult(entry, changed, total);
    }

    /// <summary>
    /// Resolve the view body's FROM sources (each TableRef and its joins) to (visible-name, ordered-columns)
    /// using the model's tables. Only table sources are resolvable; a subquery/function/unknown source yields
    /// no columns — a star that needs it then fails loudly in <see cref="WildcardExpander"/>.
    /// </summary>
    private static IReadOnlyList<WildcardExpander.Source> ResolveSources(ViewDefinition view, DatabaseModel model)
    {
        ParseResult parsed;
        try { parsed = new PgParser().Parse(view.Body); }
        catch (Exception ex) { throw new RefactorException($"Could not parse the body of view '{view.Schema}.{view.Name}': {ex.Message}"); }

        var query = parsed.Statements.OfType<QueryStatement>().FirstOrDefault()?.Query;
        if (query is null || query.SetOp is not null || query.From is null)
            throw new RefactorException($"View '{view.Schema}.{view.Name}' is not a plain single SELECT over a FROM clause (expand-wildcards does not support WITH/UNION/FROM-less views).");

        var sources = new List<WildcardExpander.Source>();
        foreach (var rel in query.From.Relations)
        {
            AddSource(sources, rel, model);
            foreach (var join in rel.Joins) AddSource(sources, join.Right, model);
        }
        return sources;
    }

    private static void AddSource(List<WildcardExpander.Source> sources, TableRef rel, DatabaseModel model)
    {
        var visible = rel.Alias ?? rel.TableName;
        if (string.IsNullOrEmpty(visible)) return;                       // unnamed subquery — unresolvable

        // Only relation refs carry resolvable columns (a subquery/function source has TableName == null).
        if (rel.TableName is null || rel.Subquery is not null || rel.Function is not null)
        {
            sources.Add(new WildcardExpander.Source(visible, Array.Empty<string>()));
            return;
        }

        var candidates = model.Tables.Where(t => DatabaseModel.NameEquals(t.Name, rel.TableName!)
            && (rel.Schema is null || DatabaseModel.NameEquals(t.Schema, rel.Schema))).ToList();
        var cols = candidates.Count == 1 ? candidates[0].Columns.Select(c => c.Name).ToList() : new List<string>();
        sources.Add(new WildcardExpander.Source(visible, cols));
    }

    /// <summary>
    /// Replace every word-boundaried occurrence of the unquoted qualified name <paramref name="oldQualified"/>
    /// across the project's .sql files with <paramref name="newQualified"/>. Returns the changed files and the
    /// total replacement count. Files are read/written as UTF-8 (no BOM).
    /// </summary>
    public static (IReadOnlyList<string> ChangedFiles, int Replacements) RewriteQualifiedName(
        DatabaseProject project, string oldQualified, string newQualified)
    {
        var (schema, name) = RequireQualified(oldQualified);
        // schema . name as a whole token: not bordered by an identifier char, a dot, or a quote.
        var pattern = $@"(?<![\w"".]){Regex.Escape(schema)}\s*\.\s*{Regex.Escape(name)}(?![\w"".])";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);

        var changed = new List<string>();
        var total = 0;
        foreach (var file in project.ResolveSqlFiles())
        {
            var text = File.ReadAllText(file);
            var replaced = rx.Replace(text, _ => { total++; return newQualified; });
            if (!ReferenceEquals(replaced, text) && replaced != text)
            {
                File.WriteAllText(file, replaced, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                changed.Add(file);
            }
        }
        return (changed, total);
    }

    private static void AppendLog(DatabaseProject project, RefactorEntry entry)
    {
        var path = RefactorLog.PathFor(project.ProjectFilePath);
        RefactorLog.Load(path).Append(entry).Save(path);
    }

    private static (string Schema, string Name) RequireQualified(string qualified)
    {
        var dot = qualified?.IndexOf('.') ?? -1;
        if (qualified is null || dot <= 0 || dot == qualified.Length - 1)
            throw new RefactorException($"Expected a schema-qualified name 'schema.name' (got '{qualified}').");
        return (qualified[..dot], qualified[(dot + 1)..]);
    }
}

/// <summary>Thrown when a refactor command's arguments are invalid or its target object is not found.</summary>
public sealed class RefactorException : Exception
{
    public RefactorException(string message) : base(message) { }
}
