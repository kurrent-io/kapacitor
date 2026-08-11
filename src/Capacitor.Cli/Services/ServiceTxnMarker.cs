using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Durable, phase-recording record of an in-flight <c>kcap daemon service</c> mutation
/// (install/replace/start). Lives at <c>{DaemonLockPaths.Directory}/{id}.service-txn</c>,
/// distinct from <see cref="ServiceTxnLock"/>. A resumer reads the last recorded
/// <see cref="TxnMarker.Phase"/> to decide how far a prior attempt got.
/// </summary>
public sealed record TxnMarker(
    int Version,
    string Operation,
    string Phase,
    string PreState,
    string SafeState,
    string? PlistFingerprint);

public static partial class ServiceTxnMarker {
    public static string MarkerPath(string serviceId) =>
        Path.Combine(DaemonLockPaths.Directory, $"{serviceId}.service-txn");

    public static bool Exists(string serviceId) => File.Exists(MarkerPath(serviceId));

    /// <summary>Null on missing OR corrupt — a torn/unreadable marker must never crash a caller.</summary>
    public static TxnMarker? Read(string serviceId) {
        var path = MarkerPath(serviceId);
        try {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), MarkerJsonContext.Default.TxnMarker);
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// Temp file + rename, flushing the file handle to disk both before and after the rename.
    /// .NET has no portable API to fsync a directory entry, so durability of the RENAME itself
    /// (as opposed to the marker's content) is best-effort here — acceptable per spec intent,
    /// since the marker content is torn-proof either way.
    /// </summary>
    public static void Write(string serviceId, TxnMarker marker) {
        DaemonLockPaths.EnsureDirectory();
        var path = MarkerPath(serviceId);
        var tmp  = path + ".tmp";
        var json = JsonSerializer.Serialize(marker, MarkerJsonContext.Default.TxnMarker);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        RandomAccess.FlushToDisk(handle);
    }

    public static void Delete(string serviceId) {
        try { File.Delete(MarkerPath(serviceId)); } catch (IOException) { /* best-effort */ }
    }

    public static string Fingerprint(string plistText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plistText)));

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(TxnMarker))]
    partial class MarkerJsonContext : JsonSerializerContext;
}
