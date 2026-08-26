using TokenOptimizer.Providers.FreeToken;

namespace TokenOptimizer.Providers.Tests.FreeToken;

/// <summary>
/// Live filesystem probes - no faked path abstraction. Whether FreeToken is
/// actually installed is real, ambient, per-machine state (this dev box has
/// it; a clean CI box won't), so the "not found" case is checked as an
/// internal-consistency property rather than assumed. The positive case
/// plants a real exe at the real %LOCALAPPDATA%\FreeToken Desktop\ path
/// FreeTokenLocator looks in (skipping creation/cleanup if a real install is
/// already sitting there), to prove discovery works the way it would for an
/// actual FreeToken install.
/// </summary>
[Collection("FreeToken")]
public sealed class FreeTokenLocatorTests
{
    [Fact]
    public void FindDesktopApp_ReturnsNullOrARealExistingFile()
    {
        // Whatever FindDesktopApp() reports, it must never point at a file
        // that doesn't actually exist - that's the one invariant that holds
        // whether or not FreeToken happens to be installed here.
        var found = FreeTokenLocator.FindDesktopApp();
        if (found is not null) Assert.True(File.Exists(found));
    }

    [Fact]
    public void DefaultBaseUrl_MatchesFreeTokensDocumentedPort()
    {
        Assert.Equal("http://127.0.0.1:1919", FreeTokenLocator.DefaultBaseUrl);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void FindDesktopApp_ExeAtDocumentedLocalAppDataPath_IsFound()
    {
        if (!OperatingSystem.IsWindows()) return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "FreeToken Desktop");
        var exePath = Path.Combine(dir, "freetoken-desktop.exe");
        var createdDir = !Directory.Exists(dir);
        var createdFile = false;

        try
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(exePath))
            {
                File.WriteAllBytes(exePath, []);
                createdFile = true;
            }

            var found = FreeTokenLocator.FindDesktopApp();

            Assert.Equal(exePath, found);
        }
        finally
        {
            if (createdFile) File.Delete(exePath);
            if (createdDir) Directory.Delete(dir, recursive: true);
        }
    }
}
