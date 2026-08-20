namespace TokenOptimizer.Providers.Diagnostics;

/// <summary>
/// Single source of truth for the live model selftest matrix. Each entry is
/// (provider display name, model id). Kept separate from the probing engine
/// so the CLI, tests, and docs can all reference the same list.
/// </summary>
public static class SelftestMatrix
{
    public static readonly IReadOnlyList<(string Provider, string Model)> Entries = new (string, string)[]
    {
        ("Claude Code", "claude-sonnet-5"),
        ("Groq", "openai/gpt-oss-120b"),
        ("Groq", "openai/gpt-oss-20b"),
        ("Groq", "qwen/qwen3.6-27b"),
        ("Groq", "groq/compound"),
        ("Groq", "groq/compound-mini"),
        ("OpenCode", "opencode-go"),
        ("Unsloth (local model)", "unsloth/Qwen3.8-27B-GGUF:UD-IQ4_XS"),
        ("Unsloth (local model)", "mudler/KAT-Coder-V2.5-Dev-APEX-GGUF:I-QUALITY"),
        ("Antigravity", "gemini-3-pro"),
        ("Antigravity", "gemini-3-pro-high"),
    };
}
