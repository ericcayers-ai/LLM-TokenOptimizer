using System.Net.Http;

namespace TokenOptimizer.Sandbox;

public sealed record ServerStatus(bool DockerUp, bool ServerUp, Uri? Domain, string? Error);

public class ServerLifecycleManager
{
    private readonly IProcessRunner _runner;
    private readonly SandboxSettings _settings;
    private readonly HttpClient _http;

    public ServerLifecycleManager(IProcessRunner runner, SandboxSettings settings)
        : this(runner, settings, new HttpClient()) { }

    internal ServerLifecycleManager(IProcessRunner runner, SandboxSettings settings, HttpClient http)
    {
        _runner = runner;
        _settings = settings;
        _http = http;
    }

    public Uri HealthUri => new UriBuilder(_settings.Protocol, _settings.Domain.Split(':')[0],
        _settings.Domain.Contains(':') ? int.Parse(_settings.Domain.Split(':')[1]) : 80,
        "/health").Uri;

    protected virtual int PollAttempts => 30;
    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    public async Task<ServerStatus> GetStatusAsync()
    {
        var docker = await ProbeDockerAsync();
        if (docker is not null)
            return docker;

        var probe = await ProbeHealthAsync(HealthUri, CancellationToken.None);
        return probe
            ? new ServerStatus(true, true, HealthUri, null)
            : new ServerStatus(true, false, HealthUri, $"OpenSandbox server is not responding at {HealthUri}");
    }

    public async Task<ServerStatus> EnsureRunningAsync(CancellationToken ct)
    {
        var docker = await ProbeDockerAsync();
        if (docker is not null)
            return docker;

        if (await ProbeHealthAsync(HealthUri, ct))
            return new ServerStatus(true, true, HealthUri, null);

        var configPath = ResolveConfigPath();
        if (!File.Exists(configPath))
        {
            ProcResult init;
            try
            {
                init = await _runner.RunAsync("uvx",
                    new[] { "opensandbox-server", "init-config", configPath, "--example", "docker" });
            }
            catch (Exception ex)
            {
                // A missing uvx (Win32Exception/FileNotFound from Process.Start) must
                // degrade to a status the preflight gate can act on, not crash.
                return new ServerStatus(true, false, HealthUri, "uvx CLI not found: " + ex.Message);
            }

            if (init.ExitCode != 0)
                return new ServerStatus(true, false, HealthUri, "init-config failed: " + FirstLine(init.StdErr));
        }

        StartServer(configPath);

        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeHealthAsync(HealthUri, ct))
                return new ServerStatus(true, true, HealthUri, null);
            await Task.Delay(PollInterval, ct);
        }

        return new ServerStatus(true, false, HealthUri,
            $"OpenSandbox server did not become healthy within {PollAttempts * PollInterval.TotalSeconds:0}s");
    }

    protected virtual string ResolveConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sandbox.toml");

    protected virtual void StartServer(string configPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "uvx",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.ArgumentList.Add("opensandbox-server");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        System.Diagnostics.Process.Start(psi);
    }

    protected virtual async Task<bool> ProbeHealthAsync(Uri healthUri, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(healthUri, timeoutCts.Token);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the `docker info` gate probe. Returns a failed ServerStatus when
    /// docker is down OR absent from PATH (Process.Start then throws
    /// Win32Exception/FileNotFound) - never lets that exception escape, so
    /// PreflightGate can list "docker" as missing and route to the wizard.
    /// Null means docker answered.
    /// </summary>
    private async Task<ServerStatus?> ProbeDockerAsync()
    {
        ProcResult docker;
        try
        {
            docker = await _runner.RunAsync("docker", new[] { "info" });
        }
        catch (Exception ex)
        {
            return new ServerStatus(false, false, null, "docker CLI not found: " + ex.Message);
        }

        return docker.ExitCode != 0
            ? new ServerStatus(false, false, null, "Docker is not running: " + FirstLine(docker.StdErr))
            : null;
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        return null;
    }
}
