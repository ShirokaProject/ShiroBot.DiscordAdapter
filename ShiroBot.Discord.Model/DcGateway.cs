using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.Discord.Model;

/// <summary>Gateway opcode 常量。</summary>
public static class DcOpcode
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
}

/// <summary>Gateway intents 位标志(常用子集)。</summary>
public static class DcIntents
{
    public const int Guilds = 1 << 0;
    public const int GuildMembers = 1 << 1;
    public const int GuildMessages = 1 << 9;
    public const int GuildMessageReactions = 1 << 10;
    public const int DirectMessages = 1 << 12;
    public const int DirectMessageReactions = 1 << 13;
    public const int MessageContent = 1 << 15;

    public const int Default = Guilds | GuildMembers | GuildMessages | GuildMessageReactions
                               | DirectMessages | DirectMessageReactions | MessageContent;
}

/// <summary>Gateway 帧:{ op, d, s, t }。</summary>
public sealed record DcGatewayPayload
{
    [JsonPropertyName("op")]
    public int Op { get; init; }

    [JsonPropertyName("d")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("s")]
    public long? Sequence { get; init; }

    [JsonPropertyName("t")]
    public string? EventName { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>Hello 事件数据。</summary>
public sealed record DcHello
{
    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>READY 事件数据(子集)。</summary>
public sealed record DcReady
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("user")]
    public required DcUser User { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("resume_gateway_url")]
    public string? ResumeGatewayUrl { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>GUILD_MEMBER_ADD 事件数据 = GuildMember + guild_id。</summary>
public sealed record DcGuildMemberAdd
{
    [JsonPropertyName("guild_id")]
    public required string GuildId { get; init; }

    [JsonPropertyName("user")]
    public DcUser? User { get; init; }

    [JsonPropertyName("nick")]
    public string? Nick { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>GUILD_MEMBER_REMOVE 事件数据。</summary>
public sealed record DcGuildMemberRemove
{
    [JsonPropertyName("guild_id")]
    public required string GuildId { get; init; }

    [JsonPropertyName("user")]
    public required DcUser User { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>MESSAGE_DELETE 事件数据。</summary>
public sealed record DcMessageDelete
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("channel_id")]
    public required string ChannelId { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

/// <summary>MESSAGE_REACTION_ADD / REMOVE 事件数据(子集)。</summary>
public sealed record DcMessageReaction
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("channel_id")]
    public required string ChannelId { get; init; }

    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    [JsonPropertyName("emoji")]
    public required DcReactionEmoji Emoji { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}

public sealed record DcReactionEmoji
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>未显式建模的平台字段，原样保留。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; init; }
}
