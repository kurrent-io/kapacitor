using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The decision <c>kcap daemon shim ensure</c> makes from a fresh login-shell probe: whether the
/// terminal already resolves <c>kcap</c> (the flow's done state), whether the shim should be
/// installed, or whether to fail closed with a coded reason. Pure — no I/O — so the ladder is
/// directly testable. Every ambiguous row fails closed, never guessed.
/// </summary>
public enum ShimEnsureAction {
    /// <summary>kcap already resolves from the terminal — nothing to do.</summary>
    AlreadyOnPath,

    /// <summary>Probe positively found no kcap; macOS shim install is the fix.</summary>
    Install,

    /// <summary>Nothing was mutated; <see cref="ShimEnsureDecision.Reason"/> names the row.</summary>
    Refuse,
}

/// <summary>One ladder classification. <see cref="Reason"/> is non-null only for
/// <see cref="ShimEnsureAction.Refuse"/> — a coded token naming the row, never prose.</summary>
public readonly record struct ShimEnsureDecision(ShimEnsureAction Action, string? Reason = null);

/// <summary>
/// Pure ladder classifier for the PATH shim (spec §5): only a POSITIVE probe finding no
/// <c>kcap</c> on the terminal PATH, on macOS, is installable. An unknown probe and a positive
/// "already on PATH" are both terminal rows; off-macOS there is no shim to install (the
/// osascript-based writer is macOS-only), so the flow reflects plain "show me the line" there.
/// </summary>
internal static class ShimEnsureClassifier {
    public static ShimEnsureDecision Classify(bool? onPath, bool isMacOs) {
        if (onPath == true)  return new ShimEnsureDecision(ShimEnsureAction.AlreadyOnPath);
        if (onPath is null)  return new ShimEnsureDecision(ShimEnsureAction.Refuse, "probe_unknown");
        if (!isMacOs)        return new ShimEnsureDecision(ShimEnsureAction.Refuse, "unsupported_platform");
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
    public const string Capability = "path_shim";

    public static async Task<int> DispatchAsync(string[] args) {
        if (args.Length == 0) return Usage();

        return args[0] switch {
            "ensure" => await Ensure(args[1..]),
            _        => Usage(),
        };
    }

    /// <param name="resolveTarget">Test seam: the running CLI's own path. Production resolves
    /// <c>Environment.ProcessPath</c> — the CLI knows where it is; the server never supplies one.</param>
    /// <param name="probe">Test seam for the login-shell probe (the production probe spawns the
    /// real <c>$SHELL</c>).</param>
    /// <param name="install">Test seam for the shim install (the production installer prompts via
    /// osascript).</param>
    /// <param name="isMacOs">Test seam — the shim is osascript-based, so the classifier refuses
    /// off-macOS; production resolves the real OS.</param>
    internal static async Task<int> Ensure(string[] args, Func<string?>? resolveTarget = null,
            ILoginShellProbe? probe = null, Func<string, CancellationToken, Task<ShimResult>>? install = null,
            bool isMacOs = default) {
        var json = args.Contains("--json");
        isMacOs |= OperatingSystem.IsMacOS();

        var target = (resolveTarget ?? (() => Environment.ProcessPath))();
        if (string.IsNullOrEmpty(target)) {
            return await Report(new ShimEnsureJson(Capability, null, null, null, "none", "refused", "no_cli_path"), 1, json);
        }

        probe ??= new LoginShellProbe(new ProcessRunner(), Environment.GetEnvironmentVariable);
        var onPath = await probe.KcapOnPathAsync(CancellationToken.None).ConfigureAwait(false);

        var decision = ShimEnsureClassifier.Classify(onPath, isMacOs);
        return decision.Action switch {
            ShimEnsureAction.AlreadyOnPath =>
                await Report(new ShimEnsureJson(Capability, target, onPath != null, onPath, "none", "already_on_path"), 0, json),

            ShimEnsureAction.Refuse =>
                await Report(new ShimEnsureJson(Capability, target, onPath != null, onPath, "none", "refused", decision.Reason), 1, json),

            _ => await Install(target, json, install),
        };
    }

    static async Task<int> Install(string target, bool json, Func<string, CancellationToken, Task<ShimResult>>? install) {
        // Preflight is checked here (before the admin prompt) so a conflict is a coded refusal
        // with what was found, not a prompt that then fails. AlreadyInstalled and Installable
        // both flow into InstallAsync, which re-probes fresh and maps the same way.
        if (install is null
                && PathShimInstaller.Preflight(PathShimInstaller.Destination, target) == ShimPreflight.Conflict) {
            var conflict = $"a different filesystem entry already exists at {PathShimInstaller.Destination} and was left untouched";
            return await Report(new ShimEnsureJson(Capability, target, true, false, "install", "refused", "conflict", conflict), 1, json);
        }

        var result = install is not null
            ? await install(target, CancellationToken.None).ConfigureAwait(false)
            : await new PathShimInstaller(new ProcessRunner(),
                    new LoginShellProbe(new ProcessRunner(), Environment.GetEnvironmentVariable))
                .InstallAsync(target, CancellationToken.None).ConfigureAwait(false);
        return result.Outcome switch {
            ShimOutcome.Installed =>
                await Report(new ShimEnsureJson(Capability, target, true, true, "install", "installed"), 0, json),

            ShimOutcome.InstalledButNotOnPath =>
                await Report(new ShimEnsureJson(Capability, target, true, false, "install", "installed_not_on_path",
                    Detail: result.Detail), 1, json),

            ShimOutcome.Cancelled =>
                await Report(new ShimEnsureJson(Capability, target, true, false, "install", "cancelled"), 1, json),

            _ =>
                await Report(new ShimEnsureJson(Capability, target, true, false, "install", "failed",
                    Detail: result.Detail, SudoFallback: result.SudoFallback), 1, json),
        };
    }

    static async Task<int> Report(ShimEnsureJson dto, int exit, bool json) {
        if (json) {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(dto, ShimJsonContext.Default.ShimEnsureJson));
            return exit;
        }

        switch (dto.Outcome) {
            case "already_on_path":
                await Console.Out.WriteLineAsync("kcap is already on your terminal PATH.");
                return 0;
            case "installed":
                await Console.Out.WriteLineAsync($"Linked {PathShimInstaller.Destination} to {dto.Target}; kcap is now on your terminal PATH.");
                return 0;
            case "installed_not_on_path":
                await Console.Out.WriteLineAsync(dto.Detail);
                return 1;
            case "cancelled":
                await Console.Out.WriteLineAsync("Installing the command-line tool was canceled.");
                return 1;
            case "failed" when dto.SudoFallback is not null:
                await Console.Error.WriteLineAsync($"{dto.Detail} Or run: {dto.SudoFallback}");
                return 1;
            case "failed":
                await Console.Error.WriteLineAsync(dto.Detail ?? "Installing the command-line tool failed.");
                return 1;
            default: // refused
                await Console.Error.WriteLineAsync(dto.Reason switch {
                    "no_cli_path"          => "Could not resolve this CLI's own path.",
                    "probe_unknown"        => "Could not determine whether kcap is on your terminal PATH.",
                    "unsupported_platform" => "The command-line tool install is only available on macOS.",
                    "conflict"             => dto.Detail,
                    _                      => "Path fix refused.",
                });
                return 1;
        }
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
