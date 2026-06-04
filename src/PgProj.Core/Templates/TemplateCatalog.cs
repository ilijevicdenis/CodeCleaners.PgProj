using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Templates;

/// <summary>
/// The catalog of object templates used by <c>pgproj add</c> (and surfaced as <c>dotnet new</c>
/// short names). Each template renders a complete, parse-clean CREATE statement with the schema/name
/// tokens substituted, and declares the folder it files into — mirroring the <c>extract</c> layout
/// (<see cref="Comparison.DdlExporter"/> / <see cref="Comparison.RawObjectMeta"/>) so scaffolded files
/// land exactly where reverse-sync would put them.
/// </summary>
public static class TemplateCatalog
{
    private static readonly IReadOnlyDictionary<TemplateKind, ObjectTemplate> _byKind =
        new[]
        {
            new ObjectTemplate(TemplateKind.Table, "Tables", Table),
            new ObjectTemplate(TemplateKind.View, "Views", View),
            new ObjectTemplate(TemplateKind.Function, "Functions", Function),
            new ObjectTemplate(TemplateKind.Procedure, "Procedures", Procedure),
            new ObjectTemplate(TemplateKind.Trigger, "Triggers", Trigger),
            new ObjectTemplate(TemplateKind.Sequence, "Sequences", Sequence),
            new ObjectTemplate(TemplateKind.Type, "Types", Type),
            new ObjectTemplate(TemplateKind.Policy, "Policies", Policy),
            new ObjectTemplate(TemplateKind.Schema, "Schemas", Schema, SchemaScoped: false),
        }.ToDictionary(t => t.Kind);

    public static IReadOnlyCollection<ObjectTemplate> All => (IReadOnlyCollection<ObjectTemplate>)_byKind.Values;

    /// <summary>Looks up a template by its CLI kind word (case-insensitive). Returns null if unknown.</summary>
    public static ObjectTemplate? Resolve(string kind) =>
        Enum.TryParse<TemplateKind>(kind, ignoreCase: true, out var k) && _byKind.TryGetValue(k, out var t)
            ? t
            : null;

    public static ObjectTemplate Get(TemplateKind kind) => _byKind[kind];

    /// <summary>The comma-separated list of kinds for usage/error messages.</summary>
    public static string KindWords =>
        string.Join(", ", _byKind.Keys.Select(k => k.ToString().ToLowerInvariant()));

    // ---- templates ----------------------------------------------------------------------
    // Bodies favour the sample-project house style: bare identifiers, a leading comment, a
    // minimal-but-real shape that PARSES clean and is an obvious starting point to edit.

    private static string Table(ObjectName o) =>
        $"""
        -- Table {o.Qualified}. Replace the placeholder columns with your own.
        CREATE TABLE {o.Qualified} (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            created_at  timestamptz NOT NULL DEFAULT now()
        );
        """ + "\n";

    private static string View(ObjectName o) =>
        $"""
        -- View {o.Qualified}.
        CREATE VIEW {o.Qualified} AS
        SELECT 1 AS placeholder;
        """ + "\n";

    private static string Function(ObjectName o) =>
        $"""
        -- Function {o.Qualified}.
        CREATE OR REPLACE FUNCTION {o.Qualified}()
            RETURNS void
            LANGUAGE plpgsql
        AS $$
        BEGIN
            -- TODO: implement
            RETURN;
        END;
        $$;
        """ + "\n";

    private static string Procedure(ObjectName o) =>
        $"""
        -- Procedure {o.Qualified}.
        CREATE OR REPLACE PROCEDURE {o.Qualified}()
            LANGUAGE plpgsql
        AS $$
        BEGIN
            -- TODO: implement
        END;
        $$;
        """ + "\n";

    private static string Trigger(ObjectName o) =>
        // Triggers are table-scoped: the file is named for the trigger, but it must target a table.
        // We scaffold a self-evident placeholder target + function the developer renames.
        $"""
        -- Trigger {o.Name} (in schema {o.Schema}). Point it at a real table and function.
        CREATE TRIGGER {o.Name}
            BEFORE INSERT OR UPDATE ON {o.Schema}.table_name
            FOR EACH ROW
            EXECUTE FUNCTION {o.Schema}.trigger_function();
        """ + "\n";

    private static string Sequence(ObjectName o) =>
        $"""
        -- Sequence {o.Qualified}.
        CREATE SEQUENCE {o.Qualified}
            AS bigint
            START WITH 1
            INCREMENT BY 1
            NO MAXVALUE
            NO CYCLE;
        """ + "\n";

    private static string Type(ObjectName o) =>
        $"""
        -- Type {o.Qualified}. Replace the placeholder enum with your own definition
        -- (e.g. AS (a int, b text) for a composite type).
        CREATE TYPE {o.Qualified} AS ENUM ('first', 'second');
        """ + "\n";

    private static string Policy(ObjectName o) =>
        // Policies are table-scoped too; scaffold an enable + permissive policy placeholder.
        $"""
        -- Row-level-security policy {o.Name} (in schema {o.Schema}). Point it at a real table.
        ALTER TABLE {o.Schema}.table_name ENABLE ROW LEVEL SECURITY;

        CREATE POLICY {o.Name} ON {o.Schema}.table_name
            AS PERMISSIVE
            FOR ALL
            TO PUBLIC
            USING (true)
            WITH CHECK (true);
        """ + "\n";

    private static string Schema(ObjectName o) =>
        // Schema-scoped=false → o.Name carries the schema name, o.Schema is ignored.
        $"""
        -- Schema {o.Name}.
        CREATE SCHEMA IF NOT EXISTS {o.Name};
        """ + "\n";
}
