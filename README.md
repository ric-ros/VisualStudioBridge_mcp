# VsMcpBridge

<!-- mcp-name: io.github.ric-ros/vsmcpbridge -->

[![NuGet](https://img.shields.io/nuget/v/VsMcpBridge?logo=nuget&label=NuGet)](https://www.nuget.org/packages/VsMcpBridge)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

**Let AI coding agents drive the Visual Studio you already have open.**

VsMcpBridge is a [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server that connects agents such as Claude Code, GitHub Copilot, Codex, Cursor or VS Code to a **running Visual Studio** on Windows. The agent can build, read the real Error List, inspect the solution, read unsaved editor buffers, open files and control the debugger. It sees what you see in the IDE.

- **No VSIX to install.** It talks to Visual Studio through the standard DTE COM automation interface, found through the Windows Running Object Table.
- **Works with Visual Studio 2019, 2022 and 2026.** Verified end to end on Visual Studio Enterprise 2026 (18.x).
- **Honest results.** It reports the real failed-project count and the real Error List, and doesn't claim "build succeeded" when nothing was built.

```text
You:    Build the solution and fix whatever is broken.
Agent:  vs_build_solution → 1 project failed
        vs_get_error_list → CS0246 in OrderService.cs(42,17): 'IClock' could not be found
        … edits the file …
        vs_build_project  → 0 failed ✔
```

---

## Quick start

You need the **.NET 10 SDK**, which provides `dnx`, and **Visual Studio running with a solution open**.

Add this to your MCP client's configuration:

```json
{
  "mcpServers": {
    "visual-studio": {
      "command": "dnx",
      "args": ["VsMcpBridge", "--yes"]
    }
  }
}
```

`dnx` downloads the package from NuGet on first use and runs it. There's nothing to install or keep up to date. To pin a version, use `"VsMcpBridge@1.0.0"`.

Then ask your agent: *"Which Visual Studio instances are running?"*

---

## Client setup

<details>
<summary><b>Claude Code</b></summary>

```powershell
claude mcp add visual-studio -- dnx.cmd VsMcpBridge --yes
```

Or add the JSON above to `.mcp.json` in your repository. Use `"command": "dnx.cmd"` on Windows.
</details>

<details>
<summary><b>GitHub Copilot CLI</b></summary>

Add to `~/.copilot/mcp-config.json`, or to `.mcp.json` in your repository:

```json
{
  "mcpServers": {
    "visual-studio": { "command": "dnx.cmd", "args": ["VsMcpBridge", "--yes"] }
  }
}
```
</details>

<details>
<summary><b>VS Code (GitHub Copilot)</b></summary>

`.vscode/mcp.json`:

```json
{
  "servers": {
    "visual-studio": { "type": "stdio", "command": "dnx", "args": ["VsMcpBridge", "--yes"] }
  }
}
```
</details>

<details>
<summary><b>Visual Studio (GitHub Copilot)</b></summary>

`.mcp.json` next to your solution, or `%USERPROFILE%\.mcp.json` for every solution:

```json
{
  "servers": {
    "visual-studio": { "type": "stdio", "command": "dnx", "args": ["VsMcpBridge", "--yes"] }
  }
}
```
</details>

<details>
<summary><b>Codex</b></summary>

`~/.codex/config.toml`, or `.codex/config.toml` in a trusted project:

```toml
[mcp_servers.visual-studio]
command = "dnx.cmd"
args = ["VsMcpBridge", "--yes"]
```
</details>

<details>
<summary><b>Cursor, Claude Desktop, others</b></summary>

Use the JSON from [Quick start](#quick-start) in the client's MCP configuration file, for example `%APPDATA%\Claude\claude_desktop_config.json`.
</details>

> **`dnx` or `dnx.cmd`?** On Windows the .NET SDK installs `dnx.cmd`. VS Code and Visual Studio find it as `dnx`. Some clients, including Claude Code, Copilot CLI and Codex, need the full `dnx.cmd`.

---

## Tools

Every tool accepts an optional `targetInstance`: a process ID, or part of the solution name or path. If it matches nothing, the call **fails with the list of running instances** instead of quietly using a different solution.

### Solution and projects

| Tool | What it does |
|---|---|
| `vs_list_instances` | Every running Visual Studio: PID, DTE version, edition, solution, window title, build state. |
| `vs_get_solution_info` | Solution path, active configuration and platform, all configurations, project and solution-folder counts. |
| `vs_list_projects` | **Every** project, including ones nested in solution folders, with full paths. Filterable. |

### Build

| Tool | What it does |
|---|---|
| `vs_build_solution` | Build, then report the real number of failed projects. |
| `vs_build_project` | Build one project by name, unique name or path, with the active configuration and platform. |
| `vs_rebuild_solution` | Clean and rebuild. |
| `vs_clean_solution` | Clean build output. |
| `vs_get_build_state` | In progress, not started or done, plus the last failed-project count. |

### Diagnostics and output

| Tool | What it does |
|---|---|
| `vs_get_error_list` | Error List items with file, line, column and per-level counts. Filter by `error`, `warning` or `message`. |
| `vs_list_output_panes` | Names of all Output panes (`Build`, `Debug`, `Tests`, …). |
| `vs_get_output_pane` | A pane's text, capped and tail-truncated so a huge Build log can't flood the agent's context. |

### Editor

| Tool | What it does |
|---|---|
| `vs_get_active_document` | Active file, language, saved state, caret position. |
| `vs_get_document_text` | The **editor buffer**, including unsaved edits that reading the file from disk would miss. |
| `vs_open_document` | Open a file at a line and column. |
| `vs_save_all` | Save all modified documents. |

### Debugger and IDE commands

| Tool | What it does |
|---|---|
| `vs_get_debugger_state` | Design, run or break mode, the debugged process, breakpoint count. |
| `vs_start_debugging` / `vs_stop_debugging` | F5 / Shift+F5. |
| `vs_list_commands` | Search the IDE command table, for example `TestExplorer`. |
| `vs_execute_command` | Run any IDE command, for example `TestExplorer.RunAllTests` or `Edit.FormatDocument`. |

### Safety

Tools carry MCP annotations, so clients can tell them apart and ask before acting:

- **Read-only (11 tools):** listing, state, Error List, output, documents, command search.
- **Changes IDE state:** builds, clean, open, save, debugger.
- **Destructive:** `vs_execute_command`. It can run *any* IDE command, including ones that launch external programs. Allow it only for commands you asked for.

---

## Teaching your agent to use it

Two files in this repository are written for the **agent**, not for you:

- **[`AGENTS.md`](AGENTS.md):** when to use these tools and when to fall back to the CLI, standard call sequences, and anti-patterns. Copy it into your own repository, or paste its short block into your agent's rules.
- **[`docs/agent-examples.md`](docs/agent-examples.md):** ten worked examples with real prompts, tool calls and captured output, including a busy Visual Studio, multiple instances and "no solution open".

The key rule for the agent: **when Visual Studio is open with the solution, it is the source of truth.** It knows about unsaved buffers, the real Error List and the build that actually ran, and a CLI build sees none of that.

---

## Requirements

- **Windows 10 or 11**, or Windows Server. The bridge uses COM, so it's Windows-only.
- **.NET 10 SDK** for `dnx`, or the .NET 10 runtime if you build it yourself.
- **Visual Studio 2019, 2022 or 2026** (Community, Professional or Enterprise), running with a solution open.

| Visual Studio | Product version | DTE version |
|---|---|---|
| 2019 | 16.x | `16.0` |
| 2022 | 17.x | `17.0` |
| 2026 | 18.x | `18.0` |

The version parser is version-agnostic, so future Visual Studio releases work without a code change.

---

## Troubleshooting

**"No running Visual Studio instance" while Visual Studio is open**

- **Elevation:** if Visual Studio runs as administrator, the MCP client, and therefore the bridge, must run as administrator too. Otherwise COM returns `E_ACCESSDENIED`.
- **Integrity level:** the bridge must run at **Medium integrity** or higher. Windows hides COM objects registered by higher-integrity processes, so a Low-integrity process sees an *empty* Running Object Table with no error at all. This happens when the bridge runs from a folder with a low mandatory integrity label, typically a sandboxed workspace. `vs_list_instances` detects this and says so. To fix a folder:

  ```powershell
  icacls <folder> | Select-String Mandatory          # no "Mandatory Label" line = Medium, fine
  icacls <folder> /setintegritylevel Medium /T       # raise it
  ```

**Calls fail while Visual Studio is busy.** The bridge already retries automatically while Visual Studio is mid-build (`RPC_E_SERVERCALL_RETRYLATER`) with a bounded backoff. If a modal dialog is open in Visual Studio, close it.

**`dnx` not found.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet). On Windows, try `dnx.cmd` as the command.

---

## How it works

- **One STA thread for all automation.** An `IOleMessageFilter` only applies to the COM apartment it is registered in, and MCP tool calls arrive on thread-pool (MTA) threads. So `VsComHost` owns a dedicated single-threaded-apartment thread. That thread registers the message filter, runs every Visual Studio call, and pumps Win32 messages between calls.
- **Busy handling.** The message filter retries calls Visual Studio rejects with `SERVERCALL_RETRYLATER`, using a bounded backoff. A definite rejection (`SERVERCALL_REJECTED`) fails immediately.
- **Real project enumeration.** DTE's `Solution.Projects` only returns the top level. In a solution organised into solution folders, that is just the folders. The bridge walks `ProjectItems`/`SubProject` recursively, so a 53-project solution reports 53 projects, not 3 folders, and `vs_build_project` can resolve any of them by name. Folders are detected by kind GUID *and* by structure, so a future change to those GUIDs can't silently break it.

### Compared with the alternatives

| Approach | Strengths | Limits |
|---|---|---|
| **VsMcpBridge** | No VSIX. Any Visual Studio version. Live IDE state: Error List, output, debugger, editor buffer. | Visual Studio must be running. |
| VSIX extensions | In-process access to Roslyn internals. | Must be installed and updated with every Visual Studio release. |
| CLI (`dotnet build`) | Headless, fast, great for CI. | No live IDE state: no unsaved buffers, Error List or debugger. |
| Copilot agent mode inside Visual Studio | Native to Visual Studio. | Lets Visual Studio *use* MCP tools; it doesn't let external agents drive Visual Studio. |

---

## Build from source

```powershell
git clone https://github.com/ric-ros/VisualStudioBridge_mcp.git
cd VisualStudioBridge_mcp
dotnet publish VsMcpBridge -c Release -o publish\win-x64
```

Point your client at the executable: `"command": "C:\\path\\to\\publish\\win-x64\\VsMcpBridge.exe"`.

**Smoke test.** `tools/smoke-test.ps1` runs the real MCP protocol against your live Visual Studio, using `publish\win-x64\VsMcpBridge.exe` by default (`-ExePath` to override). It does the handshake, calls the read-only tools, checks that bad input fails with a useful message, and verifies the project enumeration. It exits non-zero on failure:

```powershell
powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1 [-TargetInstance <pid>]
```

Package versions are pinned exactly, with no floating ranges, so restore works offline from the NuGet cache. Bump them deliberately in `VsMcpBridge.csproj`.

**Pack.** `dotnet pack VsMcpBridge -c Release` produces the NuGet tool package, with package types `DotnetTool` and `McpServer`.

**Release.** Bump the version in `VsMcpBridge.csproj` and both versions in `VsMcpBridge/.mcp/server.json`, then publish a GitHub release tagged `v<version>`. `.github/workflows/publish.yml` checks that the tag matches, packs, and pushes to nuget.org through Trusted Publishing, so no API key is stored.

---

## License

[MIT](LICENSE)
