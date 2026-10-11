using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using EnvDTE;
using EnvDTE80;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace VsMcpBridge;

/// <summary>
/// The MCP tool surface for controlling a running Visual Studio instance.
///
/// Every method body runs on <see cref="VsComHost"/>'s dedicated STA thread so that the
/// busy/retry COM message filter is actually in effect and all DTE access is serialised.
///
/// Conventions:
/// <list type="bullet">
///   <item>Expected failures (busy, busy-wait timeout, no matching instance) are thrown,
///   so the MCP SDK reports them to the agent with <c>isError = true</c> and a usable message.</item>
///   <item>Genuine "there is nothing here" states (no solution open, empty pane) are returned
///   as normal text, because they are not errors.</item>
/// </list>
/// </summary>
[McpServerToolType]
public class VisualStudioTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Windows paths and non-ASCII text should not be escaped into unreadable soup.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string TargetInstanceDescription =
        "Optional target: a Visual Studio process ID, or a substring of the solution name/path. " +
        "When omitted the instance with an open solution is used.";

    /// <summary>A waited-on build or rebuild can legitimately run far longer than the default COM timeout.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromHours(2);

    // ---------------------------------------------------------------- discovery

    [McpServerTool(ReadOnly = true), Description("List all running Visual Studio instances with process ID, DTE version, edition, open solution, main window title and build state. " +
                                "Visual Studio 2019/2022/2026 register under major DTE versions 16.x/17.x/18.x respectively.")]
    public static string VsListInstances() => Execute(() =>
    {
        var instances = VsInstanceLocator.GetRunningInstances();
        if (instances.Count == 0)
        {
            return "No running Visual Studio instances with DTE automation were found. " +
                   "Open a solution in Visual Studio and try again." +
                   VsInstanceLocator.IntegrityHint();
        }

        var list = instances.Select(i => new
        {
            processId = i.Info.ProcessId,
            dteVersion = i.Info.Version,
            fullVersion = i.Info.FullVersion,
            solutionOpen = i.Info.IsSolutionOpen,
            solutionName = i.Info.SolutionName,
            solutionPath = i.Info.SolutionPath,
            mainWindowTitle = i.Info.MainWindowTitle,
            buildState = i.Info.BuildState
        });

        return JsonSerializer.Serialize(list, Json);
    });

    [McpServerTool(ReadOnly = true), Description("Get information about the active solution: path, open state, active configuration and platform, all available configurations, " +
                                "and the count of real projects (including projects nested inside solution folders).")]
    public static string VsGetSolutionInfo(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var solution = dte.Solution;

        if (solution == null || !solution.IsOpen)
        {
            return "Visual Studio is running, but no solution is currently open.";
        }

        var activeConfig = Safe(() => solution.SolutionBuild?.ActiveConfiguration);
        var configName = activeConfig is SolutionConfiguration2 c2
            ? $"{c2.Name} | {c2.PlatformName}"
            : activeConfig?.Name ?? "Unknown";

        var projects = VsProjectWalker.Enumerate(solution);
        var folders = VsProjectWalker.Enumerate(solution, includeSolutionFolders: true)
            .Count(p => p.IsSolutionFolder);

        var configurations = new List<string>();
        try
        {
            foreach (SolutionConfiguration c in solution.SolutionBuild.SolutionConfigurations)
            {
                if (c is SolutionConfiguration2 sc2) configurations.Add($"{sc2.Name} | {sc2.PlatformName}");
                else if (c != null) configurations.Add(c.Name);
            }
        }
        catch { /* enumeration is best-effort */ }

        var projectNames = projects.Select(p => p.Name).ToList();
        const int sampleSize = 100;
        var capped = projectNames.Count > sampleSize;

        var info = new
        {
            solutionPath = solution.FullName,
            isOpen = true,
            activeConfiguration = configName,
            availableConfigurations = configurations,
            projectCount = projects.Count,
            solutionFolderCount = folders,
            // Keep this payload bounded on huge solutions - vs_list_projects has the full list.
            projects = capped ? projectNames.Take(sampleSize).ToList() : projectNames,
            projectsTruncated = capped,
            isBuilding = solution.SolutionBuild?.BuildState == vsBuildState.vsBuildStateInProgress
        };

        return JsonSerializer.Serialize(info, Json);
    });

    [McpServerTool(ReadOnly = true), Description("List every project in the solution, including projects nested inside solution folders. " +
                                "Returns the folder path, full project path, project kind, and whether it is a solution folder. " +
                                "Use this to find the exact project name to pass to vs_build_project.")]
    public static string VsListProjects(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null,
        [Description("When true, also include the solution folder nodes themselves so the hierarchy is visible. Default is false.")]
        bool includeSolutionFolders = false,
        [Description("Optional case-insensitive substring filter on the project name or its folder path.")]
        string? filter = null,
        [Description("Maximum number of projects to return. Default is 200. A 50-project solution returns all of them.")]
        int maxResults = 200) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        // Always enumerate the full tree so the totals below are accurate even when
        // the caller asked for folders to be hidden or passed a filter.
        var everything = VsProjectWalker.Enumerate(dte.Solution, includeSolutionFolders: true);
        var all = includeSolutionFolders
            ? everything
            : everything.Where(p => p.IsBuildable).ToList();

        var matched = string.IsNullOrWhiteSpace(filter)
            ? all
            : all.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (maxResults is < 1 or > 5000) maxResults = 200;
        var returned = matched.Count > maxResults ? matched.Take(maxResults).ToList() : matched;

        var payload = returned.Select(p => new
        {
            p.Name,
            p.UniqueName,
            p.FullPath,
            p.KindName,
            p.IsSolutionFolder,
            folderPath = p.FolderPath.Length == 0 ? null : p.FolderPath
        });

        return JsonSerializer.Serialize(new
        {
            totalProjects = everything.Count(p => p.IsBuildable),
            totalSolutionFolders = everything.Count(p => p.IsSolutionFolder),
            filter,
            matchedCount = matched.Count,
            returnedCount = returned.Count,
            truncated = matched.Count > returned.Count,
            projects = payload
        }, Json);
    });

    // -------------------------------------------------------------------- build

    [McpServerTool(Destructive = false), Description("Build the open solution in Visual Studio. Returns the number of projects that failed, " +
                                "so the result reflects reality rather than just that the call was accepted.")]
    public static string VsBuildSolution(
        [Description("Wait synchronously until the build finishes. Default is true.")]
        bool waitForCompletion = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        var build = dte.Solution.SolutionBuild;
        build.Build(WaitForBuildToFinish: waitForCompletion);

        if (!waitForCompletion)
        {
            return JsonSerializer.Serialize(new
            {
                started = true,
                completed = false,
                note = "Build started in the background. Poll vs_get_build_state, then read vs_get_error_list for diagnostics."
            }, Json);
        }

        return BuildResult(dte, "Build");
    }, waitForCompletion ? BuildTimeout : null);

    [McpServerTool, Description("Clean and then rebuild the open solution in Visual Studio. Returns the number of projects that failed.")]
    public static string VsRebuildSolution(
        [Description("Wait synchronously until the rebuild finishes. Default is true.")]
        bool waitForCompletion = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        var build = dte.Solution.SolutionBuild;
        build.Clean(WaitForCleanToFinish: true);
        build.Build(WaitForBuildToFinish: waitForCompletion);

        if (!waitForCompletion)
        {
            return JsonSerializer.Serialize(new
            {
                started = true,
                completed = false,
                note = "Rebuild started in the background. Poll vs_get_build_state, then read vs_get_error_list."
            }, Json);
        }

        return BuildResult(dte, "Rebuild");
    }, BuildTimeout); // The clean step always waits.

    [McpServerTool, Description("Clean the open solution in Visual Studio.")]
    public static string VsCleanSolution(
        [Description("Wait synchronously until the clean finishes. Default is true.")]
        bool waitForCompletion = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        dte.Solution.SolutionBuild.Clean(WaitForCleanToFinish: waitForCompletion);
        return waitForCompletion
            ? "Clean completed successfully."
            : "Clean started in the background.";
    }, waitForCompletion ? BuildTimeout : null);

    [McpServerTool(Destructive = false), Description("Build a single project by name, unique name or full path, including projects nested inside solution folders. " +
                                "Uses the active solution configuration and reports which build call Visual Studio accepted.")]
    public static string VsBuildProject(
        [Description("Name, UniqueName or full path of the project (see vs_list_projects).")]
        string projectName,
        [Description("Wait synchronously until the build finishes. Default is true.")]
        bool waitForCompletion = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        var projects = VsProjectWalker.Enumerate(dte.Solution);
        var target = VsProjectWalker.Resolve(dte.Solution, projectName);

        if (target == null)
        {
            // Name the closest candidates rather than just saying "not found".
            var near = projects.Where(p =>
                    p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .Take(10)
                .ToList();

            var hint = near.Count > 0
                ? $" Did you mean: {string.Join(", ", near)}?"
                : string.Empty;

            throw new McpException(
                $"Project '{projectName}' was not found in the open solution " +
                $"'{dte.Solution.FullName}' (which has {projects.Count} projects).{hint} " +
                "Call vs_list_projects to see every project.");
        }

        var build = dte.Solution.SolutionBuild;
        var active = Safe(() => build.ActiveConfiguration);
        var configName = active?.Name ?? "Debug";
        var platform = (active as SolutionConfiguration2)?.PlatformName;
        var usedConfig = string.IsNullOrWhiteSpace(platform) || platform == "Any CPU"
            ? configName
            : $"{configName}|{platform}";

        // EnvDTE exposes exactly one per-project build entry point:
        // SolutionBuild.BuildProject(config, projectName, wait). It is picky - the accepted
        // combination of configuration string and project name varies between Visual Studio
        // product waves and between .sln and .slnx solutions, and a wrong guess surfaces as
        // a bare E_FAIL (0x80004005).
        //
        // So try the known-good shapes in order and report which one Visual Studio accepted,
        // rather than failing on the first guess.
        var attempts = new[]
        {
            (Config: configName, Name: target.UniqueName),
            (Config: usedConfig,   Name: target.UniqueName),
            (Config: configName,   Name: target.Name),
            (Config: usedConfig,   Name: target.Name),
        };

        string? accepted = null;
        string? lastError = null;

        foreach (var attempt in attempts)
        {
            try
            {
                build.BuildProject(attempt.Config, attempt.Name, waitForCompletion);
                accepted = $"{attempt.Config} -> {attempt.Name}";
                break;
            }
            catch (Exception ex)
            {
                lastError = VsComError.Describe(ex);
            }
        }

        if (accepted == null)
        {
            throw new McpException(
                $"Visual Studio refused to build project '{projectName}' in configuration " +
                $"'{usedConfig}'. Last error: {lastError}");
        }

        var payload = new
        {
            project = target.Name,
            uniqueName = target.UniqueName,
            fullPath = Safe(() => target.FullName),
            configuration = usedConfig,
            acceptedCall = accepted,
            completed = waitForCompletion,
            lastBuildFailedProjects = waitForCompletion ? SafeInt(() => build.LastBuildInfo) : null,
            note = waitForCompletion
                ? "Read vs_get_error_list to see the compiler diagnostics for this build."
                : "Build started in the background; poll vs_get_build_state."
        };

        return JsonSerializer.Serialize(payload, Json);
    }, waitForCompletion ? BuildTimeout : null);

    [McpServerTool(ReadOnly = true), Description("Get the current solution build state (in progress, not started, done) plus the last build's failed project count.")]
    public static string VsGetBuildState(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        if (dte.Solution == null || !dte.Solution.IsOpen)
        {
            return "No solution is currently open in Visual Studio.";
        }

        var build = dte.Solution.SolutionBuild;
        var state = build.BuildState switch
        {
            vsBuildState.vsBuildStateInProgress => "InProgress",
            vsBuildState.vsBuildStateNotStarted => "NotStarted",
            vsBuildState.vsBuildStateDone => "Done",
            _ => "Unknown"
        };

        return JsonSerializer.Serialize(new
        {
            solution = dte.Solution.FullName,
            buildState = state,
            isBuilding = state == "InProgress",
            lastBuildFailedProjects = SafeInt(() => build.LastBuildInfo)
        }, Json);
    });

    // -------------------------------------------------------------- diagnostics

    [McpServerTool(ReadOnly = true), Description("Read items from the Visual Studio Error List (compiler errors, warnings and messages) with file, line and column. " +
                                "Returns per-level counts so the agent can triage before pulling detail. " +
                                "If hiddenLevels is present, the user has hidden those levels in the Error List and they are not counted.")]
    public static string VsGetErrorList(
        [Description("Optional filter: 'error', 'warning', 'message' or 'all' (plurals accepted). Default is 'all'.")]
        string filter = "all",
        [Description("Maximum number of items to return. Default is 50.")]
        int maxResults = 50,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var errorList = dte.ToolWindows.ErrorList;
        var errorItems = errorList?.ErrorItems;

        if (errorItems == null)
        {
            return "The Visual Studio Error List is not available.";
        }

        var wanted = NormalizeFilter(filter, out var filterError);
        if (filterError != null) throw new McpException(filterError);

        int total = 0;
        try { total = errorItems.Count; } catch { /* ignore */ }

        // ErrorItems only contains what the Error List currently shows, so levels the user has
        // toggled off are missing from every count below. Say so instead of reporting a false zero.
        var hidden = new List<string>();
        if (Safe(() => !errorList!.ShowErrors)) hidden.Add("errors");
        if (Safe(() => !errorList!.ShowWarnings)) hidden.Add("warnings");
        if (Safe(() => !errorList!.ShowMessages)) hidden.Add("messages");
        var hiddenLevels = hidden.Count == 0 ? null : hidden;

        if (total == 0)
        {
            return JsonSerializer.Serialize(new
            {
                totalCount = 0,
                counts = new { errors = 0, warnings = 0, messages = 0, other = 0 },
                returnedCount = 0,
                hiddenLevels,
                note = "The Error List is empty. If you just built, re-read it - or run vs_get_output_pane with paneName 'Build'."
            }, Json);
        }

        if (maxResults is < 1 or > 5000) maxResults = 50;

        var items = new List<object>();
        int errors = 0, warnings = 0, messages = 0, other = 0;
        int matched = 0;

        for (int i = 1; i <= total; i++)
        {
            ErrorItem item;
            try { item = errorItems.Item(i); }
            catch { continue; }
            if (item == null) continue;

            var level = item.ErrorLevel switch
            {
                vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
                vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
                vsBuildErrorLevel.vsBuildErrorLevelLow => "Message",
                _ => "Info"
            };

            switch (level)
            {
                case "Error": errors++; break;
                case "Warning": warnings++; break;
                case "Message": messages++; break;
                default: other++; break;
            }

            if (wanted != "all" && !level.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            matched++;
            if (items.Count >= maxResults) continue;

            items.Add(new
            {
                level,
                description = Safe(() => item.Description),
                fileName = Safe(() => item.FileName),
                line = SafeInt(() => item.Line),
                column = SafeInt(() => item.Column),
                project = Safe(() => item.Project)
            });
        }

        return JsonSerializer.Serialize(new
        {
            totalCount = total,
            counts = new { errors, warnings, messages, other },
            filter = wanted,
            matchedCount = matched,
            returnedCount = items.Count,
            // Truncation means we had MORE matches than we returned - not that the
            // filter happened to match nothing.
            truncated = matched > items.Count,
            hiddenLevels,
            errors = items
        }, Json);
    });

    [McpServerTool(ReadOnly = true), Description("List the names of every available Output window pane, so you know what vs_get_output_pane can read.")]
    public static string VsListOutputPanes(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var panes = dte.ToolWindows.OutputWindow?.OutputWindowPanes;
        if (panes == null) return "The Visual Studio Output window is not available.";

        var names = new List<string>();
        for (int i = 1; i <= panes.Count; i++)
        {
            try
            {
                var name = panes.Item(i)?.Name;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            catch { /* skip a pane that is being torn down */ }
        }

        return JsonSerializer.Serialize(new
        {
            paneCount = names.Count,
            panes = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        }, Json);
    });

    [McpServerTool(ReadOnly = true), Description("Read text from a Visual Studio Output window pane such as 'Build' or 'Debug'. " +
                                "Output is truncated to the tail by default, because pane contents are usually unbounded.")]
    public static string VsGetOutputPane(
        [Description("Name of the output pane, e.g. 'Build', 'Debug', 'General'. Use vs_list_output_panes to see all of them.")]
        string paneName = "Build",
        [Description("Maximum number of characters to return. Default is 20000.")]
        int maxChars = 20000,
        [Description("When true (default) return the most recent characters; when false return the beginning.")]
        bool tail = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var panes = dte.ToolWindows.OutputWindow?.OutputWindowPanes;
        if (panes == null) return "The Visual Studio Output window is not available.";

        OutputWindowPane? targetPane = null;
        var available = new List<string>();
        for (int i = 1; i <= panes.Count; i++)
        {
            try
            {
                var pane = panes.Item(i);
                if (pane == null) continue;
                if (!string.IsNullOrEmpty(pane.Name)) available.Add(pane.Name);
                if (pane.Name.Equals(paneName, StringComparison.OrdinalIgnoreCase)) targetPane = pane;
            }
            catch { /* skip */ }
        }

        if (targetPane == null)
        {
            return $"Output pane '{paneName}' was not found. " +
                   $"Available panes: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}";
        }

        if (maxChars is < 100 or > 1_000_000) maxChars = 20000;

        string content;
        try
        {
            var textDoc = targetPane.TextDocument;
            var start = textDoc.StartPoint.CreateEditPoint();
            content = start.GetText(textDoc.EndPoint) ?? string.Empty;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            // A pane that has never received output has no text buffer yet, and DTE reports E_FAIL.
            content = string.Empty;
        }
        catch (Exception ex)
        {
            throw new VsInteropException(
                $"Could not read the '{paneName}' output pane: {ex.Message}", ex);
        }

        if (content.Length == 0) return $"(Output pane '{paneName}' is empty)";

        var truncated = false;
        if (content.Length > maxChars)
        {
            truncated = true;
            content = tail
                ? content[^maxChars..]
                : content[..maxChars];
        }

        if (truncated)
        {
            content = tail
                ? $"...(truncated, showing the last {maxChars} characters)...{Environment.NewLine}{content}"
                : $"{content}{Environment.NewLine}...(truncated at {maxChars} characters; pass tail=true for the end of the pane)...";
        }

        return content;
    });

    // -------------------------------------------------------- editor navigation

    [McpServerTool(ReadOnly = true), Description("Get information about the currently active document in Visual Studio, including the window caption and caret position.")]
    public static string VsGetActiveDocument(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var doc = dte.ActiveDocument;

        if (doc == null)
        {
            return "No document is currently active in Visual Studio.";
        }

        int? line = null, column = null;
        if (doc.Selection is TextSelection selection)
        {
            line = SafeInt(() => selection.CurrentLine);
            column = SafeInt(() => selection.CurrentColumn);
        }

        return JsonSerializer.Serialize(new
        {
            name = doc.Name,
            fullName = doc.FullName,
            isSaved = doc.Saved,
            language = doc.Language,
            currentLine = line,
            currentColumn = column,
            windowCaption = Safe(() => dte.MainWindow?.Caption)
        }, Json);
    });

    [McpServerTool(ReadOnly = true), Description("Read the text currently in the Visual Studio editor buffer for the active document. " +
                                "This reflects unsaved edits, which reading the file from disk would miss.")]
    public static string VsGetDocumentText(
        [Description("Maximum number of characters to return. Default is 20000.")]
        int maxChars = 20000,
        [Description("When true return the end of the document; when false (default) return the beginning.")]
        bool tail = false,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var doc = dte.ActiveDocument;
        if (doc == null) return "No document is currently active in Visual Studio.";

        if (maxChars is < 100 or > 5_000_000) maxChars = 20000;

        // EnvDTE does not expose a TextDocument on Document. A TextSelection's parent
        // is the document's TextDocument, which is the supported way to reach the buffer.
        var textDoc = (doc.Selection as TextSelection)?.Parent;
        if (textDoc == null)
        {
            return $"The active document '{doc.FullName}' has no text buffer to read. " +
                   "It is probably a designer surface or another non-text view.";
        }

        string content;
        try
        {
            var start = textDoc.StartPoint.CreateEditPoint();
            content = start.GetText(textDoc.EndPoint) ?? string.Empty;
        }
        catch (Exception ex)
        {
            throw new VsInteropException($"Could not read the document buffer: {ex.Message}", ex);
        }

        var header = $"# {doc.FullName}{(doc.Saved ? string.Empty : "  (UNSAVED CHANGES IN BUFFER)")}";
        if (content.Length > maxChars)
        {
            content = tail ? content[^maxChars..] : content[..maxChars];
            content = $"(truncated to {maxChars} characters)" + Environment.NewLine + content;
        }

        return content.Length == 0 ? header + Environment.NewLine + "(document is empty)" : header + Environment.NewLine + content;
    });

    [McpServerTool(Destructive = false), Description("Open a file in the Visual Studio editor and optionally navigate to a specific line and column.")]
    public static string VsOpenDocument(
        [Description("Full absolute path to the file to open.")]
        string filePath,
        [Description("Optional 1-indexed line number to navigate to. 0 (default) opens the file without moving the caret.")]
        int line = 0,
        [Description("Optional 1-indexed column number to navigate to.")]
        int column = 1,
        [Description("Bring the document window to the foreground. Default is true.")]
        bool activate = true,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new McpException("filePath is required.");
        }

        if (!File.Exists(filePath))
        {
            throw new McpException($"No file exists at '{filePath}'.");
        }

        var dte = VsInstanceLocator.GetDte(targetInstance);
        var window = dte.ItemOperations.OpenFile(filePath);

        if (window == null)
        {
            return $"Visual Studio refused to open '{filePath}'.";
        }

        if (activate)
        {
            try { window.Visible = true; window.Activate(); } catch { /* non-fatal */ }
        }

        var navigated = false;
        if (line > 0 && window.Document?.Selection is TextSelection selection)
        {
            try
            {
                selection.MoveToLineAndOffset(line, Math.Max(1, column));
                navigated = true;
            }
            catch { /* out of range for this file */ }
        }

        return JsonSerializer.Serialize(new
        {
            opened = true,
            filePath,
            line,
            column,
            navigated,
            caption = Safe(() => window.Caption),
            isSaved = Safe(() => window.Document?.Saved)
        }, Json);
    });

    [McpServerTool, Description("Save all modified documents in Visual Studio (equivalent to File.SaveAll).")]
    public static string VsSaveAll(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        dte.ExecuteCommand("File.SaveAll");
        return "Saved all open documents in Visual Studio.";
    });

    // ------------------------------------------------------- debugger & commands

    [McpServerTool(ReadOnly = true), Description("Get the current debugger state (Design, Run, Break mode) and the debugged process, if any.")]
    public static string VsGetDebuggerState(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        var debugger = dte.Debugger;
        if (debugger == null) return "The Visual Studio debugger is not available.";

        var mode = debugger.CurrentMode switch
        {
            dbgDebugMode.dbgDesignMode => "DesignMode",
            dbgDebugMode.dbgRunMode => "RunMode",
            dbgDebugMode.dbgBreakMode => "BreakMode",
            _ => "Unknown"
        };

        return JsonSerializer.Serialize(new
        {
            debuggerMode = mode,
            isDebugging = mode != "DesignMode",
            currentProcess = Safe(() => debugger.CurrentProcess?.Name),
            breakpointCount = Safe(() => debugger.Breakpoints?.Count)
        }, Json);
    });

    [McpServerTool, Description("Start debugging the startup project in Visual Studio (equivalent to pressing F5).")]
    public static string VsStartDebugging(
        [Description("Wait (up to 2 minutes) until the debugger reaches run or break mode before returning. Default is false.")]
        bool waitForStart = false,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);

        // Never Go(true): that waits for the debuggee to *break or exit*, which holds the COM
        // thread for the whole debug session. Start asynchronously and poll for a bounded time.
        dte.Debugger.Go(false);

        bool left = false;
        if (waitForStart)
        {
            var deadline = Environment.TickCount64 + 120_000;
            while (!(left = Safe(() => dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)) &&
                   Environment.TickCount64 < deadline)
            {
                System.Threading.Thread.Sleep(250);
            }
        }

        var mode = Safe(() => dte.Debugger?.CurrentMode.ToString()) ?? "Unknown";
        return JsonSerializer.Serialize(new
        {
            started = true,
            waited = waitForStart,
            debuggerMode = mode,
            note = !waitForStart
                ? "Debugging started in the background; poll vs_get_debugger_state."
                : left ? null : "Still in design mode after 2 minutes (probably still building); poll vs_get_debugger_state."
        }, Json);
    });

    [McpServerTool, Description("Stop the current debugging session in Visual Studio (equivalent to pressing Shift+F5).")]
    public static string VsStopDebugging(
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        var dte = VsInstanceLocator.GetDte(targetInstance);
        dte.Debugger.Stop();
        return "Stopped the active debugging session.";
    });

    [McpServerTool(Destructive = true), Description("Execute a named Visual Studio IDE command, e.g. 'TestExplorer.RunAllTests', 'Edit.FormatDocument', 'File.SaveAll', 'Build.RebuildSolution'. " +
                                "Use vs_list_commands first to discover the exact command name. " +
                                "This can run any IDE command, including ones that launch external programs (e.g. Tools.Shell); " +
                                "only run commands the user asked for.")]
    public static string VsExecuteCommand(
        [Description("The Visual Studio command name, e.g. 'TestExplorer.RunAllTests'.")]
        string commandName,
        [Description("Optional command arguments string.")]
        string commandArgs = "",
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            throw new McpException("commandName is required.");
        }

        var dte = VsInstanceLocator.GetDte(targetInstance);
        dte.ExecuteCommand(commandName, commandArgs ?? string.Empty);
        return $"Command '{commandName}' executed successfully.";
    });

    [McpServerTool(ReadOnly = true), Description("Search the Visual Studio IDE command table by name. Use this to discover the exact command id " +
                                "before calling vs_execute_command. Always filter - the full table has thousands of entries.")]
    public static string VsListCommands(
        [Description("Case-insensitive substring to match against command names, e.g. 'Test', 'Format'. Strongly recommended.")]
        string filter,
        [Description("Maximum number of commands to return. Default is 50.")]
        int maxResults = 50,
        [Description(TargetInstanceDescription)]
        string? targetInstance = null) => Execute(() =>
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new ArgumentException("filter is required - the Visual Studio command table is far too large to list wholesale.",
                                        nameof(filter));
        }

        if (maxResults is < 1 or > 1000) maxResults = 50;

        var dte = VsInstanceLocator.GetDte(targetInstance);
        var commands = dte.Commands;
        if (commands == null) return "The Visual Studio command table is not available.";

        var matches = new List<object>();
        int count = commands.Count;
        // Collect one more than requested so 'truncated' is only true when matches were really dropped.
        for (int i = 1; i <= count && matches.Count <= maxResults; i++)
        {
            try
            {
                var command = commands.Item(i);
                if (command == null) continue;
                var name = Safe(() => command.Name);
                if (name == null || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                matches.Add(new
                {
                    name,
                    id = SafeInt(() => command.ID),
                    guid = Safe(() => command.Guid),
                    isAvailable = Safe(() => command.IsAvailable),
                    localizedName = Safe(() => command.LocalizedName)
                });
            }
            catch { /* skip a command that cannot be read */ }
        }

        var truncated = matches.Count > maxResults;
        if (truncated) matches.RemoveAt(matches.Count - 1);

        return JsonSerializer.Serialize(new
        {
            filter,
            scannedCommands = count,
            returnedCount = matches.Count,
            truncated,
            commands = matches
        }, Json);
    });

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs a tool body on the Visual Studio COM thread and translates the failures an
    /// agent can actually act on into <see cref="McpException"/>.
    ///
    /// This matters: the SDK replaces the message of an arbitrary exception with a
    /// generic "An error occurred invoking 'x'", which throws away everything useful
    /// (which instances exist, that Visual Studio is busy, and so on).
    /// </summary>
    private static T Execute<T>(Func<T> work, TimeSpan? timeout = null)
    {
        try
        {
            return VsComHost.Run(work, timeout);
        }
        catch (VsInteropException ex)
        {
            // VsComHost wraps everything. If the real cause was one of our own
            // McpExceptions, rethrow it unchanged so its message is not lost behind a
            // COM description of some deeper inner exception.
            if (ex.InnerException is McpException mcp) throw mcp;
            throw new McpException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (TimeoutException ex)
        {
            throw new McpException(VsComError.Describe(ex), ex);
        }
    }

    private static string BuildResult(DTE2 dte, string label)
    {
        var build = dte.Solution!.SolutionBuild;
        int? failed = SafeInt(() => build.LastBuildInfo);
        var state = Safe(() => build.BuildState.ToString());

        // LastBuildInfo can be unreadable on some project systems. That is "unknown",
        // NOT "failed" - reporting failure here would be a false negative.
        bool? succeeded = failed is null ? null : failed == 0;

        return JsonSerializer.Serialize(new
        {
            operation = label,
            succeeded,
            failedProjects = failed,
            buildState = state,
            note = succeeded switch
            {
                true => null,
                false => "Some projects failed. Call vs_get_error_list for the compiler diagnostics.",
                null => "The build outcome could not be read back from Visual Studio. Call vs_get_error_list to check for diagnostics."
            }
        }, Json);
    }

    private static string? NormalizeFilter(string filter, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filter)) return "all";

        var value = filter.Trim().ToLowerInvariant();
        switch (value)
        {
            case "all":
            case "error":
            case "errors":
            case "warning":
            case "warnings":
            case "message":
            case "messages":
            case "info":
                return value.TrimEnd('s');
            default:
                error = $"Unknown filter '{filter}'. Use 'error', 'warning', 'message', 'info' or 'all'.";
                return null;
        }
    }

    /// <summary>
    /// Reads a DTE property without letting a transient COM failure (an unloading
    /// project, a closing window) take down the whole tool call.
    ///
    /// Note that this only yields null for reference types: for a value type <c>T?</c> is just
    /// <c>T</c>, so a failed read still produces <c>default(T)</c>. Use <see cref="SafeInt"/> for
    /// integers, where a silent zero is indistinguishable from a real one.
    /// </summary>
    private static T? Safe<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    /// <summary>
    /// Reads a DTE integer property, returning <c>null</c> when it cannot be read.
    ///
    /// This matters most for <c>SolutionBuild.LastBuildInfo</c>: reporting 0 for an unreadable
    /// value claims the build succeeded when the outcome is simply unknown.
    /// </summary>
    private static int? SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return null; }
    }
}
