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

    // Effort picker: the shared low→xhigh ladder the daemon passes through (codex maps max→xhigh
    // itself); Default hands the choice back to the harness. A wrong value for a given vendor
    // surfaces as that session's own launch error, same as the CLI's --effort.
    static readonly string[] EffortLadder = ["low", "medium", "high", "xhigh"];

    void OnEffortChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var flyout = new MenuFlyout();
        var byDefault = new MenuItem {
            Header = "Default", ToggleType = MenuItemToggleType.Radio, IsChecked = vm.SelectedEffort is null,
        };
        byDefault.Click += (_, _) => vm.SelectedEffort = null;
        flyout.Items.Add(byDefault);
        flyout.Items.Add(new Separator());
        foreach (var effort in EffortLadder) {
            var item = new MenuItem {
                Header = effort, ToggleType = MenuItemToggleType.Radio, IsChecked = vm.SelectedEffort == effort,
            };
            var value = effort;
            item.Click += (_, _) => vm.SelectedEffort = value;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    // The combined harness+model picker (T3-style): one searchable surface, grouped by vendor,
    // each group leading with the vendor-default row and following with the curated suggestions.
    // Unavailable vendors stay listed but disabled (never withdrawn silently — HostedHarnessCatalog's
    // rule), and a non-empty search always offers itself as a custom model id, so the curated
    // catalog can drift without ever blocking a launch.
    void OnAgentChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var text = (IBrush)this.FindResource("KcapTextBrush")!;
        var muted = (IBrush)this.FindResource("KcapMutedBrush")!;
        var faint = (IBrush)this.FindResource("KcapFaintBrush")!;
        var accent = (IBrush)this.FindResource("KcapAccentBrush")!;

        var search = new TextBox { PlaceholderText = "Search models…", FontSize = 12.5 };
        var rows = new StackPanel { Spacing = 2 };
        var flyout = new Flyout {
            Placement = PlacementMode.Bottom,
            Content = new StackPanel {
                Width = 330, Spacing = 8,
                Children = { search, new ScrollViewer { MaxHeight = 380, Content = rows } },
            },
        };

        async void Pick(string vendor, string slug) {
            await vm.ChooseHarnessAsync(vendor);
            vm.SelectedModel = slug;
            flyout.Hide();
        }

        Button Row(string label, string? sub, bool enabled, bool selected, Action pick) {
            var body = new StackPanel { Spacing = 1 };
            body.Children.Add(new TextBlock {
                Text = label, FontSize = 12.5,
                Foreground = selected ? accent : enabled ? text : faint,
            });
            if (sub is not null)
                body.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = faint });

            var row = new Button {
                Content = body, IsEnabled = enabled,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5),
            };
            row.Click += (_, _) => pick();
            return row;
        }

        void Rebuild() {
            rows.Children.Clear();
            var term = search.Text?.Trim() ?? "";
            bool Matches(string s) => term.Length == 0 || s.Contains(term, StringComparison.OrdinalIgnoreCase);

            foreach (var option in vm.Harnesses) {
                var vendor = option.Vendor;
                var vendorMatches = Matches(option.Label) || Matches(option.Vendor);
                var models = HostedHarnessCatalog.ModelChoicesFor(vendor);
                var visible = vendorMatches ? models : models.Where(m => Matches(m.Label) || Matches(m.Slug)).ToList();
                var showDefault = vendorMatches || Matches("default");
                if (!showDefault && visible.Count == 0) continue;

                rows.Children.Add(new TextBlock {
                    Text = $"{option.Label}  ·  {HostedHarnessCatalog.DescriptionFor(option)}",
                    FontSize = 10, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = faint,
                    Margin = new Thickness(8, 8, 8, 2),
                });
                var isCurrentVendor = string.Equals(vm.SelectedVendor, vendor, StringComparison.OrdinalIgnoreCase);
                if (showDefault)
                    rows.Children.Add(Row(
                        "Default", $"whatever {option.Label} chooses", option.Available,
                        isCurrentVendor && vm.SelectedModel.Length == 0, () => Pick(vendor, "")));
                foreach (var model in visible)
                    rows.Children.Add(Row(
                        model.Label, model.Slug, option.Available,
                        isCurrentVendor && string.Equals(vm.SelectedModel, model.Slug, StringComparison.OrdinalIgnoreCase),
                        () => Pick(vendor, model.Slug)));
            }

            // The escape hatch: whatever was typed is offered verbatim for the CURRENT vendor,
            // unless it already matched a curated slug above.
            if (term.Length > 0 && !vm.Harnesses.SelectMany(o => HostedHarnessCatalog.ModelChoicesFor(o.Vendor))
                    .Any(m => string.Equals(m.Slug, term, StringComparison.OrdinalIgnoreCase))) {
                var current = HostedHarnessCatalog.LabelFor(vm.Harnesses, vm.SelectedVendor);
                rows.Children.Add(Row(
                    $"Use “{term}”", $"as the model id for {current}", enabled: true,
                    selected: false, () => Pick(vm.SelectedVendor, term)));
            }
        }

        search.TextChanged += (_, _) => Rebuild();
        Rebuild();
        flyout.ShowAt(anchor);
        search.Focus();
    }
}

/// AgentChip's label: "Claude · Fable 5" — vendor label plus the model's curated label (raw slug
/// when uncurated, "default" for the "" sentinel).
public sealed class AgentChipTextConverter : IMultiValueConverter {
    public static readonly AgentChipTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [IReadOnlyList<HarnessOption> options, string vendor, string model]
            ? $"{HostedHarnessCatalog.LabelFor(options, vendor)} · {HostedHarnessCatalog.ModelLabelFor(vendor, model)}"
            : "";
}

/// EffortChip's label: the chosen rung, or the default wording for null.
public sealed class EffortChipTextConverter : IValueConverter {
    public static readonly EffortChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } effort ? $"Effort: {effort}" : "Default effort";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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
