namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Keyboard seam for the sign-in escape hatch. Every member must return without blocking — the
/// watcher polls it while another leg of the flow is in flight.
/// </summary>
public interface IKeyWatcher {
    /// <summary>False when there is no keyboard to poll, which makes every other member unusable.</summary>
    bool CanWatch { get; }

    bool KeyAvailable { get; }

    /// <summary>Consumes one buffered key without echoing it.</summary>
    char ReadKey();

    /// <summary>
    /// Consumes everything still buffered. The escape-hatch key is usually followed by a Return, and
    /// whatever prompt runs next (Spectre's picker) would otherwise read it as an answer.
    /// </summary>
    void Drain();
}

/// <inheritdoc cref="IKeyWatcher"/>
public sealed class ConsoleKeyWatcher : IKeyWatcher {
    public static readonly ConsoleKeyWatcher Instance = new();

    /// <summary>
    /// Redirected stdin has no keypresses, and <see cref="Console.KeyAvailable"/> throws on it. The
    /// catch covers a host with no console attached at all, where even the probe throws.
    /// </summary>
    public bool CanWatch {
        get {
            try {
                return !Console.IsInputRedirected;
            } catch (Exception ex) when (ex is IOException or InvalidOperationException) {
                return false;
            }
        }
    }

    public bool KeyAvailable {
        get {
            try {
                return Console.KeyAvailable;
            } catch (Exception ex) when (ex is IOException or InvalidOperationException) {
                return false;
            }
        }
    }

    public char ReadKey() => Console.ReadKey(intercept: true).KeyChar;

    public void Drain() {
        while (KeyAvailable) ReadKey();
    }
}

/// <summary>
/// The default for any host that is not a terminal. A GUI or a service has no keyboard to poll, and
/// the escape hatch it would offer is unreachable there.
/// </summary>
public sealed class NoKeyWatcher : IKeyWatcher {
    public static readonly NoKeyWatcher Instance = new();

    public bool CanWatch => false;

    public bool KeyAvailable => false;

    public char ReadKey() => throw new NotSupportedException("There is no keyboard to read.");

    public void Drain() { }
}
