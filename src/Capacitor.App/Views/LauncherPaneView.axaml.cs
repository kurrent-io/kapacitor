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

    // Monogram + tint per vendor: the glyph is the fallback where VendorIcons has no brand mark
    // (kiro, antigravity, pi, unknown tokens); the tint colors both the mark and the monogram.
    // Monochrome brands render in the near-white text color; claude/gemini keep their brand hues.
    static readonly Dictionary<string, (string Glyph, string Color)> VendorTiles = new(StringComparer.OrdinalIgnoreCase) {
        ["claude"]      = ("✳", "#D97757"),
        ["codex"]       = ("Cx", "#F1F3F7"),
        ["cursor"]      = ("Cu", "#F1F3F7"),
        ["copilot"]     = ("Cp", "#F1F3F7"),
        ["gemini"]      = ("Ge", "#7BA7F7"),
        ["kiro"]        = ("Ki", "#B78BF7"),
        ["opencode"]    = ("Oc", "#F1F3F7"),
        ["antigravity"] = ("An", "#F4B860"),
        ["pi"]          = ("π", "#A994FF"),
    };

    static (string Glyph, string Color) TileFor(string vendor) =>
        VendorTiles.TryGetValue(vendor, out var tile)
            ? tile
            : (vendor.Length > 0 ? vendor[..1].ToUpperInvariant() : "?", "#9299AA");

    /// The vendor's mark at a given size: the brand path when VendorIcons carries one, the tinted
    /// monogram otherwise. UI-thread only (constructs thread-affine Geometry/brushes).
    internal static Control BuildGlyph(string vendor, double size) {
        var (glyph, color) = TileFor(vendor);
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
        var accent = (IBrush)this.FindResource("KcapAccentBrush")!;
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

        var flyout = new Flyout { Placement = PlacementMode.Bottom, Content = root };
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
                Foreground = selected ? accent : enabled ? text : faint,
            });
            body.Children.Add(sub);

            var row = new Button {
                Content = body, IsEnabled = enabled,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
            };
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
        searchBox.GotFocus += (_, _) => underline.Background = accent;
        searchBox.LostFocus += (_, _) => underline.Background = border;
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
