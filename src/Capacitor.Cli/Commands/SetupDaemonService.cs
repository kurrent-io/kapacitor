using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The daemon-service capability the browser asked for, performed once setup has written its own
/// settings.
///
/// <para><b>Deliberately not in the browser leg's poll loop.</b> A service unit bakes the profile name,
/// the expected server URL and the daemon name, and the leg runs before the step that commits any of
/// them — so acting there installs a unit for the profile this process started with. After a login that
/// adopted another tenant that is the wrong server, and on a run where the user renames the daemon it is
/// a service named for one they are about to stop using, leaving the daemon they do use uninstalled.</para>
///
/// <para>The profile handed in is therefore the one setup just <i>wrote</i>, not a re-resolution of it:
/// CLI, environment and repository precedence can each land somewhere other than what this run
/// chose.</para>
/// </summary>
static class SetupDaemonService {
    /// <summary>Bounded, because the request stays outstanding when nothing lands and the screen then
    /// says it asked for ever. There is no next tick here to retry on, unlike the poll loop's.</summary>
    const int ReportAttempts = 3;

    /// <param name="ladder">Test seam for the ensure ladder; production resolves the real service
    /// manager. Null return means the platform has no service manager at all.</param>
    public static async Task RunAsync(
            IFirstRunFlowChannel             channel,
            string                           serverUrl,
            string                           flowId,
            ConfigRoot                       root,
            ProfileContext                   saved,
            UserHome                         home,
            Func<Task<ServiceEnsureJson?>>?  ladder = null,
            CancellationToken                ct     = default) {
        // Polled fresh rather than read off the leg's last view: the browser can still be open, so the
        // press can land after the leg returned and while the steps below are running.
        var poll = await channel.PollAsync(serverUrl, flowId, ct);

        if (poll.Body is not { } view) return;

        var outstanding = FirstRunFlowOutcomes.MachineActions(view)
            .FirstOrDefault(a => a.Capability == FirstRunMachineCapabilities.DaemonService);

        if (outstanding.Capability is null) return;

        AnsiConsole.MarkupLine(
            "  [dim]The browser asked to run the agent daemon as a service, so this machine stays "
          + "reachable.[/]");

        var result = await PerformAsync(root, saved, home, ladder);

        for (var attempt = 0; attempt < ReportAttempts; attempt++) {
            var reported = await channel.ReportMachineActionAsync(
                serverUrl, flowId,
                new ReportFirstRunMachineActionRequest {
                    Capability  = outstanding.Capability,
                    RequestedAt = outstanding.RequestedAt,
                    Outcome     = result.Outcome,
                    Reason      = result.Reason
                },
                ct);

            if (reported.Recorded) return;
        }
    }

    static async Task<FirstRunMachineActionResult> PerformAsync(
            ConfigRoot root, ProfileContext saved, UserHome home, Func<Task<ServiceEnsureJson?>>? ladder) {
        try {
            var run = ladder ?? (() => DaemonServiceCommands.FlowEnsureAsync(root, saved, home));

            return await run() is { } outcome
                ? EnsureFlowMap.Map(outcome)
                : new FirstRunMachineActionResult(
                      FirstRunMachineActionOutcomes.Refused,
                      FirstRunMachineActionReasons.UnsupportedPlatform);
        } catch (Exception) {
            // Something was attempted, so this is a failure rather than a refusal — and a screen left
            // waiting on an outcome that threw is the state the whole lane exists to avoid.
            return new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Failed, null);
        }
    }
}
