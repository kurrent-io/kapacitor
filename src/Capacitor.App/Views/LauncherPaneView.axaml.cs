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
    // the folder-picker affordance sit last, each behind a separator. Built as a kcapPanel Flyout
    // (same shape as the agent picker) rather than MenuFlyout — Fluent's radio MenuItem chrome
    // fights the dark palette.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var muted = Brush("KcapMutedBrush");
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };

        var flyout = PanelFlyout(rows, minWidth: 300);
        foreach (var option in await vm.ListRepositoriesAsync()) {
            // Scratch ("No repository") is listed after real repos — the separator is only the
            // break between those two groups, never a stray rule above the first row.
            if (option.RepoPath.Length == 0 && rows.Children.Count > 0)
                rows.Children.Add(ChoiceSeparator());
            rows.Children.Add(RepositoryRow(vm, option, muted, flyout));
        }
        rows.Children.Add(ChoiceSeparator());
        rows.Children.Add(ChoiceRow("Add repository…", selected: false, () => {
            flyout.Hide();
            _ = AddRepositoryAsync(vm);
        }));

        flyout.ShowAt(anchor);
    }

    Button RepositoryRow(HomeViewModel vm, RepositoryOption option, IBrush muted, Flyout flyout) {
        var isScratch = option.RepoPath.Length == 0;
        var title = new TextBlock {
            Text = isScratch ? "No repository" : RepoLabel.Leaf(option.RepoPath),
            FontSize = 13.5,
            FontWeight = option.Selected ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Brush("KcapTextBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        // Pill sits on the title row only (not vertically centered on title+path), so every
        // repo's badge lines up with "No repository"'s badge.
        var pill = new Border {
            Background = Brush("KcapMutedBrush"), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock {
                Text = HostedHarnessCatalog.LabelFor(vm.Harnesses, option.Vendor),
                FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = Brush("KcapOnPrimaryBrush"),
            },
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            MinWidth = 260,
            // Stretch so the * column eats leftover width and the pill stays right-aligned
            // across one-line and two-line rows alike.
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        if (!isScratch) {
            var path = new TextBlock {
                Text = option.RepoPath, FontSize = 10.5, Foreground = muted,
                Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetRow(path, 1);
            grid.Children.Add(path);
        }

        var repoPath = option.RepoPath;
        return ChoiceButton(grid, () => {
            flyout.Hide();
            _ = vm.SelectRepositoryAsync(repoPath);
        });
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

    // Effort picker over HostedHarnessCatalog.EffortLadder; Default hands the choice back to the
    // harness. A wrong value for a given vendor surfaces as that session's own launch error, same
    // as the CLI's --effort. Separator after Default matches Permissions (base vs escalations).
    void OnEffortChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 180);

        rows.Children.Add(ChoiceRow(HostedHarnessCatalog.EffortDefaultLabel, vm.SelectedEffort is null, () => {
            vm.SelectedEffort = null;
            flyout.Hide();
        }));
        rows.Children.Add(ChoiceSeparator());
        foreach (var effort in HostedHarnessCatalog.EffortLadder) {
            var value = effort;
            rows.Children.Add(ChoiceRow(HostedHarnessCatalog.EffortLabelFor(value), vm.SelectedEffort == effort, () => {
                vm.SelectedEffort = value;
                flyout.Hide();
            }));
        }
        flyout.ShowAt(anchor);
    }

    void OnPermissionChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 200);

        // Manual first (omit-from-wire default), then separator, then escalations — same shape as Effort.
        foreach (var mode in HostedHarnessCatalog.PermissionModes) {
            if (rows.Children.Count == 1)
                rows.Children.Add(ChoiceSeparator());

            var token = mode.Token;
            var selected = string.Equals(vm.SelectedPermissionMode, token, StringComparison.Ordinal);
            rows.Children.Add(ChoiceRow(mode.Label, selected, () => {
                vm.SelectedPermissionMode = token;
                flyout.Hide();
            }));
        }
        flyout.ShowAt(anchor);
    }

    // Shared chip-picker chrome: kcapPanel Flyout + ghost rows; selection is weight, not green.
    static Flyout PanelFlyout(Control content, double minWidth) {
        var host = new Border { Child = content, MinWidth = minWidth };
        var flyout = new Flyout {
            Placement = PlacementMode.Bottom, Content = host,
            // A few pixels of air between the chip and the panel — flush looks glued on.
            VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        return flyout;
    }

    Button ChoiceRow(string label, bool selected, Action pick) {
        var body = new TextBlock {
            Text = label, FontSize = 13.5,
            FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Brush("KcapTextBrush"),
        };
        return ChoiceButton(body, pick);
    }

    static Button ChoiceButton(object content, Action pick) {
        var row = new Button {
            Content = content,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            // Stretch: left-aligned content stays content-sized and never pushes a trailing pill
            // to the row's right edge (repo list badges).
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
        };
        row.Classes.Add("kcapGhost");
        row.Click += (_, _) => pick();
        return row;
    }

    Border ChoiceSeparator() => new() {
        Height = 1, Margin = new Thickness(8, 4),
        Background = Brush("KcapBorderBrush"),
    };

    IBrush Brush(string key) => (IBrush)this.FindResource(key)!;

    /// The vendor's mark at a given size: the brand path when VendorIcons carries one, the tinted
    /// monogram otherwise (both from HostedHarnessCatalog.TileFor). UI-thread only (constructs
    /// thread-affine Geometry/brushes).
    internal static Control BuildGlyph(string vendor, double size) {
        var (glyph, color) = HostedHarnessCatalog.TileFor(vendor);
        if (VendorIcons.For(vendor) is { } geometry)
            return new Avalonia.Controls.Shapes.Path {
                Data = geometry, Fill = new SolidColorBrush(Color.Parse(color)),
                Width = size, Height = size, Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        return new TextBlock {
            Text = glyph, FontSize = size, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    // The combined harness+model picker, T3-shaped: a vendor icon rail on the left, an underlined
    // search over the model rows on the right. No search term = the active vendor tab's models;
    // typing searches ACROSS vendors and always offers the term verbatim as a custom model id, so
    // the curated catalog can drift without ever blocking a launch. Unavailable vendors stay
    // listed but disabled (never withdrawn silently — HostedHarnessCatalog's rule).
    void OnAgentChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var text = (IBrush)this.FindResource("KcapTextBrush")!;
        var muted = (IBrush)this.FindResource("KcapMutedBrush")!;
        var faint = (IBrush)this.FindResource("KcapFaintBrush")!;
        var success = (IBrush)this.FindResource("KcapSuccessBrush")!;
        var raised = (IBrush)this.FindResource("KcapSurfaceRaisedBrush")!;
        var border = (IBrush)this.FindResource("KcapBorderBrush")!;

        var currentTab = vm.SelectedVendor;

        var searchBox = new TextBox {
            PlaceholderText = "Search models…", FontSize = 13.5,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var underline = new Border { Height = 1, Background = border };
        var magnifier = new Avalonia.Controls.Shapes.Path {
            Data = Geometry.Parse("M13.5,13.5 L10.4,10.4 M11.5,6.75 A4.75,4.75 0 1 1 2,6.75 A4.75,4.75 0 1 1 11.5,6.75"),
            Stroke = muted, StrokeThickness = 1.7, Width = 16, Height = 16,
            Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(10, 8, 10, 10) };
        var tabs = new StackPanel { Spacing = 6, Margin = new Thickness(9, 12, 9, 12) };

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 46 };
        searchRow.Children.Add(magnifier);
        Grid.SetColumn(searchBox, 1);
        searchRow.Children.Add(searchBox);

        var header = new StackPanel();
        header.Children.Add(searchRow);
        header.Children.Add(underline);

        var right = new DockPanel();
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        right.Children.Add(header);
        right.Children.Add(new ScrollViewer { Height = 380, Content = rows });

        var rail = new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = border, Child = tabs };

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*"), Width = 440 };
        root.Children.Add(rail);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        var flyout = new Flyout {
            Placement = PlacementMode.Bottom, Content = root, VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");

        async void Pick(string vendor, string slug) {
            await vm.ChooseHarnessAsync(vendor);
            vm.SelectedModel = slug;
            flyout.Hide();
        }

        Button Row(string vendor, string vendorLabel, string label, bool enabled, bool selected, Action pick) {
            var sub = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            sub.Children.Add(BuildGlyph(vendor, 10));
            sub.Children.Add(new TextBlock { Text = vendorLabel, FontSize = 11.5, Foreground = muted });

            var body = new StackPanel();
            body.Children.Add(new TextBlock {
                Text = label, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
                Foreground = selected ? success : enabled ? text : faint,
            });
            body.Children.Add(sub);

            var row = new Button {
                Content = body, IsEnabled = enabled,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
            };
            row.Classes.Add("kcapGhost");
            row.Click += (_, _) => pick();
            return row;
        }

        void AddVendorRows(HarnessOption option, Func<string, bool> matches, bool includeDefault) {
            var vendor = option.Vendor;
            var isCurrentVendor = string.Equals(vm.SelectedVendor, vendor, StringComparison.OrdinalIgnoreCase);
            if (includeDefault)
                rows.Children.Add(Row(
                    vendor, option.Label, $"Default — {option.Label} chooses", option.Available,
                    isCurrentVendor && vm.SelectedModel.Length == 0, () => Pick(vendor, "")));
            foreach (var model in HostedHarnessCatalog.ModelChoicesFor(vendor)
                         .Where(m => matches(m.Label) || matches(m.Slug)))
                rows.Children.Add(Row(
                    vendor, option.Label, model.Label, option.Available,
                    isCurrentVendor && string.Equals(vm.SelectedModel, model.Slug, StringComparison.OrdinalIgnoreCase),
                    () => Pick(vendor, model.Slug)));
        }

        void RebuildRows() {
            rows.Children.Clear();
            var term = searchBox.Text?.Trim() ?? "";

            if (term.Length == 0) {
                var option = vm.Harnesses.FirstOrDefault(
                    o => string.Equals(o.Vendor, currentTab, StringComparison.OrdinalIgnoreCase));
                if (option is not null) AddVendorRows(option, _ => true, includeDefault: true);
                return;
            }

            bool Matches(string s) => s.Contains(term, StringComparison.OrdinalIgnoreCase);
            foreach (var option in vm.Harnesses) {
                var vendorMatches = Matches(option.Label) || Matches(option.Vendor);
                AddVendorRows(
                    option,
                    vendorMatches ? _ => true : Matches,
                    includeDefault: vendorMatches || Matches("default"));
            }

            // The escape hatch: whatever was typed, offered verbatim for the active vendor tab.
            if (!vm.Harnesses.SelectMany(o => HostedHarnessCatalog.ModelChoicesFor(o.Vendor))
                    .Any(m => string.Equals(m.Slug, term, StringComparison.OrdinalIgnoreCase))) {
                var tabOption = vm.Harnesses.FirstOrDefault(
                    o => string.Equals(o.Vendor, currentTab, StringComparison.OrdinalIgnoreCase));
                var tabLabel = tabOption?.Label ?? currentTab;
                rows.Children.Add(Row(
                    currentTab, tabLabel, $"Use “{term}”", tabOption?.Available ?? true,
                    selected: false, () => Pick(currentTab, term)));
            }
        }

        void RebuildTabs() {
            tabs.Children.Clear();
            foreach (var option in vm.Harnesses) {
                var vendor = option.Vendor;
                var active = string.Equals(vendor, currentTab, StringComparison.OrdinalIgnoreCase);
                var tile = new Button {
                    Content = BuildGlyph(vendor, 16), IsEnabled = option.Available,
                    Width = 34, Height = 34, Padding = new Thickness(0),
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = active ? raised : Brushes.Transparent,
                    BorderBrush = active ? border : Brushes.Transparent, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Opacity = option.Available ? 1 : 0.4,
                };
                tile.Classes.Add(active ? "kcapChip" : "kcapGhost");
                ToolTip.SetTip(tile, $"{option.Label} — {HostedHarnessCatalog.DescriptionFor(option)}");
                tile.Click += (_, _) => {
                    currentTab = vendor;
                    RebuildTabs();
                    RebuildRows();
                };
                tabs.Children.Add(tile);
            }
        }

        searchBox.TextChanged += (_, _) => RebuildRows();
        RebuildTabs();
        RebuildRows();
        flyout.ShowAt(anchor);
        searchBox.Focus();
    }
}

/// The vendor's mark as bindable content (AgentChip's leading glyph). One-way; parameter is the
/// size in pixels. Bindings evaluate on the UI thread, which BuildGlyph requires.
public sealed class VendorGlyphConverter : IValueConverter {
    public static readonly VendorGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor
            ? LauncherPaneView.BuildGlyph(vendor, double.TryParse(parameter as string, out var size) ? size : 13)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// AgentChip's label: "Claude · Fable 5" — vendor label plus the model's curated label (raw slug
/// when uncurated, "Default" for the "" sentinel). Same "left · right" shape as Effort/Permissions.
public sealed class AgentChipTextConverter : IMultiValueConverter {
    public static readonly AgentChipTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [IReadOnlyList<HarnessOption> options, string vendor, string model]
            ? $"{HostedHarnessCatalog.LabelFor(options, vendor)} · {HostedHarnessCatalog.ModelLabelFor(vendor, model)}"
            : "";
}

/// EffortChip's label: always "Effort · …" — Default when null, otherwise the chosen rung's
/// sentence-case label. Keeps the category visible after a pick.
public sealed class EffortChipTextConverter : IValueConverter {
    public static readonly EffortChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"Effort · {HostedHarnessCatalog.EffortLabelFor(value as string)}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// PermissionChip's label: always "Permissions · …" so a bare "Manual" is not mistaken for an
/// effort or harness setting.
public sealed class PermissionChipTextConverter : IValueConverter {
    public static readonly PermissionChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string token
            ? $"Permissions · {HostedHarnessCatalog.PermissionModeLabelFor(token)}"
            : "Permissions · Manual";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PermissionChipVisibleConverter : IValueConverter {
    public static readonly PermissionChipVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor && HostedHarnessCatalog.SupportsPermissionMode(vendor);

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
