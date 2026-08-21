using TokenOptimizer.Providers.LlamaCpp;

namespace TokenOptimizer.Providers.Tests.LlamaCpp;

public sealed class LlamaCppDefaultPresetsTests
{
    [Fact]
    public void SelectContextLength_AmpleMemory_ReturnsFullContext()
    {
        // 64GB pool, 13GB model weights - plenty of room for the full 128k KV cache.
        var result = LlamaCppDefaultPresets.SelectContextLength(inferencePoolGb: 64, modelWeightGb: 13);

        Assert.Equal(131_072, result);
    }

    [Fact]
    public void SelectContextLength_TightMemory_StepsDownInsteadOfFailing()
    {
        // Reproduces the real OOM from docs/testing/selftest-2026-08-20.md: a 21GB model already
        // resident, only ~13GB of pool left for the next model's weights + KV cache.
        var result = LlamaCppDefaultPresets.SelectContextLength(inferencePoolGb: 13, modelWeightGb: 13);

        Assert.True(result < 131_072, "should not request full 128k context when memory is tight");
        Assert.True(result >= 8_192);
    }

    [Fact]
    public void SelectContextLength_NoRoomAtAll_ReturnsSmallestStep()
    {
        var result = LlamaCppDefaultPresets.SelectContextLength(inferencePoolGb: 10, modelWeightGb: 20);

        Assert.Equal(8_192, result);
    }

    [Fact]
    public void SelectContextLength_NeverExceedsMaxContextLength()
    {
        var result = LlamaCppDefaultPresets.SelectContextLength(inferencePoolGb: 256, modelWeightGb: 13, maxContextLength: 32_768);

        Assert.Equal(32_768, result);
    }

    [Theory]
    [InlineData(24, 13)]
    [InlineData(48, 21)]
    public void SelectContextLength_AlwaysReturnsASupportedStep(double poolGb, double weightGb)
    {
        var result = LlamaCppDefaultPresets.SelectContextLength(poolGb, weightGb);

        Assert.Contains(result, new[] { 8_192, 16_384, 32_768, 65_536, 131_072 });
    }
}
