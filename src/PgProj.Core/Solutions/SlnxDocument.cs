using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PgProj.Core.Solutions;

/// <summary>
/// A minimal, deterministic model of an XML solution file (<c>.slnx</c>) — the format Visual Studio
/// 2022 17.13+ and the <c>dotnet</c> CLI load natively. It models exactly what the grouping feature
/// needs: solution folders (by their full <c>/a/b/</c> name) and project paths. Load preserves every
/// project already in the file (any extension, not just <c>.pgproj</c>); Save rewrites the document in
/// canonical form — folders sorted, projects sorted, forward-slash paths — so regeneration is
/// byte-stable and diffs stay readable.
/// </summary>
public sealed class SlnxDocument
{
    // Folder name (canonical "/a/b/" form, "" = solution root) → project paths (forward-slash, relative).
    private readonly SortedDictionary<string, SortedSet<string>> _foldersToProjects;

    private SlnxDocument(SortedDictionary<string, SortedSet<string>> foldersToProjects)
        => _foldersToProjects = foldersToProjects;

    /// <summary>An empty solution.</summary>
    public static SlnxDocument Empty() => new(NewMap());

    /// <summary>All project paths in the document (relative, forward-slash), sorted.</summary>
    public IReadOnlyList<string> Projects
        => _foldersToProjects.Values.SelectMany(p => p).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Folder → projects view (folder "" is the solution root). Folder names are canonical
    /// (<c>/a/b/</c>); projects are sorted within each folder.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Folders
        => _foldersToProjects.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList());

    /// <summary>
    /// Adds a project under <paramref name="folderName"/> ("" or null = solution root). Paths are
    /// normalized to forward slashes; re-adding an already-present path is a no-op (returns false) so
    /// regeneration over an existing solution is idempotent.
    /// </summary>
    public bool AddProject(string projectPath, string? folderName = null)
    {
        var path = NormalizePath(projectPath);
        var folder = NormalizeFolderName(folderName);

        if (_foldersToProjects.Values.Any(set => set.Contains(path)))
            return false;

        if (!_foldersToProjects.TryGetValue(folder, out var set))
            _foldersToProjects[folder] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        return set.Add(path);
    }

    /// <summary>Loads an existing <c>.slnx</c>, keeping all folders and projects it declares.</summary>
    public static SlnxDocument Load(string path)
    {
        var doc = XDocument.Load(path);
        return Parse(doc, path);
    }

    /// <summary>Parses <c>.slnx</c> XML text (for tests and in-memory round-trips).</summary>
    public static SlnxDocument Parse(string xml) => Parse(XDocument.Parse(xml), "<memory>");

    private static SlnxDocument Parse(XDocument doc, string origin)
    {
        var root = doc.Root ?? throw new InvalidDataException($"{origin}: not a solution file (no root element).");
        if (root.Name.LocalName != "Solution")
            throw new InvalidDataException($"{origin}: not a .slnx solution (root element is <{root.Name.LocalName}>, expected <Solution>).");

        var map = NewMap();
        foreach (var project in root.Elements("Project"))
            AddParsed(map, "", project, origin);
        foreach (var folder in root.Elements("Folder"))
        {
            var name = NormalizeFolderName(folder.Attribute("Name")?.Value);
            // An empty folder is preserved as a key so Save round-trips it.
            if (!map.ContainsKey(name) && name.Length > 0)
                map[name] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in folder.Elements("Project"))
                AddParsed(map, name, project, origin);
        }
        return new SlnxDocument(map);
    }

    private static void AddParsed(SortedDictionary<string, SortedSet<string>> map, string folder, XElement project, string origin)
    {
        var path = project.Attribute("Path")?.Value
            ?? throw new InvalidDataException($"{origin}: a <Project> element has no Path attribute.");
        if (!map.TryGetValue(folder, out var set))
            map[folder] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(NormalizePath(path));
    }

    /// <summary>
    /// The canonical <c>.slnx</c> XML: two-space indent, sorted folders/projects, LF line endings
    /// (byte-stable across platforms), trailing newline.
    /// </summary>
    public string ToXml()
    {
        var root = new XElement("Solution");
        if (_foldersToProjects.TryGetValue("", out var rootProjects))
            foreach (var p in rootProjects)
                root.Add(new XElement("Project", new XAttribute("Path", p)));
        foreach (var (folder, projects) in _foldersToProjects.Where(kv => kv.Key.Length > 0))
        {
            var element = new XElement("Folder", new XAttribute("Name", folder));
            foreach (var p in projects)
                element.Add(new XElement("Project", new XAttribute("Path", p)));
            root.Add(element);
        }
        return root.ToString().ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Writes the canonical form to <paramref name="path"/>.</summary>
    public void Save(string path) => File.WriteAllText(path, ToXml());

    /// <summary>Forward slashes, no leading "./".</summary>
    public static string NormalizePath(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>Canonical folder form: forward slashes, one leading and one trailing slash ("/a/b/").</summary>
    public static string NormalizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Replace('\\', '/').Trim('/');
        return n.Length == 0 ? "" : "/" + n + "/";
    }

    private static SortedDictionary<string, SortedSet<string>> NewMap()
        => new(StringComparer.OrdinalIgnoreCase);
}
