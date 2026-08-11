using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The MCP servers used to answer EVERY post-recovery 401 with
/// "Not logged in. Run 'kcap login' on the host shell." — including the incident shape where
/// the stored token was re-read from disk, re-sent, and rejected by a server whose signing
/// key had rotated. The user then saw `kcap status` report a valid login while the tools
/// insisted they were logged out, concluded `kcap login` was ineffective, and restarted the
/// daemon (which never helps — a fresh process reads the same store). These tests pin the
/// truthful classification: the legacy message survives byte-identical for a genuinely
/// missing login, and the other states say what is actually wrong.
/// </summary>
public class AuthRejectionNoticeTests {
    const string Target = "https://kurrent.kcap.ai";

    static StoredTokens Tokens(
            DateTimeOffset expiresAt, string? serverUrl = "https://kurrent.kcap.ai:443", string user = "octocat") =>
        new() {
            AccessToken    = "token-under-test",
            ExpiresAt      = expiresAt,
            GitHubUsername = user,
            Provider       = "GitHubApp",
            ServerUrl      = serverUrl,
        };

    [Test]
    public async Task Missing_store_keeps_the_legacy_message_byte_identical() {
        var state = AuthRejectionNotice.Classify(null, Target);

        await Assert.That(state).IsEqualTo(StoredCredentialState.Missing);
        await Assert.That(AuthRejectionNotice.Render(state, null, Target))
            .IsEqualTo("Not logged in. Run 'kcap login' on the host shell.");
    }

    [Test]
    public async Task Locally_valid_token_is_reported_as_a_server_rejection_not_a_missing_login() {
        var tokens = Tokens(DateTimeOffset.UtcNow.AddHours(23));
        var state  = AuthRejectionNotice.Classify(tokens, Target);

        await Assert.That(state).IsEqualTo(StoredCredentialState.LooksValid);

        var text = AuthRejectionNotice.Render(state, tokens, Target);

        // The incident's wild-goose chase, point by point: the user IS logged in — never say
        // otherwise; login IS the remedy (a fresh credential under the server's current key);
        // a daemon restart is NOT (every process re-reads the same store).
        await Assert.That(text).DoesNotContain("Not logged in");
        await Assert.That(text).Contains("octocat");
        await Assert.That(text).Contains("kcap login");
        await Assert.That(text).Contains("restarting the daemon will not help");
        await Assert.That(text).Contains("HTTP 401");
    }

    [Test]
    public async Task Port_only_url_difference_still_counts_as_the_same_server() {
        // Stored tokens are stamped with the canonicalized ":443" form while callers pass the
        // configured URL without it — that pair must classify as LooksValid, never WrongServer.
        var tokens = Tokens(DateTimeOffset.UtcNow.AddHours(2), serverUrl: "https://kurrent.kcap.ai:443");

        await Assert.That(AuthRejectionNotice.Classify(tokens, "https://kurrent.kcap.ai"))
            .IsEqualTo(StoredCredentialState.LooksValid);
    }

    [Test]
    public async Task Unbound_legacy_token_classifies_by_expiry_alone() {
        var tokens = Tokens(DateTimeOffset.UtcNow.AddHours(2), serverUrl: null);

        await Assert.That(AuthRejectionNotice.Classify(tokens, Target))
            .IsEqualTo(StoredCredentialState.LooksValid);
    }

    [Test]
    public async Task Expired_token_says_expired_and_points_at_login() {
        var tokens = Tokens(DateTimeOffset.UtcNow.AddHours(-1));
        var state  = AuthRejectionNotice.Classify(tokens, Target);

        await Assert.That(state).IsEqualTo(StoredCredentialState.Expired);

        var text = AuthRejectionNotice.Render(state, tokens, Target);

        await Assert.That(text).Contains("expired");
        await Assert.That(text).Contains("kcap login");
    }

    [Test]
    public async Task Wrong_server_token_names_both_servers() {
        var tokens = Tokens(DateTimeOffset.UtcNow.AddHours(2), serverUrl: "https://other.kcap.ai");
        var state  = AuthRejectionNotice.Classify(tokens, Target);

        await Assert.That(state).IsEqualTo(StoredCredentialState.WrongServer);

        var text = AuthRejectionNotice.Render(state, tokens, Target);

        await Assert.That(text).Contains("https://other.kcap.ai");
        await Assert.That(text).Contains(Target);
        await Assert.That(text).Contains("kcap use");
    }

    [Test]
    public async Task Every_state_renders_a_login_remedy() {
        // Whatever the diagnosis, the reader must leave with an action; 'kcap login' is the
        // one remedy that exists on every path (WrongServer additionally offers 'kcap use').
        foreach (var (tokens, target) in new (StoredTokens?, string)[] {
                     (null, Target),
                     (Tokens(DateTimeOffset.UtcNow.AddHours(-1)), Target),
                     (Tokens(DateTimeOffset.UtcNow.AddHours(2)), Target),
                     (Tokens(DateTimeOffset.UtcNow.AddHours(2), serverUrl: "https://other.kcap.ai"), Target),
                 }) {
            var text = AuthRejectionNotice.Render(AuthRejectionNotice.Classify(tokens, target), tokens, target);

            await Assert.That(text).Contains("kcap login");
        }
    }

    // ── Recording-hook rendering of the same states ─────────────────────────────────────────
    // The hooks need one line, not a paragraph: a Claude systemMessage is a single transcript
    // warning and the other vendors get a single stderr line. These lock that wording, which the
    // Claude-hook, poster and Cursor-hook tests assert against.

    [Test]
    public async Task Recording_notice_renders_one_line_per_state() {
        await Assert.That(AuthRejectionNotice.RecordingNotice(StoredCredentialState.Expired)).IsEqualTo(
            "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.");
        await Assert.That(AuthRejectionNotice.RecordingNotice(StoredCredentialState.Missing)).IsEqualTo(
            "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.");
        await Assert.That(AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid)).IsEqualTo(
            "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.");
    }

    /// <summary>Pre-existing hook behaviour, preserved by the fold: the short form does not name
    /// both servers the way <see cref="AuthRejectionNotice.Render"/> does.</summary>
    [Test]
    public async Task Recording_notice_renders_wrong_server_as_the_not_authenticated_line() {
        await Assert.That(AuthRejectionNotice.RecordingNotice(StoredCredentialState.WrongServer)).IsEqualTo(
            AuthRejectionNotice.RecordingNotice(StoredCredentialState.Missing));
    }

    [Test]
    public async Task Every_recording_notice_names_the_recovery_command() {
        foreach (var state in Enum.GetValues<StoredCredentialState>()) {
            await Assert.That(AuthRejectionNotice.RecordingNotice(state)).Contains("kcap login");
        }
    }

    /// <summary>The hook path holds an <see cref="AuthStatus"/> and must not read the store just
    /// to name a state it already knows.</summary>
    [Test]
    public async Task Auth_status_maps_onto_the_stored_credential_states() {
        await Assert.That(AuthRejectionNotice.FromAuthStatus(AuthStatus.Expired)).IsEqualTo(StoredCredentialState.Expired);
        await Assert.That(AuthRejectionNotice.FromAuthStatus(AuthStatus.NotAuthenticated)).IsEqualTo(StoredCredentialState.Missing);
        await Assert.That(AuthRejectionNotice.FromAuthStatus(AuthStatus.WrongServer)).IsEqualTo(StoredCredentialState.WrongServer);
        // A 401 answering a usable client is exactly the "looks valid locally" case.
        await Assert.That(AuthRejectionNotice.FromAuthStatus(AuthStatus.Ok)).IsEqualTo(StoredCredentialState.LooksValid);
        await Assert.That(AuthRejectionNotice.FromAuthStatus(AuthStatus.NoAuthRequired)).IsEqualTo(StoredCredentialState.LooksValid);
    }

    [Test]
    public async Task Vendor_stderr_line_names_the_remedy_on_401_and_stays_bare_otherwise() {
        await Assert.That(AuthRejectionNotice.VendorStderrLine("codex-hook", "stop", 401)).IsEqualTo(
            "[kcap] codex-hook stop: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording");
        await Assert.That(AuthRejectionNotice.VendorStderrLine("codex-hook", "stop", 500)).IsEqualTo(
            "[kcap] codex-hook stop: HTTP 500");
    }
}
