namespace TokenOptimizer.Providers.Manifests;

/// <summary>
/// Provider-neutral description of a plugin/marketplace package - the
/// source it comes from (a marketplace id, a git url, a local path) plus
/// the identifier the adapter installs.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string DisplayName,
    PluginSource Source,
    string SourceLocator,
    string Scope = "user");

public enum PluginSource
{
    Marketplace,
    GitRepository,
    LocalPath,
}
