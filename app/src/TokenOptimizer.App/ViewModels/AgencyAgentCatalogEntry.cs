using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenOptimizer.App.ViewModels;

/// <summary>
/// One row in the agency-agents picker: a single agent from a single division,
/// tick to make it show up in ~/.claude/agents on next launch.
/// </summary>
public sealed partial class AgencyAgentCatalogEntry : ViewModelBase
{
    public AgencyAgentCatalogEntry(string division, string slug, string name, string description, bool isTicked)
    {
        Division = division;
        Slug = slug;
        Name = name;
        Description = description;
        IsTicked = isTicked;
    }

    public string Division { get; }
    public string Slug { get; }
    public string Name { get; }
    public string Description { get; }

    public string Key => $"{Division}/{Slug}";

    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}