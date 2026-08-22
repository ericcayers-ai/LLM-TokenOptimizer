## graphify

This project has a graphify knowledge graph at .graphify/.

Rules:
- For codebase or architecture questions, when `.graphify/graph.json` exists, first run `graphify query "<question>"` (or `graphify path "<A>" "<B>"` / `graphify explain "<concept>"`); these return a scoped subgraph, usually much smaller than `GRAPH_REPORT.md` or raw grep output
- If .graphify/wiki/index.md exists, navigate it instead of reading raw files
- If .graphify/graph.json is missing but graphify-out/graph.json exists, run `graphify migrate-state --dry-run` first; if tracked legacy artifacts are reported, ask before using the recommended `git mv -f graphify-out .graphify` and commit message
- If .graphify/needs_update exists or .graphify/branch.json has stale=true, warn before relying on semantic results and run /graphify . --update when appropriate
- Before proposing or committing .graphify artifacts, run `graphify portable-check .graphify`; commit-safe graph artifacts must use repo-relative paths, and never commit .graphify/branch.json, .graphify/worktree.json, .graphify/needs_update, or .graphify/cache/. If a repo already tracks any of them, first add them to .gitignore, then propose `git rm --cached .graphify/branch.json .graphify/worktree.json .graphify/needs_update` and `git rm -r --cached .graphify/cache`; never mutate git state without asking
- Before deep graph traversal, prefer `graphify summary --graph .graphify/graph.json` for compact first-hop orientation
- For review impact on changed files, use `graphify review-delta --graph .graphify/graph.json` instead of generic traversal
- Read `.graphify/GRAPH_REPORT.md` only for broad architecture review or when `query` / `path` / `explain` do not surface enough context
- After modifying code files in this session, run `npx graphify hook-rebuild` to keep the graph current

CRITICAL: You must run `graphify query` or read `graphify-out/GRAPH_REPORT.md` (or `.graphify/graph.json` / `.graphify/studio/studio.html` on newer Graphify builds) before any raw file read, Glob, or Grep. This is non-negotiable.

# Graphify enforcement

- Treat `graphify` as mandatory for understanding this codebase. `grep`/`Grep` and raw file reads are a fallback only, to be used after consulting the graph, never before it.
- Any subagent spawned inside this project must follow the same rule: query the graph first, fall back to grep only if the graph doesn't have the answer.
- At the start of a session: use `graphify-out/GRAPH_REPORT.md` (or the current project's `.graphify/graph.json`) before searching files. Do not use raw grep first.
- Strict-mode enforcement is active for this project (`graphify install --project --strict`, `GRAPHIFY_HOOK_STRICT=1`, and a `PreToolUse` hook installed via `graphify claude install` in `.claude/settings.json`). The first raw source read of a session is hard-blocked and redirected to the graph; file search and bash commands are intercepted by the hook.

# Companion tooling

The following are installed once at user scope (`~/.claude/`) and are active in every session in this project, not just this one. Each reacts to its own lifecycle hook or slash command - nothing below needs manual invocation. Tools are split into two families: hook-automatic (registered in `~/.claude/settings.json` or a plugin manifest, fire on their own) and manual-skill-only (described in the skill list, triggered by you/Claude when relevant).

Hook-automatic:
- **claude-mem** - captures what happens in this session (files read/edited, decisions made) and injects relevant memories back in at the start of future sessions. Runs on Claude Code's own SessionStart/PostToolUse/Stop hooks.
- **headroom** - a live context-window usage bar in the statusline, reading the actual session JSONL rather than estimating. Driven by its PostToolUse hook.
- **Session hygiene** (headroom-driven): when the statusline bar gets to roughly 70-80% used, or the conversation has drifted onto a second, unrelated task, run `/compact` to summarize and free space - don't wait until it's nearly full, compaction is lossy and works best at a natural checkpoint, not mid-task. If the next thing you want to do is genuinely unrelated to what's already in context, prefer starting a NEW session over compacting an unrelated history into it - v5.0+ of this launcher allows multiple concurrent sessions against the same project folder for exactly this: open another window/session rather than dragging old, irrelevant context along. Use Claude Code's own `--resume` picker (offered on a returning project) to come back to a specific old session by name instead of losing it. Within one session, `/clear` resets context between unrelated tasks without a new window; if you've corrected the same mistake twice, `/clear` and rewrite the prompt rather than layering a third correction on a polluted context.
- **RTK (Rust Token Killer)** - rewrites eligible Bash commands (git/cargo/test/ls etc) through a token-compressing proxy via its PreToolUse hook, cutting up to 90% of tool output tokens. Meta commands: `rtk gain`, `rtk discover`, `rtk proxy <cmd>`.
- **context-mode** - MCP server for toggling conversation context behavior; wired through its own plugin manifest.
- **caveman** - ultra-compressed communication mode (SessionStart + UserPromptSubmit hooks). Use `/caveman <level>` to activate.
- **ponytail** - session-management helpers (SessionStart + SubagentStart + UserPromptSubmit hooks via its hooks.json).
- **context7** (MCP) - version-specific library/API docs on demand. Prefer it over guessing from training data or grepping through node_modules/site-packages when you need to know how a specific dependency version actually behaves - ask for it by name, e.g. "use context7 to check react-router v7's data loading API."

Manual-skill-only (no hooks, trigger when relevant):
- **claude-code-setup** - read-only; if asked to recommend MCP servers, hooks, skills, or subagents for this project, this is the mechanism, invoked via its own skill.
- **task-observer** - a skill for spotting when an existing skill in this project is out of date or missing something, based on how it's actually being used.
- **claude-md-management** - this file. Run `/revise-claude-md` (or press `#` mid-session) to capture a learning - a discovered build flag, a naming convention you were corrected on - directly into this file instead of losing it at session end. Keep additions concise and merged into the relevant existing section rather than appended as a new one where one already fits.

- **Prompt cache**: Claude Code caches its own system prompt, tool definitions, and this file automatically - no setup needed, and nothing to add on top of it. It IS fragile mid-session, though: switching models, or a plugin/MCP change that needs `/reload-plugins`, invalidates the cache and the next turn re-reads the whole conversation at full price. Avoid both unless actually necessary. Delegate large, noisy reads (broad exploration, verbose command output you only need the conclusion of) to a subagent so that bulk never enters this session's own cached context at all.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
