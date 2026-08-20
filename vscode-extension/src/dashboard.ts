import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';

// Live dashboard: a webview panel (not a terminal) showing (1) RTK's own
// token-savings stats and (2) which skills/plugins Claude Code has actually
// invoked in this project's current session, as it happens. Both are built
// on real, already-existing data sources rather than anything tracked by
// this extension itself:
//   - Token savings: `rtk.exe gain --format json` - RTK's own CLI, confirmed
//     live to support --format json. Polled on an interval since RTK's
//     internal storage format isn't something this extension should reach
//     into directly - the CLI is the stable interface.
//   - Skill/tool activity: Claude Code's own session transcript JSONL under
//     ~/.claude/projects/<slugified-path>/<session-id>.jsonl (or
//     $CLAUDE_CONFIG_DIR/projects/... under an isolated profile), watched
//     for changes and tailed for new tool_use blocks. Same file format
// the legacy launcher's Export-SessionHandoff already reads.

let panel: vscode.WebviewPanel | undefined;
let pollTimer: NodeJS.Timeout | undefined;
let fileWatcher: fs.FSWatcher | undefined;
let lastByteOffset = 0;
let currentTranscriptPath: string | undefined;

interface ToolEvent {
    tool: string;
    detail: string;
}

function findRtkExe(): string | undefined {
    const candidate = path.join(process.env.LOCALAPPDATA || '', 'rtk', 'rtk.exe');
    return fs.existsSync(candidate) ? candidate : undefined;
}

function slugifyProjectPath(projectPath: string): string {
    return projectPath.replace(/[:\\/]/g, '-');
}

function findLatestTranscript(projectPath: string): string | undefined {
    const claudeHome = process.env.CLAUDE_CONFIG_DIR || path.join(process.env.USERPROFILE || '', '.claude');
    const dir = path.join(claudeHome, 'projects', slugifyProjectPath(projectPath));
    if (!fs.existsSync(dir)) { return undefined; }
    const files = fs.readdirSync(dir)
        .filter(f => f.endsWith('.jsonl'))
        .map(f => ({ f, mtime: fs.statSync(path.join(dir, f)).mtimeMs }))
        .sort((a, b) => b.mtime - a.mtime);
    return files.length > 0 ? path.join(dir, files[0].f) : undefined;
}

function extractToolInvocations(line: string): ToolEvent[] {
    const out: ToolEvent[] = [];
    let obj: any;
    try { obj = JSON.parse(line); } catch { return out; }
    if (obj.type !== 'assistant') { return out; }
    const content = obj.message?.content;
    if (!Array.isArray(content)) { return out; }
    for (const block of content) {
        if (block.type === 'tool_use') {
            let detail = '';
            if (block.name === 'Skill' && block.input?.skill) { detail = block.input.skill; }
            else if (block.input?.command) { detail = String(block.input.command).slice(0, 80); }
            else if (block.input?.file_path) { detail = String(block.input.file_path); }
            else if (block.input?.pattern) { detail = String(block.input.pattern).slice(0, 80); }
            out.push({ tool: block.name, detail });
        }
    }
    return out;
}

function postSkillEvents(events: ToolEvent[]): void {
    if (!panel || events.length === 0) { return; }
    panel.webview.postMessage({ type: 'skillEvents', events, ts: Date.now() });
}

function tailTranscript(transcriptPath: string): void {
    try {
        const stat = fs.statSync(transcriptPath);
        if (stat.size < lastByteOffset) { lastByteOffset = 0; } // rotated/truncated
        if (stat.size === lastByteOffset) { return; }
        const fd = fs.openSync(transcriptPath, 'r');
        const buf = Buffer.alloc(stat.size - lastByteOffset);
        fs.readSync(fd, buf, 0, buf.length, lastByteOffset);
        fs.closeSync(fd);
        lastByteOffset = stat.size;
        const lines = buf.toString('utf8').split('\n').filter(l => l.trim());
        const events: ToolEvent[] = [];
        for (const line of lines) { events.push(...extractToolInvocations(line)); }
        postSkillEvents(events);
    } catch {
        // best-effort - never throw from a watcher callback
    }
}

function pollTokenSavings(): void {
    if (!panel) { return; }
    const rtkExe = findRtkExe();
    if (!rtkExe) {
        panel.webview.postMessage({ type: 'tokenSavings', available: false });
        return;
    }
    execFile(rtkExe, ['gain', '--format', 'json'], { timeout: 5000 }, (err, stdout) => {
        if (!panel) { return; }
        if (err) {
            panel.webview.postMessage({ type: 'tokenSavings', available: false });
            return;
        }
        try {
            const data = JSON.parse(stdout);
            panel.webview.postMessage({ type: 'tokenSavings', available: true, summary: data.summary });
        } catch {
            panel.webview.postMessage({ type: 'tokenSavings', available: false });
        }
    });
}

function startWatchingTranscript(projectPath: string): void {
    if (fileWatcher) { fileWatcher.close(); fileWatcher = undefined; }
    const transcript = findLatestTranscript(projectPath);
    if (!transcript) { return; }
    currentTranscriptPath = transcript;
    lastByteOffset = 0;
    tailTranscript(transcript); // report existing activity immediately too
    try {
        fileWatcher = fs.watch(transcript, { persistent: false }, () => tailTranscript(transcript));
    } catch {
        // best-effort
    }
}

export function openDashboard(context: vscode.ExtensionContext): void {
    const folders = vscode.workspace.workspaceFolders;
    const projectPath = folders && folders.length > 0 ? folders[0].uri.fsPath : undefined;

    if (panel) {
        panel.reveal(vscode.ViewColumn.Beside);
        return;
    }

    panel = vscode.window.createWebviewPanel(
        'llmTokenOptimizerDashboard',
        'LLM-TokenOptimizer Dashboard',
        vscode.ViewColumn.Beside,
        { enableScripts: true, retainContextWhenHidden: true }
    );
    panel.iconPath = vscode.Uri.joinPath(context.extensionUri, 'media', 'icon.svg');
    panel.webview.html = getHtml();

    panel.onDidDispose(() => {
        panel = undefined;
        if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined; }
        if (fileWatcher) { fileWatcher.close(); fileWatcher = undefined; }
    });

    if (!projectPath) {
        panel.webview.postMessage({ type: 'noWorkspace' });
    } else {
        startWatchingTranscript(projectPath);
    }
    pollTokenSavings();
    pollTimer = setInterval(() => {
        pollTokenSavings();
        // Re-resolve the latest transcript each poll too, in case a new
        // session started (new .jsonl) since the dashboard opened.
        if (projectPath) {
            const latest = findLatestTranscript(projectPath);
            if (latest && latest !== currentTranscriptPath) { startWatchingTranscript(projectPath); }
        }
    }, 5000);
}

function getHtml(): string {
    return `<!DOCTYPE html>
<html>
<head>
<meta charset="UTF-8">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); background: var(--vscode-editor-background); padding: 16px; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: 0.5px; opacity: 0.8; margin-bottom: 8px; }
  .card { background: var(--vscode-sideBar-background); border: 1px solid var(--vscode-widget-border, transparent); border-radius: 6px; padding: 14px; margin-bottom: 16px; }
  .stat-row { display: flex; gap: 24px; flex-wrap: wrap; }
  .stat { min-width: 120px; }
  .stat .value { font-size: 22px; font-weight: 600; }
  .stat .label { font-size: 11px; opacity: 0.7; }
  #feed { max-height: 420px; overflow-y: auto; font-family: var(--vscode-editor-font-family); font-size: 12px; }
  .feed-item { padding: 4px 0; border-bottom: 1px solid var(--vscode-widget-border, rgba(128,128,128,0.15)); display: flex; gap: 8px; align-items: baseline; }
  .feed-item .tool { color: var(--vscode-textLink-foreground); font-weight: 600; min-width: 90px; }
  .feed-item .time { opacity: 0.5; font-size: 10px; min-width: 60px; }
  .feed-item .detail { opacity: 0.85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .empty { opacity: 0.6; font-style: italic; }
</style>
</head>
<body>
  <h2>Token Savings (RTK)</h2>
  <div class="card">
    <div id="savings-body" class="stat-row"><span class="empty">Waiting for data...</span></div>
  </div>

  <h2>Live Skill &amp; Plugin Activity</h2>
  <div class="card">
    <div id="feed"><span class="empty">Watching this project's Claude Code session...</span></div>
  </div>

<script>
  const feedEl = document.getElementById('feed');
  const savingsEl = document.getElementById('savings-body');
  let feedItems = [];

  window.addEventListener('message', event => {
    const msg = event.data;
    if (msg.type === 'noWorkspace') {
      feedEl.innerHTML = '<span class="empty">Open a folder/workspace to watch its session activity.</span>';
    } else if (msg.type === 'tokenSavings') {
      if (!msg.available) {
        savingsEl.innerHTML = '<span class="empty">RTK not detected on this machine.</span>';
        return;
      }
      const s = msg.summary;
      savingsEl.innerHTML =
        '<div class="stat"><div class="value">' + s.total_saved.toLocaleString() + '</div><div class="label">tokens saved</div></div>' +
        '<div class="stat"><div class="value">' + s.avg_savings_pct.toFixed(1) + '%</div><div class="label">avg savings</div></div>' +
        '<div class="stat"><div class="value">' + s.total_commands.toLocaleString() + '</div><div class="label">commands tracked</div></div>';
    } else if (msg.type === 'skillEvents') {
      for (const e of msg.events) { feedItems.unshift(Object.assign({}, e, { ts: msg.ts })); }
      feedItems = feedItems.slice(0, 100);
      feedEl.innerHTML = feedItems.map(i =>
        '<div class="feed-item">' +
          '<span class="time">' + new Date(i.ts).toLocaleTimeString() + '</span>' +
          '<span class="tool">' + i.tool + '</span>' +
          '<span class="detail">' + (i.detail || '') + '</span>' +
        '</div>'
      ).join('');
    }
  });
</script>
</body>
</html>`;
}
