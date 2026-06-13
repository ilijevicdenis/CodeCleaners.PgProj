using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Project;

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
