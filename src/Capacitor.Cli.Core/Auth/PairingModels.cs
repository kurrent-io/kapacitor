using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// The CLI half of the machine-pairing channel, field-for-field the tenant's Capacitor.Pairing
// contracts. The server's pairing-channel spec is authoritative for what each field means and which
// of them are deliberately NOT security controls.

/// <summary>POST /api/pairings. Both fields are display material shown beside the code on the
/// consent screen — the server sanitises them precisely because they are not credentials.</summary>
public sealed record MintPairingRequest {
    [JsonPropertyName("machine_id")]   public required string MachineId   { get; init; }
    [JsonPropertyName("machine_name")] public required string MachineName { get; init; }
}

/// <summary>The only response that ever carries the secret.</summary>
public sealed record MintPairingResponse {
    [JsonPropertyName("pairing_id")]            public string         PairingId           { get; init; } = "";
    [JsonPropertyName("user_code")]             public string         UserCode            { get; init; } = "";
    [JsonPropertyName("secret")]                public string         Secret              { get; init; } = "";
    [JsonPropertyName("expires_at")]            public DateTimeOffset ExpiresAt           { get; init; }
    [JsonPropertyName("poll_interval_seconds")] public int            PollIntervalSeconds { get; init; }
    [JsonPropertyName("setup_url")]             public string         SetupUrl            { get; init; } = "";
}

/// <summary>The approving human, echoed back so the CLI can prove it authenticated as the same
/// person. An object around one field because the tenant intends to add to it.</summary>
public sealed record PairingUser {
    [JsonPropertyName("id")] public string Id { get; init; } = "";
}

/// <summary><c>server_url</c> and <c>user</c> arrive only once approved. <c>completed</c> is not in
/// the vocabulary: completion invalidates the secret, so the caller that could observe it no longer
/// authenticates.</summary>
public sealed record PairingStatusResponse {
    [JsonPropertyName("status")]        public string       Status       { get; init; } = "";
    [JsonPropertyName("server_url")]    public string?      ServerUrl    { get; init; }
    [JsonPropertyName("user")]          public PairingUser? User         { get; init; }
    [JsonPropertyName("state_version")] public int          StateVersion { get; init; }
}
