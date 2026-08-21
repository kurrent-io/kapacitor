using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;
using ReactiveUI;

namespace Capacitor.App.Views;

/// The thin, dumb adapter wiring TrayViewModel.MenuModel into a real TrayIcon/NativeMenu (spec
/// §5): the icon (glyph + count) updates IMMEDIATELY on every model change; menu items rebuild
/// ONLY inside NativeMenu.NeedsUpdate, via TrayMenuSync's dirty-flag state machine. macOS
/// status-item menus never raise NativeMenu.Opening (confirmed live in manual acceptance) —
/// NeedsUpdate is the pre-display hook that DOES fire, so it also fire-and-forgets the
/// pause-state refresh, kicked before the rebuild it triggers; the refresh only starts async
/// socket work through the §6 serialized lane and never touches menu structure itself.
public sealed class TrayIconManager : IDisposable {
    readonly Application _app;
    readonly TrayIcon _trayIcon;
    readonly NativeMenu _menu = new();
    readonly TrayMenuBuilder _builder;
    readonly TrayMenuSync _sync = new();
    readonly IDisposable _subscription;

    public TrayIconManager(Application app, TrayViewModel vm) {
        _app = app;
        _builder = new TrayMenuBuilder(vm);

        _trayIcon = new TrayIcon { Menu = _menu, ToolTipText = "Kurrent Capacitor" };
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        _menu.NeedsUpdate += (_, _) => {
            vm.RequestPauseRefresh();
            _sync.OnNeedsUpdate(model => _builder.Rebuild(_menu, model));
        };

        // WhenAnyValue replays the current value synchronously on subscribe, so both the icon and
        // the sync's dirty flag are seeded from the live model immediately — menu ITEMS still wait
        // for the first NeedsUpdate (native menus raise it before every display, per Avalonia's own
        // doc comment on the event), never populated eagerly here.
        _subscription = vm.WhenAnyValue(x => x.MenuModel).Subscribe(model => {
            _trayIcon.Icon = TrayIconRenderer.Get(model.State, model.RunningCount);
            _sync.OnModelChanged(model);
        });
    }

    public void Dispose() {
        _subscription.Dispose();
        TrayIcon.SetIcons(_app, null);
        _trayIcon.Dispose();
    }
}
