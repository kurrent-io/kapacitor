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

        menu.Items.Add(BuildPauseItem(model.Pause));
        menu.Items.Add(new NativeMenuItem("Open Kurrent Capacitor") { Command = vm.OpenMainWindowCommand });
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
    NativeMenuItem BuildPauseItem(TrayPauseItem pause) =>
        new("Pause new launches") {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = pause.Checked,
            IsEnabled = pause.Enabled,
            Command = vm.TogglePauseCommand,
            CommandParameter = !pause.Checked,
        };
}
