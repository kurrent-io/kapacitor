using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>Hands the watcher a fixed sequence of keys, and records what a drain swallowed.</summary>
public sealed class ScriptedKeyWatcher(params char[] keys) : IKeyWatcher {
    readonly Queue<char> buffered = new(keys);

    public bool CanWatch { get; init; } = true;

    public int Drained { get; private set; }

    public bool KeyAvailable => buffered.Count > 0;

    public char ReadKey() => buffered.Dequeue();

    public void Drain() {
        Drained += buffered.Count;
        buffered.Clear();
    }

    /// <summary>A watcher with no keyboard behind it — redirected stdin, or a host with no console.</summary>
    public static ScriptedKeyWatcher Blind() => new() { CanWatch = false };
}
