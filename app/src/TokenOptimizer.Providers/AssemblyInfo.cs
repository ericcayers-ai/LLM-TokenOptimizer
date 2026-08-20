using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

[assembly: InternalsVisibleTo("TokenOptimizer.Providers.Tests")]
[assembly: InternalsVisibleTo("TokenOptimizer.Core.Tests")]

// Every adapter here targets a Windows-only install surface (Claude Code's
// Windows bin dirs, Antigravity/Codex/Cursor Windows paths, DPAPI-backed
// credentials, console-buffer rate-limit watching) - same platform scope as
// the PowerShell launcher this replaces.
[assembly: SupportedOSPlatform("windows")]
