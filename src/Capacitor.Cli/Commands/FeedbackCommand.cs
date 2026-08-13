using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap feedback (--bug | --feedback) [-m|--message &lt;text&gt;]</c> — files a bug report or
/// sends feedback to Kurrent support through the tenant's <c>POST /api/feedback</c> (Task 10),
/// which resolves the reporter's email server-side and forwards to the auth proxy's Plain sink.
///
/// <para>Follows <see cref="ValidatePlanCommand"/>'s <c>Handle</c>/<c>HandleCore</c> split: <c>Handle</c>
/// owns argument parsing, the interactive prompt, and building the authenticated client;
/// <c>HandleCore</c> takes an already-built <see cref="HttpClient"/> so tests can drive it against a
/// fake server without touching the token store.</para>
/// </summary>
public static class FeedbackCommand {
    /// <summary>
    /// The pinned success line (spec: no reply promise — a later revision may add one, but this
    /// exact string is what ships first). Printed to stdout so <c>kcap feedback ... | ...</c> can
    /// capture it; everything else in this command writes to stderr.
    /// </summary>
    internal const string SuccessPrefix = "✓ Sent to Kurrent support as ";

    const string BugFlag      = "--bug";
    const string FeedbackFlag = "--feedback";

    const string InteractivePrompt = "What's going on? (end with an empty line)";

    public static Task<int> HandleAsync(string baseUrl, string[] args) =>
        HandleAsync(baseUrl, args, Console.IsInputRedirected, Console.ReadLine);

    /// <summary>
    /// Test-friendly entry point: <paramref name="stdinIsRedirected"/> and <paramref name="readLine"/>
    /// stand in for <see cref="Console.IsInputRedirected"/>/<see cref="Console.ReadLine"/> so the
    /// TTY-vs-piped branch and the interactive prompt's line collection are exercised without a real
    /// terminal or process stdin.
    /// </summary>
    internal static async Task<int> HandleAsync(
            string baseUrl, string[] args, bool stdinIsRedirected, Func<string?> readLine) {
        var isBug      = args.Contains(BugFlag);
        var isFeedback = args.Contains(FeedbackFlag);

        // Exactly one of --bug/--feedback: neither and both are the same usage error, naming both
        // flags so the fix is obvious either way.
        if (isBug == isFeedback) {
            await Console.Error.WriteLineAsync("Usage: kcap feedback (--bug | --feedback) [-m|--message <text>]");
            await Console.Error.WriteLineAsync("  Pass exactly one of --bug or --feedback.");

            return 1;
        }

        var category   = isBug ? "bug" : "feedback";
        var rawMessage = GetMessageArg(args);

        if (rawMessage is null) {
            // stdin is not a TTY (piped/redirected): there is no one to prompt, so -m is mandatory.
            if (stdinIsRedirected) {
                await Console.Error.WriteLineAsync("A message is required.");

                return 1;
            }

            await Console.Error.WriteLineAsync(InteractivePrompt);
            rawMessage = ReadInteractiveMessage(readLine);
        }

        var message = rawMessage.Trim();

        if (message.Length == 0) {
            await Console.Error.WriteLineAsync("A message is required.");

            return 1;
        }

        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        return await HandleCore(httpClient, baseUrl, category, message);
    }

    /// <summary>
    /// Test-friendly core: caller owns the <see cref="HttpClient"/> (mirrors
    /// <see cref="ValidatePlanCommand.HandleCore"/>'s seam). <paramref name="category"/> is already
    /// "bug"/"feedback" and <paramref name="message"/> is already trimmed and non-empty.
    /// </summary>
    internal static async Task<int> HandleCore(HttpClient httpClient, string baseUrl, string category, string message) {
        var request = new FeedbackSubmitRequest(
            Category:        category,
            Message:         message,
            ClientRequestId: Guid.NewGuid(),
            Context: new FeedbackSubmitContext(
                Source:        "cli",
                ClientVersion: CapacitorVersion.CurrentDisplay(),
                Os:            RuntimeInformation.OSDescription
            )
        );

        HttpResponseMessage resp;

        try {
            using var content = JsonContent.Create(request, CapacitorJsonContext.Default.FeedbackSubmitRequest);
            resp = await httpClient.PostWithRetryAsync($"{baseUrl}/api/feedback", content);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        return await ReportResultAsync(resp);
    }

    static async Task<int> ReportResultAsync(HttpResponseMessage resp) {
        if (resp.StatusCode == HttpStatusCode.OK) {
            var success = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.FeedbackSubmitResponse);
            await Console.Out.WriteLineAsync($"{SuccessPrefix}{success?.ReporterEmail ?? ""}");

            return 0;
        }

        var body      = await resp.Content.ReadAsStringAsync();
        var errorCode = TryGetField(body, "error");

        switch (resp.StatusCode) {
            // A bare 404/405 (no JSON body — see the server's SupportEndpoints doc: a POST to an
            // unmapped route 405s via ASP.NET's own routing layer, a GET 404s via the Blazor
            // fallback route) means the whole feature is off on this server. A CODED 404
            // (`feedback_not_configured`) means the route exists but the sink isn't configured —
            // same user-facing advice as the coded 503, so both land on the same message below.
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed when errorCode is null:
                await Console.Error.WriteLineAsync("This server doesn't have support intake enabled.");

                return 1;

            case HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable:
                await Console.Error.WriteLineAsync("Support intake isn't configured on this server — ask your admin.");

                return 1;

            case HttpStatusCode.Conflict:
                await Console.Error.WriteLineAsync(
                    "Your account has no email on file — sign in to the web app once, then retry.");

                return 1;

            case HttpStatusCode.TooManyRequests:
                await Console.Error.WriteLineAsync("You've sent several reports recently — try again in a few minutes.");

                return 1;

            case HttpStatusCode.BadGateway:
                var suffix = resp.Headers.RetryAfter?.Delta is { } delta
                    ? $" in {(int)Math.Ceiling(delta.TotalSeconds)}s."
                    : ".";
                await Console.Error.WriteLineAsync($"Couldn't reach Kurrent support (temporary) — try again{suffix}");

                return 1;

            case HttpStatusCode.BadRequest:
                await Console.Error.WriteLineAsync(TryGetField(body, "message") ?? "The feedback request was invalid.");

                return 1;

            default:
                await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");

                return 1;
        }
    }

    /// <summary>Reads a named string field from a possibly-empty, possibly-non-JSON body. Absence
    /// (empty body, malformed JSON, or a missing/wrong-typed field) all read as <c>null</c> — the
    /// same defensive contract <see cref="JsonElementExtensions"/> uses everywhere else.</summary>
    static string? TryGetField(string body, string field) {
        if (string.IsNullOrEmpty(body)) return null;

        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.Str(field);
        } catch (JsonException) {
            return null;
        }
    }

    static string? GetMessageArg(string[] args) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] is "-m" or "--message") return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Collects lines from <paramref name="readLine"/> until an empty line (or end of input,
    /// e.g. Ctrl+D) and joins them with newlines. Pure and injectable so tests can drive the
    /// multi-line collection without a real TTY.
    /// </summary>
    internal static string ReadInteractiveMessage(Func<string?> readLine) {
        var lines = new List<string>();

        while (true) {
            var line = readLine();

            if (string.IsNullOrEmpty(line)) break;

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }
}
