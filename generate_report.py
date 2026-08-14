"""Generates BENCHMARK_REPORT.md: full quality reviews, per-test scoring
matrix, and per-model averages across all 4 test prompts. Reads the same
benchmark_<model>.json files and merge_quality.py's QUALITY_SCORES dict that
merge_quality.py itself uses - run that first (or this reads whatever's
currently on disk in benchmark_summary.json/QUALITY_SCORES).

Kept separate from merge_quality.py's QUALITY_SCORES data structure (imported,
not duplicated) so there's one source of truth for human-judged scores.
"""
import json
from pathlib import Path
from merge_quality import QUALITY_SCORES

FIELDS = ["ttft_seconds", "reasoning_tokens", "completion_tokens",
          "tokens_per_second", "total_tokens", "capability_score"]


def load_results():
    """Returns {model_id: [test_result, ...]} for every benchmark_<model>.json on disk."""
    out = {}
    for f in sorted(Path(".").glob("benchmark_*.json")):
        if f.name == "benchmark_summary.json":
            continue
        try:
            results = json.loads(f.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        if not results:
            continue
        model_id = results[0].get("model", f.stem.replace("benchmark_", "").replace("_", "/", 1))
        out[model_id] = results
    return out


def averages(results):
    """Averages FIELDS plus capability_detail sub-fields across ok tests."""
    ok = [r for r in results if r.get("status") == "ok"]
    if not ok:
        return None
    avg = {f: round(sum(r.get(f, 0) or 0 for r in ok) / len(ok), 3) for f in FIELDS}
    avg["tests_ok"] = len(ok)
    avg["tests_total"] = len(results)
    avg["rubric_hits"] = round(sum(r.get("capability_detail", {}).get("rubric_hits", 0) for r in ok) / len(ok), 2)
    avg["rubric_total"] = round(sum(r.get("capability_detail", {}).get("rubric_total", 0) for r in ok) / len(ok), 2)
    avg["syntax_valid_count"] = sum(1 for r in ok if r.get("capability_detail", {}).get("syntax_valid"))
    avg["delivered_count"] = sum(1 for r in ok if r.get("capability_detail", {}).get("delivered", True))
    return avg


def main():
    all_results = load_results()
    if not all_results:
        print("No benchmark_<model>.json files found - nothing to report.")
        return

    lines = ["# Benchmark Report", ""]
    lines.append("Generated from all `benchmark_<model>.json` files on disk. "
                  "`capability_score` is an automated syntax+keyword heuristic - "
                  "see each model's Quality Review for a human-verified assessment; "
                  "the two can and do disagree (see `delivered`/miscalibration notes).")
    lines.append("")

    # ---- Ranked summary table ----
    lines.append("## Ranked Summary")
    lines.append("")
    lines.append("| Rank | Model | Quality (human) | Avg Capability (auto) | Avg TTFT (s) | Avg Tok/s | Tests OK |")
    lines.append("|---|---|---|---|---|---|---|")
    ranked = []
    for model_id, results in all_results.items():
        avg = averages(results)
        q = QUALITY_SCORES.get(model_id, {}).get("score")
        ranked.append((model_id, q, avg))
    ranked.sort(key=lambda x: (x[1] if x[1] is not None else -1), reverse=True)
    for i, (model_id, q, avg) in enumerate(ranked, 1):
        if avg is None:
            lines.append(f"| {i} | {model_id} | {q if q is not None else '-'} | - | - | - | 0/0 |")
            continue
        lines.append(f"| {i} | {model_id} | {q if q is not None else 'PENDING'} | "
                      f"{avg['capability_score']} | {avg['ttft_seconds']} | {avg['tokens_per_second']} | "
                      f"{avg['tests_ok']}/{avg['tests_total']} |")
    lines.append("")

    # ---- Per-model detail: quality review, scoring matrix, averages ----
    lines.append("## Per-Model Detail")
    lines.append("")
    for model_id, q, avg in ranked:
        results = all_results[model_id]
        lines.append(f"### {model_id}")
        lines.append("")
        q_entry = QUALITY_SCORES.get(model_id)
        if q_entry:
            lines.append(f"**Quality score: {q_entry['score'] if q_entry['score'] is not None else 'PENDING'}/10**")
            lines.append("")
            lines.append(q_entry["note"])
        else:
            lines.append("**Quality score: not yet reviewed**")
        lines.append("")

        lines.append("**Scoring matrix (per test):**")
        lines.append("")
        lines.append("| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |")
        lines.append("|---|---|---|---|---|---|---|---|---|---|---|")
        for r in results:
            if r.get("status") != "ok":
                lines.append(f"| {r.get('test_name', '?')} | {r.get('status')} | - | - | - | - | - | - | - | - | - |")
                continue
            d = r.get("capability_detail", {})
            lines.append(
                f"| {r.get('test_name', '?')} | ok | {r.get('ttft_seconds')} | {r.get('reasoning_tokens')} | "
                f"{r.get('completion_tokens')} | {r.get('tokens_per_second')} | {r.get('total_tokens')} | "
                f"{r.get('capability_score')} | {d.get('syntax_valid')} | {d.get('rubric_hits')}/{d.get('rubric_total')} | "
                f"{d.get('delivered', True)} |"
            )
        lines.append("")

        if avg:
            lines.append(f"**Averages across {avg['tests_ok']}/{avg['tests_total']} successful tests:** "
                          f"TTFT={avg['ttft_seconds']}s, reasoning_tokens={avg['reasoning_tokens']}, "
                          f"completion_tokens={avg['completion_tokens']}, tokens_per_second={avg['tokens_per_second']}, "
                          f"total_tokens={avg['total_tokens']}, capability_score={avg['capability_score']}, "
                          f"rubric={avg['rubric_hits']}/{avg['rubric_total']}, "
                          f"syntax_valid={avg['syntax_valid_count']}/{avg['tests_ok']}, "
                          f"delivered={avg['delivered_count']}/{avg['tests_ok']}.")
        lines.append("")

    Path("BENCHMARK_REPORT.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote BENCHMARK_REPORT.md ({len(all_results)} models).")


if __name__ == "__main__":
    main()
