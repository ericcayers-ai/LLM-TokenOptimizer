using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Single construction point for the lazily-built default sandbox launcher
/// (real OpenSandbox runtime + default settings) that every adapter falls
/// back to when none was injected - previously duplicated per adapter.
/// </summary>
internal static class SandboxLauncherFactory
{
    public static SandboxSessionLauncher CreateDefault() => Create(new SandboxSettings());

    public static SandboxSessionLauncher Create(SandboxSettings settings) =>
        new(new OpenSandboxSdkRuntime(settings), settings);
}
