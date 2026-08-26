# Investigation handoff: Claude Code `/model` picker won't show ticked non-Claude models

**Audience:** a fresh Claude Opus session with zero prior context on this repo or this bug. Read this whole document before touching anything. Every claim below marked "confirmed" was verified empirically in a prior session (live HTTP probes, live app launches, and direct string-search of the shipped `claude.exe` binary) — treat unconfirmed items as hypotheses to test, not facts.

**Repo:** `C:\Users\ericc\OneDrive\Desktop\Programs\misc\LLM-TokenOptimizer` (git repo, branch `main` unless changed). Solution: `app/TokenOptimizer.slnx`. .NET 10.

**Do not commit anything unless the user explicitly asks.** Build/test freely.

---

## 1. What the app is trying to do

TokenOptimizer is a desktop app (Avalonia/.NET) that launches Claude Code CLI as a child process, pointed at a local HTTP proxy (`UnifiedModelRouter`) via `ANTHROPIC_BASE_URL`. The proxy fans requests out to multiple LLM providers (Groq, OpenCode, local llama.cpp models, real Anthropic) based on which "model" id the request specifies. The goal: when a user ticks several models across providers in the app's Session tab and clicks Launch, Claude Code's own `/model` picker (the built-in `Select model` interactive menu) should list all of them, not just the account's real Claude model, so the user can switch models with a normal `/model` command inside the running CLI session.

**Current symptom:** after ticking e.g. Groq's `compound` and OpenCode's `deepseek-v4-flash-free` and launching, the CLI's `/model` picker still shows only:
```
1. Default (recommended)  ✔ Use the default model (currently Opus 4.7)
```
No trace of the ticked models. This is true even after the fixes described in section 3 below, which were expected to fix it and did not (or the test that showed they didn't may itself have been invalid — see section 5, this is the most important open question).

## 2. Relevant files in this repo

- **`app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs`** — method `LaunchTickedModelsAsync` (around line 1250-1360) builds the child process. Key block (as of the last edit, roughly line 1300-1315):
  ```csharp
  var router = new UnifiedModelRouter(routes, autoFallbackDelegate: ResolveAutoFallbackRouteAsync);
  await router.StartAsync();
  var defaultModelId = orderedBridged[0].ModelId;
  var args = new List<string>();
  if (defaultModelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
  {
      args.Add($"--model {defaultModelId}");
  }
  // ... resume flag ...
  var psi = new System.Diagnostics.ProcessStartInfo
  {
      FileName = claudeExe,
      Arguments = string.Join(' ', args),
      WorkingDirectory = SelectedProject.FullPath,
      UseShellExecute = false,
  };
  psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = router.BaseUrl;
  psi.EnvironmentVariables["CLAUDE_CODE_USE_GATEWAY"] = "1";
  psi.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = "tokenoptimizer-local-gateway";
  psi.EnvironmentVariables["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1";
  psi.EnvironmentVariables["CLAUDE_MEM_WORKER_PORT"] = CompanionToolingInstaller.IsolatedWorkerPort.ToString();
  psi.EnvironmentVariables["CLAUDE_MEM_DATA_DIR"] = CompanionToolingInstaller.IsolatedDataDir;
  if (IsolateClaudeConfig) { psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = IsolatedClaudeProfileService.GetOrCreateProfileDir(SelectedProject.FullPath); }
  var process = System.Diagnostics.Process.Start(psi);
  ```
  Note: `--model` is deliberately only passed when the default ticked model is Claude-native (real `claude-*` id). Passing a non-Claude id via `--model` causes Claude Code to reject it outright ("restricted by your organization's settings") and fall back to the account default, confirmed in a prior session — do not remove that guard.

- **`app/src/TokenOptimizer.Providers/Compat/UnifiedModelRouter.cs`** — the local proxy. `StartAsync()` binds a free loopback port via `HttpListener` (Windows `http.sys`-backed — see section 6 gotcha). `Advertise(modelId)` / `Unadvertise(modelId)` disguise every non-Claude-native id by prefixing `claude-gateway-` before it's returned from `GET /v1/models`, and reverse the prefix on the way back in for route lookups. `GET /v1/models` returns:
  ```json
  {"data":[{"id":"claude-sonnet-5", ...}, {"id":"claude-gateway-deepseek-v4-flash-free", ...}, {"id":"claude-gateway-groq/compound", ...}, {"id":"claude-gateway-__auto__", ...}], "first_id":"...", "last_id":"...", "has_more":false}
  ```
  This was confirmed live in a prior session via `Invoke-WebRequest` directly against the router's port — the server side works and returns exactly this shape. **Do not re-litigate whether the router itself works — it does.** If you suspect otherwise, re-verify with a direct HTTP probe (see section 6) before assuming regression.

- **`claude.exe`** — the actual CLI binary, at `C:\Users\ericc\.local\bin\claude.exe`. Version 2.1.238, `BUILD_TIME:"2026-08-20T15:08:27Z"`, `GIT_SHA:"46283063a4c23f7afadb8440f549264ad93b7c06"`. This is a ~319MB PE executable — a bundled/minified JS application (esbuild-style bundle) with an embedded Node/Bun runtime. It is NOT open source in this repo; all knowledge of its internals in this document came from directly string-searching the binary (see section 4 for method) in a prior session. Treat every snippet below as ground truth extracted from the actual shipped binary, not speculation — but also assume there is more relevant code not yet found.

## 3. What was already tried, and the reasoning (confirmed via binary string search)

Claude Code CLI has an internal function (minified name `to()`, exported as `getAPIProvider`) that returns one of: `"gateway"`, `"bedrock"`, `"foundry"`, `"anthropicAws"`, `"anthropicGoogleCloud"`, `"mantle"`, `"vertex"`, `"firstParty"`. Verbatim:
```js
function to(){
  if(ny()||ONs())return"gateway";
  return q.CLAUDE_CODE_USE_BEDROCK?"bedrock"
    :q.CLAUDE_CODE_USE_FOUNDRY?"foundry"
    :q.CLAUDE_CODE_USE_ANTHROPIC_AWS?"anthropicAws"
    :q.CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD?"anthropicGoogleCloud"
    :q.CLAUDE_CODE_USE_MANTLE?"mantle"
    :q.CLAUDE_CODE_USE_VERTEX?"vertex"
    :"firstParty"
}
```
`ny()` = `vr.host.credentialSlots.gatewayAuth()`, `ONs()` = `vr.host.credentialSlots.gatewayServerProcess()`. Setting `ANTHROPIC_BASE_URL` alone does **not** put the CLI into `"gateway"` mode — that surprised the previous investigation and was the actual root cause of an earlier failed fix attempt (see git history / MainViewModel.cs comments for the full story if useful, not required reading).

The **only found way** to populate the `gatewayAuth` credential slot from outside (short of a real enterprise SSO `/login` flow) is this function (minified name `eLn`):
```js
async function eLn(){
  if(q.CLAUDE_CODE_USE_GATEWAY){
    let e=q.ANTHROPIC_BASE_URL, t=q.ANTHROPIC_AUTH_TOKEN;
    if(e&&t){
      let r;
      try{ r=l7o(e) } catch(o){ throw Error(`CLAUDE_CODE_USE_GATEWAY is set but ANTHROPIC_BASE_URL is invalid: ${ce(o)}`) }
      let n=Lze(t);
      dYe({url:r, jwt:t, expiresAt:n!==null?n*1000:Number.MAX_SAFE_INTEGER, unpinned:!0});
      return;
    }
    w("CLAUDE_CODE_USE_GATEWAY is set but ANTHROPIC_BASE_URL or ANTHROPIC_AUTH_TOKEN is missing; ignoring", {level:"warn"});
  }
}
```
`dYe` = `vr.host.credentialSlots.replaceGatewayAuth`. `Lze(t)` appears to try to parse `t` as a JWT and extract an `exp` claim; if it can't (our token is not a real JWT), it returns `null` and `expiresAt` falls back to `Number.MAX_SAFE_INTEGER` (never expires) — so **the auth token does not need to be a real/valid JWT**, any non-empty string works to get past this gate.

`l7o` (the URL validator) rejects plain `http://` URLs unless the hostname is a recognized loopback name:
```js
function l7o(e){
  let t=e.trim();
  if(!/^https?:\/\//i.test(t)) t=`https://${t}`;
  t=t.replace(/\/$/,"");
  let r=new URL(t);
  if(r.protocol==="http:" && !jIb.has(r.hostname))
    throw Error("Gateway URL must use https:// (got http://). Plain HTTP is only allowed for localhost during development.");
  return t;
}
```
The error text explicitly says plain HTTP is allowed for localhost — the router binds `127.0.0.1`, which should qualify (the exact contents of the `jIb` hostname-allowlist Set were not confirmed by direct string dump — the surrounding module-scope constant table was too noisy to isolate cleanly with a naive grep in the prior session; **this is one of the first things to verify** if `l7o` turns out to be silently throwing).

Based on this, the prior session changed `MainViewModel.cs` to set, on top of the pre-existing `ANTHROPIC_BASE_URL` and `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`:
```
CLAUDE_CODE_USE_GATEWAY=1
ANTHROPIC_AUTH_TOKEN=tokenoptimizer-local-gateway
```
This built cleanly and all 169 existing unit tests still passed (no regression). **Live evidence this partially worked:** a subsequent screenshot of a freshly-opened Claude Code window showed the CLI's own startup banner now reading `Opus 4.7 with high effort · Cloud gateway` — that `Cloud gateway` label is new and directly implies `to()` is now returning `"gateway"` (this label was NOT present before the env var change, per the user's original bug report screenshot). **This part of the fix is confirmed working.**

**However**, `/model` still showed only `Default (recommended)` in that same screenshot. This is the unresolved half of the bug.

## 4. The actual model-fetch mechanism once in "gateway" mode (confirmed)

Once `to()==="gateway"`, the CLI's bootstrap sequence calls (minified name `ngw`):
```js
async function ngw(e){
  if(to()==="gateway"){
    if(!q.CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY)
      return w("[Bootstrap] Skipped gateway /v1/models (CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY not set)"), {response:{additional_model_options:[]}, viaScopelessOAuth:!1};
    let a=await igw();
    return a && {response:a, viaScopelessOAuth:!1};
  }
  // ... unrelated firstParty/other-provider branches, not relevant here ...
}
```
`igw()` is the function that actually does the HTTP call:
```js
async function igw(){
  await NLt();
  let e=ny();              // = vr.host.credentialSlots.gatewayAuth() = the {url, jwt, expiresAt, unpinned} object set by eLn() above
  if(!e) return null;
  try{
    let t=await ui.get(`${e.url}/v1/models`, {
      headers:{ Authorization:`Bearer ${e.jwt}`, "anthropic-version":"2023-06-01", "User-Agent":NT() },
      params:{ limit:1000 },
      timeout:5000
    });
    let r=ogw().safeParse(t.data);
    if(!r.success) return w(`[Bootstrap] Gateway /v1/models failed validation: ${r.error.message}`), null;
    let n=r.data.data
      .filter((o)=>/(claude|anthropic)/i.test(o.id))          // <-- filter 1
      .filter((o)=>{ let i=sZ(o.id); return i===null||i===jIt }) // <-- filter 2 (see below)
      .map((o)=>({ value:o.id, label:XJ(o.display_name??"")||XJ(o.id), description:dMr(o.description??"") }));
    return w(`[Bootstrap] Gateway /v1/models \u2192 ${n.length} custom options`), {additional_model_options:n};
  } catch(t) {
    return w(`[Bootstrap] Gateway /v1/models fetch failed: ${ui.isAxiosError(t)?t.response?.status??t.code:"unknown"}`), null;
  }
}
```
Response schema required (`ogw`):
```js
ogw = Ee(() => Be.object({
  data: Be.array(Be.object({
    id: Be.string(),
    display_name: Be.string().nullish(),
    description: Be.string().nullish()
  }))
}))
```
(`Be` = zod. This schema does **not** use `.strict()`, so extra top-level keys like our router's `first_id`/`last_id`/`has_more` should be harmless — zod objects allow unknown keys by default.) This matches the router's actual response shape exactly, confirmed by direct comparison against the live JSON captured in a prior session (section 2 above).

**Filter 1** (`/(claude|anthropic)/i.test(o.id)`) is why the router's `Advertise()` disguises every non-Claude id with a `claude-gateway-` prefix — `"claude-gateway-groq/compound"` contains `"claude"` and passes.

**Filter 2** (`sZ(o.id)` must be `null` or `=== jIt`) was found and partially analyzed but **not fully resolved** — this is important unfinished work:
```js
function sZ(e){
  let t=e.toLowerCase();
  for(let r of Object.values(cd))
    for(let n of Object.values(r))
      if(typeof n==="string" && n.toLowerCase()===t) return r;
  return null;
}
```
`cd` is a catalog object of real, known Claude model ids (its initializer starts `ncb={"claude-3-5-haiku...`, truncated in the prior session's dump — never fully captured). `sZ(id)` returns the matching family-config object if `id` is an *exact* (case-insensitive) match against some known real Claude id string anywhere in that catalog, else `null`. Since our disguised ids (`"claude-gateway-groq/compound"`, `"claude-gateway-deepseek-v4-flash-free"`, `"claude-gateway-__auto__"`) do not exactly equal any real catalogued Claude id, `sZ()` should return `null` for all of them, and the filter condition `i===null` should be `true` → they should pass. **This reasoning was not verified by finding what `jIt` actually is in this module's scope** (a same-named `jIt` was found elsewhere in the bundle but it was PCRE2 regex-engine error-message text — an unrelated module-scope collision from the minifier reusing short identifiers; the real `jIt` used inside `sZ`'s module (declared in the same `var ncb,cd,...,jIt,jpd,rta,zpd,EMe;` list, initialized inside the `var $3=A(()=>{...})` IIFE that also builds `cd`) was never actually located/read. **This is the top candidate for where a silent, subtle rejection could still be happening** — e.g. if `jIt` turns out to be something like "the currently active default model's family," and one of our ids' loose parsing coincidentally matches some other logic path, or if there's a scope subtlety making `sZ` behave differently than this trace suggests. Find the `$3` module's full body and pin down `jIt`'s actual value before ruling this out.

## 5. Most important open question — was the negative test even valid?

Both times the user reported "still only one model showing," the screenshot's Claude Code window showed working directory `~\OneDrive\Desktop\Programs` — **not** `...\Desktop\Programs\misc\LLM-TokenOptimizer` (this repo) or any other path that looks like a project selected in the TokenOptimizer app's Session tab. Both screenshots also showed an identical, word-for-word `claude-mem` "This project has no memory yet" first-run banner — suspicious for being the *same* pre-existing terminal tab/session screenshotted twice, rather than a fresh process spawned by `LaunchTickedModelsAsync` after the fix.

**Before doing any further binary spelunking, rule this out first, cheaply:**
1. Rebuild: `dotnet build app/TokenOptimizer.slnx` from the repo root, then `dotnet test app/TokenOptimizer.slnx --no-build` (expect 169 passed, 0 failed — that's the established baseline; do not proceed if this regresses).
2. Kill any existing `TokenOptimizer.App` and stray `claude.exe` processes so you can be sure what's new:
   ```powershell
   Get-Process TokenOptimizer.App -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
   (Do **not** kill `claude.exe` processes blindly if the user might be using one for something unrelated — ask, or just check `Get-Process claude -ErrorAction SilentlyContinue | Select Id,Path,StartTime` first and use judgment.)
3. Launch the freshly built app: `app/src/TokenOptimizer.App/bin/Debug/net10.0/TokenOptimizer.App.exe`.
4. In the Session tab: select a project (any is fine, but note the path so you can confirm the child process's cwd matches). Tick at least one clearly non-Claude model (a Groq or OpenCode model). Click Launch.
5. A **new** terminal window should open. Confirm its working directory (shown in the Claude Code banner, third line) matches the project you selected — if it instead shows an unrelated stale path, something is wrong with how the app resolves `SelectedProject.FullPath` or you're looking at a leftover window; investigate that first, it would explain everything without any further binary work.
6. In that confirmed-fresh window, run `/model`. Report exactly what appears.
7. If ticked models still don't appear, proceed to section 6's live diagnostics — do not keep guessing from static analysis alone.

## 6. If the fresh-process test still fails: live diagnostics to run next

**a. Confirm the env vars actually reached the child process.** From the TokenOptimizer app's own Log pane (or by adding a temporary `Log($"...")` call right after `Process.Start(psi)` in `LaunchTickedModelsAsync`), print `psi.EnvironmentVariables["ANTHROPIC_BASE_URL"]`, `["CLAUDE_CODE_USE_GATEWAY"]`, `["ANTHROPIC_AUTH_TOKEN"]`, `["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"]` right before/after `Process.Start`. This rules out a silent .NET `ProcessStartInfo` env-var propagation bug (unlikely, but free to check).

**b. Confirm the router actually receives the `GET /v1/models` request from the CLI.** Add temporary logging inside `UnifiedModelRouter`'s request-handling loop (wherever it dispatches on path — read the full file first, it's `app/src/TokenOptimizer.Providers/Compat/UnifiedModelRouter.cs`) to append every incoming request's method, path, and headers to a fixed scratch file (e.g. `File.AppendAllText(Path.Combine(Path.GetTempPath(), "router-requests.log"), ...)`). Relaunch, check `/model`, then read that log file:
   - If the `GET /v1/models?limit=1000` request with an `Authorization: Bearer tokenoptimizer-local-gateway` header **never appears** → the CLI never called `igw()` at all → something in sections 3-4's gating logic is not actually satisfied for this real process, despite the "Cloud gateway" banner label appearing (i.e., `to()==="gateway"` might be true, but something else — a caching layer, a different code path, an additional undiscovered gate — is short-circuiting before `igw()` fires). Go re-read `ngw()`'s exact call site and whatever calls `ngw()` in turn (search the binary for `ngw(` callers) to find what decides whether `ngw()` runs at all during startup, and under what conditions its result actually gets merged into the `/model` UI's option list (see part **d** below — this is probably the real remaining gap).
   - If the request **does** arrive → capture the exact response bytes the router sent back (log the outgoing JSON body too) and manually validate it by hand against the `ogw` zod schema in section 4. Also double check the `Authorization` header value the router received matches what was expected, and that the router isn't (surprisingly) requiring some auth it silently rejects requests without (re-read the full router file, not just the grep snippet in section 2, to be sure).

**c. If needed, try to surface the CLI's own internal `[Bootstrap]` debug logging directly, without any TokenOptimizer-side instrumentation.** These log lines (`"[Bootstrap] Fetching"`, `"[Bootstrap] Gateway /v1/models → N custom options"`, `"[Bootstrap] Gateway /v1/models failed validation: ..."`, `"[Bootstrap] Gateway /v1/models fetch failed: ..."`) are emitted via a logger function minified as `w(...)`. Find `w`'s definition in the binary (`Select-String -Path <exe> -Pattern 'function w\(' -Encoding ascii`, there will likely be many hits for the single-letter name — narrow using nearby context from one of the call sites captured above) to determine whether its output is gated behind an env var (candidates already found in the binary's env-var string table, purpose **unconfirmed**, worth trying directly by launching `claude.exe` manually from a plain terminal — not through the app — with the full env var set from section 3 plus each of these added one at a time: `CLAUDE_GATEWAY_LOG_LEVEL=debug` (or `verbose`/`trace`), `DEBUG=1`, or checking if there's a documented `--debug` / `--verbose` CLI flag (`claude --help` output) that turns on this internal logger's output to stderr or a log file. This is likely the fastest way to get a direct, authoritative answer straight from the CLI itself about what `igw()` actually did on a given run, without needing to trust static analysis of the bundle at all.

**d. Find where `additional_model_options` is *read* (not just produced).** Everything traced so far is about *producing* the `{additional_model_options: [...]}` value from `ngw()`/`igw()`. It was never confirmed that this value actually flows into the `/model` picker's rendered list of options — there could be a disconnect (a caching layer that persists an earlier, empty/stale result to disk and never re-fetches; a UI component that reads from a different in-memory store that only gets populated once at a different point in startup; a feature flag gating whether the picker even looks at `additional_model_options` at all). Search the binary for other occurrences of `additional_model_options` beyond the ones already found (the write site in `igw()`, and the read site referenced in the bootstrap-cache logic in section "confirmed" text — that cache logic block, partially captured, mentioned `additionalModelOptionsCache` being read/written to a persisted `clientDataCacheSlots` structure — this looks like exactly the kind of stale-cache mechanism that could keep showing an old empty result even after `igw()` starts succeeding; find where that cache is stored on disk (likely under `%USERPROFILE%\.claude\` somewhere) and consider whether it needs to be cleared/invalidated, or whether its staleness-check logic (`H`/`O` boolean flags seen in the partial dump) has a bug or an unmet precondition preventing a fresh write). Also search for whatever React/ink component actually renders the `Select model` list (look for the string `"Select model"` itself, or `"Use the default model"`, in the binary and work backward from there) to see exactly which data source it pulls its option list from, and whether that's the same object `ngw()`'s caller populates.

## 7. Method notes for working with the `claude.exe` binary

- It's a ~319MB PE binary containing a minified JS bundle. Loading the whole thing into one PowerShell string (`[System.IO.File]::ReadAllBytes` + `[System.Text.Encoding]::ASCII.GetString`) is extremely slow/can hang — **do not do this**, it timed out at 60s+ in a prior attempt.
- Instead, use PowerShell's streaming `Select-String`, which is fast even on the full file:
  ```powershell
  Select-String -Path "C:\Users\ericc\.local\bin\claude.exe" -Pattern "SOME_LITERAL_OR_REGEX" -Encoding ascii -AllMatches |
    ForEach-Object {
      foreach ($m in $_.Matches) {
        $start = [Math]::Max(0, $m.Index - 200)
        $len = [Math]::Min(500, $_.Line.Length - $start)
        $_.Line.Substring($start, $len)
        "---"
      }
    }
  ```
  Adjust the `-200`/`500` window to get more/less context. This is the technique that produced every code snippet in this document.
- The `Grep` tool (ripgrep-backed) also finds matches in the binary but reports only `"binary file matches"` without showing content — useful only to confirm a string exists at all, not to read context. Use the PowerShell method above for anything requiring actual surrounding code.
- Minified identifiers are short and **reused across unrelated module scopes** by the bundler (e.g. two completely different `jIt` variables exist in different modules) — when chasing a specific identifier's definition, always widen the context window around a known nearby anchor (a call site, an adjacent function) rather than grepping the bare identifier alone, or you'll cross-contaminate results from a different module.
- `.NET`'s `HttpListener` on Windows is backed by the kernel-mode `http.sys` driver — a listening socket bound this way does **not** show up under the hosting process's own PID in `netstat -ano` or `Get-NetTCPConnection -OwningProcess <pid>` (it appears to belong to PID 4 / System instead). Don't mistake this for "the router never started." To verify the router is actually up and serving, HTTP-probe candidate ports directly instead:
  ```powershell
  Invoke-WebRequest -Uri "http://127.0.0.1:<port>/v1/models" -UseBasicParsing
  ```
- The Bash tool's `curl`/`wget` are intercepted/redirected by a `context-mode`/graphify hook installed in this environment ("context-mode: curl/wget redirected..."). Use the PowerShell tool's `Invoke-WebRequest` for raw HTTP probes instead — it is not intercepted.

## 8. Constraints / house rules for this repo

- `dotnet build app/TokenOptimizer.slnx` then `dotnet test app/TokenOptimizer.slnx --no-build` must both stay clean (0 errors, and the established 169/169 tests passing) after any change. If a test needs updating because new intended behavior genuinely changed, that's fine — but understand *why* before changing an assertion, don't just make it pass.
- Don't add unrequested abstractions, fallback shims, or defensive code for scenarios that can't happen. Keep any temporary debug logging added per section 6 clearly temporary (or remove it once the real bug is found and fixed) — don't leave permanent verbose logging in hot request paths as a side effect of debugging.
- Never commit without the user explicitly asking.
- ## 9. RESOLVED (2026-08-26, claude.exe 2.1.238): pipeline verified end-to-end

Empirical verification against a fake loopback gateway (`HTTPServer` on 127.0.0.1 serving
`GET /v1/models` with `claude-gateway-*` ids), a real `-p` probe session, and
`--debug --debug-file <path>` (this flag pair exists in `claude.exe --help` and emits the
internal `[Bootstrap]` logger directly - section 6c's question answered):

```
[Bootstrap] Gateway /v1/models -> 2 custom options
[Bootstrap] Cache updated, persisting to disk
```

and afterwards `~/.claude.json` contains `additionalModelOptionsCache` with exactly those
two options. The whole chain works as designed in this version.

### What the open questions actually were

- **Filter 2 / `jIt` (section 4): exonerated.** Found the `$3` module initializer:
  `jIt = oae(cd.fable5)` - it is the *fable5 family config object* (the current default
  model's family), not a magic string. `sZ(id)` returns a whole family-config object or
  `null`; our disguised ids never equal any catalogued Claude id, so `sZ()` returns `null`
  and every `claude-gateway-*` id passes filter 2. No hidden rejection there.
- **Plain-HTTP localhost gate (`l7o`): passes** for `127.0.0.1`, confirmed by the live
  fetch succeeding without any https upgrade.
- **Where the picker list comes from (section 6d): the persisted cache, not the live
  fetch.** Flow: `gSt()` -> `ngw()` (gateway branch) -> `igw()` fetch -> result written to
  `clientState.additionalModelOptionsCache` in `~/.claude.json`. The `/model` picker
  builder (`aYb()`) merges `vXe()` = sanitized read of that cached array whenever provider
  mode is `"firstParty"` or `"gateway"` (the merge is NOT gated on first-party-only).
  Consequences: (a) if bootstrap never ran with discovery enabled, the cache stays empty
  forever and the picker shows only Default - exactly the historical symptom; (b) entries
  land once the CLI's startup bootstrap completes - a `/model` opened before that shows
  nothing yet; (c) the cache survives across sessions, so one good launch fixes later ones.
- **Section 5 was right**: earlier "still broken" evidence was stale-window screenshots,
  plus expecting ticked models in a window whose process had bootstrapped before the env
  fix or before the router was reachable.

### Additional lever found while tracing the picker

`ANTHROPIC_CUSTOM_MODEL_OPTION` (+ optional `ANTHROPIC_CUSTOM_MODEL_OPTION_NAME` /
`ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION`) injects an entry into the same picker list
unconditionally - no gateway mode, no fetch, no cache. Useful as a guaranteed-visible
hint even when bootstrap timing hides the fetched list.

### Guidance for TokenOptimizer (MainViewModel launch env)

Keep `CLAUDE_CODE_USE_GATEWAY=1` + `ANTHROPIC_AUTH_TOKEN` +
`CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1` exactly as they are. Optionally surface a
note in the app UI that the CLI's own `/model` list populates right after launch (bootstrap
fetch) and persists per-machine in `%USERPROFILE%\.claude.json`. To always show at least
one branded entry, set `ANTHROPIC_CUSTOM_MODEL_OPTION` to the default ticked model id.

Debugging recipe for future sessions: launch claude.exe with `--debug --debug-file
<abs path>` and grep it for `[Bootstrap]`; do not re-spelunk the binary for logger gates.

If, after real investigation, this turns out to be a hard platform wall (e.g., Anthropic added a server-side check that only genuine enterprise-gateway-authenticated accounts get any `additional_model_options` at all, regardless of what the local `/v1/models` proxy returns — this cannot be ruled out from the client-side bundle alone, since the "Cloud gateway" mode might also involve a server-side handshake/verification against Anthropic's own backend that a fake local JWT cannot satisfy) — say so plainly and explain the evidence, rather than continuing to guess at client-side tweaks indefinitely. If it is a hard wall, the fallback plan is: drop the goal of populating Claude Code's own `/model` picker, and instead let the TokenOptimizer app itself be the only place a user switches models (already partially true — the app already builds a working default `--model` launch arg and the router already does correct per-request routing/fallback regardless of what shows in the picker). That's an acceptable, simpler outcome if the gateway-discovery path is confirmed to be genuinely unreachable — don't chase it forever without checking in.
