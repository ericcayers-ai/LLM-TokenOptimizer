using System.Net;
using System.Text;
using TokenOptimizer.Providers.FreeToken;

namespace TokenOptimizer.Providers.Tests.FreeToken;

/// <summary>
/// Live tests for FreeTokenAdapter - no mocked HTTP, a real loopback listener
/// stands in for the FreeToken desktop server so ProbeServerAsync/
/// ListServedModelsAsync exercise a real request/response round trip.
///
/// IsAvailableAsync/LaunchSessionAsync branch on whether the FreeToken
/// desktop app is actually installed on the machine running the tests -
/// that's real, ambient, per-machine state (this dev box has it installed;
/// a clean CI box won't). Both tests below read FreeTokenLocator.FindDesktopApp()
/// themselves first and adapt, rather than assuming either state, and the
/// "installed" branch pins a fake server to FreeToken's own documented port
/// (1919) specifically so ProbeServerAsync answers before LaunchSessionAsync
/// would otherwise shell out and pop the real desktop app's GUI window.
/// </summary>
[Collection("FreeToken")]
public sealed class FreeTokenAdapterTests
{
    [Fact]
    public void Name_IsHumanReadableProviderLabel()
    {
        Assert.Equal("FreeToken (local MoE)", new FreeTokenAdapter().Name);
    }

    [Fact]
    public async Task ProbeServerAsync_NothingListening_ReturnsFalse()
    {
        var deadUrl = $"http://127.0.0.1:{GetFreePort()}";
        Assert.False(await FreeTokenAdapter.ProbeServerAsync(deadUrl));
    }

    [Fact]
    public async Task ProbeServerAsync_RealServerRespondingOk_ReturnsTrue()
    {
        using var server = new FakeFreeTokenServer("{\"data\":[]}");
        Assert.True(await FreeTokenAdapter.ProbeServerAsync(server.BaseUrl));
    }

    [Fact]
    public async Task ListServedModelsAsync_ParsesRealResponseBody()
    {
        using var server = new FakeFreeTokenServer(
            "{\"data\":[{\"id\":\"Qwen3.6-35B-A3B\"},{\"id\":\"GLM-5.2\"}]}");

        var models = await FreeTokenAdapter.ListServedModelsAsync(server.BaseUrl);

        Assert.Equal(["Qwen3.6-35B-A3B", "GLM-5.2"], models);
    }

    [Fact]
    public async Task ListServedModelsAsync_ServerUnreachable_ReturnsEmptyNotThrow()
    {
        var deadUrl = $"http://127.0.0.1:{GetFreePort()}";
        Assert.Empty(await FreeTokenAdapter.ListServedModelsAsync(deadUrl));
    }

    [Fact]
    public async Task ListServedModelsAsync_MalformedBody_ReturnsEmptyNotThrow()
    {
        using var server = new FakeFreeTokenServer("not json");
        Assert.Empty(await FreeTokenAdapter.ListServedModelsAsync(server.BaseUrl));
    }

    [Fact]
    public async Task IsAvailableAsync_NotInstalled_ReturnsFalse()
    {
        if (FreeTokenLocator.FindDesktopApp() is not null) return; // covered by the _Installed_ test below on this machine

        Assert.False(await new FreeTokenAdapter().IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_InstalledAndServerServing_ReturnsTrue()
    {
        if (FreeTokenLocator.FindDesktopApp() is null) return; // covered by the _NotInstalled_ test above on a clean box

        using var server = new FakeFreeTokenServer("{\"data\":[{\"id\":\"m\"}]}", port: FreeTokenDefaultPort);
        Assert.True(await new FreeTokenAdapter().IsAvailableAsync());
    }

    [Fact]
    public async Task LaunchSessionAsync_DesktopAppNotInstalled_ThrowsWithInstallLink()
    {
        if (FreeTokenLocator.FindDesktopApp() is not null) return; // covered by the _NoModelsLoaded_ test below on this machine

        var adapter = new FreeTokenAdapter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchSessionAsync(new SessionLaunchOptions(Path.GetTempPath(), null)));

        Assert.Contains("flashml.ai", ex.Message);
    }

    [Fact]
    public async Task LaunchSessionAsync_ServerUpWithNoModelsLoaded_ThrowsWithoutLaunchingRealDesktopApp()
    {
        if (FreeTokenLocator.FindDesktopApp() is null) return; // covered by the _NotInstalled_ test above on a clean box

        // Binding the fake server on FreeToken's real default port makes
        // ProbeServerAsync succeed immediately, so LaunchSessionAsync never
        // falls into LaunchDesktopApp() - this must never spawn the real,
        // actually-installed FreeToken.exe as a side effect of a test run.
        using var server = new FakeFreeTokenServer("{\"data\":[]}", port: FreeTokenDefaultPort);
        var adapter = new FreeTokenAdapter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchSessionAsync(new SessionLaunchOptions(Path.GetTempPath(), null)));

        Assert.Contains("no loaded models", ex.Message);
    }

    [Fact]
    public async Task InstallSkillAsync_ReturnsFail_PointsAtCodingAgentAdapter()
    {
        var result = await new FreeTokenAdapter().InstallSkillAsync(
            new TokenOptimizer.Providers.Manifests.SkillManifest("x", "x", "x", "x", "x", []));

        Assert.False(result.Success);
        Assert.Contains("model backend", result.Message);
    }

    /// <summary>Port FreeTokenLocator.DefaultBaseUrl points at - the one port LaunchSessionAsync/IsAvailableAsync can't be redirected away from.</summary>
    private const int FreeTokenDefaultPort = 1919;

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Minimal real HTTP server standing in for FreeToken's GET /v1/models.</summary>
    private sealed class FakeFreeTokenServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly string _modelsBody;
        public string BaseUrl { get; }

        public FakeFreeTokenServer(string modelsBody, int? port = null)
        {
            _modelsBody = modelsBody;
            BaseUrl = $"http://127.0.0.1:{port ?? GetFreePort()}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(_modelsBody);
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch { /* test infra only */ }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
