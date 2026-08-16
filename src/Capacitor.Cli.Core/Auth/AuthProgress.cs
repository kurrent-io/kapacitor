using System.Diagnostics.CodeAnalysis;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Structured sink for auth-flow progress, so a desktop app can render login/discovery steps
/// without depending on <see cref="Console"/>. Every login/discovery/exchange path accepts one
/// as a trailing optional parameter and defaults to <see cref="ConsoleAuthProgress"/>.
/// </summary>
public interface IAuthProgress {
    /// <summary>An informational line — today's stdout output, verbatim.</summary>
    void Notice(string message);

    /// <summary>An error line — today's stderr output, verbatim.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Error names a severity level, mirroring Console.Error — not a cross-language keyword collision here.")]
    void Error(string message);

    /// <summary>A browser is being opened for an interactive sign-in step; <paramref name="url"/> is the fallback link.</summary>
    void BrowserOpening(string url);

    /// <summary>A device-flow user code is ready for the user to enter at <paramref name="verificationUri"/>.</summary>
    void DeviceCode(string code, string verificationUri);

    /// <summary>One device-flow poll attempt came back pending.</summary>
    void PollTick();
}

/// <summary>Reproduces exactly what the CLI printed to stdout/stderr before <see cref="IAuthProgress"/> existed.</summary>
public sealed class ConsoleAuthProgress : IAuthProgress {
    public static readonly ConsoleAuthProgress Instance = new();

    public void Notice(string message) => Console.Out.WriteLine(message);

    public void Error(string message) => Console.Error.WriteLine(message);

    public void BrowserOpening(string url) {
        Console.Out.WriteLine("Opening browser for authentication...");
        Console.Out.WriteLine($"  If the browser doesn't open, visit: {url}");
    }

    public void DeviceCode(string code, string verificationUri) {
        Console.Out.WriteLine($"  2. Enter the code: {code}");
        Console.Out.WriteLine("  3. Approve access when GitHub asks.");
        Console.Out.WriteLine();
        Console.Write("Waiting for you to authorize...");
    }

    public void PollTick() => Console.Write(".");
}
