#requires -version 5.1
<#
.SYNOPSIS
    Builds TokenOptimizer.msi: publishes the app as a self-contained
    single-file win-x64 build, packages the VS Code extension as a .vsix,
    then builds the MSI that installs both.

.NOTES
    One-time setup this script assumes is already done:
      dotnet tool install --global wix --version 5.0.2
      wix extension add WixToolset.UI.wixext/5.0.2 WixToolset.Util.wixext/5.0.2   (run once, from this installer/ folder)
    (WiX v6+ requires accepting a paid-tier EULA for some usage; v5.0.2 predates that and is free.)
#>
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$installerDir = $PSScriptRoot
$publishDir = Join-Path $root "publish\app"
$vsixDir = Join-Path $root "publish"

Write-Host "==> Publishing TokenOptimizer.App (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\TokenOptimizer.App") -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Packaging the VS Code extension (.vsix)..." -ForegroundColor Cyan
Push-Location (Join-Path $root "..\vscode-extension")
try {
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw "npm run compile failed" }
    npx --yes @vscode/vsce package --allow-missing-repository --skip-license -o (Join-Path $vsixDir "llm-token-optimizer.vsix")
    if ($LASTEXITCODE -ne 0) { throw "vsce package failed" }
} finally {
    Pop-Location
}

Write-Host "==> Building TokenOptimizer.msi..." -ForegroundColor Cyan
Push-Location $installerDir
try {
    wix build Product.wxs -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -d PublishDir="$publishDir" -d VsixDir="$vsixDir" `
        -arch x64 -o TokenOptimizer.msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
} finally {
    Pop-Location
}

Write-Host "==> Done: $(Join-Path $installerDir 'TokenOptimizer.msi')" -ForegroundColor Green
