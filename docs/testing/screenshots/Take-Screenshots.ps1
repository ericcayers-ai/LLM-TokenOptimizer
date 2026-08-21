# Take-Screenshots.ps1 — Launch TokenOptimizer and capture UI screenshots
# Usage: .\Take-Screenshots.ps1 [-WaitMs 3000] [-NoLaunch]

param(
    [string]$AppPath = "app\publish\app\TokenOptimizer.App.exe",
    [string]$OutputDir = "docs\testing\screenshots\captures",
    [int]$WaitMs = 3000,
    [switch]$NoLaunch,
    [switch]$EveryPage
)

$root = & git rev-parse --show-toplevel

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$fullOutputDir = Join-Path $root $OutputDir
New-Item -ItemType Directory -Force -Path $fullOutputDir | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

Write-Output "=== TokenOptimizer UI Screenshot Capture ==="
Write-Output "Output: $fullOutputDir"

# Launch or find the app
if ($NoLaunch) {
    $proc = Get-Process | Where-Object { $_.MainWindowTitle -match "TokenOptimizer|LLM Token" } | Select-Object -First 1
    if (-not $proc) {
        Write-Warning "No TokenOptimizer window found. Launch manually and re-run with -NoLaunch."
        return
    }
} else {
    $exePath = Join-Path $root $AppPath
    if (-not (Test-Path $exePath)) {
        Write-Warning "App not found at $exePath"
        return
    }
    Write-Output "Launching app..."
    $proc = Start-Process -FilePath $exePath -PassThru
    Write-Output "Waiting ${WaitMs}ms for render..."
    Start-Sleep -Milliseconds $WaitMs
}

$hwnd = $proc.MainWindowHandle
Write-Output "Window handle: $hwnd"

# Take full-screen capture (simplest, most reliable)
$screenshotPath = Join-Path $fullOutputDir "main_window_${timestamp}.png"
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$bitmap = New-Object System.Drawing.Bitmap($screen.Bounds.Width, $screen.Bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($screen.Bounds.Location, [System.Drawing.Point]::Empty, $screen.Bounds.Size)
$bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
Write-Output "Saved: $screenshotPath"

# Interactive page-by-page mode
if ($EveryPage) {
    Write-Output ""
    Write-Output "=== Interactive Page Capture ==="
    Write-Output "Navigate to each page, then press ENTER to capture. Type 'q' to quit."
    Write-Output ""

    @("main_overview","models_card","providers_card","companion_tooling",
      "settings","danger_zone","agency_agents","cli_tab") | ForEach-Object {
        Write-Host "Navigate to: $_" -ForegroundColor Cyan
        $ans = Read-Host "ENTER to capture, q to quit"
        if ($ans -eq 'q') { return }

        $p = Join-Path $fullOutputDir "${_}_${timestamp}.png"
        $bm = New-Object System.Drawing.Bitmap($screen.Bounds.Width, $screen.Bounds.Height)
        $gr = [System.Drawing.Graphics]::FromImage($bm)
        $gr.CopyFromScreen($screen.Bounds.Location, [System.Drawing.Point]::Empty, $screen.Bounds.Size)
        $bm.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
        $gr.Dispose()
        $bm.Dispose()
        Write-Output "Saved: $p"
    }
}

Write-Output ""
Write-Output "=== Done ==="
Get-ChildItem $fullOutputDir -Filter "*.png" | Sort-Object LastWriteTime -Descending |
    Format-Table Name, @{N='Size(KB)';E={[math]::Round($_.Length/1KB, 1)}}, LastWriteTime -AutoSize
