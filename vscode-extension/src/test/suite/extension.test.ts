import * as assert from 'assert';
import * as vscode from 'vscode';

const EXTENSION_ID = 'local.llm-token-optimizer';
const EXPECTED_COMMANDS = [
    'llmTokenOptimizer.openCurrentWorkspace',
    'llmTokenOptimizer.openLauncherForFolder',
    'llmTokenOptimizer.changeMasterFolder',
    'llmTokenOptimizer.resetConfig',
    'llmTokenOptimizer.reconfigureOmniRoute',
    'llmTokenOptimizer.quickMenu'
];

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
        assert.strictEqual(cfg.get('powershellExecutable'), 'powershell.exe');
        assert.strictEqual(cfg.get('isolateClaudeConfig'), false);
        assert.strictEqual(cfg.get('verboseMode'), false);
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

    test('only the Start command is visible in the Command Palette', () => {
        const ext = vscode.extensions.getExtension(EXTENSION_ID);
        const paletteMenu: any[] = ext!.packageJSON.contributes.menus.commandPalette;
        const visible = paletteMenu.filter(m => m.when !== 'false').map(m => m.command);
        const hidden = paletteMenu.filter(m => m.when === 'false').map(m => m.command);

        assert.deepStrictEqual(visible, ['llmTokenOptimizer.quickMenu']);
        for (const cmd of EXPECTED_COMMANDS) {
            if (cmd === 'llmTokenOptimizer.quickMenu') { continue; }
            assert.ok(hidden.includes(cmd), `${cmd} should be hidden from the Command Palette (still invocable from the tree view/chat)`);
        }
    });
});
