using System.IO.Compression;

namespace TokenOptimizer.Core.Benchmarking;

/// <summary>
/// Packages every benchmark_&lt;model&gt;.json (plus benchmark_summary.json if
/// present) into a single zip, alongside a ready-to-paste prompt instructing
/// an AI reviewer how to quality-score the results. Exists because the
/// automated capability_score in those files is a syntax+keyword heuristic,
/// not a real correctness check - this session's own manual review process
/// (read each model's full_code_output, judge it directly, watch for the
/// scorer falling back to grading raw &lt;think&gt; text when a model never
/// delivered a final answer) repeatedly found scores the automated pass
/// missed or got wrong. This turns that same process into a one-click export
/// instead of something only reproducible by hand.
/// </summary>
public static class BenchmarkExporter
{
    public const string PromptFileName = "AI_QUALITY_REVIEW_PROMPT.txt";

    /// <summary>
    /// Finds every benchmark_*.json in repoRoot (excluding the summary file,
    /// which is included separately) and zips them plus the review prompt
    /// into outputZipPath. Returns the count of per-model result files
    /// included, or 0 if none were found (no zip is written in that case).
    /// </summary>
    public static int Export(string repoRoot, string outputZipPath)
    {
        var resultFiles = Directory.EnumerateFiles(repoRoot, "benchmark_*.json")
            .Where(f => !Path.GetFileName(f).Equals("benchmark_summary.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (resultFiles.Count == 0) return 0;

        if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

        using (var zip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create))
        {
            foreach (var file in resultFiles)
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }

            var summaryPath = Path.Combine(repoRoot, "benchmark_summary.json");
            if (File.Exists(summaryPath))
            {
                zip.CreateEntryFromFile(summaryPath, "benchmark_summary.json");
            }

            var promptEntry = zip.CreateEntry(PromptFileName);
            using var writer = new StreamWriter(promptEntry.Open());
            writer.Write(BuildPrompt(resultFiles.Count));
        }

        return resultFiles.Count;
    }

    /// <summary>The same prompt text written into the zip, exposed separately so the UI can copy it to the clipboard without re-opening the archive.</summary>
    public static string BuildPrompt(int modelCount) => $"""
        You are reviewing local-LLM coding-benchmark results. This zip contains {modelCount} benchmark_<model>.json file(s), each an array of 4 test results for one model, plus benchmark_summary.json with the automated metrics.

        IMPORTANT - the automated `capability_score` field in each result is NOT reliable. It is a syntax-parses-plus-keyword-regex heuristic, not a real correctness check. It has repeatedly been found to:
        - Score a model 1.00 for code that parses but crashes immediately at runtime (undefined variables, wrong imports, self-referential classes, calling methods that don't exist).
        - Score a model highly on a response that never reached a real final answer - when a model's `<think>` reasoning trace runs out of budget without closing, the scorer falls back to grading the raw incomplete reasoning text, which can look superficially complete. Check `reasoning_tokens` vs `completion_tokens`/`answer` in each result: if a test shows high reasoning_tokens and 0 (or very low) completion tokens, treat the automated score as unreliable and re-read the actual `full_code_output` yourself.

        For each model, for each of its 4 tests:
        1. Read `full_code_output` in full (it may contain a `<think>...</think>` block followed by, or instead of, a final answer).
        2. Judge the code yourself: does it actually work? Is it complete (not a stub/truncated mid-function)? Does it follow reasonable practices? Does it address the specific requirements in the test's prompt (visible in the corresponding TEST_PROMPTS entry in run_benchmarks.py, or inferable from the test_name)? Are there genuine automated unit tests (real assertions), or just print statements / no tests at all?
        3. Score 0-10: 10 = production-quality, correct, complete, tested. 0 = empty, broken, or irrelevant. If the response never reached a final answer (all reasoning, no delivered code), that's a low score regardless of how plausible the reasoning sounds.
        4. Write one sentence justifying the score, citing the SPECIFIC bug or strength you found (not "looks good" or "has some issues" - name the actual defect, e.g. "undefined `time` import causes NameError" or "cache logic never actually skips recomputation").

        Then for each model: report the 4 test scores, a one-line justification each, and an overall average (0-10).

        Finally: rank all models in this zip by their average quality score, highest first, and name the single best-performing model.

        Keep the whole review honest and specific - don't round up marginal code to "pretty good," and don't let verbose, confident-sounding responses substitute for actually checking whether the code runs.
        """;
}
