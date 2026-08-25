using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The launcher pane: DataContext is supplied externally (a plainly-constructed HomeViewModel),
/// same contract as HomeView — this view never builds its own ViewModel.
public partial class LauncherPaneView : UserControl {
    public LauncherPaneView() => InitializeComponent();

    // Repository picker: one flyout item per ListRepositoriesAsync entry — leaf name over full
    // path, remembered-harness pill on the right, per the settled design. The scratch entry and
    // the folder-picker affordance sit last, each behind a separator.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var flyout = new MenuFlyout();
        foreach (var option in await vm.ListRepositoriesAsync()) {
            if (option.RepoPath.Length == 0) flyout.Items.Add(new Separator());
            flyout.Items.Add(RepositoryItem(vm, option));
        }
        flyout.Items.Add(new Separator());

        var add = new MenuItem { Header = "Add repository…" };
        add.Click += async (_, _) => await AddRepositoryAsync(vm);
        flyout.Items.Add(add);

        flyout.ShowAt(anchor);
    }

    MenuItem RepositoryItem(HomeViewModel vm, RepositoryOption option) {
        var isScratch = option.RepoPath.Length == 0;
        var muted = (IBrush)this.FindResource("KcapMutedBrush")!;

        var left = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        left.Children.Add(new TextBlock {
            Text = isScratch ? "No repository" : RepoLabel.Leaf(option.RepoPath),
        });
        if (!isScratch)
            left.Children.Add(new TextBlock { Text = option.RepoPath, FontSize = 10.5, Foreground = muted });

        var pill = new Border {
            Background = (IBrush)this.FindResource("KcapSurfaceRaisedBrush")!,
            CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 2),
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock {
                Text = HostedHarnessCatalog.LabelFor(vm.Harnesses, option.Vendor),
                FontSize = 10, Foreground = muted,
            },
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 260 };
        header.Children.Add(left);
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);

        var item = new MenuItem {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = option.Selected,
        };
        var path = option.RepoPath;
        item.Click += async (_, _) => await vm.SelectRepositoryAsync(path);
        return item;
    }

    // "Add one" via a native folder picker — SelectRepositoryAsync then treats the choice like
    // any other repository, so it shows up in the menu from the next open on.
    async Task AddRepositoryAsync(HomeViewModel vm) {
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

/// The pane's headline names the selected repository the way T3's new-thread screen does; the
/// scratch workspace ("") gets the plain question rather than a fake repo name.
public sealed class LauncherHeadlineConverter : IValueConverter {
    public static readonly LauncherHeadlineConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? $"What should we build in {RepoLabel.Leaf(path)}?" : "What should we build?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
