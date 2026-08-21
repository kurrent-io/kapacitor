using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

static class PromptHygiene {
    /// <summary>
    /// Empties the keyboard buffer immediately before a prompt.
    ///
    /// <para>Every prompt in the auth flows follows a long wait the user did not control - a browser
    /// sign-in, a device-code approval - so anything buffered was typed at something else. It has to
    /// be here rather than where the keystrokes were *provoked*: the Return after the <c>d</c> escape
    /// hatch arrives after any drain at the hatch has run, survives the whole approval, and then
    /// answers the next SelectionPrompt on the user's behalf.</para>
    ///
    /// <para>Safe to call unconditionally: with no console attached the underlying probe reports
    /// nothing available rather than throwing.</para>
    /// </summary>
    internal static void DiscardTypeAhead() => ConsoleKeyWatcher.Instance.Drain();
}
