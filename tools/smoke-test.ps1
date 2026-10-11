<#
.SYNOPSIS
    End-to-end smoke test for the VsMcpBridge MCP server against a live Visual Studio instance.

.DESCRIPTION
    Launches VsMcpBridge.exe as a real MCP server over stdio, performs the MCP
    initialize/tools handshake, then invokes tools and prints the raw results.
    This exercises the same path an MCP client (Cursor, Claude, Antigravity) uses.

    Compatible with Windows PowerShell 5.1 and PowerShell 7+.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1
    powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1 -TargetInstance 34800
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$TargetInstance,
    [string[]]$Only,
    [switch]$IncludeBuild,
    [string]$BuildProject
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably populated inside a param() default under Windows
# PowerShell 5.1 when invoked with -File, so resolve the default path after binding.
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ExePath) { $ExePath = Join-Path $scriptDir '..\publish\win-x64\VsMcpBridge.exe' }

if (-not (Test-Path $ExePath)) {
    throw "MCP server executable not found: $ExePath`nBuild it first:  dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish\win-x64"
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName               = (Resolve-Path $ExePath).Path
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false
$psi.CreateNoWindow         = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$null = $proc.Start()

$script:nextId = 0

function Send-Mcp {
    param([string]$Method, $Params = @{}, [switch]$Notification)
    if ($Notification) {
        $msg = @{ jsonrpc = '2.0'; method = $Method; params = $Params }
    }
    else {
        $script:nextId++
        $msg = @{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    }
    $line = $msg | ConvertTo-Json -Depth 20 -Compress
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()
    return $line
}

function Receive-Mcp {
    param([int]$TimeoutMs = 180000)
    # The stdio transport is newline-delimited JSON.
    $task = $proc.StandardOutput.ReadLineAsync()
    if (-not $task.Wait($TimeoutMs)) { throw "Timed out after $TimeoutMs ms waiting for a server response." }
    if ($null -eq $task.Result) { throw 'Server closed stdout unexpectedly.' }
    return ($task.Result | ConvertFrom-Json)
}

$script:results = New-Object 'System.Collections.Generic.List[object]'

function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments = @{}, [string]$Expect = 'ok', [string]$ExpectText, [int]$TimeoutMs = 180000)
    # PowerShell binds an explicitly passed $null over the default, and the plan table
    # passes $null for steps with no expectation. Without this the assertion silently
    # never runs for those steps.
    if (-not $Expect) { $Expect = 'ok' }
    Write-Host ''
    Write-Host ("=== {0} {1}" -f $Name, ($Arguments | ConvertTo-Json -Compress)) -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $null = Send-Mcp -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    $resp = Receive-Mcp -TimeoutMs $TimeoutMs
    $sw.Stop()
    if ($resp.error) {
        $status = 'PROTOCOL-ERROR'
        $text   = ($resp.error | ConvertTo-Json -Compress)
    }
    else {
        $text   = (($resp.result.content | ForEach-Object { $_.text }) -join "`n")
        $isErr  = ($resp.result.isError -eq $true)
        $status = if ($isErr) { 'TOOL-ERROR' } else { 'OK' }
        # A tool that swallows the exception and returns a friendly English sentence
        # is still a failure - flag the common shapes so they are not missed.
        if ((-not $isErr) -and ($text -match '^(Error|No running|No solution|No document|Visual Studio is running|Pane .* not found|\(Pane)')) {
            $status = 'SOFT-FAIL'
        }
        if ((-not $isErr) -and ($text -match 'COMException|RPC_E_|0x8001|RPC_E_CALL_REJECTED')) {
            $status = 'COM-FAIL'
        }
    }

    # Apply the caller's expectation and decide whether this call counts as a pass.
    $pass = $true
    if ($Expect -eq 'error') {
        # A bad target or bad argument MUST fail, and the reason MUST reach the client.
        if ($status -eq 'OK') { $status = 'UNEXPECTED-SUCCESS'; $pass = $false }
        else {
            $status = 'EXPECTED-ERROR'
            if ($ExpectText -and ($text -notlike "*$ExpectText*")) {
                $status = 'ERROR-MISSING-DETAIL'
                $pass = $false
            }
        }
    }
    else {
        $pass = ($status -eq 'OK')
    }

    $color = if ($pass) { 'Green' } else { 'Red' }
    $preview = $text
    if ($preview.Length -gt 700) { $preview = $preview.Substring(0, 700) + ' ...[truncated for display]' }
    Write-Host ("  [{0}] {1}   ({2:n1}s)" -f $status, $preview, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
    $script:results.Add([pscustomobject]@{ Tool = $Name; Status = $status; Pass = $pass; Output = $text })
}

try {
    Write-Host ("Server: {0}" -f $psi.FileName) -ForegroundColor DarkGray

    # --- MCP handshake -------------------------------------------------------
    $null = Send-Mcp -Method 'initialize' -Params @{
        protocolVersion = '2024-11-05'
        capabilities    = @{}
        clientInfo      = @{ name = 'vsmcpbridge-smoke-test'; version = '1.0' }
    }
    $init = Receive-Mcp
    Write-Host ("initialize -> {0} v{1}, protocol {2}" -f $init.result.serverInfo.name, $init.result.serverInfo.version, $init.result.protocolVersion) -ForegroundColor Green
    $null = Send-Mcp -Method 'notifications/initialized' -Notification

    $null = Send-Mcp -Method 'tools/list' -Params @{}
    $list = Receive-Mcp
    $toolNames = @($list.result.tools | ForEach-Object { $_.name })
    Write-Host ("tools/list -> {0} tools: {1}" -f $toolNames.Count, ($toolNames -join ', ')) -ForegroundColor Green

    # --- exercise tools ------------------------------------------------------
    $ti = @{}
    if ($TargetInstance) { $ti['targetInstance'] = $TargetInstance }

    $plan = @(
        @{ n = 'vs_list_instances';       a = @{} }
        @{ n = 'vs_get_solution_info';   a = $ti }
        @{ n = 'vs_list_projects';       a = ($ti + @{ maxResults = 3 }) }
        @{ n = 'vs_get_build_state';     a = $ti }
        @{ n = 'vs_get_error_list';      a = ($ti + @{ filter = 'all'; maxResults = 5 }) }
        @{ n = 'vs_list_output_panes';   a = $ti }
        @{ n = 'vs_get_output_pane';     a = ($ti + @{ paneName = 'Build' }) }
        @{ n = 'vs_get_active_document'; a = $ti }
        @{ n = 'vs_get_document_text';  a = ($ti + @{ maxChars = 300 }) }
        @{ n = 'vs_get_debugger_state';  a = $ti }
        @{ n = 'vs_list_commands';       a = ($ti + @{ filter = 'TestExplorer'; maxResults = 5 }) }
        # Negative tests: these MUST fail loudly, with a useful reason, not silently succeed.
        @{ n = 'vs_get_solution_info'; a = @{ targetInstance = 'NoSuchSolution-9999' }
           expect = 'error'; expectText = "No running Visual Studio instance matches" }
        @{ n = 'vs_get_error_list'; a = ($ti + @{ filter = 'banana' })
           expect = 'error'; expectText = "Unknown filter" }
        @{ n = 'vs_build_project'; a = ($ti + @{ projectName = 'NoSuchProject-12345' })
           expect = 'error'; expectText = 'was not found in the open solution' }
    )

    foreach ($step in $plan) {
        if ($Only -and ($Only -notcontains $step.n)) { continue }
        Invoke-Tool -Name $step.n -Arguments $step.a -Expect $step.expect -ExpectText $step.expectText
    }

    # ------------------------------------------------- side-effecting build pass
    #
    # Off by default: these actually compile inside the live IDE. Enable with
    # -IncludeBuild and name a project to build.

    if ($IncludeBuild) {
        if (-not $BuildProject) {
            throw '-IncludeBuild requires -BuildProject <name>. Use vs_list_projects to find one.'
        }

        Write-Host ''
        Write-Host '--- build pass (this compiles inside Visual Studio) ---' -ForegroundColor Yellow

        $buildSteps = @(
            @{ n = 'vs_build_project'; a = ($ti + @{ projectName = $BuildProject; waitForCompletion = $true }) }
            @{ n = 'vs_get_error_list'; a = ($ti + @{ filter = 'error'; maxResults = 10 }) }
            @{ n = 'vs_build_solution'; a = ($ti + @{ waitForCompletion = $true }) }
            @{ n = 'vs_get_build_state'; a = $ti }
            @{ n = 'vs_get_error_list'; a = ($ti + @{ filter = 'error'; maxResults = 10 }) }
        )

        foreach ($step in $buildSteps) {
            # A full solution build can run for many minutes.
            Invoke-Tool -Name $step.n -Arguments $step.a -TimeoutMs 1800000
        }
    }

    # ---------------------------------------------------- regression assertions
    #
    # The bug this suite exists to catch: Solution.Projects only returns the TOP LEVEL
    # entries, so a solution organised into folders reported 3 "projects" instead of 53
    # and vs_build_project could not resolve any project by name. Guard that directly.

    Write-Host ''
    Write-Host '--- regression assertions ---' -ForegroundColor Magenta

    $null = Send-Mcp -Method 'tools/call' -Params @{ name = 'vs_get_solution_info'; arguments = $ti }
    $solutionInfo = Receive-Mcp
    $null = Send-Mcp -Method 'tools/call' -Params @{ name = 'vs_list_projects'; arguments = ($ti + @{ maxResults = 500 }) }
    $projectList = Receive-Mcp

    $info = ($solutionInfo.result.content | ForEach-Object { $_.text }) -join '' | ConvertFrom-Json
    $list = ($projectList.result.content | ForEach-Object { $_.text }) -join '' | ConvertFrom-Json

    # Independently count the projects declared in the solution file, so the walker is
    # checked against ground truth rather than against a hard-coded expectation.
    $fileCount = $null
    $solutionFile = $info.solutionPath
    if ($solutionFile -and (Test-Path -LiteralPath $solutionFile)) {
        try {
            if ($solutionFile -like '*.slnx') {
                $fileCount = (Select-String -LiteralPath $solutionFile -Pattern '<Project\s' -AllMatches).Count
            }
            elseif ($solutionFile -like '*.sln') {
                $fileCount = (Select-String -LiteralPath $solutionFile -Pattern '^Project\("' ).Count
            }
        } catch { $fileCount = $null }
    }

    $checks = @(
        @{ Name = 'DTE project count matches the solution file (folder walking works)'
           Pass = ($null -ne $fileCount -and $info.projectCount -eq $fileCount)
           Actual = "dte=$($info.projectCount) file=$fileCount ($([System.IO.Path]::GetFileName($solutionFile)))" }
        @{ Name = 'project counts agree between solution info and project list'
           Pass = ($info.projectCount -eq $list.totalProjects)
           Actual = "info=$($info.projectCount) list=$($list.totalProjects)" }
        @{ Name = 'every project has a resolvable name for vs_build_project'
           Pass = (@($list.projects | Where-Object { [string]::IsNullOrWhiteSpace($_.Name) }).Count -eq 0)
           Actual = "$(@($list.projects).Count) projects listed" }
        @{ Name = 'at least one project is reported'
           Pass = ($info.projectCount -ge 1)
           Actual = "projectCount = $($info.projectCount)" }
    )

    foreach ($c in $checks) {
        $color = if ($c.Pass) { 'Green' } else { 'Red' }
        $mark  = if ($c.Pass) { 'PASS' } else { 'FAIL' }
        Write-Host ("  [{0}] {1}  ({2})" -f $mark, $c.Name, $c.Actual) -ForegroundColor $color
        $script:results.Add([pscustomobject]@{ Tool = 'ASSERTION'; Status = $mark; Pass = $c.Pass; Output = $c.Actual })
    }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $proc.WaitForExit(5000) | Out-Null
    $err = $proc.StandardError.ReadToEnd()
    if ($err -and $err.Trim()) {
        Write-Host ''
        Write-Host '--- server stderr ---' -ForegroundColor DarkGray
        Write-Host $err.Trim() -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '================ SUMMARY ================' -ForegroundColor Cyan
$script:results | Format-Table -Property Tool, Status, Pass -AutoSize
$okCount  = @($script:results | Where-Object { $_.Pass }).Count
$badCount = @($script:results | Where-Object { -not $_.Pass }).Count
Write-Host ("Passed: {0}   Failed: {1}" -f $okCount, $badCount)
if ($badCount -gt 0) { exit 1 }
