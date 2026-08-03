#Requires -Version 5.1
<#
.SYNOPSIS
    LLM-TokenOptimizer - Production Quality v4.0
.DESCRIPTION
    Self-bootstrapping launcher that verifies the environment, installs
    dependencies, generates Graphify graphs, and launches Claude Code reliably
    on any Windows 10/11 PC. References itself as LLM-TokenOptimizer throughout.

    v4.0 - three changes:

    1) MULTI-WINDOW. The launcher no longer runs one project at a time behind a
       global single-instance mutex. You now pick a MASTER FOLDER once (the
       parent directory that holds your projects); the launcher lists the
       subfolders inside it and you choose which ones to open. Each chosen
       subfolder gets its own independent console window running its own
       Graphify extraction and its own Claude Code session, and they all run
       at the same time. The launcher window stays open as a control panel so
       you can open more project windows whenever you want. The instance lock
       is now per-project (two windows on the SAME folder is still blocked -
       they would fight over the same .graphify output), and config.json is
       written with a cross-process lock + merge so concurrent windows don't
       clobber each other's project history.

    2) SETUP IS REMEMBERED. Previous versions re-ran the OmniRoute onboarding
       (API key prompt, "open the dashboard and connect Claude Code") on
       basically every launch, because the only connectivity probe was
       `omniroute providers list --json` and any failure of that command read
       as "not connected". Now the saved API key is validated against
       OmniRoute's own /v1/models endpoint, a rejected key (401/403) is the
       ONLY thing that triggers a re-prompt, an unreachable server never
       discards a good key, and once the Claude provider has been seen working
       the result is recorded in config.json and never asked about again.
       Use -ReconfigureOmniRoute to deliberately redo that setup.

    3) 1M-CONTEXT MODELS, DISTINCT FROM THE DEFAULTS. Claude Opus 5 and Claude
       Sonnet 5 both carry a 1M-token context window as BOTH the default and
       the maximum - per Anthropic's model docs there is no smaller context
       variant and no separate "1m" model ID for either one, so the old
       `claude-sonnet-5(?!.*1m)` exclusion was filtering for something that
       does not exist. Model resolution now reads OmniRoute's live /v1/models
       catalog, prefers the `cc/` (Claude Code OAuth) provider prefix that
       OmniRoute documents for Claude-family models, and accepts an entry only
       when the catalog agrees it carries a >=1M context window (or is a
       -5 model, which is 1M by definition). Claude Code's auto-compaction
       window and output cap are raised to match, otherwise the client
       compacts at ~190k and the 1M window goes unused. The two entries are
       pinned to their resolved OmniRoute catalog IDs and labelled
       "Opus 5 - 1M - OmniRoute" / "Sonnet 5 - 1M - OmniRoute" so they are
       visibly distinct from Claude Code's built-in defaults, and
       availableModels is restricted to exactly those two.

    v3.1: fixed the Graphify output path for Graphify 0.17.1+, which now
    writes to a hidden .graphify\graph.json (not graphify-out\graph.json) and
    auto-generates the HTML studio during `extract` itself.

    v3.0: pxpipe removed entirely; Claude Code routes through OmniRoute, which
    applies its own compression pipeline (RTK -> Caveman -> LLMLingua -> Lite).
.NOTES
    Version: 4.0.0
    Exit Codes:
        0   - Success
        99  - Unexpected error
        100 - This project is already open in another window
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
    # as its default. Session-scoped only.
    [ValidateSet('sonnet', 'opus')]
    [string]$Model,

    # ---- v4.0 multi-window parameters -------------------------------------
    # The parent directory holding your projects. Supply it to skip the
    # master-folder prompt entirely.
    [string]$MasterFolder,
    # Child mode: run directly against this one project folder and launch
    # Claude there. This is what the launcher passes to each window it spawns;
    # you can also use it by hand to open a single project without the picker.
    [string]$ProjectPath,
    # Internal marker set on spawned windows so they skip the shared,
    # already-completed setup work (winget dependency installs, update
    # prompts, starting OmniRoute) that the launcher window already did.
    [switch]$ChildWindow,
    # Give this project its own CLAUDE_CONFIG_DIR (separate settings,
    # credentials, history and cache). Off by default so windows keep sharing
    # your normal ~/.claude setup - MCP servers, custom settings and all.
    [switch]$IsolateClaudeConfig,
    # Deliberately redo the OmniRoute onboarding: forget the saved API key and
    # the "provider already connected" flag, then ask again.
    [switch]$ReconfigureOmniRoute
)

# ============================================================================
# STRICT MODE AND GLOBAL STATE
# ============================================================================
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Application constants
$script:APP_NAME = "LLM-TokenOptimizer"
$script:APP_VERSION = "4.0.0"
$script:MAX_HISTORY = 20
$script:MAX_LOG_FILES = 10
$script:OMNIROUTE_URL = "http://localhost:20128"

# Claude Opus 5 and Claude Sonnet 5 both have a 1M-token context window as
# both the default AND the maximum, with no smaller context variant. These
# numbers exist so we can (a) sanity-check a catalog entry actually offers the
# full window and (b) stop Claude Code auto-compacting at its normal ~190k
# threshold, which would waste most of the window.
$script:CONTEXT_1M = 1000000
$script:MIN_1M_CONTEXT = 900000       # tolerance for catalogs reporting 1048576, 999424, etc.
$script:AUTO_COMPACT_WINDOW = 900000  # compact only near the top of the 1M window
$script:MAX_OUTPUT_TOKENS = 128000    # both models support 128k max output

# Paths (computed once, never hardcoded)
$script:AppDataDir = Join-Path $env:LOCALAPPDATA $script:APP_NAME
$script:ConfigPath = Join-Path $script:AppDataDir "config.json"
$script:LogDir = Join-Path $script:AppDataDir "logs"
$script:ProfileRoot = Join-Path $script:AppDataDir "claude-profiles"
$script:GlobalGateFile = Join-Path $env:USERPROFILE ".graphify_platform_claude_done"

# Mutable global state (minimized)
$script:Config = $null
$script:InstanceMutex = $null
$script:StartTime = Get-Date
$script:DependencyCache = @{}
$script:CleanupRegistered = $false
# Session-only "-Model sonnet|opus" override; set inside Set-OmniRouteLaunchEnvironment.
$script:ForcedModelAlias = $null
# Whether this window actually ended up routed through OmniRoute. Kept on the
# script scope because Start-ClaudeSession can't return it - the interactive
# `claude` process it runs owns the pipeline.
$script:OmniRouteRouted = $false
# True when this process is one of the per-project windows the launcher spawned
# (or was started by hand with -ProjectPath). Child windows skip the shared
# environment bootstrap the launcher window already completed.
$script:IsChild = [bool]($ChildWindow -or $ProjectPath)
# Resolved once per process so respawning works no matter how we were started.
$script:SelfPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }

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
    param([string]$Subtitle = "")
    $width = [Math]::Min(64, [Math]::Max(40, (Get-SafeConsoleWidth) - 4))
    $bar = ('=' * $width)
    Write-Host ""
    Write-Host "  $bar" -ForegroundColor DarkCyan
    Write-Host "   LLM-TokenOptimizer " -ForegroundColor Cyan -NoNewline
    Write-Host "v$($script:APP_VERSION)" -ForegroundColor DarkGray
    if ($Subtitle) {
        Write-Host "   $Subtitle" -ForegroundColor DarkGray
    } else {
        Write-Host "   Self-bootstrapping environment for Claude Code + OmniRoute" -ForegroundColor DarkGray
    }
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

function Get-PathSlug {
    # Stable, filesystem-safe, collision-resistant identifier for a directory.
    # Used for per-project mutex names and per-project CLAUDE_CONFIG_DIR names.
    # Case-insensitive because Windows paths are.
    param([Parameter(Mandatory)][string]$Path)
    $normalized = $Path.TrimEnd('\', '/').ToLowerInvariant()
    $leaf = ($normalized -split '[\\/]' | Where-Object { $_ } | Select-Object -Last 1)
    if (-not $leaf) { $leaf = "root" }
    $leaf = ($leaf -replace '[^a-z0-9]', '-').Trim('-')
    if (-not $leaf) { $leaf = "project" }
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
        $hash = ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 8).ToLowerInvariant()
    } finally { $md5.Dispose() }
    if ($leaf.Length -gt 24) { $leaf = $leaf.Substring(0, 24) }
    return "$leaf-$hash"
}

# ============================================================================
# LOGGING SYSTEM
# ============================================================================

function Initialize-Logging {
    try {
        if (-not (Test-Path $script:LogDir)) {
            New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
        }
        # Only the launcher window prunes old logs. Child windows starting up
        # concurrently would otherwise race each other deleting the same files.
        if (-not $script:IsChild) {
            Get-ChildItem -Path $script:LogDir -Filter "launcher_*.log" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -Skip $script:MAX_LOG_FILES |
                ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
        }
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
    # PID is in every line now: with several windows appending to the same
    # daily log, interleaved entries are otherwise impossible to untangle.
    $logEntry = "[$timestamp][$Level][pid $PID] $Message"
    $logFile = Join-Path $script:LogDir "launcher_$((Get-Date).ToString('yyyyMMdd')).log"
    # Append can transiently fail when two windows write at the same instant;
    # a couple of quick retries makes concurrent logging effectively reliable
    # without ever being able to block the launcher.
    foreach ($attempt in 1..3) {
        try {
            $logEntry | Out-File -FilePath $logFile -Append -Encoding UTF8 -ErrorAction Stop
            break
        } catch { Start-Sleep -Milliseconds (25 * $attempt) }
    }
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
#   Shared by every window. Because several windows can now be running at
#   once, every write goes through a named cross-process mutex and re-reads
#   the file first, so one window saving its project history never discards
#   what another window saved a moment earlier.
# ============================================================================

$script:CONFIG_MUTEX_NAME = "Global\LLMTokenOptimizer_v4_Config"

function Get-DefaultConfiguration {
    return [PSCustomObject]@{
        MasterFolder = ""
        MasterFolderHistory = [array]@()
        LastProject = ""
        ProjectHistory = [array]@()
        UseOmniRoute = $null
        OmniRouteApiKeyEnc = ""
        # Set once the saved key has actually been accepted by OmniRoute, so
        # a working key is never re-prompted for on later launches.
        OmniRouteKeyVerifiedUtc = ""
        # Set once a Claude-family model has been seen in OmniRoute's catalog
        # (i.e. the Claude Code provider really is connected). This is the flag
        # that stops the launcher sending you back to the OmniRoute dashboard
        # every single time it starts.
        OmniRouteProviderVerifiedUtc = ""
        # Set if you tell the launcher to stop asking about the provider.
        OmniRouteProviderPromptSuppressed = $false
        ClaudePath = ""
        AutoUpdateGraphify = $false
        FirstRunComplete = $false
        LastGraphifyVersion = ""
    }
}

function Invoke-WithConfigLock {
    # Runs a scriptblock while holding the cross-process config mutex. Falls
    # back to running it unguarded if the mutex can't be had within the
    # timeout - a slightly racy save is strictly better than a hung launcher.
    param([Parameter(Mandatory)][scriptblock]$Body, [int]$TimeoutMs = 5000)
    $mutex = $null
    $held = $false
    try {
        $mutex = New-Object System.Threading.Mutex($false, $script:CONFIG_MUTEX_NAME)
        try { $held = $mutex.WaitOne($TimeoutMs, $false) }
        catch [System.Threading.AbandonedMutexException] {
            # Another window died holding the lock. The mutex is now ours.
            $held = $true
            Write-Log "Config mutex was abandoned by a dead process - reclaimed" -Level "DEBUG"
        }
        if (-not $held) { Write-Log "Config mutex timeout - proceeding unguarded" -Level "WARN" }
        return (& $Body)
    } catch {
        Write-Log "Config lock error: $_" -Level "WARN"
        return (& $Body)
    } finally {
        if ($mutex) {
            if ($held) { try { $mutex.ReleaseMutex() } catch {} }
            try { $mutex.Dispose() } catch {}
        }
    }
}

function ConvertTo-Configuration {
    # Reads config.json from disk and back-fills any keys added since it was
    # written, so upgrading the script never loses or misreads an old config.
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try {
        $raw = Get-Content $Path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        $saved = $raw | ConvertFrom-Json
        foreach ($prop in (Get-DefaultConfiguration).PSObject.Properties) {
            if (-not ($saved.PSObject.Properties.Name -contains $prop.Name)) {
                $saved | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value
            }
        }
        return $saved
    } catch {
        Write-Log "Failed to parse config: $_" -Level "WARN"
        return $null
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

    # Only the launcher may reset - a spawned child doing it would wipe the
    # config out from under its siblings mid-run.
    if ($ResetConfig -and -not $script:IsChild -and (Test-Path $script:ConfigPath)) {
        Invoke-WithConfigLock { Remove-Item $script:ConfigPath -Force -ErrorAction SilentlyContinue }
        Write-Log "Configuration reset by user request"
    }

    $loaded = Invoke-WithConfigLock { ConvertTo-Configuration -Path $script:ConfigPath }
    if ($loaded) {
        $script:Config = $loaded
        Write-Log "Configuration loaded from: $($script:ConfigPath)"
    } else {
        $script:Config = Get-DefaultConfiguration
        Write-Log "No usable configuration found, using defaults"
    }

    if ($ReconfigureOmniRoute) {
        Write-Info "-ReconfigureOmniRoute: forgetting the saved OmniRoute key and setup state"
        $script:Config.OmniRouteApiKeyEnc = ""
        $script:Config.OmniRouteKeyVerifiedUtc = ""
        $script:Config.OmniRouteProviderVerifiedUtc = ""
        $script:Config.OmniRouteProviderPromptSuppressed = $false
        Save-Configuration
    }
}

function Merge-ConfigurationLists {
    # Union of two ordered lists, ours first, de-duplicated case-insensitively,
    # capped at MAX_HISTORY. This is what keeps two windows from erasing each
    # other's project history.
    param([array]$Ours, [array]$Theirs)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $merged = [System.Collections.ArrayList]::new()
    foreach ($item in (@($Ours) + @($Theirs))) {
        if (-not $item) { continue }
        if ($seen.Add([string]$item)) { $null = $merged.Add([string]$item) }
        if ($merged.Count -ge $script:MAX_HISTORY) { break }
    }
    return [array]$merged
}

function Save-Configuration {
    if (-not $script:Config) { return }
    Invoke-WithConfigLock {
        try {
            $onDisk = ConvertTo-Configuration -Path $script:ConfigPath
            $toWrite = $script:Config
            if ($onDisk) {
                # Lists merge; scalars are last-writer-wins EXCEPT that we never
                # overwrite a value another window has set with an empty one.
                $toWrite.ProjectHistory = Merge-ConfigurationLists -Ours @($script:Config.ProjectHistory) -Theirs @($onDisk.ProjectHistory)
                $toWrite.MasterFolderHistory = Merge-ConfigurationLists -Ours @($script:Config.MasterFolderHistory) -Theirs @($onDisk.MasterFolderHistory)
                foreach ($name in @(
                    'OmniRouteApiKeyEnc', 'OmniRouteKeyVerifiedUtc', 'OmniRouteProviderVerifiedUtc',
                    'ClaudePath', 'LastGraphifyVersion', 'MasterFolder', 'LastProject')) {
                    $ours = $toWrite.$name
                    $theirs = $onDisk.$name
                    if ([string]::IsNullOrWhiteSpace([string]$ours) -and -not [string]::IsNullOrWhiteSpace([string]$theirs)) {
                        $toWrite.$name = $theirs
                    }
                }
                # A suppression/verification flag set anywhere sticks everywhere.
                if ($onDisk.OmniRouteProviderPromptSuppressed) { $toWrite.OmniRouteProviderPromptSuppressed = $true }
                if ($null -eq $toWrite.UseOmniRoute -and $null -ne $onDisk.UseOmniRoute) { $toWrite.UseOmniRoute = $onDisk.UseOmniRoute }
            }
            # Write to a temp file and swap it in, so a window killed mid-write
            # can never leave a truncated config.json behind.
            $tmp = "$($script:ConfigPath).$PID.tmp"
            $toWrite | ConvertTo-Json -Depth 10 | Out-File -FilePath $tmp -Encoding UTF8 -Force
            Move-Item -Path $tmp -Destination $script:ConfigPath -Force
            Write-Log "Configuration saved" -Level "DEBUG"
        } catch {
            Write-Log "Failed to save configuration: $_" -Level "ERROR"
        }
    }
}

# ============================================================================
# PER-PROJECT INSTANCE LOCK
#   v3 held one global mutex, so a second window exited immediately with code
#   100. That is now scoped to the project folder instead: any number of
#   windows may run side by side as long as they are working on DIFFERENT
#   folders. Two windows on the SAME folder is still refused, because they
#   would both be writing .graphify\graph.json and the project's
#   .claude\settings.json at the same time.
# ============================================================================

function Initialize-InstanceLock {
    [CmdletBinding()]
    param([string]$ProjectDirectory)
    if (-not $ProjectDirectory) { return $true }   # launcher window takes no lock
    try {
        $slug = Get-PathSlug -Path $ProjectDirectory
        $mutexName = "Global\LLMTokenOptimizer_v4_Project_$slug"
        $script:InstanceMutex = New-Object System.Threading.Mutex($false, $mutexName)
        $acquired = $false
        try { $acquired = $script:InstanceMutex.WaitOne(0, $false) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            Write-Warning "This project is already open in another LLM-TokenOptimizer window."
            Write-Hint "  $ProjectDirectory"
            Write-Hint "Two windows on the same folder would fight over .graphify and .claude\settings.json."
            Write-Hint "Switch to the window that already has it open, or pick a different project."
            try { $script:InstanceMutex.Dispose() } catch {}
            $script:InstanceMutex = $null
            return $false
        }
        Write-Log "Project lock acquired: $mutexName"
        return $true
    } catch {
        Write-Log "Project lock creation failed (continuing): $_" -Level "WARN"
        return $true
    }
}

function Release-InstanceLock {
    if ($null -ne $script:InstanceMutex) {
        try {
            $script:InstanceMutex.ReleaseMutex()
            $script:InstanceMutex.Dispose()
            Write-Log "Project lock released"
        } catch {
            Write-Log "Project lock release error: $_" -Level "WARN"
        }
        $script:InstanceMutex = $null
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
    # OmniRoute is a standalone app shared by every window - we never stop it
    # on exit, and a closing project window must not take its siblings' router
    # down with it.
    Write-Log "Cleanup initiated"
    Release-InstanceLock
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
#   Installer package). When it's missing (older Win10, winget disabled by
#   policy, etc.) we degrade gracefully to the old "tell the user where to get
#   it" behavior instead of failing.
#
#   Only the launcher window runs any of this. Several project windows racing
#   each other through winget/npm installs would be slow at best and would
#   corrupt a half-finished install at worst.
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
    # Invoke-UpdateCheckIfRequested's prompt, and only in the launcher window.
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
    param([switch]$Quiet)
    if (-not $Quiet) { Write-Section "Dependencies" }
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
            if (-not $Quiet) {
                try {
                    $verResult = Invoke-ExternalCommand -Command $dep.Command -Arguments "--version" -TimeoutSeconds 5 -Silent
                    if ($verResult.Success) { $version = ($verResult.Output.Trim() -replace "`r`n", " " -replace "`n", " ") }
                } catch {}
                Write-Success ("{0} {1}" -f $name.PadRight(9), $version)
            }
        } else {
            $null = $missing.Add(@{ Name = $name; Info = $dep })
            if (-not $Quiet) { Write-Fail ("{0} not found" -f $name.PadRight(9)) }
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
    param([switch]$Quiet)
    if (-not $Quiet) { Write-Section "Claude" }
    if ($script:Config.ClaudePath -and (Test-Path $script:Config.ClaudePath -PathType Leaf)) {
        if (-not $Quiet) { Write-Success "Found (saved): $($script:Config.ClaudePath)" }
        return $script:Config.ClaudePath
    }
    if (Test-CommandAvailable "claude" -UseCache) {
        $path = (Get-Command "claude").Source
        if (-not $Quiet) { Write-Success "Found on PATH: $path" }
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
        if (-not $Quiet) { Write-Success "Found: $found" }
        $script:Config.ClaudePath = $found; Save-Configuration
        return $found
    }
    $regResult = Find-ClaudeInRegistry
    if ($regResult) {
        if (-not $Quiet) { Write-Success "Found (registry): $regResult" }
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
#   OmniRoute is a standalone local gateway (http://localhost:20128) that
#   Claude Code is pointed at via environment variables. All compression
#   (RTK -> Caveman -> LLMLingua -> Lite) happens inside OmniRoute itself.
#
#   Per OmniRoute's own Claude Code guide:
#     ANTHROPIC_BASE_URL     gateway root, NO /v1 suffix
#     ANTHROPIC_AUTH_TOKEN   sent as Authorization: Bearer ...; wins over
#                            ANTHROPIC_API_KEY when both are set
#     CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1
#                            makes the native /model picker list claude*/
#                            anthropic*-prefixed IDs from /v1/models
#     ANTHROPIC_DEFAULT_{OPUS,SONNET,HAIKU}_MODEL
#                            map Claude Code's capability tiers onto specific
#                            gateway model IDs (any ID, prefixed or not)
#   Env vars are read once at Claude Code startup, which is why everything
#   below happens before the `claude` process is spawned.
# ============================================================================

function Test-OmniRouteRunning {
    try {
        $null = Invoke-RestMethod -Uri "$script:OMNIROUTE_URL/v1/models" -Method Get -TimeoutSec 3 -ErrorAction Stop
        return $true
    } catch {
        # A 401/403 still proves the server is up and listening - it just
        # wanted credentials. Only a connection failure means "not running".
        $status = Get-HttpStatusCode $_
        if ($status -in @(401, 403)) { return $true }
        return $false
    }
}

function Get-HttpStatusCode {
    # Pulls the numeric HTTP status out of a terminating web error, across
    # both the WebException (PS 5.1) and HttpResponseException (PS 7) shapes.
    param([Parameter(Mandatory)]$ErrorRecord)
    try {
        $resp = $ErrorRecord.Exception.Response
        if ($resp) {
            if ($resp.PSObject.Properties.Name -contains 'StatusCode') {
                return [int]$resp.StatusCode
            }
        }
    } catch {}
    try {
        if ($ErrorRecord.PSObject.Properties.Name -contains 'Exception' -and
            $ErrorRecord.Exception.PSObject.Properties.Name -contains 'StatusCode') {
            return [int]$ErrorRecord.Exception.StatusCode
        }
    } catch {}
    return 0
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
    }

    Write-Info "OmniRoute not detected - attempting to start it..."
    $started = $false

    if (Test-CommandAvailable "omniroute" -UseCache) {
        try {
            # Launch in its own titled, minimized console - a separate window
            # from both this launcher and every Claude Code window, so nothing
            # ever shares a console with an interactive Claude session.
            # Minimized (not Hidden): OmniRoute is a long-running server the
            # user may want to glance at or Ctrl+C, so keep it reachable in
            # the taskbar. One server serves every project window.
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
        return (Test-OmniRouteRunning)
    }

    Write-Success "OmniRoute launching in its own window - continuing without waiting"
    Write-Hint "It usually finishes booting in 10-20s; the rest of setup runs in parallel with that."
    return $true
}

function Wait-OmniRouteReady {
    param([int]$MaxWaitSeconds = 25)
    if (Test-OmniRouteRunning) { return $true }
    Write-Info "Waiting for OmniRoute to finish booting..."
    for ($waited = 0; $waited -lt $MaxWaitSeconds; $waited++) {
        Start-Sleep -Seconds 1
        if (Test-OmniRouteRunning) { return $true }
    }
    return $false
}

function Set-OmniRouteDisabled {
    $script:Config.UseOmniRoute = $false
    Save-Configuration
    return $false
}

# ----------------------------------------------------------------------------
# API KEY STORAGE AND VALIDATION
#
# The v3 behaviour that sent you back through onboarding on every launch had
# two causes, both fixed here:
#   1. The only "is this set up?" probe was `omniroute providers list --json`.
#      If that subcommand was missing, renamed, slow, or printed anything
#      unparseable, the answer read as "not connected" and the dashboard was
#      opened again.
#   2. Nothing was ever recorded once setup HAD succeeded, so there was no
#      memory to consult on the next run.
# Now: a saved key is trusted unless OmniRoute actively rejects it (401/403),
# an unreachable server never discards it, and both "key works" and "Claude
# provider is connected" are written to config.json the first time they're
# observed and short-circuit every later launch.
# ----------------------------------------------------------------------------

function Protect-OmniRouteApiKey {
    param([Parameter(Mandatory)][string]$PlainKey)
    $secure = ConvertTo-SecureString -String $PlainKey -AsPlainText -Force
    return ($secure | ConvertFrom-SecureString)
}

function Get-OmniRouteApiKey {
    # Environment beats config: `omniroute launch` and CI setups export this,
    # and honouring it means those callers are never asked for a key at all.
    if ($env:OMNIROUTE_API_KEY) { return $env:OMNIROUTE_API_KEY }
    if (-not $script:Config.OmniRouteApiKeyEnc) { return $null }
    try {
        $secure = $script:Config.OmniRouteApiKeyEnc | ConvertTo-SecureString
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
        finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    } catch {
        # DPAPI is account-bound: this fires when config.json was copied from
        # another Windows account or machine, not when the key is wrong.
        Write-Log "Failed to decrypt OmniRoute API key (different Windows account?): $_" -Level "WARN"
        return $null
    }
}

function Read-OmniRouteApiKey {
    param([string]$Prompt = "OmniRoute API key")
    Write-Hint "Grab your key from the OmniRoute dashboard (Settings -> API Keys)."
    $secure = Read-Host "  $Prompt" -AsSecureString
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ([string]::IsNullOrWhiteSpace($plain)) { return $null }
    return $plain.Trim()
}

function Save-OmniRouteApiKey {
    param([Parameter(Mandatory)][string]$PlainKey, [switch]$Verified)
    $script:Config.OmniRouteApiKeyEnc = Protect-OmniRouteApiKey -PlainKey $PlainKey
    if ($Verified) { $script:Config.OmniRouteKeyVerifiedUtc = (Get-Date).ToUniversalTime().ToString('o') }
    Save-Configuration
}

function Get-ModelContextLength {
    # Catalogs disagree on where the context window lives. Check every spelling
    # we've seen before concluding "unknown" (0), which callers treat as
    # "can't confirm 1M" rather than "not 1M".
    param($ModelEntry)
    $candidates = @(
        'context_length', 'context_window', 'context_size', 'max_context_tokens',
        'max_context_length', 'max_input_tokens', 'contextLength', 'contextWindow'
    )
    foreach ($name in $candidates) {
        try {
            if ($ModelEntry.PSObject.Properties.Name -contains $name) {
                $value = $ModelEntry.$name
                if ($value -and ([int64]$value) -gt 0) { return [int64]$value }
            }
        } catch {}
    }
    # OpenRouter-style nesting, which some OmniRoute providers mirror.
    foreach ($container in @('top_provider', 'limits', 'capabilities', 'architecture')) {
        try {
            if ($ModelEntry.PSObject.Properties.Name -contains $container -and $ModelEntry.$container) {
                $nested = Get-ModelContextLength -ModelEntry $ModelEntry.$container
                if ($nested -gt 0) { return $nested }
            }
        } catch {}
    }
    return [int64]0
}

function Get-OmniRouteCatalog {
    # Single source of truth for "what can OmniRoute actually serve right now".
    # Returns Reachable / Authorized so callers can tell a wrong key apart from
    # a server that simply isn't up yet - the distinction v3 collapsed, which
    # is what made it throw away good keys.
    [CmdletBinding()]
    param([string]$ApiKey)
    $result = @{ Reachable = $false; Authorized = $false; Models = @(); Error = "" }
    $headers = @{}
    if ($ApiKey) { $headers["Authorization"] = "Bearer $ApiKey" }
    try {
        $resp = Invoke-RestMethod -Uri "$script:OMNIROUTE_URL/v1/models" -Headers $headers -Method Get -TimeoutSec 10 -ErrorAction Stop
        $result.Reachable = $true
        $result.Authorized = $true
        $entries = @()
        foreach ($container in @('data', 'models')) {
            try {
                if ($resp -and ($resp.PSObject.Properties.Name -contains $container) -and $resp.$container) {
                    $entries += @($resp.$container)
                }
            } catch {}
        }
        if ($entries.Count -eq 0 -and $resp -is [System.Array]) { $entries = @($resp) }
        $models = [System.Collections.ArrayList]::new()
        foreach ($entry in $entries) {
            $id = $null
            foreach ($idProp in @('id', 'name', 'model')) {
                try {
                    if ($entry.PSObject.Properties.Name -contains $idProp -and $entry.$idProp) { $id = [string]$entry.$idProp; break }
                } catch {}
            }
            if (-not $id) { continue }
            $null = $models.Add([PSCustomObject]@{
                Id = $id
                Context = (Get-ModelContextLength -ModelEntry $entry)
            })
        }
        $result.Models = @($models)
    } catch {
        $status = Get-HttpStatusCode $_
        $result.Error = $_.Exception.Message
        if ($status -in @(401, 403)) {
            # Server answered - it just refused these credentials.
            $result.Reachable = $true
            $result.Authorized = $false
        } elseif ($status -gt 0) {
            # Some other HTTP error: server is up, catalog call misbehaved.
            $result.Reachable = $true
            $result.Authorized = $true
        }
        Write-Log "Catalog fetch failed (status $status): $(Get-Truncated $result.Error 200)" -Level "DEBUG"
    }
    return $result
}

# ----------------------------------------------------------------------------
# 1M-CONTEXT MODEL RESOLUTION
#
# Claude Opus 5 (`claude-opus-5`) and Claude Sonnet 5 (`claude-sonnet-5`) each
# carry a 1M-token context window as BOTH the default and the maximum, and
# Anthropic's model docs are explicit that there is no smaller context variant
# of either. That means:
#   - there is no separate "-1m" / "[1m]" model ID to hunt for on the -5
#     models the way there was on the 4.x generation, and
#   - v3's `claude-sonnet-5(?!.*1m)` pattern was excluding a variant that does
#     not exist, while v3's literal-only `claude-opus-5` match meant Opus
#     silently never appeared at all.
# So: a -5 model is accepted as 1M on sight. Anything else is accepted only if
# the catalog itself reports a >=1M window (this is the escape hatch for an
# explicitly long-context 4.x entry). Nothing shorter is ever pinned.
#
# Provider prefix: OmniRoute serves Claude-family models under `cc/` (its
# Claude Code OAuth provider). Unprefixed `claude-*` IDs can come back as
# "Ambiguous model ..." when more than one connected provider exposes the same
# Claude model, so the prefixed form is preferred for the env-var pins, which
# accept any ID. The bare form is kept as a fallback.
# ----------------------------------------------------------------------------

function Test-Is1MContextModel {
    param([Parameter(Mandatory)]$Model)
    if ($Model.Id -match '(^|/)claude-(opus|sonnet)-5(\b|$|[-.])') { return $true }
    return ($Model.Context -ge $script:MIN_1M_CONTEXT)
}

function Resolve-OmniRoute1MModel {
    # Picks the best OmniRoute catalog entry for one Claude family, scoring
    # candidates rather than taking the first regex hit, so the choice is
    # stable and explainable in the log.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('opus', 'sonnet')][string]$Family,
        [Parameter(Mandatory)][array]$Models
    )
    # "auto/*" is OmniRoute's combo/router pseudo-model, not a real Claude
    # model - never pin one, we want a specific 1M model or nothing.
    $candidates = @($Models | Where-Object { $_.Id -and $_.Id -notmatch '^auto/' })
    if ($candidates.Count -eq 0) { return $null }

    # Built by concatenation, not interpolation: the pattern contains regex
    # '$' anchors, and burying those in a double-quoted PowerShell string is
    # asking for a subtle mis-parse the day someone edits it.
    $familyPattern = '(^|/)claude-' + $Family + '-5(\b|$|[-.])'
    $familyLoose = '(^|/)claude-' + $Family
    $familyAlias = 'claude-' + $Family + '-5$'
    $exact = @($candidates | Where-Object { $_.Id -match $familyPattern })

    if ($exact.Count -eq 0) {
        # No -5 in the catalog. Only consider this family's other members if
        # the catalog explicitly reports a >=1M window for them.
        $exact = @($candidates | Where-Object {
            $_.Id -match $familyLoose -and $_.Context -ge $script:MIN_1M_CONTEXT
        })
        if ($exact.Count -eq 0) {
            Write-Log "No 1M-context $Family model in OmniRoute's catalog" -Level "DEBUG"
            return $null
        }
        Write-Log "No claude-$Family-5 in catalog; using a 1M-context $Family fallback" -Level "DEBUG"
    }

    $scored = foreach ($model in $exact) {
        $score = 0
        # Claude Code OAuth provider: the unambiguous route for Claude models.
        if ($model.Id -match '^cc/') { $score += 100 }
        elseif ($model.Id -match '^anthropic/') { $score += 60 }
        elseif ($model.Id -notmatch '/') { $score += 40 }   # bare id, may be ambiguous
        else { $score += 10 }                                # some other provider's mirror
        if ($model.Context -ge $script:MIN_1M_CONTEXT) { $score += 30 }
        # Prefer the clean, undated alias over a pinned snapshot so the pin
        # keeps working when the snapshot rolls.
        if ($model.Id -match $familyAlias) { $score += 20 }
        [PSCustomObject]@{ Model = $model; Score = $score }
    }
    $best = @($scored | Sort-Object -Property Score -Descending | Select-Object -First 1)
    if ($best.Count -eq 0) { return $null }
    $chosen = $best[0].Model
    if (-not (Test-Is1MContextModel -Model $chosen)) {
        Write-Log "Best $Family candidate '$($chosen.Id)' is not 1M-context - refusing to pin it" -Level "WARN"
        return $null
    }
    Write-Log "Resolved $Family -> $($chosen.Id) (context $($chosen.Context), score $($best[0].Score))" -Level "DEBUG"
    return $chosen
}

# ----------------------------------------------------------------------------
# CLAUDE CODE PROVIDER CONNECTION (asked once, then remembered)
# ----------------------------------------------------------------------------

function Test-ClaudeProviderInCatalog {
    param([array]$Models)
    if (-not $Models -or $Models.Count -eq 0) { return $false }
    return [bool](@($Models | Where-Object { $_.Id -match 'claude' }).Count -gt 0)
}

function Test-OmniRouteProviderViaCli {
    # Secondary probe only. A failure here no longer means "not connected" -
    # the catalog check above is authoritative, because it tests the thing we
    # actually care about (can OmniRoute serve a Claude model?) instead of
    # whether one particular CLI subcommand exists and prints parseable JSON.
    $result = Invoke-ExternalCommand -Command "omniroute" -Arguments "providers list --json" -TimeoutSeconds 15 -Silent -NoLog
    if (-not $result.Success) { return $false }
    try {
        $providers = $result.Output | ConvertFrom-Json -ErrorAction Stop
        foreach ($p in @($providers)) {
            $idText = ("$($p.id) $($p.name)").ToLowerInvariant()
            if ($idText -notmatch "claude|^cc$|\bcc\b") { continue }
            $statusText = "$($p.status)".ToLowerInvariant()
            if ($statusText -match "connect|active|ok|ready" -or $p.connected -eq $true -or $p.enabled -eq $true) { return $true }
        }
    } catch { Write-Log "Could not parse 'omniroute providers list --json': $_" -Level "DEBUG" }
    return $false
}

function Confirm-ClaudeCodeProvider {
    # Returns $true if OmniRoute can serve Claude models. Consults, in order:
    #   1. the remembered result in config.json  (no network, no prompt)
    #   2. the live catalog we already fetched   (authoritative)
    #   3. `omniroute providers list --json`     (best-effort secondary)
    # Only if all three come up empty does it offer the dashboard - and even
    # then it offers to stop asking.
    [CmdletBinding()]
    param([array]$CatalogModels)

    if ($script:Config.OmniRouteProviderVerifiedUtc) {
        Write-Log "Claude provider previously verified at $($script:Config.OmniRouteProviderVerifiedUtc) - skipping onboarding" -Level "DEBUG"
        Write-Success "Claude Code provider already set up in OmniRoute (remembered)"
        return $true
    }

    if (Test-ClaudeProviderInCatalog -Models $CatalogModels) {
        $script:Config.OmniRouteProviderVerifiedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Save-Configuration
        Write-Success "Claude Code provider connected (found Claude models in OmniRoute's catalog)"
        Write-Hint "Recorded - you won't be sent to the OmniRoute dashboard again."
        return $true
    }

    if ((Test-CommandAvailable "omniroute" -UseCache) -and (Test-OmniRouteProviderViaCli)) {
        $script:Config.OmniRouteProviderVerifiedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Save-Configuration
        Write-Success "Claude Code provider connected (confirmed via the OmniRoute CLI)"
        return $true
    }

    if ($script:Config.OmniRouteProviderPromptSuppressed) {
        Write-Log "Provider not detected but prompting is suppressed by config" -Level "DEBUG"
        return $false
    }

    Write-Section "OmniRoute: Claude Code provider"
    Write-Warning "No Claude Code account connected in OmniRoute yet"
    Write-Hint "This is a one-time browser sign-in OmniRoute requires (it can't be"
    Write-Hint "automated from the CLI) before the Opus 5 / Sonnet 5 routes have"
    Write-Hint "anything behind them."
    $providerUrl = "$script:OMNIROUTE_URL/dashboard/providers/claude"
    try { Start-Process $providerUrl; Write-Hint "Opened: $providerUrl" }
    catch { Write-Log "Could not open browser to ${providerUrl}: $_" -Level "DEBUG"; Write-Hint "Open manually: $providerUrl" }
    Write-Hint "Click '+ Add', sign in with your Claude.ai account, then come back here."
    try { $null = Read-Host "  Press Enter once you've added the connection (or just Enter to skip)" } catch {}

    $recheck = Get-OmniRouteCatalog -ApiKey (Get-OmniRouteApiKey)
    if (Test-ClaudeProviderInCatalog -Models $recheck.Models) {
        $script:Config.OmniRouteProviderVerifiedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Save-Configuration
        Write-Success "Claude Code provider connected - remembered for next time"
        return $true
    }

    Write-Warning "Still not detected - you can finish this later at $providerUrl"
    if (Read-YesNo "Stop asking about this on future launches?" $false) {
        $script:Config.OmniRouteProviderPromptSuppressed = $true
        Save-Configuration
        Write-Info "Won't ask again. Use -ReconfigureOmniRoute to re-enable this check."
    }
    return $false
}

# ----------------------------------------------------------------------------
# CLAUDE CODE SETTINGS (~/.claude/settings.json, or an isolated profile dir)
# ----------------------------------------------------------------------------

function Get-ClaudeConfigDir {
    # Honours CLAUDE_CONFIG_DIR when -IsolateClaudeConfig set it, so settings
    # land in the same place the launched `claude` process will read them.
    if ($env:CLAUDE_CONFIG_DIR) { return $env:CLAUDE_CONFIG_DIR }
    return (Join-Path $env:USERPROFILE ".claude")
}

function Initialize-IsolatedClaudeProfile {
    # -IsolateClaudeConfig only. Gives this project window its own
    # CLAUDE_CONFIG_DIR (separate settings, credentials, history, cache) so
    # concurrent windows can never write the same Claude Code state file at
    # the same time. Seeded once from your real ~/.claude so MCP servers and
    # personal settings carry over instead of starting from nothing.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectDirectory)
    $slug = Get-PathSlug -Path $ProjectDirectory
    $profileDir = Join-Path $script:ProfileRoot $slug
    try {
        if (-not (Test-Path $profileDir)) {
            New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
            $source = Join-Path $env:USERPROFILE ".claude"
            if (Test-Path $source) {
                foreach ($leaf in @("settings.json", "CLAUDE.md", "commands", "agents", "skills")) {
                    $src = Join-Path $source $leaf
                    if (Test-Path $src) {
                        Copy-Item -Path $src -Destination $profileDir -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Log "Seeded isolated Claude profile from $source" -Level "DEBUG"
            }
            Write-Info "Created an isolated Claude config for this project"
        }
        $env:CLAUDE_CONFIG_DIR = $profileDir
        Write-Hint "CLAUDE_CONFIG_DIR = $profileDir"
        Write-Log "CLAUDE_CONFIG_DIR set to $profileDir"
    } catch {
        Write-Warning "Could not create an isolated Claude config - falling back to the shared one"
        Write-Log "Isolated profile setup failed: $_" -Level "WARN"
    }
}

function Set-ClaudeAvailableModels {
    # Restricts the /model picker to exactly the two OmniRoute 1M entries we
    # resolved, and nothing else. Claude Code's `availableModels` setting is
    # the documented allowlist mechanism and applies to gateway-discovered
    # models too.
    #
    # These are the RESOLVED OmniRoute catalog IDs (typically `cc/claude-opus-5`
    # and `cc/claude-sonnet-5`), not the bare Anthropic names - which is
    # precisely what keeps them distinguishable from Claude Code's own
    # built-in defaults in the list. Paired with the display labels set in
    # Set-OmniRouteLaunchEnvironment, a glance at /model tells you whether
    # you're on the OmniRoute 1M route or a stock model.
    #
    # Guarded by a named mutex because several project windows share one
    # ~/.claude/settings.json unless -IsolateClaudeConfig was used.
    [CmdletBinding()]
    param([string[]]$ModelIds)

    if (-not $ModelIds -or @($ModelIds).Count -eq 0) {
        Write-Log "No resolved model IDs - leaving availableModels untouched" -Level "DEBUG"
        return
    }
    $wanted = @($ModelIds | Where-Object { $_ } | Select-Object -Unique)
    $claudeDir = Get-ClaudeConfigDir
    $settingsPath = Join-Path $claudeDir "settings.json"

    $mutex = $null
    $held = $false
    try {
        $mutex = New-Object System.Threading.Mutex($false, "Global\LLMTokenOptimizer_v4_ClaudeSettings")
        try { $held = $mutex.WaitOne(5000, $false) } catch [System.Threading.AbandonedMutexException] { $held = $true }

        if (-not (Test-Path $claudeDir)) { New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null }
        $settings = if (Test-Path $settingsPath) {
            try { Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
            catch { Write-Log "Existing settings.json invalid, recreating: $_" -Level "WARN"; [PSCustomObject]@{} }
        } else { [PSCustomObject]@{} }

        $current = @()
        if ($settings.PSObject.Properties.Name -contains "availableModels") { $current = @($settings.availableModels) }
        $differs = $true
        if ($current.Count -eq $wanted.Count) {
            $differs = [bool](@(Compare-Object -ReferenceObject $current -DifferenceObject $wanted -SyncWindow 0).Count -gt 0)
        }
        if (-not $differs) {
            Write-Log "availableModels already correct - skipping write" -Level "DEBUG"
            return
        }
        if ($settings.PSObject.Properties.Name -contains "availableModels") {
            $settings.availableModels = $wanted
        } else {
            $settings | Add-Member -NotePropertyName "availableModels" -NotePropertyValue $wanted
        }
        # Atomic swap: another window may be reading this file right now.
        $tmp = "$settingsPath.$PID.tmp"
        $settings | ConvertTo-Json -Depth 10 | Out-File -FilePath $tmp -Encoding UTF8 -Force
        Move-Item -Path $tmp -Destination $settingsPath -Force
        Write-Success "Model picker restricted to: $($wanted -join ', ')"
        Write-Log "Wrote availableModels to $settingsPath : $($wanted -join ', ')"
    } catch {
        Write-Warning "Could not restrict the model picker (settings.json write failed)"
        Write-Log "Set-ClaudeAvailableModels failed: $_" -Level "WARN"
    } finally {
        if ($mutex) {
            if ($held) { try { $mutex.ReleaseMutex() } catch {} }
            try { $mutex.Dispose() } catch {}
        }
    }
}

# ----------------------------------------------------------------------------
# OMNIROUTE ONBOARDING (launcher window) - runs at most once per machine
# ----------------------------------------------------------------------------

function Initialize-OmniRoute {
    if ($SkipOmniRoute) { Write-Info "OmniRoute routing disabled via -SkipOmniRoute"; return $false }

    Write-Section "OmniRoute routing"
    if ($null -eq $script:Config.UseOmniRoute) {
        Write-Hint "Routes Claude Code through OmniRoute, which auto-applies its"
        Write-Hint "compression pipeline (RTK -> Caveman -> LLMLingua -> Lite) to"
        Write-Hint "every request. Opus 5 and Sonnet 5, 1M context, real Claude."
        $script:Config.UseOmniRoute = Read-YesNo "Route Claude Code through OmniRoute?" $true
        $script:Config.FirstRunComplete = $true
        Save-Configuration
    }
    if (-not $script:Config.UseOmniRoute) { Write-Info "OmniRoute routing disabled"; return $false }

    $started = Start-OmniRoute
    if (-not $started) {
        Write-Warning "OmniRoute isn't reachable - skipping the rest of its setup for now"
        return $false
    }

    # Bring the server up before touching credentials, otherwise a key check
    # against a still-booting server looks like a bad key.
    $null = Wait-OmniRouteReady -MaxWaitSeconds 25

    $apiKey = Get-OmniRouteApiKey
    $hadSavedKey = [bool]$apiKey

    if (-not $apiKey) {
        Write-Info "No OmniRoute API key saved yet"
        $apiKey = Read-OmniRouteApiKey
        if (-not $apiKey) {
            Write-Warning "No key entered - disabling OmniRoute routing"
            return (Set-OmniRouteDisabled)
        }
        Save-OmniRouteApiKey -PlainKey $apiKey
    } else {
        Write-Success "OmniRoute API key already saved - not asking again"
    }

    # Validate. This is the ONLY place a saved key can be discarded, and only
    # on an explicit rejection.
    $catalog = Get-OmniRouteCatalog -ApiKey $apiKey
    if ($catalog.Reachable -and -not $catalog.Authorized) {
        Write-Warning "OmniRoute rejected the saved API key"
        $apiKey = Read-OmniRouteApiKey -Prompt "New OmniRoute API key"
        if (-not $apiKey) {
            Write-Warning "No key entered - disabling OmniRoute routing"
            return (Set-OmniRouteDisabled)
        }
        Save-OmniRouteApiKey -PlainKey $apiKey
        $catalog = Get-OmniRouteCatalog -ApiKey $apiKey
    }

    if ($catalog.Authorized) {
        if ($hadSavedKey -and -not $script:Config.OmniRouteKeyVerifiedUtc) {
            $script:Config.OmniRouteKeyVerifiedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Save-Configuration
        } elseif (-not $hadSavedKey) {
            Save-OmniRouteApiKey -PlainKey $apiKey -Verified
            Write-Success "API key saved (encrypted for this Windows account only) and verified"
        }
        Write-Success "OmniRoute catalog reachable ($($catalog.Models.Count) models)"
    } else {
        Write-Warning "Could not read OmniRoute's catalog - keeping the saved key and continuing"
        Write-Log "Catalog unavailable: $(Get-Truncated $catalog.Error 200)" -Level "DEBUG"
    }

    $null = Confirm-ClaudeCodeProvider -CatalogModels $catalog.Models
    return $true
}

# ----------------------------------------------------------------------------
# PER-LAUNCH ENVIRONMENT - runs in each project window, right before `claude`
# ----------------------------------------------------------------------------

function Set-OmniRouteLaunchEnvironment {
    # Process-scoped only - never touches the permanent environment, so each
    # project window configures itself independently and closing one has no
    # effect on the others.
    if ($SkipOmniRoute -or -not $script:Config.UseOmniRoute) {
        Write-Warning "OmniRoute routing is OFF for this session - Claude will use its direct Anthropic login, not the OmniRoute 1M models"
        Write-Hint "Run the launcher again and answer 'y' to the OmniRoute prompt, or use -ReconfigureOmniRoute."
        return $false
    }

    $apiKey = Get-OmniRouteApiKey
    if (-not $apiKey) {
        # Reaches here mainly when config.json came from another Windows
        # account (DPAPI is account-bound), so decryption failed rather than
        # the key being absent.
        Write-Warning "Saved OmniRoute API key could not be read - re-entering it now"
        $apiKey = Read-OmniRouteApiKey
        if (-not $apiKey) {
            Write-Warning "No key entered - launching Claude directly (it will ask you to log in)"
            return $false
        }
        Save-OmniRouteApiKey -PlainKey $apiKey
    }

    if (-not (Wait-OmniRouteReady -MaxWaitSeconds 25)) {
        Write-Warning "OmniRoute isn't responding at $script:OMNIROUTE_URL - launching Claude directly (it will ask you to log in)"
        Write-Hint "Once OmniRoute finishes booting, use /model inside Claude to switch to the OmniRoute models."
        return $false
    }

    $env:ANTHROPIC_BASE_URL = $script:OMNIROUTE_URL   # root URL, no /v1 suffix
    $env:ANTHROPIC_AUTH_TOKEN = $apiKey
    $env:CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY = "1"

    # Gateway model discovery only surfaces bare claude*/anthropic*-prefixed
    # IDs in the picker, and a bare Claude ID can come back "Ambiguous model"
    # when several connected providers expose it. This opt-in OmniRoute
    # setting makes unprefixed claude-* IDs resolve to the Claude Code
    # provider, which is where we want them.
    [Environment]::SetEnvironmentVariable("OMNIROUTE_PREFER_CLAUDE_CODE_FOR_UNPREFIXED_CLAUDE_MODELS", "true", "Process")

    # If a real Anthropic API key is set in this environment, Claude Code can
    # prefer it over ANTHROPIC_AUTH_TOKEN and bypass OmniRoute entirely (this
    # is what "randomly asks for login" usually turns out to be). Clear it for
    # this process only so OmniRoute is the only path Claude Code has.
    if ($env:ANTHROPIC_API_KEY) {
        Write-Log "Clearing pre-existing ANTHROPIC_API_KEY for this process so OmniRoute is used instead" -Level "DEBUG"
        [Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", $null, "Process")
    }

    # ---- Resolve and pin the two 1M models ----
    $catalog = Get-OmniRouteCatalog -ApiKey $apiKey
    if (-not $catalog.Authorized -or $catalog.Models.Count -eq 0) {
        # The catalog can be briefly unavailable right after the server starts
        # answering. One retry before giving up on pinning.
        Start-Sleep -Seconds 2
        $catalog = Get-OmniRouteCatalog -ApiKey $apiKey
    }

    $modelPins = @(
        @{ Family = 'opus';   EnvVar = 'ANTHROPIC_DEFAULT_OPUS_MODEL';   NameVar = 'ANTHROPIC_DEFAULT_OPUS_MODEL_NAME';   Label = 'Opus 5 - 1M - OmniRoute' }
        @{ Family = 'sonnet'; EnvVar = 'ANTHROPIC_DEFAULT_SONNET_MODEL'; NameVar = 'ANTHROPIC_DEFAULT_SONNET_MODEL_NAME'; Label = 'Sonnet 5 - 1M - OmniRoute' }
    )
    $pinnedIds = [System.Collections.ArrayList]::new()
    foreach ($pin in $modelPins) {
        $resolved = Resolve-OmniRoute1MModel -Family $pin.Family -Models $catalog.Models
        if (-not $resolved) {
            Write-Warning "No 1M-context $($pin.Family) model found in OmniRoute's catalog - not pinning one"
            Write-Log "$($pin.Family): unpinned; Claude Code's built-in default for that tier stays in place" -Level "DEBUG"
            continue
        }
        [Environment]::SetEnvironmentVariable($pin.EnvVar, $resolved.Id, "Process")
        # Display name so the picker shows "Opus 5 - 1M - OmniRoute" rather
        # than something indistinguishable from the stock entry.
        [Environment]::SetEnvironmentVariable($pin.NameVar, $pin.Label, "Process")
        $null = $pinnedIds.Add($resolved.Id)
        $contextNote = if ($resolved.Context -ge $script:MIN_1M_CONTEXT) { "$([math]::Round($resolved.Context / 1000000.0, 2))M context" } else { "1M context (by model definition)" }
        Write-Success "$($pin.Label)  ->  $($resolved.Id)  [$contextNote]"
    }

    if ($pinnedIds.Count -eq 0) {
        Write-Warning "Neither 1M model could be pinned - /model may still show non-OmniRoute entries"
        Write-Hint "Check that the Claude Code provider is connected in OmniRoute's dashboard."
    } else {
        # Claude Code auto-compacts well before 1M by default, which would
        # throw away most of the window we just went to the trouble of
        # pinning. Raise the threshold and the output cap to match the models.
        [Environment]::SetEnvironmentVariable("CLAUDE_CODE_AUTO_COMPACT_WINDOW", "$($script:AUTO_COMPACT_WINDOW)", "Process")
        [Environment]::SetEnvironmentVariable("CLAUDE_CODE_MAX_OUTPUT_TOKENS", "$($script:MAX_OUTPUT_TOKENS)", "Process")
        Write-Hint "Auto-compact at $([math]::Round($script:AUTO_COMPACT_WINDOW / 1000)) k tokens, max output $([math]::Round($script:MAX_OUTPUT_TOKENS / 1000)) k"
        Set-ClaudeAvailableModels -ModelIds @($pinnedIds)
    }

    if ($Model) {
        # -Model sonnet|opus: session-only override so this launch doesn't
        # fall back onto whatever Claude Code last saved as its default.
        # Doesn't touch the saved default and doesn't persist to next launch.
        Write-Info "Forcing this session onto $Model (via -Model flag)"
        $script:ForcedModelAlias = $Model
    }

    Write-Info "Routing Claude through OmniRoute ($env:ANTHROPIC_BASE_URL)"
    Write-Hint "Compression applies automatically. Switch models inside Claude Code with /model."
    return $true
}

# ============================================================================
# MASTER FOLDER + PROJECT SELECTION
#   v4 model: you choose ONE master folder (the parent directory your projects
#   live in). Its immediate subfolders are the projects. You pick which of
#   them to open, and each one gets its own window.
# ============================================================================

function Read-PathWithHistory {
    # Inline path editor: type a path, or arrow through previously used ones.
    # Returns $null if the user pressed Escape.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Label, [array]$History = @())
    Write-Hint "Up/Down cycle history   Del remove   Esc cancel"
    Write-Host ""
    $history = @($History | Where-Object { $_ })
    $index = $history.Count
    $currentInput = ""
    while ($true) {
        [Console]::CursorLeft = 0
        Write-Host (' ' * [Math]::Min((Get-SafeConsoleWidth) - 1, 120)) -NoNewline
        [Console]::CursorLeft = 0
        Write-Host "  $($Label): " -NoNewline -ForegroundColor White
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
                Write-Host ""
                Write-Info "Removed from history: $removed"
                $index = [Math]::Min($index, $history.Count)
                $currentInput = if ($index -lt $history.Count) { $history[$index] } else { "" }
                # Persist the removal against whichever list this editor is on.
                $script:Config.MasterFolderHistory = @($script:Config.MasterFolderHistory | Where-Object { $_ -ne $removed })
                $script:Config.ProjectHistory = @($script:Config.ProjectHistory | Where-Object { $_ -ne $removed })
                Save-Configuration
                continue
            }
        }
        else {
            $currentInput += $key.KeyChar
            while ([Console]::KeyAvailable) { $currentInput += [Console]::ReadKey($true).KeyChar }
        }
    }
    $path = $currentInput.Trim().Trim('"').Trim()
    if ($path.EndsWith("\") -and $path -notmatch '^[A-Za-z]:\\$') { $path = $path.Substring(0, $path.Length - 1) }
    return $path
}

function Select-MasterFolderViaDialog {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "Select the master folder that contains your projects"
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq "OK" -and $dialog.SelectedPath) { return $dialog.SelectedPath }
    } catch { Write-Log "Folder dialog unavailable: $_" -Level "DEBUG" }
    return $null
}

function Test-MasterFolder {
    [CmdletBinding()] param([string]$Path)
    if (-not $Path) { Write-Fail "Input cannot be blank"; return $false }
    if (-not (Test-Path $Path -PathType Container)) { Write-Fail "Not a directory: $Path"; return $false }
    $subdirs = @(Get-ProjectCandidates -MasterPath $Path)
    if ($subdirs.Count -eq 0) {
        Write-Fail "No project subfolders found in: $Path"
        Write-Hint "The master folder should be the PARENT directory that holds your projects."
        return $false
    }
    Write-Success "Master folder: $Path ($($subdirs.Count) project$(if ($subdirs.Count -ne 1) { 's' }))"
    return $true
}

function Read-MasterFolder {
    Write-Section "Master folder"
    Write-Hint "Pick the parent folder that holds your projects. Each subfolder in it"
    Write-Hint "can then be opened in its own window, running at the same time."
    Write-Host ""

    # -MasterFolder wins, then the saved one (confirmed, not silently reused),
    # then a fresh prompt.
    if ($MasterFolder) {
        if (Test-MasterFolder -Path $MasterFolder) { return $MasterFolder.TrimEnd('\') }
        Write-Warning "-MasterFolder is not usable - falling back to the prompt"
    }
    if ($script:Config.MasterFolder -and (Test-Path $script:Config.MasterFolder -PathType Container)) {
        Write-Info "Last used: $($script:Config.MasterFolder)"
        if (Read-YesNo "Use it again?" $true) {
            if (Test-MasterFolder -Path $script:Config.MasterFolder) { return $script:Config.MasterFolder }
        }
    }

    while ($true) {
        Write-Hint "Enter a path, press Enter on an empty line to browse, or Esc to quit."
        $path = Read-PathWithHistory -Label "Master folder" -History @($script:Config.MasterFolderHistory)
        if ($null -eq $path) {
            if (Read-YesNo "Exit launcher?" $false) { Write-Info "Exiting by request"; exit 0 }
            continue
        }
        if (-not $path) {
            $path = Select-MasterFolderViaDialog
            if (-not $path) { continue }
        }
        $path = $path.TrimEnd('\')
        if (Test-MasterFolder -Path $path) { return $path }
    }
}

function Save-MasterFolder {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $script:Config.MasterFolder = $Path
    $script:Config.MasterFolderHistory = Merge-ConfigurationLists -Ours @($Path) -Theirs @($script:Config.MasterFolderHistory)
    Save-Configuration
}

function Get-ProjectCandidates {
    # Immediate subdirectories of the master folder, minus the noise nobody
    # means to open as a project.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$MasterPath)
    $excluded = @(
        'node_modules', '.git', '.svn', '.hg', '.venv', 'venv', 'env',
        '__pycache__', 'dist', 'build', 'out', 'target', '.idea', '.vscode',
        '.graphify', 'graphify-out', '.claude', 'bin', 'obj', '.next', '.cache'
    )
    try {
        return @(
            Get-ChildItem -Path $MasterPath -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -notin $excluded -and
                    $_.Name -notlike '.*' -and
                    -not ($_.Attributes -band [System.IO.FileAttributes]::Hidden) -and
                    -not ($_.Attributes -band [System.IO.FileAttributes]::System)
                } |
                Sort-Object Name
        )
    } catch {
        Write-Log "Could not enumerate '$MasterPath': $_" -Level "WARN"
        return @()
    }
}

function Test-ProjectWindowOpen {
    # True when another LLM-TokenOptimizer window currently holds this
    # project's lock, so the picker can show it as already open instead of
    # letting you launch a window that immediately refuses to run.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$ProjectDirectory)
    try {
        $name = "Global\LLMTokenOptimizer_v4_Project_$(Get-PathSlug -Path $ProjectDirectory)"
        $existing = [System.Threading.Mutex]::OpenExisting($name)
        $existing.Dispose()
        return $true
    } catch { return $false }
}

function Show-ProjectMenu {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MasterPath, [Parameter(Mandatory)][array]$Projects)
    Write-Section "Projects in $(Split-Path $MasterPath -Leaf)"
    Write-Hint $MasterPath
    Write-Host ""
    $i = 1
    foreach ($project in $Projects) {
        $isOpen = Test-ProjectWindowOpen -ProjectDirectory $project.FullName
        $known = (@($script:Config.ProjectHistory) -contains $project.FullName)
        $marker = if ($isOpen) { "open " } elseif ($known) { "seen " } else { "     " }
        $color = if ($isOpen) { [System.ConsoleColor]::DarkGray } else { [System.ConsoleColor]::Gray }
        Write-Host ("   {0,3}. " -f $i) -ForegroundColor DarkCyan -NoNewline
        Write-Host ("{0,-40}" -f $project.Name) -ForegroundColor $color -NoNewline
        Write-Host "  $marker" -ForegroundColor DarkYellow
        $i++
    }
    Write-Host ""
    Write-Hint "1        open that project in its own window"
    Write-Hint "1,3,7    open several at once, one window each"
    Write-Hint "a        open all of them"
    Write-Hint "m        open the master folder itself as a single project"
    Write-Hint "r        refresh this list      c  change master folder      q  quit"
    Write-Hint "'open' = already running in another window. 'seen' = opened before (session resumes)."
}

function Select-Projects {
    # Parses the picker input into a list of full paths. Returns:
    #   array  -> open these
    #   'r'    -> refresh
    #   'c'    -> change master folder
    #   'q'    -> quit
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MasterPath, [Parameter(Mandatory)][array]$Projects)
    $answer = (Read-Host "  Choose").Trim()
    if (-not $answer) { return 'r' }
    switch -Regex ($answer) {
        '^[Qq]$' { return 'q' }
        '^[Rr]$' { return 'r' }
        '^[Cc]$' { return 'c' }
        '^[Mm]$' { return @($MasterPath) }
        '^[Aa]$' { return @($Projects | ForEach-Object { $_.FullName }) }
    }
    $selected = [System.Collections.ArrayList]::new()
    foreach ($token in ($answer -split '[,\s]+' | Where-Object { $_ })) {
        if ($token -notmatch '^\d+$') { Write-Fail "Not a number: $token"; return 'r' }
        $index = [int]$token
        if ($index -lt 1 -or $index -gt $Projects.Count) { Write-Fail "Out of range: $index"; return 'r' }
        $path = $Projects[$index - 1].FullName
        if (-not ($selected -contains $path)) { $null = $selected.Add($path) }
    }
    if ($selected.Count -eq 0) { return 'r' }
    return @($selected)
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

function Test-ProjectAlreadyKnown {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    return (@($script:Config.ProjectHistory) -contains $Path)
}

function Add-ProjectToHistory {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $script:Config.ProjectHistory = Merge-ConfigurationLists -Ours @($Path) -Theirs @($script:Config.ProjectHistory)
    $script:Config.LastProject = $Path
    Save-Configuration
}

# ----------------------------------------------------------------------------
# WINDOW SPAWNING
#   Each project runs in a brand-new PowerShell console, re-invoking this same
#   script with -ProjectPath. Separate process, separate console, separate
#   Claude Code session - which is what lets several of them run at once.
# ----------------------------------------------------------------------------

function Start-ProjectWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectDirectory)

    if (-not $script:SelfPath -or -not (Test-Path $script:SelfPath -PathType Leaf)) {
        Write-Fail "Can't find this script's own path - unable to open a new window"
        Write-Hint "Run it from a file (not piped into powershell) so new windows can be spawned."
        return $false
    }
    if (Test-ProjectWindowOpen -ProjectDirectory $ProjectDirectory) {
        Write-Warning "Already open: $(Split-Path $ProjectDirectory -Leaf) - skipping"
        return $false
    }

    # Forward the flags that should apply to every window this launcher opens.
    $argList = [System.Collections.ArrayList]::new()
    $null = $argList.Add('-NoProfile')
    $null = $argList.Add('-ExecutionPolicy'); $null = $argList.Add('Bypass')
    $null = $argList.Add('-File');            $null = $argList.Add("`"$($script:SelfPath)`"")
    $null = $argList.Add('-ProjectPath');     $null = $argList.Add("`"$ProjectDirectory`"")
    $null = $argList.Add('-ChildWindow')
    if ($Model)               { $null = $argList.Add('-Model'); $null = $argList.Add($Model) }
    if ($SkipOmniRoute)       { $null = $argList.Add('-SkipOmniRoute') }
    if ($VerboseMode)         { $null = $argList.Add('-VerboseMode') }
    if ($IsolateClaudeConfig) { $null = $argList.Add('-IsolateClaudeConfig') }

    try {
        $null = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -WorkingDirectory $ProjectDirectory -ErrorAction Stop
        Write-Success "Opened window: $(Split-Path $ProjectDirectory -Leaf)"
        Write-Log "Spawned project window for $ProjectDirectory"
        return $true
    } catch {
        Write-Fail "Could not open a window for $(Split-Path $ProjectDirectory -Leaf)"
        Write-Log "Start-Process failed for ${ProjectDirectory}: $_" -Level "ERROR"
        return $false
    }
}

# ============================================================================
# GRAPHIFY OPERATIONS
#   NOTE: Graphify 0.17.1+ writes to a hidden .graphify\ directory (not
#   graphify-out\) and auto-generates the HTML studio during `extract` itself
#   - there is no separate `export html` step anymore.
#
#   Everything here is scoped to $PWD, which each project window has already
#   set to its own folder - so parallel windows never touch the same graph.
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
# Strict-mode enforcement: hard-blocks the first raw source read of a session
# and redirects it to the graph, then writes a mandatory `PreToolUse` hook into
# .claude\settings.json that intercepts file search (Glob/Grep) and bash
# commands so Claude can't bypass the graph by shelling out to `grep`/`find`.
# Runs every launch; each step is idempotent and marker-gated.
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

    # Keeps the block active for this process; strict installs alone are only
    # a marker file on disk, this env var is what Graphify's hook actually
    # checks at runtime before letting a raw read through.
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
# Ensures every project this launcher touches has the graph-first directive in
# its CLAUDE.md, so strict mode is backed up by an explicit instruction even on
# a machine where the PreToolUse hook install failed.
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
    # Graphify already tracks what it's seen. On a first run in this project it
    # does a full scan (`graphify .`); once a graph exists, `graphify update`
    # only re-parses files that changed since the last run.
    $isUpdate = Test-Path $graphFile -PathType Leaf
    $extractArgs = if ($isUpdate) { "update" } else { "." }
    $verb = if ($isUpdate) { "Updating changed files in" } else { "Extracting" }
    Write-Info "$verb project structure (also builds the HTML studio)..."
    Write-Log "Starting graph $extractArgs in: $($PWD.Path)"
    $extractStart = Get-Date
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments $extractArgs -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph"
    $extractTime = (Get-Date) - $extractStart

    # Newer Graphify builds refuse to run on a mixed repo (code + docs/PDFs/
    # images) unless you either point it at an LLM backend for semantic
    # extraction or tell it to skip the non-code files entirely. The exact skip
    # flag isn't consistent across versions, so read graphify's own --help
    # output and use whatever it actually advertises.
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
# semantic extraction" isn't consistent across versions. Read graphify's own
# --help text and pick whatever it advertises. Cached per-process.
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
#   Claude Code skills from the skills.sh registry. Idempotent; `-y` on both
#   npx and autoskills skips every interactive prompt.
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
    # Recorded on the script scope rather than returned: `& $ClaudePath` below
    # writes to the pipeline, so anything this function returns would arrive at
    # the caller mixed in with Claude's own output.
    $script:OmniRouteRouted = [bool](Set-OmniRouteLaunchEnvironment)

    $claudeArgs = @()
    if ($Resume) {
        $claudeArgs += "--continue"
        Write-Info "Same workspace as before - resuming the previous session"
    } else {
        Write-Info "Starting a new session"
    }
    if ($script:ForcedModelAlias) { $claudeArgs += @("--model", $script:ForcedModelAlias) }

    Write-Log "Launching Claude: $ClaudePath $($claudeArgs -join ' ') in $($PWD.Path) | routed=$($script:OmniRouteRouted)"
    try {
        if ($claudeArgs.Count -gt 0) { & $ClaudePath @claudeArgs } else { & $ClaudePath }

        # --continue fails fast (non-zero exit) when there's no prior session to
        # resume - e.g. history was cleared, or this is actually the first time
        # despite being in our ProjectHistory. Fall back to a fresh session
        # automatically instead of leaving the user stuck.
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
    Write-Hint ("OmniRoute   " + $(if ($OmniRouteActive) { "active - Opus 5 / Sonnet 5 at 1M context, compression applied automatically" } else { "not used" }))
    Write-Hint ("Elapsed     " + (Get-Elapsed))
}

# ============================================================================
# PROJECT MODE - one spawned window, one project folder
#   Skips the machine-wide bootstrap the launcher window already did (winget
#   installs, update prompts, starting the OmniRoute server) and gets straight
#   to this project's graph and its own Claude session.
# ============================================================================

function Invoke-ProjectMode {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $projectName = Split-Path $Path -Leaf
    $host.UI.RawUI.WindowTitle = "LLM-TokenOptimizer - $projectName"
    Write-Title -Subtitle "Project window: $projectName"

    if (-not (Test-Path $Path -PathType Container)) {
        Stop-Script -Code 102 -Reason "Project folder not found: $Path"
    }

    # Per-project lock. Different projects run side by side; the same project
    # twice would have two Graphify runs writing one graph.json.
    if (-not (Initialize-InstanceLock -ProjectDirectory $Path)) { Stop-Script -Code 100 }
    Register-CleanupHandlers
    Write-Log "=== PROJECT WINDOW === $Path"

    Write-Section "Environment"
    Add-StandardPaths
    $depSummary = Get-DependencySummary -Quiet
    $criticalMissing = @($depSummary.Missing | Where-Object { $_.Name -in @("Python", "pip", "npm") })
    if ($criticalMissing.Count -gt 0) {
        Write-Warning "Missing here: $(($criticalMissing | ForEach-Object { $_.Name }) -join ', ')"
        Write-Hint "Run the launcher window (no -ProjectPath) once to install them."
    } else {
        Write-Success "Toolchain present"
    }

    if (-not (Test-CommandAvailable "graphify" -UseCache)) {
        Write-Section "Graphify installation"
        if (-not (Install-Graphify)) { Stop-Script -Code 104 -Reason "Cannot continue without Graphify" }
    }

    $claudePath = Find-ClaudeExecutable -Quiet
    Write-Success "Claude: $claudePath"

    # A window opened directly with -ProjectPath (rather than by the launcher)
    # may be the very first run on this machine - do the one-time OmniRoute
    # onboarding here rather than launching with no routing at all.
    if ($null -eq $script:Config.UseOmniRoute -and -not $SkipOmniRoute) {
        $null = Initialize-OmniRoute
    }

    if ($IsolateClaudeConfig) { Initialize-IsolatedClaudeProfile -ProjectDirectory $Path }

    if (-not (Test-ProjectDirectory -Path $Path)) {
        Stop-Script -Code 102 -Reason "Project folder is not usable: $Path"
    }
    $isReturningProject = Test-ProjectAlreadyKnown -Path $Path
    Add-ProjectToHistory -Path $Path
    Set-Location $Path
    Write-Log "Working directory: $Path | Returning project: $isReturningProject"

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
        Write-Info "Exiting without launching Claude"
        return
    }
    Start-ClaudeSession -ClaudePath $claudePath -Resume:$isReturningProject
    Show-SessionSummary -ProjectPath $Path -Resumed $isReturningProject -OmniRouteActive ([bool]$script:OmniRouteRouted)

    Write-Section "Done"
    Write-Success "Completed in $(Get-Elapsed)"
    Write-Hint "Closing this window won't affect your other project windows."
    Read-Host "  Press Enter to close this window" | Out-Null
}

# ============================================================================
# LAUNCHER MODE - the control panel window
#   Does the machine-wide setup once, then stays open so you can open (and
#   re-open) as many project windows as you like, all running simultaneously.
# ============================================================================

function Invoke-LauncherMode {
    $host.UI.RawUI.WindowTitle = "LLM-TokenOptimizer v$($script:APP_VERSION) - launcher"
    Write-Title
    Write-Log "=== LAUNCHER STARTED === v$($script:APP_VERSION) | User: $env:USERNAME | PID: $PID"

    Register-CleanupHandlers

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

    # Asked fresh every launch - Git, Node, Python (via winget), Graphify (via
    # pip), Claude Code and OmniRoute (via npm). Best-effort, non-fatal.
    Invoke-UpdateCheckIfRequested

    $claudePath = Find-ClaudeExecutable
    Test-ClaudeExecutable -Path $claudePath

    # One OmniRoute server serves every project window. Onboarding runs at most
    # once per machine now - see Confirm-ClaudeCodeProvider.
    $null = Initialize-OmniRoute

    $masterPath = Read-MasterFolder
    Save-MasterFolder -Path $masterPath

    $openedCount = 0
    while ($true) {
        $projects = @(Get-ProjectCandidates -MasterPath $masterPath)
        if ($projects.Count -eq 0) {
            Write-Warning "No project subfolders in $masterPath any more"
            $masterPath = Read-MasterFolder
            Save-MasterFolder -Path $masterPath
            continue
        }

        Show-ProjectMenu -MasterPath $masterPath -Projects $projects
        Write-Host ""
        $choice = Select-Projects -MasterPath $masterPath -Projects $projects

        # Deliberately if/elseif rather than switch: inside a switch,
        # PowerShell's `continue` applies to the SWITCH, not the enclosing
        # while loop, so a 'r'/'c' answer would fall straight through into the
        # window-opening block below with $choice still set to a letter.
        if ($choice -is [string]) {
            if ($choice -eq 'q') {
                Write-Section "Done"
                if ($openedCount -gt 0) {
                    Write-Success "$openedCount project window$(if ($openedCount -ne 1) { 's' }) opened this session"
                    Write-Hint "They keep running after this launcher closes."
                }
                Write-Success "Launcher finished in $(Get-Elapsed)"
                return
            }
            if ($choice -eq 'c') {
                $masterPath = Read-MasterFolder
                Save-MasterFolder -Path $masterPath
            }
            continue   # 'r' / 'c' / bad input -> redraw the menu
        }

        Write-Section "Opening windows"
        foreach ($project in @($choice)) {
            if (Start-ProjectWindow -ProjectDirectory $project) {
                $openedCount++
                # Stagger the spawns slightly: several windows hitting pip,
                # npx and the OmniRoute catalog in the same instant is a
                # needless thundering herd on a cold start.
                Start-Sleep -Milliseconds 700
            }
        }
        Write-Host ""
        Write-Hint "Windows are running independently. Pick more below, or 'q' to close the launcher."
        Write-Host ""
    }
}

# ============================================================================
# MAIN ENTRY POINT
# ============================================================================

function Main {
    Initialize-Logging
    Initialize-Configuration

    try {
        if ($ProjectPath) {
            Invoke-ProjectMode -Path ($ProjectPath.Trim().Trim('"').TrimEnd('\'))
        } else {
            Invoke-LauncherMode
        }
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

