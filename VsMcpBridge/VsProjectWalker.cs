using EnvDTE;

namespace VsMcpBridge;

/// <summary>
/// A single project (or solution folder) discovered in a Visual Studio solution,
/// including where it lives in the solution folder hierarchy.
/// </summary>
public sealed record VsProjectInfo(
    string Name,
    string UniqueName,
    string FullPath,
    string Kind,
    string KindName,
    bool IsSolutionFolder,
    string FolderPath
)
{
    /// <summary>True when this is a real, buildable project rather than a folder.</summary>
    public bool IsBuildable => !IsSolutionFolder;
}

/// <summary>
/// Enumerates every project in a solution, including projects nested inside
/// solution folders.
///
/// DTE's <c>Solution.Projects</c> only yields the <em>top level</em> entries. In any
/// solution that uses solution folders (which is most real-world solutions) that
/// means the top level is just a handful of folders and the actual projects are
/// invisible. This walker recurses through <c>Project.ProjectItems</c> so callers
/// see every real project.
///
/// Everything here must run on the Visual Studio COM thread - the returned
/// <see cref="Project"/> objects are apartment-bound COM references.
/// </summary>
public static class VsProjectWalker
{
    /// <summary>EnvDTE kind GUID for a solution folder project (verified against VS 18.x).</summary>
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    /// <summary>EnvDTE kind GUID for a virtual folder project (Miscellaneous Files).</summary>
    private const string VirtualFolderKind = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";

    // Guards against a malformed or self-referencing solution tree.
    private const int MaxDepth = 32;

    private static readonly Dictionary<string, string> KnownKinds = new()
    {
        ["{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"] = "CSharpProject",
        ["{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}"] = "VCProject",
        ["{E24C65DC-7377-472B-9ABA-BC803B73C61A}"] = "VCProject",
        ["{3AC096D0-A1C2-E12C-1390-A8335801FDAB}"] = "VCProject",
        ["{F184B08F-C81C-45F6-A57F-5ABD9991F28F}"] = "VBProject",
        ["{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}"] = "FSharpProject",
    };

    public static string DescribeKind(string kind) =>
        KnownKinds.TryGetValue(kind, out var name) ? name : "Project";

    /// <summary>
    /// Walks the whole solution and returns only the real, buildable projects.
    /// </summary>
    public static List<VsProjectInfo> Enumerate(Solution? solution, bool includeSolutionFolders = false)
    {
        var results = new List<VsProjectInfo>();
        foreach (var (info, _) in Walk(solution, includeSolutionFolders)) results.Add(info);
        return results;
    }

    /// <summary>
    /// Finds one project by name, unique name or full path, returning the live COM
    /// object so callers can operate on it directly.
    ///
    /// <c>vs_build_project</c> uses the returned object's Name / UniqueName to try the
    /// shapes <c>SolutionBuild.BuildProject</c> accepts, which differ between .sln and .slnx.
    /// </summary>
    public static Project? Resolve(Solution? solution, string nameOrPath)
    {
        if (solution == null || string.IsNullOrWhiteSpace(nameOrPath)) return null;

        var all = Walk(solution, includeSolutionFolders: false);
        var token = nameOrPath.Trim();

        foreach (var candidate in all)
        {
            if (candidate.Info.Name.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                candidate.Info.UniqueName.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                candidate.Info.FullPath.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Project;
            }
        }

        return null;
    }

    private static List<(VsProjectInfo Info, Project Project)> Walk(Solution? solution, bool includeSolutionFolders)
    {
        var results = new List<(VsProjectInfo, Project)>();
        if (solution == null) return results;
        WalkTopLevel(solution.Projects, results, includeSolutionFolders, 0);
        return results;
    }

    private static void WalkTopLevel(Projects? projects, List<(VsProjectInfo, Project)> acc, bool includeFolders, int depth)
    {
        if (projects == null || depth > MaxDepth) return;

        foreach (Project project in EnumerateProjects(projects))
        {
            if (project == null) continue;
            AddProject(project, "", acc, includeFolders, depth);
        }
    }

    private static void AddProject(Project project, string folderPath, List<(VsProjectInfo Info, Project Project)> acc, bool includeFolders, int depth)
    {
        if (project == null || depth > MaxDepth) return;

        string name = SafeGet(() => project.Name) ?? "(unnamed)";
        string kind = SafeGet(() => project.Kind) ?? string.Empty;

        bool folderByKind =
            string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, VirtualFolderKind, StringComparison.OrdinalIgnoreCase);

        var children = EnumerateChildProjects(project);

        // Treat a node as a folder if its kind says so OR if it actually contains
        // projects. The structural test keeps this correct even if a future Visual
        // Studio renumbers the folder kind GUID.
        bool isFolder = folderByKind || children.Count > 0;

        if (includeFolders || !isFolder)
        {
            acc.Add((new VsProjectInfo(
                Name: name,
                UniqueName: SafeGet(() => project.UniqueName) ?? name,
                FullPath: SafeGet(() => project.FullName) ?? string.Empty,
                Kind: kind,
                KindName: isFolder ? "SolutionFolder" : DescribeKind(kind),
                IsSolutionFolder: isFolder,
                FolderPath: folderPath), project));
        }

        if (children.Count == 0) return;

        var childPath = folderPath.Length == 0 ? name : $"{folderPath}/{name}";
        foreach (var child in children)
        {
            AddProject(child, childPath, acc, includeFolders, depth + 1);
        }
    }

    /// <summary>
    /// Returns the projects nested directly inside <paramref name="project"/>.
    /// A solution folder holds both real projects and loose files; only projects expose
    /// a SubProject, so files are naturally skipped.
    /// </summary>
    private static List<Project> EnumerateChildProjects(Project project)
    {
        var children = new List<Project>();

        ProjectItems? items = null;
        try { items = project.ProjectItems; } catch { return children; }
        if (items == null) return children;

        foreach (ProjectItem item in EnumerateItems(items))
        {
            if (item == null) continue;

            Project? child = null;
            try { child = item.SubProject; } catch { /* ignore */ }
            if (child == null)
            {
                try { child = item.Object as Project; } catch { /* ignore */ }
            }

            if (child != null) children.Add(child);
        }

        return children;
    }

    /// <summary>
    /// Materialises a DTE collection into a list. DTE collections throw on a
    /// half-dead or unloading project, so enumeration has to be defensive.
    /// </summary>
    private static List<Project> EnumerateProjects(Projects? projects)
    {
        var list = new List<Project>();
        if (projects == null) return list;
        try
        {
            foreach (Project p in projects)
            {
                if (p != null) list.Add(p);
            }
        }
        catch
        {
            // Partial enumeration is better than none.
        }
        return list;
    }

    private static List<ProjectItem> EnumerateItems(ProjectItems? items)
    {
        var list = new List<ProjectItem>();
        if (items == null) return list;
        try
        {
            foreach (ProjectItem i in items)
            {
                if (i != null) list.Add(i);
            }
        }
        catch
        {
            // Partial enumeration is better than none.
        }
        return list;
    }

    private static T? SafeGet<T>(Func<T> getter) where T : class
    {
        try { return getter(); } catch { return null; }
    }
}
