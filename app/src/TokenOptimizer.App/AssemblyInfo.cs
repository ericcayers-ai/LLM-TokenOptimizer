using System.Runtime.Versioning;

// The fallback-chain adapters (Antigravity/Codex/Cursor) and DPAPI credential
// store this app wires up are Windows-only today - same platform scope as
// the PowerShell launcher this replaces.
[assembly: SupportedOSPlatform("windows")]
