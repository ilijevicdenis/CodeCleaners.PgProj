namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>
/// A throwaway <c>.pgproj</c> on disk: a temp directory with a manifest the CLI's DatabaseProject.Load
/// reads directly (no MSBuild SDK import needed — that is only for `dotnet build`). Add <c>.sql</c>
/// files, then point the CLI at <see cref="ProjectFile"/>. Deleted on dispose.
/// </summary>
public sealed class TempProject : IDisposable
{
    public string Dir { get; }
    public string Name { get; }
    public string ProjectFile { get; }

    private TempProject(string dir, string name, string projectFile)
    {
        Dir = dir;
        Name = name;
        ProjectFile = projectFile;
    }

    /// <param name="targetVersion">Emitted as &lt;TargetPostgresVersion&gt; when set (drives the PGV gate).</param>
    /// <param name="extraItemGroup">Raw XML spliced into the manifest (references, pre/post-deploy, SqlCmdVariable).</param>
    public static TempProject Create(string name, int? targetVersion = 18, string defaultSchema = "app",
        string? extraItemGroup = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pgproj-bb", name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // Pre-create bin/ — the `script`/`publish -o` verbs write the output file but do NOT create its
        // directory (unlike `build`), so tests that target bin/ need it to already exist.
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var versionLine = targetVersion is null ? "" : $"\n    <TargetPostgresVersion>{targetVersion}</TargetPostgresVersion>";
        var manifest =
            $"""
            <Project>
              <PropertyGroup>
                <Name>{name}</Name>
                <DefaultSchema>{defaultSchema}</DefaultSchema>{versionLine}
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            {extraItemGroup ?? ""}
            </Project>
            """;
        var projectFile = Path.Combine(dir, name + ".pgproj");
        File.WriteAllText(projectFile, manifest);
        return new TempProject(dir, name, projectFile);
    }

    /// <summary>Write a .sql file (relative path under the project dir) and return its absolute path.</summary>
    public string AddSql(string relativePath, string sql)
    {
        var path = Path.Combine(Dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
        return path;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Dir, relativePath));

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
