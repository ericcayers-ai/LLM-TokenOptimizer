using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.App.Cli;

namespace TokenOptimizer.App.Tests;

[SupportedOSPlatform("windows")]
public sealed class CliHostArgParsingTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public CliHostArgParsingTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(Console.Out);
        Console.SetError(Console.Error);
        _stdout.Dispose();
        _stderr.Dispose();
    }

    [Fact]
    public async Task NoCommand_ReturnsFail()
    {
        var exit = await CliHost.RunAsync([]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("No command given", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownCommand_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["not-a-command"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("not-a-command", json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("launch", "--project")]
    [InlineData("uninstall", "--confirm")]
    [InlineData("master-folder-set", "--path")]
    [InlineData("create-project", "--path")]
    [InlineData("add-project", "--path")]
    [InlineData("set-credential", "--provider")]
    [InlineData("opt-in", "--provider")]
    [InlineData("export-handoff", "--project")]
    public async Task MissingRequiredArg_NamesTheArg(string command, string argName)
    {
        var exit = await CliHost.RunAsync([command]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains(argName, json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetConfig_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["reset-config"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.True(json["data"]!["reset"]!.GetValue<bool>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task History_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["history"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["history"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task SetCredential_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["set-credential", "--provider", "groq", "--key", "test-key"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("Groq", json["data"]!["stored"]!.GetValue<string>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task OptIn_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["opt-in", "--provider", "antigravity"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("Antigravity", json["data"]!["optedIn"]!.GetValue<string>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Providers_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["providers"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["providers"]);
        Assert.NotNull(json["data"]!["auto"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Status_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["status"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["dependencies"]);
        Assert.NotNull(json["data"]!["fallbackChain"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task MasterFolderSet_InvalidPath_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["master-folder-set", "--path", "not-a-real-folder"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid master folder", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProject_MissingName_ReturnsFailNamingName()
    {
        var exit = await CliHost.RunAsync(["create-project", "--path", Environment.CurrentDirectory]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--name", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetCredential_MissingKey_ReturnsFailNamingKey()
    {
        var exit = await CliHost.RunAsync(["set-credential", "--provider", "groq"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--key", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninstall_WrongConfirm_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["uninstall", "--confirm", "no"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("UNINSTALL", json["error"]!.GetValue<string>());
    }

    private JsonNode ParseStdout()
    {
        var text = _stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected JSON on stdout");
        return JsonNode.Parse(text)!;
    }
}
