using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Moves the workspace pick to a page on the auth proxy, falling back to
/// <see cref="SpectreTenantPicker"/> whenever it cannot.
/// </summary>
/// <remarks>
/// The CLI stays the authenticated party: it prepares the pick with its own bearer and collects the
/// answer with a secret it generated, so nothing that travels through the browser is enough to read
/// the result. Every failure lands on the terminal picker — a degraded pick still picks, whereas a
/// failed one ends the whole of <c>kcap setup</c>.
/// </remarks>
/// <param name="canPrompt">
/// False under <c>--no-prompt</c> or without a terminal. A browser pick is still a prompt: it stops
/// an unattended run just as dead as a terminal one, so it is refused for the same reason.
/// </param>
public sealed class BrowserTenantPicker(
        IBrowserLauncher    launcher,
        SpectreTenantPicker fallback,
        IAuthProgress?      progress   = null,
        IKeyWatcher?        keys       = null,
        TimeProvider?       time       = null,
        Func<bool>?         canPrompt  = null
    ) : ITenantPicker {

    IAuthProgress Progress => progress ?? ConsoleAuthProgress.Instance;
    IKeyWatcher   Keys     => keys     ?? ConsoleKeyWatcher.Instance;
    TimeProvider  Clock    => time     ?? TimeProvider.System;

    public DiscoveredTenant? Pick(DiscoveredTenant[] tenants) => fallback.Pick(tenants);

    public async Task<DiscoveredTenant?> PickAsync(
            DiscoveredTenant[] tenants, TenantPickContext context, CancellationToken ct) {
        // GitHub rows go to the terminal: the proxy resolves this pick from a WorkOS bearer, and a
        // GitHub-lane caller has none. Same for a login that never reached a browser.
        if ((canPrompt is not null && !canPrompt()) || !context.CanPickInBrowser || !tenants.All(t => t.IsWorkOS)) {
            return await fallback.PickAsync(tenants, context, ct);
        }

        var secret   = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var prepared = await context.Proxy!.PreparePickAsync(
            context.ProxyUrl!, context.Bearer!,
            Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), ct);

        if (prepared is null || prepared.Tenants.Length == 0) {
            return await FallBackAsync(tenants, context, ct);
        }

        var url = $"{context.ProxyUrl}/cli/v1/picker/{prepared.Handle}";

        // Printed as well as opened: launching cannot confirm a browser appeared, and a user whose
        // browser never came up should not discover the way out ten minutes later.
        Progress.Notice("Pick a workspace in your browser, or press a key to choose here.");
        Progress.Notice(url);
        launcher.TryOpen(url);

        var key = await AwaitChoiceAsync(context, prepared, secret, ct);

        if (key is null) return await FallBackAsync(tenants, context, ct);

        // Resolved against discovery's array, never the prepared one. The two can differ — a
        // membership changes, or the proxy skips an org on a hostname collision — and the rest of
        // the flow creates profiles from discovery's list alone, so a key that maps only onto
        // prepare's would name a profile that was never created and dead-end the setup.
        var picked = tenants.FirstOrDefault(t => string.Equals(t.OrganizationId, key, StringComparison.Ordinal));

        return picked ?? await FallBackAsync(tenants, context, ct);
    }

    /// <summary>
    /// Hands over to the terminal, unless the caller has given up on the flow entirely.
    /// </summary>
    /// <remarks>
    /// The check is the whole point: <see cref="SpectreTenantPicker.PickAsync"/> does not read its
    /// token — a Spectre prompt is not cancellable — so falling through on a cancelled token opens a
    /// live prompt for a run the user has already abandoned.
    /// </remarks>
    async Task<DiscoveredTenant?> FallBackAsync(
            DiscoveredTenant[] tenants, TenantPickContext context, CancellationToken ct) =>
        ct.IsCancellationRequested ? null : await fallback.PickAsync(tenants, context, ct);

    /// <summary>The chosen key, or null — a keypress, the deadline and an expired handle are one
    /// answer as far as the caller is concerned.</summary>
    async Task<string?> AwaitChoiceAsync(
            TenantPickContext context, CliPickerPrepareResponse prepared, string secret, CancellationToken ct) {
        var interval = TimeSpan.FromSeconds(Math.Clamp(prepared.PollIntervalSeconds, 1, 10));
        var waited   = false;

        try {
            while (!ct.IsCancellationRequested && Clock.GetUtcNow() < prepared.ExpiresAt) {
                waited = true;

                // Checked every tick rather than waited on: Console.ReadLine is not cancellable, so
                // a blocking read on another thread cannot lose this race cleanly. It also has to be
                // checked before the poll, or a slow one holds the keypress for its whole timeout.
                if (Keys.CanWatch && Keys.KeyAvailable) {
                    // Drain, not ReadKey: the escape key is usually followed by a Return, and the
                    // Spectre prompt about to run would read that as its answer.
                    Keys.Drain();
                    await Abandon(context, prepared);

                    return null;
                }

                if (Answer(await context.Proxy!.PollPickAsync(context.ProxyUrl!, prepared.Handle, secret, ct))
                    is { } chosen) {
                    return chosen.Length == 0 ? null : chosen;
                }

                await Task.Delay(interval, Clock, ct);
            }

            // One last look after the deadline. A choice posted inside the final interval is already
            // stored and the page has told the user it was saved, so exiting without collecting it
            // is the browser-says-yes-terminal-asks-again contradiction the shared deadline exists
            // to prevent. Only when we actually waited: a handle already dead on arrival cannot be
            // carrying a choice made through the browser this call just opened.
            if (waited && !ct.IsCancellationRequested &&
                Answer(await context.Proxy!.PollPickAsync(context.ProxyUrl!, prepared.Handle, secret, ct))
                    is { Length: > 0 } late) {
                return late;
            }
        } catch (OperationCanceledException) {
            // The caller is abandoning the whole flow; FallBackAsync sees the same token and stops.
            return null;
        }

        await Abandon(context, prepared);

        return null;
    }

    /// <summary>
    /// The poll's verdict: the chosen key, an empty string for a terminal non-answer, or null to
    /// keep waiting. Anything unrecognised is treated as pending, so a newer proxy cannot strand us.
    /// </summary>
    string? Answer(CliPickerResultResponse? result) {
        switch (result?.Status) {
            case "selected" when !string.IsNullOrEmpty(result.Key):
                return result.Key;

            case "expired":
                Progress.Notice("That link expired before a workspace was picked.");
                return "";

            default:
                return null;
        }
    }

    /// <summary>
    /// Releases the handle so the page stops offering a choice nobody will collect. Best effort, and
    /// never on the caller's token — it is usually already cancelled by the time this runs.
    /// </summary>
    static Task Abandon(TenantPickContext context, CliPickerPrepareResponse prepared) =>
        context.Proxy!.AbandonPickAsync(context.ProxyUrl!, prepared.Handle, CancellationToken.None);
}
