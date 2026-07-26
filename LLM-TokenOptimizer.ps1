#Requires -Version 5.1
<#
.SYNOPSIS
    LLM-TokenOptimizer - Production Quality v3.1
.DESCRIPTION
    Self-bootstrapping launcher that verifies the environment, installs
    dependencies, generates Graphify graphs, and launches Claude Code reliably
    on any Windows 10/11 PC. References itself as LLM-TokenOptimizer throughout.

    v3.1: fixed the Graphify output path for Graphify 0.17.1+, which now
    writes to a hidden .graphify\graph.json (not graphify-out\graph.json) and
    auto-generates the HTML studio during `extract` itself, so the separate
    `export html` step has been removed.

    v3.0: pxpipe has been removed entirely. Claude Code is now routed straight
    through OmniRoute (auto-started via the `omniroute` command if needed),
    which applies its own compression pipeline (RTK -> Caveman -> LLMLingua ->
    Lite) to every request automatically - no separate proxy process to
    manage. You can pick Claude Sonnet or Claude Opus as the active model
    each run (auto/claude-sonnet, auto/claude-opus - real Claude models
    either way, routed through OmniRoute). Files Graphify can't parse are now
    sent to OmniRoute for a fallback summary instead of being silently
    skipped. Reopening a workspace you've already used resumes the previous
    Claude session instead of starting a new one.
.NOTES
    Version: 3.1.0
    Exit Codes:
        0   - Success
        99  - Unexpected error
        100 - Duplicate instance
        101 - Unsupported Windows version
        102 - Missing required dependency
        103 - Claude not found
        104 - Graphify installation failed
        106 - Graph extraction failed
#>

[CmdletBinding()]
param(
    [switch]$VerboseMode,
    [switch]$ForceUpdate,
    [switch]$SkipOmniRoute,
    [switch]$ResetConfig,
    # One-time launch override: forces this session onto Sonnet or Opus via
    # `claude --model <alias>`, regardless of whatever Claude Code last saved
    # as its default (e.g. Fable, from a previous /model pick). Session-scoped
    # only - doesn't touch the saved default, and doesn't persist to the next
    # launch. Leave unset to keep using whatever Claude Code already has saved.
    [ValidateSet('sonnet', 'opus')]
    [string]$Model
)

# ============================================================================
# STRICT MODE AND GLOBAL STATE
# ============================================================================
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Application constants
$script:APP_NAME = "LLM-TokenOptimizer"
$script:APP_VERSION = "3.1.0"
$script:MAX_HISTORY = 20
$script:MAX_LOG_FILES = 10
$script:OMNIROUTE_URL = "http://localhost:20128"

# Paths (computed once, never hardcoded)
$script:AppDataDir = Join-Path $env:LOCALAPPDATA $script:APP_NAME
$script:ConfigPath = Join-Path $script:AppDataDir "config.json"
$script:LogDir = Join-Path $script:AppDataDir "logs"
$script:GlobalGateFile = Join-Path $env:USERPROFILE ".graphify_platform_claude_done"

# Mutable global state (minimized)
$script:Config = $null
$script:Mutex = $null
$script:StartTime = Get-Date
$script:DependencyCache = @{}
$script:CleanupRegistered = $false
# Session-only "-Model sonnet|opus" override; set inside Set-OmniRouteLaunchEnvironment.
$script:ForcedModelAlias = $null

# ============================================================================
# UI TOOLKIT (ASCII only - safe in any console/encoding)
# ============================================================================

function Get-SafeConsoleWidth {
    try { $w = [Console]::WindowWidth; if ($w -gt 0) { return $w } } catch {}
    return 80
}

function Get-Rule {
    return ('-' * [Math]::Min(52, [Math]::Max(20, (Get-SafeConsoleWidth) - 4)))
}

function Write-Status {
    param(
        [string]$Tag,
        [System.ConsoleColor]$Color,
        [string]$Message,
        [System.ConsoleColor]$MessageColor = [System.ConsoleColor]::Gray
    )
    Write-Host ("  " + $Tag.PadRight(6)) -ForegroundColor $Color -NoNewline
    Write-Host $Message -ForegroundColor $MessageColor
}

function Write-Success { param([Parameter(Mandatory)][string]$Message) Write-Status "ok"   ([System.ConsoleColor]::Green)    $Message ([System.ConsoleColor]::Gray) }
function Write-Info    { param([Parameter(Mandatory)][string]$Message) Write-Status "info" ([System.ConsoleColor]::DarkCyan) $Message ([System.ConsoleColor]::Gray) }
function Write-Warning { param([Parameter(Mandatory)][string]$Message) Write-Status "warn" ([System.ConsoleColor]::Yellow)   $Message ([System.ConsoleColor]::Yellow) }
function Write-Fail    { param([Parameter(Mandatory)][string]$Message) Write-Status "fail" ([System.ConsoleColor]::Red)      $Message ([System.ConsoleColor]::Red) }
function Write-Hint    { param([string]$Message = "") Write-Host "  $Message" -ForegroundColor DarkGray }

function Write-ProgressBar {
    # Determinate progress bar (ASCII only). Redraws in place via `r - call
    # Clear-ProgressLine (or just Write-Host "") once the operation finishes.
    param(
        [Parameter(Mandatory)][int]$Percent,
        [string]$Label = "",
        [int]$Width = 28
    )
    $pct = [Math]::Max(0, [Math]::Min(100, $Percent))
    $filled = [Math]::Round($Width * $pct / 100)
    $bar = ('#' * $filled) + ('-' * ($Width - $filled))
    $line = "  [$bar] {0,3}%  $Label" -f $pct
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    if ($line.Length -gt $maxWidth) { $line = $line.Substring(0, $maxWidth) }
    Write-Host ("`r" + $line.PadRight($maxWidth)) -NoNewline -ForegroundColor Cyan
}

function Clear-ProgressLine {
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    Write-Host ("`r" + (' ' * $maxWidth) + "`r") -NoNewline
}

$script:SpinnerFrames = @('|', '/', '-', '\')

function Write-Spinner {
    # One animation frame of an indeterminate spinner. Caller tracks frame
    # index and elapsed time; used by Invoke-ExternalCommand's -ShowSpinner.
    param([Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][int]$FrameIndex, [string]$Elapsed = "")
    $frame = $script:SpinnerFrames[$FrameIndex % $script:SpinnerFrames.Length]
    $suffix = if ($Elapsed) { " ($Elapsed)" } else { "" }
    $line = "  $frame $Label$suffix"
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    if ($line.Length -gt $maxWidth) { $line = $line.Substring(0, $maxWidth) }
    Write-Host ("`r" + $line.PadRight($maxWidth)) -NoNewline -ForegroundColor DarkCyan
}

function Write-Title {
    $width = [Math]::Min(64, [Math]::Max(40, (Get-SafeConsoleWidth) - 4))
    $bar = ('=' * $width)
    Write-Host ""
    Write-Host "  $bar" -ForegroundColor DarkCyan
    Write-Host "   LLM-TokenOptimizer " -ForegroundColor Cyan -NoNewline
    Write-Host "v$($script:APP_VERSION)" -ForegroundColor DarkGray
    Write-Host "   Self-bootstrapping environment for Claude Code + OmniRoute" -ForegroundColor DarkGray
    Write-Host "  $bar" -ForegroundColor DarkCyan
}

function Write-Section {
    param([Parameter(Mandatory)][string]$Name, [int]$Step = 0, [int]$TotalSteps = 0)
    Write-Host ""
    Write-Host "  > " -ForegroundColor DarkCyan -NoNewline
    if ($Step -gt 0 -and $TotalSteps -gt 0) {
        Write-Host "[$Step/$TotalSteps] " -ForegroundColor DarkYellow -NoNewline
    }
    Write-Host $Name -ForegroundColor Cyan
    Write-Host ("  " + (Get-Rule)) -ForegroundColor DarkGray
}

function Get-Elapsed { return ((Get-Date) - $script:StartTime).ToString('mm\:ss') }

function Read-YesNo {
    param([Parameter(Mandatory)][string]$Prompt, [bool]$Default = $false)
    $suffix = if ($Default) { "[Y/n]" } else { "[y/N]" }
    $ans = Read-Host "  $Prompt $suffix"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $Default }
    return ($ans -match '^\s*[Yy]')
}

function Get-Truncated {
    param([string]$Text, [int]$Max = 200)
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    if ($Text.Length -le $Max) { return $Text }
    return $Text.Substring(0, $Max)
}

function Set-Marker {
    param([Parameter(Mandatory)][string]$Path)
    try { "done" | Out-File -FilePath $Path -Encoding ASCII -Force -NoNewline } catch {}
}

# ============================================================================
# LOGGING SYSTEM
# ============================================================================

function Initialize-Logging {
    try {
        if (-not (Test-Path $script:LogDir)) {
            New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
        }
        Get-ChildItem -Path $script:LogDir -Filter "launcher_*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -Skip $script:MAX_LOG_FILES |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    } catch {}
}

function Write-Log {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "DEBUG", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logEntry = "[$timestamp][$Level] $Message"
    $logFile = Join-Path $script:LogDir "launcher_$((Get-Date).ToString('yyyyMMdd')).log"
    try { $logEntry | Out-File -FilePath $logFile -Append -Encoding UTF8 -ErrorAction SilentlyContinue } catch {}
    if ($VerboseMode -or $Level -eq "ERROR") { Write-Verbose $logEntry }
}

# ============================================================================
# CONTROLLED EXIT
# ============================================================================

function Stop-Script {
    [CmdletBinding()]
    param([int]$Code = 0, [string]$Reason = "")
    if ($Reason) { Write-Fail $Reason }
    Write-Host ""
    Write-Hint "The launcher stopped (exit code $Code). Press Enter to close..."
    try { $null = Read-Host } catch { Start-Sleep -Seconds 15 }
    exit $Code
}

# ============================================================================
# CONFIGURATION SYSTEM
# ============================================================================

function Get-DefaultConfiguration {
    return [PSCustomObject]@{
        LastProject = ""
        ProjectHistory = [array]@()
        UseOmniRoute = $null
        OmniRouteApiKeyEnc = ""
        ClaudePath = ""
        AutoUpdateGraphify = $false
        FirstRunComplete = $false
        LastGraphifyVersion = ""
    }
}

function Initialize-Configuration {
    try {
        if (-not (Test-Path $script:AppDataDir)) {
            New-Item -ItemType Directory -Path $script:AppDataDir -Force | Out-Null
        }
    } catch {
        Write-Log "Failed to create app data directory: $_" -Level "ERROR"
    }

    if ($ResetConfig -and (Test-Path $script:ConfigPath)) {
        Remove-Item $script:ConfigPath -Force -ErrorAction SilentlyContinue
        Write-Log "Configuration reset by user request"
    }

    if (Test-Path $script:ConfigPath) {
        try {
            $savedConfig = (Get-Content $script:ConfigPath -Raw -Encoding UTF8) | ConvertFrom-Json
            foreach ($prop in (Get-DefaultConfiguration).PSObject.Properties) {
                if (-not ($savedConfig.PSObject.Properties.Name -contains $prop.Name)) {
                    $savedConfig | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value
                }
            }
            $script:Config = $savedConfig
            Write-Log "Configuration loaded from: $($script:ConfigPath)"
        } catch {
            Write-Log "Failed to parse config, using defaults: $_" -Level "WARN"
            $script:Config = Get-DefaultConfiguration
        }
    } else {
        $script:Config = Get-DefaultConfiguration
        Write-Log "No configuration found, using defaults"
    }
}

function Save-Configuration {
    if (-not $script:Config) { return }
    try {
        $script:Config | ConvertTo-Json -Depth 10 | Out-File -FilePath $script:ConfigPath -Encoding UTF8 -Force
        Write-Log "Configuration saved"
    } catch {
        Write-Log "Failed to save configuration: $_" -Level "ERROR"
    }
}

# ============================================================================
# SINGLE INSTANCE PROTECTION
# ============================================================================

function Initialize-Mutex {
    try {
        $mutexName = "Global\LLMTokenOptimizerMutex_v3_$(whoami | ForEach-Object { $_ -replace '[^a-zA-Z0-9]', '' })"
        $script:Mutex = New-Object System.Threading.Mutex($false, $mutexName)
        if (-not $script:Mutex.WaitOne(0, $false)) {
            Write-Warning "An LLM-TokenOptimizer session is already running."
            Write-Hint "Closing to prevent environment conflicts."
            Stop-Script -Code 100
        }
        Write-Log "Mutex acquired: $mutexName"
    } catch {
        Write-Log "Mutex creation failed: $_" -Level "WARN"
    }
}

function Release-Mutex {
    if ($null -ne $script:Mutex) {
        try {
            $script:Mutex.ReleaseMutex()
            $script:Mutex.Dispose()
            Write-Log "Mutex released"
        } catch {
            Write-Log "Mutex release error: $_" -Level "WARN"
        }
        $script:Mutex = $null
    }
}

# ============================================================================
# CLEANUP SYSTEM
# ============================================================================

function Register-CleanupHandlers {
    if ($script:CleanupRegistered) { return }
    $script:CleanupRegistered = $true
    try {
        $null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Invoke-Cleanup } -ErrorAction SilentlyContinue
    } catch {
        Write-Log "Failed to register PowerShell.Exiting handler" -Level "WARN"
    }
}

function Invoke-Cleanup {
    # OmniRoute is a standalone app the user runs independently - we never
    # stop it on exit, unlike the old pxpipe child process.
    Write-Log "Cleanup initiated"
    Release-Mutex
    Save-Configuration
    Write-Log "Cleanup complete"
}

# ============================================================================
# ENVIRONMENT VALIDATION
# ============================================================================

function Test-WindowsVersion {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        if (([version]$os.Version).Major -lt 10) {
            Write-Fail "Unsupported Windows version"
            Write-Hint "Detected: $($os.Caption) - requires Windows 10 or higher"
            Stop-Script -Code 101
        }
        Write-Success "Windows $($os.Version) detected"
        Write-Log "OS: $($os.Caption), Version: $($os.Version)"
    } catch {
        Write-Warning "Could not verify Windows version, continuing..."
        Write-Log "OS detection failed: $_" -Level "WARN"
    }
}

# ============================================================================
# PATH AUGMENTATION
# ============================================================================

function Add-StandardPaths {
    $patterns = @(
        "$env:LOCALAPPDATA\Programs\Python\*\Scripts",
        "$env:APPDATA\Python\*\Scripts",
        "$env:ProgramFiles\Python*\Scripts",
        "$env:USERPROFILE\.local\bin",
        "$env:ProgramFiles\Git\cmd",
        "$env:ProgramFiles\Git\bin",
        "${env:ProgramFiles(x86)}\Git\cmd",
        "$env:ProgramFiles\nodejs",
        "$env:LOCALAPPDATA\Programs\nodejs",
        "$env:USERPROFILE\scoop\shims",
        "$env:APPDATA\npm",
        "$env:ProgramData\chocolatey\bin",
        "$env:LOCALAPPDATA\Microsoft\WindowsApps"
    )
    $addedCount = 0
    foreach ($pattern in $patterns) {
        try {
            foreach ($resolvedPath in (Resolve-Path -Path $pattern -ErrorAction SilentlyContinue)) {
                $pathStr = $resolvedPath.Path
                if ($env:PATH -notlike "*$pathStr*") {
                    $env:PATH = "$env:PATH;$pathStr"
                    $addedCount++
                }
            }
        } catch {}
    }
    if ($addedCount -gt 0) { Write-Log "Added $addedCount directories to PATH" }
}

function Sync-ProcessPathFromRegistry {
    # Freshly-installed tools (via winget/npm) update the Machine/User PATH in
    # the registry, but this already-running process never re-reads it. Pull
    # both scopes and merge them into $env:PATH so new installs are usable
    # immediately, without restarting the shell.
    try {
        $machinePath = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
        $userPath = [System.Environment]::GetEnvironmentVariable("Path", "User")
        $combined = @($machinePath, $userPath, $env:PATH) -join ';'
        $parts = $combined -split ';' | Where-Object { $_ -and $_.Trim() } | Select-Object -Unique
        $env:PATH = ($parts -join ';')
        Write-Log "Synced PATH from registry ($($parts.Count) entries)" -Level "DEBUG"
    } catch { Write-Log "PATH sync from registry failed: $_" -Level "DEBUG" }
    $script:DependencyCache = @{}
    Add-StandardPaths
}

# ============================================================================
# AUTO-INSTALL / AUTO-UPDATE (winget-based, best-effort on any Windows 10/11)
#   winget ships by default on Windows 11 and Windows 10 2004+ (via the App
#   Installer package, auto-updated through the Store on most machines). When
#   it's missing (older Win10, winget disabled by policy, etc.) we degrade
#   gracefully to the old "tell the user where to get it" behavior instead of
#   failing - this script must not assume winget exists.
# ============================================================================

function Test-WingetAvailable {
    if ($script:DependencyCache.ContainsKey("__winget__")) { return $script:DependencyCache["__winget__"] }
    $available = [bool](Get-Command "winget" -ErrorAction SilentlyContinue)
    if ($available) {
        # winget can exist on PATH but still be a stub with no working source
        # (fresh machine, first launch). A cheap sanity call confirms it works.
        try {
            $probe = Invoke-ExternalCommand -Command "winget" -Arguments "--version" -TimeoutSeconds 10 -Silent -NoLog
            $available = $probe.Success
        } catch { $available = $false }
    }
    $script:DependencyCache["__winget__"] = $available
    return $available
}

function Install-ViaWinget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WingetId,
        [Parameter(Mandatory)][string]$FriendlyName,
        [int]$TimeoutSeconds = 300
    )
    if (-not (Test-WingetAvailable)) { return $false }
    Write-Info "Installing $FriendlyName via winget ($WingetId)..."
    $baseArgs = "install --id $WingetId -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity"
    $result = Invoke-ExternalCommand -Command "winget" -Arguments $baseArgs -TimeoutSeconds $TimeoutSeconds -ShowSpinner -SpinnerLabel "Installing $FriendlyName"
    # Exit code -1978335189 / 0x8A150061 = "already installed" in winget - treat as success.
    if ($result.Success -or $result.Output -match "already installed|No available upgrade") {
        Write-Success "$FriendlyName installed"
        Sync-ProcessPathFromRegistry
        return $true
    }

    # Machine-scope installs commonly fail silently (no UAC prompt possible in
    # --disable-interactivity mode) on a non-admin account, which is the
    # default on a clean Windows box. Retry per-user scope, which most
    # packages (Git, Node.js, Python) support and doesn't need elevation.
    if ($result.Output -match "requires administrator|elevat|access is denied|0x80070005") {
        Write-Info "Machine-wide install needs admin - retrying as a per-user install..."
        $userArgs = "$baseArgs --scope user"
        $result = Invoke-ExternalCommand -Command "winget" -Arguments $userArgs -TimeoutSeconds $TimeoutSeconds
        if ($result.Success -or $result.Output -match "already installed|No available upgrade") {
            Write-Success "$FriendlyName installed (per-user)"
            Sync-ProcessPathFromRegistry
            return $true
        }
    }

    Write-Warning "$FriendlyName installation via winget did not confirm success"
    Write-Log "winget install $WingetId output: $(Get-Truncated $result.Output 400)" -Level "WARN"
    Write-Hint "You may need to run this script as Administrator, or install $FriendlyName manually."
    return $false
}

function Update-ViaWinget {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WingetId, [Parameter(Mandatory)][string]$FriendlyName)
    if (-not (Test-WingetAvailable)) { return }
    $wingetArgs = "upgrade --id $WingetId -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity"
    $result = Invoke-ExternalCommand -Command "winget" -Arguments $wingetArgs -TimeoutSeconds 180 -Silent
    if ($result.Success) { Write-Success "$FriendlyName up to date"; Sync-ProcessPathFromRegistry }
    elseif ($result.Output -match "No applicable update|No installed package") { Write-Log "$FriendlyName already latest (winget)" -Level "DEBUG" }
    else { Write-Log "winget upgrade $WingetId output: $(Get-Truncated $result.Output 300)" -Level "DEBUG" }
}

function Install-MissingDependencies {
    [CmdletBinding()]
    param([array]$Missing)

    $installMap = [ordered]@{
        "Git"     = @{ WingetId = "Git.Git";                    FriendlyName = "Git" }
        "Node.js" = @{ WingetId = "OpenJS.NodeJS.LTS";           FriendlyName = "Node.js LTS" }
        # npm is not an independent package - it ships bundled with Node.js.
        # If npm is missing (but node itself might already be present) the
        # Node install is broken/incomplete; reinstalling Node.js repairs it.
        "npm"     = @{ WingetId = "OpenJS.NodeJS.LTS";           FriendlyName = "Node.js LTS (repairs npm)" }
        "Python"  = @{ WingetId = "Python.Python.3.12";          FriendlyName = "Python 3.12" }
    }
    $toInstall = @($Missing | Where-Object { $installMap.Contains($_.Name) })
    if ($toInstall.Count -eq 0) { return }

    if (-not (Test-WingetAvailable)) {
        Write-Warning "winget is not available on this machine - cannot auto-install missing tools"
        Write-Hint "Install winget from the Microsoft Store ('App Installer'), or install these manually:"
        foreach ($dep in $toInstall) { Write-Hint "  - $($dep.Name): $($dep.Info.Url)" }
        return
    }

    Write-Section "Auto-install"
    Write-Info "winget detected - installing missing tools automatically..."
    # Dedupe by WingetId - if both Node.js and npm are missing, that's one
    # Node.js reinstall, not two.
    $seenIds = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($dep in $toInstall) {
        $spec = $installMap[$dep.Name]
        if (-not $seenIds.Add($spec.WingetId)) { continue }
        $null = Install-ViaWinget -WingetId $spec.WingetId -FriendlyName $spec.FriendlyName
    }
    # npm/pip only exist once Node/Python are actually installed - re-detect.
    $script:DependencyCache = @{}
}

function Invoke-UpdateCheckIfRequested {
    Write-Section "Update checks"
    Write-Hint "Checks Git/Node/Python/Graphify/Claude Code/OmniRoute for newer versions."
    if (-not (Read-YesNo "Check for updates now?" $false)) {
        Write-Info "Skipping update check"
        return
    }
    Update-AllDependencies
    Update-GraphifyIfNeeded
}

function Update-AllDependencies {
    # Best-effort, short timeouts. Only runs when the user says yes to
    # Invoke-UpdateCheckIfRequested's prompt, asked fresh every launch.
    Write-Section "Checking for updates"
    if (Test-WingetAvailable) {
        if (Test-CommandAvailable "git" -UseCache) { Update-ViaWinget -WingetId "Git.Git" -FriendlyName "Git" }
        if (Test-CommandAvailable "node" -UseCache) { Update-ViaWinget -WingetId "OpenJS.NodeJS.LTS" -FriendlyName "Node.js" }
        if (Test-CommandAvailable "python" -UseCache) { Update-ViaWinget -WingetId "Python.Python.3.12" -FriendlyName "Python" }
    } else {
        Write-Info "winget unavailable - skipping tool version checks"
    }
    if ((Test-CommandAvailable "npm" -UseCache) -and (Test-CommandAvailable "claude" -UseCache)) {
        Write-Info "Checking Claude Code for updates..."
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "update -g @anthropic-ai/claude-code" -TimeoutSeconds 120 -Silent
        if ($result.Success) { Write-Success "Claude Code up to date" }
    }
    if (Test-CommandAvailable "omniroute" -UseCache) { Update-OmniRouteCli }
    if (Test-CommandAvailable "autoskills" -UseCache) {
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "update -g autoskills" -TimeoutSeconds 60 -Silent
        if ($result.Success) { Write-Success "autoskills up to date" }
    }
    Write-Success "Update check complete"
}

# ============================================================================
# EXTERNAL COMMAND WRAPPER
# ============================================================================

function Invoke-ExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Command,
        [string]$Arguments = "",
        [string]$WorkingDirectory = $PWD.Path,
        [int]$TimeoutSeconds = 0,
        [switch]$Silent,
        [switch]$NoLog,
        [switch]$ShowSpinner,
        [string]$SpinnerLabel = ""
    )
    $result = @{ Success = $false; Output = ""; ExitCode = -1; TimedOut = $false }
    if (-not $NoLog) { Write-Log "Exec: $Command $Arguments" -Level "DEBUG" }
    $process = $null
    try {
        # Resolve the command so we can correctly launch .cmd/.bat/.ps1 shims.
        # With UseShellExecute=$false the Windows process API cannot start a
        # batch file (npm.cmd, claude.cmd, etc.) directly - it must be run
        # through cmd.exe. Bare .exe/console commands are launched as-is.
        $fileName = $Command
        $effectiveArgs = $Arguments
        try { $resolved = Get-Command $Command -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $resolved = $null }
        if ($resolved -and $resolved.Source) {
            $src = $resolved.Source
            switch (([System.IO.Path]::GetExtension($src)).ToLowerInvariant()) {
                ".cmd" { $fileName = $env:ComSpec; $effectiveArgs = "/c `"`"$src`" $Arguments`"" }
                ".bat" { $fileName = $env:ComSpec; $effectiveArgs = "/c `"`"$src`" $Arguments`"" }
                ".ps1" { $fileName = "powershell.exe"; $effectiveArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$src`" $Arguments" }
                default { $fileName = $src }
            }
        }

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $fileName
        $psi.Arguments = $effectiveArgs
        $psi.WorkingDirectory = $WorkingDirectory
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) {
            Write-Log "Failed to start process: $Command" -Level "ERROR"
            $result.Output = "Process failed to start"
            return $result
        }
        # Capture stdout/stderr with async stream reads instead of scriptblock
        # event handlers. The add_OutputDataReceived / BeginOutputReadLine
        # pattern runs handlers on background threads and is unstable in
        # Windows PowerShell 5.1 (it can crash the whole process). Kicking off
        # both ReadToEndAsync reads BEFORE waiting drains the pipes so the child
        # never blocks on a full buffer, and avoids the classic deadlock.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($ShowSpinner -and -not $Silent) {
            $label = if ($SpinnerLabel) { $SpinnerLabel } else { $Command }
            $frameIdx = 0
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $timedOut = $false
            while (-not $process.HasExited) {
                if ($TimeoutSeconds -gt 0 -and $sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; break }
                Write-Spinner -Label $label -FrameIndex $frameIdx -Elapsed $sw.Elapsed.ToString('mm\:ss')
                Start-Sleep -Milliseconds 150
                $frameIdx++
            }
            Clear-ProgressLine
            if ($timedOut) {
                Write-Log "Process timeout: $Command (${TimeoutSeconds}s)" -Level "WARN"
                try { $process.Kill() } catch {}
                $result.TimedOut = $true
                $result.Output = "Command timed out after ${TimeoutSeconds}s"
                return $result
            }
        } elseif ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                Write-Log "Process timeout: $Command (${TimeoutSeconds}s)" -Level "WARN"
                try { $process.Kill() } catch {}
                $result.TimedOut = $true
                $result.Output = "Command timed out after ${TimeoutSeconds}s"
                return $result
            }
        } else {
            $process.WaitForExit()
        }
        $stdout = ""; $stderr = ""
        try { $stdout = $stdoutTask.Result } catch {}
        try { $stderr = $stderrTask.Result } catch {}
        $result.ExitCode = $process.ExitCode
        $result.Success = ($process.ExitCode -eq 0)
        $result.Output = ($stdout + $stderr).Trim()
        if (-not $NoLog) { Write-Log "Exit: $($result.ExitCode) | Success: $($result.Success)" }
    } catch {
        Write-Log "Command exception ($Command): $_" -Level "ERROR"
        $result.Output = $_.Exception.Message
    } finally {
        if ($process) { try { $process.Dispose() } catch {} }
    }
    return $result
}

# ============================================================================
# DEPENDENCY DETECTION
# ============================================================================

function Test-CommandAvailable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [switch]$UseCache)
    if ($UseCache -and $script:DependencyCache.ContainsKey($Name)) { return $script:DependencyCache[$Name] }
    $result = [bool](Get-Command $Name -ErrorAction SilentlyContinue)
    $script:DependencyCache[$Name] = $result
    return $result
}

function Find-ExecutableInPaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string[]]$SearchPaths)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($basePath in $SearchPaths) {
        foreach ($candidate in @((Join-Path $basePath "$Name.exe"), (Join-Path $basePath "$Name.cmd"), (Join-Path $basePath $Name))) {
            if (Test-Path $candidate -PathType Leaf) { return $candidate }
        }
    }
    return $null
}

function Get-DependencySummary {
    Write-Section "Dependencies"
    $dependencies = [ordered]@{
        "Git"      = @{ Command = "git";      Required = $true; Url = "https://git-scm.com/download/win"; Advice = "" }
        "Node.js"  = @{ Command = "node";     Required = $true; Url = "https://nodejs.org";               Advice = "Install the LTS version" }
        "npm"      = @{ Command = "npm";      Required = $true; Url = "https://nodejs.org";               Advice = "Included with Node.js" }
        "Python"   = @{ Command = "python";   Required = $true; Url = "https://python.org";               Advice = "Install Python 3.10+" }
        "pip"      = @{ Command = "pip";      Required = $true; Url = "https://python.org";               Advice = "Check 'Add pip' during Python install" }
        "Graphify" = @{ Command = "graphify"; Required = $true; Url = "pip install graphify";             Advice = "Auto-installed if missing" }
        "Claude"   = @{ Command = "claude";   Required = $true; Url = "https://claude.ai";                Advice = "Claude Code CLI" }
    }
    $missing = [System.Collections.ArrayList]::new()
    foreach ($name in $dependencies.Keys) {
        $dep = $dependencies[$name]
        if (Test-CommandAvailable -Name $dep.Command -UseCache) {
            $version = ""
            try {
                $verResult = Invoke-ExternalCommand -Command $dep.Command -Arguments "--version" -TimeoutSeconds 5 -Silent
                if ($verResult.Success) { $version = ($verResult.Output.Trim() -replace "`r`n", " " -replace "`n", " ") }
            } catch {}
            Write-Success ("{0} {1}" -f $name.PadRight(9), $version)
        } else {
            $null = $missing.Add(@{ Name = $name; Info = $dep })
            Write-Fail ("{0} not found" -f $name.PadRight(9))
        }
    }
    return @{ Missing = @($missing); Dependencies = $dependencies }
}

function Test-RequiredDependencies {
    [CmdletBinding()]
    param([array]$Missing)

    # Graphify + Claude are excluded: Graphify is auto-installed via pip
    # below, and Claude has its own multi-strategy detection/install.
    $fatalMissing = @($Missing | Where-Object { $_.Info.Required -and $_.Name -notin @("Graphify", "Claude", "pip") })
    if ($fatalMissing.Count -eq 0) { return }

    # Try to auto-install whatever winget can handle (Git, Node.js, Python).
    Install-MissingDependencies -Missing $fatalMissing

    # Re-check after the install attempt.
    $depSummary = Get-DependencySummary
    $stillMissing = @($depSummary.Missing | Where-Object { $_.Info.Required -and $_.Name -notin @("Graphify", "Claude", "pip") })

    if (@($depSummary.Missing | Where-Object { $_.Name -eq "pip" }).Count -gt 0) {
        Write-Host ""
        Write-Fail "Python was found but pip is missing"
        Write-Hint "Reinstall Python with 'Add Python to PATH' and 'Install pip' checked."
        Stop-Script -Code 102
    }

    if ($stillMissing.Count -gt 0) {
        Write-Host ""
        Write-Fail "Some required dependencies could not be auto-installed:"
        foreach ($dep in $stillMissing) {
            Write-Hint "  - $($dep.Name): $($dep.Info.Url)"
            if ($dep.Info.Advice) { Write-Hint "      $($dep.Info.Advice)" }
        }
        Write-Hint "Install them manually, then run this script again."
        Stop-Script -Code 102
    }
}

# ============================================================================
# GRAPHIFY MANAGEMENT
# ============================================================================

function Install-Graphify {
    [CmdletBinding()]
    param([switch]$Silent)
    Write-Info "Installing Graphify via pip..."
    $result = Invoke-ExternalCommand -Command "pip" -Arguments "install graphify" -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "Installing Graphify"
    if ($result.Success) {
        $script:DependencyCache["graphify"] = $false
        if (Test-CommandAvailable -Name "graphify") { Write-Success "Graphify installed"; return $true }
    }
    Write-Fail "Graphify installation failed"
    if (-not $Silent) { Write-Hint (Get-Truncated $result.Output 200) }
    return $false
}

function Test-GraphifyVersion {
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "--version" -TimeoutSeconds 10
    if ($result.Success) {
        $version = $result.Output.Trim() -replace "`r`n", ""
        Write-Success "$version ready"
        $script:Config.LastGraphifyVersion = $version
        Save-Configuration
        Write-Log "Graphify version: $version"
        return $true
    }
    return $false
}

function Update-GraphifyIfNeeded {
    Write-Info "Checking for Graphify updates..."
    $result = Invoke-ExternalCommand -Command "pip" -Arguments "install --upgrade graphify" -TimeoutSeconds 180 -Silent
    if ($result.Success) {
        $script:DependencyCache["graphify"] = $false
        Write-Success "Graphify up to date"
        Write-Log "Graphify updated successfully"
    }
}

# ============================================================================
# CLAUDE DETECTION
# ============================================================================

function Find-ClaudeExecutable {
    Write-Section "Claude"
    if ($script:Config.ClaudePath -and (Test-Path $script:Config.ClaudePath -PathType Leaf)) {
        Write-Success "Found (saved): $($script:Config.ClaudePath)"
        return $script:Config.ClaudePath
    }
    if (Test-CommandAvailable "claude" -UseCache) {
        $path = (Get-Command "claude").Source
        Write-Success "Found on PATH: $path"
        $script:Config.ClaudePath = $path; Save-Configuration
        return $path
    }
    $searchPaths = @(
        "$env:LOCALAPPDATA\Programs\claude",
        "$env:ProgramFiles\Claude",
        "${env:ProgramFiles(x86)}\Claude",
        "$env:USERPROFILE\.local\bin",
        "$env:APPDATA\npm\node_modules\@anthropic-ai\claude-code\bin",
        "$env:LOCALAPPDATA\Programs\claude-code"
    )
    $found = Find-ExecutableInPaths -Name "claude" -SearchPaths $searchPaths
    if ($found) {
        Write-Success "Found: $found"
        $script:Config.ClaudePath = $found; Save-Configuration
        return $found
    }
    $regResult = Find-ClaudeInRegistry
    if ($regResult) {
        Write-Success "Found (registry): $regResult"
        $script:Config.ClaudePath = $regResult; Save-Configuration
        return $regResult
    }

    if (Test-CommandAvailable "npm" -UseCache) {
        Write-Info "Claude CLI not found - installing via npm..."
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g @anthropic-ai/claude-code" -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "Installing Claude Code"
        if ($result.Success) {
            Sync-ProcessPathFromRegistry
            if (Test-CommandAvailable "claude") {
                $path = (Get-Command "claude").Source
                Write-Success "Installed via npm: $path"
                $script:Config.ClaudePath = $path; Save-Configuration
                return $path
            }
        }
        Write-Warning "npm install of Claude Code did not confirm success"
        Write-Log "npm install claude-code output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    }

    return Request-ClaudePathFromUser
}

function Find-ClaudeInRegistry {
    $regPaths = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($regPath in $regPaths) {
        try {
            foreach ($app in (Get-ItemProperty $regPath -ErrorAction SilentlyContinue)) {
                if (($app.PSObject.Properties.Name -contains "DisplayName") -and $app.DisplayName -match "Claude") {
                    $installDir = $app.InstallLocation
                    if ($installDir) {
                        foreach ($leaf in @("claude.exe", "claude.cmd")) {
                            $candidate = Join-Path $installDir $leaf
                            if (Test-Path $candidate) { return $candidate }
                        }
                    }
                }
            }
        } catch { Write-Log "Registry search error: $_" -Level "DEBUG" }
    }
    return $null
}

function Request-ClaudePathFromUser {
    Write-Warning "Claude CLI could not be detected automatically."
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = "Select Claude Executable"
        $dialog.Filter = "Executables (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*"
        $dialog.FileName = "claude.exe"
        if ($dialog.ShowDialog() -eq "OK" -and (Test-Path $dialog.FileName -PathType Leaf)) {
            $script:Config.ClaudePath = $dialog.FileName; Save-Configuration
            Write-Success "Claude path saved: $($dialog.FileName)"
            return $dialog.FileName
        }
    } catch { Write-Log "File dialog unavailable: $_" -Level "DEBUG" }
    $manualPath = (Read-Host "  Enter the full path to claude.exe").Trim().Trim('"')
    if ($manualPath -and (Test-Path $manualPath -PathType Leaf)) {
        $script:Config.ClaudePath = $manualPath; Save-Configuration
        Write-Success "Claude path saved: $manualPath"
        return $manualPath
    }
    Write-Fail "Claude CLI not found"
    Write-Hint "Install Claude Code first: https://claude.ai"
    Stop-Script -Code 103
}

function Test-ClaudeExecutable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $result = Invoke-ExternalCommand -Command $Path -Arguments "--version" -TimeoutSeconds 10 -Silent
    if ($result.Success) { Write-Success "Verified ($($result.Output.Trim()))" }
    else { Write-Info "Version check skipped (executable present)" }
}

# ============================================================================
# OMNIROUTE MANAGEMENT
#   Replaces the old pxpipe proxy entirely. OmniRoute is a standalone local
#   app/server (http://localhost:20128) that Claude Code is pointed at via
#   env vars. All compression (RTK -> Caveman -> LLMLingua -> Lite) happens
#   inside OmniRoute itself - nothing to configure here beyond routing to it.
# ============================================================================

function Test-OmniRouteRunning {
    try {
        $null = Invoke-RestMethod -Uri "$script:OMNIROUTE_URL/v1/models" -Method Get -TimeoutSec 3 -ErrorAction Stop
        return $true
    } catch { return $false }
}

function Install-OmniRouteCli {
    if (-not (Test-CommandAvailable "npm" -UseCache)) { return $false }
    Write-Info "OmniRoute CLI not found - installing via npm (omniroute@latest)..."
    $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g omniroute@latest" -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "Installing OmniRoute"
    if ($result.Success) {
        Sync-ProcessPathFromRegistry
        if (Test-CommandAvailable "omniroute") { Write-Success "OmniRoute CLI installed"; return $true }
    }
    Write-Warning "npm install of OmniRoute did not confirm success"
    Write-Log "npm install omniroute output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Update-OmniRouteCli {
    if (-not (Test-CommandAvailable "omniroute" -UseCache)) { return }
    Write-Info "Checking OmniRoute for updates..."
    # Prefer the CLI's own updater (it knows its release channel); fall back
    # to a plain npm bump if `omniroute update` isn't available.
    $result = Invoke-ExternalCommand -Command "omniroute" -Arguments "update" -TimeoutSeconds 120 -Silent
    if ($result.Success) { Write-Success "OmniRoute up to date"; return }
    if (Test-CommandAvailable "npm" -UseCache) {
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g omniroute@latest" -TimeoutSeconds 180 -Silent
        if ($result.Success) { Write-Success "OmniRoute up to date (npm)" }
        else { Write-Log "OmniRoute update output: $(Get-Truncated $result.Output 300)" -Level "DEBUG" }
    }
}

function Start-OmniRoute {
    Write-Section "OmniRoute"
    if (Test-OmniRouteRunning) { Write-Success "Already running at $script:OMNIROUTE_URL"; return $true }

    if (-not (Test-CommandAvailable "omniroute" -UseCache)) {
        if (-not (Install-OmniRouteCli)) {
            Write-Warning "Could not install the OmniRoute CLI automatically"
            Write-Hint "Install it manually: npm install -g omniroute@latest"
        }
    } else {
        Update-OmniRouteCli
    }

    Write-Info "OmniRoute not detected - attempting to start it..."
    $started = $false

    if (Test-CommandAvailable "omniroute" -UseCache) {
        try {
            # Launch in its own titled, minimized console - a separate window
            # from both this launcher and Claude Code, so the two never share
            # a console (Claude needs its own for interactive input/output).
            # Minimized (not Hidden): OmniRoute is a long-running server the
            # user may want to glance at or Ctrl+C, so keep it reachable in
            # the taskbar instead of fully invisible.
            $cmdArgs = '/c "title OmniRoute Server && omniroute"'
            Start-Process -FilePath "cmd.exe" -ArgumentList $cmdArgs -WindowStyle Minimized -ErrorAction Stop
            $started = $true
            Write-Log "Started OmniRoute via 'omniroute' (minimized, titled window)"
        } catch { Write-Log "omniroute launch failed: $_" -Level "DEBUG" }
    }

    if (-not $started) {
        Write-Warning "Could not auto-launch OmniRoute ('omniroute' not found on PATH)"
        Write-Hint "Start it manually by running 'omniroute', then press Enter"
        try { $null = Read-Host } catch {}
        return $false
    }

    # Don't block here waiting for the HTTP server to come online - it opened
    # in its own window and boots on its own timeline (~10-20s on a cold
    # start). The rest of this launcher (Graphify extraction, autoskills,
    # project selection) takes long enough that OmniRoute is almost always
    # ready by the time Claude actually launches and needs it - and if it
    # isn't yet, Set-OmniRouteLaunchEnvironment / Test-OmniRouteRunning at
    # that point just proceed without compression rather than erroring.
    Write-Success "OmniRoute launching in its own window - continuing without waiting"
    Write-Hint "It usually finishes booting in 10-20s; the rest of setup runs in parallel with that."
    return $true
}

function Set-OmniRouteDisabled {
    $script:Config.UseOmniRoute = $false
    Save-Configuration
    return $false
}

function Protect-OmniRouteApiKey {
    param([Parameter(Mandatory)][string]$PlainKey)
    $secure = ConvertTo-SecureString -String $PlainKey -AsPlainText -Force
    return ($secure | ConvertFrom-SecureString)
}

function Get-OmniRouteApiKey {
    if (-not $script:Config.OmniRouteApiKeyEnc) { return $null }
    try {
        $secure = $script:Config.OmniRouteApiKeyEnc | ConvertTo-SecureString
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } catch {
        Write-Log "Failed to decrypt OmniRoute API key: $_" -Level "ERROR"
        return $null
    }
}

function Set-ClaudeAvailableModels {
    # Restricts the /model picker to exactly two entries: claude-opus-5 and
    # claude-sonnet-5, both routed through OmniRoute. Fable and Haiku are
    # deliberately NOT included anymore - per explicit instruction, nothing
    # else should be selectable. Claude Code's own `availableModels` setting
    # (~/.claude/settings.json) is the documented allowlist mechanism, and it
    # applies to gateway-discovered models too.
    #
    # NOTE: "claude-opus-5" does not exist in OmniRoute's current catalog
    # (the current Opus release is "claude-opus-4-8") - this is a literal,
    # intentional match on "claude-opus-5" specifically. Until a model with
    # that exact name ships, no Opus entry will appear in /model at all;
    # only Sonnet 5 will be selectable. This is expected, not a bug.
    $claudeDir = Join-Path $env:USERPROFILE ".claude"
    $settingsPath = Join-Path $claudeDir "settings.json"
    $wanted = @("claude-opus-5", "claude-sonnet-5")
    try {
        if (-not (Test-Path $claudeDir)) { New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null }
        $settings = if (Test-Path $settingsPath) {
            try { Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
            catch { Write-Log "Existing ~/.claude/settings.json invalid, recreating: $_" -Level "WARN"; [PSCustomObject]@{} }
        } else { [PSCustomObject]@{} }

        $current = @()
        if ($settings.PSObject.Properties.Name -contains "availableModels") { $current = @($settings.availableModels) }
        if (-not (@(Compare-Object $current $wanted -SyncWindow 0).Count -eq 0)) {
            if ($settings.PSObject.Properties.Name -contains "availableModels") {
                $settings.availableModels = $wanted
            } else {
                $settings | Add-Member -NotePropertyName "availableModels" -NotePropertyValue $wanted
            }
            $settings | ConvertTo-Json -Depth 10 | Out-File -FilePath $settingsPath -Encoding UTF8 -Force
            Write-Success "Model picker restricted to claude-opus-5 and claude-sonnet-5 only"
            Write-Log "Wrote availableModels to $settingsPath : $($wanted -join ', ')"
        } else {
            Write-Log "availableModels already set correctly - skipping write" -Level "DEBUG"
        }
    } catch {
        Write-Warning "Could not restrict the model picker (settings.json write failed)"
        Write-Log "Set-ClaudeAvailableModels failed: $_" -Level "WARN"
    }
}

function Initialize-OmniRoute {
    if ($SkipOmniRoute) { Write-Info "OmniRoute routing disabled via -SkipOmniRoute"; return $false }

    Write-Section "OmniRoute routing"
    if ($null -eq $script:Config.UseOmniRoute) {
        Write-Hint "Routes Claude Code (Sonnet or Opus - your choice, real Claude either way)"
        Write-Hint "through OmniRoute, which auto-applies its compression pipeline"
        Write-Hint "(RTK -> Caveman -> LLMLingua -> Lite) to every request."
        $script:Config.UseOmniRoute = Read-YesNo "Route Claude Code through OmniRoute?" $true
        $script:Config.FirstRunComplete = $true
        Save-Configuration
    }
    if (-not $script:Config.UseOmniRoute) { Write-Info "OmniRoute routing disabled"; return $false }

    if (-not $script:Config.OmniRouteApiKeyEnc) {
        Write-Hint "Grab your key from the OmniRoute dashboard (Settings -> API Keys)."
        $secure = Read-Host "  OmniRoute API key" -AsSecureString
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        if ([string]::IsNullOrWhiteSpace($plain)) {
            Write-Warning "No key entered - disabling OmniRoute routing"
            return (Set-OmniRouteDisabled)
        }
        $script:Config.OmniRouteApiKeyEnc = Protect-OmniRouteApiKey -PlainKey $plain
        Save-Configuration
        Write-Success "API key saved (encrypted for this Windows account only)"
    }

    $started = Start-OmniRoute
    if ($started) {
        Initialize-ClaudeCodeProvider
        Set-ClaudeAvailableModels
    }
    return $started
}

# ============================================================================
# OMNIROUTE CLAUDE CODE PROVIDER CONNECTION
#   OmniRoute needs a Claude Code OAuth connection (your Claude.ai login,
#   added on the OmniRoute dashboard) before auto/claude-sonnet and
#   auto/claude-opus can actually route anywhere. That login is a browser
#   OAuth flow - it cannot be scripted headlessly - so the best this script
#   can do is detect whether it's already connected via `omniroute providers
#   list --json` and, if not, open the exact dashboard page and wait for you
#   to click "+ Add" and sign in.
# ============================================================================

function Test-OmniRouteClaudeProviderConnected {
    $result = Invoke-ExternalCommand -Command "omniroute" -Arguments "providers list --json" -TimeoutSeconds 15 -Silent -NoLog
    if (-not $result.Success) { return $false }
    try {
        $providers = $result.Output | ConvertFrom-Json -ErrorAction Stop
        foreach ($p in @($providers)) {
            $idText = ("$($p.id) $($p.name)").ToLowerInvariant()
            if ($idText -notmatch "claude") { continue }
            $statusText = "$($p.status)".ToLowerInvariant()
            if ($statusText -match "connect|active|ok" -or $p.connected -eq $true -or $p.enabled -eq $true) { return $true }
        }
    } catch { Write-Log "Could not parse 'omniroute providers list --json' output: $_" -Level "DEBUG" }
    return $false
}

function Initialize-ClaudeCodeProvider {
    if (-not (Test-CommandAvailable "omniroute" -UseCache)) { return }
    if (Test-OmniRouteClaudeProviderConnected) { Write-Success "Claude Code provider already connected in OmniRoute"; return }

    Write-Section "OmniRoute: Claude Code provider"
    Write-Warning "No Claude Code account connected in OmniRoute yet"
    Write-Hint "This is a one-time browser sign-in OmniRoute requires (can't be"
    Write-Hint "automated from the CLI) before auto/claude-sonnet and"
    Write-Hint "auto/claude-opus have anything to route through."
    $providerUrl = "$script:OMNIROUTE_URL/dashboard/providers/claude"
    try { Start-Process $providerUrl; Write-Hint "Opened: $providerUrl" }
    catch { Write-Log "Could not open browser to ${providerUrl}: $_" -Level "DEBUG"; Write-Hint "Open manually: $providerUrl" }
    Write-Hint "Click '+ Add', sign in with your Claude.ai account, then come back here."
    try { $null = Read-Host "  Press Enter once you've added the connection (or just Enter to skip for now)" } catch {}

    if (Test-OmniRouteClaudeProviderConnected) { Write-Success "Claude Code provider connected" }
    else { Write-Warning "Still not detected - you can finish this later at $providerUrl" }
}

function Resolve-OmniRouteClaudeModelId {
    # Asks OmniRoute's live /v1/models catalog for the exact ID it's using
    # for Opus and Sonnet - the only two families this script supports now.
    # No "auto/*" combo involved anywhere, and no fallback substitution: if
    # nothing in the catalog matches, this returns $null and the caller
    # leaves that family's env var unset entirely.
    #
    # Opus is matched literally against "claude-opus-5" - NOT "claude-opus-
    # 4-8" (the current real Opus release name in OmniRoute's catalog as of
    # this writing). That's intentional, per explicit instruction: until a
    # model literally named claude-opus-5 exists, this will find nothing and
    # Opus simply won't be pinned or appear in /model - only Sonnet 5 will.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('opus', 'sonnet')][string]$Family,
        [Parameter(Mandatory)][string]$ApiKey
    )
    try {
        $headers = @{ "Authorization" = "Bearer $ApiKey" }
        $resp = Invoke-RestMethod -Uri "$script:OMNIROUTE_URL/v1/models" -Headers $headers -Method Get -TimeoutSec 8 -ErrorAction Stop
        $ids = @($resp.data | ForEach-Object { $_.id }) + @($resp.models | ForEach-Object { $_.id })
        $ids = @($ids | Where-Object { $_ -and $_ -notmatch '^auto/' })
    } catch {
        Write-Log "Could not fetch /v1/models to pin exact $Family version: $_" -Level "DEBUG"
        return $null
    }
    if ($ids.Count -eq 0) { return $null }

    # Sonnet excludes the "[1m]"/"1m" long-context variant so it doesn't
    # accidentally pin to that instead of the standard model. "auto/*" IDs
    # are already filtered out above.
    $patterns = switch ($Family) {
        'opus'   { @('^claude-opus-5$', 'claude-opus-5') }
        'sonnet' { @('^claude-sonnet-5$', 'claude-sonnet-5(?!.*1m)') }
    }
    foreach ($pattern in $patterns) {
        $match = $ids | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($match) { return $match }
    }
    Write-Log "No exact $Family match in OmniRoute's catalog (excluding auto/*)" -Level "DEBUG"
    return $null
}

function Set-OmniRouteLaunchEnvironment {
    # Process-scoped only - does not touch the permanent environment.
    #
    # CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY is real after all - it's
    # OmniRoute's own env var (confirmed in OmniRoute's
    # docs/guides/CLAUDE-CODE-CONFIGURATION.md), and it does make the native
    # /model picker list claude*/anthropic*-prefixed IDs from /v1/models.
    # Only two picker rows are supported now (see Set-ClaudeAvailableModels):
    # Opus and Sonnet, each repointed at OmniRoute's own catalog entry via
    # the ANTHROPIC_DEFAULT_*_MODEL vars. Fable and Haiku are intentionally
    # not pinned anymore. No "auto/*" combo is used anywhere -
    # Resolve-OmniRouteClaudeModelId filters those out and returns $null
    # instead of substituting one, so a family with no exact OmniRoute
    # catalog match just falls back to Claude Code's own built-in default
    # for that alias rather than "auto".
    if (-not $script:Config.UseOmniRoute) { return }
    $apiKey = Get-OmniRouteApiKey
    if (-not $apiKey) {
        Write-Warning "No OmniRoute API key available - launching Claude directly (no compression)"
        return
    }
    $env:ANTHROPIC_BASE_URL = $script:OMNIROUTE_URL
    $env:ANTHROPIC_AUTH_TOKEN = $apiKey
    $env:CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY = "1"

    $modelPins = @(
        @{ Family = 'opus';   EnvVar = 'ANTHROPIC_DEFAULT_OPUS_MODEL';   NameVar = 'ANTHROPIC_DEFAULT_OPUS_MODEL_NAME';   Label = 'Opus 5 (OmniRoute)' }
        @{ Family = 'sonnet'; EnvVar = 'ANTHROPIC_DEFAULT_SONNET_MODEL'; NameVar = 'ANTHROPIC_DEFAULT_SONNET_MODEL_NAME'; Label = 'Sonnet 5 (OmniRoute)' }
    )
    $pinnedLog = [System.Collections.ArrayList]::new()
    foreach ($pin in $modelPins) {
        $resolvedId = Resolve-OmniRouteClaudeModelId -Family $pin.Family -ApiKey $apiKey
        if ($resolvedId) {
            [Environment]::SetEnvironmentVariable($pin.EnvVar, $resolvedId, "Process")
            [Environment]::SetEnvironmentVariable($pin.NameVar, $pin.Label, "Process")
            $null = $pinnedLog.Add("$($pin.Family) -> $resolvedId")
        } else {
            Write-Log "$($pin.Family): no OmniRoute catalog match - leaving Claude Code's built-in default in place" -Level "DEBUG"
        }
    }
    if ($pinnedLog.Count -gt 0) { Write-Log "Pinned via OmniRoute: $($pinnedLog -join ', ')" -Level "DEBUG" }

    if ($Model) {
        # -Model sonnet|opus: session-only override so this launch doesn't
        # fall back onto whatever Claude Code last saved as its default
        # (e.g. Fable, from a previous /model pick). Doesn't touch the saved
        # default and doesn't persist to the next launch.
        Write-Info "Forcing this session onto $Model (via -Model flag)"
        $script:ForcedModelAlias = $Model
    }

    Write-Info "Routing Claude through OmniRoute ($env:ANTHROPIC_BASE_URL)"
    Write-Hint "Compression applies automatically. Pick a model inside Claude Code with /model."
}

# ============================================================================
# PROJECT SELECTION
# ============================================================================

function Read-ProjectPath {
    Write-Section "Project"
    Write-Hint "Up/Down cycle history   Del remove   Esc cancel"
    Write-Host ""
    $history = @($script:Config.ProjectHistory)
    $index = $history.Count
    $currentInput = ""
    while ($true) {
        [Console]::CursorLeft = 0
        Write-Host (' ' * [Math]::Min((Get-SafeConsoleWidth) - 1, 120)) -NoNewline
        [Console]::CursorLeft = 0
        Write-Host "  Path: " -NoNewline -ForegroundColor White
        Write-Host $currentInput -NoNewline
        if (-not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 10; continue }
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq 'Enter') { Write-Host ""; break }
        elseif ($key.Key -eq 'UpArrow') { if ($history.Count -gt 0 -and $index -gt 0) { $index--; $currentInput = $history[$index] } }
        elseif ($key.Key -eq 'DownArrow') {
            if ($index -lt ($history.Count - 1)) { $index++; $currentInput = $history[$index] }
            else { $index = $history.Count; $currentInput = "" }
        }
        elseif ($key.Key -eq 'Backspace') { if ($currentInput.Length -gt 0) { $currentInput = $currentInput.Substring(0, $currentInput.Length - 1) } }
        elseif ($key.Key -eq 'Escape') { Write-Host ""; return $null }
        elseif ($key.Key -eq 'Delete') {
            if ($index -lt $history.Count -and $index -ge 0) {
                $removed = $history[$index]
                $history = @($history | Where-Object { $_ -ne $removed })
                $script:Config.ProjectHistory = @($history)
                Save-Configuration
                Write-Host ""
                Write-Info "Removed: $removed"
                $index = [Math]::Min($index, $history.Count)
                $currentInput = if ($index -lt $history.Count) { $history[$index] } else { "" }
                continue
            }
        }
        else {
            $currentInput += $key.KeyChar
            while ([Console]::KeyAvailable) { $currentInput += [Console]::ReadKey($true).KeyChar }
        }
    }
    $projectDir = $currentInput.Trim().Trim('"').Trim()
    if ($projectDir.EndsWith("\") -and $projectDir -notmatch '^[A-Za-z]:\\$') { $projectDir = $projectDir.Substring(0, $projectDir.Length - 1) }
    return $projectDir
}

function Test-ProjectAlreadyKnown {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    return (@($script:Config.ProjectHistory) -contains $Path)
}

function Add-ProjectToHistory {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $history = @($Path) + @($script:Config.ProjectHistory | Where-Object { $_ -ne $Path })
    if ($history.Count -gt $script:MAX_HISTORY) { $history = $history[0..($script:MAX_HISTORY - 1)] }
    $script:Config.ProjectHistory = [array]$history
    $script:Config.LastProject = $Path
    Save-Configuration
}

function Test-ProjectDirectory {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    if (-not $Path) { Write-Fail "Input cannot be blank"; return $false }
    if (-not (Test-Path $Path -PathType Container)) { Write-Fail "Not a directory: $Path"; return $false }
    if ($Path -match '^[A-Za-z]:\\$') { Write-Fail "Cannot process a drive root"; return $false }
    if (-not (Get-ChildItem $Path -ErrorAction SilentlyContinue)) { Write-Fail "Directory is empty - nothing to process"; return $false }
    try {
        $testFile = Join-Path $Path ".graphify_perm_test_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        "test" | Out-File -FilePath $testFile -ErrorAction Stop -NoNewline
        Remove-Item $testFile -Force -ErrorAction Stop
    } catch { Write-Fail "Missing write permissions"; return $false }
    Write-Success "Validated: $(Split-Path $Path -Leaf)"
    return $true
}

# ============================================================================
# GRAPHIFY OPERATIONS
#   NOTE: Graphify 0.17.1+ writes to a hidden .graphify\ directory (not
#   graphify-out\) and auto-generates the HTML studio during `extract`
#   itself - there is no separate `export html` step anymore.
# ============================================================================

function Install-GraphifyPlatform {
    if (Test-Path $script:GlobalGateFile) { Write-Success "Platform registration cached"; return }
    Write-Info "Registering Graphify with the Claude platform..."
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "install --platform claude" -TimeoutSeconds 60
    if ($result.Success) { Set-Marker $script:GlobalGateFile; Write-Success "Platform registered" }
    else { Write-Warning "Platform registration may have failed"; Write-Log "Platform reg output: $($result.Output)" -Level "WARN" }
}

function Install-GraphifyHook {
    $hookMarker = Join-Path $PWD ".graphify_hook_installed"
    if (Test-Path $hookMarker) { Write-Success "Hook already installed"; return }
    foreach ($attempt in 1..2) {
        if ($attempt -eq 1) { Write-Info "Installing Graphify hook..." }
        else { Write-Info "Retrying hook installation..."; Start-Sleep -Seconds 2 }
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "hook install" -TimeoutSeconds 30
        if ($result.Success) { Set-Marker $hookMarker; Write-Success "Hook installed"; return }
    }
    Write-Warning "Hook installation failed - continuing"
    Write-Log "Hook install failed after retries" -Level "WARN"
}

# ----------------------------------------------------------------------------
# Strict-mode enforcement: hard-blocks the first raw source read of a
# session and redirects it to the graph, then writes a mandatory
# `PreToolUse` hook into .claude\settings.json that intercepts file search
# (Glob/Grep) and bash commands so Claude can't bypass the graph by shelling
# out to `grep`/`find` directly. Runs every launch; each step is idempotent
# and marker-gated so repeat launches are a no-op.
# ----------------------------------------------------------------------------
function Install-GraphifyStrictMode {
    $strictMarker = Join-Path $PWD ".graphify_strict_installed"
    if (-not (Test-Path $strictMarker)) {
        Write-Info "Installing Graphify strict mode (blocks raw source reads before the graph)..."
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "install --project --strict" -TimeoutSeconds 30
        if ($result.Success) {
            Set-Marker $strictMarker
            Write-Success "Strict mode installed"
        } else {
            Write-Warning "Strict mode install failed - continuing without the hard block"
            Write-Log "graphify install --project --strict failed: $($result.Output)" -Level "WARN"
        }
    } else {
        Write-Log "Strict mode already installed for this project" -Level "DEBUG"
    }

    # Keeps the block active for this process; strict installs alone are
    # only a marker file on disk, this env var is what Graphify's hook
    # actually checks at runtime before letting a raw read through.
    [Environment]::SetEnvironmentVariable("GRAPHIFY_HOOK_STRICT", "1", "Process")

    $claudeHookMarker = Join-Path $PWD ".graphify_claude_hook_installed"
    if (-not (Test-Path $claudeHookMarker)) {
        Write-Info "Wiring Graphify into Claude Code's PreToolUse hook..."
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "claude install" -TimeoutSeconds 30
        if ($result.Success) {
            Set-Marker $claudeHookMarker
            Write-Success "Claude Code hook installed (.claude\settings.json)"
        } else {
            Write-Warning "graphify claude install failed - PreToolUse hook not written"
            Write-Log "graphify claude install failed: $($result.Output)" -Level "WARN"
        }
    } else {
        Write-Log "Claude Code PreToolUse hook already installed" -Level "DEBUG"
    }
}

# ----------------------------------------------------------------------------
# Ensures every project this launcher touches has the graph-first directive
# in its CLAUDE.md, so strict mode is backed up by an explicit instruction
# even on a machine where the PreToolUse hook install failed or an older
# Graphify build ignores it. Two paths:
#   - No CLAUDE.md yet          -> write a new one with just the directive.
#   - CLAUDE.md already exists  -> leave existing content untouched, just
#                                   append the directive block if it isn't
#                                   already present (checked via a marker
#                                   heading, so repeat launches don't stack
#                                   duplicate copies).
# ----------------------------------------------------------------------------
function Set-ProjectClaudeMdDirective {
    $claudeMdPath = Join-Path $PWD "CLAUDE.md"
    $markerHeading = "# Graphify enforcement"
    $directiveBlock = @"
CRITICAL: You must run ``graphify query`` or read ``graphify-out/GRAPH_REPORT.md`` (or ``.graphify/graph.json`` / ``.graphify/studio/studio.html`` on newer Graphify builds) before any raw file read, Glob, or Grep. This is non-negotiable.

$markerHeading

- Treat ``graphify`` as mandatory for understanding this codebase. ``grep``/``Grep`` and raw file reads are a fallback only, to be used after consulting the graph, never before it.
- Any subagent spawned inside this project must follow the same rule: query the graph first, fall back to grep only if the graph doesn't have the answer.
- At the start of a session: use ``graphify-out/GRAPH_REPORT.md`` (or the current project's ``.graphify/graph.json``) before searching files. Do not use raw grep first.
- Strict-mode enforcement is active for this project (``graphify install --project --strict``, ``GRAPHIFY_HOOK_STRICT=1``, and a ``PreToolUse`` hook installed via ``graphify claude install`` in ``.claude/settings.json``). The first raw source read of a session is hard-blocked and redirected to the graph; file search and bash commands are intercepted by the hook.
"@

    try {
        if (-not (Test-Path $claudeMdPath -PathType Leaf)) {
            $directiveBlock | Out-File -FilePath $claudeMdPath -Encoding UTF8 -Force
            Write-Success "Created CLAUDE.md with the Graphify directive"
            Write-Log "CLAUDE.md created at $claudeMdPath" -Level "DEBUG"
            return
        }

        $existing = Get-Content -Path $claudeMdPath -Raw -Encoding UTF8
        if ($existing -match [regex]::Escape($markerHeading)) {
            Write-Log "CLAUDE.md already has the Graphify directive - leaving as-is" -Level "DEBUG"
            return
        }

        $merged = $existing.TrimEnd() + "`r`n`r`n" + $directiveBlock
        $merged | Out-File -FilePath $claudeMdPath -Encoding UTF8 -Force
        Write-Success "Added the Graphify directive to existing CLAUDE.md"
        Write-Log "CLAUDE.md merged at $claudeMdPath" -Level "DEBUG"
    } catch {
        Write-Warning "Could not write/merge CLAUDE.md - continuing without it"
        Write-Log "CLAUDE.md write failed: $_" -Level "WARN"
    }
}

function Invoke-GraphifyExtract {
    Write-Section "Graph extraction"
    $graphFile = Join-Path (Join-Path $PWD ".graphify") "graph.json"
    # Graphify already tracks what it's seen. On a first run in this project
    # it does a full scan (`graphify .`); once a graph exists, `graphify
    # update` only re-parses files that changed since the last run instead of
    # rebuilding from scratch - much faster on repeat launches.
    $isUpdate = Test-Path $graphFile -PathType Leaf
    $extractArgs = if ($isUpdate) { "update" } else { "." }
    $verb = if ($isUpdate) { "Updating changed files in" } else { "Extracting" }
    Write-Info "$verb project structure (also builds the HTML studio)..."
    Write-Log "Starting graph $extractArgs in: $($PWD.Path)"
    $extractStart = Get-Date
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments $extractArgs -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph"
    $extractTime = (Get-Date) - $extractStart

    # Newer Graphify builds refuse to run on a mixed repo (code + docs/
    # PDFs/images) unless you either point it at an LLM backend for
    # semantic extraction or tell it to skip the non-code files entirely.
    # The exact skip flag isn't consistent across Graphify versions (we've
    # seen --code-only rejected as "unknown option" on some installs), so
    # instead of hardcoding a guess we read graphify's own --help output
    # and pick whatever flag it actually advertises for this.
    if ((-not $result.Success) -and ($result.Output -match "non-code corpus files|--semantic|--backend")) {
        Write-Log "graphify $extractArgs hit the semantic-extraction gate: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
        $skipFlag = Find-GraphifySkipSemanticFlag
        if ($skipFlag) {
            Write-Hint "Project has non-code files (docs/PDFs/images) - retrying with $skipFlag"
            $codeOnlyArgs = "$extractArgs $skipFlag"
            $result = Invoke-ExternalCommand -Command "graphify" -Arguments $codeOnlyArgs -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph (code-only)"
            $extractTime = (Get-Date) - $extractStart
            if ($result.Success) { $extractArgs = $codeOnlyArgs }
        } else {
            Write-Log "No code-only/skip-semantic flag found in 'graphify --help' output" -Level "DEBUG"
        }
    }

    if (-not $result.Success) {
        if ($isUpdate) {
            # Older Graphify builds may not support `update` - fall back to a
            # full rescan rather than failing outright.
            Write-Log "graphify update failed, falling back to full scan: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
            $result = Invoke-ExternalCommand -Command "graphify" -Arguments "." -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph"
            $extractTime = (Get-Date) - $extractStart
            if ((-not $result.Success) -and ($result.Output -match "non-code corpus files|--semantic|--backend")) {
                $skipFlag = Find-GraphifySkipSemanticFlag
                if ($skipFlag) {
                    Write-Log "graphify . also hit the semantic-extraction gate, retrying with $skipFlag" -Level "DEBUG"
                    $result = Invoke-ExternalCommand -Command "graphify" -Arguments ". $skipFlag" -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph (code-only)"
                    $extractTime = (Get-Date) - $extractStart
                }
            }
        }
        if (-not $result.Success) {
            Write-Fail "Graph extraction failed"
            foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
            Write-Warning "Continuing without a graph - Claude Code will still launch normally"
            return $true
        }
    }
    if (-not (Test-Path $graphFile -PathType Leaf)) {
        Write-Fail "Graph file missing: .graphify\graph.json"
        foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
        Write-Warning "Continuing without a graph - Claude Code will still launch normally"
        return $true
    }
    $stats = Get-GraphStatistics -GraphPath $graphFile
    Write-Success "Extracted in $($extractTime.ToString('mm\:ss'))"
    Write-Hint "Nodes $($stats.Nodes)   Edges $($stats.Edges)   Size $($stats.Size)"
    Write-Log "Extraction complete: $($stats.Nodes) nodes, $($stats.Edges) edges"
    return $true
}

# ----------------------------------------------------------------------------
# Graphify's exact flag for "index code, skip docs/PDFs/images that need
# semantic extraction" isn't consistent across versions (--code-only is
# rejected as an unknown option on some installs). Rather than hardcoding
# a guess, read graphify's own --help text and pick whatever it actually
# advertises. Cached per-process since --help output won't change mid-run.
# ----------------------------------------------------------------------------
$script:GraphifySkipFlagChecked = $false
$script:GraphifySkipFlagCached = $null
function Find-GraphifySkipSemanticFlag {
    if ($script:GraphifySkipFlagChecked) { return $script:GraphifySkipFlagCached }
    $script:GraphifySkipFlagChecked = $true
    try {
        $helpResult = Invoke-ExternalCommand -Command "graphify" -Arguments "--help" -TimeoutSeconds 15 -NoLog
        $helpText = $helpResult.Output
        if (-not $helpText) { return $null }
        # Prefer flags that explicitly mention skipping/limiting to code,
        # in rough order of specificity. First match wins.
        $candidates = @(
            '--code-only', '--skip-semantic', '--no-semantic',
            '--ast-only', '--code-mode', '--skip-docs'
        )
        foreach ($candidate in $candidates) {
            if ($helpText -match [regex]::Escape($candidate)) {
                $script:GraphifySkipFlagCached = $candidate
                return $candidate
            }
        }
        # Fall back to a generic scan of the help text for any flag whose
        # name mentions "code" alongside "only"/"skip", in case this
        # version spells it differently than our candidate list.
        $match = [regex]::Match($helpText, '--[a-z][a-z0-9-]*(code[a-z0-9-]*only|skip[a-z0-9-]*semantic|only[a-z0-9-]*code)[a-z0-9-]*')
        if ($match.Success) {
            $script:GraphifySkipFlagCached = $match.Value
            return $match.Value
        }
    } catch {
        Write-Log "Find-GraphifySkipSemanticFlag failed: $_" -Level "DEBUG"
    }
    return $null
}

function Get-GraphStatistics {
    [CmdletBinding()] param([string]$GraphPath)
    $stats = @{ Nodes = 0; Edges = 0; Size = "0 B" }
    try {
        $graph = Get-Content $GraphPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        $graphProps = $graph.PSObject.Properties.Name
        if (($graphProps -contains "nodes") -and $graph.nodes) { $stats.Nodes = @($graph.nodes).Count }
        # Graphify uses the networkx node-link schema, where edges live under
        # "links". Fall back to "edges" for other/older formats.
        if (($graphProps -contains "links") -and $graph.links) { $stats.Edges = @($graph.links).Count }
        elseif (($graphProps -contains "edges") -and $graph.edges) { $stats.Edges = @($graph.edges).Count }
    } catch { Write-Log "Could not parse graph stats: $_" -Level "DEBUG" }
    try {
        $bytes = (Get-Item $GraphPath -ErrorAction Stop).Length
        if ($bytes -gt 1MB) { $stats.Size = "$([math]::Round($bytes / 1MB, 1)) MB" }
        elseif ($bytes -gt 1KB) { $stats.Size = "$([math]::Round($bytes / 1KB, 1)) KB" }
        else { $stats.Size = "$bytes B" }
    } catch { Write-Log "Could not get graph file size" -Level "DEBUG" }
    return $stats
}

function Show-GraphResult {
    Write-Section "Graph ready"
    $studioFile = Join-Path (Join-Path $PWD ".graphify") "studio\studio.html"
    if (-not (Test-Path $studioFile -PathType Leaf)) {
        Write-Warning "Studio HTML not found at .graphify\studio\studio.html - skipping preview"
        return
    }
    Write-Success "Interactive map generated"
    Write-Hint ("file:///" + $studioFile.Replace('\', '/'))
    if (Read-YesNo "Open the graph now?" $false) { Start-Process $studioFile }
}

# ============================================================================
# AUTOSKILLS
#   npx autoskills detects the project's tech stack and installs matching
#   Claude Code skills from the skills.sh registry. Runs every launch (it's
#   fast and idempotent - already-installed skills are just left alone).
#   `-y` on both npx and autoskills itself skips every interactive prompt:
#   npx's "ok to install this package?" and autoskills' own skill-selection
#   confirmation.
# ============================================================================

function Install-AutoSkillsCli {
    if (-not (Test-CommandAvailable "npm" -UseCache)) { return $false }
    if (Test-CommandAvailable "autoskills" -UseCache) { return $true }
    Write-Info "Installing autoskills globally (npm install -g autoskills)..."
    $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g autoskills" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Installing autoskills"
    if ($result.Success) {
        Sync-ProcessPathFromRegistry
        if (Test-CommandAvailable "autoskills") { Write-Success "autoskills installed"; return $true }
    }
    # Not fatal - `npx autoskills` below will fetch it on demand anyway.
    Write-Log "Global autoskills install did not confirm success: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
    return $false
}

function Invoke-AutoSkills {
    Write-Section "AutoSkills"
    if (-not (Test-CommandAvailable "npm" -UseCache)) {
        Write-Info "npm not available - skipping autoskills"
        return
    }
    $null = Install-AutoSkillsCli
    Write-Info "Detecting stack and installing matching AI skills..."
    $result = Invoke-ExternalCommand -Command "npx" -Arguments "-y autoskills -y -a claude-code" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Running autoskills"
    if ($result.Success) {
        Write-Success "autoskills complete"
    } else {
        Write-Warning "autoskills did not complete cleanly"
        Write-Log "autoskills output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    }
}

# ============================================================================
# CLAUDE LAUNCH
# ============================================================================

function Start-ClaudeSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ClaudePath,
        [switch]$Resume
    )
    Write-Section "Launch Claude"
    $script:ForcedModelAlias = $null
    Set-OmniRouteLaunchEnvironment

    $claudeArgs = @()
    if ($Resume) {
        $claudeArgs += "--continue"
        Write-Info "Same workspace as before - resuming the previous session"
    } else {
        Write-Info "Starting a new session"
    }
    if ($script:ForcedModelAlias) { $claudeArgs += @("--model", $script:ForcedModelAlias) }

    Write-Log "Launching Claude: $ClaudePath $($claudeArgs -join ' ') in $($PWD.Path)"
    try {
        if ($claudeArgs.Count -gt 0) { & $ClaudePath @claudeArgs } else { & $ClaudePath }

        # --continue fails fast (non-zero exit) when there's no prior session
        # to resume - e.g. history was cleared, or this is actually the first
        # time despite being in our ProjectHistory. Fall back to a fresh
        # session automatically instead of leaving the user stuck.
        if ($Resume -and $LASTEXITCODE -ne 0) {
            Write-Warning "No previous conversation found to continue - starting a new session instead"
            Write-Log "Claude --continue failed (exit $LASTEXITCODE) - retrying without --continue" -Level "WARN"
            if ($script:ForcedModelAlias) { & $ClaudePath --model $script:ForcedModelAlias } else { & $ClaudePath }
        }

        Write-Success "Claude session ended"
    } catch {
        Write-Warning "Claude exited with error: $_"
        Write-Log "Claude exit error: $_" -Level "ERROR"
    }
}

function Show-SessionSummary {
    param(
        [string]$ProjectPath,
        [bool]$Resumed,
        [bool]$OmniRouteActive
    )
    Write-Section "Session summary"
    Write-Hint ("Project     " + (Split-Path $ProjectPath -Leaf))
    Write-Hint ("Session     " + $(if ($Resumed) { "resumed" } else { "new" }))
    Write-Hint ("OmniRoute   " + $(if ($OmniRouteActive) { "active (compression applied automatically; pick a model with /model in Claude)" } else { "not used" }))
    Write-Hint ("Elapsed     " + (Get-Elapsed))
}

# ============================================================================
# MAIN ENTRY POINT
# ============================================================================

function Main {
    $host.UI.RawUI.WindowTitle = "LLM-TokenOptimizer v$($script:APP_VERSION)"
    Write-Title
    Initialize-Logging
    Initialize-Configuration
    Initialize-Mutex
    Register-CleanupHandlers
    Write-Log "=== LAUNCHER STARTED === v$($script:APP_VERSION) | User: $env:USERNAME | PID: $PID"

    try {
        Write-Section "Environment"
        Test-WindowsVersion
        Add-StandardPaths

        $depSummary = Get-DependencySummary
        Test-RequiredDependencies -Missing $depSummary.Missing

        if (-not (Test-CommandAvailable "graphify" -UseCache)) {
            Write-Section "Graphify installation"
            if (-not (Install-Graphify)) { Stop-Script -Code 104 -Reason "Cannot continue without Graphify" }
        }
        if (-not (Test-GraphifyVersion)) { Write-Warning "Could not verify Graphify version (continuing)" }

        # Asked fresh every launch - Git, Node, Python (via winget), Graphify
        # (via pip), Claude Code and OmniRoute (via npm). Best-effort, non-fatal.
        Invoke-UpdateCheckIfRequested

        $claudePath = Find-ClaudeExecutable
        Test-ClaudeExecutable -Path $claudePath

        $omniRouteActive = Initialize-OmniRoute

        # Project selection loop
        $projectPath = $null
        while ($true) {
            $projectPath = Read-ProjectPath
            if ($null -eq $projectPath) {
                if (Read-YesNo "Exit launcher?" $false) { Write-Info "Exiting by request"; exit 0 }
                continue
            }
            if (Test-ProjectDirectory -Path $projectPath) { break }
        }
        $isReturningProject = Test-ProjectAlreadyKnown -Path $projectPath
        Add-ProjectToHistory -Path $projectPath
        Set-Location $projectPath
        Write-Log "Working directory: $projectPath | Returning project: $isReturningProject"

        Write-Section "Graphify setup"
        Install-GraphifyPlatform
        Install-GraphifyHook
        Install-GraphifyStrictMode
        Set-ProjectClaudeMdDirective

        if (-not (Invoke-GraphifyExtract)) { Stop-Script -Code 106 -Reason "Extraction failed - aborting" }
        Show-GraphResult

        Invoke-AutoSkills

        Write-Host ""
        if ((Read-Host "  Press Enter to launch Claude, or X to exit") -match "^[Xx]") {
            Write-Info "Exiting without launching Claude"; exit 0
        }
        Start-ClaudeSession -ClaudePath $claudePath -Resume:$isReturningProject

        Show-SessionSummary -ProjectPath $projectPath -Resumed $isReturningProject -OmniRouteActive $omniRouteActive

        Write-Section "Done"
        Write-Success "Completed in $(Get-Elapsed)"
        if ($host.Name -ne 'ConsoleHost') { Read-Host "  Press Enter to close this window" }
        exit 0
    } catch {
        Write-Host ""
        Write-Fail "Unexpected error: $_"
        Write-Log "Fatal error: $_" -Level "ERROR"
        Write-Log "Stack: $($_.ScriptStackTrace)" -Level "ERROR"
        Write-Hint $_.ScriptStackTrace
        Stop-Script -Code 99
    } finally {
        Invoke-Cleanup
    }
}

Main
