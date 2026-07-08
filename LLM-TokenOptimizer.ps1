#Requires -Version 5.1
<#
.SYNOPSIS
    LLM-TokenOptimizer - Production Quality v2.1
.DESCRIPTION
    Self-bootstrapping launcher that verifies the environment, installs
    dependencies, generates Graphify graphs, and launches Claude Code reliably
    on any Windows 10/11 PC. References itself as LLM-TokenOptimizer throughout.
.NOTES
    Version: 2.1.0
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
    [switch]$SkipProxy,
    [switch]$ResetConfig
)

# ============================================================================
# STRICT MODE AND GLOBAL STATE
# ============================================================================
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Application constants
$script:APP_NAME = "LLM-TokenOptimizer"
$script:APP_VERSION = "2.1.0"
$script:MAX_HISTORY = 20
$script:GRAPHIFY_IGNORE_VERSION = "V4"
$script:PROXY_PORT = 47821
$script:MAX_LOG_FILES = 10

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

# ============================================================================
# UI TOOLKIT (ASCII only - safe in any console/encoding)
#   One status primitive + thin wrappers, so every line shares an aligned,
#   colour-coded gutter. No boxes, no centered text.
# ============================================================================

function Get-SafeConsoleWidth {
    # [Console]::WindowWidth throws when there is no attached console
    # (redirected output, some hosts). Fall back to a sane default.
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

function Write-Success { param([Parameter(Mandatory)][string]$Message) Write-Status "ok"   ([System.ConsoleColor]::Green)  $Message ([System.ConsoleColor]::Gray) }
function Write-Info    { param([Parameter(Mandatory)][string]$Message) Write-Status "info" ([System.ConsoleColor]::DarkCyan) $Message ([System.ConsoleColor]::Gray) }
function Write-Warning { param([Parameter(Mandatory)][string]$Message) Write-Status "warn" ([System.ConsoleColor]::Yellow) $Message ([System.ConsoleColor]::Yellow) }
function Write-Fail    { param([Parameter(Mandatory)][string]$Message) Write-Status "fail" ([System.ConsoleColor]::Red)    $Message ([System.ConsoleColor]::Red) }
function Write-Hint    { param([string]$Message = "") Write-Host "  $Message" -ForegroundColor DarkGray }

function Write-Title {
    Write-Host ""
    Write-Host "  LLM-TokenOptimizer " -ForegroundColor Cyan -NoNewline
    Write-Host "v$($script:APP_VERSION)" -ForegroundColor DarkGray
    Write-Host "  Self-bootstrapping environment for Claude Code" -ForegroundColor DarkGray
    Write-Host ("  " + (Get-Rule)) -ForegroundColor DarkGray
}

function Write-Section {
    param([Parameter(Mandatory)][string]$Name)
    Write-Host ""
    Write-Host "  $Name" -ForegroundColor Cyan
    Write-Host ("  " + (Get-Rule)) -ForegroundColor DarkGray
}

function Get-Elapsed { return ((Get-Date) - $script:StartTime).ToString('mm\:ss') }

function Read-YesNo {
    # Streamlined confirm with a default: Enter accepts the default answer.
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
    # Write a small "done" sentinel file; failures are non-fatal.
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
    } catch {
        # Logging is non-critical
    }
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
    # Central exit path. `exit` is NOT catchable by try/catch and slams the
    # window shut, so always pause here so the user can read WHY it stopped.
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
        PxpipeDirectory = ""
        LastProject = ""
        ProjectHistory = [array]@()
        UseProxy = $null
        ClaudePath = ""
        GraphifyIgnoreVersion = ""
        AutoUpdatePxpipe = $false
        AutoUpdateGraphify = $false
        FirstRunComplete = $false
        LastGraphifyVersion = ""
    }
}

function Initialize-Configuration {
    try {
        if (-not (Test-Path $script:AppDataDir)) {
            New-Item -ItemType Directory -Path $script:AppDataDir -Force | Out-Null
            Write-Log "Created app data directory: $($script:AppDataDir)"
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
            # Backfill any keys added in newer versions.
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

function Set-ProxyDisabled {
    # Turn the proxy off, persist, and return $false so callers can
    # `return (Set-ProxyDisabled)` in one line.
    $script:Config.UseProxy = $false
    Save-Configuration
    return $false
}

# ============================================================================
# SINGLE INSTANCE PROTECTION
# ============================================================================

function Initialize-Mutex {
    try {
        $mutexName = "Global\LLMTokenOptimizerMutex_v2_$(whoami | ForEach-Object { $_ -replace '[^a-zA-Z0-9]', '' })"
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
    Write-Log "Cleanup initiated"
    if ($script:Config -and $script:Config.UseProxy) { Stop-PxpipeProxy }
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
        } catch {
            # Pattern didn't match anything
        }
    }
    if ($addedCount -gt 0) { Write-Log "Added $addedCount directories to PATH" }
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
        [switch]$NoLog
    )
    $result = @{ Success = $false; Output = ""; ExitCode = -1; TimedOut = $false }
    if (-not $NoLog) { Write-Log "Exec: $Command $Arguments" -Level "DEBUG" }
    $process = $null
    try {
        # Resolve the command so we can correctly launch .cmd/.bat/.ps1 shims.
        # With UseShellExecute=$false, the Windows process API cannot start a
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
        if ($TimeoutSeconds -gt 0) {
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

    # pip has a more targeted remediation message and must be checked before
    # the generic fatal check below (which excludes it) can be reached.
    if (@($Missing | Where-Object { $_.Name -eq "pip" }).Count -gt 0) {
        Write-Host ""
        Write-Fail "Python was found but pip is missing"
        Write-Hint "Reinstall Python with 'Add Python to PATH' and 'Install pip' checked."
        Stop-Script -Code 102
    }

    # Graphify + Claude are excluded: Graphify is auto-installed later, and
    # Claude has its own multi-strategy detection (PATH, registry, folders,
    # picker) that a simple PATH check must not short-circuit.
    $fatalMissing = @($Missing | Where-Object { $_.Info.Required -and $_.Name -notin @("Graphify", "Claude", "pip") })
    if ($fatalMissing.Count -gt 0) {
        Write-Host ""
        Write-Fail "Required dependencies are missing:"
        foreach ($dep in $fatalMissing) {
            Write-Hint "  - $($dep.Name): $($dep.Info.Url)"
            if ($dep.Info.Advice) { Write-Hint "      $($dep.Info.Advice)" }
        }
        Write-Hint "Install them, then run this script again."
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
    $result = Invoke-ExternalCommand -Command "pip" -Arguments "install graphify" -TimeoutSeconds 180
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
    if (-not $script:Config.AutoUpdateGraphify -and -not $ForceUpdate) { return }
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
# PXPIPE MANAGEMENT
# ============================================================================

function Initialize-Pxpipe {
    if ($SkipProxy) { Write-Info "Proxy disabled via -SkipProxy"; return $false }

    if ($null -eq $script:Config.UseProxy) {
        Write-Section "Pxpipe proxy (optional)"
        Write-Hint "Optional proxy for enhanced Claude integration; best with Fable 5 workflows."
        $script:Config.UseProxy = Read-YesNo "Enable Pxpipe proxy?" $false
        $script:Config.FirstRunComplete = $true
        Save-Configuration
    }
    if (-not $script:Config.UseProxy) { Write-Info "Proxy disabled"; return $false }

    if (-not $script:Config.PxpipeDirectory) {
        Write-Hint "Pxpipe will be cloned to a directory you specify (e.g. D:\Tools\Pxpipe)."
        $pxInput = (Read-Host "  Installation path").Trim().Trim('"')
        if (-not $pxInput) { Write-Warning "No path provided - disabling proxy"; return (Set-ProxyDisabled) }
        $script:Config.PxpipeDirectory = $pxInput
        Save-Configuration
    }

    $pxDir = $script:Config.PxpipeDirectory
    if (-not (Test-Path $pxDir)) {
        Write-Section "Pxpipe installation"
        try {
            $parentDir = Split-Path $pxDir -Parent
            if ($parentDir -and -not (Test-Path $parentDir)) { New-Item -ItemType Directory -Path $parentDir -Force | Out-Null }
            New-Item -ItemType Directory -Path $pxDir -Force | Out-Null
        } catch {
            Write-Fail "Cannot create directory: $_"
            return (Set-ProxyDisabled)
        }
        Write-Info "Cloning Pxpipe repository..."
        $result = Invoke-ExternalCommand -Command "git" -Arguments "clone https://github.com/nicepkg/pxpipe.git `"$pxDir`"" -TimeoutSeconds 120
        if (-not $result.Success) {
            Write-Fail "Failed to clone Pxpipe"
            Write-Hint (Get-Truncated $result.Output 300)
            return (Set-ProxyDisabled)
        }
        Write-Success "Pxpipe cloned"
    }

    if ($script:Config.AutoUpdatePxpipe -or $ForceUpdate) {
        Write-Info "Updating Pxpipe..."
        $result = Invoke-ExternalCommand -Command "git" -Arguments "pull" -WorkingDirectory $pxDir -TimeoutSeconds 60
        if ($result.Success -and $result.Output -notmatch "Already up to date") { Write-Success "Pxpipe updated" }
    }

    if (Test-PxpipeNeedsInstall -PxDir $pxDir) {
        Write-Info "Installing Pxpipe dependencies..."
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "install" -WorkingDirectory $pxDir -TimeoutSeconds 300
        if (-not $result.Success) { Write-Fail "npm install failed for Pxpipe"; return $false }
        Write-Success "Pxpipe dependencies installed"
        $lockFile = Join-Path $pxDir "package-lock.json"
        if (Test-Path $lockFile) {
            try { (Get-FileHash $lockFile -Algorithm MD5).Hash | Out-File -FilePath (Join-Path $pxDir ".package-lock.hash") -Force -Encoding ASCII } catch {}
        }
    } else {
        Write-Success "Pxpipe dependencies up to date"
    }
    return $true
}

function Test-PxpipeNeedsInstall {
    [CmdletBinding()] param([string]$PxDir)
    if (-not (Test-Path (Join-Path $PxDir "node_modules"))) { return $true }
    $lockFile = Join-Path $PxDir "package-lock.json"
    $hashFile = Join-Path $PxDir ".package-lock.hash"
    if (-not (Test-Path $lockFile)) { return $false }
    if (-not (Test-Path $hashFile)) { return $true }
    try {
        $currentHash = (Get-FileHash $lockFile -Algorithm MD5).Hash
        $savedHash = (Get-Content $hashFile -Raw -ErrorAction SilentlyContinue).Trim()
        return ($currentHash -ne $savedHash)
    } catch { return $true }
}

function Start-PxpipeProxy {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$WorkingDirectory)
    Write-Section "Proxy"
    if (Test-ProxyRunning) { Write-Success "Already running on port $($script:PROXY_PORT)"; return $true }
    Write-Info "Starting Pxpipe proxy..."
    try {
        $proxyArgs = '/c "title PxpipeProxyWindow && npm exec pxpipe-proxy"'
        $null = Start-Process -FilePath "cmd.exe" -ArgumentList $proxyArgs -WorkingDirectory $WorkingDirectory -WindowStyle Normal
        for ($waited = 0; $waited -lt 15; $waited++) {
            Start-Sleep -Seconds 1
            if (Test-ProxyRunning) {
                Write-Success "Proxy started on port $($script:PROXY_PORT)"
                try { $null = (New-Object -ComObject Wscript.Shell).AppActivate($host.UI.RawUI.WindowTitle) } catch {}
                return $true
            }
        }
        Write-Warning "Proxy startup timed out (15s) - it may still be starting"
        return $true
    } catch { Write-Fail "Failed to start proxy: $_"; return $false }
}

function Test-ProxyRunning {
    try { if (Get-NetTCPConnection -LocalPort $script:PROXY_PORT -State Listen -ErrorAction Stop) { return $true } }
    catch {
        try { if (netstat -ano 2>$null | Select-String ":$($script:PROXY_PORT)\s.*LISTENING") { return $true } } catch {}
    }
    return $false
}

function Stop-PxpipeProxy {
    Write-Info "Stopping Pxpipe proxy..."
    $stopped = $false
    try {
        foreach ($proc in (Get-Process | Where-Object { $_.MainWindowTitle -match "PxpipeProxyWindow" })) {
            try {
                $null = $proc.CloseMainWindow()
                $proc.WaitForExit(5000) | Out-Null
                if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
                $stopped = $true
            } catch { Write-Log "Graceful stop failed for PID $($proc.Id)" -Level "DEBUG" }
        }
    } catch {}
    if (Test-ProxyRunning) {
        try {
            foreach ($conn in (Get-NetTCPConnection -LocalPort $script:PROXY_PORT -State Listen -ErrorAction SilentlyContinue)) {
                if ($conn.OwningProcess) {
                    try { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue; $stopped = $true } catch { Write-Log "Force kill failed for PID $($conn.OwningProcess)" -Level "WARN" }
                }
            }
        } catch { Write-Log "Port-based cleanup failed" -Level "WARN" }
    }
    if ($stopped) { Write-Success "Proxy stopped" }
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

function Get-GraphifyIgnoreTemplate {
    return @(
        "# GRAPHIFY_IGNORE_TEMPLATE_$($script:GRAPHIFY_IGNORE_VERSION)",
        "# Auto-generated by LLM-TokenOptimizer v$($script:APP_VERSION)",
        "# Preserve this header for automatic updates",
        "",
        "# === Build Outputs ===",
        "node_modules/", "dist/", "build/", "out/", ".next/", ".nuxt/", ".output/", ".svelte-kit/",
        "",
        "# === Rust/Backend ===",
        "target/", "src-tauri/target/", ".tauri/",
        "",
        "# === Version Control ===",
        ".git/", ".gitignore",
        "",
        "# === Graphify Output ===",
        "graphify-out/",
        "",
        "# === Logs ===",
        "*.log", "*.json.log",
        "",
        "# === Temp Files ===",
        "*.tmp", "*.bak", "*.swp", "*.swo", "*~",
        "",
        "# === 3D Models ===",
        "*.ply", "*.obj", "*.fbx", "*.splat", "*.gltf", "*.glb",
        "",
        "# === Documents ===",
        "*.md", "*.txt", "*.html", "*.htm", "*.xml", "*.pdf", "*.docx", "*.doc",
        "*.xlsx", "*.xls", "*.pptx", "*.ppt", "*.csv", "*.tsv",
        "",
        "# === Images ===",
        "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.bmp", "*.tiff",
        "*.tif", "*.ico", "*.heic", "*.dng", "*.cr2", "*.nef", "*.arw",
        "",
        "# === Audio ===",
        "*.aup3", "*.wav", "*.mp3", "*.flac", "*.m4a", "*.ogg", "*.aac",
        "",
        "# === Video ===",
        "*.mp4", "*.mkv", "*.mov", "*.avi", "*.wmv",
        "",
        "# === Config/Data ===",
        "*.yaml", "*.yml", "*.toml", "*.ini", "*.jsonld",
        "",
        "# === Licenses ===",
        "LICENSE", "LICENSE.*", "COPYING",
        "",
        "# === END TEMPLATE ===",
        "# Add custom rules below this line"
    )
}

function Initialize-GraphifyIgnore {
    $ignoreFile = Join-Path $PWD ".graphifyignore"
    if (-not (Test-Path $ignoreFile)) {
        Write-Info "Creating .graphifyignore (template $($script:GRAPHIFY_IGNORE_VERSION))"
        Get-GraphifyIgnoreTemplate | Out-File -FilePath $ignoreFile -Encoding UTF8 -Force
        $script:Config.GraphifyIgnoreVersion = $script:GRAPHIFY_IGNORE_VERSION; Save-Configuration
        return
    }
    $content = Get-Content $ignoreFile -Raw -ErrorAction SilentlyContinue
    if ($content -match "GRAPHIFY_IGNORE_TEMPLATE_(\w+)") {
        $currentVersion = $matches[1]
        if ($currentVersion -eq $script:GRAPHIFY_IGNORE_VERSION) { Write-Success "Ignore template current ($currentVersion)"; return }
        Write-Info "Updating .graphifyignore ($currentVersion -> $($script:GRAPHIFY_IGNORE_VERSION))"
        $customRules = @()
        $inCustom = $false
        foreach ($line in (Get-Content $ignoreFile -ErrorAction SilentlyContinue)) {
            if ($line -match "# === END TEMPLATE ===") { $inCustom = $true; continue }
            if ($inCustom -and $line.Trim() -ne "" -and -not $line.StartsWith("#")) { $customRules += $line }
        }
        $newContent = [System.Collections.ArrayList]@(Get-GraphifyIgnoreTemplate)
        if ($customRules.Count -gt 0) {
            $null = $newContent.Add(""); $null = $newContent.Add("# === PRESERVED CUSTOM RULES ===")
            foreach ($rule in $customRules) { $null = $newContent.Add($rule) }
        }
        $newContent | Out-File -FilePath $ignoreFile -Encoding UTF8 -Force
        $script:Config.GraphifyIgnoreVersion = $script:GRAPHIFY_IGNORE_VERSION; Save-Configuration
        Write-Success "Ignore template updated (custom rules preserved)"
    } else {
        Write-Info "Legacy .graphifyignore detected - replacing"
        try { Copy-Item $ignoreFile "$ignoreFile.legacy" -Force } catch {}
        Get-GraphifyIgnoreTemplate | Out-File -FilePath $ignoreFile -Encoding UTF8 -Force
        $script:Config.GraphifyIgnoreVersion = $script:GRAPHIFY_IGNORE_VERSION; Save-Configuration
        Write-Success "Ignore template replaced (backup: .graphifyignore.legacy)"
    }
}

function Invoke-GraphifyExtract {
    Write-Section "Graph extraction"
    Write-Info "Extracting project structure..."
    Write-Log "Starting graph extraction in: $($PWD.Path)"
    $extractStart = Get-Date
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "extract ." -TimeoutSeconds 300
    $extractTime = (Get-Date) - $extractStart
    if (-not $result.Success) {
        Write-Fail "Graph extraction failed"
        foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
        return $false
    }
    $graphFile = Join-Path (Join-Path $PWD "graphify-out") "graph.json"
    if (-not (Test-Path $graphFile -PathType Leaf)) { Write-Fail "Graph file missing: graphify-out\graph.json"; return $false }
    $stats = Get-GraphStatistics -GraphPath $graphFile
    Write-Success "Extracted in $($extractTime.ToString('mm\:ss'))"
    Write-Hint "Nodes $($stats.Nodes)   Edges $($stats.Edges)   Size $($stats.Size)"
    Write-Log "Extraction complete: $($stats.Nodes) nodes, $($stats.Edges) edges"
    return $true
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

function Invoke-GraphifyExport {
    Write-Section "HTML export"
    Write-Info "Generating interactive visualization..."
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "export html --graph graphify-out/graph.json" -TimeoutSeconds 60
    if (-not $result.Success) {
        Write-Fail "HTML export failed"
        Write-Hint (Get-Truncated $result.Output 200)
        return $false
    }
    if (-not (Test-Path (Join-Path $PWD "graphify-out/graph.html") -PathType Leaf)) { Write-Fail "HTML file not generated"; return $false }
    Write-Success "graphify-out\graph.html"
    return $true
}

function Show-GraphResult {
    Write-Section "Graph ready"
    Write-Success "Interactive map generated"
    Write-Hint ("file:///" + (Get-Location).Path.Replace('\', '/') + "/graphify-out/graph.html")
    if (Read-YesNo "Open the graph now?" $false) { Start-Process "graphify-out\graph.html" }
}

# ============================================================================
# CLAUDE LAUNCH
# ============================================================================

function Start-ClaudeSession {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$ClaudePath)
    Write-Section "Launch Claude"
    Write-Info "Starting Claude Code..."
    Write-Log "Launching Claude: $ClaudePath in $($PWD.Path)"
    try { & $ClaudePath; Write-Success "Claude session ended" }
    catch { Write-Warning "Claude exited with error: $_"; Write-Log "Claude exit error: $_" -Level "ERROR" }
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
        # A version-string check must NOT be fatal: graphify is already present,
        # and some builds return non-zero or don't support --version.
        if (-not (Test-GraphifyVersion)) { Write-Warning "Could not verify Graphify version (continuing)" }
        Update-GraphifyIfNeeded

        $claudePath = Find-ClaudeExecutable
        Test-ClaudeExecutable -Path $claudePath

        if (Initialize-Pxpipe) {
            if (-not (Start-PxpipeProxy -WorkingDirectory $script:Config.PxpipeDirectory)) { Write-Warning "Continuing without proxy" }
        }

        # Project selection loop
        $projectPath = $null
        while ($true) {
            $projectPath = Read-ProjectPath
            if ($null -eq $projectPath) {
                if (Read-YesNo "Exit launcher?" $false) { Write-Info "Exiting by request"; exit 0 }
                continue
            }
            if (Test-ProjectDirectory -Path $projectPath) { Add-ProjectToHistory -Path $projectPath; break }
        }
        Set-Location $projectPath
        Write-Log "Working directory: $projectPath"

        Write-Section "Graphify setup"
        Install-GraphifyPlatform
        Install-GraphifyHook
        Initialize-GraphifyIgnore

        if (-not (Invoke-GraphifyExtract)) { Stop-Script -Code 106 -Reason "Extraction failed - aborting" }
        if (-not (Invoke-GraphifyExport)) { Write-Warning "Export failed - continuing" }
        Show-GraphResult

        Write-Host ""
        if ((Read-Host "  Press Enter to launch Claude, or X to exit") -match "^[Xx]") {
            Write-Info "Exiting without launching Claude"; exit 0
        }
        Start-ClaudeSession -ClaudePath $claudePath

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
