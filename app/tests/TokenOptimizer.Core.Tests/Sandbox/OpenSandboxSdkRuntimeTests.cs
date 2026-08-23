using OpenSandbox.Config;
using OpenSandbox.Models;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class OpenSandboxSdkRuntimeTests
{
    [Fact]
    public void BuildConnectionConfig_MapsDomainAndHttpProtocol()
    {
        var runtime = new OpenSandboxSdkRuntime(new SandboxSettings { Domain = "localhost:8080", Protocol = "http" });

        var config = runtime.BuildConnectionConfig();

        Assert.Equal("localhost:8080", config.Domain);
        Assert.Equal(ConnectionProtocol.Http, config.Protocol);
    }

    [Fact]
    public void BuildConnectionConfig_MapsHttpsProtocolCaseInsensitive()
    {
        var runtime = new OpenSandboxSdkRuntime(new SandboxSettings { Protocol = "HTTPS" });

        Assert.Equal(ConnectionProtocol.Https, runtime.BuildConnectionConfig().Protocol);
    }

    [Fact]
    public void BuildConnectionConfig_UnknownProtocolFallsBackToHttp()
    {
        var runtime = new OpenSandboxSdkRuntime(new SandboxSettings { Protocol = "gopher" });

        Assert.Equal(ConnectionProtocol.Http, runtime.BuildConnectionConfig().Protocol);
    }

    [Fact]
    public void BuildCreateOptions_MapsImageEnvAndMountsToVolumes()
    {
        var spec = new SandboxSpec(
            Image: "opensandbox/aio:latest",
            Mounts: new Dictionary<string, string> { ["/workspace"] = @"C:\proj" },
            Env: new Dictionary<string, string> { ["FOO"] = "bar" });

        var options = OpenSandboxSdkRuntime.BuildCreateOptions(spec, config: null);

        Assert.Equal("opensandbox/aio:latest", options.Image);
        Assert.Equal("bar", options.Env!["FOO"]);
        var volume = Assert.Single(options.Volumes!);
        Assert.Equal("/workspace", volume.MountPath);
        Assert.Equal(@"C:\proj", volume.Host!.Path);
        Assert.False(volume.ReadOnly);
        Assert.True(options.ManualCleanup);
        Assert.Null(options.TimeoutSeconds);
    }

    [Fact]
    public void BuildCreateOptions_TimeoutMapsToTtlSeconds()
    {
        var spec = new SandboxSpec(
            Image: "opensandbox/aio:latest",
            Mounts: new Dictionary<string, string>(),
            Timeout: TimeSpan.FromMinutes(5));

        var options = OpenSandboxSdkRuntime.BuildCreateOptions(spec, config: null);

        Assert.False(options.ManualCleanup);
        Assert.Equal(300, options.TimeoutSeconds);
    }

    [Fact]
    public void BuildCommand_ShellQuotesEachArgument()
    {
        var command = OpenSandboxSdkRuntime.BuildCommand(new[] { "echo", "hello world", "it's", "" });

        Assert.Equal("'echo' 'hello world' 'it'\\''s' ''", command);
    }

    [Fact]
    public void BuildCommand_EmptyArgvThrows()
    {
        Assert.Throws<ArgumentException>(() => OpenSandboxSdkRuntime.BuildCommand(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(ServerStreamEventTypes.Stdout, "out text", "stdout")]
    [InlineData(ServerStreamEventTypes.Stderr, "err text", "stderr")]
    public void MapStreamEvent_ConvertsOutputEvents(string type, string text, string expectedStream)
    {
        var ev = new ServerStreamEvent { Type = type, Text = text };

        var mapped = OpenSandboxSdkRuntime.MapStreamEvent(ev);

        var output = Assert.IsType<ExecOutput>(mapped);
        Assert.Equal(expectedStream, output.Stream);
        Assert.Equal(text, output.Text);
    }

    [Theory]
    [InlineData(ServerStreamEventTypes.Init)]
    [InlineData(ServerStreamEventTypes.Result)]
    [InlineData(ServerStreamEventTypes.ExecutionComplete)]
    public void MapStreamEvent_IgnoresNonOutputEvents(string type)
    {
        var ev = new ServerStreamEvent { Type = type };

        Assert.Null(OpenSandboxSdkRuntime.MapStreamEvent(ev));
    }

    [Fact]
    public void ResolveExitCode_ErrorValueWinsOverComplete()
    {
        Assert.Equal(42, OpenSandboxSdkRuntime.ResolveExitCode(sawComplete: true, errorCode: 42));
    }

    [Fact]
    public void ResolveExitCode_CompleteWithoutErrorMeansZero()
    {
        Assert.Equal(0, OpenSandboxSdkRuntime.ResolveExitCode(sawComplete: true, errorCode: null));
    }

    [Fact]
    public void ResolveExitCode_NoCompleteNoErrorDefaultsToOne()
    {
        Assert.Equal(1, OpenSandboxSdkRuntime.ResolveExitCode(sawComplete: false, errorCode: null));
    }

    [Fact]
    public void TryParseErrorExitCode_ReadsEvalueKey()
    {
        var error = new Dictionary<string, object> { ["evalue"] = "7" };

        Assert.True(OpenSandboxSdkRuntime.TryParseErrorExitCode(error, out var code));
        Assert.Equal(7, code);
    }

    [Fact]
    public async Task OpsOnUnknownId_ThrowInvalidOperationException_BeforeAnyNetworkCall()
    {
        var runtime = new OpenSandboxSdkRuntime(LocalSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ReadFileAsync("sbx-nope", "/tmp/x"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.WriteFileAsync("sbx-nope", "/tmp/x", "y"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runtime.ExecAsync("sbx-nope", new[] { "true" })) { }
        });
    }

    [Fact]
    public async Task KillUnknownId_IsAcceptedLikeFake_AndLaterOpsStayUnknown()
    {
        var runtime = new OpenSandboxSdkRuntime(LocalSettings());

        await runtime.KillAsync("sbx-never-created");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ReadFileAsync("sbx-never-created", "/tmp/x"));
    }

    private static SandboxSettings LocalSettings() =>
        new() { Domain = "localhost:8080", Protocol = "http" };
}
