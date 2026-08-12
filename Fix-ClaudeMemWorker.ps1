<#
.SYNOPSIS
    Fix-ClaudeMemWorker - one-shot repair for claude-mem's stuck-worker bug.
.DESCRIPTION
    claude-mem's background worker can die on Windows without releasing its
    listener on port 37777 (CLAUDE_MEM_WORKER_PORT). The next Claude Code
    session then can't start a new worker ("Is port 37777 in use?"), and
    because the UserPromptSubmit hook fails CLOSED instead of open, every
    prompt gets blocked while ~/.claude-mem/state/hook-failures.json's
    consecutiveFailures counter climbs without bound - this is exactly the
    "claude-mem worker unreachable for N consecutive hooks" error.

    Tracked upstream, still open: github.com/thedotmack/claude-mem/issues/2926

    This affects the `claude` CLI and the official Claude Code VS Code
    extension IDENTICALLY - both read the same ~/.claude config, the same
    ~/.claude-mem state, and fire the same hook. There is nothing separate
    to fix for the extension; clearing this shared state fixes both at once.

    MULTI-SESSION SAFE: the worker is ONE process shared by every Claude Code
    session on the machine, including several project windows opened at once
    by LLM-TokenOptimizer.ps1 (v5.0+ explicitly supports that). Before killing
    anything, this script checks (a) whether the worker is actually answering
    on its port and (b) how many other `claude` processes are currently
    running. If the worker looks healthy AND other sessions are active,
    killing it would interrupt their memory capture too - so it asks for
    confirmation instead of just doing it. Pass -Force to skip that prompt
    (e.g. from another automated script) once you're sure.

    Also fixes a real bug in the prior version of this script: the "sweep
    stray worker processes" step filtered Win32_Process by `Name = 'node.exe'`,
    but claude-mem's worker actually runs under `bun.exe` on this machine
    (confirmed live via Get-CimInstance) - so that sweep was silently a
    no-op. It now matches both.

    Safe to re-run any time you see the error. Does not touch anything
    outside ~/.claude-mem and the orphaned worker process itself - no
    plugins, settings, or conversation history are affected.
.PARAMETER Force
    Skip the confirmation prompt even if the worker looks healthy and other
    Claude Code sessions are currently running. Use when you're certain the
    worker needs a hard reset regardless.
.NOTES
    Usage: powershell -ExecutionPolicy Bypass -File .\Fix-ClaudeMemWorker.ps1
    After it finishes, fully quit Claude Code / VS Code (not just close the
    window) and reopen - or just re-run this script's caller, which
    (LLM-TokenOptimizer.ps1) restarts the worker as part of normal launch.
#>

[CmdletBinding()]
param([switch]$Force)

Write-Host "Fix-ClaudeMemWorker - repairing claude-mem's stuck worker state" -ForegroundColor Cyan
Write-Host ""

$port = if ($env:CLAUDE_MEM_WORKER_PORT) { [int]$env:CLAUDE_MEM_WORKER_PORT } else { 37777 }

function Test-WorkerHealthy {
    param([int]$Port)
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
        $ok = $connectTask.Wait(750) -and $client.Connected
        $client.Close()
        return [bool]$ok
    } catch { return $false }
}

# 0. Multi-session safety check: is the worker actually alive right now, and
#    is anything else likely depending on it? A plain TCP connect (not a
#    guess at an HTTP path inside claude-mem's bundled/minified worker) is
#    enough to answer "is something actively serving this port right now".
$workerLooksHealthy = Test-WorkerHealthy -Port $port
$otherClaudeSessions = @(Get-Process -Name "claude" -ErrorAction SilentlyContinue)
$sessionCount = $otherClaudeSessions.Count

if ($workerLooksHealthy -and $sessionCount -gt 0 -and -not $Force) {
    Write-Host "  The worker on port $port is currently responding, and $sessionCount Claude Code" -ForegroundColor Yellow
    Write-Host "  process(es) appear to be running right now." -ForegroundColor Yellow
    Write-Host "  Killing it would interrupt memory capture in those sessions too - it may not" -ForegroundColor Yellow
    Write-Host "  even be the cause of what you're seeing (e.g. a stale error message, or a" -ForegroundColor Yellow
    Write-Host "  problem in a single session rather than the shared worker)." -ForegroundColor Yellow
    Write-Host ""
    $confirm = Read-Host "  Reset the shared worker anyway? [y/N]"
    if ($confirm -notmatch '^[Yy]') {
        Write-Host ""
        Write-Host "  Skipped - the worker looks healthy, so nothing was killed." -ForegroundColor Gray
        Write-Host "  If a specific session is still stuck, check that session's own logs" -ForegroundColor Gray
        Write-Host "  (~/.claude-mem/state/hook-failures.json) rather than resetting the shared worker." -ForegroundColor Gray
        exit 0
    }
    Write-Host ""
}

# 1. Kill whatever's holding the worker port.
try {
    $conns = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($conns) {
        foreach ($conn in $conns) {
            $procId = $conn.OwningProcess
            Write-Host "  Killing process $procId holding port $port..." -ForegroundColor Yellow
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host "  Nothing is currently listening on port $port." -ForegroundColor Gray
    }
} catch {
    Write-Host "  Could not check port $port (Get-NetTCPConnection unavailable): $_" -ForegroundColor Yellow
}

# 2. Sweep any other stray claude-mem worker processes - the upstream bug
#    report notes these can leak across sessions even beyond the one
#    holding the port. Matches bun.exe (confirmed live: claude-mem 13.14.x
#    runs worker-service.cjs under bun, not node) as well as node.exe, since
#    the exact runtime can vary by claude-mem version/install.
try {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @('bun.exe', 'node.exe') -and
            $_.CommandLine -match 'claude-mem' -and
            $_.CommandLine -match 'worker-service'
        } |
        ForEach-Object {
            Write-Host "  Killing stray claude-mem worker process $($_.ProcessId) ($($_.Name))..." -ForegroundColor Yellow
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
} catch {}

# 3. Clear the stuck failure counter and stale supervisor record so the
#    next worker starts with a clean slate.
$stateDir = Join-Path $env:USERPROFILE ".claude-mem\state"
$failFile = Join-Path $stateDir "hook-failures.json"
$supervisorFile = Join-Path $env:USERPROFILE ".claude-mem\supervisor.json"
foreach ($f in @($failFile, $supervisorFile)) {
    if (Test-Path $f) {
        Write-Host "  Removing $f" -ForegroundColor Yellow
        Remove-Item $f -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($sessionCount -gt 0) {
    Write-Host "Done. $sessionCount other Claude Code process(es) were running - their worker" -ForegroundColor Green
    Write-Host "connection will reconnect/restart automatically; no need to close them." -ForegroundColor Green
} else {
    Write-Host "Done. Fully quit Claude Code (and VS Code, if you use the extension) and reopen." -ForegroundColor Green
}
Write-Host "If this keeps recurring, the reliable workaround until upstream ships a fix" -ForegroundColor Gray
Write-Host "is to disable the plugin: claude plugin uninstall claude-mem" -ForegroundColor Gray
