namespace Capacitor.Cli.Daemon.Services;

/// Thrown from the permission invoke lambda when the request settled before the hub call went
/// out. Its own type on purpose: ConnectionRetry retries OperationCanceledException and
/// InvalidOperationException as transient, and this must leave the loop at once.
internal sealed class PermissionRequestAbandonedException() : Exception("permission request settled before the server invoke");
