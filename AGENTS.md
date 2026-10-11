# AGENTS.md — using the Visual Studio MCP bridge

This repository ships **VsMcpBridge**: an MCP server that drives a *running* Visual Studio
through COM automation. If the `visual-studio` MCP server is available in your tool list,
use it. This file tells you **when** and **how**.

---

## 1. When to use these tools

Use the `vs_*` tools whenever the answer depends on **live IDE state**. The signals:

| The user asks about… | Use | Do **not** |
|---|---|---|
| Build / rebuild / clean the solution or a project | `vs_build_solution`, `vs_rebuild_solution`, `vs_build_project` | shell out to `dotnet build` / `msbuild` |
| Compile errors, warnings, or "what's broken" | `vs_get_error_list` | guess from files, or rely on CLI output only |
| Full build log / verbose output | `vs_get_output_pane` (`Build`) | re-run a build just to capture logs |
| "What am I looking at?" / current file, caret, line | `vs_get_active_document` | ask the user |
| The **current contents** of the open file, incl. unsaved edits | `vs_get_document_text` | read the file from disk (misses unsaved edits) |
| Solution or project structure | `vs_get_solution_info`, `vs_list_projects` | parse the `.sln`/`.slnx` by hand |
| Open a file at a specific line | `vs_open_document` | tell the user to navigate manually |
| Run tests | `vs_list_commands` → `vs_execute_command` (`TestExplorer.RunAllTests`) | assume a test runner CLI exists |
| Debugger state (running / paused / breakpoints) | `vs_get_debugger_state` | infer from source |
| Any other IDE action | `vs_list_commands` to find the command, then `vs_execute_command` | invent command names |

**The rule of thumb:** if Visual Studio is open with the solution, it is the source of truth.
It knows about unsaved buffers, the real Error List, and the actual build that just ran.
Prefer it over CLI tools — the CLI cannot see any of that.

**When *not* to use it:**

- Visual Studio is not running, or has no solution open. The tools will error; fall back to
  CLI (`dotnet build`, `dotnet test`) and say so.
- You need a headless/CI answer. Use the CLI.
- Reading a file that has no unsaved changes — a plain file read is cheaper.

---

## 2. Standard sequences

### Build then explain what broke
```
vs_build_solution          (waitForCompletion: true)
vs_get_error_list          (filter: "error", maxResults: 50)
vs_get_output_pane         (paneName: "Build", maxChars: 8000)
```
Do not stop after the build call — always read the diagnostics.

### Build one project
```
vs_list_projects           → find the exact name
vs_build_project           (projectName: "<exact name>")
vs_get_error_list          (filter: "error")
```
`vs_list_projects` returns projects **nested inside solution folders** too, so the name you
get from it is always safe to pass to `vs_build_project`.

### Orient in an unfamiliar solution
```
vs_list_instances          → which Visual Studio, which solution, which PID
vs_get_solution_info       → solution path, active configuration | platform
vs_list_projects           → every project with its folder path
```

### Work against the file the user is looking at
```
vs_get_active_document     → path + caret line/column
vs_get_document_text       → the live buffer, including unsaved edits
```

### Multi-instance targeting
Every tool accepts `targetInstance` — a **process ID** or a **substring of the solution name**.
Get it from `vs_list_instances`. Example: `targetInstance: "Contoso"` or `targetInstance: "34800"`.

If a target does not match, the call **fails loudly** with the list of available instances.
It never silently picks a different solution — so if you get that error, re-read
`vs_list_instances` rather than retrying the same value.

---

## 3. Behaviour you must account for

- **Errors are real MCP errors** (`isError: true`) and carry an actionable message —
  read it. They are not silent.
- **Busy errors are retryable.** If the message mentions `RPC_E_SERVERCALL_RETRYLATER`,
  Visual Studio was mid-build or showing a dialog. Wait a few seconds and retry the same call.
- **Empty results are not errors.** "No solution is currently open", "The Error List is
  empty", "(Output pane 'Build' is empty)" come back as normal text. Do not retry them —
  either the state is genuinely empty, or you need a build first.
- **Errors vs warnings.** Check `counts` in `vs_get_error_list` output before pulling detail.
  A clean build with 61 warnings is not a failed build.
- **`succeeded` may be `null`.** That means the build outcome could not be read back — it is
  *unknown*, not failed. Check `vs_get_error_list`.
- **Output is capped.** `vs_get_output_pane` and `vs_get_document_text` truncate
  (default 20 000 chars). Use `maxChars` / `tail` rather than assuming you saw everything.
- **Builds can be non-blocking.** `vs_build_solution` and `vs_build_project` accept
  `waitForCompletion: false`; then poll `vs_get_build_state` and read `vs_get_error_list`.
- **`vs_get_error_list` only sees what the Error List shows.** If the result has `hiddenLevels`,
  the user has toggled those levels off in the UI and they are not counted.
- **`vs_get_error_list` state is the IDE's.** After a build it reflects that build. If it
  looks stale, read the Build output pane.
- **`vs_execute_command` can do anything.** Prefer a specific tool when one exists. Never
  run a destructive IDE command without the user asking for it.

---

## 4. Anti-patterns

| Don't | Why |
|---|---|
| `dotnet build` while VS has the solution open | Bypasses the IDE; you will not see unsaved buffers or the real Error List, and you can fight over `obj/` locks. |
| Reading a file from disk to report "current" contents | Misses unsaved editor changes. Use `vs_get_document_text`. |
| Retrying a "no solution open" result | It is not transient. Report it. |
| Retrying a non-matching `targetInstance` | It will never match. Re-read `vs_list_instances`. |
| Calling `vs_start_debugging` / `vs_save_all` / `vs_clean_solution` unprompted | These change the user's environment. Only when asked. |
| Parsing `.sln`/`.slnx` to list projects | You will miss solution-folder nesting and conditional loads. Use `vs_list_projects`. |

---

## 5. Minimal tool reference

| Tool | Purpose |
|---|---|
| `vs_list_instances` | Running Visual Studio instances: PID, DTE version, solution, build state. |
| `vs_get_solution_info` | Solution path, active + available configurations, project/folder counts. |
| `vs_list_projects` | Every project incl. nested ones: name, unique name, path, kind, folder path. |
| `vs_build_solution` / `vs_rebuild_solution` / `vs_clean_solution` | Solution-level build/rebuild/clean. |
| `vs_build_project` | Build one project by name. |
| `vs_get_build_state` | In progress / not started / done, plus last failed-project count. |
| `vs_get_error_list` | Error List with per-level counts, file, line, column. |
| `vs_list_output_panes` / `vs_get_output_pane` | Names of, and text from, Output panes. |
| `vs_get_active_document` | Active file, language, saved state, caret position. |
| `vs_get_document_text` | Live editor buffer, **including unsaved edits**. |
| `vs_open_document` | Open a file and navigate to a line/column. |
| `vs_save_all` | Save all documents. |
| `vs_get_debugger_state` | Design / Run / Break mode, process, breakpoints. |
| `vs_start_debugging` / `vs_stop_debugging` | F5 / Shift+F5. |
| `vs_list_commands` / `vs_execute_command` | Discover and run IDE commands. |

---

## 6. Installing this guidance

`AGENTS.md` is read automatically by Cursor, GitHub Copilot, Codex, and Claude Code.

For other hosts, copy the block below into the host's system prompt or rules file
(`.github/copilot-instructions.md`, `.cursor/rules/visual-studio.mdc`, etc.):

```text
When Visual Studio is running with the solution open, use the `visual-studio` MCP tools
for any question about builds, compile errors, the active file, or IDE state — do not
shell out to dotnet build/msbuild, and do not read files from disk when the user may have
unsaved edits (use vs_get_document_text). Standard sequence for a build question:
vs_build_solution, then vs_get_error_list. For a project build: vs_list_projects to get the
exact name, then vs_build_project. Tools raise real errors with actionable messages; retry
only on busy/RPC_E_SERVERCALL_RETRYLATER errors. "No solution is open" and empty results
are normal states, not failures.
```
