# /// script
# requires-python = ">=3.10"
# dependencies = [
#   "openai>=1.0.0",
# ]
# ///
"""
LM Studio local-model benchmark pipeline.

Downloads each model in MODEL_LIST via the `lms` CLI, loads it into LM
Studio's local server, runs it against the architecture-specific TEST_PROMPTS,
records TTFT/tokens-per-second, then unloads and purges the model from disk
before moving to the next pair - so disk usage never grows past ~2 models'
worth of weights at a time.

Run with uv (reads the inline dependency block above automatically):
    uv run run_benchmarks.py
    uv run run_benchmarks.py --dry-run
    uv run run_benchmarks.py --models qwen/qwen3.6-35b-a3b,openai/gpt-oss-20b
    uv run run_benchmarks.py --keep-on-disk --skip-download

Requires LM Studio installed with its CLI (`lms`) on PATH - confirmed present
at ~/.lmstudio/bin/lms.exe once LM Studio has been run at least once. Get it
from https://lmstudio.ai if `lms` isn't found.
"""
import argparse
import ast
import csv
import io
import json
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path

from openai import OpenAI

# ----------------------------------------------------------------------------
# Windows consoles default to the system codepage (cp1252 etc.), not UTF-8 -
# a model emitting so much as a single emoji or curly quote in its response
# crashes a bare print() with UnicodeEncodeError (reproduced live: gemma-3n-e4b
# answering "Say OK" with a smiley emoji killed the process here). Reconfigure
# stdout/stderr to UTF-8 with lossy replacement instead of failing outright.
# ----------------------------------------------------------------------------
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

LMS_BASE_URL = "http://localhost:1234/v1"
LMS_SERVER_PORT = 1234
# 1, not 2: this pipeline has to run unattended on machines with unknown free
# disk space (that's the whole point of the per-model purge cycle). Two
# concurrent downloads means two models' worth of peak disk usage instead of
# one, and this was live-confirmed to actually bite: a batch-size-2 run
# exhausted disk mid-download and silently skipped several models with
# "only N GB free" warnings that a batch-size-1 run right after didn't hit.
# Override with --batch-size on a machine you know has room to spare.
DOWNLOAD_BATCH_SIZE = 1
# 1536 originally - raised after a live run confirmed it's not enough for a
# reasoning/"thinking" model: qwen3.6-35b-a3b spent its ENTIRE 1536-token
# budget on reasoning_content and never reached a final answer on any of the
# 4 prompts (0 completion tokens every time, ~3 minutes each). A follow-up
# live probe measured the actual reasoning cost: even a trivial "write a
# one-liner" prompt burned ~286 reasoning tokens before the model answered.
# These benchmark prompts ask for a full class plus unit tests, so budget
# needs real headroom for reasoning AND a complete answer, not just one.
# Balanced against wall-clock cost: this model measured ~8 tok/s on this
# machine (partial GPU offload for a 22 GB model), so pushing much higher
# multiplies run time steeply. A response that still gets truncated at 4096
# degrades gracefully (score_capability falls back to scoring whatever
# reasoning text exists) rather than corrupting the run.
DEFAULT_MAX_TOKENS = 4096
DEFAULT_MIN_FREE_GB = 15  # safety buffer left after a model's estimated size

# Model catalog IDs - corrected against real `lms get` resolution failures,
# not just web research (research alone got one wrong: qwen2.5-coder-7b-
# instruct looked right but LM Studio's actual catalog slug drops
# "-instruct", same as qwen3-coder-30b; confirmed live via `lms get
# qwen/qwen2.5-coder-7b`, which resolved to a real 4.68 GB GGUF while the
# "-instruct" form errored "does not exist"). Also fixed two wrong
# publisher/slug combos found via web research (gpt-oss/gpt-oss-20b ->
# openai is the actual publisher; zai-org/glm-4.6v -> the released variant
# is glm-4.6v-flash). Kat-Coder and Nemotron were single placeholder names
# in the original list; expanded to 3 models each per family as requested,
# picked to span small/fast -> large/flagship so the comparison is actually
# informative. Sizes are APPROXIMATE (default quant for a typical consumer
# GPU) - used only for the disk-space preflight check below, not for exact
# accounting. Given how easily one of these slugs was still wrong despite
# research, treat every entry as "best effort" - a failed `lms get` for any
# of them just skips that one model (see download_model) rather than
# aborting the run.
MODEL_LIST = [
    # --- Top 5 general/coding picks ---
    {"id": "qwen/qwen3.6-35b-a3b",          "size_gb": 22},
    {"id": "qwen/qwen3-coder-30b",          "size_gb": 19},
    {"id": "zai-org/glm-4.7-flash",         "size_gb": 18},
    {"id": "openai/gpt-oss-20b",            "size_gb": 12},
    {"id": "qwen/qwen2.5-coder-7b",         "size_gb": 5},
    # --- Models 6-10: multimodal-capable & variants ---
    {"id": "qwen/qwen3-vl-8b",              "size_gb": 6},
    {"id": "google/gemma-4-26b-a4b",        "size_gb": 15},
    {"id": "qwen/qwen3-vl-30b-a3b",         "size_gb": 18},
    {"id": "qwen/qwen3.6-27b",              "size_gb": 16},
    {"id": "zai-org/glm-4.6v-flash",        "size_gb": 6},
    # --- 3 best Kwaipilot KAT models (small -> flagship) ---
    {"id": "kwaipilot/kat-dev",             "size_gb": 20},
    {"id": "kwaipilot/kat-coder-v2.5-dev",  "size_gb": 20},
    {"id": "kwaipilot/kat-dev-72b-exp",     "size_gb": 45},
    # --- 3 best NVIDIA Nemotron models (small -> flagship) ---
    {"id": "nvidia/nemotron-3-nano",        "size_gb": 18},
    {"id": "nvidia/nemotron-3.5-lightning", "size_gb": 18},
    {"id": "nvidia/nemotron-3-super",       "size_gb": 70},
]

# Tailored test prompts derived from your GitHub architecture (Neiro & Restoration-Workflow)
# Each "checks" list is a SWE-bench-style rubric for that prompt: regexes
# matched against the model's response, standing in for "does this patch
# actually address the spec" when there's no hidden test suite to execute
# against. See score_capability() below for how these combine with a hard
# syntax-validity gate into a per-test capability score.
TEST_PROMPTS = [
    {
        "name": "1. Stream & Memory-Mapped Chunking",
        "prompt": "Write a Python class using NumPy and memory-mapped files (mmap) that processes a large binary stream or multidimensional array in overlapping chunks. It must maintain an internal state for overlap-add buffering, accept a transformation function callback, and process the entire dataset iteratively without loading the full file into RAM. Include strict boundary handling for edge chunks, type hints, and a unit test simulating a stream larger than available memory.",
        "checks": [
            r"\bimport\s+numpy\b|\bimport\s+numpy\s+as\s+np\b",
            r"\bimport\s+mmap\b|mmap\.mmap\(",
            r"\bclass\s+\w+",
            r"->\s*[\w\[\]\.]+|:\s*(int|float|str|bytes|np\.ndarray|Optional|Callable)",
            r"overlap",
            r"\bdef\s+test_\w+|\bunittest\b|\bassert\s",
            r"boundary|edge[\s_-]?case",
        ],
    },
    {
        "name": "2. Tauri-Python Subprocess & IPC Lifecycle",
        "prompt": "Write a robust Python script using FastAPI or a lightweight HTTP server designed to act as a local sidecar engine. Implement a background worker thread with a thread-safe queue, a `/api/health` endpoint that reports active hardware state (RAM/VRAM usage), and graceful shutdown handling on SIGINT/SIGTERM. Then, write a corresponding Rust/Tauri or Node.js snippet that spawns this process, polls `/api/health` with exponential backoff until it responds, and safely handles unexpected process termination.",
        "checks": [
            r"fastapi|FastAPI|BaseHTTPRequestHandler|http\.server|HTTPServer",
            r"/api/health",
            r"SIGINT|SIGTERM|signal\.signal",
            r"\bQueue\(|queue\.Queue|Queue\[",
            r"tauri|Tauri|spawn\(|child_process|Command::new",
            r"backoff|exponential",
        ],
    },
    {
        "name": "3. Dynamic Node Graph & Pipeline Orchestration",
        "prompt": "Implement a lightweight Directed Acyclic Graph (DAG) pipeline orchestrator in Python. Nodes must carry execution stage ranks (e.g., pre-process -> transform -> post-process). The engine must support: 1. Auto-ordering a collection of nodes based on their stage constraints. 2. Dynamic insertion of intermediate nodes (e.g., injecting an auto-generated mask generator node if an inpainting node requires one). 3. A content-addressed caching mechanism where node outputs are hashed, and re-running a pipeline skips unchanged subgraph branches. Provide unit tests verifying topological sorting and cache invalidation.",
        "checks": [
            r"topological|toposort|topo_sort|topological_sort",
            r"\bclass\s+\w*(Node|Pipeline|DAG|Graph)",
            r"hash|sha256|md5|content[\s_-]?address",
            r"cache|Cache",
            r"\bdef\s+test_\w+|\bunittest\b|\bassert\s",
            r"stage|rank",
        ],
    },
    {
        "name": "4. Hardware Graceful Degradation & VRAM Fallback",
        "prompt": "Design a Python VRAM/Memory Manager class for a local model runner. It should track active model weights and memory footprints. Implement a 'downgrade ladder' pattern: when a target VRAM threshold is breached or an OutOfMemoryError is caught during execution, the manager must automatically attempt: 1. Evicting idle/cached models. 2. Converting/reloading weights to a lower precision (e.g., fp16 to quantized/CPU). 3. Falling back to a pure CPU-based DSP or lighter fallback path while returning a structured warning object detailing why the fallback occurred. Include mock tests simulating low-VRAM exceptions.",
        "checks": [
            r"\bclass\s+\w*(VRAM|Memory|Manager)",
            r"evict",
            r"quant|precision|fp16|int8|bfloat16",
            r"OutOfMemory|OOM|MemoryError",
            r"\bmock\b|Mock\(|unittest\.mock|MagicMock",
            r"warning|Warning",
        ],
    },
]

LOG_FILE = Path("run_benchmarks.log")


def log(message, level="INFO"):
    ts = datetime.now().strftime("%H:%M:%S")
    line = f"[{ts}] [{level}] {message}"
    print(line)
    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass  # logging to disk is best-effort; never let it break the run


# ----------------------------------------------------------------------------
# Capability scoring (SWE-bench-style: does the code actually work, not just
# how fast it was generated)
#
# Real SWE-bench resolves a task by running the repo's hidden test suite
# against the model's patch - pass or fail, no partial credit. We don't have
# a hidden test suite for these four open-ended architecture prompts, so this
# approximates the same spirit with what's actually checkable offline:
#   1. A hard gate - does the returned code even PARSE as valid Python? A
#      patch that doesn't run is a fail in SWE-bench regardless of how good
#      the prose around it looks, and the same logic applies here.
#   2. A requirement-coverage rubric (TEST_PROMPTS[i]["checks"]) - the
#      fraction of the prompt's explicitly-stated requirements the response
#      actually addresses, e.g. "did it implement the /api/health endpoint",
#      "did it handle SIGTERM", "did it include a unit test".
# ----------------------------------------------------------------------------
def _extract_code_blocks(text):
    blocks = re.findall(r"```(?:python|py)?\s*\n(.*?)```", text, re.DOTALL)
    return blocks if blocks else ([text] if text and text.strip() else [])


def _has_valid_python_syntax(code_blocks):
    for block in code_blocks:
        try:
            ast.parse(block)
            return True
        except (SyntaxError, ValueError):
            continue
    return False


def score_capability(test_name, response_text, checks):
    """Returns (capability_score 0..1, detail dict) for one test's response."""
    if not response_text or not response_text.strip():
        return 0.0, {"syntax_valid": False, "rubric_hits": 0, "rubric_total": len(checks)}

    code_blocks = _extract_code_blocks(response_text)
    syntax_valid = _has_valid_python_syntax(code_blocks) if code_blocks else False
    hits = sum(1 for pattern in checks if re.search(pattern, response_text, re.IGNORECASE))
    rubric_fraction = (hits / len(checks)) if checks else 1.0

    if not syntax_valid:
        # SWE-bench-style hard gate: code that doesn't parse is a fail,
        # regardless of how many keywords/requirements it happens to mention.
        return 0.0, {"syntax_valid": False, "rubric_hits": hits, "rubric_total": len(checks)}
    return round(rubric_fraction, 3), {"syntax_valid": True, "rubric_hits": hits, "rubric_total": len(checks)}


def compute_composite_score(resolve_rate, tokens_per_second, max_tokens_per_second):
    """Combines capability (SWE-bench-style resolve rate) with speed into one
    ranking score. Capability-weighted 70/30: a fast model that writes broken
    code is a bad coding agent no matter how many tokens/sec it produces, but
    speed still matters for picking a genuinely usable interactive fallback -
    it's the tiebreaker among models that are actually correct, not the other
    way around."""
    normalized_speed = (tokens_per_second / max_tokens_per_second) if max_tokens_per_second > 0 else 0.0
    return round(0.7 * resolve_rate + 0.3 * normalized_speed, 4)


# ----------------------------------------------------------------------------
# Preflight
# ----------------------------------------------------------------------------

def find_lms():
    lms = shutil.which("lms")
    if lms:
        return lms
    # LM Studio's CLI isn't always on PATH even when installed - it ships at
    # a fixed per-user location once LM Studio has been launched at least once.
    fallback = Path.home() / ".lmstudio" / "bin" / ("lms.exe" if sys.platform == "win32" else "lms")
    if fallback.exists():
        return str(fallback)
    return None


def run_lms(lms_path, args, timeout=None, stream_output=False):
    """Run an `lms` subcommand. Returns (success, combined_output)."""
    cmd = [lms_path] + args
    try:
        if stream_output:
            # Long-running steps (downloads, model loads) get their output
            # streamed live instead of captured - a multi-GB download with
            # zero visible output for 20+ minutes looks identical to a hang.
            # stdout is left inherited (still streams to the console/log),
            # but stderr IS captured - the previous version threw it away
            # entirely, which meant every failed download in a live run
            # showed up with detail="" and no way to tell a bad model id
            # from a network blip from disk exhaustion.
            proc = subprocess.run(cmd, timeout=timeout, encoding="utf-8", errors="replace",
                                   stdout=None, stderr=subprocess.PIPE)
            return proc.returncode == 0, (proc.stderr or "")
        else:
            proc = subprocess.run(
                cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout
            )
            return proc.returncode == 0, (proc.stdout or "") + (proc.stderr or "")
    except subprocess.TimeoutExpired:
        return False, f"TIMEOUT after {timeout}s running: {' '.join(cmd)}"
    except Exception as e:
        return False, f"EXCEPTION running {' '.join(cmd)}: {e}"


def _server_is_up(status_ok, status_out):
    # `lms server status` prints "The server is not running." when down and
    # "The server is running on port N." when up - a naive `"running" in
    # out.lower()` check matches BOTH (the word "running" appears in "not
    # running" too), so it reports the server as up when it's actually down.
    # Reproduced live: this exact bug made ensure_server_running() short-
    # circuit as "already running" while every benchmark call then failed
    # with "Connection error." Explicitly exclude the negative form instead.
    text = status_out.lower()
    return status_ok and ("running" in text) and ("not running" not in text)


def ensure_server_running(lms_path):
    """Idempotently make sure LM Studio's local API server is listening.

    Confirmed live: `lms load` does NOT start the API server on its own -
    after a successful load, `lms server status` still reports "not running"
    and every OpenAI-client call gets connection-refused. This is the reason
    the original version of this script never actually produced a benchmark.
    """
    ok, out = run_lms(lms_path, ["server", "status"])
    if _server_is_up(ok, out):
        log(f"LM Studio server already running: {out.strip()}")
        return True
    log("Starting LM Studio local server...")
    ok, out = run_lms(lms_path, ["server", "start", "--port", str(LMS_SERVER_PORT)], timeout=30)
    if not ok:
        log(f"Failed to start LM Studio server: {out.strip()}", "ERROR")
        return False
    # Poll briefly rather than trusting the start command's own exit code -
    # matches the "verify, don't just trust the reported success" pattern
    # used throughout this project's other tooling installers.
    for _ in range(10):
        ok, out = run_lms(lms_path, ["server", "status"])
        if _server_is_up(ok, out):
            log("LM Studio server is up.")
            return True
        time.sleep(1)
    log("Server start reported success but status never confirmed 'running'", "ERROR")
    return False


def check_disk_space(size_gb, min_free_gb=DEFAULT_MIN_FREE_GB):
    try:
        free_gb = shutil.disk_usage(Path.home()).free / (1024 ** 3)
    except OSError:
        return True, None  # can't check - don't block the run over it
    needed = size_gb + min_free_gb
    if free_gb < needed:
        return False, free_gb
    return True, free_gb


def get_installed_model_ids(lms_path):
    """Model IDs already fully present on disk, per `lms ls --json`.

    Without this, check_disk_space() ran unconditionally before every
    download attempt - including for a model that's ALREADY on disk, where
    `lms get` is a fast no-op verification, not a real download. On a
    machine with limited free space that wrongly skipped models that needed
    zero additional space (confirmed live: qwen/qwen3.6-35b-a3b, 22 GB,
    already downloaded, on a machine with 19.7 GB free - the naive check
    demanded 22+15=37 GB free and would have skipped it entirely).
    """
    ok, out = run_lms(lms_path, ["ls", "--json"])
    if not ok:
        return set()
    try:
        entries = json.loads(out)
    except json.JSONDecodeError:
        return set()
    return {e.get("modelKey") for e in entries if e.get("type") == "llm"}


# ----------------------------------------------------------------------------
# Model lifecycle
# ----------------------------------------------------------------------------

def download_model(lms_path, model, installed_ids=None, min_free_gb=DEFAULT_MIN_FREE_GB):
    model_id = model["id"]
    if installed_ids and model_id in installed_ids:
        log(f"[{model_id}] Already on disk - skipping download.")
        return model_id, True, ""

    ok_space, free_gb = check_disk_space(model["size_gb"], min_free_gb)
    if not ok_space:
        msg = (f"Skipping download - only {free_gb:.1f} GB free, need ~"
               f"{model['size_gb'] + min_free_gb} GB (model + safety margin)")
        log(f"[{model_id}] {msg}", "WARN")
        return model_id, False, msg

    log(f"[{model_id}] Downloading (~{model['size_gb']} GB estimated)...")
    ok, out = run_lms(lms_path, ["get", "-y", model_id], timeout=3 * 3600, stream_output=True)
    if ok:
        log(f"[{model_id}] Download complete.")
        return model_id, True, ""

    # `lms get`'s exit code can lie: confirmed live under this script's own
    # nohup/background/log-redirected invocation, three models (qwen3-vl-8b,
    # gemma-4-26b-a4b, qwen3.6-27b) reported a non-zero exit code yet ended
    # up fully downloaded and registered anyway - likely a progress-bar/TTY
    # detection quirk when stdout isn't a real console. Don't trust the exit
    # code alone; check the model registry before declaring failure.
    if model_id in get_installed_model_ids(lms_path):
        log(f"[{model_id}] Exit code indicated failure but the model is fully "
            f"registered - treating as success.")
        return model_id, True, ""

    log(f"[{model_id}] Download failed: {out.strip()[-500:]}", "ERROR")
    _cleanup_partial_download(model_id)
    return model_id, False, out.strip()[-500:]


def _cleanup_partial_download(model_id):
    """Remove incomplete `downloading_*` / `.part` artifacts a failed `lms
    get` leaves behind. These were never cleaned up before (the purge path
    only ever runs for downloads that reported success), so a genuinely bad
    model id or an interrupted download left multi-GB partial files sitting
    on disk permanently across every past run.

    Batches can download multiple models concurrently (ThreadPoolExecutor,
    one worker per batch member), so a blanket "remove every .part in the
    tree" would risk deleting a SIBLING model's still-in-progress download
    out from under it. Only remove a partial file whose parent directory
    name token-matches this model's own id - anything ambiguous is left
    alone rather than risk collateral damage.
    """
    models_dir = Path.home() / ".lmstudio" / "models"
    if not models_dir.exists():
        return
    tokens = [t for t in re.split(r"[/\-_. ]+", model_id.lower()) if len(t) > 2]
    removed_any = False
    for pattern in ("**/downloading_*", "**/*.part"):
        for f in models_dir.glob(pattern):
            parent_name = f.parent.name.lower()
            if not any(t in parent_name for t in tokens):
                continue
            try:
                f.unlink()
                removed_any = True
            except OSError:
                pass
    if removed_any:
        log(f"[{model_id}] Cleaned up incomplete download artifact(s).")


def load_model(lms_path, model_id, context_length=8192):
    log(f"[{model_id}] Loading into memory (context={context_length})...")
    ok, out = run_lms(
        lms_path,
        ["load", model_id, "--gpu", "max", "--context-length", str(context_length), "-y"],
        timeout=600,
    )
    if not ok:
        log(f"[{model_id}] Load failed: {out.strip()[-500:]}", "ERROR")
        return False, out.strip()[-500:]
    log(f"[{model_id}] Loaded.")
    return True, ""


def unload_all(lms_path):
    run_lms(lms_path, ["unload", "--all"], timeout=30)
    time.sleep(2)


def resolve_disk_path(lms_path, model_id):
    """Find the actual on-disk directory for a downloaded model.

    `lms ls --json`'s "path" field is NOT a filesystem path - it's just an
    alias for modelKey. Confirmed live: the JSON entry for
    "qwen/qwen3-coder-30b" reports `"path": "qwen/qwen3-coder-30b"`, while
    the real files live at
    .lmstudio/models/lmstudio-community/Qwen3-Coder-30B-A3B-Instruct-GGUF/
    - LM Studio's catalog often proxies to a re-quantizer's own repo (here
    "lmstudio-community"), which has nothing to do with the catalog id's
    publisher segment. Trusting that field made every purge attempt build
    a path like .lmstudio/models/qwen/ that never existed, so it always
    logged "nothing found" and silently left every model on disk - this is
    why free space kept shrinking across a run that was supposed to be
    self-cleaning.

    Locate the real directory instead by matching `sizeBytes` (the one
    field `lms ls --json` reports that isn't a guessed path) against actual
    .gguf file sizes on disk - summed PER DIRECTORY, not per file. A vision
    model ships a companion mmproj-*.gguf alongside the main weights, and
    `sizeBytes` reports their combined total (confirmed live: GLM-4.6V-Flash
    reports sizeBytes=7953555436, but its main weight file alone is
    6166577888 - a 22% gap that no per-file tolerance would ever bridge;
    summed with its 1786959488-byte mmproj file, the total matches within
    rounding).
    """
    ok, out = run_lms(lms_path, ["ls", "--json"])
    if not ok:
        return None
    try:
        entries = json.loads(out)
    except json.JSONDecodeError:
        return None
    entry = next((e for e in entries if e.get("modelKey") == model_id
                  or e.get("indexedModelIdentifier") == model_id), None)
    target_size = entry.get("sizeBytes") if entry else None
    models_dir = Path.home() / ".lmstudio" / "models"
    if not target_size or not models_dir.exists():
        return None
    dir_totals = {}
    for gguf in models_dir.rglob("*.gguf"):
        try:
            size = gguf.stat().st_size
        except OSError:
            continue
        dir_totals[gguf.parent] = dir_totals.get(gguf.parent, 0) + size
    tolerance = 0.05  # slack for rounding
    for directory, total in dir_totals.items():
        if abs(total - target_size) / target_size <= tolerance:
            return directory
    return None


def delete_model_from_disk(lms_path, model_id):
    target_dir = resolve_disk_path(lms_path, model_id)
    if target_dir and target_dir.exists():
        try:
            shutil.rmtree(target_dir)
            log(f"[{model_id}] Purged from disk: {target_dir}")
        except OSError as e:
            log(f"[{model_id}] Could not purge {target_dir}: {e}", "WARN")
    else:
        log(f"[{model_id}] Could not locate on-disk directory to purge (already gone, "
            f"or no size match in `lms ls --json`)", "WARN")


# ----------------------------------------------------------------------------
# Benchmarking
# ----------------------------------------------------------------------------

def run_benchmarks_for_model(model_id, max_tokens):
    client = OpenAI(base_url=LMS_BASE_URL, api_key="not-needed")
    results = []

    log(f"[{model_id}] Running {len(TEST_PROMPTS)} architectural benchmarks...")

    for test in TEST_PROMPTS:
        log(f"[{model_id}]  -> {test['name']}")
        messages = [{"role": "user", "content": test["prompt"]}]

        start_time = time.time()
        first_token_time = None
        reasoning_tokens = 0
        completion_tokens = 0
        reasoning_parts = []
        full_response_parts = []

        try:
            stream = client.chat.completions.create(
                model=model_id,
                messages=messages,
                temperature=0.2,
                max_tokens=max_tokens,
                stream=True,
                timeout=300,
            )
            for chunk in stream:
                if not chunk.choices:
                    continue
                delta = chunk.choices[0].delta
                if not delta:
                    continue
                # "Thinking"/reasoning models (confirmed live: qwen3.6-35b-a3b)
                # stream their chain-of-thought via a non-standard
                # `reasoning_content` delta field BEFORE any `content` field
                # ever appears - LM Studio's OpenAI-compat server exposes it
                # even though the official OpenAI schema doesn't define it.
                # Reading only `delta.content` (the original version of this
                # script) silently measured 0 tokens / 0 tok/s for every
                # reasoning model: the model was genuinely generating for
                # 3+ minutes per prompt, none of it ever landed anywhere.
                # getattr(..., None) instead of a direct attribute access
                # because the openai SDK's ChoiceDelta type doesn't declare
                # this field - it's only present as a pydantic "extra" attr.
                reasoning_piece = getattr(delta, "reasoning_content", None) or getattr(delta, "reasoning", None)
                content_piece = delta.content
                if reasoning_piece or content_piece:
                    if first_token_time is None:
                        first_token_time = time.time()
                if reasoning_piece:
                    reasoning_parts.append(reasoning_piece)
                    reasoning_tokens += 1
                if content_piece:
                    full_response_parts.append(content_piece)
                    completion_tokens += 1

            end_time = time.time()
            ttft = (first_token_time - start_time) if first_token_time else 0
            generation_time = (end_time - first_token_time) if first_token_time else (end_time - start_time)
            total_tokens = reasoning_tokens + completion_tokens
            tok_per_sec = (total_tokens / generation_time) if generation_time > 0 else 0

            reasoning_text = "".join(reasoning_parts)
            content_text = "".join(full_response_parts)
            # Score against content if the model actually finished thinking
            # and answered; otherwise fall back to the reasoning trace itself
            # (reasoning models often draft/refine code IN their thinking
            # before ever reaching a final answer - a response truncated
            # mid-thought is itself a real failure mode worth scoring, not
            # something to hide by only looking at an empty `content`).
            scoring_text = content_text if content_text.strip() else reasoning_text
            full_output = (f"<think>\n{reasoning_text}\n</think>\n\n{content_text}"
                           if reasoning_text else content_text)
            capability_score, capability_detail = score_capability(test["name"], scoring_text, test.get("checks", []))

            log(f"[{model_id}]     TTFT: {ttft:.3f}s | Speed: {tok_per_sec:.2f} tok/s | "
                f"Tokens: {total_tokens} (reasoning={reasoning_tokens}, answer={completion_tokens}) | "
                f"Capability: {capability_score:.2f} (syntax_valid={capability_detail['syntax_valid']}, "
                f"rubric={capability_detail['rubric_hits']}/{capability_detail['rubric_total']})")

            results.append({
                "test_name": test["name"],
                "model": model_id,
                "status": "ok",
                "ttft_seconds": round(ttft, 3),
                "reasoning_tokens": reasoning_tokens,
                "completion_tokens": completion_tokens,
                "tokens_per_second": round(tok_per_sec, 2),
                "total_tokens": total_tokens,
                "capability_score": capability_score,
                "capability_detail": capability_detail,
                "full_code_output": full_output,
            })
        except Exception as e:
            log(f"[{model_id}]     FAILED: {e}", "ERROR")
            results.append({
                "test_name": test["name"],
                "model": model_id,
                "status": "error",
                "error": str(e),
            })

    filename = f"benchmark_{model_id.replace('/', '_').replace(':', '_')}.json"
    with open(filename, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)
    log(f"[{model_id}] Saved -> {filename}")
    return results


# ----------------------------------------------------------------------------
# Orchestration
# ----------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--models", type=str, default=None,
                    help="Comma-separated subset of model IDs to run (default: all in MODEL_LIST)")
    p.add_argument("--max-tokens", type=int, default=DEFAULT_MAX_TOKENS)
    p.add_argument("--context-length", type=int, default=8192)
    p.add_argument("--batch-size", type=int, default=DOWNLOAD_BATCH_SIZE)
    p.add_argument("--min-free-gb", type=int, default=DEFAULT_MIN_FREE_GB,
                    help="Safety margin left free after each download (default: %(default)s GB)")
    p.add_argument("--keep-on-disk", action="store_true", help="Don't delete models after benchmarking")
    p.add_argument("--skip-download", action="store_true", help="Assume models are already on disk")
    p.add_argument("--dry-run", action="store_true", help="Print the execution plan and exit")
    p.add_argument("--rescore", action="store_true",
                    help="Recompute capability/composite scores from already-saved benchmark_*.json "
                         "files in the current directory - no LM Studio calls, no downloads.")
    return p.parse_args()


def rescore_existing_results():
    """Recomputes capability_score/resolve_rate/composite_score from
    benchmark_<model>.json files already on disk (each holds the full saved
    model responses), and rewrites benchmark_summary.json/.csv from them.

    Exists so a scoring-logic change (like this one) can be applied to a run
    that's already in flight or already finished, WITHOUT re-running any LLM
    calls - the responses are already saved, only the scoring is new.
    """
    result_files = sorted(Path(".").glob("benchmark_*.json"))
    result_files = [f for f in result_files if f.name != "benchmark_summary.json"]
    if not result_files:
        log("No benchmark_<model>.json files found in the current directory - nothing to rescore.", "WARN")
        return

    summary = []
    for f in result_files:
        try:
            results = json.loads(f.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as e:
            log(f"Skipping {f.name}: {e}", "WARN")
            continue
        if not results:
            continue
        model_id = results[0].get("model", f.stem.replace("benchmark_", "").replace("_", "/", 1))

        changed = False
        for r in results:
            if r.get("status") != "ok":
                continue
            test = next((t for t in TEST_PROMPTS if t["name"] == r["test_name"]), None)
            checks = test["checks"] if test else []
            score, detail = score_capability(r["test_name"], r.get("full_code_output", ""), checks)
            r["capability_score"] = score
            r["capability_detail"] = detail
            changed = True
        if changed:
            f.write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")

        ok_count = sum(1 for r in results if r["status"] == "ok")
        resolve_rate = round(
            sum(r.get("capability_score", 0) for r in results if r["status"] == "ok") / ok_count, 3
        ) if ok_count else 0
        avg_tps = round(
            sum(r["tokens_per_second"] for r in results if r["status"] == "ok") / ok_count, 2
        ) if ok_count else 0
        summary.append({
            "model": model_id, "stage": "benchmark",
            "status": "ok" if ok_count == len(results) else "partial",
            "tests_ok": ok_count, "tests_total": len(results),
            "avg_tokens_per_second": avg_tps,
            "resolve_rate": resolve_rate,
        })
        log(f"[{model_id}] Rescored: resolve_rate={resolve_rate:.2f}, avg {avg_tps:.2f} tok/s "
            f"({ok_count}/{len(results)} tests ok)")

    max_tps = max((r["avg_tokens_per_second"] for r in summary if r["avg_tokens_per_second"]), default=0)
    for r in summary:
        r["composite_score"] = compute_composite_score(r.get("resolve_rate", 0), r["avg_tokens_per_second"], max_tps)

    with open("benchmark_summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    with open("benchmark_summary.csv", "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["model", "stage", "status", "tests_ok", "tests_total",
                                                "avg_tokens_per_second", "resolve_rate", "composite_score", "detail"])
        writer.writeheader()
        for row in summary:
            writer.writerow({k: row.get(k, "") for k in writer.fieldnames})

    print("\n" + "=" * 60)
    print(" RESCORED RESULTS (ranked by composite score: 70% capability, 30% speed)")
    print("=" * 60)
    ranked = sorted(summary, key=lambda r: r["composite_score"], reverse=True)
    for r in ranked:
        print(f"  {r['model']:<38} composite={r['composite_score']:.3f}  "
              f"resolve_rate={r['resolve_rate']:.2f}  {r['avg_tokens_per_second']:>7.2f} tok/s  "
              f"({r['tests_ok']}/{r['tests_total']} tests ok)")
    if ranked:
        best = ranked[0]
        print(f"\n  BEST CODING AGENT: {best['model']} "
              f"(composite={best['composite_score']:.3f}, resolve_rate={best['resolve_rate']:.2f}, "
              f"{best['avg_tokens_per_second']:.2f} tok/s)")


def main():
    args = parse_args()

    if args.rescore:
        rescore_existing_results()
        return

    models = MODEL_LIST
    if args.models:
        wanted = {m.strip() for m in args.models.split(",") if m.strip()}
        models = [m for m in MODEL_LIST if m["id"] in wanted]
        missing = wanted - {m["id"] for m in models}
        if missing:
            log(f"Unknown model id(s) ignored: {', '.join(sorted(missing))}", "WARN")

    print("=" * 60)
    print(f" LM STUDIO BENCHMARK PIPELINE - {len(models)} model(s)")
    print("=" * 60)

    if args.dry_run:
        for m in models:
            print(f"  - {m['id']}  (~{m['size_gb']} GB)")
        print(f"\nBatch size: {args.batch_size} | max_tokens: {args.max_tokens} | context: {args.context_length}")
        return

    lms_path = find_lms()
    if not lms_path:
        log("`lms` CLI not found on PATH or at ~/.lmstudio/bin - install LM Studio from "
            "https://lmstudio.ai and run it at least once, then retry.", "ERROR")
        sys.exit(1)
    log(f"Using lms CLI: {lms_path}")

    if not ensure_server_running(lms_path):
        log("Could not confirm the LM Studio server is running - aborting.", "ERROR")
        sys.exit(1)

    summary = []
    installed_ids = get_installed_model_ids(lms_path)
    if installed_ids:
        log(f"Already on disk (will skip download, benchmark directly): {sorted(installed_ids & {m['id'] for m in models})}")

    try:
        for i in range(0, len(models), args.batch_size):
            batch = models[i:i + args.batch_size]
            ids = [m["id"] for m in batch]
            log(f"### BATCH {i // args.batch_size + 1} ({i + 1}-{i + len(batch)}/{len(models)}): {ids} ###")

            download_ok = {m["id"]: True for m in batch}
            if not args.skip_download:
                with ThreadPoolExecutor(max_workers=len(batch)) as executor:
                    dl = lambda m: download_model(lms_path, m, installed_ids, args.min_free_gb)
                    for model_id, ok, err in executor.map(dl, batch):
                        download_ok[model_id] = ok
                        if not ok:
                            summary.append({"model": model_id, "stage": "download", "status": "failed", "detail": err})

            for model in batch:
                model_id = model["id"]
                if not download_ok.get(model_id):
                    log(f"[{model_id}] Skipping (download did not succeed)", "WARN")
                    continue

                unload_all(lms_path)
                loaded, load_err = load_model(lms_path, model_id, args.context_length)
                if not loaded:
                    summary.append({"model": model_id, "stage": "load", "status": "failed", "detail": load_err})
                    unload_all(lms_path)
                    continue

                try:
                    results = run_benchmarks_for_model(model_id, args.max_tokens)
                    ok_count = sum(1 for r in results if r["status"] == "ok")
                    # resolve_rate mirrors SWE-bench's headline "% resolved" -
                    # the average capability_score (syntax-valid AND rubric-
                    # covered) across this model's benchmark prompts.
                    resolve_rate = round(
                        sum(r.get("capability_score", 0) for r in results if r["status"] == "ok") / ok_count, 3
                    ) if ok_count else 0
                    summary.append({
                        "model": model_id, "stage": "benchmark",
                        "status": "ok" if ok_count == len(results) else "partial",
                        "tests_ok": ok_count, "tests_total": len(results),
                        "avg_tokens_per_second": round(
                            sum(r["tokens_per_second"] for r in results if r["status"] == "ok") / ok_count, 2
                        ) if ok_count else 0,
                        "resolve_rate": resolve_rate,
                    })
                except Exception as e:
                    log(f"[{model_id}] Unexpected benchmark failure: {e}", "ERROR")
                    summary.append({"model": model_id, "stage": "benchmark", "status": "failed", "detail": str(e)})
                finally:
                    unload_all(lms_path)

            if not args.keep_on_disk:
                # Never purge a model that was already on disk before this run
                # started - it's the user's own model, not something this
                # script downloaded and is responsible for cleaning up.
                purge_ids = [m["id"] for m in batch if download_ok.get(m["id"]) and m["id"] not in installed_ids]
                if purge_ids:
                    log(f"### PURGING BATCH FROM DISK: {purge_ids} ###")
                    for model_id in purge_ids:
                        delete_model_from_disk(lms_path, model_id)
                skipped_purge = [m["id"] for m in batch if m["id"] in installed_ids]
                if skipped_purge:
                    log(f"Leaving pre-existing model(s) on disk (not purging): {skipped_purge}")
    except KeyboardInterrupt:
        log("Interrupted by user - cleaning up before exit...", "WARN")
        unload_all(lms_path)
    finally:
        run_lms(lms_path, ["server", "stop"], timeout=15)

    # ------------------------------------------------------------------
    # Final diagnostic report - composite_score needs the fastest model's
    # tokens/sec across the WHOLE run to normalize speed, so it's computed
    # here as a post-pass rather than inline per-model above.
    # ------------------------------------------------------------------
    bench_rows = [r for r in summary if r.get("stage") == "benchmark" and r.get("avg_tokens_per_second")]
    max_tps = max((r["avg_tokens_per_second"] for r in bench_rows), default=0)
    for r in bench_rows:
        r["composite_score"] = compute_composite_score(r.get("resolve_rate", 0), r["avg_tokens_per_second"], max_tps)

    with open("benchmark_summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    with open("benchmark_summary.csv", "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["model", "stage", "status", "tests_ok", "tests_total",
                                                "avg_tokens_per_second", "resolve_rate", "composite_score", "detail"])
        writer.writeheader()
        for row in summary:
            writer.writerow({k: row.get(k, "") for k in writer.fieldnames})

    print("\n" + "=" * 60)
    print(" PIPELINE COMPLETE - SUMMARY (ranked by composite score: 70% capability, 30% speed)")
    print("=" * 60)
    ranked = sorted(bench_rows, key=lambda r: r["composite_score"], reverse=True)
    for r in ranked:
        print(f"  {r['model']:<38} composite={r['composite_score']:.3f}  "
              f"resolve_rate={r['resolve_rate']:.2f}  {r['avg_tokens_per_second']:>7.2f} tok/s  "
              f"({r['tests_ok']}/{r['tests_total']} tests ok)")
    if ranked:
        best = ranked[0]
        print(f"\n  BEST CODING AGENT: {best['model']} "
              f"(composite={best['composite_score']:.3f}, resolve_rate={best['resolve_rate']:.2f}, "
              f"{best['avg_tokens_per_second']:.2f} tok/s)")
    failed = [r for r in summary if r["status"] == "failed"]
    if failed:
        print(f"\n  {len(failed)} model(s) failed - see run_benchmarks.log and benchmark_summary.json:")
        for r in failed:
            print(f"    - {r['model']} (failed at {r['stage']})")
    print(f"\nFull report: benchmark_summary.json / benchmark_summary.csv")
    print(f"Log: {LOG_FILE}")


if __name__ == "__main__":
    main()
