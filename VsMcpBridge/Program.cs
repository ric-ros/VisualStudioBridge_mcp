using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VsMcpBridge;

// Windows-only (COM/DTE, STA, Win32 message pumping). Declared here rather than through a
// net10.0-windows TFM because .NET tool packages cannot target a platform-specific TFM.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// NOTE: the COM busy/retry message filter is deliberately *not* registered here.
// MCP tool calls run on thread-pool (MTA) threads, and an IOleMessageFilter only
// applies to the apartment it was registered on. VsComHost owns a dedicated STA
// thread, registers the filter there, and funnels every piece of Visual Studio
// automation through it.

var builder = Host.CreateApplicationBuilder(args);

// MCP communicates over stdio, so stdout belongs exclusively to the JSON-RPC
// stream. Every log line must go to stderr or the protocol is corrupted.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "visual-studio-bridge",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" // the package <Version>
        };

        options.ServerInstructions = """
            Control a running Visual Studio (2019/2022/2026) through COM automation - no VSIX required.

            Typical flow:
              1. vs_list_instances        - find the target Visual Studio (returns process IDs).
              2. vs_get_solution_info     - confirm the solution, configuration and platform.
              3. vs_list_projects         - every project, including ones inside solution folders.
              4. vs_build_solution        - or vs_build_project for one project.
              5. vs_get_error_list        - read the real compiler diagnostics.
              6. vs_get_output_pane       - read the Build / Debug output for detail.

            Targeting: every tool takes an optional targetInstance (process ID, or a
            substring of the solution name). If it does not match, the call fails loudly
            instead of quietly operating on a different solution.

            Notes:
              - Visual Studio must be running with a solution open.
              - Calls made while Visual Studio is mid-build can briefly fail with a busy
                error; those are safe to retry.
              - vs_get_document_text reads the editor buffer, so it includes unsaved edits.
            """;
    })
    .WithStdioServerTransport()
    .WithTools<VisualStudioTools>();

var app = builder.Build();
await app.RunAsync();
