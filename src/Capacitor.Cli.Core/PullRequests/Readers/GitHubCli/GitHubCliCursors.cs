using System.Security.Cryptography;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>Opaque 64-hex handles for pages, bounded to 256 entries, least recently used first.</summary>
public sealed class GitHubCliCursors {
    const int Capacity = 256;
    readonly Lock _lock = new();
    readonly Dictionary<string, LinkedListNode<(string Handle, GitHubCliCursorEntry Entry)>> _entries = new(StringComparer.Ordinal);
    readonly LinkedList<(string Handle, GitHubCliCursorEntry Entry)> _order = new();

    public static string NewHandle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string Mint(GitHubCliCursorEntry entry) {
        var handle = NewHandle();
        lock (_lock) {
            _entries[handle] = _order.AddFirst((handle, entry));
            while (_order.Count > Capacity) { _entries.Remove(_order.Last!.Value.Handle); _order.RemoveLast(); }
        }
        return handle;
    }

    public GitHubCliCursorEntry? Get(string handle) {
        lock (_lock) {
            if (!_entries.TryGetValue(handle, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entry;
        }
    }
}
