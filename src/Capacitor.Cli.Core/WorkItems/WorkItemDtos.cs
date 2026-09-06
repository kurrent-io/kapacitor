using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// The body of <c>GET /api/work-items/{id}</c>. <see cref="WorkItemId"/> is the served item, which
/// differs from the requested id when an absorbed item redirected to its survivor.
/// <see cref="EnrichedTitle"/> is the tracker's title when <see cref="Title"/> is a key, else null.
/// <see cref="SessionCount"/> counts only attached sessions the caller can see.
public sealed record WorkItemDto {
    List<WorkItemLinkDto> _links = [];
    List<WorkItemPartDto> _parts = [];
    List<WorkItemContributorDto> _contributors = [];

    [JsonPropertyName("work_item_id")]           public required string             WorkItemId           { get; init; }
    [JsonPropertyName("title")]                  public required string             Title                { get; init; }
    [JsonPropertyName("enriched_title")]         public string?                     EnrichedTitle        { get; init; }
    [JsonPropertyName("overview")]               public string?                     Overview             { get; init; }
    [JsonPropertyName("is_overview_mechanical")] public bool                        IsOverviewMechanical { get; init; }
    [JsonPropertyName("key")]                    public WorkItemKeyDto?             Key                  { get; init; }
    [JsonPropertyName("state")]                  public WorkItemStateDto?           State                { get; init; }
    [JsonPropertyName("links")]                  public List<WorkItemLinkDto>       Links        { get => _links; init => _links = value ?? []; }
    [JsonPropertyName("parts")]                  public List<WorkItemPartDto>       Parts        { get => _parts; init => _parts = value ?? []; }
    [JsonPropertyName("contributors")]           public List<WorkItemContributorDto> Contributors { get => _contributors; init => _contributors = value ?? []; }
    [JsonPropertyName("session_count")]          public int                         SessionCount { get; init; }
}

/// The item's seed key. <see cref="Value"/> is the link's normalized value, so it matches one row of
/// <see cref="WorkItemDto.Links"/>.
public sealed record WorkItemKeyDto {
    [JsonPropertyName("short_key")] public required string ShortKey { get; init; }
    [JsonPropertyName("provider")]  public required string Provider { get; init; }
    [JsonPropertyName("kind")]      public required string Kind     { get; init; }
    [JsonPropertyName("value")]     public required string Value    { get; init; }
}

/// <see cref="Kind"/> is in_flight|shipped|closed; the server may add kinds, so an unknown one is
/// tolerated. <see cref="SettledAt"/> is null while in flight.
public sealed record WorkItemStateDto {
    [JsonPropertyName("kind")]       public required string   Kind      { get; init; }
    [JsonPropertyName("settled_at")] public DateTimeOffset?   SettledAt { get; init; }
}

/// One active link. <see cref="LinkClass"/> is <c>link</c> for identity-bearing evidence or
/// <c>reference</c> for an ambient mention the server passes through for other consumers.
public sealed record WorkItemLinkDto {
    [JsonPropertyName("kind")]       public required string Kind      { get; init; }
    [JsonPropertyName("provider")]   public required string Provider  { get; init; }
    [JsonPropertyName("value")]      public required string Value     { get; init; }
    [JsonPropertyName("short_key")]  public required string ShortKey  { get; init; }
    [JsonPropertyName("url")]        public string?         Url       { get; init; }
    [JsonPropertyName("title")]      public string?         Title     { get; init; }
    [JsonPropertyName("state")]      public string?         State     { get; init; }
    [JsonPropertyName("link_class")] public required string LinkClass { get; init; }
    [JsonPropertyName("is_seed")]    public bool            IsSeed    { get; init; }
}

public sealed record WorkItemPartDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title      { get; init; }
    [JsonPropertyName("ordinal")]      public int             Ordinal    { get; init; }
    [JsonPropertyName("is_settled")]   public bool            IsSettled  { get; init; }
    [JsonPropertyName("settled_at")]   public DateTimeOffset? SettledAt  { get; init; }
}

/// <see cref="DisplayName"/> and <see cref="AvatarUrl"/> are null for an owner with no user row.
public sealed record WorkItemContributorDto {
    [JsonPropertyName("user_id")]          public required string UserId         { get; init; }
    [JsonPropertyName("display_name")]     public string?         DisplayName    { get; init; }
    [JsonPropertyName("avatar_url")]       public string?         AvatarUrl      { get; init; }
    [JsonPropertyName("last_activity_at")] public DateTimeOffset? LastActivityAt { get; init; }
}
