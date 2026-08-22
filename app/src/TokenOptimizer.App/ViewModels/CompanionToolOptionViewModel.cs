using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenOptimizer.App.ViewModels;

/// <summary>
/// One row in the Setup tab's companion-tooling picker: a single installer
/// step from MainViewModel.CompanionToolingSteps, tick to include it in
/// "Install Companion Tooling" / the daily auto-update pass.
/// </summary>
public sealed partial class CompanionToolOptionViewModel : ViewModelBase
{
    public CompanionToolOptionViewModel(string name, string description, bool isTicked)
    {
        Name = name;
        Description = description;
        IsTicked = isTicked;
    }

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}
