using TokenOptimizer.Providers.Hermes;

namespace TokenOptimizer.Providers.Tests.Hermes;

/// <summary>
/// Hermes adapter tests run entirely on injected locators/probes - no test
/// reaches for a real hermes install or spawns real processes. Ambient-state
/// behavior (a real machine with/without Hermes) is covered by the locator
/// tests below reading actual disk state and adapting, mirroring the
/// FreeTokenAdapterTests convention.
/// </summary>
public sealed class HermesAgentAdapterTests
{
    private static readonly string TempDir = Path.GetTempPath();

    [Fact]
    public void Name_IsHumanReadableProviderLabel()
    {
        Assert.Equal("Hermes Agent", new HermesAgentAdapter().Name);
    }

    [Fact]
    public async Task IsAvailableAsync_NoExecutable_ReturnsFalse()
    {
        var adapter = new HermesAgentAdapter(findExecutable: () => null, probeHome: () => true);
        Assert.False(await adapter.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_ExecutableWithoutHome_ReturnsFalse()
    {
        // An on-PATH hermes with no ~/.hermes has no config/credentials -
        // report unavailable rather than half-working (invariant 5).
        var adapter = new HermesAgentAdapter(findExecutable: () => "C:\\fake\\hermes.exe", probeHome: () => false);
        Assert.False(await adapter.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_ExecutableWithHome_ReturnsTrue()
    {
        var adapter = new HermesAgentAdapter(findExecutable: () => "C:\\fake\\hermes.exe", probeHome: () => true);
        Assert.True(await adapter.IsAvailableAsync());
    }

    [Fact]
    public async Task LaunchSessionAsync_NoExecutableFound_ThrowsWithInstallLink()
    {
        var adapter = new HermesAgentAdapter(findExecutable: () => null, probeHome: () => true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchSessionAsync(new SessionLaunchOptions(TempDir, null)));

        Assert.Contains("hermes-agent.nousresearch.com", ex.Message);
    }

    [Theory]
    [InlineData("New", @"chat --in ""C:\proj""", false)]
    [InlineData("Continue", @"chat --in ""C:\proj"" -c", false)]
    [InlineData("Continue", @"chat --in ""C:\proj"" --model glm-5.2 -c", true)]
    public void BuildArguments_MapsProjectModelAndResumeMode(string resumeModeRaw, string expected, bool withModel)
    {
        var resumeMode = Enum.Parse<SessionResumeMode>(resumeModeRaw);

        var actual = HermesAgentAdapter.BuildArguments(
            @"C:\proj",
            withModel ? "glm-5.2" : null,
            resumeMode);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildArguments_PickMode_FailsFast_NoPickerExistsInHermes()
    {
        // Verified live: `hermes chat --resume` errors with "expected one
        // argument". A silent fallback would launch the WRONG session state.
        Assert.Throws<NotSupportedException>(() =>
            HermesAgentAdapter.BuildArguments(@"C:\proj", null, SessionResumeMode.Pick));
    }

    [Fact]
    public void BuildArguments_BlankModel_OmitsFlag()
    {
        var args = HermesAgentAdapter.BuildArguments(@"C:\proj", "   ", SessionResumeMode.New);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public async Task InstallSkillAsync_Fails_PointsAtHermesOwnTooling()
    {
        var result = await new HermesAgentAdapter().InstallSkillAsync(
            new TokenOptimizer.Providers.Manifests.SkillManifest("x", "x", "x", "x", "x", []));

        Assert.False(result.Success);
        Assert.Contains("hermes skills", result.Message);
    }
}

/// <summary>
/// Locator tests read real ambient disk state: on this repo's dev box Hermes is
/// installed at %LOCALAPPDATA%\hermes\..., on a clean CI box it isn't. Each
/// test checks which world it's in first and asserts the matching branch.
/// </summary>
public sealed class HermesLocatorTests
{
    [Fact]
    public void Find_InstalledMachine_ReturnsExistingExecutablePath()
    {
        var path = HermesLocator.Find();
        if (path is null)
        {
            // Clean machine: nothing to assert beyond not throwing.
            return;
        }
        Assert.True(File.Exists(path), $"Locator returned '{path}' which does not exist.");
    }

    [Fact]
    public void ProbeDefaultHome_MatchesRealHermesHomeResolution()
    {
        // Independently re-derive what Hermes considers its home on this
        // machine ($HERMES_HOME override, else ~/.hermes) and require the
        // probe to agree - catches either branch drifting out of order.
        var envHome = Environment.GetEnvironmentVariable("HERMES_HOME");
        var expected = !string.IsNullOrWhiteSpace(envHome)
            ? Directory.Exists(envHome)
            : Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"));
        Assert.Equal(expected, HermesAgentAdapter.ProbeDefaultHome());
    }
}
