using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers;

/// <summary>Continue = --continue (most recent conversation in this folder), Pick = --resume (Claude's own session picker), New = no flag.</summary>
public enum SessionResumeMode
{
    Continue,
    Pick,
    New,
}

public sealed record SessionLaunchOptions(
    string ProjectPath,
    string? Model = null,
    bool IsolateConfig = false,
    SessionResumeMode ResumeMode = SessionResumeMode.Continue,
    LmStudioContextPreset? ContextPreset = null);

/// <summary>
/// The translation-layer contract: every coding-agent provider (Claude Code,
/// LM Studio-local today; Antigravity/Codex/Cursor as their own future
/// adapters) implements this the same way, so skills/plugins/tools installed
/// once against the neutral manifest types above can be synced out to
/// whichever providers are actually present on the machine.
/// </summary>
public interface IProviderAdapter
{
    string Name { get; }

    Task<bool> IsAvailableAsync();

    Task<IReadOnlyList<string>> ListInstalledSkillsAsync();

    Task<IReadOnlyList<string>> ListInstalledPluginsAsync();

    Task<ProviderResult> InstallSkillAsync(SkillManifest skill);

    Task<ProviderResult> InstallPluginAsync(PluginManifest plugin);

    Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool);

    Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options);
}
