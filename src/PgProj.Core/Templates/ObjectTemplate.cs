using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Templates;

/// <summary>
/// The object kinds <c>pgproj add</c> can scaffold. A deliberately small, curated subset of the
/// full <see cref="Model.ObjectKind"/> surface — the kinds a developer reaches for day-to-day,
/// matching SSDT's "Add New Item" object templates.
/// </summary>
public enum TemplateKind
{
    Table,
    View,
    Function,
    Procedure,
    Trigger,
    Sequence,
    Type,
    Schema,
    Policy,
}

/// <summary>
/// One object template: the folder it files into (mirroring <see cref="Comparison.DdlExporter"/>'s
/// extract layout so scaffolded files sit exactly where <c>extract</c>/<c>pull</c> would write them),
/// and a body factory that substitutes the schema/name tokens. The rendered body is always a
/// complete, parse-clean CREATE statement.
/// </summary>
public sealed record ObjectTemplate(
    TemplateKind Kind,
    string Folder,
    Func<ObjectName, string> Render,
    bool SchemaScoped = true)
{
    /// <summary>The <c>dotnet new</c> short name for this object template (e.g. <c>pgproj-table</c>).</summary>
    public string DotnetNewShortName => "pgproj-" + Kind.ToString().ToLowerInvariant();
}

/// <summary>A parsed <c>schema.name</c> argument. <see cref="Schema"/> falls back to the project default.</summary>
public readonly record struct ObjectName(string Schema, string Name)
{
    public string Qualified => $"{Schema}.{Name}";

    /// <summary>
    /// Parses <c>schema.name</c> or bare <c>name</c>. A bare name takes <paramref name="defaultSchema"/>.
    /// Rejects empty parts and anything with more than one dot (we don't scaffold catalog.schema.name).
    /// </summary>
    public static ObjectName Parse(string raw, string defaultSchema)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("An object name is required (e.g. 'app.Customer' or 'Customer').");

        var parts = raw.Split('.');
        return parts.Length switch
        {
            1 when parts[0].Length > 0 => new ObjectName(defaultSchema, parts[0]),
            2 when parts.All(p => p.Length > 0) => new ObjectName(parts[0], parts[1]),
            _ => throw new ArgumentException($"Invalid object name '{raw}'. Use 'schema.name' or 'name'."),
        };
    }
}
