using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// Auth proxy: POST /cli/v1/picker/prepare request. The secret itself never leaves this process.
public sealed record CliPickerPrepareRequest {
    [JsonPropertyName("secret_hash")] public string SecretHash { get; init; } = "";
}

public sealed record CliPickerTenant {
    [JsonPropertyName("key")]          public string  Key         { get; init; } = "";
    [JsonPropertyName("slug")]         public string? Slug        { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("origin")]       public string  Origin      { get; init; } = "";
}

public sealed record CliPickerPrepareResponse {
    [JsonPropertyName("tenants")]               public CliPickerTenant[] Tenants             { get; init; } = [];
    [JsonPropertyName("handle")]                public string               Handle              { get; init; } = "";
    [JsonPropertyName("poll_interval_seconds")] public int                  PollIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// The proxy's deadline, which the CLI adopts as its own rather than timing independently: the
    /// page stops accepting a choice at this instant, so a CLI that waited longer would poll a
    /// handle nobody can answer, and one that gave up sooner would strand a choice already made.
    /// </summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record CliPickerResultRequest {
    [JsonPropertyName("secret")] public string Secret { get; init; } = "";
}

public sealed record CliPickerResultResponse {
    [JsonPropertyName("status")] public string  Status { get; init; } = "";
    [JsonPropertyName("key")]    public string? Key    { get; init; }
}

/// <summary>
/// What a picker needs beyond the rows, and could not otherwise reach: the seam is constructed in
/// <c>SetupCommand</c> before any login exists, so the bearer and the channel are only known later.
/// </summary>
/// <param name="ViaLoopback">
/// Whether the login went through the loopback browser. Observed rather than predicted: the browser
/// leg falls through to the device grant on the escape hatch, on a missing browser, and on a headless
/// console, and none of that is knowable from the flags. False means a browser is not reachable, so
/// opening one would leave the user watching a terminal poll to its deadline.
/// </param>
public sealed record TenantPickContext(
    string?           Bearer      = null,
    IAuthProxyClient? Proxy       = null,
    string?           ProxyUrl    = null,
    bool              ViaLoopback = false,
    int               PickerVersion = 0
) {
    /// <summary>The GitHub lane, and every caller that has nothing to offer a browser picker.</summary>
    public static readonly TenantPickContext None = new();

    /// <summary>The picker shape this build calls, and the one <c>/cli/v1/picker/*</c> names.</summary>
    public const int SupportedPickerVersion = 1;

    /// <summary>
    /// Whether a browser pick is worth attempting at all. Below the supported version is a proxy that
    /// predates the routes, and it degrades to the terminal rather than polling a 404 to the deadline.
    /// At or above it, the routes this build calls are the frozen v1 ones: a later shape gets its own
    /// path, so a proxy that has moved on still serves these.
    /// </summary>
    public bool CanPickInBrowser =>
        ViaLoopback && PickerVersion >= SupportedPickerVersion &&
        !string.IsNullOrEmpty(Bearer) && !string.IsNullOrEmpty(ProxyUrl) && Proxy is not null;
}
