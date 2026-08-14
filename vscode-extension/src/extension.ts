import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { execFile, spawn } from 'child_process';
import { openDashboard } from './dashboard';

// This extension is a thin front door onto TokenOptimizer.App.exe's headless
// CLI surface (`TokenOptimizer.App.exe --cli <command> [--opt value]`, one
// JSON object on stdout). ALL real logic - Graphify, companion tooling
// (Caveman/RTK/claude-mem/...), the fallback chain, provider hotswap,
// benchmarking, session launch - lives in the C# app (TokenOptimizer.Core /
// TokenOptimizer.Providers) and nowhere else, so the desktop UI and this
// extension can never drift into different behavior. There is no PowerShell
// script dependency here anymore.

const TERMINAL_NAME = 'LLM-TokenOptimizer';
const LAST_MASTER_FOLDER_KEY = 'llmTokenOptimizer.lastMasterFolder';

interface CliResult<T = unknown> {
    ok: boolean;
    data?: T;
    error?: string;
}

function resolveAppExecutablePath(context: vscode.ExtensionContext): string | undefined {
    const configured = vscode.workspace.getConfiguration('llmTokenOptimizer').get<string>('appExecutablePath', '').trim();
    if (configured) {
        return fs.existsSync(configured) ? configured : undefined;
    }
    // Auto-detect a local build of the app: it lives as a sibling "app/"
    // folder next to this extension's own repo root (extensionPath is
    // .../LLM-TokenOptimizer/vscode-extension), or next to this extension
    // itself once bundled into the MSI (see Product.wxs - both ship side by
    // side under the same INSTALLFOLDER).
    const repoRoot = path.dirname(context.extensionPath);
    const candidates = [
        path.join(context.extensionPath, 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Debug', 'net10.0', 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Release', 'net10.0', 'TokenOptimizer.App.exe'),
    ];
    return candidates.find(fs.existsSync);
}

function requireAppExecutable(context: vscode.ExtensionContext): string | undefined {
    const exePath = resolveAppExecutablePath(context);
    if (!exePath) {
        vscode.window.showErrorMessage(
            'LLM-TokenOptimizer: TokenOptimizer.App.exe not found. Build it (`dotnet build` under app/) or set "llmTokenOptimizer.appExecutablePath".'
        );
    }
    return exePath;
}

/**
 * Runs one CLI command against TokenOptimizer.App.exe and parses its single
 * JSON line of stdout. Errors are surfaced as CliResult.ok === false rather
 * than throwing, so every call site can decide how to present a failure
 * (status bar message, chat reply, etc.) without a try/catch of its own.
 */
function runCli<T = unknown>(context: vscode.ExtensionContext, args: string[]): Promise<CliResult<T>> {
    return new Promise(resolve => {
        const exePath = requireAppExecutable(context);
        if (!exePath) {
            resolve({ ok: false, error: 'TokenOptimizer.App.exe not found.' });
            return;
        }

        execFile(exePath, ['--cli', ...args], { maxBuffer: 32 * 1024 * 1024, timeout: 0 }, (err, stdout) => {
            const lastLine = stdout.trim().split('\n').pop() ?? '';
            try {
                const parsed = JSON.parse(lastLine) as CliResult<T>;
                resolve(parsed);
            } catch {
                resolve({ ok: false, error: err ? err.message : (stdout.trim() || 'No output from TokenOptimizer.App.exe --cli.') });
            }
        });
    });
}

function launchApp(context: vscode.ExtensionContext): void {
    const exePath = requireAppExecutable(context);
    if (!exePath) { return; }

    const folders = vscode.workspace.workspaceFolders;
    const args = folders && folders.length > 0 ? [folders[0].uri.fsPath] : [];

    const child = spawn(exePath, args, { detached: true, stdio: 'ignore' });
    child.unref();
}

function commonLaunchArgs(): string[] {
    const cfg = vscode.workspace.getConfiguration('llmTokenOptimizer');
    const args: string[] = [];
    const model = cfg.get<string>('model', '').trim();
    if (model) { args.push('--model', model); }
    if (cfg.get<boolean>('isolateClaudeConfig', false)) { args.push('--isolate'); }
    return args;
}

function getOrCreateTerminal(): vscode.Terminal {
    const existing = vscode.window.terminals.find(t => t.name === TERMINAL_NAME && t.exitStatus === undefined);
    if (existing) { return existing; }
    return vscode.window.createTerminal({ name: TERMINAL_NAME });
}

/** Launches a project session via the CLI and reports the result in the managed terminal, mirroring the old script's terminal-based feedback without actually running PowerShell. */
async function launchProjectSession(context: vscode.ExtensionContext, projectPath: string, extraArgs: string[] = []): Promise<void> {
    const terminal = getOrCreateTerminal();
    terminal.show();
    terminal.sendText(`# Launching TokenOptimizer session for ${projectPath} ...`, false);

    const result = await runCli(context, ['launch', '--project', projectPath, ...commonLaunchArgs(), ...extraArgs]);
    if (result.ok) {
        const data = result.data as { provider?: string; processId?: number } | undefined;
        terminal.sendText(`# Launched via ${data?.provider ?? 'provider'} (pid ${data?.processId ?? 'n/a'}).`, false);
    } else {
        terminal.sendText(`# Launch failed: ${result.error}`, false);
        vscode.window.showErrorMessage(`LLM-TokenOptimizer: launch failed - ${result.error}`);
    }
}

async function transferSession(
    context: vscode.ExtensionContext,
    provider: 'Codex' | 'Cursor' | 'LM Studio (local)',
    targetLabel: string
): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        vscode.window.showErrorMessage('LLM-TokenOptimizer: open a folder or workspace first.');
        return;
    }
    const projectPath = folders[0].uri.fsPath;

    const confirmed = await vscode.window.showWarningMessage(
        `Continue in ${targetLabel}? Session context and this project's skills (as reference material) will be handed off, but this is a best-effort bridge, not a full session migration.`,
        { modal: true },
        'Transfer Session'
    );
    if (confirmed !== 'Transfer Session') { return; }

    await launchProjectSession(context, projectPath, ['--provider', provider]);
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
    await runCli(context, ['master-folder-set', '--path', folder]);
    return folder;
}

async function setupProxyCredentials(context: vscode.ExtensionContext): Promise<void> {
    type ProxyChoice = vscode.QuickPickItem & { action: 'key' | 'opt-in'; provider: string };
    const choices: ProxyChoice[] = [
        { label: 'Antigravity', description: 'Opt in (auto-activates when Claude Code hits a usage limit)', action: 'opt-in', provider: 'Antigravity' },
        { label: 'Codex', description: 'Store OPENAI_API_KEY (manual transfer only)', action: 'key', provider: 'Codex' },
        { label: 'Cursor', description: 'Opt in (manual transfer only)', action: 'opt-in', provider: 'Cursor' },
        { label: 'Groq', description: 'Store GROQ_API_KEY (manual transfer only)', action: 'key', provider: 'Groq' },
    ];
    const choice = await vscode.window.showQuickPick(choices, { placeHolder: 'Set up which fallback provider?' });
    if (!choice) { return; }

    if (choice.action === 'opt-in') {
        const result = await runCli(context, ['opt-in', '--provider', choice.provider]);
        vscode.window.showInformationMessage(
            result.ok ? `${choice.provider} opted into the fallback chain.` : `Failed: ${result.error}`
        );
        return;
    }

    const key = await vscode.window.showInputBox({
        prompt: `Enter the API key for ${choice.provider}`,
        password: true,
        ignoreFocusOut: true,
    });
    if (!key) { return; }

    // The key goes straight from this input box to the CLI process argument
    // and from there into DPAPI-encrypted storage (ProxyCredentialStore) -
    // never logged, never sent anywhere else by this extension.
    const result = await runCli(context, ['set-credential', '--provider', choice.provider, '--key', key]);
    vscode.window.showInformationMessage(
        result.ok ? `${choice.provider} credential stored (DPAPI-encrypted, this account only).` : `Failed: ${result.error}`
    );
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
        description: 'Runs Graphify + companion-tooling setup and launches (or resumes) a session for the folder open right now.'
    },
    {
        id: 'llmTokenOptimizer.openLauncherForFolder',
        label: 'Open Launcher (Master Folder Picker)',
        themeIcon: 'list-selection',
        description: 'Pick which project subfolders under a master folder to open, one independent session each.'
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
        description: 'Forget everything saved: master folder, project history, and provider preferences.'
    },
    {
        id: 'llmTokenOptimizer.setupProxy',
        label: 'Set Up Fallback Providers (Antigravity / Codex / Cursor / Groq)',
        themeIcon: 'key',
        description: 'Register credentials/opt-ins for backup providers. Antigravity auto-activates on a Claude Code usage limit; Codex/Cursor/Groq are manual transfer only.'
    },
    {
        id: 'llmTokenOptimizer.transferToCodex',
        label: 'Transfer Session to Codex',
        themeIcon: 'arrow-swap',
        description: 'Continue in Codex, carrying over this session\'s context and this project\'s skills as reference material.'
    },
    {
        id: 'llmTokenOptimizer.transferToCursor',
        label: 'Transfer Session to Cursor',
        themeIcon: 'arrow-swap',
        description: 'Continue in Cursor, carrying over this session\'s context and this project\'s skills as reference material.'
    },
    {
        id: 'llmTokenOptimizer.continueLocally',
        label: 'Continue Locally',
        themeIcon: 'arrow-swap',
        description: 'Continue with the configured local LM Studio model - no credential needed, hotswappable any time from the provider dropdown.'
    },
    {
        id: 'llmTokenOptimizer.openDashboard',
        label: 'Open Dashboard',
        themeIcon: 'graph-line',
        description: 'Live panel: RTK token-savings stats and every skill/plugin this project\'s Claude Code session invokes, as it happens.'
    },
    {
        id: 'llmTokenOptimizer.openApp',
        label: 'Open TokenOptimizer App',
        themeIcon: 'window',
        description: 'Launches the full desktop app (project picker, dependency dashboard, provider/fallback-chain launcher, benchmark tab) with this workspace pre-selected.'
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
            void launchProjectSession(context, folders[0].uri.fsPath);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.openLauncherForFolder', async () => {
            let masterFolder = context.globalState.get<string>(LAST_MASTER_FOLDER_KEY);
            if (!masterFolder || !fs.existsSync(masterFolder)) {
                masterFolder = await pickMasterFolder(context);
                if (!masterFolder) { return; }
            }

            const result = await runCli<{ candidates: { fullPath: string; name: string; seenBefore: boolean }[] }>(
                context, ['master-folder-list', '--path', masterFolder]
            );
            if (!result.ok || !result.data) {
                vscode.window.showErrorMessage(`LLM-TokenOptimizer: could not list projects - ${result.error}`);
                return;
            }
            if (result.data.candidates.length === 0) {
                vscode.window.showInformationMessage('No project subfolders found in that master folder.');
                return;
            }

            type ProjectPick = vscode.QuickPickItem & { fullPath: string };
            const items: ProjectPick[] = result.data.candidates.map(c => ({
                label: c.name,
                description: c.seenBefore ? 'previously opened' : 'new',
                fullPath: c.fullPath,
            }));
            const picked = await vscode.window.showQuickPick(items, {
                placeHolder: 'Select project(s) to open (multi-select)',
                canPickMany: true,
            });
            if (!picked || picked.length === 0) { return; }

            for (const p of picked) {
                await launchProjectSession(context, p.fullPath);
            }
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.changeMasterFolder', async () => {
            const masterFolder = await pickMasterFolder(context);
            if (!masterFolder) { return; }
            const openNow = await vscode.window.showInformationMessage(
                `Master folder set to "${masterFolder}". Open the launcher now?`,
                'Open Launcher', 'Not Now'
            );
            if (openNow === 'Open Launcher') {
                await vscode.commands.executeCommand('llmTokenOptimizer.openLauncherForFolder');
            }
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.resetConfig', async () => {
            const confirmed = await vscode.window.showWarningMessage(
                'This forgets the saved master folder, project history, and provider preferences. Continue?',
                { modal: true },
                'Reset Configuration'
            );
            if (confirmed !== 'Reset Configuration') { return; }
            const result = await runCli(context, ['reset-config']);
            vscode.window.showInformationMessage(result.ok ? 'Configuration reset.' : `Reset failed: ${result.error}`);
        }),

        vscode.commands.registerCommand('llmTokenOptimizer.setupProxy', () => setupProxyCredentials(context)),

        vscode.commands.registerCommand('llmTokenOptimizer.transferToCodex', () =>
            transferSession(context, 'Codex', 'Codex')),
        vscode.commands.registerCommand('llmTokenOptimizer.transferToCursor', () =>
            transferSession(context, 'Cursor', 'Cursor')),
        vscode.commands.registerCommand('llmTokenOptimizer.continueLocally', () =>
            transferSession(context, 'LM Studio (local)', 'the local model')),

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
        } else if (match('proxy', 'fallback', 'antigravity', 'codex', 'cursor', 'groq', 'backup agent', 'usage limit')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.setupProxy');
        } else if (match('open app', 'desktop app', 'full app')) {
            matched = ACTIONS.find(a => a.id === 'llmTokenOptimizer.openApp');
        }

        if (matched) {
            stream.markdown(`Running **${matched.label}**...\n\n`);
            await vscode.commands.executeCommand(matched.id);
            stream.markdown('Started - check the LLM-TokenOptimizer terminal for progress.');
            return;
        }

        stream.markdown(
            'I run TokenOptimizer actions against the app\'s CLI - Graphify + companion-tooling setup, ' +
            'provider/fallback-chain session launches, and session transfers. Tell me what to do, or pick one:\n\n'
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
    // Nothing to tear down - TokenOptimizer.App.exe owns its own cleanup
    // (config saves, instance-lock release) on process exit; this extension
    // never holds a process handle across command invocations (--cli runs
    // are fire-and-wait or detached, never tracked here).
}
