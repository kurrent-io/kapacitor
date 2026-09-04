using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The work-context pane. DataContext is the workspace's WorkContextViewModel, supplied by
/// WorkspaceView; this view builds nothing of its own.
public partial class WorkContextView : UserControl {
    public WorkContextView() {
        InitializeComponent();
    }

    void OnHeaderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);
}
