using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Strict floor predicate for the daemon-side kcap CLI version. Unlike
/// <see cref="PrereleaseSemver"/>'s tolerant Compare (which treats unparseable input as lowest),
/// StrictParse rejects malformed SemVer outright so a garbled version string never silently reads
/// as "too old" instead of "unknown".
/// </summary>
public static class KcapCliCompatibility {
    public const string Floor = "0.12.0-beta.1";

    public static bool Satisfies(string? version) =>
        version is not null &&
        StrictParse(version) &&
        PrereleaseSemver.Compare(version, Floor) >= 0;

    public static bool StrictParse(string version) {
        var core = version;

        var plusIndex = core.IndexOf('+');
        if (plusIndex >= 0) {
            var build = core[(plusIndex + 1)..];
            core = core[..plusIndex];
            if (!IsValidBuild(build)) return false;
        }

        var dashIndex = core.IndexOf('-');
        if (dashIndex >= 0) {
            var prerelease = core[(dashIndex + 1)..];
            core = core[..dashIndex];
            if (!IsValidPrerelease(prerelease)) return false;
        }

        return IsValidCore(core);
    }

    static bool IsValidCore(string core) {
        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        foreach (var part in parts) {
            if (!IsNumericIdentifierNoLeadingZero(part)) return false;
        }
        return true;
    }

    static bool IsValidPrerelease(string prerelease) {
        if (prerelease.Length == 0) return false;
        var identifiers = prerelease.Split('.');
        foreach (var id in identifiers) {
            if (id.Length == 0) return false;
            // Each identifier is either strictly numeric (no leading zero) or alphanumeric-with-hyphen.
            if (IsAllDigits(id)) {
                if (!IsNumericIdentifierNoLeadingZero(id)) return false;
            } else if (!IsAlphanumericWithHyphen(id)) {
                return false;
            }
        }
        return true;
    }

    // Build identifiers, unlike prerelease ones, allow leading zeros (SemVer §10).
    static bool IsValidBuild(string value) {
        if (value.Length == 0) return false;
        var identifiers = value.Split('.');
        foreach (var id in identifiers) {
            if (!IsAlphanumericWithHyphen(id)) return false;
        }
        return true;
    }

    static bool IsNumericIdentifierNoLeadingZero(string value) {
        if (!IsAllDigits(value)) return false;
        return value.Length == 1 || value[0] != '0';
    }

    static bool IsAllDigits(string value) {
        if (value.Length == 0) return false;
        foreach (var c in value) {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }

    static bool IsAlphanumericWithHyphen(string value) {
        if (value.Length == 0) return false;
        foreach (var c in value) {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')) return false;
        }
        return true;
    }
}
