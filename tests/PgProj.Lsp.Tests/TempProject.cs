using System;
using System.IO;
using PgProj.Lsp.Workspace;

namespace PgProj.Lsp.Tests;

/// <summary>
/// A throwaway on-disk .pgproj workspace for the handler tests: a temp dir with a minimal manifest and the
/// SQL files written in. Disposable so each test gets an isolated tree (deleted on Dispose). Mirrors the
/// repo's temp-dir test convention used elsewhere in the suite.
/// </summary>
public sealed class TempProject : IDisposable
{
    public string Dir { get; }
    public string ProjectFilePath { get; }

    public TempProject(string defaultSchema = "public", string name = "TestDb")
    {
        Dir = Path.Combine(Path.GetTempPath(), "pgproj-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        ProjectFilePath = Path.Combine(Dir, name + ".pgproj");
        File.WriteAllText(ProjectFilePath, $"""
        <Project Sdk="PgProj.Sdk/0.1.0">
          <PropertyGroup>
            <Name>{name}</Name>
            <DefaultSchema>{defaultSchema}</DefaultSchema>
          </PropertyGroup>
          <ItemGroup>
            <Build Include="**/*.sql" />
          </ItemGroup>
        </Project>
        """);
    }

    /// <summary>Writes (or overwrites) a .sql file and returns its absolute path.</summary>
    public string WriteSql(string relativeName, string sql)
    {
        var path = Path.Combine(Dir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
        return path;
    }

    /// <summary>The file:// URI for a relative .sql file in this workspace.</summary>
    public string UriFor(string relativeName) => DocumentUri.FromPath(Path.Combine(Dir, relativeName));

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
