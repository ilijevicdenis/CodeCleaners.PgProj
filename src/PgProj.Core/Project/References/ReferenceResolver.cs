using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Packaging;

namespace PgProj.Core.Project.References;

/// <summary>One resolved reference: the declaration plus the external model it contributed.</summary>
public sealed record ResolvedReference(ProjectReferenceItem Item, DatabaseModel Model);

/// <summary>The outcome of resolving a project's full (transitive) reference graph.</summary>
public sealed record ReferenceResolution(
    IReadOnlyList<ResolvedReference> References,
    IReadOnlyList<ReferenceDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Count > 0;

    /// <summary>The union of every external object pulled in, flattened into one model.</summary>
    public DatabaseModel ExternalModel => _external ??= Flatten();
    private DatabaseModel? _external;

    private DatabaseModel Flatten()
    {
        var m = new DatabaseModel();
        var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in References)
        {
            foreach (var s in r.Model.Schemas) if (seenSchemas.Add(s.Name)) m.Schemas.Add(s);
            m.Tables.AddRange(r.Model.Tables);
            m.Indexes.AddRange(r.Model.Indexes);
            m.Views.AddRange(r.Model.Views);
            m.Sequences.AddRange(r.Model.Sequences);
            m.Functions.AddRange(r.Model.Functions);
            m.Objects.AddRange(r.Model.Objects);
        }
        return m;
    }
}

/// <summary>
/// Resolves a project's <see cref="DatabaseProject.References"/> (EP-REF) into external models that
/// validation can resolve against but the comparer never emits. Walks the graph depth-first so transitive
/// references are pulled in, and tracks the active resolution path so a cycle is reported as
/// <see cref="ReferenceErrorCodes.Circular"/> instead of overflowing the stack.
///
/// Scope: same-database / other-schema references. Cross-database (FDW/dblink) is deferred — see the
/// epic. A NuGet <c>&lt;PackageReference/&gt;</c> (#148) is resolved from the restored global packages folder:
/// the package's embedded <c>pgpkg/&lt;Name&gt;.pgpkg</c> is loaded reference-only, exactly like an
/// <c>&lt;ArtifactReference/&gt;</c>. NuGet restore itself stays an MSBuild concern; an unrestored package
/// fails with <see cref="ReferenceErrorCodes.PackageNotResolved"/>.
/// </summary>
public sealed class ReferenceResolver
{
    private readonly List<ResolvedReference> _resolved = new();
    private readonly List<ReferenceDiagnostic> _diagnostics = new();
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);   // keys already injected
    private readonly HashSet<string> _onPath = new(StringComparer.OrdinalIgnoreCase); // active DFS stack (cycle guard)
    private readonly string _packagesFolder;

    /// <param name="nugetPackagesFolder">
    /// The NuGet global packages folder to resolve <c>&lt;PackageReference/&gt;</c>s from. Defaults to
    /// <see cref="NuGetPackageLocator.DefaultGlobalPackagesFolder"/> (<c>$NUGET_PACKAGES</c> / <c>~/.nuget/packages</c>);
    /// tests inject a temp folder.
    /// </param>
    public ReferenceResolver(string? nugetPackagesFolder = null) =>
        _packagesFolder = nugetPackagesFolder ?? NuGetPackageLocator.DefaultGlobalPackagesFolder();

    /// <summary>Resolves every reference of <paramref name="project"/>, transitively.</summary>
    public ReferenceResolution Resolve(DatabaseProject project)
    {
        // Seed the cycle guard with the root project so a reference back to it is caught too.
        _onPath.Add(project.ProjectFilePath.ToLowerInvariant());
        foreach (var r in project.References) ResolveOne(r);
        _onPath.Remove(project.ProjectFilePath.ToLowerInvariant());
        return new ReferenceResolution(_resolved, _diagnostics);
    }

    private void ResolveOne(ProjectReferenceItem item)
    {
        if (_done.Contains(item.Key)) return;        // diamond / already pulled in — inject once

        switch (item.Kind)
        {
            case ReferenceKind.Project: ResolveProject(item); break;
            case ReferenceKind.Artifact: ResolveArtifact(item); break;
            case ReferenceKind.Package: ResolvePackage(item); break;
        }
    }

    private void ResolveProject(ProjectReferenceItem item)
    {
        var path = item.ResolvedPath;
        if (!File.Exists(path))
        {
            Report(ReferenceErrorCodes.NotFound, $"Referenced project not found: '{item.Include}' (resolved to '{path}').");
            return;
        }

        var key = path.ToLowerInvariant();
        if (_onPath.Contains(key))
        {
            Report(ReferenceErrorCodes.Circular, $"Circular project reference detected: '{item.Include}' (resolved to '{path}') is already being resolved.");
            return;
        }

        DatabaseProject referenced;
        try { referenced = DatabaseProject.Load(path); }
        catch (Exception ex)
        {
            Report(ReferenceErrorCodes.ReferencedBuildFailed, $"Failed to load referenced project '{item.Include}': {ex.Message}");
            return;
        }

        _onPath.Add(key);
        // Depth-first: pull in the referenced project's OWN references first (transitivity).
        foreach (var nested in referenced.References) ResolveOne(nested);

        var built = referenced.Build();
        if (built.HasErrors)
        {
            Report(ReferenceErrorCodes.ReferencedBuildFailed,
                $"Referenced project '{referenced.Name}' has build problems and cannot be used as a reference: "
                + string.Join("; ", built.Diagnostics.Take(5)));
            _onPath.Remove(key);
            return;
        }

        _onPath.Remove(key);
        Inject(item, built.Model);
    }

    private void ResolveArtifact(ProjectReferenceItem item)
    {
        var path = item.ResolvedPath;
        if (!File.Exists(path))
        {
            Report(ReferenceErrorCodes.NotFound, $"Referenced artifact not found: '{item.Include}' (resolved to '{path}').");
            return;
        }

        try
        {
            var pkg = PgPkg.Read(path);   // verifies the integrity checksum
            Inject(item, pkg.Model);
        }
        catch (PgPkgFormatException ex)
        {
            Report(ReferenceErrorCodes.InvalidArtifact, $"Referenced artifact '{item.Include}' is not a valid .pgpkg: {ex.Message}");
        }
        catch (Exception ex)
        {
            Report(ReferenceErrorCodes.InvalidArtifact, $"Failed to read referenced artifact '{item.Include}': {ex.Message}");
        }
    }

    private void ResolvePackage(ProjectReferenceItem item)
    {
        var (pgpkgPath, _) = NuGetPackageLocator.Locate(_packagesFolder, item.Include, item.Version);
        if (pgpkgPath is null)
        {
            var ver = item.Version is null ? "" : $" (version {item.Version})";
            Report(ReferenceErrorCodes.PackageNotResolved,
                $"PackageReference '{item.Include}'{ver} could not be resolved: no restored .pgpkg was found under the " +
                $"NuGet global packages folder ('{_packagesFolder}'). Run `dotnet restore` (or `dotnet add package`) so the " +
                "package is restored, and ensure it was produced by `dotnet pack` on a PgProj project (it must carry pgpkg/<Name>.pgpkg).");
            _done.Add(item.Key);   // don't re-report the same package id repeatedly
            return;
        }

        try
        {
            var pkg = PgPkg.Read(pgpkgPath);   // verifies the integrity checksum
            Inject(item, pkg.Model);           // reference-only — never enters the comparer's model (no deploy DDL)
        }
        catch (PgPkgFormatException ex)
        {
            Report(ReferenceErrorCodes.InvalidArtifact,
                $"PackageReference '{item.Include}' restored an invalid .pgpkg ('{pgpkgPath}'): {ex.Message}");
            _done.Add(item.Key);
        }
        catch (Exception ex)
        {
            Report(ReferenceErrorCodes.InvalidArtifact,
                $"Failed to read the .pgpkg restored for PackageReference '{item.Include}' ('{pgpkgPath}'): {ex.Message}");
            _done.Add(item.Key);
        }
    }

    private void Inject(ProjectReferenceItem item, DatabaseModel model)
    {
        _done.Add(item.Key);
        _resolved.Add(new ResolvedReference(item, model));
    }

    private void Report(string code, string message) => _diagnostics.Add(new ReferenceDiagnostic(code, message));
}
