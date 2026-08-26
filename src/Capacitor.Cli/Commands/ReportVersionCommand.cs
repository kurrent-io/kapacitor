using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hidden command spawned by the npm wrapper right after <c>kcap update</c> installs a new
/// binary: makes one authenticated GET against <see cref="WhoamiCommand.ProbePath"/> (read-only,
/// no side effects — unlike <see cref="SetupCommand"/>'s cli-setup POST) so the server's
/// endpoint-agnostic version-observer middleware sees the new <see cref="HttpClientExtensions.CliVersionHeader"/>
/// immediately. Fail-open: never throws, never prints, always returns 0, bounded to
/// <see cref="Budget"/> total (discovery + request).
/// </summary>
public sealed class ReportVersionCommand(ConfigRoot config, ProfileContext profiles) {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public async Task<int> HandleAsync() {
        try {
            using var cts = new CancellationTokenSource(Budget);

            // `report-version` is an offline command, so it is reached with no server configured —
            // and it must still return 0 silently rather than error. The local default gives the
            // probe somewhere to fail against.
            var effectiveBaseUrl = profiles.Resolution.ServerUrl ?? "http://localhost:5108";

            var (client, status) =
                await HttpClientExtensions.CreateClientWithAuthStatusAsync(config, profiles, effectiveBaseUrl, cts.Token);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return 0;

                var url = AppConfig.NormalizeUrl(effectiveBaseUrl) + WhoamiCommand.ProbePath;

                using var _ = await client.GetOnceAsync(url, Budget, cts.Token);
            }
        } catch {
            // Fail-open — see class doc.
        }

        return 0;
    }
}
