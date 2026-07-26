using System.Text.Json.Serialization;

namespace ShiroBot.Discord.Model;

/// <summary>Discord channel type 常量(常用子集)。</summary>
public static class DcChannelType
{
    public const int GuildText = 0;
    public const int Dm = 1;
    public const int GuildVoice = 2;
    public const int GroupDm = 3;
    public const int GuildCategory = 4;
    public const int GuildAnnouncement = 5;
    public const int AnnouncementThread = 10;
    public const int PublicThread = 11;
    public const int PrivateThread = 12;
    public const int GuildForum = 15;
}

/// <summary>Discord `Channel`。</summary>
public sealed record DcChannel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }

    [JsonPropertyName("recipients")]
    public IReadOnlyList<DcUser>? Recipients { get; init; }

    [JsonIgnore]
    public bool IsDm => Type is DcChannelType.Dm or DcChannelType.GroupDm;

    [JsonIgnore]
    public bool IsThread => Type is DcChannelType.AnnouncementThread
        or DcChannelType.PublicThread
        or DcChannelType.PrivateThread;
}

/// <summary>Discord `Guild`(部分字段)。</summary>
public sealed record DcGuild
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; init; }
}
