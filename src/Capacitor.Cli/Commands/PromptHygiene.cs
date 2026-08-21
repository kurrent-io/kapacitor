using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

static class PromptHygiene {
    /// <summary>
    /// Empties the keyboard buffer immediately before a prompt.
    ///
    /// <para>Every prompt in the auth flows follows a long wait the user did not control - a browser
    /// sign-in, a device-code approval - so anything buffered was typed at something else. Draining
    /// at the point the keystrokes were *provoked* cannot work: the Return after the <c>d</c> escape
    /// hatch arrives after that drain has already run, waits out the whole approval, and then answers
    /// the next SelectionPrompt on the user's behalf. Observed doing exactly that on 2026-08-21,
    /// silently choosing "Create a new workspace".</para>
    ///
    /// <para>Safe to call unconditionally: with no console attached the underlying probe reports
    /// nothing available rather than throwing.</para>
    /// </summary>
    internal static void DiscardTypeAhead() => ConsoleKeyWatcher.Instance.Drain();
}
