using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Sync;

/// <summary>
/// Scenario 3 — reverse-sync ("pull"/"drift"): bring the project's .sql files back in line with what
/// is actually in a live database, so a hotfix applied directly to production can be captured into
/// source instead of hand-copied. The inverse of compare/publish (which push the project TO the DB).
///
/// This lives in PgProj.Core, not the CLI, because both the headless tool and the future Visual Studio
/// project-system/templates drive it: <see cref="Plan"/> is pure (computes file edits, touches nothing)
/// so a host can preview/diff them, and <see cref="Apply"/> writes an approved plan to disk.
/// </summary>
public static class ReverseSync
{
    /// <summary>
    /// Computes how to rewrite the project's files so they match <paramref name="live"/>. Reads the
    /// project's files and the supplied live model only — no disk writes, no server calls.
    /// </summary>
    public static async Task<DriftPlan> PlanAsync(DatabaseProject project, DatabaseModel live, DriftOptions? options = null)
    {
        options ??= new DriftOptions();
        var projectModel = (await project.BuildAsync()).Model;

        // The authoritative semantic diff: steps that would make the project (target) match the DB
        // (source). Reuses the same comparer publish trusts, so "drift" never reports phantom changes.
        var changes = new SchemaComparer().Compare(live, projectModel, new ComparerOptions
        {
            DropObjectsNotInSource = options.AllowDeletes,
        });
        if (changes.Count == 0)
            return new DriftPlan(changes, Array.Empty<ProjectFileChange>());

        // Canonical one-object-per-file rendering of the DB, plus a map from each canonical file unit
        // to the real project file that currently defines it (so edits preserve the user's layout).
        var liveByUnit = DdlExporter.ExportFiles(live);
        var (unitToFile, fileToUnits) = await MapProjectFilesAsync(project);

        // The canonical file units the drift touches (a table's columns/indexes/FKs all roll up to its file).
        var touchedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in changes)
            if (CanonicalUnit(ch, projectModel) is { } u) touchedUnits.Add(u);

        var affectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newUnits = new List<string>();                 // touched units with no existing file = new in DB
        foreach (var u in touchedUnits)
        {
            if (unitToFile.TryGetValue(u, out var rf)) affectedFiles.Add(rf);
            else if (liveByUnit.ContainsKey(u)) newUnits.Add(u);
        }

        var edits = new List<ProjectFileChange>();

        // Existing files owning a drifted object: regenerate them wholesale from the DB (a file may
        // hold several objects — regenerate every one it still owns so nothing unrelated is lost), or
        // delete when none of its objects survive in the DB.
        foreach (var rf in affectedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var owned = fileToUnits[rf];
            var surviving = owned.Where(liveByUnit.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (surviving.Count == 0)
            {
                if (!options.AllowDeletes) continue;        // never delete unless explicitly allowed
                edits.Add(new ProjectFileChange(rf, ProjectFileChangeKind.Delete, null,
                    $"dropped from database: {Describe(owned)}", IsDestructive: true));
            }
            else
            {
                var content = string.Concat(surviving.Select(u => liveByUnit[u]));
                edits.Add(new ProjectFileChange(rf, ProjectFileChangeKind.Update, content,
                    $"updated from database: {Describe(surviving)}", IsDestructive: false));
            }
        }

        // Objects that exist in the DB but nowhere in the project: create a new file at the canonical path.
        foreach (var u in newUnits.OrderBy(x => x, StringComparer.Ordinal))
            edits.Add(new ProjectFileChange(u, ProjectFileChangeKind.Create, liveByUnit[u],
                $"new in database: {Describe(u)}", IsDestructive: false));

        return new DriftPlan(changes, edits);
    }

    /// <summary>Writes an (approved) plan to disk under the project directory. Returns the relative paths touched.</summary>
    public static IReadOnlyList<string> Apply(DatabaseProject project, DriftPlan plan)
    {
        var touched = new List<string>();
        foreach (var fc in plan.FileChanges)
        {
            var full = Path.Combine(project.ProjectDirectory, fc.RelativePath);
            if (fc.Kind == ProjectFileChangeKind.Delete)
            {
                if (File.Exists(full)) File.Delete(full);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, fc.NewContent ?? string.Empty);
            }
            touched.Add(fc.RelativePath);
        }
        return touched;
    }

    // ---- mapping helpers ---------------------------------------------------------------------

    /// <summary>The canonical units (DdlExporter keys) defined by ONE project file — the basis for
    /// file-scoped operations like <see cref="FileSync"/>. Empty when the file defines none.</summary>
    public static async Task<IReadOnlyList<string>> UnitsOfFileAsync(DatabaseProject project, string relativeFile)
    {
        var (_, fileToUnits) = await MapProjectFilesAsync(project);
        var wanted = relativeFile.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (var (rel, units) in fileToUnits)
            if (string.Equals(rel.Replace('/', Path.DirectorySeparatorChar), wanted, StringComparison.OrdinalIgnoreCase))
                return units;
        return Array.Empty<string>();
    }

    /// <summary>Public face of <see cref="CanonicalUnit"/> for file-scoped change filtering.</summary>
    public static string? UnitOf(SchemaChange change, DatabaseModel modelForIndexResolution) =>
        CanonicalUnit(change, modelForIndexResolution);

    /// <summary>
    /// canonical-unit → owning project file, and the inverse. Built by parsing each project file in
    /// isolation and asking <see cref="DdlExporter"/> what canonical unit(s) it would emit — so the
    /// keying matches the live side exactly even when a file is named differently from the convention.
    /// </summary>
    private static async Task<(Dictionary<string, string> UnitToFile, Dictionary<string, List<string>> FileToUnits)>
        MapProjectFilesAsync(DatabaseProject project)
    {
        // Same shape as DatabaseProject.BuildAsync: parse each file in isolation with bounded
        // concurrency into a per-file slot (index = sorted position), then merge in order so the
        // "first definition wins" tie-break is byte-identical to the old sequential loop.
        var files = project.ResolveSqlFiles();             // already sorted → defines merge order
        var perFile = new (string Rel, List<string>? Units)[files.Count];

        if (files.Count <= 1)
        {
            // Small-N short-circuit: skip the parallel machinery for 0–1 files (pure overhead there).
            if (files.Count == 1) perFile[0] = MapOne(project, files[0]);
        }
        else
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            await Parallel.ForEachAsync(EnumerateIndexed(files), options, (item, _) =>
            {
                perFile[item.Index] = MapOne(project, item.Path);
                return ValueTask.CompletedTask;             // CPU-bound body — no real awaiting
            });
        }

        var unitToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileToUnits = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, units) in perFile)               // ordered walk → matches sequential
        {
            if (units is null) continue;
            fileToUnits[rel] = units;
            foreach (var u in units)
                if (!unitToFile.ContainsKey(u)) unitToFile[u] = rel;   // first definition wins
        }
        return (unitToFile, fileToUnits);
    }

    // Parse one project file in isolation (fresh parser + builder) and return the canonical units it
    // would emit, keyed by its project-relative path. An unreadable/odd file maps to null units.
    private static (string Rel, List<string>? Units) MapOne(DatabaseProject project, string path)
    {
        var rel = Path.GetRelativePath(project.ProjectDirectory, path);
        try
        {
            var parsed = new PgParser().Parse(SourceReader.ReadAllText(path)); // fresh instance per worker; LF-normalised (#62)
            var model = new ModelBuilder(project.DefaultSchema).Build(parsed);
            return (rel, DdlExporter.ExportFiles(model).Keys.ToList());
        }
        catch { return (rel, null); }                       // unreadable/odd file → not a mapping source
    }

    private static IEnumerable<(int Index, string Path)> EnumerateIndexed(IReadOnlyList<string> files)
    {
        for (var i = 0; i < files.Count; i++) yield return (i, files[i]);
    }

    /// <summary>The canonical file unit a single change rolls up to (matches <see cref="DdlExporter"/> keys).</summary>
    private static string? CanonicalUnit(SchemaChange ch, DatabaseModel projectModel) => ch switch
    {
        CreateSchemaChange c => DdlExporter.SchemaUnit(c.Schema),
        CreateSequenceChange c => Seq(c.Sequence.Schema, c.Sequence.Name),
        AlterSequenceChange c => Seq(c.Sequence.Schema, c.Sequence.Name),
        CreateTableChange c => Tbl(c.Table.Schema, c.Table.Name),
        AddColumnChange c => Tbl(c.Schema, c.Table),
        AlterColumnChange c => Tbl(c.Schema, c.Table),
        DropColumnChange c => Tbl(c.Schema, c.Table),
        AddCheckConstraintChange c => Tbl(c.Schema, c.Table),
        AddRawTableConstraintChange c => Tbl(c.Schema, c.Table),
        DropConstraintChange c => Tbl(c.Schema, c.Table),
        DropPrimaryKeyChange c => Tbl(c.Schema, c.Table),
        AddPrimaryKeyChange c => Tbl(c.Schema, c.Table),
        DropForeignKeyChange c => Tbl(c.Schema, c.Table),
        AddForeignKeyChange c => Tbl(c.Table.Schema, c.Table.Name),
        CreateIndexChange c => Tbl(c.Index.Schema, c.Index.Table),
        DropIndexChange c => ResolveIndexTable(c, projectModel),
        DropTableChange c => Tbl(c.Schema, c.Name),
        CreateOrReplaceViewChange c => View(c.View.Schema, c.View.Name),
        DropViewChange c => View(c.Schema, c.Name),
        CreateOrReplaceFunctionChange c => DdlExporter.FunctionUnit(c.Function),
        CreateRawObjectChange c => Raw(c.Def),
        RecreateRawObjectChange c => Raw(c.Def),
        DropRawObjectChange c => Raw(c.Def),
        _ => null,
    };

    private static string Tbl(string s, string n) => DdlExporter.TableUnit(s, n);
    private static string View(string s, string n) => DdlExporter.ViewUnit(s, n);
    private static string Seq(string s, string n) => DdlExporter.SequenceUnit(s, n);
    private static string Raw(RawObjectDefinition d) => DdlExporter.RawUnit(d);

    // A dropped index carries no table; find it in the project to learn which table file owns it.
    private static string? ResolveIndexTable(DropIndexChange c, DatabaseModel projectModel) =>
        projectModel.Indexes.FirstOrDefault(i =>
            DatabaseModel.NameEquals(i.Schema, c.Schema) && DatabaseModel.NameEquals(i.Name, c.Name)) is { } idx
            ? Tbl(idx.Schema, idx.Table)
            : null;

    private static string Describe(IEnumerable<string> units) =>
        string.Join(", ", units.Select(Describe));

    private static string Describe(string unit)   // "afd/Tables/customer.sql" -> "afd.customer (Tables)"
    {
        var segments = unit.Replace('\\', '/').Split('/');
        var name = Path.GetFileNameWithoutExtension(segments[^1]);
        var folder = segments.Length >= 2 ? segments[^2] : "";
        if (segments.Length >= 3) name = $"{segments[^3]}.{name}";   // schema/Kind/name.sql
        return folder.Length > 0 ? $"{name} ({folder})" : name;
    }
}

/// <summary>Options controlling a reverse-sync plan.</summary>
public sealed class DriftOptions
{
    /// <summary>Allow deleting project files for objects that were dropped from the database.</summary>
    public bool AllowDeletes { get; init; }
}

/// <summary>A planned edit to one project .sql file.</summary>
public sealed record ProjectFileChange(
    string RelativePath,
    ProjectFileChangeKind Kind,
    string? NewContent,
    string Summary,
    bool IsDestructive);

public enum ProjectFileChangeKind { Create, Update, Delete }

/// <summary>The result of <see cref="ReverseSync.Plan"/>: the semantic diff plus the concrete file edits.</summary>
public sealed record DriftPlan(
    IReadOnlyList<SchemaChange> SchemaChanges,
    IReadOnlyList<ProjectFileChange> FileChanges)
{
    public bool HasDrift => FileChanges.Count > 0;
    public int DestructiveCount => FileChanges.Count(f => f.IsDestructive);
}
