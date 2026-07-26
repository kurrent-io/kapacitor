using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.SessionStartMemory;

internal static class BoundedJsonFile {
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static T? Read<T>(string path, int limit, JsonTypeInfo<T> typeInfo) where T : class {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > limit) throw new InvalidDataException("JSON file exceeds its byte limit.");
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length) {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > limit || stream.ReadByte() >= 0) throw new InvalidDataException("JSON file grew beyond its byte limit.");
        _ = StrictUtf8.GetString(buffer, 0, total);
        var reader = new Utf8JsonReader(buffer.AsSpan(0, total), new JsonReaderOptions {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var value = JsonSerializer.Deserialize(ref reader, typeInfo);
        while (reader.Read()) { }
        if (reader.BytesConsumed != total) throw new JsonException("Trailing data is not allowed.");
        return value;
    }

    public static void AtomicWrite<T>(string root, string destination, T value, int limit, JsonTypeInfo<T> typeInfo) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (bytes.Length > limit) throw new InvalidDataException("Serialized JSON exceeds its byte limit.");
        var stem = Path.GetFileNameWithoutExtension(destination);
        var temp = Path.Combine(root, $"{stem}.{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                stream.Write(bytes);
                stream.Flush();
            }
            File.Move(temp, destination, overwrite: true);
        } finally {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
