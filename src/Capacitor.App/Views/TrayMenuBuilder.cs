using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Rebuilds a NativeMenu's Items from a TrayMenuModel snapshot (spec §5): clears and repopulates
/// in full every time — NativeMenu has no ItemsSource, and a full rebuild is cheap at these sizes.
/// Layout: disabled header, separator, agent entries with a Stop/Open-in-web submenu each (only
/// when the model has entries, with a trailing separator), the pause toggle, "Open Kurrent
/// Capacitor", a separator, then "Quit".
public sealed class TrayMenuBuilder(TrayViewModel vm) {
    public void Rebuild(NativeMenu menu, TrayMenuModel model) {
        menu.Items.Clear();

        menu.Items.Add(new NativeMenuItem(model.Header) { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());

        if (model.Agents.Count > 0) {
            foreach (var entry in model.Agents) menu.Items.Add(BuildAgentItem(entry));
            menu.Items.Add(new NativeMenuItemSeparator());
        }

        // Between the agents section and the pause toggle (spec §8), visible only while a launch
        // is actually awaiting the owner.
        if (model.PendingConsent > 0)
            menu.Items.Add(new NativeMenuItem("Review pending launches…") { Command = vm.ReviewPendingCommand });

        menu.Items.Add(BuildPauseItem(model.Pause));
        menu.Items.Add(new NativeMenuItem("Open Kurrent Capacitor") { Command = vm.OpenMainWindowCommand });

        // AI-1654 §5: visible only while applicable-but-absent (ShimOfferCoordinator.Offerable) —
        // a manual click always re-runs the install path, regardless of the once-ever auto-offer.
        if (model.ShimInstallVisible)
            menu.Items.Add(new NativeMenuItem("Install command-line tool…") { Command = vm.InstallShimCommand });

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Quit") { Command = vm.QuitCommand });
    }

    NativeMenuItem BuildAgentItem(TrayAgentEntry entry) {
        var submenu = new NativeMenu();
        submenu.Items.Add(new NativeMenuItem("Stop") {
            Command = vm.StopAgentCommand, CommandParameter = entry.Id, IsEnabled = entry.StopEnabled,
        });
        submenu.Items.Add(new NativeMenuItem("Open in web") {
            Command = vm.OpenInWebCommand, CommandParameter = entry.Id,
        });
        return new NativeMenuItem(entry.Label) { Menu = submenu };
    }

    // The frozen-desired-value capture rule (spec §6): CommandParameter is the desired checked
    // value computed HERE, at rebuild time, from the model's last-known Checked — the click
    // handler must never read NativeMenuItem.IsChecked, because Avalonia's native click path
    // (TrayIcon/NativeMenuItem.RaiseClicked, decompiler-verified) never mutates it.
    //
    // IsEnabled MUST be assigned last: NativeMenuItem.OnPropertyChanged reacts to the Command
    // assignment by recomputing IsEnabled from Command.CanExecute(CommandParameter) (decompiler-
    // verified), which would silently overwrite an earlier IsEnabled = pause.Enabled with true.
    NativeMenuItem BuildPauseItem(TrayPauseItem pause) =>
        new("Pause new launches") {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = pause.Checked,
            Command = vm.TogglePauseCommand,
            CommandParameter = !pause.Checked,
            IsEnabled = pause.Enabled,
        };
}
