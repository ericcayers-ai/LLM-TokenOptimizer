using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using TokenOptimizer.Providers.Compat;

namespace TokenOptimizer.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class RollingContextProxyTrimTests
{
    [Fact]
    public void ApplyRollingWindow_UnderBudget_PassesThroughUnchanged()
    {
        var request = BuildRequest(
            contextLength: 8192,
            system: new string('s', 40),
            messages: Enumerable.Range(0, 10).Select(_ => new string('x', 4)).ToArray());

        var original = request.ToJsonString();
        CreateProxy(8192).ApplyRollingWindow(request);

        Assert.Equal(original, request.ToJsonString());
    }

    [Fact]
    public void ApplyRollingWindow_ExactlyAtBudget_PassesThroughUnchanged()
    {
        var request = BuildRequest(
            contextLength: 4097,
            messages: new[] { new string('x', 4) });

        var original = request.ToJsonString();
        CreateProxy(4097).ApplyRollingWindow(request);

        Assert.Equal(original, request.ToJsonString());
    }

    [Fact]
    public void ApplyRollingWindow_OverBudget_DropsOldestAndInsertsMarker()
    {
        var request = BuildRequest(
            contextLength: 4097,
            messages: new[] { "first", "second" });

        CreateProxy(4097).ApplyRollingWindow(request);

        var messages = request["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("rolling context window", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("second", messages[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyRollingWindow_ToolResultSpanningCut_RePairsToolUse()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new string('x', 200),
                },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "tu_1",
                            ["name"] = "calc",
                            ["input"] = new JsonObject(),
                        },
                    },
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = "tu_1",
                            ["content"] = "ok",
                        },
                    },
                },
            },
        };

        CreateProxy(4097).ApplyRollingWindow(request);

        var messages = request["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_use", messages[1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_result", messages[2]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyRollingWindow_SystemPromptAloneExceedsWindow_DoesNothing()
    {
        var request = BuildRequest(
            contextLength: 8192,
            system: new string('s', 40_000),
            messages: new[] { "hello" });

        var original = request.ToJsonString();
        CreateProxy(8192).ApplyRollingWindow(request);

        Assert.Equal(original, request.ToJsonString());
    }

    private static JsonObject BuildRequest(int contextLength, string? system = null, params string[] messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            arr.Add(new JsonObject { ["role"] = "user", ["content"] = m });
        }

        var request = new JsonObject { ["messages"] = arr };
        if (system is not null) request["system"] = system;
        return request;
    }

    private static RollingContextProxy CreateProxy(int contextLength) =>
        new(new Uri("http://127.0.0.1:1"), () => null, contextLength);
}
