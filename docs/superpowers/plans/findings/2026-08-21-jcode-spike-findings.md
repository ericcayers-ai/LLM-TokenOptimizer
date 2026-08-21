# jcode Phase 0 Spike Findings — 2026-08-21

Empirical verification of jcode v0.79.1 on Windows (x86_64), no Git Bash/WSL required.

## Task 0.1: Install + Windows-native confirmation

```
PS> irm https://jcode.sh/install.ps1 | iex
Installing jcode v0.79.1
  launcher: C:\Users\ericc\AppData\Local\jcode\bin\jcode.exe
  Verified SHA256: jcode-windows-x86_64.exe
  Validated jcode binary: v0.79.1
  Updated user PATH with C:\Users\ericc\AppData\Local\jcode\bin
```

- **0.1.1**: `jcode --version` returns `jcode v0.79.1 (993da322e)`, exit 0. No Git Bash, no WSL.
- **0.1.2**: Installed path = `C:\Users\ericc\AppData\Local\jcode\bin\jcode.exe`. User PATH updated. `ResolveOnPath("jcode")` finds it in fresh processes.

## Task 0.2: Headless JSON contract + exit codes

### 0.2.1 — Success case (OpenAI, after auth)

```json
Command: jcode --quiet run --json --provider openai "Reply with exactly: PONG"
Exit: 0
Stdout:
{
  "session_id": "session_monkey_1787315074438_d6be7d6289480da4",
  "provider": "OpenAI",
  "model": "gpt-5.6-terra",
  "text": "PONG",
  "usage": {
    "input_tokens": 13630,
    "output_tokens": 6,
    "cache_read_input_tokens": 0,
    "cache_creation_input_tokens": null
  }
}
Stderr: (empty)
Elapsed: 8.86s
```

### 0.2.2 — Failure case: invalid provider (clap validation)

```
Command: jcode --quiet run --json --provider nonexistent "Reply with exactly: PONG"
Exit: 2
Stderr: error: invalid value 'nonexistent' for '--provider <PROVIDER>'
  [possible values: jcode, claude, anthropic-api, openai, ...]
```

Exit code 2 = clap-level validation error (invalid enum value). Distinct from provider-not-configured.

### 0.2.2 — Failure case: provider not configured (before auth)

```
Command: jcode --quiet run --json --provider antigravity "Reply with exactly: PONG"
Exit: 1
Stderr: Error: No Antigravity tokens found. Run `jcode login --provider antigravity`.
```

Exit code 1 = provider-level error (auth missing). This is the code `ProcessSessionHandle`-style pass/fail logic should check.

### 0.2.3 — Auth-missing case

```
Command: jcode auth status --json
Exit: 0
Stdout: {"any_available": false, "providers": [...all 46 providers with "status":"not_configured"...]}
```

`auth status` always returns exit 0 regardless of auth state. The `--provider` flag is global (not a filter) — it always lists all providers.

### Exit code summary

| Condition | Exit code | stderr shape |
|---|---|---|
| Success | 0 | (empty) |
| Provider error (not configured, rate limit, backend error) | 1 | `Error: <message>` |
| CLI validation error (bad flag, missing required arg) | 2 | `error: <message>` |

**Verdict for `ProcessSessionHandle`:** exit code 0 = success, non-zero = failure. The existing "did the process start" check maps cleanly: start process → wait for exit → check exit code. No guessing needed.

## Task 0.3: Provider-specific verification

### 0.3.1 — Antigravity login

```json
Command: jcode login --provider antigravity --print-auth-url --json
Exit: 0
Stdout: {
  "status": "pending",
  "provider": "antigravity",
  "auth_url": "https://accounts.google.com/o/oauth2/v2/auth?...",
  "input_kind": "callback_url",
  "pending_path": "C:\\Users\\ericc\\.jcode\\pending-login\\antigravity.json"
}
```

```json
Command: jcode login --provider antigravity --callback-url '<callback-url>'
Exit: 1 (post-login smoke 429, but credentials saved)
Stdout: {
  "status": "authenticated",
  "provider": "antigravity",
  "credentials_path": "C:\\Users\\ericc\\.jcode\\antigravity_oauth.json",
  "email": "eric.c.ayers@gmail.com"
}
```

Post-login auth-test: credential_probe PASS, refresh_probe PASS, provider_smoke FAIL (HTTP 429 — Antigravity quota exhausted, not an auth failure).

`auth status` after login: `"status": "available"`, `"method": "OAuth"`, `"source": "~/.jcode/antigravity_oauth.json"`.

### 0.3.2 — Codex/OpenAI login

```json
Command: jcode login --provider openai --print-auth-url --json
Exit: 0
Stdout: {
  "status": "pending",
  "provider": "openai",
  "auth_url": "https://auth.openai.com/oauth/authorize?...",
  "input_kind": "callback_url",
  "pending_path": "C:\\Users\\ericc\\.jcode\\pending-login\\openai.json"
}
```

```json
Command: jcode login --provider openai --callback-url '<callback-url>'
Exit: 0
Stdout: {
  "status": "authenticated",
  "provider": "openai",
  "account_label": "openai-otter",
  "credentials_path": "C:\\Users\\ericc\\.jcode\\openai-auth.json"
}
```

Post-login auth-test: ALL PASS (credential_probe, refresh_probe, provider_smoke, tool_smoke). `AUTH_TEST_OK`.

### 0.3.2 — Cursor login

```
Command: jcode login --provider cursor --no-browser
Exit: 1
Output: Starting Cursor API key setup...
  Get your API key from: https://cursor.com/settings
  (Dashboard > Integrations > User API Keys)
  Paste your Cursor API key: Error: No API key provided.
```

jcode detected existing `AppData\Roaming\Cursor\auth.json` on `model list`, but login requires a **Cursor API key** (not OAuth). The desktop app's auth.json is a different auth mechanism.

### 0.3.3 — Real response through expected backend

- **OpenAI**: PONG test returned `"model":"gpt-5.6-terra"` — confirmed real OpenAI backend, not a fallback.
- **Antigravity**: 429 rate limit — can't verify backend response, but credentials are valid (OAuth completed, tokens stored, refresh works).

### 0.3.4 — OS session reuse

| Provider | Detects OS credentials? | Login mechanism | Reuses OS session? |
|---|---|---|---|
| Antigravity | No (no detectable credential file) | Google OAuth (fresh) | No — requires separate OAuth |
| Cursor | Yes (`AppData\Roaming\Cursor\auth.json`) | API key (not OAuth) | No — desktop app auth is different mechanism |
| Codex/OpenAI | Yes (`.codex/auth.json`) | OpenAI OAuth (fresh) | No — requires separate OAuth |

**Answer to0.3.4:** jcode does NOT reuse any existing OS-level session for any of the three candidate providers. Each requires its own auth setup. For Antigravity and OpenAI, this is a fresh OAuth flow. For Cursor, this is an API key from cursor.com/settings.

## Task 0.4: Resume/model flag behavior

### 0.4.1 — Resume

```
Command: jcode run --resume "Reply with exactly: RESUME_OK" (no session ID)
Exit: 2
Stderr: error: the following required arguments were not provided: <MESSAGE>
```

`--resume` without a RESUME value and without a MESSAGE fails. The `run` command always requires a `<MESSAGE>` argument.

```
Command: jcode run --resume session_monkey_1787315074438_d6be7d6289480da4 "Reply with exactly: RESUME_WITH_ID"
Exit: 0
Stdout: {
  "session_id": "session_monkey_1787315074438_d6be7d6289480da4",
  "provider": "OpenAI",
  "model": "gpt-5.6-terra",
  "text": "RESUME_WITH_ID",
  "usage": {
    "input_tokens": 13667,
    "output_tokens": 8,
    "cache_read_input_tokens": 13056,
    "cache_creation_input_tokens": null
  }
}
```

Resume with a valid session ID works. Prompt caching confirmed (cache_read_input_tokens: 13056). The session_id in the response matches the one passed via `--resume`.

**`SessionResumeMode` mapping:**
- `New` → omit `--resume` entirely
- `Continue` → `--resume <last_session_id>` (requires storing the session ID from the last launch)
- `Pick` → no clean non-interactive equivalent. jcode has no `--resume` without an ID that lists sessions in `--json` mode. Degrade to `New` with a log note.

### 0.4.2 — Model flag

```
Command: jcode run --provider openai --model gpt-5.6-terra "Reply with exactly: MODEL_OK"
Exit: 0
Stdout: {
  "session_id": "session_rat_1787315107291_4a59ae26d2026f10",
  "provider": "OpenAI",
  "model": "gpt-5.6-terra",
  "text": "MODEL_OK"
}
```

`--model` is accepted and passed through. The model field in the response confirms the requested model was used.

## Task 0.5: Pass/fail gate

| Provider | Auth confirmed | Real response | Exit codes known | Verdict |
|---|---|---|---|---|
| **OpenAI/Codex** | YES (OAuth, account: openai-otter) | YES (PONG, model: gpt-5.6-terra) | YES (0/1/2) | **PASS** |
| **Antigravity** | YES (OAuth, eric.c.ayers@gmail.com) | NO (429 quota exhausted — persistent, retried multiple times) | YES (0/1/2) | **FAIL** — stays on AntigravityAdapter. jcode's Antigravity generateContent integration hits a different quota pool than `agy` (selftest proved `agy` works fine). The migration would be a real regression. |
| **Cursor** | NO (needs API key) | NO | YES (0/1/2) | **FAIL** — stays on current adapter |

### Flags for JcodeHarnessAdapter (from Phase 0 findings)

```csharp
// Codex/OpenAI (only provider migrated to jcode)
jcodeProviderId: "openai"       // confirmed from auth status + run --provider openai
displayName: "Codex"            // unchanged from today's provider name (UI shows "Codex", not "OpenAI")

// Antigravity: FAIL (stays on AntigravityAdapter — jcode 429s where agy works)

// BuildArguments shape (confirmed):
$"--provider {jcodeProviderId}" + (model is null ? "" : $" --model {model}")
  + resumeMode switch {
      SessionResumeMode.New => "",
      SessionResumeMode.Continue => $" --resume {lastSessionId}",  // requires storing session ID
      SessionResumeMode.Pick => ""  // no non-interactive equivalent; degrade to New with log
    }
```

### Credential semantics (Task 6)

Codex today stores a real `OPENAI_API_KEY` via `ProxyCredentialStore`. After migration, `JcodeHarnessAdapter` only calls `HasCredential(FallbackProvider.Codex)` — never reads the stored value. jcode manages its own OpenAI OAuth via `jcode login --provider openai`. Existing stored API keys still make `HasCredential` return true (backward compatible). The opt-in UI/CLI copy should say "requires `jcode login --provider openai`" instead of "requires an OpenAI API key."
