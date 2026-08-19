using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using TokenOptimizer.App.ViewModels;
using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Drag-reorder for the custom fallback chain list: press-and-hold on a
    /// row remembers it, releasing over another row asks the view model to
    /// swap sort positions. Deliberately hand-rolled with plain pointer
    /// events (no capture) rather than Avalonia's newer async DataTransfer
    /// drag/drop API - everything here is in-process, so there's no data to
    /// marshal, only a row identity to remember between press and release.
    ///
    /// The pointer is EXPLICITLY captured to the pressed row on press and
    /// released on release - without this, ListBoxItem's own built-in
    /// selection/click handling captures the pointer first, so
    /// PointerReleased would always fire back on the ORIGINAL row instead of
    /// whichever row the pointer is actually over, making every drag a
    /// no-op. With explicit capture, PointerMoved/PointerReleased keep
    /// routing to the captured row control regardless of where the cursor
    /// physically is, so the target row is found via an explicit hit-test
    /// against the ListBox at release time instead of relying on event
    /// routing. The Up/Down buttons next to each row do the same reorder
    /// and always work even if a given input device makes
    /// press-drag-release awkward.
    /// </summary>
    private FallbackChainOrderItemViewModel? _draggedChainItem;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void CustomChainItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not FallbackChainOrderItemViewModel item) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;

        _draggedChainItem = item;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void CustomChainItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        var dragged = _draggedChainItem;
        _draggedChainItem = null;
        if (dragged is null) return;
        if (DataContext is not MainViewModel viewModel) return;

        var point = e.GetPosition(CustomChainListBox);
        var hit = CustomChainListBox.InputHitTest(point);
        var targetItem = (hit as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (targetItem?.DataContext is not FallbackChainOrderItemViewModel target || ReferenceEquals(target, dragged)) return;

        viewModel.ReorderCustomChain(dragged.SortIndex, target.SortIndex);
    }

    private void MasterFolderTreeNode_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: FolderTreeNode node }) return;
        if (DataContext is not MainViewModel viewModel) return;

        viewModel.LaunchAtPathCommand.Execute(node.FullPath);
    }
}
