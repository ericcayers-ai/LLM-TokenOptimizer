using CommunityToolkit.Mvvm.ComponentModel;
using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.App.ViewModels;

public partial class ProjectCandidateViewModel : ViewModelBase
{
    public ProjectCandidateViewModel(ProjectCandidate candidate)
    {
        FullPath = candidate.FullPath;
        Name = candidate.Name;
        SeenBefore = candidate.SeenBefore;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool SeenBefore { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
