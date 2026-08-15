using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace TokenOptimizer.App.Services;

/// <summary>Native "select a folder" Explorer dialog, same TopLevel-resolution pattern MainViewModel already uses for clipboard access.</summary>
public static class FolderPickerService
{
    public static async Task<string?> PickFolderAsync(string title)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return null;
        }

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(window);
        if (topLevel?.StorageProvider is not { } storageProvider) return null;

        var results = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }
}
