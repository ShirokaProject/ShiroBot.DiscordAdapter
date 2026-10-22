using System.Text.Json.Serialization;

namespace ShiroBot.Discord.Model;

/// <summary>Discord `Message`(常用字段子集)。</summary>
public sealed record DcMessage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("channel_id")]
    public required string ChannelId { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    [JsonPropertyName("author")]
    public DcUser? Author { get; init; }

    [JsonPropertyName("member")]
    public DcGuildMember? Member { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("mentions")]
    public IReadOnlyList<DcUser>? Mentions { get; init; }

    [JsonPropertyName("mention_everyone")]
    public bool MentionEveryone { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<DcAttachment>? Attachments { get; init; }

    [JsonPropertyName("embeds")]
    public IReadOnlyList<DcEmbed>? Embeds { get; init; }

    [JsonPropertyName("message_reference")]
    public DcMessageReference? MessageReference { get; init; }

    [JsonPropertyName("referenced_message")]
    public DcMessage? ReferencedMessage { get; init; }

    [JsonPropertyName("sticker_items")]
    public IReadOnlyList<DcStickerItem>? StickerItems { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>Discord `Attachment`。</summary>
public sealed record DcAttachment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("duration_secs")]
    public double? DurationSecs { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>Discord `Embed`(发送/接收共用,字段子集)。</summary>
public sealed record DcEmbed
{
    [JsonPropertyName("image")]
    public DcEmbedImage? Image { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<DcEmbedField>? Fields { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("color")]
    public int? Color { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>回复引用。</summary>
public sealed record DcMessageReference
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

public sealed record DcStickerItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

public sealed record DcEmbedImage([property: JsonPropertyName("url")] string Url)
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}
public sealed record DcEmbedField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value)
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}
