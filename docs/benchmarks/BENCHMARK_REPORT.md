# Benchmark Report

Generated from all `benchmark_<model>.json` files on disk. `capability_score` is an automated syntax+keyword heuristic - see each model's Quality Review for a human-verified assessment; the two can and do disagree (see `delivered`/miscalibration notes).

## Ranked Summary

| Rank | Model | Quality (human) | Avg Capability (auto) | Avg TTFT (s) | Avg Tok/s | Tests OK |
|---|---|---|---|---|---|---|
| 1 | qwen/qwen3.6-27b | 7.6 | 0.964 | 4.721 | 2.12 | 4/4 |
| 2 | qwen/qwen3-coder-30b | 6.25 | 1.0 | 3.441 | 7.463 | 4/4 |
| 3 | kwaipilot/kat-dev | 6.0 | 0.958 | 4.878 | 1.34 | 4/4 |
| 4 | ministral-3-14b-instruct-2512 | 5.75 | 0.964 | 3.559 | 5.02 | 4/4 |
| 5 | laguna-xs-2.1 | 5.5 | 1.0 | 3.635 | 7.157 | 4/4 |
| 6 | muse-glimmer-30b | 5.5 | 1.0 | 5.768 | 2.28 | 4/4 |
| 7 | openai/gpt-oss-20b | 5.25 | 1.0 | 3.438 | 13.193 | 4/4 |
| 8 | qwen/qwen2.5-coder-7b | 5.25 | 0.881 | 2.26 | 29.992 | 4/4 |
| 9 | qwen/qwen3.6-35b-a3b | 5.0 | 0.5 | 6.649 | 7.48 | 4/4 |
| 10 | seed-coder-8b-instruct | 5.0 | 1.0 | 2.767 | 16.48 | 4/4 |
| 11 | nvidia/nemotron-3-nano | 4.75 | 0.85 | 4.558 | 9.255 | 4/4 |
| 12 | devstral-small-2-24b-instruct-2512 | 4.25 | 0.964 | 3.649 | 2.493 | 4/4 |
| 13 | qwen/qwen3-vl-8b | 4.0 | 0.75 | 2.591 | 43.675 | 4/4 |
| 14 | google/gemma-4-26b-a4b | 3.75 | 0.5 | 3.907 | 7.617 | 4/4 |
| 15 | prism-ml/bonsai-27b | 3.75 | 1.0 | 4.644 | 14.77 | 4/4 |
| 16 | ministral-3-8b-instruct-2512 | 3.25 | 1.0 | 2.487 | 14.198 | 4/4 |
| 17 | granite-4.1-8b | 3.0 | 0.964 | 2.713 | 11.843 | 4/4 |
| 18 | zai-org/glm-4.6v-flash | 2.75 | 0.958 | 2.693 | 14.685 | 4/4 |
| 19 | google/gemma-4-12b | 2.5 | 0.714 | 4.083 | 6.32 | 4/4 |
| 20 | falcon-h1r-7b | 1.75 | 0.45 | 3.222 | 43.59 | 4/4 |
| 21 | nvidia/nemotron-3.5-lightning | 1.5 | 0.25 | 4.733 | 9.117 | 4/4 |
| 22 | deepseek/deepseek-r1-0528-qwen3-8b | 0.75 | 0.0 | 3.262 | 10.638 | 4/4 |

## Per-Model Detail

### qwen/qwen3.6-27b

**Quality score: 7.6/10**

Biggest turnaround of the run: 0/10 -> 7.6/10. Bigger context (32768) fixed the context-overflow errors that killed 3/4 tests in round 1; all 4 tests now reach clean final answers with real, mostly-correct code and genuine unit tests (topological sort, cache invalidation, downgrade ladder, cycle detection).

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.397 | 7656 | 2206 | 2.13 | 9862 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 4.833 | 5702 | 2278 | 2.19 | 7980 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.776 | 8667 | 2928 | 2.04 | 11595 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 4.877 | 6754 | 3016 | 2.12 | 9770 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.721s, reasoning_tokens=7194.75, completion_tokens=2607.0, tokens_per_second=2.12, total_tokens=9801.75, capability_score=0.964, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### qwen/qwen3-coder-30b

**Quality score: 6.25/10**

Retry succeeded after fixing the URL/load bug: consistently complete, syntactically valid, well-structured code on all 4 tests, but repeatedly simulates the hardest part of each spec (fake overlap-add, not-actually-Tauri, buggy edge rewiring, fake quantization) and tests print rather than assert.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.058 | 0 | 3017 | 8.04 | 3017 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.281 | 0 | 3173 | 7.57 | 3173 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.202 | 0 | 3227 | 7.46 | 3227 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.223 | 0 | 3713 | 6.78 | 3713 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.441s, reasoning_tokens=0.0, completion_tokens=3282.5, tokens_per_second=7.463, total_tokens=3282.5, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### kwaipilot/kat-dev

**Quality score: 6.0/10**

Re-run with bigger budget: DAG orchestrator test is genuinely strong (8/10, real unit tests incl. cycle detection); IPC and chunking tests solid but with fragility/missing-tests gaps; VRAM-fallback test has a fatal undefined-variable typo (NameError on import).

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.719 | 0 | 4272 | 1.2 | 4272 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 5.022 | 0 | 2626 | 1.43 | 2626 | 0.833 | True | 5/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.599 | 0 | 3072 | 1.38 | 3072 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 5.171 | 0 | 3525 | 1.35 | 3525 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.878s, reasoning_tokens=0.0, completion_tokens=3373.75, tokens_per_second=1.34, total_tokens=3373.75, capability_score=0.958, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### ministral-3-14b-instruct-2512

**Quality score: 5.75/10**

All 4 tests are plausible, well-organized designs but each has a real correctness bug (missing imports, unusable memmap approach on .npy files, broken Rust process-check logic); only the DAG test (7/10) has genuine automated unit tests.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 5.936 | 0 | 1504 | 5.38 | 1504 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.676 | 0 | 1547 | 5.48 | 1547 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.781 | 0 | 3180 | 4.8 | 3180 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.842 | 0 | 4139 | 4.42 | 4139 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.559s, reasoning_tokens=0.0, completion_tokens=2592.5, tokens_per_second=5.02, total_tokens=2592.5, capability_score=0.964, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### laguna-xs-2.1

**Quality score: 5.5/10**

Functional-looking code with real algorithmic ideas across all 4 tests, but consistently thin/fake test coverage (print-based demos, weak assertions) and at least one non-trivial bug per test (broken memmap shape, unimported Rust crate, misused torch.cuda.Event).

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 3.971 | 0 | 3940 | 6.67 | 3940 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.471 | 0 | 4095 | 7.38 | 4095 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.553 | 0 | 2815 | 8.11 | 2815 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.546 | 0 | 4359 | 6.47 | 4359 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.635s, reasoning_tokens=0.0, completion_tokens=3802.25, tokens_per_second=7.157, total_tokens=3802.25, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### muse-glimmer-30b

**Quality score: 5.5/10**

Automated score claimed a perfect 1.00 on all 4 tests but that's inaccurate - 2/4 tests have crash-level bugs invisible to syntax checks (numpy read-only-array write, a JS identifier with a literal space in it). The other 2 tests (DAG orchestrator, VRAM fallback) are genuinely strong with verified-correct logic.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 5.959 | 9761 | 1147 | 2.15 | 10908 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 5.624 | 1651 | 1191 | 2.31 | 2842 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 5.731 | 2282 | 1554 | 2.31 | 3836 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 5.76 | 1001 | 1690 | 2.35 | 2691 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=5.768s, reasoning_tokens=3673.75, completion_tokens=1395.5, tokens_per_second=2.28, total_tokens=5069.25, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### openai/gpt-oss-20b

**Quality score: 5.25/10**

[round-1 result, pending re-review] Full non-truncated code on all 4 tests; only the VRAM-fallback test (8/10) was fully correct and verified, others had test-breaking bugs.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.048 | 81 | 2515 | 13.26 | 2596 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.183 | 67 | 2554 | 13.12 | 2621 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.241 | 109 | 2619 | 13.43 | 2728 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.28 | 21 | 3057 | 12.96 | 3078 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.438s, reasoning_tokens=69.5, completion_tokens=2686.25, tokens_per_second=13.193, total_tokens=2755.75, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### qwen/qwen2.5-coder-7b

**Quality score: 5.25/10**

[round-1 result, pending re-review] Syntactically valid, on-topic code every time, but each answer has at least one functionally broken or unimplemented core mechanism.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 2.173 | 0 | 650 | 33.84 | 650 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.261 | 0 | 1000 | 33.8 | 1000 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.295 | 0 | 1058 | 28.43 | 1058 | 0.833 | True | 5/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.309 | 0 | 863 | 23.9 | 863 | 0.833 | True | 5/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.26s, reasoning_tokens=0.0, completion_tokens=892.75, tokens_per_second=29.992, total_tokens=892.75, capability_score=0.881, rubric=5.5/6.25, syntax_valid=4/4, delivered=4/4.

### qwen/qwen3.6-35b-a3b

**Quality score: 5.0/10**

New config (12K budget, disable_thinking attempted): 2/4 tests now produce valid working code (rubric 6-7/7), 1/4 still burns entire budget on reasoning (disable_thinking kwarg not consistently honored), 1/4 produced an answer but with invalid syntax.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 6.764 | 12000 | 0 | 7.1 | 12000 | 0.0 | False | 7/7 | False |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 5.06 | 5891 | 2372 | 7.86 | 8263 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 7.977 | 10103 | 1895 | 7.06 | 11998 | 0.0 | False | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 6.796 | 6149 | 3318 | 7.9 | 9467 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=6.649s, reasoning_tokens=8535.75, completion_tokens=1896.25, tokens_per_second=7.48, total_tokens=10432.0, capability_score=0.5, rubric=6.25/6.25, syntax_valid=2/4, delivered=3/4.

### seed-coder-8b-instruct

**Quality score: 5.0/10**

All 4 tests run without syntax errors but each has a functional or fidelity gap: incoherent chunking loop that likely never terminates, Tauri requirement substituted with plain Node.js, superficial caching, no real hardware introspection. Only the DAG test has genuine assert-based unit tests.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 2.74 | 0 | 1360 | 18.25 | 1360 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.766 | 0 | 1212 | 16.97 | 1212 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.781 | 0 | 1417 | 14.42 | 1417 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.782 | 0 | 1517 | 16.28 | 1517 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.767s, reasoning_tokens=0.0, completion_tokens=1376.5, tokens_per_second=16.48, total_tokens=1376.5, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### nvidia/nemotron-3-nano

**Quality score: 4.75/10**

Config improvement clearly helped 3/4 tests (short clean reasoning, real code with good architecture), but the chunking test still catastrophically loops in <think> to full budget exhaustion; every test's unit-test suite has at least one bug preventing it from actually running.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.504 | 10000 | 0 | 8.48 | 10000 | 0.4 | True | 7/7 | False |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 4.506 | 458 | 4415 | 11.17 | 4873 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.548 | 238 | 4243 | 8.96 | 4481 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 4.672 | 183 | 6654 | 8.41 | 6837 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.558s, reasoning_tokens=2719.75, completion_tokens=3828.0, tokens_per_second=9.255, total_tokens=6547.75, capability_score=0.85, rubric=6.25/6.25, syntax_valid=4/4, delivered=3/4.

### devstral-small-2-24b-instruct-2512

**Quality score: 4.25/10**

Below the bar expected of an agentic-coding-specialist model: plausible scaffolding across all 4 tests undermined by concrete logic bugs (caching that never actually skips recomputation, undefined variables, incoherent index math); tests too shallow or absent to catch them.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.492 | 0 | 2458 | 2.56 | 2458 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.313 | 0 | 2533 | 2.58 | 2533 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.412 | 0 | 3081 | 2.4 | 3081 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.381 | 0 | 2800 | 2.43 | 2800 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.649s, reasoning_tokens=0.0, completion_tokens=2718.0, tokens_per_second=2.493, total_tokens=2718.0, capability_score=0.964, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### qwen/qwen3-vl-8b

**Quality score: 4.0/10**

Bigger budget (6000 vs 4096) cut the repetition-loop failure from 2/4 tests to 1/4, but that 1 now loops to the full cap instead of stopping early; the 3 that complete still ship code with runtime-breaking bugs (TypeError from dataclass field order, undefined var, missing import).

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 2.314 | 0 | 6000 | 43.03 | 6000 | 0.0 | False | 5/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.713 | 0 | 4096 | 44.05 | 4096 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.627 | 0 | 5155 | 43.48 | 5155 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.708 | 0 | 3856 | 44.14 | 3856 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.591s, reasoning_tokens=0.0, completion_tokens=4776.75, tokens_per_second=43.675, total_tokens=4776.75, capability_score=0.75, rubric=5.75/6.25, syntax_valid=3/4, delivered=4/4.

### google/gemma-4-26b-a4b

**Quality score: 3.75/10**

[round-1 result, pending re-review] Architecturally sound designs undermined by fatal typos/copy-paste artifacts (undefined vars, mangled identifiers) that break execution.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.031 | 2567 | 2211 | 8.33 | 4778 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.453 | 662 | 2102 | 9.04 | 2764 | 0.0 | False | 5/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.221 | 1234 | 2640 | 8.16 | 3874 | 0.0 | False | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.925 | 865 | 2843 | 4.94 | 3708 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=3.907s, reasoning_tokens=1332.0, completion_tokens=2449.0, tokens_per_second=7.617, total_tokens=3781.0, capability_score=0.5, rubric=6.0/6.25, syntax_valid=2/4, delivered=4/4.

### prism-ml/bonsai-27b

**Quality score: 3.75/10**

USER-VERIFIED BY ACTUAL EXECUTION. Test 1: `from unittest import TestCase, assertRaises` is an import error (assertRaises isn't a module-level name); patched past that, np.save() silently appends .npy while raw mmap tries to open the un-suffixed path - FileNotFoundError, plus tearDown calls os.rmdir on a non-empty dir. Test 2: the STRONGEST single deliverable across both retried models - Python side genuinely imports and runs (real signal handlers, threading.Event-gated shutdown, real nvidia-smi VRAM detection, non-blocking hardware checks via run_in_executor); Node side has real bugs (process.isRunning() doesn't exist, res.body is never populated so JSON.parse throws). Test 3: add_edge validates against dicts that add_node never populates, so the first add_edge on any pair raises KeyError - all 4 tests fail. Test 4: partial credit, runs without crashing and 2/5 tests pass, but memory accounting after eviction is wrong and a warning field has the wrong type.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.397 | 9572 | 2154 | 15.76 | 11726 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 4.89 | 5872 | 1881 | 11.52 | 7753 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.525 | 6837 | 2980 | 15.64 | 9817 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 4.765 | 4428 | 3145 | 16.16 | 7573 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.644s, reasoning_tokens=6677.25, completion_tokens=2540.0, tokens_per_second=14.77, total_tokens=9217.25, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### ministral-3-8b-instruct-2512

**Quality score: 3.25/10**

Verbose, well-documented code across all 4 tests, but each has a concrete runtime-breaking bug (missing import, undefined reference, self-contradictory graph-rewiring logic) and tests are often incoherent relative to the class under test.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 2.515 | 0 | 2003 | 15.33 | 2003 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.407 | 0 | 1888 | 14.39 | 1888 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.47 | 0 | 2867 | 13.59 | 2867 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.557 | 0 | 3370 | 13.48 | 3370 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.487s, reasoning_tokens=0.0, completion_tokens=2532.0, tokens_per_second=14.198, total_tokens=2532.0, capability_score=1.0, rubric=6.25/6.25, syntax_valid=4/4, delivered=4/4.

### granite-4.1-8b

**Quality score: 3.0/10**

Short (~1000-1300 token) monolithic responses reflect under-delivery, not efficiency - every test contains fabricated APIs (mmap.MMAP_SHARED, torch.cuda.OOMException), indexing errors, or tests that assert on undefined/wrong objects. None would run successfully as-is.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 2.707 | 0 | 1144 | 6.19 | 1144 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.725 | 0 | 988 | 14.07 | 988 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.917 | 0 | 1315 | 10.33 | 1315 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.505 | 0 | 1306 | 16.78 | 1306 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.713s, reasoning_tokens=0.0, completion_tokens=1188.25, tokens_per_second=11.843, total_tokens=1188.25, capability_score=0.964, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### zai-org/glm-4.6v-flash

**Quality score: 2.75/10**

Retry succeeded (3rd download attempt): all 4 tests now reach a closed </think> with real final code, a genuine improvement over round-1's 1.0/10 all-truncated result. But every sample has at least one fatal runtime bug (invalid numpy slicing, wrong module import, self-referential/missing class methods) - none is deployable as-is.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 3.427 | 3344 | 1822 | 13.72 | 5166 | 1.0 | True | 7/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.481 | 1781 | 1535 | 15.72 | 3316 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.428 | 2605 | 1288 | 15.26 | 3893 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.438 | 3123 | 1676 | 14.04 | 4799 | 0.833 | True | 5/6 | True |

**Averages across 4/4 successful tests:** TTFT=2.693s, reasoning_tokens=2713.25, completion_tokens=1580.25, tokens_per_second=14.685, total_tokens=4293.5, capability_score=0.958, rubric=6.0/6.25, syntax_valid=4/4, delivered=4/4.

### google/gemma-4-12b

**Quality score: 2.5/10**

USER-VERIFIED BY ACTUAL EXECUTION (not just reading): every test crashes on import, crashes on run, or fails its own assertions. Test 1: hand-calculated overlap-add test value is simply wrong (asserts 11.0, actual result is 12.0). Test 2: uses builtin any() instead of typing.Any in a Pydantic response annotation - FastAPI fails to even load the module. Test 3: all 3 unit tests fail (TypeError from mismatched lambda kwargs, RuntimeError from mutating a dict while iterating it, and cache invalidation that doesn't invalidate); final sort() also silently discards the DFS topological order. Test 4: script crashes outright on an uncaught OutOfMemoryError, and the tests never actually exercise the real fallback-ladder logic at all, only a mock runner directly.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.843 | 1911 | 1917 | 6.12 | 3828 | 0.857 | True | 6/7 | True |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.521 | 695 | 1734 | 6.65 | 2429 | 0.0 | False | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.803 | 1095 | 2142 | 6.26 | 3237 | 1.0 | True | 6/6 | True |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 4.165 | 555 | 2610 | 6.25 | 3165 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.083s, reasoning_tokens=1064.0, completion_tokens=2100.75, tokens_per_second=6.32, total_tokens=3164.75, capability_score=0.714, rubric=6.0/6.25, syntax_valid=3/4, delivered=4/4.

### falcon-h1r-7b

**Quality score: 1.75/10**

Only 1/4 tests reached a clean final answer (and that one has a hard JS syntax error, no tests); the other 3 burn the entire 12K budget on unclosed <think> reasoning with no promoted code. The file's own capability_score is badly miscalibrated here since the scorer falls back to grading incomplete reasoning text.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.114 | 11990 | 0 | 43.58 | 11990 | 0.4 | True | 6/7 | False |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 2.749 | 6568 | 4624 | 43.59 | 11192 | 1.0 | True | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 3.198 | 11997 | 0 | 43.62 | 11997 | 0.0 | False | 6/6 | False |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 2.827 | 11996 | 0 | 43.57 | 11996 | 0.4 | True | 6/6 | False |

**Averages across 4/4 successful tests:** TTFT=3.222s, reasoning_tokens=10637.75, completion_tokens=1156.0, tokens_per_second=43.59, total_tokens=11793.75, capability_score=0.45, rubric=6.0/6.25, syntax_valid=3/4, delivered=1/4.

### nvidia/nemotron-3.5-lightning

**Quality score: 1.5/10**

Re-run with new config: 2 of 4 tests still stuck fully in unclosed <think> reasoning, 1 produced only a stub fragment, only 1 (VRAM fallback) delivered a real, plausible design - but no automated tests anywhere. disable_thinking did not reliably suppress reasoning for this model.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.494 | 10000 | 0 | 9.06 | 10000 | 0.0 | False | 6/7 | False |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 4.759 | 9132 | 602 | 7.62 | 9734 | 0.0 | False | 6/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 4.616 | 10000 | 0 | 8.99 | 10000 | 0.0 | False | 6/6 | False |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 5.062 | 1347 | 3239 | 10.8 | 4586 | 1.0 | True | 6/6 | True |

**Averages across 4/4 successful tests:** TTFT=4.733s, reasoning_tokens=7619.75, completion_tokens=960.25, tokens_per_second=9.117, total_tokens=8580.0, capability_score=0.25, rubric=6.0/6.25, syntax_valid=1/4, delivered=2/4.

### deepseek/deepseek-r1-0528-qwen3-8b

**Quality score: 0.75/10**

disable_thinking did NOT suppress reasoning: 3 of 4 tests burned the full 12K budget entirely in <think> with 0 completion tokens, never delivering any code. The one test that escaped (Tauri/IPC) produced broken code (Rust typo, .await on non-async, a nonexistent GPUtil API, missing uvicorn import) with zero tests.

**Scoring matrix (per test):**

| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Stream & Memory-Mapped Chunking | ok | 4.11 | 11998 | 0 | 8.69 | 11998 | 0.0 | False | 4/7 | False |
| 2. Tauri-Python Subprocess & IPC Lifecycle | ok | 3.182 | 2194 | 2165 | 16.81 | 4359 | 0.0 | False | 5/6 | True |
| 3. Dynamic Node Graph & Pipeline Orchestration | ok | 2.656 | 11998 | 0 | 8.59 | 11998 | 0.0 | False | 5/6 | False |
| 4. Hardware Graceful Degradation & VRAM Fallback | ok | 3.101 | 11998 | 0 | 8.46 | 11998 | 0.0 | False | 5/6 | False |

**Averages across 4/4 successful tests:** TTFT=3.262s, reasoning_tokens=9547.0, completion_tokens=541.25, tokens_per_second=10.638, total_tokens=10088.25, capability_score=0.0, rubric=4.75/6.25, syntax_valid=0/4, delivered=1/4.
