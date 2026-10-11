using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;

namespace VsMcpBridge;

public sealed record VisualStudioInstanceInfo(
    int ProcessId,
    string Version,
    string? FullVersion,
    string? SolutionPath,
    string? SolutionName,
    bool IsSolutionOpen,
    string? MainWindowTitle,
    string? BuildState
);

public static class VsInstanceLocator
{
    /// <summary>
    /// Visual Studio registers itself in the Running Object Table under a moniker of the
    /// form <c>!VisualStudio.DTE.&lt;major&gt;.&lt;minor&gt;:&lt;pid&gt;</c>.
    ///
    /// The major version tracks the Visual Studio product wave, so:
    /// VS 2019 -> 16.x, VS 2022 -> 17.x, VS 2026 -> 18.x. The regex is version agnostic
    /// on purpose so future waves keep working without a code change.
    /// </summary>
    private static readonly Regex MonikerPattern = new(
        @"^!VisualStudio\.DTE(?:\.([0-9\.]+))?:(\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>CLSID of the Visual Studio DTE automation object (VisualStudio.DTE).</summary>
    private static readonly Guid DteClsid = new("05963E82-BA01-11CF-AB67-00400517E5E2");

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindCtx);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    /// <summary>
    /// Enumerates every running Visual Studio instance that exposes DTE automation, with details.
    /// Must be called from the Visual Studio COM thread.
    /// </summary>
    public static List<(VisualStudioInstanceInfo Info, DTE2 Dte)> GetRunningInstances()
    {
        var results = new List<(VisualStudioInstanceInfo, DTE2)>();
        foreach (var (pid, version, dte) in EnumerateDtes())
        {
            try
            {
                results.Add((Describe(dte, pid, version), dte));
            }
            catch
            {
                // Instance became unreachable mid-inspection.
            }
        }
        return results;
    }

    /// <summary>
    /// The DTE objects in the Running Object Table, without asking any of them anything.
    ///
    /// Describing an instance costs several cross-process calls, and a busy instance (modal
    /// dialog, build) makes each one wait out the message filter's retry budget. Resolving a
    /// target must not pay that for instances it is not going to use.
    /// </summary>
    private static List<(int Pid, string Version, DTE2 Dte)> EnumerateDtes()
    {
        var results = new List<(int, string, DTE2)>();

        IRunningObjectTable rot;
        if (GetRunningObjectTable(0, out rot) != 0 || rot == null)
        {
            return results;
        }

        IBindCtx bindCtx;
        if (CreateBindCtx(0, out bindCtx) != 0 || bindCtx == null)
        {
            return results;
        }

        IEnumMoniker enumMoniker;
        try
        {
            rot.EnumRunning(out enumMoniker);
        }
        catch
        {
            return results;
        }

        if (enumMoniker == null) return results;

        try
        {
            while (true)
            {
                var monikers = new IMoniker[1];
                IntPtr fetched = IntPtr.Zero;

                int hr;
                try
                {
                    hr = enumMoniker.Next(1, monikers, fetched);
                }
                catch
                {
                    break;
                }

                if (hr != 0 || monikers[0] == null) break;

                TryAddInstance(rot, bindCtx, monikers[0], results);
            }
        }
        finally
        {
            try { Marshal.ReleaseComObject(enumMoniker); } catch { /* ignore */ }
        }

        return results;
    }

    private static void TryAddInstance(
        IRunningObjectTable rot,
        IBindCtx bindCtx,
        IMoniker moniker,
        List<(int, string, DTE2)> results)
    {
        string displayName;
        try
        {
            moniker.GetDisplayName(bindCtx, null, out displayName);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(displayName)) return;

        // Only Visual Studio monikers are interesting.
        if (displayName.IndexOf("!VisualStudio.DTE", StringComparison.OrdinalIgnoreCase) < 0) return;

        var match = MonikerPattern.Match(displayName);
        int pid = match.Success && int.TryParse(match.Groups[2].Value, out var parsed) ? parsed : 0;
        string version = match.Success && match.Groups[1].Success ? match.Groups[1].Value : "Unknown";

        object comObject;
        try
        {
            rot.GetObject(moniker, out comObject);
        }
        catch
        {
            return; // The instance is starting up or shutting down.
        }

        if (comObject is DTE2 dte) results.Add((pid, version, dte));
    }

    private static VisualStudioInstanceInfo Describe(DTE2 dte, int pid, string monikerVersion)
    {
        bool isOpen = false;
        string? solutionPath = null;
        try
        {
            isOpen = dte.Solution?.IsOpen ?? false;
            if (isOpen) solutionPath = dte.Solution?.FullName;
        }
        catch
        {
            // Treat as no solution.
        }

        string? solutionName = null;
        if (!string.IsNullOrEmpty(solutionPath))
        {
            try { solutionName = Path.GetFileName(solutionPath); } catch { /* ignore */ }
        }

        string? fullVersion = null;
        try { fullVersion = dte.Version; } catch { /* ignore */ }

        string? title = null;
        try { title = dte.MainWindow?.Caption; } catch { /* ignore */ }

        string? buildState = null;
        try
        {
            if (dte.Solution?.IsOpen == true)
            {
                buildState = dte.Solution.SolutionBuild?.BuildState switch
                {
                    vsBuildState.vsBuildStateInProgress => "InProgress",
                    vsBuildState.vsBuildStateNotStarted => "NotStarted",
                    vsBuildState.vsBuildStateDone => "Done",
                    _ => "Unknown"
                };
            }
        }
        catch
        {
            // ignore
        }

        return new VisualStudioInstanceInfo(
            ProcessId: pid,
            Version: monikerVersion,
            FullVersion: fullVersion,
            SolutionPath: solutionPath,
            SolutionName: solutionName,
            IsSolutionOpen: isOpen,
            MainWindowTitle: title,
            BuildState: buildState);
    }

    /// <summary>
    /// Resolves the Visual Studio instance to automate.
    /// </summary>
    /// <param name="targetInstance">
    /// A process ID or a substring of the solution name/path. When supplied it must match
    /// exactly one instance - the call fails loudly rather than silently operating on a
    /// different solution, which would be very hard for an agent to notice.
    /// </param>
    public static DTE2 GetDte(string? targetInstance = null)
    {
        var instances = EnumerateDtes();
        bool targeted = !string.IsNullOrWhiteSpace(targetInstance);

        if (instances.Count == 0)
        {
            // Fallback: bind directly to the single DTE instance, which works even when
            // the instance has not (yet) published a parseable moniker. It cannot be matched
            // against a target, so it is only used when no target was given.
            var direct = targeted ? null : TryGetActiveDte();
            if (direct != null) return direct;

            throw new InvalidOperationException(
                "No running Visual Studio instance with DTE automation was found. " +
                "Open a solution in Visual Studio and try again." + IntegrityHint());
        }

        if (targeted)
        {
            var token = targetInstance!.Trim();

            if (int.TryParse(token, out var targetPid))
            {
                // Matched on the moniker alone, so other (possibly busy) instances are never touched.
                var byPid = instances.FirstOrDefault(i => i.Pid == targetPid);
                if (byPid.Dte != null) return byPid.Dte;

                throw new InvalidOperationException(
                    $"No running Visual Studio instance with process ID {targetPid} was found. " +
                    DescribeAvailable());
            }

            foreach (var i in instances)
            {
                var path = SolutionPathOf(i.Dte);
                if (path?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return i.Dte;
            }

            throw new InvalidOperationException(
                $"No running Visual Studio instance matches '{targetInstance}'. " +
                DescribeAvailable());
        }

        // No explicit target: prefer an instance that has a solution open.
        foreach (var i in instances)
        {
            if (SolutionPathOf(i.Dte) != null) return i.Dte;
        }

        return instances[0].Dte;
    }

    private static string? SolutionPathOf(DTE2 dte)
    {
        try
        {
            var solution = dte.Solution;
            return solution?.IsOpen == true ? solution.FullName : null;
        }
        catch
        {
            return null;
        }
    }

    private static DTE2? TryGetActiveDte()
    {
        try
        {
            var clsid = DteClsid;
            GetActiveObject(ref clsid, IntPtr.Zero, out var comObject);
            return comObject as DTE2;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeAvailable()
    {
        var lines = GetRunningInstances().Select(i =>
            $"  - pid {i.Info.ProcessId}, DTE {i.Info.Version}" +
            (string.IsNullOrEmpty(i.Info.SolutionName) ? " (no solution)" : $", solution '{i.Info.SolutionName}'"));
        return "Available instances:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Extra guidance for an empty instance list, but only when there is a known reason the
    /// process genuinely cannot see an instance.
    ///
    /// Windows hides COM objects created by a higher-integrity process, so a server running below
    /// Medium integrity enumerates an empty Running Object Table while Visual Studio is running
    /// perfectly happily. Without this note the caller is told to "open a solution in Visual
    /// Studio", which is both wrong and unactionable.
    /// </summary>
    internal static string IntegrityHint()
    {
        if (!ProcessIntegrity.IsBelowMedium()) return string.Empty;

        return Environment.NewLine + Environment.NewLine +
               $"This server is running at {ProcessIntegrity.Describe()} integrity. Windows hides COM objects " +
               "registered by a higher-integrity process, and Visual Studio registers its DTE automation object " +
               "at Medium integrity - so the instance list is empty even though Visual Studio is running." +
               Environment.NewLine +
               "Fix: this executable's mandatory integrity label is below Medium (normally inherited from the " +
               "folder it was published into). Raise it and restart the server, for example:" + Environment.NewLine +
               "  icacls \"<folder containing VsMcpBridge.exe>\" /setintegritylevel Medium /T";
    }
}
