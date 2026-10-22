using System.Text.Json.Serialization;

namespace ShiroBot.Discord.Model;

/// <summary>Discord `User`。</summary>
public sealed record DcUser
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("global_name")]
    public string? GlobalName { get; init; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }

    [JsonPropertyName("bot")]
    public bool IsBot { get; init; }

    [JsonIgnore]
    public string DisplayName => GlobalName ?? Username;

    [JsonIgnore]
    public string? AvatarUrl => Avatar is null
        ? null
        : $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.png";

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>Discord `GuildMember`。</summary>
public sealed record DcGuildMember
{
    [JsonPropertyName("user")]
    public DcUser? User { get; init; }

    [JsonPropertyName("nick")]
    public string? Nick { get; init; }

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = [];

    [JsonPropertyName("joined_at")]
    public DateTimeOffset? JoinedAt { get; init; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}
