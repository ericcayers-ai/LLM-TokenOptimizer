using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Security;

/// <summary>
/// Local secret storage for the Antigravity/Codex/Cursor fallback chain,
/// mirroring the PowerShell launcher's DPAPI-based Set/Get/Test/Remove-
/// ProxyCredential family: encrypted with CryptProtectData (readable only by
/// this Windows account on this machine), never logged, never transmitted.
/// What "credential" means differs per provider - Codex stores the real
/// OPENAI_API_KEY (its documented auth mechanism); Antigravity/Cursor store
/// an opt-in marker since both authenticate via interactive OAuth inside
/// their own app - a stored value here means "include this provider in the
/// fallback chain," not a literal key that gets injected.
/// Windows-only (DPAPI) - the fallback-chain providers this guards
/// (Antigravity/Codex/Cursor desktop installs) are Windows-only today too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxyCredentialStore
{
    private readonly string _credentialDir;

    public ProxyCredentialStore(string? credentialDirectory = null)
    {
        _credentialDir = credentialDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tokenoptimizer", "proxy-credentials");
        Directory.CreateDirectory(_credentialDir);
    }

    public bool HasCredential(FallbackProvider provider) => File.Exists(GetPath(provider));

    public void SetCredential(FallbackProvider provider, string plainTextValue)
    {
        var bytes = Encoding.UTF8.GetBytes(plainTextValue);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllText(GetPath(provider), Convert.ToBase64String(protectedBytes));
    }

    public string? GetCredentialPlainText(FallbackProvider provider)
    {
        var path = GetPath(provider);
        if (!File.Exists(path)) return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(path));
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void RemoveCredential(FallbackProvider provider)
    {
        var path = GetPath(provider);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(FallbackProvider provider) =>
        Path.Combine(_credentialDir, $"{provider.ToString().ToLowerInvariant()}.cred");
}
