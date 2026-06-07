using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using PgProj.Core.Contracts;
using PgProj.Core.Diagnostics;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Refs = PgProj.Core.Project.References;

namespace PgProj.Core.Project;

/// <summary>
/// A Postgres database project: an MSBuild-style <c>.pgproj</c> manifest plus the set of
/// <c>.sql</c> files it globs in. This is the Postgres analogue of an SSDT <c>.sqlproj</c> — the
/// unit you "build" into a model and then compare/publish against a live server.
/// </summary>
public sealed record DatabaseProject
{
    public required string ProjectFilePath { get; init; }
    public required string ProjectDirectory { get; init; }
    public string Name { get; init; } = "Database";
    public string DefaultSchema { get; init; } = "public";
    public string? TargetPostgresVersion { get; init; }
    public IReadOnlyList<string> IncludePatterns { get; init; } = new[] { "**/*.sql" };

    /// <summary>
    /// Absolute path of the single pre-deployment script (SSDT <c>BuildAction=PreDeploy</c>), or null.
    /// Spliced before the schema diff in the generated deploy script.
    /// </summary>
    public string? PreDeployScriptPath { get; init; }

    /// <summary>
    /// Absolute path of the single post-deployment script (SSDT <c>BuildAction=PostDeploy</c>), or null.
    /// Spliced after the schema diff in the generated deploy script.
    /// </summary>
    public string? PostDeployScriptPath { get; init; }

    /// <summary>
    /// SQLCMD-style project variables and their project-level default values
    /// (<c>&lt;SqlCmdVariable Include="Name"&gt;&lt;DefaultValue&gt;…&lt;/DefaultValue&gt;&lt;/SqlCmdVariable&gt;</c>).
    /// Overridden at publish time by profile/CLI; see <see cref="Deployment.SqlCmdVariableResolver"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SqlCmdVariableDefaults { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional opt-in transform applied to each object file's text <em>before</em> parsing
    /// (args: relative-path, raw-text → transformed-text). Used to substitute SQLCMD <c>$(Var)</c>
    /// tokens into object files — disabled by default (deploy-script substitution is the default scope);
    /// the CLI wires this only when <c>--substitute-objects</c> is passed. Left null → files parse verbatim.
    /// </summary>
    public Func<string, string, string>? ObjectContentTransform { get; init; }

    /// <summary>
    /// The project / artifact / package references declared in the <c>.pgproj</c> ItemGroups (EP-REF).
    /// Their Include paths are resolved relative to <see cref="ProjectDirectory"/>; resolution into
    /// external models is the job of <see cref="References.ReferenceResolver"/>, not the loader.
    /// </summary>
    public IReadOnlyList<Refs.ProjectReferenceItem> References { get; init; } =
        System.Array.Empty<Refs.ProjectReferenceItem>();

    private string ReadSource(string file)
    {
        var text = File.ReadAllText(file);
        if (ObjectContentTransform is null) return text;
        return ObjectContentTransform(Path.GetRelativePath(ProjectDirectory, file), text);
    }

    public static DatabaseProject Load(string projectFilePath)
    {
        var fullPath = Path.GetFullPath(projectFilePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Project file not found: {fullPath}");

        var dir = Path.GetDirectoryName(fullPath)!;
        var doc = XDocument.Load(fullPath);
        var root = doc.Root ?? throw new InvalidOperationException("Empty project file.");

        string Prop(string name, string fallback) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
            is { Length: > 0 } v ? v : fallback;

        var includes = root.Descendants()
            .Where(e => e.Name.LocalName.Equals("Build", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        // Apply the default "**/*.sql" glob when no explicit <Build Include> items are declared
        // AND EnableDefaultSqlItems is not explicitly set to false.  This mirrors the Sdk.props
        // default at the MSBuild level so the engine produces the same result when called directly
        // (e.g. from tests or the CLI) as when MSBuild evaluates the project.
        if (includes.Count == 0 &&
            !Prop("EnableDefaultSqlItems", "true").Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            includes.Add("**/*.sql");
        }

        var (preDeploy, postDeploy) = LoadDeployScripts(root, dir);
        var variables = LoadSqlCmdVariables(root);

        return new DatabaseProject
        {
            ProjectFilePath = fullPath,
            ProjectDirectory = dir,
            Name = Prop("Name", Path.GetFileNameWithoutExtension(fullPath)),
            DefaultSchema = Prop("DefaultSchema", "public"),
            TargetPostgresVersion = root.Descendants().Any(e => e.Name.LocalName.Equals("TargetPostgresVersion", StringComparison.OrdinalIgnoreCase))
                ? Prop("TargetPostgresVersion", "") : null,
            IncludePatterns = includes,
            PreDeployScriptPath = preDeploy,
            PostDeployScriptPath = postDeploy,
            SqlCmdVariableDefaults = variables,
            References = ParseReferences(root, dir),
        };
    }

    /// <summary>
    /// Reads SSDT-style <c>&lt;None Include="…"&gt;&lt;BuildAction&gt;PreDeploy|PostDeploy&lt;/BuildAction&gt;&lt;/None&gt;</c>
    /// items (any item element carrying a BuildAction child is honoured, mirroring SSDT). At most one of
    /// each is allowed; a second of either kind is a hard error so the deploy order stays unambiguous.
    /// </summary>
    private static (string? Pre, string? Post) LoadDeployScripts(XElement root, string dir)
    {
        string? pre = null, post = null;
        foreach (var item in root.Descendants())
        {
            var include = item.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(include)) continue;

            var action = item.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("BuildAction", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(action)) continue;

            var resolved = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', '/')));
            if (action.Equals("PreDeploy", StringComparison.OrdinalIgnoreCase))
            {
                if (pre is not null)
                    throw new InvalidOperationException(
                        "Multiple PreDeploy scripts declared; exactly one BuildAction=PreDeploy item is allowed.");
                pre = resolved;
            }
            else if (action.Equals("PostDeploy", StringComparison.OrdinalIgnoreCase))
            {
                if (post is not null)
                    throw new InvalidOperationException(
                        "Multiple PostDeploy scripts declared; exactly one BuildAction=PostDeploy item is allowed.");
                post = resolved;
            }
        }
        return (pre, post);
    }

    /// <summary>
    /// Reads <c>&lt;SqlCmdVariable Include="Name"&gt;&lt;DefaultValue&gt;…&lt;/DefaultValue&gt;&lt;/SqlCmdVariable&gt;</c>
    /// items into a case-insensitive name→default map (a missing DefaultValue defaults to empty string).
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadSqlCmdVariables(XElement root)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.Descendants()
            .Where(e => e.Name.LocalName.Equals("SqlCmdVariable", StringComparison.OrdinalIgnoreCase)))
        {
            var name = item.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var def = item.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("DefaultValue", StringComparison.OrdinalIgnoreCase))
                ?.Value ?? string.Empty;
            vars[name] = def;
        }
        return vars;
    }

    /// <summary>
    /// Reads <c>&lt;ProjectReference/&gt;</c>, <c>&lt;ArtifactReference/&gt;</c> and
    /// <c>&lt;PackageReference/&gt;</c> items from any ItemGroup. The reference KIND is the element's local
    /// name; <c>Include</c> is the path (project/artifact) or package id (package). Package references also
    /// carry an optional <c>Version</c> attribute.
    /// </summary>
    private static IReadOnlyList<Refs.ProjectReferenceItem> ParseReferences(XElement root, string projectDir)
    {
        var refs = new List<Refs.ProjectReferenceItem>();
        foreach (var e in root.Descendants())
        {
            var kind = e.Name.LocalName switch
            {
                var n when n.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Project,
                var n when n.Equals("ArtifactReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Artifact,
                var n when n.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Package,
                _ => (Refs.ReferenceKind?)null,
            };
            if (kind is null) continue;

            var include = e.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(include)) continue;

            var version = e.Attribute("Version")?.Value?.Trim();
            refs.Add(new Refs.ProjectReferenceItem(kind.Value, include, version, projectDir));
        }
        return refs;
    }

    /// <summary>Resolves all .sql files the project includes, de-duplicated and ordered deterministically.</summary>
    public IReadOnlyList<string> ResolveSqlFiles()
    {
        var files = new List<string>();
        foreach (var pattern in IncludePatterns)
        {
            var norm = pattern.Replace('\\', '/');
            if (norm.Contains("**"))
            {
                files.AddRange(Directory.GetFiles(ProjectDirectory, "*.sql", SearchOption.AllDirectories));
            }
            else if (norm.Contains('*'))
            {
                var subDir = Path.Combine(ProjectDirectory, Path.GetDirectoryName(norm) ?? string.Empty);
                var glob = Path.GetFileName(norm);
                if (Directory.Exists(subDir))
                    files.AddRange(Directory.GetFiles(subDir, glob, SearchOption.TopDirectoryOnly));
            }
            else
            {
                var literal = Path.Combine(ProjectDirectory, norm);
                if (File.Exists(literal)) files.Add(literal);
            }
        }

        // Pre/post-deploy scripts are data/seed scripts spliced around the diff at publish time, not
        // schema-object sources — exclude them from the model build even if a glob would catch them.
        var deployScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (PreDeployScriptPath is not null) deployScripts.Add(PreDeployScriptPath);
        if (PostDeployScriptPath is not null) deployScripts.Add(PostDeployScriptPath);

        return files
            .Select(Path.GetFullPath)
            // Files whose name starts with '_' are treated as non-source (generated artifacts,
            // scratch, dependency-order manifests). Lets a project keep e.g. a generated
            // _full_create.sql concatenation in-tree without it being parsed twice.
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Where(f => !deployScripts.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parses every included .sql file into one model and reports build diagnostics.</summary>
    public ProjectBuildResult Build()
    {
        var model = new DatabaseModel();
        var unifiedDiags = new List<Diagnostic>();
        var files = ResolveSqlFiles();
        var builder = new Syntax.ModelBuilder(DefaultSchema);
        // First-seen position map: object identity → (rel-file, line, col).
        // Populated as we parse so FindDuplicates can point at the prior definition.
        var firstSeen = new Dictionary<string, (string RelFile, int Line, int Col)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var text = ReadSource(file);
            var parsed = new Syntax.PgParser().Parse(text);
            var rel = Path.GetRelativePath(ProjectDirectory, file).Replace('\\', '/');
            foreach (var d in parsed.Diagnostics)
                unifiedDiags.Add(Diagnostic.FromParser(d.Message, rel, d.Line, d.Column));
            // Record first-seen positions before merging into the model (model merge is first-occurrence wins,
            // matching the order we parse here).
            RecordFirstSeen(parsed, text, rel, DefaultSchema, firstSeen);
            builder.Build(parsed, model);
            parsed.ReleaseTokens();   // model built → return the pooled token buffer (no SourceText read after this)
        }

        unifiedDiags.AddRange(FindDuplicates(model, firstSeen));
        return new ProjectBuildResult(model, unifiedDiags, files);
    }

    /// <summary>
    /// Parallel analogue of <see cref="Build"/>: parses each .sql file in isolation (a private
    /// parser + model per file, since neither is thread-safe) with bounded concurrency, then merges
    /// the partial models deterministically in sorted-file order so the result is byte-identical to
    /// the sequential build. Per-file errors are isolated as diagnostics, never aborting the build.
    /// (Design: concurrency-orchestrator agent, 2026-06-03.)
    /// </summary>
    public async Task<ProjectBuildResult> BuildAsync(CancellationToken ct = default)
    {
        var files = ResolveSqlFiles();                 // already sorted → defines merge order

        // Small-N short-circuit: for 0–1 files the Parallel.ForEachAsync machinery is pure overhead
        // (crossover ≈10 files — see PgProj.Benchmarks). Accumulate directly into one model — exactly
        // like the serial Build() (no PartialParse/Merge copy) — so a single-file build is as cheap as
        // serial, while keeping BuildAsync's per-file error isolation (a bad file → diagnostic, not throw).
        if (files.Count <= 1)
        {
            ct.ThrowIfCancellationRequested();
            var model = new DatabaseModel();
            var unifiedDiags = new List<Diagnostic>();
            var firstSeen = new Dictionary<string, (string RelFile, int Line, int Col)>(StringComparer.OrdinalIgnoreCase);
            if (files.Count == 1)
            {
                try
                {
                    var text = ReadSource(files[0]);
                    var parsed = new Syntax.PgParser().Parse(text);
                    var rel = Path.GetRelativePath(ProjectDirectory, files[0]).Replace('\\', '/');
                    foreach (var d in parsed.Diagnostics)
                        unifiedDiags.Add(Diagnostic.FromParser(d.Message, rel, d.Line, d.Column));
                    RecordFirstSeen(parsed, text, rel, DefaultSchema, firstSeen);
                    new Syntax.ModelBuilder(DefaultSchema).Build(parsed, model);
                    parsed.ReleaseTokens();
                }
                catch (Exception ex)
                {
                    unifiedDiags.Add(Diagnostic.FromBuild($"Failed to read/parse '{Path.GetFileName(files[0])}': {ex.Message}"));
                }
            }
            unifiedDiags.AddRange(FindDuplicates(model, firstSeen));
            return new ProjectBuildResult(model, unifiedDiags, files);
        }

        var parts = new PartialParse[files.Count];     // one slot per file; index = order

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(EnumerateIndexed(files), options, (item, _) =>
        {
            parts[item.Index] = ParseOne(item.Path);   // total: never throws a domain error
            return ValueTask.CompletedTask;            // CPU-bound body — no real awaiting
        });

        return Merge(parts, files);
    }

    private PartialParse ParseOne(string path)
    {
        try
        {
            var text = ReadSource(path);
            var parsed = new Syntax.PgParser().Parse(text); // fresh instance → isolated per worker
            var model = new Syntax.ModelBuilder(DefaultSchema).Build(parsed);
            var rel = Path.GetRelativePath(ProjectDirectory, path).Replace('\\', '/');
            var diags = parsed.Diagnostics
                .Select(d => Diagnostic.FromParser(d.Message, rel, d.Line, d.Column))
                .ToList();
            var firstSeen = new Dictionary<string, (string RelFile, int Line, int Col)>(StringComparer.OrdinalIgnoreCase);
            RecordFirstSeen(parsed, text, rel, DefaultSchema, firstSeen);
            parsed.ReleaseTokens();   // model built → return the pooled token buffer (per-worker, ArrayPool is thread-safe)
            return new PartialParse(model, diags, firstSeen);
        }
        catch (Exception ex) // unreadable file / catastrophic parser failure → isolate to this file
        {
            return new PartialParse(new DatabaseModel(),
                new List<Diagnostic> { Diagnostic.FromBuild($"Failed to read/parse '{Path.GetFileName(path)}': {ex.Message}") },
                new Dictionary<string, (string RelFile, int Line, int Col)>());
        }
    }

    private static ProjectBuildResult Merge(PartialParse[] parts, IReadOnlyList<string> files)
    {
        var model = new DatabaseModel();
        var unifiedDiags = new List<Diagnostic>();
        // Merge first-seen maps in sorted-file order (same order as the model merge) so that the
        // first-file winner mirrors the model's first-occurrence-wins merge.
        var mergedFirstSeen = new Dictionary<string, (string RelFile, int Line, int Col)>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts) // ordered walk → deterministic, matches sequential Build()
        {
            foreach (var s in part.Model.Schemas)
                if (!model.HasSchema(s.Name)) model.Schemas.Add(s); // first-occurrence wins
            model.Tables.AddRange(part.Model.Tables);
            model.Indexes.AddRange(part.Model.Indexes);
            model.Views.AddRange(part.Model.Views);
            model.Sequences.AddRange(part.Model.Sequences);
            model.Functions.AddRange(part.Model.Functions);
            model.Objects.AddRange(part.Model.Objects);
            unifiedDiags.AddRange(part.Diagnostics);
            // First-occurrence wins (same logic as schema merge above).
            foreach (var kv in part.FirstSeen)
                if (!mergedFirstSeen.ContainsKey(kv.Key)) mergedFirstSeen[kv.Key] = kv.Value;
        }

        unifiedDiags.AddRange(FindDuplicates(model, mergedFirstSeen)); // same post-merge dup scan as Build()
        return new ProjectBuildResult(model, unifiedDiags, files);
    }

    private static IEnumerable<(int Index, string Path)> EnumerateIndexed(IReadOnlyList<string> files)
    {
        for (var i = 0; i < files.Count; i++) yield return (i, files[i]);
    }

    private readonly record struct PartialParse(
        DatabaseModel Model,
        List<Diagnostic> Diagnostics,
        Dictionary<string, (string RelFile, int Line, int Col)> FirstSeen);

    /// <summary>
    /// Records the first-seen source position for each distinctly-identifiable statement in a parse result.
    /// Must be called BEFORE <see cref="Syntax.ModelBuilder.Build"/> merges the parsed statements into the
    /// shared model (the model merge is first-occurrence wins; we mirror that here so the position map and the
    /// model stay in sync). <paramref name="defaultSchema"/> must match the project's default schema so that
    /// unqualified object names hash to the same key that <see cref="FindDuplicates"/> looks up.
    /// </summary>
    private static void RecordFirstSeen(
        Syntax.ParseResult parsed,
        string sourceText,
        string relFile,
        string defaultSchema,
        Dictionary<string, (string RelFile, int Line, int Col)> firstSeen)
    {
        foreach (var stmt in parsed.Statements)
        {
            var key = SourcePositionIndex.IdentityOf(stmt, defaultSchema);
            if (key is null) continue;
            if (firstSeen.ContainsKey(key)) continue;   // first-occurrence wins
            var (line, col) = SourcePositionIndex.LineCol(sourceText, stmt.Position);
            firstSeen[key] = (relFile, line, col);
        }
    }

    /// <summary>
    /// Scans the merged model for duplicate definitions and emits a structured <see cref="Diagnostic"/>
    /// for each group of duplicates. Each diagnostic carries a <see cref="RelatedLocation"/> pointing at
    /// the prior (first) definition when the position map knows where it was first seen.
    /// </summary>
    private static IEnumerable<Diagnostic> FindDuplicates(
        DatabaseModel model,
        Dictionary<string, (string RelFile, int Line, int Col)>? firstSeen = null)
    {
        RelatedLocation[] PriorDef(string identityKey)
        {
            if (firstSeen is null) return Array.Empty<RelatedLocation>();
            if (!firstSeen.TryGetValue(identityKey, out var pos)) return Array.Empty<RelatedLocation>();
            return new[] { new RelatedLocation(pos.RelFile, pos.Line, pos.Col, "first defined here") };
        }

        foreach (var dup in model.Tables.GroupBy(t => $"{t.Schema}.{t.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var msg = $"Duplicate table definition: {dup.Key} (defined {dup.Count()} times).";
            yield return Diagnostic.FromBuild(msg) with
            {
                Related = PriorDef($"table:{dup.Key}"),
            };
        }

        foreach (var dup in model.Views.GroupBy(v => $"{v.Schema}.{v.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var msg = $"Duplicate view definition: {dup.Key} (defined {dup.Count()} times).";
            yield return Diagnostic.FromBuild(msg) with
            {
                Related = PriorDef($"view:{dup.Key}"),
            };
        }

        foreach (var dup in model.Functions.GroupBy(f => f.Signature.ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var msg = $"Duplicate function definition: {dup.Key} (defined {dup.Count()} times).";
            yield return Diagnostic.FromBuild(msg) with
            {
                Related = PriorDef($"function:{dup.Key}"),
            };
        }

        foreach (var dup in model.Indexes.GroupBy(i => $"{i.Schema}.{i.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var msg = $"Duplicate index definition: {dup.Key} (defined {dup.Count()} times).";
            yield return Diagnostic.FromBuild(msg) with
            {
                Related = PriorDef($"index:{dup.Key}"),
            };
        }
    }
}

/// <summary>
/// The outcome of a <see cref="DatabaseProject"/> build. Carries the merged model, the list of build
/// problems (both as unified <see cref="Diagnostic"/> objects and as backwards-compatible strings), and
/// the set of parsed source files.
/// </summary>
public sealed record ProjectBuildResult
{
    public DatabaseModel Model { get; init; }

    /// <summary>
    /// All build problems as fully-structured <see cref="Diagnostic"/> values. Each diagnostic carries
    /// file/line/col/severity/code and — for duplicate-definition problems — a <c>Related</c> location
    /// pointing at the prior definition. Use this instead of <see cref="Diagnostics"/> when you need
    /// the structured form (e.g. the contract layer, editors, SARIF writers).
    /// </summary>
    public IReadOnlyList<Diagnostics.Diagnostic> UnifiedDiagnostics { get; init; }

    /// <summary>
    /// Backwards-compatible string projection of <see cref="UnifiedDiagnostics"/>, using
    /// <see cref="Diagnostics.Diagnostic.ToString"/>. Kept so existing code that iterates/stringifies
    /// diagnostics (CLI text output, <c>string.Join</c>, string-predicate <c>Assert.Contains</c>) keeps
    /// compiling without changes. For new code, prefer <see cref="UnifiedDiagnostics"/>.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; init; }

    public IReadOnlyList<string> Files { get; init; }

    public bool HasErrors => UnifiedDiagnostics.Count > 0;

    /// <summary>Initializes the result from the unified diagnostic list; the string shim is derived automatically.</summary>
    public ProjectBuildResult(DatabaseModel model, IReadOnlyList<Diagnostics.Diagnostic> unifiedDiagnostics, IReadOnlyList<string> files)
    {
        Model = model;
        UnifiedDiagnostics = unifiedDiagnostics;
        Diagnostics = unifiedDiagnostics.Select(d => d.ToString()).ToList();
        Files = files;
    }
}
