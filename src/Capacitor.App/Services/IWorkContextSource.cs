using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// One read of a session's work context, however the app reaches the server.
public interface IWorkContextSource {
    Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct);
}
