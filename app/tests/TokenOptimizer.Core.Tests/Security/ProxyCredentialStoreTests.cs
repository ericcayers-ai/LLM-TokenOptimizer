using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;

namespace TokenOptimizer.Core.Tests.Security;

public class ProxyCredentialStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProxyCredentialStore _store;

    public ProxyCredentialStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-cred-" + Guid.NewGuid().ToString("N"));
        _store = new ProxyCredentialStore(_tempDir);
    }

    [Fact]
    public void HasCredential_ReturnsFalse_WhenNeverSet()
    {
        Assert.False(_store.HasCredential(FallbackProvider.Codex));
    }

    [Fact]
    public void SetThenGetCredential_RoundTripsPlainTextValue()
    {
        _store.SetCredential(FallbackProvider.Codex, "sk-test-12345");

        Assert.True(_store.HasCredential(FallbackProvider.Codex));
        Assert.Equal("sk-test-12345", _store.GetCredentialPlainText(FallbackProvider.Codex));
    }

    [Fact]
    public void SetCredential_NeverWritesPlainTextToDisk()
    {
        _store.SetCredential(FallbackProvider.Codex, "sk-super-secret-value");

        var rawFileContent = File.ReadAllText(Path.Combine(_tempDir, "codex.cred"));
        Assert.DoesNotContain("sk-super-secret-value", rawFileContent);
    }

    [Fact]
    public void RemoveCredential_ClearsStoredValue()
    {
        _store.SetCredential(FallbackProvider.Cursor, "opted-in");
        _store.RemoveCredential(FallbackProvider.Cursor);

        Assert.False(_store.HasCredential(FallbackProvider.Cursor));
    }

    [Fact]
    public void DifferentProviders_AreStoredIndependently()
    {
        _store.SetCredential(FallbackProvider.Codex, "codex-key");
        _store.SetCredential(FallbackProvider.Antigravity, "opted-in");

        Assert.Equal("codex-key", _store.GetCredentialPlainText(FallbackProvider.Codex));
        Assert.Equal("opted-in", _store.GetCredentialPlainText(FallbackProvider.Antigravity));
        Assert.False(_store.HasCredential(FallbackProvider.Cursor));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
