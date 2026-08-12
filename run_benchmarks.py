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
import csv
import io
import json
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
DOWNLOAD_BATCH_SIZE = 2
DEFAULT_MAX_TOKENS = 1536
DEFAULT_MIN_FREE_GB = 15  # safety buffer left after a model's estimated size

# Model catalog IDs verified live against https://lmstudio.ai/models and
# HuggingFace as of Aug 2026 - the original list had two wrong publisher/
# slug combos (gpt-oss/gpt-oss-20b -> openai is the actual publisher;
# zai-org/glm-4.6v -> the released variant is glm-4.6v-flash) that would
# have made `lms get` fail outright. Kat-Coder and Nemotron were single
# placeholder names in the original list; expanded to 3 models each per
# family as requested, picked to span small/fast -> large/flagship so the
# comparison is actually informative. Sizes are APPROXIMATE (default quant
# for a typical consumer GPU) - used only for the disk-space preflight check
# below, not for exact accounting.
MODEL_LIST = [
    # --- Top 5 general/coding picks ---
    {"id": "qwen/qwen3.6-35b-a3b",          "size_gb": 22},
    {"id": "qwen/qwen3-coder-30b",          "size_gb": 19},
    {"id": "zai-org/glm-4.7-flash",         "size_gb": 18},
    {"id": "openai/gpt-oss-20b",            "size_gb": 12},
    {"id": "qwen/qwen2.5-coder-7b-instruct","size_gb": 5},
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
TEST_PROMPTS = [
    {
        "name": "1. Stream & Memory-Mapped Chunking",
        "prompt": "Write a Python class using NumPy and memory-mapped files (mmap) that processes a large binary stream or multidimensional array in overlapping chunks. It must maintain an internal state for overlap-add buffering, accept a transformation function callback, and process the entire dataset iteratively without loading the full file into RAM. Include strict boundary handling for edge chunks, type hints, and a unit test simulating a stream larger than available memory."
    },
    {
        "name": "2. Tauri-Python Subprocess & IPC Lifecycle",
        "prompt": "Write a robust Python script using FastAPI or a lightweight HTTP server designed to act as a local sidecar engine. Implement a background worker thread with a thread-safe queue, a `/api/health` endpoint that reports active hardware state (RAM/VRAM usage), and graceful shutdown handling on SIGINT/SIGTERM. Then, write a corresponding Rust/Tauri or Node.js snippet that spawns this process, polls `/api/health` with exponential backoff until it responds, and safely handles unexpected process termination."
    },
    {
        "name": "3. Dynamic Node Graph & Pipeline Orchestration",
        "prompt": "Implement a lightweight Directed Acyclic Graph (DAG) pipeline orchestrator in Python. Nodes must carry execution stage ranks (e.g., pre-process -> transform -> post-process). The engine must support: 1. Auto-ordering a collection of nodes based on their stage constraints. 2. Dynamic insertion of intermediate nodes (e.g., injecting an auto-generated mask generator node if an inpainting node requires one). 3. A content-addressed caching mechanism where node outputs are hashed, and re-running a pipeline skips unchanged subgraph branches. Provide unit tests verifying topological sorting and cache invalidation."
    },
    {
        "name": "4. Hardware Graceful Degradation & VRAM Fallback",
        "prompt": "Design a Python VRAM/Memory Manager class for a local model runner. It should track active model weights and memory footprints. Implement a 'downgrade ladder' pattern: when a target VRAM threshold is breached or an OutOfMemoryError is caught during execution, the manager must automatically attempt: 1. Evicting idle/cached models. 2. Converting/reloading weights to a lower precision (e.g., fp16 to quantized/CPU). 3. Falling back to a pure CPU-based DSP or lighter fallback path while returning a structured warning object detailing why the fallback occurred. Include mock tests simulating low-VRAM exceptions."
    }
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
            proc = subprocess.run(cmd, timeout=timeout, encoding="utf-8", errors="replace")
            return proc.returncode == 0, ""
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


# ----------------------------------------------------------------------------
# Model lifecycle
# ----------------------------------------------------------------------------

def download_model(lms_path, model):
    model_id = model["id"]
    ok_space, free_gb = check_disk_space(model["size_gb"])
    if not ok_space:
        msg = (f"Skipping download - only {free_gb:.1f} GB free, need ~"
               f"{model['size_gb'] + DEFAULT_MIN_FREE_GB} GB (model + safety margin)")
        log(f"[{model_id}] {msg}", "WARN")
        return model_id, False, msg

    log(f"[{model_id}] Downloading (~{model['size_gb']} GB estimated)...")
    ok, out = run_lms(lms_path, ["get", "-y", model_id], timeout=3 * 3600, stream_output=True)
    if ok:
        log(f"[{model_id}] Download complete.")
        return model_id, True, ""
    log(f"[{model_id}] Download failed: {out.strip()[-500:]}", "ERROR")
    return model_id, False, out.strip()[-500:]


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
    """Ask `lms ls --json` for the model's actual on-disk path rather than
    guessing it from the model_id string - the original approach assumed
    disk folder names always mirror the catalog id's casing, which isn't
    guaranteed by LM Studio."""
    ok, out = run_lms(lms_path, ["ls", "--json"])
    if not ok:
        return None
    try:
        entries = json.loads(out)
    except json.JSONDecodeError:
        return None
    for entry in entries:
        if entry.get("modelKey") == model_id or entry.get("indexedModelIdentifier") == model_id:
            return entry.get("path")
    return None


def delete_model_from_disk(lms_path, model_id):
    models_dir = Path.home() / ".lmstudio" / "models"
    rel_path = resolve_disk_path(lms_path, model_id)
    if rel_path:
        target = models_dir / rel_path
        # `path` can point at a single weight file (e.g. embeddings) or a
        # model directory - remove whichever it resolved to.
        target_dir = target if target.is_dir() else target.parent
    else:
        # Fallback: best-effort guess from the id, same as the original script.
        parts = model_id.split("/")
        target_dir = models_dir / parts[0] / parts[1] if len(parts) == 2 else models_dir / model_id

    if target_dir.exists():
        try:
            shutil.rmtree(target_dir)
            log(f"[{model_id}] Purged from disk: {target_dir}")
        except OSError as e:
            log(f"[{model_id}] Could not purge {target_dir}: {e}", "WARN")
    else:
        log(f"[{model_id}] Nothing found at {target_dir} to purge (may already be gone)", "WARN")


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
        completion_tokens = 0
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
                if delta and delta.content:
                    if first_token_time is None:
                        first_token_time = time.time()
                    full_response_parts.append(delta.content)
                    completion_tokens += 1

            end_time = time.time()
            ttft = (first_token_time - start_time) if first_token_time else 0
            generation_time = (end_time - first_token_time) if first_token_time else (end_time - start_time)
            tok_per_sec = (completion_tokens / generation_time) if generation_time > 0 else 0

            log(f"[{model_id}]     TTFT: {ttft:.3f}s | Speed: {tok_per_sec:.2f} tok/s | Tokens: {completion_tokens}")

            results.append({
                "test_name": test["name"],
                "model": model_id,
                "status": "ok",
                "ttft_seconds": round(ttft, 3),
                "tokens_per_second": round(tok_per_sec, 2),
                "total_tokens": completion_tokens,
                "full_code_output": "".join(full_response_parts),
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
    p.add_argument("--keep-on-disk", action="store_true", help="Don't delete models after benchmarking")
    p.add_argument("--skip-download", action="store_true", help="Assume models are already on disk")
    p.add_argument("--dry-run", action="store_true", help="Print the execution plan and exit")
    return p.parse_args()


def main():
    args = parse_args()

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

    try:
        for i in range(0, len(models), args.batch_size):
            batch = models[i:i + args.batch_size]
            ids = [m["id"] for m in batch]
            log(f"### BATCH {i // args.batch_size + 1} ({i + 1}-{i + len(batch)}/{len(models)}): {ids} ###")

            download_ok = {m["id"]: True for m in batch}
            if not args.skip_download:
                with ThreadPoolExecutor(max_workers=len(batch)) as executor:
                    for model_id, ok, err in executor.map(lambda m: download_model(lms_path, m), batch):
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
                    summary.append({
                        "model": model_id, "stage": "benchmark",
                        "status": "ok" if ok_count == len(results) else "partial",
                        "tests_ok": ok_count, "tests_total": len(results),
                        "avg_tokens_per_second": round(
                            sum(r["tokens_per_second"] for r in results if r["status"] == "ok") / ok_count, 2
                        ) if ok_count else 0,
                    })
                except Exception as e:
                    log(f"[{model_id}] Unexpected benchmark failure: {e}", "ERROR")
                    summary.append({"model": model_id, "stage": "benchmark", "status": "failed", "detail": str(e)})
                finally:
                    unload_all(lms_path)

            if not args.keep_on_disk:
                log(f"### PURGING BATCH FROM DISK: {ids} ###")
                for model in batch:
                    if download_ok.get(model["id"]):
                        delete_model_from_disk(lms_path, model["id"])
    except KeyboardInterrupt:
        log("Interrupted by user - cleaning up before exit...", "WARN")
        unload_all(lms_path)
    finally:
        run_lms(lms_path, ["server", "stop"], timeout=15)

    # ------------------------------------------------------------------
    # Final diagnostic report
    # ------------------------------------------------------------------
    with open("benchmark_summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    with open("benchmark_summary.csv", "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["model", "stage", "status", "tests_ok", "tests_total",
                                                "avg_tokens_per_second", "detail"])
        writer.writeheader()
        for row in summary:
            writer.writerow({k: row.get(k, "") for k in writer.fieldnames})

    print("\n" + "=" * 60)
    print(" PIPELINE COMPLETE - SUMMARY")
    print("=" * 60)
    ranked = sorted(
        (r for r in summary if r.get("stage") == "benchmark" and r.get("avg_tokens_per_second")),
        key=lambda r: r["avg_tokens_per_second"], reverse=True,
    )
    for r in ranked:
        print(f"  {r['model']:<38} {r['avg_tokens_per_second']:>8.2f} tok/s  "
              f"({r['tests_ok']}/{r['tests_total']} tests ok)")
    failed = [r for r in summary if r["status"] == "failed"]
    if failed:
        print(f"\n  {len(failed)} model(s) failed - see run_benchmarks.log and benchmark_summary.json:")
        for r in failed:
            print(f"    - {r['model']} (failed at {r['stage']})")
    print(f"\nFull report: benchmark_summary.json / benchmark_summary.csv")
    print(f"Log: {LOG_FILE}")


if __name__ == "__main__":
    main()
