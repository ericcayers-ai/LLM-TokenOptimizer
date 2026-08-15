using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenOptimizer.App.ViewModels;

/// <summary>One row of the user-editable custom fallback chain: a provider name, whether it's included, and its drag-reordered position.</summary>
public sealed partial class FallbackChainOrderItemViewModel : ViewModelBase
{
    public FallbackChainOrderItemViewModel(string providerName, bool isIncluded, int sortIndex)
    {
        ProviderName = providerName;
        IsIncluded = isIncluded;
        SortIndex = sortIndex;
    }

    public string ProviderName { get; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }
}
