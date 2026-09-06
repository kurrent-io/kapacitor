namespace Capacitor.App.Services.Update;

/// The tray's single update item: hidden, "Check for Updates…", or "Restart to update to &lt;v&gt;".
public sealed record UpdateMenuItem(bool Visible, string Label);
