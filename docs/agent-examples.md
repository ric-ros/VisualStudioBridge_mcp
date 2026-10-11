# Worked examples — driving Visual Studio from an agent

Concrete prompts and the exact tool sequences they should produce.
Outputs shown are real, captured from a live Visual Studio 2026 Enterprise (18.10, DTE 18.0).

Pair this with [`AGENTS.md`](../AGENTS.md), which states the routing rules.

---

## 1. "Build the solution and tell me what's broken"

**Tools:**
```
vs_build_solution      { "waitForCompletion": true }
vs_get_error_list      { "filter": "error", "maxResults": 50 }
```

Real result:
```json
{ "operation": "Build", "succeeded": true, "failedProjects": 0, "buildState": "vsBuildStateDone" }
```

Then triage with the counts before pulling detail:
```json
{
  "totalCount": 61,
  "counts": { "errors": 0, "warnings": 61, "messages": 0, "other": 0 },
  "filter": "error",
  "matchedCount": 0,
  "returnedCount": 0,
  "truncated": false,
  "errors": []
}
```

**What a good agent says:** "Build succeeded, 0 projects failed. There are 61 warnings
(none are errors) — mostly a known AutoMapper vulnerability advisory repeated per project."

**Note the trap:** `filter: "error"` returning `errors: []` alongside `totalCount: 61` is
correct, not a bug. `counts` tells you the whole story; `errors` holds only the filtered page.

---

## 2. "Why won't Contoso.Orders compile?"

**Tools:**
```
vs_list_projects       { "filter": "Quotes.Core" }
vs_build_project       { "projectName": "Contoso.Orders.Application" }
vs_get_error_list      { "filter": "error" }
```

`vs_list_projects` finds nested projects that a naive listing would miss:
```json
{
  "name": "Contoso.Orders.Application",
  "uniqueName": "Contoso.Orders.Application\\Contoso.Orders.Application.csproj",
  "fullPath": "C:\\src\\Contoso\\Contoso.Orders.Application\\Contoso.Orders.Application.csproj",
  "kindName": "CSharpProject",
  "isSolutionFolder": false,
  "folderPath": "src/Modules/Quotes/Core"
}
```

Then:
```json
{ "project": "Contoso.Orders.Application",
  "acceptedCall": "Debug -> Contoso.Orders.Application\\Contoso.Orders.Application.csproj",
  "completed": true, "lastBuildFailedProjects": 0 }
```

**Note:** `acceptedCall` reports which config/name pairing Visual Studio accepted. If you ever
see this tool fail with `E_FAIL`, that is the value to inspect.

---

## 3. "What am I looking at right now?"

**Tools:**
```
vs_get_active_document {}
vs_get_document_text   { "maxChars": 2000 }
```

```json
{
  "name": "OrderDetails.razor",
  "fullName": "C:\\src\\Contoso\\Pages\\OrderDetails.razor",
  "isSaved": true, "language": "Razor",
  "currentLine": 361, "currentColumn": 4,
  "windowCaption": "Contoso - OrderDetails.razor - Microsoft Visual Studio"
}
```

`vs_get_document_text` returns the **live buffer**, prefixed with the path and flagged if unsaved:

```
# C:\src\Contoso\Pages\OrderDetails.razor
@page "/jobs/{JobNumber}"
@attribute [Authorize(Policy = ShipmentsPermission.JobReadName)]
@inherits ResourceLockedDetailPageBase<JobHubService>
...
```

**Why it matters:** if `isSaved` is `false`, reading the file from disk would show the *wrong*
content. This is the case where a plain file read silently lies to you.

---

## 4. "Run the tests"

**Tools:**
```
vs_list_commands       { "filter": "TestExplorer", "maxResults": 10 }
vs_execute_command     { "commandName": "TestExplorer.RunAllTests" }
vs_get_output_pane     { "paneName": "Tests", "maxChars": 8000 }
```

`vs_list_commands` returns real ids so you are not guessing:
```json
[
  { "name": "TestExplorer.DebugAllTests", "id": 789,
    "guid": "{1E198C22-5980-4E7E-92F3-F73168D1FB63}", "isAvailable": false },
  { "name": "TestExplorer.ClearResultsForAllTests", "id": 802, "isAvailable": false }
]
```

**Note `isAvailable`:** `false` means the command exists but is currently greyed out (usually
no test project loaded). Check it before running, and use `vs_list_output_panes` to find the
right pane name first — it is `Tests`, not `Test`.

---

## 5. Two Visual Studio instances open

**Tool:**
```
vs_list_instances {}
```
```json
[
  { "processId": 34800, "dteVersion": "18.0", "solutionName": "Contoso.slnx",
    "mainWindowTitle": "Contoso - OrderDetails.razor - Microsoft Visual Studio",
    "buildState": "Done" },
  { "processId": 29936, "dteVersion": "18.0", "solutionName": "VsMcpBridge.slnx",
    "mainWindowTitle": "VsMcpBridge - Program.cs - Microsoft Visual Studio",
    "buildState": "Done" }
]
```

Now target explicitly:
```
vs_get_solution_info { "targetInstance": "Contoso" }
vs_get_solution_info { "targetInstance": "34800" }
```

A wrong target fails loudly — it never silently uses the other solution:
```
An error occurred invoking 'vs_get_solution_info': No running Visual Studio instance matches
'NoSuchSolution'. Available instances:
  - pid 34800, DTE 18.0, solution 'Contoso.slnx'
  - pid 29936, DTE 18.0, solution 'VsMcpBridge.slnx'
```

**Correct agent behaviour:** read the available list and pick a real one. Do **not** retry the
same bad value — it can never match.

---

## 6. "Is the build still running?"

**Tool:**
```
vs_get_build_state {}
```
```json
{ "solution": "...\\VsMcpBridge.slnx", "buildState": "InProgress",
  "isBuilding": true, "lastBuildFailedProjects": 0 }
```

For a long build, start it non-blocking and poll:
```
vs_build_solution  { "waitForCompletion": false }
vs_get_build_state {}          # repeat until buildState == "Done"
vs_get_error_list  { "filter": "error" }
```

---

## 7. Handling a busy Visual Studio

If Visual Studio is mid-build or showing a modal dialog, calls can come back as:

```
Visual Studio is busy and rejected the call (RPC_E_SERVERCALL_RETRYLATER, 0x8001010A).
It is likely mid-build, showing a modal dialog, or still loading. Retry in a few seconds.
```

**Correct agent behaviour:** wait a few seconds, retry the *same* call. The server already
retries internally for up to ~60s, so reaching this message means a genuinely long block.

A different message means a different problem — do not retry blindly:
```
The Visual Studio COM host thread is still executing an earlier call that never returned.
Visual Studio is most likely blocked on a modal dialog. Dismiss it in Visual Studio, or
restart this MCP server.
```
That one requires human action. Say so.

---

## 8. "No solution is open" — not an error

```
No solution is currently open in Visual Studio.
```

This arrives as a **normal result**, not an error. It is not transient. The right move is to
report it and offer the CLI fallback:

> Visual Studio is running but has no solution loaded, so I can't use the IDE bridge.
> I can build with `dotnet build` instead, or you can open the solution and I'll use VS.

---

## 9. Project enumeration — why it is trustworthy

**Tool:**
```
vs_list_projects { "maxResults": 200 }
```
```json
{
  "totalProjects": 53,
  "totalSolutionFolders": 22,
  "matchedCount": 53,
  "returnedCount": 53,
  "truncated": false,
  "projects": [
    { "name": "Contoso.Web.Api", "kindName": "CSharpProject",
      "folderPath": "src/Apps", "isSolutionFolder": false },
    { "name": "Contoso.Core", "kindName": "CSharpProject",
      "folderPath": "src/Base/Core", "isSolutionFolder": false }
  ]
}
```

`totalProjects: 53` matches the 53 `<Project>` elements in `Contoso.slnx`. A naive implementation
reports `3` here (just the top-level solution folders) and then **`vs_build_project` cannot
find any project by name**.

Use `includeSolutionFolders: true` when the hierarchy itself matters.

---

## 10. Reading output panes safely

```
vs_list_output_panes {}
vs_get_output_pane   { "paneName": "Build", "maxChars": 4000, "tail": true }
```

Real panes on a .NET web solution: `Build`, `Build Order`, `Debug`, `Hot Reload`,
`Razor Logger Output`, `Source Control - Git`, `Tests`, `GitHub Copilot`, plus one
`<Project> - ASP.NET Core Web Server` pane per running site.

Build pane output:
```
11:04:59:041	Build started at 11:04 AM...
11:04:59:079	========== Build: 0 succeeded, 0 failed, 1 up-to-date, 0 skipped ==========
11:04:59:079	========== Build completed at 11:04 AM and took 00.065 seconds ==========
```

**Always pass `maxChars`.** Panes are unbounded — a full solution build can emit megabytes.
`tail: true` (the default) returns the end, which is what you want after a build. Output is
truncated on a character boundary and marked `...(truncated, showing the last N characters)...`.

A pane can be genuinely empty, which is a normal result:
```
(Output pane 'Build' is empty)
```

---

## Multi-instance / multi-solution cheat sheet

| Situation | Do this |
|---|---|
| One VS open | Call tools with no `targetInstance`. |
| Several VS open | `vs_list_instances`, then pass a PID or solution substring. |
| Target does not match | Re-read `vs_list_instances`; never retry the same value. |
| VS not running | Fall back to CLI and say why. |
| VS running, no solution | Fall back to CLI and say why. |
