using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The Home tab: DataContext is supplied externally (a plainly-constructed
/// HomeViewModel), same contract as ConsentPromptWindow/MainWindow — this view never builds its
/// own ViewModel. The launcher card and its pickers live in LauncherPaneView now; the converters
/// below stay here because both views share them via the Views namespace.
public partial class HomeView : UserControl {
    public HomeView() => InitializeComponent();

    // The card's own DataContext (the item), not this view's: the click has to carry WHICH session
    // was clicked, and the button lives inside the ItemsControl's item template.
    void OnSessionCardClick(object? sender, RoutedEventArgs e) {
        if (DataContext is HomeViewModel vm && (sender as Control)?.DataContext is SessionCardViewModel card)
            vm.OpenSessionRequested(card.Id);
    }
}

/// RepositoryChip's label: the repo leaf name, or "No repository" for HomeViewModel.ScratchRepoPath
/// ("") — RepoLabel.Leaf("") returns "" (there is no leaf), not the sentinel this chip needs.
public sealed class RepositoryLabelConverter : IValueConverter {
    public static readonly RepositoryLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? RepoLabel.Leaf(path) : "No repository";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Single-purpose converter, not a general int-to-bool one — empty-state text visible only while
/// the count is zero.
public sealed class CountIsZeroConverter : IValueConverter {
    public static readonly CountIsZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
