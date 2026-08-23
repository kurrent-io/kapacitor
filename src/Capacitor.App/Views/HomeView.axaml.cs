using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The Home tab (Task 6, AI-2194): DataContext is supplied externally (a plainly-constructed
/// HomeViewModel), same contract as ConsentPromptWindow/MainWindow — this view never builds its
/// own ViewModel.
public partial class HomeView : UserControl {
    public HomeView() => InitializeComponent();

    void OnNewSessionClick(object? sender, RoutedEventArgs e) => GoalInput.Focus();

    // Repository picker (task-6-brief open question 2): this slice has no repository registry, so
    // the only affordance is "add one" via a native folder picker — SelectRepositoryAsync persists
    // the choice through AppState.HarnessByRepo like any other repository.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Choose a repository",
            AllowMultiple = false,
        });
        if (folders is not [{ } folder]) return;
        if (folder.TryGetLocalPath() is not { } path) return;

        await vm.SelectRepositoryAsync(path);
    }

    // Harness picker: one flyout item per HostedHarnessCatalog option, disabled (never denied
    // outright) when the daemon hasn't advertised it — same "always offered, never withdrawn
    // silently" rule HostedHarnessCatalog's own doc comment states.
    void OnHarnessChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var flyout = new MenuFlyout();
        foreach (var option in vm.Harnesses) {
            var item = new MenuItem {
                Header = $"{option.Label} — {HostedHarnessCatalog.DescriptionFor(option)}",
                IsEnabled = option.Available,
            };
            var vendor = option.Vendor;
            item.Click += async (_, _) => await vm.ChooseHarnessAsync(vendor);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
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

/// HarnessChip's label: SelectedVendor's own HarnessOption.Label when the current Harnesses list
/// carries one, falling back to the raw vendor token otherwise (e.g. before the first daemon
/// snapshot narrows the list, or a vendor token this build has never heard of).
public sealed class HarnessChipTextConverter : IMultiValueConverter {
    public static readonly HarnessChipTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) {
        if (values is not [IReadOnlyList<HarnessOption> options, string vendor]) return "";

        var match = options.FirstOrDefault(o => string.Equals(o.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
        return match?.Label ?? vendor;
    }
}

/// Single-purpose converter, not a general int-to-bool one — mirrors Views/Converters.cs's
/// HeaderRowVisibleConverter (the inverse case: empty-state text visible only while the count is
/// zero).
public sealed class CountIsZeroConverter : IValueConverter {
    public static readonly CountIsZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
