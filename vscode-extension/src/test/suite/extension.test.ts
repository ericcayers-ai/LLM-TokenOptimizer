import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { execFile } from 'child_process';

const EXTENSION_ID = 'local.llm-token-optimizer';
const EXPECTED_COMMANDS = [
    'llmTokenOptimizer.openCurrentWorkspace',
    'llmTokenOptimizer.openLauncherForFolder',
    'llmTokenOptimizer.changeMasterFolder',
    'llmTokenOptimizer.resetConfig',
    'llmTokenOptimizer.setupProxy',
    'llmTokenOptimizer.transferToCodex',
    'llmTokenOptimizer.transferToCursor',
    'llmTokenOptimizer.continueLocally',
    'llmTokenOptimizer.openDashboard',
    'llmTokenOptimizer.openApp',
    'llmTokenOptimizer.quickMenu'
];

function resolveAppExecutablePath(): string | undefined {
    const configured = vscode.workspace.getConfiguration('llmTokenOptimizer').get<string>('appExecutablePath', '').trim();
    if (configured) {
        return fs.existsSync(configured) ? configured : undefined;
    }
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    const repoRoot = ext ? path.dirname(ext.extensionPath) : path.join(__dirname, '..', '..', '..');
    const candidates = [
        path.join(repoRoot, 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Debug', 'net10.0', 'TokenOptimizer.App.exe'),
        path.join(repoRoot, 'app', 'src', 'TokenOptimizer.App', 'bin', 'Release', 'net10.0', 'TokenOptimizer.App.exe'),
    ];
    return candidates.find(fs.existsSync);
}

function fetchProviderNames(exePath: string): Promise<string[]> {
    return new Promise((resolve, reject) => {
        execFile(exePath, ['--cli', 'providers'], { maxBuffer: 32 * 1024 * 1024, timeout: 0 }, (err, stdout) => {
            const lastLine = stdout.trim().split('\n').pop() ?? '';
            try {
                const parsed = JSON.parse(lastLine) as { ok: boolean; data?: { providers?: { name: string }[] } };
                if (!parsed.ok || !Array.isArray(parsed.data?.providers)) {
                    reject(new Error(err ? err.message : 'Invalid --cli providers output'));
                    return;
                }
                resolve(parsed.data.providers.map(p => p.name));
            } catch {
                reject(new Error(err ? err.message : (stdout.trim() || 'No output from --cli providers')));
            }
        });
    });
}

suite('LLM-TokenOptimizer extension', () => {
    test('extension is present and activates without throwing', async () => {
        const ext = vscode.extensions.getExtension(EXTENSION_ID);
        assert.ok(ext, `Extension ${EXTENSION_ID} was not found by the test host`);
        await ext!.activate();
        assert.strictEqual(ext!.isActive, true);
    });

    test('all commands are registered', async () => {
        const all = await vscode.commands.getCommands(true);
        for (const cmd of EXPECTED_COMMANDS) {
            assert.ok(all.includes(cmd), `Expected command "${cmd}" to be registered`);
        }
    });

    test('openCurrentWorkspace with no workspace open does not throw', async () => {
        // No folder is open in this test host by default (runTest.ts starts
        // one with no workspace argument) - this exercises the guard clause
        // rather than the real script-launch path, which is the correct
        // thing to test headlessly without a real project folder.
        await vscode.commands.executeCommand('llmTokenOptimizer.openCurrentWorkspace');
    });

    test('configuration schema is registered with expected defaults', () => {
        const cfg = vscode.workspace.getConfiguration('llmTokenOptimizer');
        assert.strictEqual(cfg.get('isolateClaudeConfig'), false);
        assert.strictEqual(cfg.get('model'), '');
        assert.strictEqual(cfg.get('appExecutablePath'), '');
    });

    test('Activity Bar view and chat participant are declared in package.json', () => {
        const ext = vscode.extensions.getExtension(EXTENSION_ID);
        assert.ok(ext, `Extension ${EXTENSION_ID} was not found by the test host`);
        const pkg = ext!.packageJSON;

        const viewIds: string[] = Object.values(pkg.contributes.views)
            .flat()
            .map((v: any) => v.id);
        assert.ok(viewIds.includes('llmTokenOptimizerView'), 'llmTokenOptimizerView should be a declared view');

        const containerIds: string[] = pkg.contributes.viewsContainers.activitybar.map((c: any) => c.id);
        assert.ok(containerIds.includes('llmTokenOptimizer'), 'llmTokenOptimizer should be a declared Activity Bar container');

        const participantIds: string[] = (pkg.contributes.chatParticipants || []).map((p: any) => p.id);
        assert.ok(participantIds.includes('llmTokenOptimizer.agent'), 'llmTokenOptimizer.agent should be a declared chat participant');
    });

    test('only Start and Open App are visible in the Command Palette', () => {
        const ext = vscode.extensions.getExtension(EXTENSION_ID);
        const paletteMenu: any[] = ext!.packageJSON.contributes.menus.commandPalette;
        const visible = paletteMenu.filter(m => m.when !== 'false').map(m => m.command);
        const hidden = paletteMenu.filter(m => m.when === 'false').map(m => m.command);

        assert.deepStrictEqual(visible.sort(), ['llmTokenOptimizer.openApp', 'llmTokenOptimizer.quickMenu'].sort());
        for (const cmd of EXPECTED_COMMANDS) {
            if (cmd === 'llmTokenOptimizer.quickMenu' || cmd === 'llmTokenOptimizer.openApp') { continue; }
            assert.ok(hidden.includes(cmd), `${cmd} should be hidden from the Command Palette (still invocable from the tree view/chat)`);
        }
    });

    test('all provider-id literals in extension.ts exist in app provider registry', async () => {
        const ext = vscode.extensions.getExtension(EXTENSION_ID);
        const sourcePath = path.join(ext ? ext.extensionPath : path.join(__dirname, '..', '..', '..'), 'src', 'extension.ts');
        const source = fs.readFileSync(sourcePath, 'utf8');

        const providerIds: string[] = [];
        const regex = /transferSession\([^,]+,\s*'([^']+)'/g;
        let match: RegExpExecArray | null;
        while ((match = regex.exec(source)) !== null) {
            providerIds.push(match[1]);
        }
        assert.ok(providerIds.length > 0, 'Expected at least one provider-id literal in extension.ts');

        const exePath = resolveAppExecutablePath();
        assert.ok(exePath, 'TokenOptimizer.App.exe not found - build the app or set llmTokenOptimizer.appExecutablePath');

        const names = await fetchProviderNames(exePath);
        for (const id of providerIds) {
            assert.ok(names.includes(id), `Provider id "${id}" from extension.ts is missing from --cli providers output`);
        }
    });
});
