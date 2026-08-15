using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services.Onboarding;

/// Key = {Profile, CanonicalServer}; both are assumed already-canonicalized by the caller.
public sealed record ConsentFlipClaim(string Profile, string CanonicalServer);

/// Durable decision-7 claim store; every mutation is a synchronous ConfigFileLock critical section (no await while held).
public sealed partial class ConsentFlipClaims(string path, string? configPath = null) {
    // configPath is a test seam for TryConsume's config lock; production default is AppConfig.GetConfigPath().
    readonly string _configPath = configPath ?? AppConfig.GetConfigPath();
    volatile QuarantineState? _quarantine;

    public static ConsentFlipClaims Default() => new(PathHelpers.ConfigPath("consent-flip-claims.json"));

    public IReadOnlyList<ConsentFlipClaim> Pending() {
        using var _ = ConfigFileLock.Acquire(path);
        return ReadFreshLocked().Claims.Select(c => new ConsentFlipClaim(c.Profile, c.Server)).ToList();
    }

    /// Upsert by key + durable flush. False (any pre-durability failure) blocks the sign-in commit (decision 7).
    /// Defensively canonicalizes CanonicalServer at entry (idempotent for an already-canonical
    /// caller) — a raw/uncanonical URL armed here would otherwise never match TryConsume's
    /// canonical-identity re-resolve, stranding the claim as permanently pending.
    public bool Arm(ConsentFlipClaim claim) {
        claim = claim with { CanonicalServer = ServerIdentity.Canonicalize(claim.CanonicalServer) ?? claim.CanonicalServer };
        using var _ = ConfigFileLock.Acquire(path);
        var file = ReadFreshLocked();
        var claims = file.Claims
            .Where(c => c.Profile != claim.Profile || c.Server != claim.CanonicalServer)
            .Append(new ClaimEntry(claim.Profile, claim.CanonicalServer))
            .ToList();
        return Publish(new ClaimsFile(1, claims));
    }

    /// Two-lock conditional clear (spec §6): config lock → re-resolve → claims lock, fixed order, no await inside.
    public bool TryConsume(
            ConsentFlipClaim claim,
            Func<(string Profile, string Server, string DaemonName)> reResolveUnderConfigLock,
            string expectedDaemonName) {
        using var configLock = ConfigFileLock.Acquire(_configPath);
        var resolved = reResolveUnderConfigLock();
        if (resolved.Profile != claim.Profile || resolved.Server != claim.CanonicalServer || resolved.DaemonName != expectedDaemonName)
            return false;

        using var claimsLock = ConfigFileLock.Acquire(path);
        var file = ReadFreshLocked();
        var remaining = file.Claims.Where(c => c.Profile != claim.Profile || c.Server != claim.CanonicalServer).ToList();
        if (remaining.Count == file.Claims.Count) return true; // already gone — idempotent re-apply, nothing to publish

        // Publish's rename is the commit point — a post-rename durability failure still returns true (idempotent re-apply).
        return Publish(new ClaimsFile(1, remaining));
    }

    public QuarantineState? Quarantine() => _quarantine;

    // Corruption on any read quarantines the file aside and returns a fresh state (caller holds the lock).
    ClaimsFile ReadFreshLocked() {
        if (!File.Exists(path)) return new ClaimsFile(1, []);

        ClaimsFile? parsed = null;
        try {
            parsed = JsonSerializer.Deserialize(File.ReadAllText(path), ConsentFlipClaimsJsonCtx.Default.ClaimsFile);
        } catch { /* fall through to quarantine */ }

        if (parsed?.Claims is not null) return parsed;

        try { _quarantine = new QuarantineState(QuarantineLocked()); }
        catch { /* quarantining itself failing must not wedge the read */ }
        return new ClaimsFile(1, []);
    }

    string QuarantineLocked() {
        for (var n = 0; ; n++) {
            var candidate = QuarantinePath(n);
            if (File.Exists(candidate)) continue;
            File.Move(path, candidate);
            return candidate;
        }
    }

    string QuarantinePath(int n) {
        var dir  = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        return Path.Combine(dir, $"{stem}.quarantined-{n}{ext}");
    }

    // Temp write + fsync-before-rename + Windows retry; post-rename fsync is best-effort (never turns a commit into false).
    bool Publish(ClaimsFile file) {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(file, ConsentFlipClaimsJsonCtx.Default.ClaimsFile));
            fs.Flush(flushToDisk: true);
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            return false;
        }

        for (var attempt = 0; ; attempt++) {
            try {
                File.Move(tmp, path, overwrite: true);
                break;
            } catch (Exception e) when (e is UnauthorizedAccessException or IOException && attempt < 49) {
                Thread.Sleep(20);
            } catch {
                try { File.Delete(tmp); } catch { /* best-effort */ }
                return false;
            }
        }

        try {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            RandomAccess.FlushToDisk(handle);
        } catch { /* best-effort past the commit point */ }
        FlushDirectory(!string.IsNullOrEmpty(dir) ? dir : ".");

        return true;
    }

    // ── directory-durability barrier (same libc pattern as Capacitor.Cli's ServiceTxnMarker) ──

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);

    static void FlushDirectory(string dir) {
        if (OperatingSystem.IsWindows()) return; // no portable directory fsync on Windows

        var fd = -1;
        try {
            fd = open(dir, 0 /* O_RDONLY */);
            if (fd >= 0) fsync(fd);
        } catch { /* durability hardening only — never break a claim write */
        } finally {
            if (fd >= 0) close(fd);
        }
    }

    public sealed record QuarantineState(string PreservedPath);

    // JSON DTO — public record's CanonicalServer maps to the "server" wire field.
    private sealed record ClaimsFile(int Version, List<ClaimEntry> Claims);
    private sealed record ClaimEntry(string Profile, string Server);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ClaimsFile))]
    partial class ConsentFlipClaimsJsonCtx : JsonSerializerContext;
}
