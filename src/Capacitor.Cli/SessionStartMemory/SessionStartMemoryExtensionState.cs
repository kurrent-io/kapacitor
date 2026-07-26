using System.Collections.Concurrent;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryExtensionState {
    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task ObserveBridgeResultAsync(string localKey, string? fragment) {
        if (string.IsNullOrEmpty(fragment)) return;
        var entry = _entries.GetOrAdd(localKey, static _ => new Entry());
        await entry.Gate.WaitAsync();
        try { entry.Fragment ??= fragment; }
        finally { entry.Gate.Release(); }
    }

    public async Task<string?> TakeForDeliveryAsync(string localKey) {
        if (!_entries.TryGetValue(localKey, out var entry)) return null;
        await entry.Gate.WaitAsync();
        try {
            if (entry.Delivered || entry.Fragment is null) return null;
            entry.Delivered = true;
            return entry.Fragment;
        } finally { entry.Gate.Release(); }
    }

    sealed class Entry {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Fragment { get; set; }
        public bool Delivered { get; set; }
    }
}
