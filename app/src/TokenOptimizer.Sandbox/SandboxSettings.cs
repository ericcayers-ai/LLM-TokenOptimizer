namespace TokenOptimizer.Sandbox;

/// <summary>
/// Connection and image defaults for the OpenSandbox substrate. Persisted as the
/// "Sandbox" section of config.json via ConfigStore. The API key is never stored
/// here - ApiKeySecretRef points at a ProxyCredentialStore entry instead.
/// </summary>
public sealed class SandboxSettings
{
    public string Domain { get; set; } = "localhost:8080";
    public string Protocol { get; set; } = "http";
    public string? ApiKeySecretRef { get; set; }
    public string AgentImage { get; set; } = "tokenoptimizer/agent-companion:latest";
    public int IdleTimeoutMinutes { get; set; } = 60;
}
