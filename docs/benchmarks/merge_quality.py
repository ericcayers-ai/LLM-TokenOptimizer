"""Reusable merge script: recomputes benchmark_summary.json/.csv from all
benchmark_<model>.json files on disk, folding in manually-judged quality
scores. Re-run this each time new/updated benchmark_<model>.json files land
from the ongoing run - QUALITY_SCORES is the source of truth for review
results and should be updated (via Edit) as each model gets quality-reviewed.
"""
import json, csv
from pathlib import Path

# model_id -> {"score": 0-10, "note": "..."} - updated as quality reviews land.
QUALITY_SCORES = {
    "qwen/qwen3.6-35b-a3b": {"score": 5.0, "note": "New config (12K budget, disable_thinking attempted): 2/4 tests now produce valid working code (rubric 6-7/7), 1/4 still burns entire budget on reasoning (disable_thinking kwarg not consistently honored), 1/4 produced an answer but with invalid syntax."},
    "kwaipilot/kat-dev": {"score": 6.0, "note": "Re-run with bigger budget: DAG orchestrator test is genuinely strong (8/10, real unit tests incl. cycle detection); IPC and chunking tests solid but with fragility/missing-tests gaps; VRAM-fallback test has a fatal undefined-variable typo (NameError on import)."},
    "nvidia/nemotron-3-nano": {"score": 4.75, "note": "Config improvement clearly helped 3/4 tests (short clean reasoning, real code with good architecture), but the chunking test still catastrophically loops in <think> to full budget exhaustion; every test's unit-test suite has at least one bug preventing it from actually running."},
    # --- carried over from round 1 (not yet re-reviewed under new config this run) ---
    "google/gemma-4-26b-a4b": {"score": 3.75, "note": "[round-1 result, pending re-review] Architecturally sound designs undermined by fatal typos/copy-paste artifacts (undefined vars, mangled identifiers) that break execution."},
    "nvidia/nemotron-3.5-lightning": {"score": 1.5, "note": "Re-run with new config: 2 of 4 tests still stuck fully in unclosed <think> reasoning, 1 produced only a stub fragment, only 1 (VRAM fallback) delivered a real, plausible design - but no automated tests anywhere. disable_thinking did not reliably suppress reasoning for this model."},
    "openai/gpt-oss-20b": {"score": 5.25, "note": "[round-1 result, pending re-review] Full non-truncated code on all 4 tests; only the VRAM-fallback test (8/10) was fully correct and verified, others had test-breaking bugs."},
    "qwen/qwen2.5-coder-7b": {"score": 5.25, "note": "[round-1 result, pending re-review] Syntactically valid, on-topic code every time, but each answer has at least one functionally broken or unimplemented core mechanism."},
    "qwen/qwen3-coder-30b": {"score": 6.25, "note": "Retry succeeded after fixing the URL/load bug: consistently complete, syntactically valid, well-structured code on all 4 tests, but repeatedly simulates the hardest part of each spec (fake overlap-add, not-actually-Tauri, buggy edge rewiring, fake quantization) and tests print rather than assert."},
    "qwen/qwen3-vl-8b": {"score": 4.0, "note": "Bigger budget (6000 vs 4096) cut the repetition-loop failure from 2/4 tests to 1/4, but that 1 now loops to the full cap instead of stopping early; the 3 that complete still ship code with runtime-breaking bugs (TypeError from dataclass field order, undefined var, missing import)."},
    "qwen/qwen3.6-27b": {"score": 7.6, "note": "Biggest turnaround of the run: 0/10 -> 7.6/10. Bigger context (32768) fixed the context-overflow errors that killed 3/4 tests in round 1; all 4 tests now reach clean final answers with real, mostly-correct code and genuine unit tests (topological sort, cache invalidation, downgrade ladder, cycle detection)."},
    "zai-org/glm-4.6v-flash": {"score": 2.75, "note": "Retry succeeded (3rd download attempt): all 4 tests now reach a closed </think> with real final code, a genuine improvement over round-1's 1.0/10 all-truncated result. But every sample has at least one fatal runtime bug (invalid numpy slicing, wrong module import, self-referential/missing class methods) - none is deployable as-is."},
    "falcon-h1r-7b": {"score": 1.75, "note": "Only 1/4 tests reached a clean final answer (and that one has a hard JS syntax error, no tests); the other 3 burn the entire 12K budget on unclosed <think> reasoning with no promoted code. The file's own capability_score is badly miscalibrated here since the scorer falls back to grading incomplete reasoning text."},
    "google/gemma-4-12b": {"score": 2.5, "note": "USER-VERIFIED BY ACTUAL EXECUTION (not just reading): every test crashes on import, crashes on run, or fails its own assertions. Test 1: hand-calculated overlap-add test value is simply wrong (asserts 11.0, actual result is 12.0). Test 2: uses builtin any() instead of typing.Any in a Pydantic response annotation - FastAPI fails to even load the module. Test 3: all 3 unit tests fail (TypeError from mismatched lambda kwargs, RuntimeError from mutating a dict while iterating it, and cache invalidation that doesn't invalidate); final sort() also silently discards the DFS topological order. Test 4: script crashes outright on an uncaught OutOfMemoryError, and the tests never actually exercise the real fallback-ladder logic at all, only a mock runner directly."},
    "deepseek/deepseek-r1-0528-qwen3-8b": {"score": 0.75, "note": "disable_thinking did NOT suppress reasoning: 3 of 4 tests burned the full 12K budget entirely in <think> with 0 completion tokens, never delivering any code. The one test that escaped (Tauri/IPC) produced broken code (Rust typo, .await on non-async, a nonexistent GPUtil API, missing uvicorn import) with zero tests."},
    "prism-ml/bonsai-27b": {"score": 3.75, "note": "USER-VERIFIED BY ACTUAL EXECUTION. Test 1: `from unittest import TestCase, assertRaises` is an import error (assertRaises isn't a module-level name); patched past that, np.save() silently appends .npy while raw mmap tries to open the un-suffixed path - FileNotFoundError, plus tearDown calls os.rmdir on a non-empty dir. Test 2: the STRONGEST single deliverable across both retried models - Python side genuinely imports and runs (real signal handlers, threading.Event-gated shutdown, real nvidia-smi VRAM detection, non-blocking hardware checks via run_in_executor); Node side has real bugs (process.isRunning() doesn't exist, res.body is never populated so JSON.parse throws). Test 3: add_edge validates against dicts that add_node never populates, so the first add_edge on any pair raises KeyError - all 4 tests fail. Test 4: partial credit, runs without crashing and 2/5 tests pass, but memory accounting after eviction is wrong and a warning field has the wrong type."},
    "muse-glimmer-30b": {"score": 5.5, "note": "Automated score claimed a perfect 1.00 on all 4 tests but that's inaccurate - 2/4 tests have crash-level bugs invisible to syntax checks (numpy read-only-array write, a JS identifier with a literal space in it). The other 2 tests (DAG orchestrator, VRAM fallback) are genuinely strong with verified-correct logic."},
    "laguna-xs-2.1": {"score": 5.5, "note": "Functional-looking code with real algorithmic ideas across all 4 tests, but consistently thin/fake test coverage (print-based demos, weak assertions) and at least one non-trivial bug per test (broken memmap shape, unimported Rust crate, misused torch.cuda.Event)."},
    "devstral-small-2-24b-instruct-2512": {"score": 4.25, "note": "Below the bar expected of an agentic-coding-specialist model: plausible scaffolding across all 4 tests undermined by concrete logic bugs (caching that never actually skips recomputation, undefined variables, incoherent index math); tests too shallow or absent to catch them."},
    "ministral-3-14b-instruct-2512": {"score": 5.75, "note": "All 4 tests are plausible, well-organized designs but each has a real correctness bug (missing imports, unusable memmap approach on .npy files, broken Rust process-check logic); only the DAG test (7/10) has genuine automated unit tests."},
    "ministral-3-8b-instruct-2512": {"score": 3.25, "note": "Verbose, well-documented code across all 4 tests, but each has a concrete runtime-breaking bug (missing import, undefined reference, self-contradictory graph-rewiring logic) and tests are often incoherent relative to the class under test."},
    "seed-coder-8b-instruct": {"score": 5.0, "note": "All 4 tests run without syntax errors but each has a functional or fidelity gap: incoherent chunking loop that likely never terminates, Tauri requirement substituted with plain Node.js, superficial caching, no real hardware introspection. Only the DAG test has genuine assert-based unit tests."},
    "granite-4.1-8b": {"score": 3.0, "note": "Short (~1000-1300 token) monolithic responses reflect under-delivery, not efficiency - every test contains fabricated APIs (mmap.MMAP_SHARED, torch.cuda.OOMException), indexing errors, or tests that assert on undefined/wrong objects. None would run successfully as-is."},
}

def compute_composite(resolve_rate, tps, max_tps):
    normalized_speed = (tps / max_tps) if max_tps > 0 else 0.0
    return round(0.7 * resolve_rate + 0.3 * normalized_speed, 4)

def main():
    result_files = sorted(Path(".").glob("benchmark_*.json"))
    result_files = [f for f in result_files if f.name != "benchmark_summary.json"]

    summary = []
    for f in result_files:
        results = json.loads(f.read_text(encoding="utf-8"))
        if not results:
            continue
        model_id = results[0].get("model", f.stem.replace("benchmark_", "").replace("_", "/", 1))
        ok_count = sum(1 for r in results if r["status"] == "ok")
        resolve_rate = round(sum(r.get("capability_score", 0) for r in results if r["status"] == "ok") / ok_count, 3) if ok_count else 0
        avg_tps = round(sum(r["tokens_per_second"] for r in results if r["status"] == "ok") / ok_count, 2) if ok_count else 0
        q = QUALITY_SCORES.get(model_id, {"score": None, "note": "not assessed"})
        summary.append({
            "model": model_id, "stage": "benchmark",
            "status": "ok" if ok_count == len(results) else "partial",
            "tests_ok": ok_count, "tests_total": len(results),
            "avg_tokens_per_second": avg_tps,
            "resolve_rate": resolve_rate,
            "quality_score": q["score"],
            "quality_note": q["note"],
        })

    max_tps = max((r["avg_tokens_per_second"] for r in summary if r["avg_tokens_per_second"]), default=0)
    for r in summary:
        r["composite_score"] = compute_composite(r.get("resolve_rate", 0), r["avg_tokens_per_second"], max_tps)

    with open("benchmark_summary.json", "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=2, ensure_ascii=False)

    fieldnames = ["model", "stage", "status", "tests_ok", "tests_total", "avg_tokens_per_second",
                  "resolve_rate", "composite_score", "quality_score", "quality_note", "detail"]
    with open("benchmark_summary.csv", "w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=fieldnames)
        writer.writeheader()
        for row in summary:
            writer.writerow({k: row.get(k, "") for k in fieldnames})

    ranked = sorted(summary, key=lambda r: (r["quality_score"] if r["quality_score"] is not None else -1), reverse=True)
    print("Ranked by human-judged output quality:")
    for r in ranked:
        q = f"{r['quality_score']:.2f}" if r["quality_score"] is not None else "PENDING"
        print(f"  {r['model']:<32} quality={q:<8} resolve_rate={r['resolve_rate']:.2f}  composite={r['composite_score']:.3f}")


if __name__ == "__main__":
    main()
