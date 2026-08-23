using System.Diagnostics;

namespace TokenOptimizer.Sandbox;

public sealed record SetupStep(string Id, string Description, Func<CancellationToken, Task<bool>> Execute);

public sealed record PreflightResult(bool Ok, IReadOnlyList<string> Missing, IReadOnlyList<SetupStep> Steps);

public class PreflightGate
{
    private const string DockerDesktopExe = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
    private static readonly TimeSpan DockerStartTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ServerLifecycleManager _server;
    private readonly IProcessRunner _runner;

    public PreflightGate(ServerLifecycleManager server)
        : this(server, new ProcessRunner()) { }

    public PreflightGate(ServerLifecycleManager server, IProcessRunner runner)
    {
        _server = server;
        _runner = runner;
    }

    public async Task<PreflightResult> CheckAsync(CancellationToken ct = default)
    {
        var status = await _server.GetStatusAsync();
        if (status.DockerUp && status.ServerUp)
            return new PreflightResult(true, Array.Empty<string>(), Array.Empty<SetupStep>());

        var missing = new List<string>();
        var steps = new List<SetupStep>();

        if (!status.DockerUp)
        {
            missing.Add("docker");
            steps.Add(new SetupStep("wsl",
                "Enable WSL2 (Windows Subsystem for Linux) - required by Docker Desktop", EnsureWslAsync));
            steps.Add(new SetupStep("docker",
                "Install Docker Desktop via winget", InstallDockerAsync));
            steps.Add(new SetupStep("docker-start",
                "Start Docker Desktop and wait for the engine", StartDockerAsync));
        }
        if (!status.ServerUp)
        {
            missing.Add("opensandbox-server");
            steps.Add(new SetupStep("server",
                "Install and start the OpenSandbox server",
                async token => (await _server.EnsureRunningAsync(token)).ServerUp));
        }

        return new PreflightResult(false, missing, steps);
    }

    private async Task<bool> EnsureWslAsync(CancellationToken ct)
    {
        var status = await _runner.RunAsync("wsl", new[] { "--status" }, null, ct);
        if (status.ExitCode == 0) return true;
        var install = await _runner.RunAsync("wsl", new[] { "--install", "--no-distribution" }, null, ct);
        return install.ExitCode == 0;
    }

    private async Task<bool> InstallDockerAsync(CancellationToken ct)
    {
        var install = await _runner.RunAsync("winget",
            new[]
            {
                "install", "--id", "Docker.DockerDesktop", "-e",
                "--accept-source-agreements", "--accept-package-agreements",
            }, null, ct);
        return install.ExitCode == 0;
    }

    private async Task<bool> StartDockerAsync(CancellationToken ct)
    {
        if (!File.Exists(DockerDesktopExe)) return false;
        Process.Start(new ProcessStartInfo { FileName = DockerDesktopExe, UseShellExecute = true });

        for (var elapsed = TimeSpan.Zero; elapsed < DockerStartTimeout; elapsed += PollInterval)
        {
            var info = await _runner.RunAsync("docker", new[] { "info" }, null, ct);
            if (info.ExitCode == 0) return true;
            await Task.Delay(PollInterval, ct);
        }
        return false;
    }
}
