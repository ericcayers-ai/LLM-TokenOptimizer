# Archived Legacy Benchmarks

This directory contains archived legacy benchmark artifacts from earlier
live-model testing passes. The files were originally generated in the repo
root by `run_benchmarks.py` and related scripts.

They are preserved here for historical reference only. Results are tied to a
specific point in time, model catalog state, and local hardware configuration,
so they should not be treated as canonical or reproducible without re-running
the benchmark pipeline.

Contents:

- `benchmark_*.json` — per-model quality and token-usage results
- `benchmark_summary.json` / `benchmark_summary.csv` — aggregated summaries
- `BENCHMARK_REPORT.md` — human-readable report derived from the summaries
- `generate_report.py` / `merge_quality.py` — reporting helper scripts used
  during that pass
- `run_benchmarks*.log` — execution logs from the runs
