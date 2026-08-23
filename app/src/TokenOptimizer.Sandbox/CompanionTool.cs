namespace TokenOptimizer.Sandbox;

/// <summary>
/// One companion tool's full identity: how it is installed on the Windows
/// host today, how it would be baked into a Linux sandbox image, and what
/// it places into a Claude config dir to wire itself up.
///
/// Lives in TokenOptimizer.Sandbox (the bottom-most project) so that both
/// TokenOptimizer.Providers (ToolCatalog, host wiring) and ImageCatalog
/// (image baking) can name the type without a reference cycle.
/// </summary>
/// <param name="Id">Stable catalog id, e.g. "rtk" or "claude-mem".</param>
/// <param name="HostInstallCommand">
/// The essential command sequence CompanionToolingInstaller runs on the
/// Windows host today. Claude-CLI invocations are written claude-relative
/// (the installer resolves the executable and passes these as arguments);
/// multi-step installs are joined with &amp;&amp;.
/// </param>
/// <param name="ImageInstallFragment">
/// Linux/Dockerfile shell lines installing the same tool inside an image,
/// authored from the host flow and each tool's documented Linux path.
/// </param>
/// <param name="ClaudeWiringFragment">
/// What ends up in a .claude config dir to wire this tool (hooks,
/// statusline, MCP registration, plugin registration).
/// </param>
public sealed record CompanionTool(
    string Id,
    string HostInstallCommand,
    string ImageInstallFragment,
    string ClaudeWiringFragment);
