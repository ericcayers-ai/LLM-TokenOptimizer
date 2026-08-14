import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { spawn } from 'child_process';
import { openDashboard } from './dashboard';

// This extension is deliberately a THIN WRAPPER, not a reimplementation.
// Every command below just builds a `powershell.exe -File <script> <args>`
// command line and sends it to a VS Code integrated terminal. All the real
// logic (Graphify, companion tooling incl. Caveman + RTK, the rate-limit
// watcher, multi-session support) stays in LLM-TokenOptimizer.ps1, unchanged
// and already verified there - this extension only gives it a VS Code-native
// front door (commands, a status bar entry, native folder pickers) instead
// of a standalone console window you have to launch by hand.

const TERMINAL_NAME = 'LLM-TokenOptimizer';
const LAST_MASTER_FOLDER_KEY = 'llmTokenOptimizer.lastMasterFolder';

function psQuote(value: string): string {
    // PowerShell double-quoted string: wrap in quotes, escape embedded quotes
    // with a backtick. Covers the common case (paths with spaces) the same
    // way the wrapped script quotes its own spawned-window arguments.
    return `"${value.replace(/"/g, '`"')}"`;
}

function resolveScriptPath(context: vscode.ExtensionContext): string | undefined {
    const configured = vscode.workspace.getConfiguration('llmTokenOptimizer').get<string>('scriptPath', '').trim();
    const candidate = configured || path.join(context.extensionPath, 'scripts', 'LLM-TokenOptimizer.ps1');
    if (!fs.existsSync(candidate)) {
        vscode.window.showErrorMessage(
            `LLM-TokenOptimizer: script not found at "${candidate}". Check the "llmTokenOptimizer.scriptPath" setting.`
        );
        return undefined;
    }
    return candidate;
}

function resolveAppExecutablePath(context: vscode.ExtensionContext): string | undefined {
    const configured = vscode.workspace.getConfiguration('llmTokenOptimizer').get<string>('appExecutablePath', '').trim();
    if (configured) {
        return fs.existsSync(configured) ? configured : undefined;
    }
    // Auto-detect a local build of the new C# app: it lives as a sibling
    // "app/" folder next to this extension's own repo root (extensionPath
    // is .../LLM-TokenOptimizer/vscode-extension), Debug preferred since
    // that's what `dotnet build` produces by default during development.
    const repoRoot = path.dirname(context.extensionPath);
    const candidates = [
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Debug', 'net10.0', 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Release', 'net10.0', 'TokenOptimizer.App.exe'),
    ];
    return candidates.find(fs.existsSync);
}

function launchApp(context: vscode.ExtensionContext): void {
    const exePath = resolveAppExecutablePath(context);
    if (!exePath) {
        vscode.window.showErrorMessage(
            'LLM-TokenOptimizer: TokenOptimizer.App.exe not found. Build it (`dotnet build` under app/) or set "llmTokenOptimizer.appExecutablePath".'
        );
        return;
    }

    const folders = vscode.workspace.workspaceFolders;
    const args = folders && folders.length > 0 ? [folders[0].uri.fsPath] : [];

    const child = spawn(exePath, args, { detached: true, stdio: 'ignore' });
    child.unref();
}

function commonArgs(): string[] {
    const cfg = vscode.workspace.getConfiguration('llmTokenOptimizer');
    const args: string[] = [];
    const model = cfg.get<string>('model', '').trim();
    if (model) { args.push('-Model', model); }
    if (cfg.get<boolean>('isolateClaudeConfig', false)) { args.push('-IsolateClaudeConfig'); }
    if (cfg.get<boolean>('verboseMode', false)) { args.push('-VerboseMode'); }
    return args;
}

function getOrCreateTerminal(): vscode.Terminal {
    const existing = vscode.window.terminals.find(t => t.name === TERMINAL_NAME && t.exitStatus === undefined);
    if (existing) { return existing; }
    return vscode.window.createTerminal({ name: TERMINAL_NAME });
}

function runScript(context: vscode.ExtensionContext, args: string[], cwd?: string): void {
    const scriptPath = resolveScriptPath(context);
    if (!scriptPath) { return; }
    const psExe = vscode.workspace.getConfiguration('llmTokenOptimizer').get<string>('powershellExecutable', 'powershell.exe');

    const parts = [
        psExe,
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', psQuote(scriptPath),
        ...args
    ];
    const commandLine = parts.join(' ');

    const terminal = getOrCreateTerminal();
    terminal.show();
    if (cwd) {
        terminal.sendText(`Set-Location ${psQuote(cwd)}`);
    }
    terminal.sendText(commandLine);
}

async function transferSession(
    context: vscode.ExtensionContext,
    flag: '-TransferTo' | '-ContinueLocally',
    target: 'Codex' | 'Cursor' | undefined,
    targetLabel: string
): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        vscode.window.showErrorMessage('LLM-TokenOptimizer: open a folder or workspace first.');
        return;
    }
    const projectPath = folders[0].uri.fsPath;

    const confirmed = await vscode.window.showWarningMessage(
        `Stop Claude Code and continue in ${targetLabel}? Session context and this project's skills (as reference material) will be handed off, but this is a best-effort bridge, not a full session migration.`,
        { modal: true },
        'Transfer Session'
    );
    if (confirmed !== 'Transfer Session') { return; }

    const terminal = getOrCreateTerminal();
    terminal.show();
    // Ctrl+C to whatever's currently running in the managed terminal (only
    // reaches a Claude Code session that was itself launched through this
    // extension's terminal - see runScript/getOrCreateTerminal. A session
    // running in a terminal this extension doesn't control can't be
    // stopped from here; the user would need to Ctrl+C it themselves first).
    terminal.sendText('\x03', false);

    const args = flag === '-TransferTo'
        ? ['-TransferTo', target as string, '-ProjectPath', psQuote(projectPath)]
        : ['-ContinueLocally', '-ProjectPath', psQuote(projectPath)];

    // Small delay so the interrupt actually lands before the next command is
    // queued - sendText immediately after Ctrl+C can race the shell's own
    // handling of the signal.
    setTimeout(() => runScript(context, args, projectPath), 500);
}

async function pickMasterFolder(context: vscode.ExtensionContext): Promise<string | undefined> {
    const picked = await vscode.window.showOpenDialog({
        canSelectFiles: false,
        canSelectFolders: true,
        canSelectMany: false,
        openLabel: 'Use as Master Folder'
    });
    if (!picked || picked.length === 0) { return undefined; }
    const folder = picked[0].fsPath;
    await context.globalState.update(LAST_MASTER_FOLDER_KEY, folder);
    return folder;
}

// Single source of truth for "every function this extension exposes" - used
// by the Start quick-pick, the Activity Bar tree view, AND the chat
// participant, so all three surfaces stay in sync and nothing added to one
// silently goes missing from the others.
interface ActionEntry {
    id: string;
    label: string;
    themeIcon: string;
    description: string;
}

const ACTIONS: ActionEntry[] = [
    {
        id: 'llmTokenOptimizer.openCurrentWorkspace',
        label: 'Open Current Workspace as Project',
        themeIcon: 'folder-opened',
        description: 'Runs Graphify/OmniRoute setup and launches (or resumes) a Claude Code session for the folder open right now.'
    },
    {
        id: 'llmTokenOptimizer.openLauncherForFolder',
        label: 'Open Launcher (Master Folder Picker)',
        themeIcon: 'list-selection',
        description: 'Pick which project subfolders under a master folder to open, one independent window each.'
    },
    {
        id: 'llmTokenOptimizer.changeMasterFolder',
        label: 'Change Master Folder',
        themeIcon: 'folder',
        description: 'Choose a different parent folder of projects for the launcher to list.'
    },
    {
        id: 'llmTokenOptimizer.resetConfig',
        label: 'Reset Configuration',
        themeIcon: 'trash',
        description: 'Forget everything saved: master folder and project history.'
    },
    {
        id: 'llmTokenOptimizer.setupProxy',
        label: 'Set Up Proxy Fallback (Antigravity → Codex → Cursor)',
        themeIcon: 'key',
        description: 'Register credentials for backup coding agents. Only Antigravity auto-activates when Claude Code hits a usage limit - Codex and Cursor are manual transfer only (see below).'
    },
    {
        id: 'llmTokenOptimizer.transferToCodex',
        label: 'Transfer Session to Codex',
        themeIcon: 'arrow-swap',
        description: 'Stop Claude Code and continue in Codex, carrying over this session\'s context and this project\'s skills as reference material.'
    },
    {
        id: 'llmTokenOptimizer.transferToCursor',
        label: 'Transfer Session to Cursor',
        themeIcon: 'arrow-swap',
        description: 'Stop Claude Code and continue in Cursor, carrying over this session\'s context and this project\'s skills as reference material.'
    },
    {
        id: 'llmTokenOptimizer.continueLocally',
        label: 'Continue Locally',
        themeIcon: 'arrow-swap',
        description: 'Stop Claude Code and continue with the best benchmarked local model - no credential needed.'
    },
    {
        id: 'llmTokenOptimizer.openDashboard',
        label: 'Open Dashboard',
        themeIcon: 'graph-line',
        description: 'Live panel: RTK token-savings stats and every skill/plugin this project\'s Claude Code session invokes, as it happens.'
    },
    {
        id: 'llmTokenOptimizer.openApp',
        label: 'Open TokenOptimizer App (New)',
        themeIcon: 'window',
        description: 'Launches the new C# desktop app (project picker, dependency dashboard, provider/fallback-chain launcher) with this workspace pre-selected.'
    }
];

// ----------------------------------------------------------------------------
// ACTIVITY BAR VIEW: gives every function above its own left-hand-side window
// instead of only living in the Command Palette / status bar.
// ----------------------------------------------------------------------------
class ActionTreeItem extends vscode.TreeItem {
    constructor(action: ActionEntry) {
        super(action.label, vscode.TreeItemCollapsibleState.None);
        this.description = action.description;
        this.iconPath = new vscode.ThemeIcon(action.themeIcon);
        this.command = { command: action.id, title: action.label };
        this.contextValue = 'llmTokenOptimizerAction';
    }
}

class LlmTokenOptimizerTreeProvider implements vscode.TreeDataProvider<ActionTreeItem> {
    getTreeItem(element: ActionTreeItem): vscode.TreeItem {
        return element;
    }
    getChildren(): ActionTreeItem[] {
        return ACTIONS.map(a => new ActionTreeItem(a));
    }
}

export function activate(context: vscode.ExtensionContext): void {
    const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    statusBar.text = '$(rocket) TokenOptimizer';
    statusBar.tooltip = 'LLM-TokenOptimizer: click to open the menu';
    statusBar.command = 'llmTokenOptimizer.quickMenu';
    statusBar.show();
    context.subscriptions.push(statusBar);

    context.subscriptions.push(
        vscode.commands.registerCommand('llmTokenOptimizer.openCurrentWorkspace', () => {
            const folders = vscode.workspace.workspaceFolders;
            if (!folders || folders.length === 0) {
                vscode.window.showErrorMessage('LLM-TokenOptimizer: open a folder or workspace first.');
                return;
            }
            const projectPath = folders[0].uri.fsPath;
            // -ChildWindow: same code path the parent script's own picker
            // spawns for a chosen subfolder - skips the machine-wide
            // bootstrap (winget/dependency installs, update prompts) and
            // goes straight into Invoke-ProjectMode for this one folder,
            // including the v5.0 multi-session resume-mode prompt and the
            // rate-limit watcher.
            runScript(context, ['-ProjectPath', psQuote(projectPath), '-ChildWindow', ...commonArgs()], projectPath);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.openLauncherForFolder', async () => {
            let masterFolder = context.globalState.get<string>(LAST_MASTER_FOLDER_KEY);
            if (!masterFolder || !fs.existsSync(masterFolder)) {
                masterFolder = await pickMasterFolder(context);
                if (!masterFolder) { return; }
            }
            runScript(context, ['-MasterFolder', psQuote(masterFolder), ...commonArgs()]);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.changeMasterFolder', async () => {
            const masterFolder = await pickMasterFolder(context);
            if (!masterFolder) { return; }
            const openNow = await vscode.window.showInformationMessage(
                `Master folder set to "${masterFolder}". Open the launcher now?`,
                'Open Launcher', 'Not Now'
            );
            if (openNow === 'Open Launcher') {
                runScript(context, ['-MasterFolder', psQuote(masterFolder), ...commonArgs()]);
            }
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.resetConfig', async () => {
            const confirmed = await vscode.window.showWarningMessage(
                'This forgets the saved master folder and project history. Continue?',
                { modal: true },
                'Reset Configuration'
            );
            if (confirmed !== 'Reset Configuration') { return; }
            // Intentionally NOT -ChildWindow: -ResetConfig is a no-op on a
            // child window by design (Initialize-Configuration guards it),
            // so this must run as a launcher invocation.
            runScript(context, ['-ResetConfig', ...commonArgs()]);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.setupProxy', () => {
            // -SetupProxy is an interactive, console-based credential prompt
            // (Read-Host -AsSecureString, masked input) - it runs in the
            // same integrated terminal as every other action rather than a
            // webview/input-box flow, so the key never passes through this
            // extension's own JS at all, only through the terminal directly
            // to the script's DPAPI-backed storage.
            runScript(context, ['-SetupProxy']);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.transferToCodex', () =>
            transferSession(context, '-TransferTo', 'Codex', 'Codex')),
        vscode.commands.registerCommand('llmTokenOptimizer.transferToCursor', () =>
            transferSession(context, '-TransferTo', 'Cursor', 'Cursor')),
        vscode.commands.registerCommand('llmTokenOptimizer.continueLocally', () =>
            transferSession(context, '-ContinueLocally', undefined, 'the local model')),

        vscode.commands.registerCommand('llmTokenOptimizer.openDashboard', () => openDashboard(context)),
        vscode.commands.registerCommand('llmTokenOptimizer.openApp', () => launchApp(context)),

        // The ONE Command Palette entrypoint (see contributes.menus.commandPalette
        // in package.json, which hides the other five from the palette while
        // keeping them fully invocable from here, the tree view, and the chat
        // participant below). Sourced from the same ACTIONS list the tree view
        // and chat participant use, so there's exactly one place to add a
        // function and have it show up everywhere.
        vscode.commands.registerCommand('llmTokenOptimizer.quickMenu', async () => {
            const hasWorkspace = !!(vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders.length > 0);
            type MenuItem = vscode.QuickPickItem & { command: string };
            const items: MenuItem[] = ACTIONS.map(a => ({
                label: `$(${a.themeIcon}) ${a.label}`,
                description: a.id === 'llmTokenOptimizer.openCurrentWorkspace'
                    ? (hasWorkspace ? vscode.workspace.workspaceFolders![0].uri.fsPath : 'No workspace open')
                    : a.description,
                command: a.id
            }));
            const choice = await vscode.window.showQuickPick(items, {
                placeHolder: 'LLM-TokenOptimizer - choose what to do'
            });
            if (choice) {
                await vscode.commands.executeCommand(choice.command);
            }
        })
    );

    const treeProvider = new LlmTokenOptimizerTreeProvider();
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('llmTokenOptimizerView', treeProvider)
    );

    registerChatParticipant(context);
}

// ----------------------------------------------------------------------------
// CHAT PARTICIPANT: makes every function above reachable from VS Code's own
// Chat/agent panel as "@tokenoptimizer", alongside Claude Code's own chat
// integration, instead of only living in the Command Palette or sidebar.
// ----------------------------------------------------------------------------
function registerChatParticipant(context: vscode.ExtensionContext): void {
    // vscode.chat is only present on VS Code builds that ship the Chat API
    // (stable since well before this extension's engines.vscode floor) - this
    // guard is defensive, not because it's expected to fail, matching the
    // rest of this extension's "never let an optional integration block the
    // core feature" posture.
    const chatApi = (vscode as unknown as { chat?: typeof vscode.chat }).chat;
    if (!chatApi || typeof chatApi.createChatParticipant !== 'function') {
        return;
    }

    const handler: vscode.ChatRequestHandler = async (request, _context, stream) => {
        const prompt = request.prompt.trim().toLowerCase();
        const match = (...keywords: string[]) => keywords.some(k => prompt.includes(k));

        let matched: ActionEntry | undefined;
        if (match('workspace', 'this project', 'current folder', 'here')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.openCurrentWorkspace');
        } else if (match('launcher', 'picker', 'master folder picker')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.openLauncherForFolder');
        } else if (match('change master', 'set master', 'master folder')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.changeMasterFolder');
        } else if (match('reset')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.resetConfig');
        } else if (match('transfer', 'switch to codex', 'hand off to codex')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.transferToCodex');
        } else if (match('switch to cursor', 'hand off to cursor')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.transferToCursor');
        } else if (match('continue locally', 'switch to local', 'local model')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.continueLocally');
        } else if (match('dashboard', 'token savings', 'live activity', 'skill activity')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.openDashboard');
        } else if (match('proxy', 'fallback', 'antigravity', 'codex', 'cursor', 'backup agent', 'usage limit')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.setupProxy');
        }

        if (matched) {
            stream.markdown(`Running **${matched.label}**...\n\n`);
            await vscode.commands.executeCommand(matched.id);
            stream.markdown('Started - check the LLM-TokenOptimizer terminal for progress.');
            return;
        }

        stream.markdown(
            'I run [LLM-TokenOptimizer](https://github.com) actions - Graphify setup and Claude ' +
            'Code project/session windows, all in the same terminal-based script this extension wraps. ' +
            'Tell me what to do, or pick one:\n\n'
        );
        for (const action of ACTIONS) {
            stream.markdown(`- [${action.label}](command:${action.id}) - ${action.description}\n`);
        }
    };

    const participant = chatApi.createChatParticipant('llmTokenOptimizer.agent', handler);
    participant.iconPath = vscode.Uri.joinPath(context.extensionUri, 'media', 'icon.svg');
    context.subscriptions.push(participant);
}

export function deactivate(): void {
    // Nothing to tear down - the script itself owns cleanup (config saves,
    // instance-lock release) via its own Invoke-Cleanup on process exit; this
    // extension never holds process handles across command invocations.
}
