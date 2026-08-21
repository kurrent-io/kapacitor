using System.Net;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// whoami's whole value is that it distinguishes "the server rejected your token" from "I could
/// not ask". Collapsing those would send people to re-run `kcap login` for an outage — the same
/// class of misdirection that made whoami untrustworthy in the first place.
/// </summary>
public class WhoamiProbeTests {
    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_are_the_only_token_verdict(HttpStatusCode status) {
        var verdict = WhoamiCommand.Interpret(status);

        await Assert.That(verdict.ExitCode).IsEqualTo(1);
        await Assert.That(verdict.Line).Contains("REJECTS");
    }

    [Test]
    [Arguments(HttpStatusCode.OK)]
    [Arguments(HttpStatusCode.NoContent)]
    public async Task Success_reports_acceptance(HttpStatusCode status) {
        var verdict = WhoamiCommand.Interpret(status);

        await Assert.That(verdict.ExitCode).IsEqualTo(0);
        await Assert.That(verdict.Line).Contains("accepts");
    }

    [Test]
    // 404: an older server without the probe endpoint — says nothing about the token.
    [Arguments(HttpStatusCode.NotFound)]
    // 3xx: a login redirect must not be read as a rejection.
    [Arguments(HttpStatusCode.Redirect)]
    // Server-side trouble is not the user's credential being wrong.
    [Arguments(HttpStatusCode.RequestTimeout)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.BadGateway)]
    public async Task Non_auth_responses_are_reported_as_unverified_not_rejected(HttpStatusCode status) {
        var verdict = WhoamiCommand.Interpret(status);

        await Assert.That(verdict.ExitCode).IsEqualTo(0);
        await Assert.That(verdict.Line).Contains("could not verify");
        await Assert.That(verdict.Line).DoesNotContain("REJECTS");
    }

    [Test]
    public async Task Unreachable_server_keeps_whoami_usable_offline() {
        var verdict = WhoamiCommand.Interpret(null);

        await Assert.That(verdict.ExitCode).IsEqualTo(0);
        await Assert.That(verdict.Line).Contains("unreachable");
    }

    [Test]
    public async Task Server_error_line_names_the_status_so_it_is_actionable() {
        await Assert.That(WhoamiCommand.Interpret(HttpStatusCode.InternalServerError).Line).Contains("500");
    }
}
