using TokenOptimizer.Providers;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

/// <summary>
/// Gated docker build smoke for the generated image definitions. Skipped
/// unless TOKENOPTIMIZER_DOCKER_TESTS=1 AND a working docker daemon is
/// present, so `dotnet test` stays green on machines without Docker.
///
/// Each run materializes both kinds (Dockerfile + entrypoint.sh) into a
/// fresh %TEMP%\opencode\imgcat-build-&lt;guid&gt;\ directory, exactly as a
/// consumer of ImageCatalog would, and asserts `docker build` exits 0.
/// </summary>
public class ImageCatalogDockerBuildTests
{
    [Fact]
    public async Task DockerBuild_GeneratedDockerfilesBuild_WhenDockerPresent()
    {
        if (!IsEnabled) return;

        var catalog = new ImageCatalog(ToolCatalog.Tools);
        var dir = Path.Combine(Path.GetTempPath(), "opencode", $"imgcat-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (kind, tag) in new[]
                     {
                         (AgentImageKind.AgentBase, "tokenoptimizer/agent-base:golden-smoke"),
                         (AgentImageKind.AgentCompanion, "tokenoptimizer/agent-companion:golden-smoke"),
                     })
            {
                var kindDir = Path.Combine(dir, kind.ToString());
                Directory.CreateDirectory(kindDir);
                File.WriteAllText(Path.Combine(kindDir, "Dockerfile"), catalog.GenerateDockerfile(kind));
                File.WriteAllText(Path.Combine(kindDir, "entrypoint.sh"), catalog.GenerateEntrypointScript());

                var (exitCode, output) = await RunDockerBuildAsync(
                    kindDir, $"build -t {tag} .", $"docker build ({kind})");
                Assert.True(exitCode == 0, $"docker build failed:{Environment.NewLine}{output}");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup; unique guid keeps leftovers harmless */ }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDockerBuildAsync(
        string workingDirectory, string arguments, string label)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["DOCKER_BUILDKIT"] = "1";

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        if (!proc.WaitForExit(1_200_000))
        {
            proc.Kill(true);
            Assert.Fail($"{label} timed out.{Environment.NewLine}{await stderrTask}");
        }

        return (proc.ExitCode, $"{await stdoutTask}{Environment.NewLine}{await stderrTask}");
    }

    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("TOKENOPTIMIZER_DOCKER_TESTS") == "1"
        && DockerAvailable();

    private static bool DockerAvailable()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            proc.WaitForExit(15000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
