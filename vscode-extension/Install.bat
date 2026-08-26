@echo off
setlocal enabledelayedexpansion
title LLM-TokenOptimizer - Install
cd /d "%~dp0"

echo.
echo   LLM-TokenOptimizer - VS Code extension installer
echo   ================================================
echo.

rem Double-clicking the .vsix directly opens Visual Studio's installer
rem instead of VS Code's (both use the .vsix extension for unrelated
rem formats) - this script always goes through the correct "code" CLI
rem instead, so double-clicking THIS file is the one-click install path.

rem Don't hardcode the version - it goes stale every time the extension is
rem repackaged (this bit us: v5.4.0 was baked in here while the actual file
rem on disk had moved on to v5.8.0). Instead, pick the most recently built
rem llm-token-optimizer-*.vsix sitting next to this installer - or, failing
rem that, the one build-installer.ps1 drops into app\publish\.
set "VSIX="
for /f "delims=" %%F in ('dir /b /o-d "%~dp0llm-token-optimizer-*.vsix" 2^>nul') do (
    if not defined VSIX set "VSIX=%~dp0%%F"
)
if not defined VSIX (
    for /f "delims=" %%F in ('dir /b /o-d "%~dp0..\app\publish\llm-token-optimizer-*.vsix" 2^>nul') do (
        if not defined VSIX set "VSIX=%~dp0..\app\publish\%%F"
    )
)
if not defined VSIX (
    echo   ERROR: No llm-token-optimizer-*.vsix found next to this installer
    echo   or under ..app\publish\. Run build-installer.ps1 (or npx @vscode/vsce package).
    echo.
    pause
    exit /b 1
)

rem IMPORTANT: target "code.cmd" specifically, never the bare "code" name.
rem VS Code's own install puts BOTH an extensionless "code" (a Unix/git-bash
rem style shim, not a real Windows executable) and "code.cmd" (the real
rem Windows launcher) in the same PATH directory. `where code` matches both,
rem and cmd.exe can end up trying to run the extensionless one first, which
rem fails with a garbled relative-path error. Always resolving to the exact
rem "code.cmd" name sidesteps that ambiguity entirely - verified by testing.
for /f "delims=" %%F in ('where code.cmd 2^>nul') do (
    set "CODE_CMD=%%F"
    goto :install
)

rem Not on PATH - try the common install locations directly.
set "CANDIDATE1=%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd"
set "CANDIDATE2=%ProgramFiles%\Microsoft VS Code\bin\code.cmd"
if exist "%CANDIDATE1%" (
    set "CODE_CMD=%CANDIDATE1%"
    goto :install
)
if exist "%CANDIDATE2%" (
    set "CODE_CMD=%CANDIDATE2%"
    goto :install
)

echo   ERROR: Could not find VS Code's "code.cmd" launcher on PATH or in the
echo   usual install locations. Install VS Code from https://code.visualstudio.com/,
echo   or during its install check "Add to PATH", then run this file again.
echo.
pause
exit /b 1

:install
echo   Installing with: "%CODE_CMD%"
echo.
"%CODE_CMD%" --install-extension "%VSIX%"
if %errorlevel% neq 0 (
    echo.
    echo   Install command reported an error - see above.
    pause
    exit /b 1
)

echo.
echo   Done. Open (or restart) VS Code and look for the rocket icon
echo   in the left Activity Bar, or the "TokenOptimizer" status bar item.
echo.
pause
