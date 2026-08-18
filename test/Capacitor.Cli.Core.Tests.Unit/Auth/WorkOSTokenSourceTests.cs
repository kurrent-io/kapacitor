using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// A provisioning poll can run up to ~10 minutes, but a WorkOS AuthKit access token lives
// only ~5 minutes. WorkOSTokenSource keeps a valid access token across that window by
// refreshing (public-client refresh_token grant) before the current token expires.
public class WorkOSTokenSourceTests {
    static string JwtWithExp(DateTimeOffset exp) {
        var json = $"{{\"exp\":{exp.ToUnixTimeSeconds()}}}";
        var b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{b64}.signature";
    }

    [Test]
    public async Task GetAsync_refreshes_when_within_expiry_margin_and_returns_new_token() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(30)); // inside the 60s margin
        var freshTok = JwtWithExp(now.AddMinutes(5));
        var calls    = 0;

        var src = new WorkOSTokenSource(expiring, "rt1",
            refresh: (_, _) => { calls++; return Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { AccessToken = freshTok, RefreshToken = "rt2" }); },
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var token = await src.GetAsync(CancellationToken.None);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(token).IsEqualTo(freshTok);
    }

    [Test]
    public async Task GetAsync_does_not_refresh_when_token_still_valid() {
        var now   = DateTimeOffset.UnixEpoch.AddDays(1);
        var valid = JwtWithExp(now.AddMinutes(4)); // well outside the 60s margin
        var calls = 0;

        var src = new WorkOSTokenSource(valid, "rt1",
            refresh: (_, _) => { calls++; return Task.FromResult<WorkOSAuthResponse?>(null); },
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var token = await src.GetAsync(CancellationToken.None);

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(token).IsEqualTo(valid);
    }

    [Test]
    public async Task GetAsync_returns_existing_token_when_refresh_fails() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(10));

        var src = new WorkOSTokenSource(expiring, "rt1",
            refresh: (_, _) => Task.FromResult<WorkOSAuthResponse?>(null), // refresh failed
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var token = await src.GetAsync(CancellationToken.None);

        await Assert.That(token).IsEqualTo(expiring);
    }

    [Test]
    public async Task GetAsync_rotates_the_refresh_token_across_successive_refreshes() {
        var now  = DateTimeOffset.UnixEpoch.AddDays(1);
        var t0   = JwtWithExp(now.AddSeconds(10));
        var toks = new[] { JwtWithExp(now.AddSeconds(10)), JwtWithExp(now.AddMinutes(5)) };
        var rts  = new[] { "rt2", "rt3" };
        var seen = new List<string>();
        var idx  = 0;

        var src = new WorkOSTokenSource(t0, "rt1",
            refresh: (rt, _) => {
                seen.Add(rt);
                var r = new WorkOSAuthResponse { AccessToken = toks[idx], RefreshToken = rts[idx] };
                idx++;

                return Task.FromResult<WorkOSAuthResponse?>(r);
            },
            now: () => now, margin: TimeSpan.FromSeconds(60));

        await src.GetAsync(CancellationToken.None); // rt1 -> rt2 (token still near expiry)
        await src.GetAsync(CancellationToken.None); // rt2 -> rt3

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0]).IsEqualTo("rt1");
        await Assert.That(seen[1]).IsEqualTo("rt2");
    }

    [Test]
    public async Task CurrentRefreshToken_reflects_the_rotated_token_after_a_refresh() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(10));
        var fresh    = JwtWithExp(now.AddMinutes(5));

        var src = new WorkOSTokenSource(expiring, "rt1",
            refresh: (_, _) => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { AccessToken = fresh, RefreshToken = "rt2" }),
            now: () => now, margin: TimeSpan.FromSeconds(60));

        // WorkOS rotates refresh tokens (single-use), so callers that later re-use the refresh
        // token (the final org-switch) must read the rotated value, not the login-time one.
        await Assert.That(src.CurrentRefreshToken).IsEqualTo("rt1");
        await src.GetAsync(CancellationToken.None);
        await Assert.That(src.CurrentRefreshToken).IsEqualTo("rt2");
    }

    [Test]
    public async Task GetAsync_degrades_to_existing_token_when_refresh_throws() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(10));

        // A transient network/JSON failure inside the refresh must not abort the poll — the caller's
        // next status call still carries this token (and surfaces the eventual 401) instead of crashing.
        var src = new WorkOSTokenSource(expiring, "rt1",
            refresh: (_, _) => Task.FromException<WorkOSAuthResponse?>(new HttpRequestException("network down")),
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var token = await src.GetAsync(CancellationToken.None);

        await Assert.That(token).IsEqualTo(expiring);
    }

    [Test]
    public async Task GetAsync_propagates_genuine_cancellation_rather_than_swallowing_it() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var src = new WorkOSTokenSource(expiring, "rt1",
            refresh: (_, ct) => Task.FromException<WorkOSAuthResponse?>(new OperationCanceledException(ct)),
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var cancelled = false;
        try { await src.GetAsync(cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }

        await Assert.That(cancelled).IsTrue();
    }

    [Test]
    public async Task GetAsync_without_refresh_token_returns_current_and_never_refreshes() {
        var now      = DateTimeOffset.UnixEpoch.AddDays(1);
        var expiring = JwtWithExp(now.AddSeconds(1));
        var calls    = 0;

        var src = new WorkOSTokenSource(expiring, refreshToken: null,
            refresh: (_, _) => { calls++; return Task.FromResult<WorkOSAuthResponse?>(null); },
            now: () => now, margin: TimeSpan.FromSeconds(60));

        var token = await src.GetAsync(CancellationToken.None);

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(token).IsEqualTo(expiring);
    }
}
