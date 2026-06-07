using System.Collections.Generic;
using System.IO;
using System.Linq;
using PgProj.Core.Semantics;
using PgProj.Core.Syntax;

namespace PgProj.Core.Project.References;

/// <summary>A semantic problem found while validating a project, attributed to a source location.</summary>
public sealed record ReferenceValidationDiagnostic(string RelativePath, int Line, int Column, string Message)
{
    public override string ToString() => $"{RelativePath}({Line},{Column}): {Message}";
}

/// <summary>
/// Cross-schema reference validation (EP-REF). Builds a semantic <see cref="Catalog"/> from the project's
/// own SQL UNION the external objects pulled in by referenced projects/artifacts, then runs the
/// <see cref="SemanticAnalyzer"/> over each statement so an unresolved relation in a managed schema is
/// reported as a build error with <c>file(line,col)</c>.
///
/// With the reference present, B's view over A's table resolves (A's schema is external-but-managed);
/// remove the reference and the same statement fails here — exactly the issue #16 success criterion.
/// External objects only WIDEN resolution; they never enter the comparer's model, so they are never emitted.
/// </summary>
public static class ReferenceValidator
{
    public static IReadOnlyList<ReferenceValidationDiagnostic> Validate(
        DatabaseProject project, ReferenceResolution resolution)
    {
        var diagnostics = new List<ReferenceValidationDiagnostic>();

        // Base catalog: every object the project itself defines (so intra-project refs resolve), plus the
        // external objects each reference contributes (so cross-schema refs into A resolve).
        var projectCatalog = new Catalog { DefaultSchema = project.DefaultSchema };
        var files = project.ResolveSqlFiles();
        var parsedByFile = new Dictionary<string, ParseResult>();

        foreach (var file in files)
        {
            var parsed = new PgParser().Parse(File.ReadAllText(file));
            parsedByFile[file] = parsed;
            foreach (var stmt in parsed.Statements)
                CatalogBuilder.Absorb(projectCatalog, stmt);
        }

        CatalogBuilder.AbsorbExternalModel(projectCatalog, resolution.ExternalModel);

        // EP-REF resolution semantics: a qualified relation into a schema that is NOT a known system/
        // extension schema MUST resolve to a real relation — either one this project defines or one a
        // reference contributes. The base SemanticAnalyzer is conservative and skips any schema it doesn't
        // "manage" (to avoid false positives on pg_catalog/extensions). Here we additionally mark every
        // non-system schema that appears in a qualified reference as managed, so a name into a schema that
        // exists only because of a (now-missing) reference is reported as unresolved.
        foreach (var schema in CollectReferencedSchemas(parsedByFile.Values))
            if (!IsSystemSchema(schema))
                projectCatalog.AddSchema(schema);

        // For resolution we want the FULL catalog visible; for "already exists" duplicate detection we must
        // NOT treat the project's own objects as pre-existing (that would flag every CREATE as a dup). An
        // empty pre-existing catalog disables duplicate detection here — duplicates are already caught by
        // DatabaseProject.Build's FindDuplicates. This validator's job is unresolved cross-schema refs.
        var noPreExisting = new Catalog { DefaultSchema = project.DefaultSchema };

        foreach (var file in files)
        {
            var parsed = parsedByFile[file];
            var rel = Path.GetRelativePath(project.ProjectDirectory, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            // Analyze one statement at a time so every diagnostic can be attributed to that statement's
            // source position. The catalog already contains all project + external objects, so order of
            // statements across files never produces a false "does not exist".
            foreach (var stmt in parsed.Statements)
            {
                var (line, col) = OffsetToLineColumn(text, stmt.Position);

                // A CREATE VIEW / MATERIALIZED VIEW carries its query only as rendered body text (the
                // model the comparer consumes does not need a parsed tree). To catch a view that reads an
                // object in a managed schema that does not exist, re-parse the body as a query and analyze
                // it. The diagnostic is attributed to the CREATE VIEW's own position.
                var analyzable = stmt switch
                {
                    CreateViewStatement v => ParseBody(v.BodyText),
                    _ => Wrap(stmt),
                };

                // Run through the unified diagnostic so each finding carries file/line/col (issue #49);
                // ReferenceValidationDiagnostic is the stable build-output shape, derived from it.
                var found = new SemanticAnalyzer(projectCatalog, noPreExisting).AnalyzeUnified(analyzable, rel, line, col);
                foreach (var d in found)
                    diagnostics.Add(new ReferenceValidationDiagnostic(d.File ?? rel, d.Line, d.Column, d.Message));
            }
        }

        return diagnostics;
    }

    // Known schemas that are NOT part of a project's managed universe: a qualified reference into one is
    // always legitimate (system catalogs, the information schema, common extension schemas) and must never
    // be flagged. Anything else that appears qualified is expected to resolve to a project or external object.
    private static readonly HashSet<string> SystemSchemas = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "pg_catalog", "information_schema", "pg_toast", "pg_temp",
    };

    private static bool IsSystemSchema(string schema) =>
        SystemSchemas.Contains(schema) || schema.StartsWith("pg_", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Collects every schema that appears in a qualified relation reference across the project.</summary>
    private static HashSet<string> CollectReferencedSchemas(IEnumerable<ParseResult> parsed)
    {
        var schemas = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var result in parsed)
            foreach (var stmt in result.Statements)
                CollectFromStatement(stmt, schemas);
        return schemas;
    }

    private static void CollectFromStatement(SqlStatement stmt, HashSet<string> schemas)
    {
        switch (stmt)
        {
            case QueryStatement q: CollectFromQuery(q.Query, schemas); break;
            case CreateTableAsStatement ctas: CollectFromQuery(ctas.Source, schemas); break;
            case CreateViewStatement v:
                foreach (var s in ParseBody(v.BodyText).Statements) CollectFromStatement(s, schemas);
                break;
            case InsertStatement ins: CollectFromQuery(ins.Source, schemas); break;
            case CreateIndexStatement ix when ix.Schema is not null: schemas.Add(ix.Schema); break;
        }
    }

    private static void CollectFromQuery(SelectQuery? q, HashSet<string> schemas)
    {
        if (q is null) return;
        foreach (var cte in q.With) CollectFromQuery(cte.Query, schemas);
        if (q.SetOp is not null) { CollectFromQuery(q.SetOp.Left, schemas); CollectFromQuery(q.SetOp.Right, schemas); }
        if (q.From is null) return;
        foreach (var rel in q.From.Relations)
        {
            CollectFromTableRef(rel, schemas);
            foreach (var j in rel.Joins) CollectFromTableRef(j.Right, schemas);
        }
    }

    private static void CollectFromTableRef(TableRef rel, HashSet<string> schemas)
    {
        if (rel.Subquery is not null) { CollectFromQuery(rel.Subquery, schemas); return; }
        if (rel.Schema is not null && rel.TableName is not null) schemas.Add(rel.Schema);
    }

    private static ParseResult Wrap(SqlStatement stmt)
    {
        var r = new ParseResult();
        r.Statements.Add(stmt);
        return r;
    }

    // Re-parse a view's body text as a standalone query. If it doesn't parse cleanly (an exotic view we
    // don't model finely), return an empty result — we never want to emit a false unresolved-ref error.
    private static ParseResult ParseBody(string bodyText)
    {
        try
        {
            var parsed = new PgParser().Parse(bodyText);
            return parsed.Diagnostics.Count == 0 ? parsed : new ParseResult();
        }
        catch
        {
            return new ParseResult();
        }
    }

    private static (int Line, int Column) OffsetToLineColumn(string text, int offset)
    {
        int line = 1, col = 1;
        int limit = System.Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}
