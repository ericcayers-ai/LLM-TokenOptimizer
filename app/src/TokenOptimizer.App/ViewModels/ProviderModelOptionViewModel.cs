using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenOptimizer.App.ViewModels;

/// <summary>
/// One row in the Models card: a single model from a single provider, tick
/// to make it show up in Claude Code's own /model list on next launch.
/// Bridgeable models (Claude direct, Groq, OpenCode Go) share one Claude
/// Code CLI window via UnifiedModelRouter; non-bridgeable ones (Codex,
/// Cursor, Antigravity - each its own closed CLI with no chat-completions
/// API to bridge) open their own separate window instead, shown plainly in
/// the UI rather than hidden.
/// </summary>
public sealed partial class ProviderModelOptionViewModel : ViewModelBase
{
    public ProviderModelOptionViewModel(string providerName, string modelId, string displayLabel, bool isBridgeable, bool isTicked)
    {
        ProviderName = providerName;
        ModelId = modelId;
        DisplayLabel = displayLabel;
        IsBridgeable = isBridgeable;
        IsTicked = isTicked;
    }

    public string ProviderName { get; }
    public string ModelId { get; }
    public string DisplayLabel { get; }
    public bool IsBridgeable { get; }

    public string Key => $"{ProviderName}::{ModelId}";

    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}
