using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One coding-agent vendor: its id, label and exclusive plugin-install flag (null = Claude's
/// flagless default). Order is display AND sequential-install order (Claude first) — shared by the
/// Agents and Import steps.
internal sealed record AgentVendor(HarnessId Id, string Label, string? Flag);

/// Taken from Core's registry so the app and the CLI enumerate the same vendors, in the same order —
/// a tenth harness appears here without an edit.
internal static class AgentVendors {
    public static readonly IReadOnlyList<AgentVendor> All =
        [.. HarnessRegistry.Identities.Select(h => new AgentVendor(h.Id, h.Label, h.Id.PluginInstallFlag))];
}

public enum AgentInstallStatus { NotRun, Installing, Succeeded, Failed }

/// One vendor row on the Agents step, with its own checkbox and its own Retry command.
public sealed class AgentVendorRow : ReactiveObject {
    internal readonly string? Flag;
    readonly HarnessId _id;

    internal AgentVendorRow(AgentVendor vendor, Func<AgentVendorRow, Task> retry, IObservable<bool> canRetry) {
        Label = vendor.Label;
        Flag  = vendor.Flag;
        _id   = vendor.Id;

        RetryCommand = ReactiveCommand.CreateFromTask(() => retry(this), canRetry);
    }

    public string Label { get; }

    public bool IsSelected {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public AgentInstallStatus Status {
        get;
        internal set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Glyph));
            this.RaisePropertyChanged(nameof(Failed));
            this.RaisePropertyChanged(nameof(Succeeded));
        }
    } = AgentInstallStatus.NotRun;

    public string? Message {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string Glyph => Status switch {
        AgentInstallStatus.Succeeded  => "✓",
        AgentInstallStatus.Failed     => "⚠",
        AgentInstallStatus.Installing => "…",
        _                             => "",
    };

    public bool Failed    => Status == AgentInstallStatus.Failed;
    public bool Succeeded => Status == AgentInstallStatus.Succeeded;

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    internal bool DetectedIn(IReadOnlySet<HarnessId> detected) => detected.Contains(_id);
}

/// One checkbox per coding-agent vendor, pre-checked when detected. Install runs sequentially, in
/// registry order, so one vendor's failure never blocks the rest — successes stand, and only the
/// failed row offers Retry.
public sealed class AgentsStepViewModel : ReactiveObject, IWizardStep {
    readonly IKcapCli _cli;
    readonly Func<CancellationToken, Task<IReadOnlySet<HarnessId>>> _detect;

    IReadOnlySet<HarnessId>? _detected;
    Task?             _inFlight;

    public AgentsStepViewModel(IKcapCli cli, Func<CancellationToken, Task<IReadOnlySet<HarnessId>>> detect) {
        _cli    = cli;
        _detect = detect;

        var idle = this.WhenAnyValue(x => x.Busy, busy => !busy);
        Rows = AgentVendors.All.Select(v => new AgentVendorRow(v, RetryOneAsync, idle)).ToList();
        InstallCommand = ReactiveCommand.CreateFromTask(RunInstallAsync, idle.Select(i => i && CliAvailable));
    }

    public WizardStepId Id         => WizardStepId.Agents;
    public string       Title      => "Coding agents";
    public bool         Applicable => true;

    public IReadOnlyList<AgentVendorRow> Rows { get; }

    public bool CliAvailable => _cli.CliPath is not null;

    public string? Message => CliAvailable ? null : "kcap CLI not found";

    public bool Busy {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    public bool Satisfied {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> InstallCommand { get; }

    public async Task OnEnterAsync(CancellationToken ct) {
        if (_detected is not null) return; // cached: re-entering the step must not stomp user choices

        IReadOnlySet<HarnessId> detected;
        try { detected = await _detect(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _detected = detected;
        foreach (var row in Rows) row.IsSelected = row.DetectedIn(detected);
    }

    // Never vetoes: installs are short/bounded, so leaving just awaits the in-flight one rather than killing it.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (_inFlight is { } run) {
            try { await run.ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard agents install failed unexpectedly: {ex.Message}"); }
        }

        return true;
    }

    internal Task RunInstallAsync() {
        if (Busy) return Task.CompletedTask;
        return _inFlight = RunInstallCoreAsync();
    }

    async Task RunInstallCoreAsync() {
        if (!CliAvailable) return;

        Busy = true;
        try {
            foreach (var row in Rows.Where(r => r.IsSelected).ToList())
                await InstallOneAsync(row).ConfigureAwait(false);
        } finally {
            Busy = false;
        }
    }

    internal Task RetryOneAsync(AgentVendorRow row) {
        if (Busy) return Task.CompletedTask;
        return _inFlight = RetryOneCoreAsync(row);
    }

    async Task RetryOneCoreAsync(AgentVendorRow row) {
        if (!CliAvailable) return;

        Busy = true;
        try { await InstallOneAsync(row).ConfigureAwait(false); }
        finally { Busy = false; }
    }

    async Task InstallOneAsync(AgentVendorRow row) {
        row.Status  = AgentInstallStatus.Installing;
        row.Message = null;

        try {
            var result = await _cli.PluginInstallAsync(row.Flag, CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode == 0) {
                row.Status = AgentInstallStatus.Succeeded;
            } else {
                row.Status  = AgentInstallStatus.Failed;
                row.Message = string.IsNullOrWhiteSpace(result.Stderr) ? $"Install failed (exit {result.ExitCode})." : result.Stderr;
            }
        } catch (Exception ex) {
            row.Status  = AgentInstallStatus.Failed;
            row.Message = ex.Message;
        }

        UpdateSatisfied();
    }

    void UpdateSatisfied() {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        Satisfied = selected.Count > 0 && selected.All(r => r.Status == AgentInstallStatus.Succeeded);
    }

    /// The login-shell terminal PATH when the probe resolves one, in place of the process's own: a
    /// GUI launch inherits only launchd's PATH, and the app spawns kcap with the terminal's — so
    /// detecting through the wider process PATH would report agents its own installs cannot reach.
    public static Func<CancellationToken, Task<IReadOnlySet<HarnessId>>> BuildDetectionFeed(
            ILoginShellProbe probe, UserHome home) {
        // Resolved once, outside the closure: both vendor steps share one feed and each entry calls
        // it, so resolving inside would read the overrides again per step.
        var harnesses = HarnessRegistry.FromEnvironment(home);

        return async ct => {
            var terminalPath = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
            var searched     = terminalPath is null ? harnesses : harnesses.Searching(BinaryProbe.Searching(terminalPath));

            // A set rather than the live registry: the step pre-checks its rows once and must not
            // have them move under the user while they are choosing.
            return searched.Where(h => searched.Detected(h.Id)).Select(h => h.Id).ToHashSet();
        };
    }
}
