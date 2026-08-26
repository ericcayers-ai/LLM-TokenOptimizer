#Requires -Version 5.1
<#
.SYNOPSIS
  Points a local Hermes Agent install at TokenOptimizer-managed local model
  engines (FreeToken today) via Hermes' own custom-endpoint contract.

.DESCRIPTION
  Hermes Agent consumes arbitrary OpenAI/Anthropic-compatible endpoints through
  its `model.provider: custom` config shape (verified against hermes-agent
  source, August 2026):

      model:
        provider: custom
        default: <model id>
        base_url: <url>
        api_key: ${HERMES_CUSTOM_<identity>_API_KEY}   # only when a key is set
        api_mode: anthropic_messages                    # REQUIRED for bare-host
                                                        # Anthropic-native upstreams

  A bare host:port base_url defaults to the OpenAI chat-completions transport;
  FreeToken serves Anthropic /v1/messages natively on 127.0.0.1:1919, so the
  explicit api_mode is what makes the wiring correct rather than lucky. This
  script writes ONLY via `hermes config set` (never hand-edits config.yaml -
  a stray indent corrupts a live gateway), and stores any key in Hermes' .env,
  never in config.yaml.

  What it does, in order (idempotent - safe to re-run):
    1. Locate `hermes` (PATH, then %LOCALAPPDATA%\hermes\hermes-agent\venv\Scripts).
    2. Probe FreeToken's /v1/models on 127.0.0.1:1919; warn (not fail) if idle -
       you can still wire the config now and load a model later.
    3. `hermes config set model.provider custom`
       `hermes config set model.default <model>`
       `hermes config set model.base_url http://127.0.0.1:1919`
       `hermes config set model.api_mode anthropic_messages`
    4. Print verification commands (`hermes config get ...`, `hermes chat -z ...`).

  Scope guard: this never touches your default Hermes profile beyond the four
  `model.*` keys above, and never starts/stops the FreeToken desktop app.

.PARAMETER Model
  Model id to pin as model.default. Default: first id reported by /v1/models.

.PARAMETER BaseUrl
  Upstream base URL. Default: http://127.0.0.1:1919 (FreeToken).

.EXAMPLE
  .\Setup-HermesIntegration.ps1                 # wire Hermes to FreeToken defaults
  .\Setup-HermesIntegration.ps1 -Model GLM-5.2  # pin a specific loaded model
#>
[CmdletBinding()]
param(
    [string]$Model,
    [string]$BaseUrl = "http://127.0.0.1:1919"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) { Write-Host "[tokenoptimizer-hermes] $Message" }

# --- 1. Locate hermes -------------------------------------------------------
$hermes = Get-Command hermes -ErrorAction SilentlyContinue
if ($hermes) {
    $hermesExe = $hermes.Source
} else {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "hermes\hermes-agent\venv\Scripts\hermes.exe"),
        (Join-Path $env:LOCALAPPDATA "hermes\venv\Scripts\hermes.exe")
    )
    $hermesExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $hermesExe) {
    Write-Error "Hermes Agent CLI not found. Install it first: curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash"
    exit 1
}
Write-Step "Found hermes at $hermesExe"

# --- 2. Resolve the model id ------------------------------------------------
if (-not $Model) {
    try {
        $modelsResp = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/v1/models" -TimeoutSec 5
        $Model = @($modelsResp.data) | Select-Object -First 1 -ExpandProperty id
    } catch {
        $Model = $null
    }
    if (-not $Model) {
        Write-Warning "No model reported by $BaseUrl/v1/models (server idle or unreachable). Config will still be written; load a model in the FreeToken window before running a Hermes session."
        $Model = Read-Host "Enter a model id to pin (blank to skip model.default)"
    }
}
Write-Step "Using model: $(if ($Model) { $Model } else { '(none pinned)' })"

# --- 3. Write the four keys via hermes config set ---------------------------
Write-Step "Setting model.provider=custom"
& $hermesExe config set model.provider custom
if ($LASTEXITCODE -ne 0) { Write-Error "hermes config set model.provider failed"; exit 1 }

if ($Model) {
    Write-Step "Setting model.default=$Model"
    & $hermesExe config set model.default $Model
    if ($LASTEXITCODE -ne 0) { Write-Error "hermes config set model.default failed"; exit 1 }
}

Write-Step "Setting model.base_url=$BaseUrl"
& $hermesExe config set model.base_url $BaseUrl
if ($LASTEXITCODE -ne 0) { Write-Error "hermes config set model.base_url failed"; exit 1 }

Write-Step "Setting model.api_mode=anthropic_messages"
& $hermesExe config set model.api_mode anthropic_messages
if ($LASTEXITCODE -ne 0) { Write-Error "hermes config set model.api_mode failed"; exit 1 }

# --- 4. Verify + hand off ---------------------------------------------------
Write-Step "Done. Verify with:"
Write-Host "  hermes config get model.provider"
Write-Host "  hermes config get model.base_url"
Write-Host "  hermes config get model.api_mode"
Write-Host ""
Write-Host "Then run one real round-trip:"
Write-Host '  hermes -z "Reply with exactly: PONG"'
