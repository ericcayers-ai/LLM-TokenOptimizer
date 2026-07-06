# LLM-TokenOptimizer

A self-bootstrapping, production-quality PowerShell launcher designed to index local codebases, manage proxy routing, and start the Claude CLI without absolute paths or environment mismatches. It automatically detects and installs missing dependencies, handles project history, and ensures complete resource cleanup on exit.

## Functionality

* **Single Instance Mutex Guard:** Uses a global .NET Mutex (`Global\GraphifyClaudeLauncherMutex_v2`) scoped to the current user to instantly terminate duplicate windows and prevent multiple concurrent instances or environment conflicts.
* **Intelligent Dependency Detection:** Dynamically locates Git, Node.js, NPM, Python, pip, and Claude CLI across system PATH, Program Files, LocalAppData, Scoop, Chocolatey, and Winget installations.
* **Automatic Graphify Installation:** If the Graphify CLI is missing from the system, it is automatically fetched and installed via `pip install graphify`, with post-installation verification.
* **Smart Claude Resolution:** Searches for `claude.exe` using PATH resolution, common directory heuristics, and Windows Registry queries. Falls back to a native file picker dialog for manual selection if automated detection fails, then remembers the chosen path for future runs.
* **Pxpipe Proxy Lifecycle:** Checks if port 47821 is active via `Get-NetTCPConnection` (with `netstat` fallback). If the port is free, it clones/updates the Pxpipe repository, runs `npm install` only if `package-lock.json` has changed, and starts the proxy in an isolated window. Reuses the session if the port is already listening. Works best for Fable 5.
* **JSON Configuration & History:** Replaces temporary text files with a persistent JSON config stored in `%LOCALAPPDATA%\GraphifyLauncher\config.json`. Tracks proxy preferences, Claude paths, project history (up to 20 entries), and auto-update preferences.
* **Interactive Directory Selection:** Utilizes an embedded PowerShell loop to handle folder drag-and-drop operations and maintain a path history buffer navigable via Up/Down arrow keys. Supports the Delete key to remove entries from history.
* **Project Validation:** Verifies that the selected directory exists, is readable and writable, is not a root drive partition, and is not empty before proceeding.
* **Versioned Ignore Matrix:** Injects a versioned template (`GRAPHIFY_IGNORE_TEMPLATE_V4`) into `.graphifyignore` to skip build artifacts, binaries, and log files. Automatically upgrades older templates while preserving user-defined custom rules.
* **Graphify Pipeline Management:** Checks global gate files to skip redundant platform registrations and hook installations. Retries corrupted or failed hook installations automatically.
* **Visual Export and Abort Option:** Runs the structural layout extraction, tracks extraction time and node/edge statistics, compiles an interactive HTML map, and outputs a browser-compatible link. Allows pressing Enter to launch Claude or entering X to gracefully shut down the proxy and release the port.
* **Comprehensive Logging:** Logs all operations (startup, dependency detection, Git/NPM/Graphify commands, proxy lifecycle, and exceptions) with millisecond timestamps to `%LOCALAPPDATA%\GraphifyLauncher\logs\`, with automatic log rotation.
* **Guaranteed Teardown Sequence:** Uses `try/catch/finally` blocks and PowerShell engine events to ensure resources are always released—even on unexpected crashes or Ctrl+C interrupts.

## Requirements

The following core dependencies must be available globally in the system environment (the launcher will halt and provide download links if they are missing):
1. **Git**
2. **Node.js and NPM**
3. **Python and pip**

The following dependencies are managed automatically by the launcher:
4. **Graphify CLI** (`graphify`) — Auto-installed via pip if missing.
5. **Claude CLI** (`claude`) — Detected automatically or selected via file dialog.

## Usage Instructions

1. **Run the script:**
   ```powershell
   .\LLM-TokenOptimizer.ps1
   ```

2. **Optional Flags:**
   * `-VerboseMode`: Outputs detailed debug logs to the console.
   * `-ForceUpdate`: Forces Pxpipe and Graphify to update to their latest versions.
   * `-SkipProxy`: Bypasses the Pxpipe proxy setup entirely.
   * `-ResetConfig`: Deletes the saved JSON configuration and starts fresh.

3. **Select Proxy Option (First Run Only):**
   Choose whether to activate the networking layer for the current project:
   ```text
   Enable Pxpipe Proxy? [Y/N]:
   ```

4. **Specify Installation Path (First Run Only, if Proxy Enabled):**
   Provide a directory for the Pxpipe repository to be cloned into:
   ```text
   Installation path: D:\Tools\Pxpipe
   ```

5. **Select Directory:**
   Drag and drop the target folder into the window or use the Up and Down arrow keys to cycle through recent paths. Press Delete to remove a path from history.

6. **Open the Graph Map:**
   Ctrl + Click the generated file URI to view the structural network map inside a web browser:
   ```text
   file:///C:/Users/Profile/Workspace/graphify-out/graph.html
   ```

7. **Select Next Action:**
   * Press **Enter** to open the Claude CLI terminal environment.
   * Type **X** and press Enter to cancel and initiate the teardown sequence.

## Teardown Sequence

When exiting Claude or choosing to abort early via the X option, the script executes the following guaranteed cleanup steps:
* Sends a graceful close window request to processes titled `PxpipeProxyWindow`.
* Waits 5 seconds for graceful shutdown, then force-terminates if the process is still active.
* Scans port 47821 using `Get-NetTCPConnection` and force-terminates any orphaned processes still holding the port.
* Releases the global .NET Mutex.
* Saves the final state to the JSON configuration file.
* Flushes and closes all application logs.
