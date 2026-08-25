#Requires -Version 5.1
<#
.SYNOPSIS
  End-to-end smoke test for the TokenOptimizer OpenSandbox substrate.

.DESCRIPTION
  Ordered steps - each prints a "[step] message" line and any failure aborts
  the script non-zero immediately:

    1. Preflight via the repo-built TokenOptimizer.App CLI `sandbox-status` verb.
    2. Ensure opensandbox-server is running (start it when missing).
    3. Export the AgentCompanion image definition (`image-export`) + docker build.
    4. `smoke-run`: create sandbox from that image, exec `claude --version`, kill.
    5. Best-effort cleanup of leftover containers/temp dirs.

  STEP-1 DESIGN CHOICE (documented per task brief): preflight runs through the
  app's own `sandbox-status` CliHost verb instead of raw `docker info` + a
  hand-rolled HTTP health probe. Rationale: this script already depends on the
  same repo-built binary for `image-export` and `smoke-run`, so reusing it for
  preflight means one build, one source of truth for SandboxSettings (domain,
  agent image) and preflight semantics - no PowerShell-side probe logic that
  could drift from what production code actually runs.

.PARAMETER Configuration
  Build configuration used for the CLI (default Release).

.PARAMETER ImageTag
  Tag applied to the companion image built in step 3 and consumed by smoke-run.

.EXAMPLE
  .\scripts\e2e-sandbox-smoke.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$ImageTag = "tokenoptimizer/agent-companion:e2e"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "app\src\TokenOptimizer.App\TokenOptimizer.App.csproj"
$cliExe = Join-Path $repoRoot "app\src\TokenOptimizer.App\bin\$Configuration\net10.0\TokenOptimizer.App.exe"

function Write-Step([string]$Message) { Write-Host "[step] $Message" }

function Abort([string]$Message) {
    Write-Host "[step] FAIL: $Message"
    exit 1
}

function Invoke-Cli([string]$Exe, [string[]]$CliArgs) {
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $quoted = ($CliArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
        $proc = Start-Process -FilePath $Exe -ArgumentList $quoted -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        [pscustomobject]@{
            exit   = $proc.ExitCode
            stdout = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
            stderr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        }
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-CliJson([string]$Exe, [string[]]$CliArgs) {
    $result = Invoke-Cli -Exe $Exe -CliArgs $CliArgs
    $text = "$($result.stdout)".Trim()
    if (-not $text) { Abort "CLI produced no output ($($CliArgs -join ' ')); stderr: $($result.stderr)" }
    try { $json = $text | ConvertFrom-Json }
    catch { Abort "CLI output was not JSON ($($CliArgs -join ' ')): $text" }
    [pscustomobject]@{ exit = $result.exit; json = $json }
}

Write-Step "0/5 building CLI (dotnet build $Configuration)"
dotnet build $appProject -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Abort "dotnet build failed" }
if (-not (Test-Path $cliExe)) { Abort "CLI not found at $cliExe" }

Write-Step "1/5 preflight via sandbox-status"
$pre = Invoke-CliJson -Exe $cliExe -CliArgs @("sandbox-status")
$status = $pre.json
if (-not $status.dockerUp) {
    Abort "Docker is not running (dockerUp=false). Start Docker Desktop and retry."
}
if ($status.ok) {
    Write-Step "preflight ok: docker up, opensandbox-server healthy at $($status.domain)"
}
else {
    Write-Step "preflight incomplete, will attempt server startup in step 2 (missing: $($status.missing -join ', '))"
}

Write-Step "2/5 ensure opensandbox-server running"
if ($status.serverUp) {
    Write-Step "opensandbox-server already up at $($status.domain)"
}
else {
    $configPath = Join-Path $env:USERPROFILE ".sandbox.toml"
    if (-not (Test-Path $configPath)) {
        Write-Step "writing example config to $configPath"
        uvx opensandbox-server init-config $configPath --example docker
        if ($LASTEXITCODE -ne 0) { Abort "uvx opensandbox-server init-config failed" }
    }
    Write-Step "starting uvx opensandbox-server --config $configPath"
    Start-Process -FilePath "uvx" -ArgumentList "opensandbox-server", "--config", "`"$configPath`"" `
        -WorkingDirectory $env:USERPROFILE -WindowStyle Hidden
    $healthUri = "http://$($status.domain)/health"
    $up = $false
    foreach ($attempt in 1..60) {
        try {
            $response = Invoke-WebRequest -Uri $healthUri -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { $up = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $up) { Abort "opensandbox-server did not become healthy at $healthUri within ~120s" }
    Write-Step "opensandbox-server healthy at $healthUri"
}

Write-Step "3/5 export AgentCompanion image definition and docker build"
$imageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("tokenoptimizer-e2e-image-" + [guid]::NewGuid().ToString("N"))
$export = Invoke-CliJson -Exe $cliExe -CliArgs @("image-export", "--out", $imageDir)
if (-not $export.json.ok) { Abort "image-export failed: $($export.json.error)" }
Write-Step "exported Dockerfile + entrypoint.sh to $($export.json.data.dir)"
docker build -t $ImageTag $export.json.data.dir
if ($LASTEXITCODE -ne 0) { Abort "docker build failed for ${ImageTag}" }

Write-Step "4/5 smoke-run: claude --version inside a sandbox from ${ImageTag}"
$smoke = Invoke-CliJson -Exe $cliExe -CliArgs @("smoke-run", "--image", $ImageTag)
Write-Step ("smoke-run result: " + ($smoke.json | ConvertTo-Json -Compress))
if (-not $smoke.json.pass) { Abort "smoke-run failed: $($smoke.json.detail)" }

Write-Step "5/5 cleanup (best-effort)"
try {
    $leftovers = @(docker ps -q --filter "ancestor=${ImageTag}" 2>$null)
    if ($leftovers.Count -gt 0) {
        foreach ($id in $leftovers) { docker rm -f $id | Out-Null }
        Write-Step "removed leftover containers: $($leftovers -join ', ')"
    }
    else {
        Write-Step "no leftover containers for ${ImageTag}"
    }
} catch {
    Write-Step "container cleanup skipped: $($_.Exception.Message)"
}
Remove-Item $imageDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[step] PASS: e2e sandbox smoke complete"
exit 0
