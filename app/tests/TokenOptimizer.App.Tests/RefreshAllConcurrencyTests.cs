using System.Runtime.Versioning;
using TokenOptimizer.App.ViewModels;

namespace TokenOptimizer.App.Tests;

/// <summary>
/// Regression test for the cold-launch race where the constructor's three
/// fire-and-forget chains (RefreshAllAsync plus the two CLI-login checks that
/// each end in their own RefreshAllAsync) could both observe ModelCatalog.Count
/// == 0 and independently repopulate ModelCatalogGroups, doubling whole
/// provider/model groups. The fix is an in-flight-Task cache on RefreshAllAsync
/// itself, so every caller shares one real execution - three concurrent calls
/// must converge to a single population with no duplicate group names.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RefreshAllConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRefreshAllCalls_ProduceNoDuplicateModelGroups()
    {
        var vm = new MainViewModel();

        await Task.WhenAll(
                vm.RefreshAllAsync(),
                vm.RefreshAllAsync(),
                vm.RefreshAllAsync())
            .WaitAsync(TimeSpan.FromSeconds(120));

        var groupNames = vm.ModelCatalogGroups.Select(g => g.ProviderName).ToList();
        Assert.NotEmpty(groupNames);
        Assert.Equal(groupNames.Count, groupNames.Distinct().Count());
    }
}