using System.Runtime.Versioning;
using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Providers.Tests.Fallback;

[SupportedOSPlatform("windows")]
public sealed class JcodeHarnessAdapterTests
{
    [Fact]
    public void BuildArguments_WithModel_IncludesModelFlag()
    {
        var result = JcodeHarnessAdapter.BuildArguments("openai", "gpt-5.6-terra", SessionResumeMode.New);
        Assert.Equal("--provider openai --model gpt-5.6-terra", result);
    }

    [Fact]
    public void BuildArguments_WithNullOrEmptyModel_OmitsModelFlag()
    {
        Assert.Equal("--provider openai", JcodeHarnessAdapter.BuildArguments("openai", null, SessionResumeMode.New));
        Assert.Equal("--provider openai", JcodeHarnessAdapter.BuildArguments("openai", "", SessionResumeMode.New));
        Assert.Equal("--provider openai", JcodeHarnessAdapter.BuildArguments("openai", "   ", SessionResumeMode.New));
    }

    [Fact]
    public void BuildArguments_NewSession_OmitsResumeFlag()
    {
        var result = JcodeHarnessAdapter.BuildArguments("antigravity", null, SessionResumeMode.New);
        Assert.Equal("--provider antigravity", result);
    }

    [Fact]
    public void BuildArguments_ContinueOrPickSession_DegradesToNewWithLog()
    {
        // Continue and Pick both degrade to New (no --resume flag added)
        var @continue = JcodeHarnessAdapter.BuildArguments("openai", "gpt-5.6-terra", SessionResumeMode.Continue);
        var pick = JcodeHarnessAdapter.BuildArguments("openai", "gpt-5.6-terra", SessionResumeMode.Pick);
        Assert.Equal("--provider openai --model gpt-5.6-terra", @continue);
        Assert.Equal("--provider openai --model gpt-5.6-terra", pick);
    }
}
