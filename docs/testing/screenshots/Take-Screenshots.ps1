# Take-Screenshots.ps1 — Launch TokenOptimizer and capture UI screenshots
# Usage: .\Take-Screenshots.ps1 [-AppPath "path\to\exe"] [-OutputDir "path\to\output"] [-WaitMs 3000]
#
# Requires: Windows 10+, PowerShell 5.1+
# Captures the primary monitor (full screen). Position the app window
# as desired before the screenshot fires.

param(
    [string]$AppPath = "app\publish\app\TokenOptimizer.App.exe",
    [string]$OutputDir = "docs\testing\screenshots\captures",
    [int]$WaitMs = 3000,
    [switch]$NoLaunch,
    [switch]$EveryPage
)

$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- Win32 API for window enumeration ---
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Window {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT {
        public int Left; public int Top; public int Right; public int Bottom;
    }
    public static string GetTitle(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }
}
"@

function Take-ScreenCapture {
    param([string]$FilePath)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen
    $bitmap = New-Object System.Drawing.Bitmap($screen.Bounds.Width, $screen.Bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($screen.Bounds.Location, [System.Drawing.Point]::Empty, $screen.Bounds.Size)
    $bitmap.Save($FilePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Output "Saved: $FilePath"
}

function Take-WindowCapture {
    param([string]$FilePath, [IntPtr]$Hwnd)
    $rect = New-Object Win32Window+RECT
    [Win32Window]::GetWindowRect($Hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) {
        Write-Warning "Window rect invalid ($w x $h), falling back to screen capture"
        Take-ScreenCapture -FilePath $FilePath
        return
    }
    $bitmap = New-Object System.Drawing.Bitmap($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $bitmap.Save($FilePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Output "Saved: $FilePath"
}

# Create output directory
$fullOutputDir = Join-Path $root $OutputDir
New-Item -ItemType Directory -Force -Path $fullOutputDir | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

Write-Output "=== TokenOptimizer UI Screenshot Capture ==="
Write-Output "Output: $fullOutputDir"
Write-Output ""

# Launch the app if requested
if (-not $NoLaunch) {
    $appPath = Join-Path $root $AppPath
    if (-not (Test-Path $appPath)) {
        Write-Warning "App not found at $appPath — attempting to build first"
        & dotnet publish app/TokenOptimizer.slnx -c Release -r win-x64 --self-contained -o app/publish 2>&1 | Out-Null
    }

    Write-Output "Launching app..."
    $process = Start-Process -FilePath $appPath -PassThru
    Write-Output "Waiting ${WaitMs}ms for app to render..."
    Start-Sleep -Milliseconds $WaitMs
} else {
    # Find the existing window
    $process = Get-Process | Where-Object { $_.MainWindowTitle -match "TokenOptimizer" -or $_.MainWindowTitle -match "LLM Token" } | Select-Object -First 1
    if ($null -eq $process) {
        Write-Warning "No TokenOptimizer window found. Launch it manually and re-run with -NoLaunch."
        exit 1
    }
}

# Find the window handle
$hwnd = $process.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Warning "MainWindowHandle is zero — falling back to full-screen capture"
}

$title = if ($hwnd -ne [IntPtr]::Zero) { [Win32Window]::GetTitle($hwnd) } else { "unknown" }
Write-Output "Window: '$title' (handle: $hwnd)"
Write-Output ""

# Capture 1: Main window (default state)
$screenshotPath = Join-Path $fullOutputDir "main_window_${timestamp}.png"
if ($hwnd -ne [IntPtr]::Zero) {
    [Win32Window]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
    Take-WindowCapture -FilePath $screenshotPath -Hwnd $hwnd
} else {
    Take-ScreenCapture -FilePath $screenshotPath
}

# For page-by-page captures, user navigates manually
if ($EveryPage) {
    Write-Output ""
    Write-Output "=== Interactive Page Capture Mode ==="
    Write-Output "Navigate to each section and press ENTER to capture."
    Write-Output "Type 'q' + ENTER to quit."
    Write-Output ""

    $pageNames = @(
        "main_overview",
        "models_card",
        "providers_card",
        "companion_tooling",
        "settings",
        "danger_zone",
        "agency_agents",
        "cli_tab"
    )

    foreach ($page in $pageNames) {
        Write-Host "Navigate to: $page" -ForegroundColor Cyan
        $input = Read-Host "Press ENTER to capture, or 'q' to quit"
        if ($input -eq 'q') { break }

        $pagePath = Join-Path $fullOutputDir "${page}_${timestamp}.png"
        if ($hwnd -ne [IntPtr]::Zero) {
            [Win32Window]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 300
            Take-WindowCapture -FilePath $pagePath -Hwnd $hwnd
        } else {
            Take-ScreenCapture -FilePath $pagePath
        }
    }
}

Write-Output ""
Write-Output "=== Screenshots complete ==="
Write-Output "Output directory: $fullOutputDir"
Get-ChildItem $fullOutputDir -Filter "*.png" | Sort-Object LastWriteTime -Descending | Format-Table Name, @{N='Size(KB)';E={[math]::Round($_.Length/1KB, 1)}}, LastWriteTime -AutoSize
