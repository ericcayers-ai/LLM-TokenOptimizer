namespace TokenOptimizer.App.Tests;

/// <summary>
/// CliHost.RunAsync always constructs a real ConfigStore/ProxyCredentialStore
/// unless TOKENOPTIMIZER_CONFIG_DIR/TOKENOPTIMIZER_CREDENTIAL_DIR are set - without
/// this fixture, every test in the "CliHost" collection (reset-config,
/// set-credential, add-project, opt-in, ...) reads and writes the real developer
/// machine's %APPDATA%\TokenOptimizer and ~/.tokenoptimizer directories.
/// </summary>
public sealed class CliHostTestFixture : IDisposable
{
    private readonly string _configDir;
    private readonly string _credentialDir;
    private readonly string? _previousConfigDir;
    private readonly string? _previousCredentialDir;

    public CliHostTestFixture()
    {
        _previousConfigDir = Environment.GetEnvironmentVariable("TOKENOPTIMIZER_CONFIG_DIR");
        _previousCredentialDir = Environment.GetEnvironmentVariable("TOKENOPTIMIZER_CREDENTIAL_DIR");

        _configDir = Path.Combine(Path.GetTempPath(), "tokenoptimizer-test-config-" + Guid.NewGuid().ToString("N"));
        _credentialDir = Path.Combine(Path.GetTempPath(), "tokenoptimizer-test-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_credentialDir);

        Environment.SetEnvironmentVariable("TOKENOPTIMIZER_CONFIG_DIR", _configDir);
        Environment.SetEnvironmentVariable("TOKENOPTIMIZER_CREDENTIAL_DIR", _credentialDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TOKENOPTIMIZER_CONFIG_DIR", _previousConfigDir);
        Environment.SetEnvironmentVariable("TOKENOPTIMIZER_CREDENTIAL_DIR", _previousCredentialDir);
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_credentialDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
