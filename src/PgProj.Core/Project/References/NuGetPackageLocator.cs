using System;
using System.IO;
using System.Linq;

namespace PgProj.Core.Project.References;

/// <summary>
/// Locates a restored <c>.pgpkg</c> inside the NuGet global packages folder for a <c>&lt;PackageReference/&gt;</c>
/// (EP-REF #148). A PgProj package produced by <c>dotnet pack</c> carries its package under
/// <c>pgpkg/&lt;Name&gt;.pgpkg</c>; once NuGet restore has expanded it into
/// <c>&lt;globalPackages&gt;/&lt;id&gt;/&lt;version&gt;/</c>, this finds the embedded artifact so the resolver can load it.
/// Restore itself is MSBuild/NuGet's job (a normal <c>dotnet restore</c>); this only consumes the result.
/// </summary>
public static class NuGetPackageLocator
{
    /// <summary>
    /// The NuGet global packages folder: <c>$NUGET_PACKAGES</c> when set, else <c>~/.nuget/packages</c>.
    /// </summary>
    public static string DefaultGlobalPackagesFolder()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".nuget", "packages");
    }

    /// <summary>
    /// Find the restored <c>.pgpkg</c> for package <paramref name="id"/> (+ optional <paramref name="version"/>)
    /// under <paramref name="packagesFolder"/>. An exact version is tried directly; a null/range version (or a
    /// missing exact folder) falls back to the highest installed version that actually carries a <c>.pgpkg</c>.
    /// Returns the artifact path and the resolved version, or <c>(null, null)</c> when nothing is restored.
    /// </summary>
    public static (string? PgpkgPath, string? ResolvedVersion) Locate(string packagesFolder, string id, string? version)
    {
        if (string.IsNullOrWhiteSpace(id)) return (null, null);
        // NuGet lower-cases id + version folder names.
        var idDir = Path.Combine(packagesFolder, id.ToLowerInvariant());
        if (!Directory.Exists(idDir)) return (null, null);

        if (IsExactVersion(version))
        {
            var exact = Path.Combine(idDir, version!.ToLowerInvariant());
            var pgpkg = FindPgpkg(exact);
            return pgpkg is null ? (null, null) : (pgpkg, Path.GetFileName(exact));
        }

        // No exact version (or unparsable range): the highest installed version carrying a .pgpkg wins.
        var best = Directory.EnumerateDirectories(idDir)
            .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Ver is not null && FindPgpkg(x.Dir) is not null)
            .OrderByDescending(x => x.Ver)
            .FirstOrDefault();
        if (best.Dir is null) return (null, null);
        return (FindPgpkg(best.Dir), Path.GetFileName(best.Dir));
    }

    /// <summary>The single <c>.pgpkg</c> under a restored version dir (preferring the <c>pgpkg/</c> folder), or null.</summary>
    private static string? FindPgpkg(string versionDir)
    {
        if (!Directory.Exists(versionDir)) return null;
        var pgpkgDir = Path.Combine(versionDir, "pgpkg");
        var search = Directory.Exists(pgpkgDir) ? pgpkgDir : versionDir;
        return Directory.EnumerateFiles(search, "*.pgpkg", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>True when <paramref name="version"/> is a single concrete version (not null, not a range like <c>[1,2)</c>).</summary>
    private static bool IsExactVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.IndexOfAny(new[] { '[', ']', '(', ')', ',', '*' }) < 0;

    /// <summary>Parse the numeric core of a NuGet version folder name for ordering (prerelease tail dropped).</summary>
    private static Version? ParseVersion(string name)
    {
        var dash = name.IndexOf('-');
        var core = dash >= 0 ? name[..dash] : name;
        return Version.TryParse(core, out var v) ? v : null;
    }
}
