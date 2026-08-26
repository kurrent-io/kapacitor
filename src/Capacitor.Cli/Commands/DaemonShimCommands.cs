using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The decision <c>kcap daemon shim ensure</c> makes from a fresh login-shell probe: whether the
/// terminal already resolves <c>kcap</c> (the flow's done state), whether the shim should be
/// installed, or whether to fail closed with a coded reason. Pure — no I/O — so the ladder is
/// directly testable. Every ambiguous row fails closed, never guessed.
/// </summary>
internal enum ShimEnsureAction {
    /// <summary>kcap already resolves from the terminal — nothing to do.</summary>
    AlreadyOnPath,

    /// <summary>Probe positively found no kcap; macOS shim install is the fix.</summary>
    Install,

    /// <summary>Nothing was mutated; <see cref="ShimEnsureDecision.Reason"/> names the row.</summary>
    Refuse,
}

/// <summary>One ladder classification. <see cref="Reason"/> is non-null only for
/// <see cref="ShimEnsureAction.Refuse"/> — a coded token naming the row, never prose.</summary>
internal readonly record struct ShimEnsureDecision(ShimEnsureAction Action, string? Reason = null);

/// <summary>
/// Pure ladder classifier for the PATH shim (spec §5): only a POSITIVE probe finding no
/// <c>kcap</c> on the terminal PATH, on macOS, is installable. A positive "already on PATH" is
/// terminal on any platform; off-macOS there is no shim to install (the osascript-based writer is
/// macOS-only), so the platform refusal beats an unknown probe — the flow expects non-macOS to be
/// a stable <c>unsupported_platform</c> row, not a probe-dependent one.
/// </summary>
internal static class ShimEnsureClassifier {
    public static ShimEnsureDecision Classify(bool? onPath, bool isMacOs) {
        if (onPath == true)  return new ShimEnsureDecision(ShimEnsureAction.AlreadyOnPath);
        if (!isMacOs)        return new ShimEnsureDecision(ShimEnsureAction.Refuse, FirstRunMachineActionReasons.UnsupportedPlatform);
        if (onPath is null)  return new ShimEnsureDecision(ShimEnsureAction.Refuse, FirstRunMachineActionReasons.ProbeUnknown);
        return new ShimEnsureDecision(ShimEnsureAction.Install);
    }
}

/// <summary>
/// <c>kcap daemon shim ensure</c> — the flow's named PATH-fix capability. The CLI composes the
/// whole operation itself: it resolves its own binary path (never a server-supplied path), probes
/// the login shell, and on a positive absence installs the shim via <see cref="PathShimInstaller"/>
/// (osascript admin prompt, non-forcing symlink, post-install re-probe). Machine-readable result
/// via <c>--json</c>; human output otherwise. Exit 0 only when the terminal now resolves
/// <c>kcap</c>; every refusal and every not-on-path outcome exits non-zero with a coded token.
/// </summary>
public static class DaemonShimCommands {
    public const string Capability = FirstRunMachineCapabilities.PathShim;

    public static async Task<int> DispatchAsync(string[] args) {
        if (args.Length == 0) return Usage();

        return args[0] switch {
            "ensure" => await Ensure(args[1..]),
            _        => Usage(),
        };
    }

    /// <param name="resolveTarget">Test seam: the shim link target. Production resolves via
    /// <see cref="ResolveLinkTarget"/> — the npm launcher when this CLI is part of an npm-global
    /// install, else the running binary. The server never supplies one.</param>
    /// <param name="probe">Test seam for the login-shell probe (the production probe spawns the
    /// real <c>$SHELL</c>).</param>
    /// <param name="install">Test seam for the shim install (the production installer prompts via
    /// osascript).</param>
    /// <param name="preflight">Test seam for the destination preflight — the production default
    /// refuses on any non-target filesystem entry at <see cref="PathShimInstaller.Destination"/>.
    /// Injected independently of <paramref name="install"/> so the conflict row is stubbable.</param>
    /// <param name="isMacOs">Test seam — the shim is osascript-based, so the classifier refuses
    /// off-macOS. A bool cannot distinguish "unspecified" from "explicitly false" on a macOS host,
    /// so this is nullable: null resolves to the real OS, false forces the off-macOS arm.</param>
    internal static async Task<int> Ensure(string[] args, Func<string?>? resolveTarget = null,
            ILoginShellProbe? probe = null, Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool? isMacOs = null) {
        // Only --json is a legal flag; anything else (including --help and typos) is rejected
        // before the first probe or prompt — this verb's own ladder principle is fail-closed, and
        // an unknown flag must not be silently ignored into a mutation. (-h/--help never reach
        // here: Program.cs intercepts them for the whole command group.)
        if (args.Any(a => a != "--json"))
            return Usage();

        var result = await EvaluateAsync(resolveTarget, probe, install, preflight, isMacOs, CancellationToken.None);

        return await Report(result, args.Contains("--json"));
    }

    /// <summary>
    /// The ladder, with no console output and no exit code — what the browser flow drives through
    /// <c>IFirstRunMachineActions</c>.
    ///
    /// <para><b>The one place the operation is composed.</b> Both callers reach the machine through here,
    /// so the flow cannot end up running a different ladder from the one the verb documents, and the
    /// outcome the browser renders is the outcome the terminal would have printed.</para>
    /// </summary>
    internal static async Task<ShimEnsureJson> EvaluateAsync(
            Func<string?>? resolveTarget = null, ILoginShellProbe? probe = null,
            Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool? isMacOs = null,
            CancellationToken ct = default) {
        isMacOs ??= OperatingSystem.IsMacOS();

        var target = (resolveTarget ?? (() => ResolveLinkTarget(() => Environment.ProcessPath, File.Exists)))();

        if (string.IsNullOrEmpty(target))
            return new ShimEnsureJson(Capability, null, null, null, "none",
                FirstRunMachineActionOutcomes.Refused, FirstRunMachineActionReasons.NoCliPath);

        probe ??= new LoginShellProbe(new ProcessRunner(), Environment.GetEnvironmentVariable);
        var onPath = await probe.KcapOnPathAsync(ct).ConfigureAwait(false);

        var decision = ShimEnsureClassifier.Classify(onPath, isMacOs.Value);

        return decision.Action switch {
            ShimEnsureAction.AlreadyOnPath =>
                new ShimEnsureJson(Capability, target, onPath != null, onPath, "none",
                    FirstRunMachineActionOutcomes.AlreadyOnPath),

            ShimEnsureAction.Refuse =>
                new ShimEnsureJson(Capability, target, onPath != null, onPath, "none",
                    FirstRunMachineActionOutcomes.Refused, decision.Reason),

            _ => await Install(target, probe, install, preflight, ct),
        };
    }

    /// <summary>
    /// The shim's link target: the npm launcher when this CLI is part of an npm-global install,
    /// else the running binary itself.
    ///
    /// <para>In the npm topology the process running this code is the platform NativeAOT binary
    /// that <c>npm/kcap/bin/kcap.js</c> spawned, so <see cref="Environment.ProcessPath"/> resolves
    /// to that binary — but linking <c>/usr/local/bin/kcap</c> to it would bypass the launcher for
    /// every subsequent command. <c>kcap.js</c> is the component that intercepts <c>kcap update</c>
    /// and runs npm; the native binary's own update path only prints "Run kcap update" when invoked
    /// directly. The launcher is a sibling package (<c>@kurrent/kcap</c> vs the platform package
    /// <c>@kurrent/kcap-&lt;platform&gt;</c>), so its path is derivable from the running binary's
    /// own location with no environment lookup. When the derived launcher is absent (dev build,
    /// standalone binary), the running image is the target.</para>
    /// </summary>
    internal static string? ResolveLinkTarget(Func<string?> processPath, Func<string, bool> fileExists) {
        var native = processPath();
        if (string.IsNullOrEmpty(native)) return null;

        // .../node_modules/@kurrent/kcap-<platform>/bin/kcap  →  launcher:
        // .../node_modules/@kurrent/kcap/bin/kcap.js
        // From the platform package's bin dir: up 3 (bin → <platform> → @kurrent → node_modules),
        // then the wrapper package's launcher.
        var launcher = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(native) ?? string.Empty,
            "..", "..", "..", "@kurrent", "kcap", "bin", "kcap.js"));
        return fileExists(launcher) ? launcher : native;
    }

    static async Task<ShimEnsureJson> Install(string target, ILoginShellProbe probe,
            Func<string, CancellationToken, Task<ShimResult>>? install, Func<string, ShimPreflight>? preflight,
            CancellationToken ct) {
        // Preflight is checked here (before the admin prompt) so a conflict is a coded refusal
        // with what was found, not a prompt that then fails. AlreadyInstalled and Installable
        // both flow into InstallAsync, which re-probes fresh and maps the same way. The preflight
        // seam is independent of the install seam so the conflict row is stubbable in tests.
        preflight ??= target => PathShimInstaller.Preflight(PathShimInstaller.Destination, target);
        if (preflight(target) == ShimPreflight.Conflict)
            return ConflictRefusal(target);

        var result = install is not null
            ? await install(target, ct).ConfigureAwait(false)
            : await new PathShimInstaller(new ProcessRunner(), probe)
                .InstallAsync(target, ct).ConfigureAwait(false);

        // The outer preflight and the installer's own checks are not atomic: an entry can appear
        // between them, or the non-forcing `ln -s` can lose the race against one. Re-preflight
        // now — a foreign entry present after a failed install is the coded conflict row the flow
        // was promised, not a generic failure.
        if (result.Outcome == ShimOutcome.Failed && preflight(target) == ShimPreflight.Conflict)
            return ConflictRefusal(target);

        return result.Outcome switch {
            ShimOutcome.Installed =>
                new ShimEnsureJson(Capability, target, true, true, "install",
                    FirstRunMachineActionOutcomes.Installed),

            ShimOutcome.InstalledButNotOnPath =>
                new ShimEnsureJson(Capability, target, true, false, "install",
                    FirstRunMachineActionOutcomes.InstalledNotOnPath, Detail: result.Detail),

            ShimOutcome.Cancelled =>
                new ShimEnsureJson(Capability, target, true, false, "install",
                    FirstRunMachineActionOutcomes.Cancelled),

            _ =>
                new ShimEnsureJson(Capability, target, true, null, "install",
                    FirstRunMachineActionOutcomes.Failed,
                    Detail: result.Detail, SudoFallback: result.SudoFallback),
        };
    }

    static ShimEnsureJson ConflictRefusal(string target) {
        var conflict = $"a different filesystem entry already exists at {PathShimInstaller.Destination} and was left untouched";

        return new ShimEnsureJson(Capability, target, true, false, "install",
            FirstRunMachineActionOutcomes.Refused, FirstRunMachineActionReasons.Conflict, conflict);
    }

    static async Task<int> Report(ShimEnsureJson dto, bool json) {
        var exit = dto.ExitCode;

        if (json) {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(dto, ShimJsonContext.Default.ShimEnsureJson));
            return exit;
        }

        switch (dto.Outcome) {
            case FirstRunMachineActionOutcomes.AlreadyOnPath:
                await Console.Out.WriteLineAsync("kcap is already on your terminal PATH.");
                return 0;
            case FirstRunMachineActionOutcomes.Installed:
                // Not "Linked … to …" — the AlreadyInstalled preflight row reaches here without
                // creating anything, so claiming a fresh link would be a lie.
                await Console.Out.WriteLineAsync("kcap is now on your terminal PATH.");
                return 0;
            case FirstRunMachineActionOutcomes.InstalledNotOnPath:
                await Console.Out.WriteLineAsync(Sanitize(dto.Detail));
                return 1;
            case FirstRunMachineActionOutcomes.Cancelled:
                await Console.Out.WriteLineAsync("Installing the command-line tool was canceled.");
                return 1;
            case FirstRunMachineActionOutcomes.Failed when dto.SudoFallback is not null:
                await Console.Error.WriteLineAsync($"{Sanitize(dto.Detail)} Or run: {Sanitize(dto.SudoFallback)}");
                return 1;
            case FirstRunMachineActionOutcomes.Failed:
                await Console.Error.WriteLineAsync(Sanitize(dto.Detail) ?? "Installing the command-line tool failed.");
                return 1;
            default: // refused
                await Console.Error.WriteLineAsync(dto.Reason switch {
                    FirstRunMachineActionReasons.NoCliPath => "Could not resolve this CLI's own path.",
                    FirstRunMachineActionReasons.ProbeUnknown =>
                        "Could not determine whether kcap is on your terminal PATH.",
                    FirstRunMachineActionReasons.UnsupportedPlatform =>
                        "The command-line tool install is only available on macOS.",
                    FirstRunMachineActionReasons.Conflict => Sanitize(dto.Detail),
                    _                                    => "Path fix refused.",
                });
                return 1;
        }
    }

    /// Human output is terminal-bound; a macOS path or shell error text may legally carry ESC /
    /// other control bytes, which could inject ANSI sequences into the operator's terminal. The
    /// flow consumes the JSON arm (already escaped by System.Text.Json), so this only guards the
    /// console lines. Newlines/tabs are preserved — the Detail's own line breaks are intended.
    static string? Sanitize(string? text) {
        if (text is null) return null;
        return new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap daemon shim <ensure> [--json]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  ensure [--json]   Probe the login shell; if kcap is absent from the");
        Console.Error.WriteLine("                    terminal PATH, link /usr/local/bin/kcap to this CLI");
        Console.Error.WriteLine("                    (macOS; prompts once for your admin password).");
        return 1;
    }
}
