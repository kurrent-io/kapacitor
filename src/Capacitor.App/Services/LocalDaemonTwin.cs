using Capacitor.Cli.Core.Auth;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class LocalDaemonTwin {
    /// Exactly-one match or null (fail open). Server scoping first: no twin when the local
    /// daemon's server is not the app's server.
    public static (string OwnerUserId, string DaemonName)? Find(
            IReadOnlyList<DaemonInfo> daemons, string? localMachineId, string localDaemonName,
            string? localServerUrl, string? appServerUrl) {
        if (localMachineId is null) return null;
        if (!ServerIdentity.SameServer(localServerUrl, appServerUrl)) return null;

        (string, string)? found = null;
        foreach (var d in daemons) {
            if (d.MachineId != localMachineId || d.Name != localDaemonName || d.OwnerUserId is null) continue;
            if (found is not null) return null;
            found = (d.OwnerUserId, d.Name);
        }
        return found;
    }
}
