using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenOptimizer.App.ViewModels;

/// <summary>One provider's row of models in the Models card, collapsible so a long combined list stays scannable.</summary>
public sealed partial class ProviderModelGroupViewModel : ViewModelBase
{
    public ProviderModelGroupViewModel(string providerName, IEnumerable<ProviderModelOptionViewModel> models, bool isBridgeable)
    {
        ProviderName = providerName;
        IsBridgeable = isBridgeable;
        Models = new ObservableCollection<ProviderModelOptionViewModel>(models);
    }

    public string ProviderName { get; }
    public bool IsBridgeable { get; }
    public string ProviderHeader => IsBridgeable ? ProviderName : $"{ProviderName} (opens separately)";
    public ObservableCollection<ProviderModelOptionViewModel> Models { get; }

    /// <summary>Expanded by default - collapsing is an opt-out for someone who wants a shorter list, not the default state.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}
