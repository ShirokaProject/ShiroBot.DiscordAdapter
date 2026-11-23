using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.Model.Discord;
using static ShiroBot.DiscordAdapter.DiscordModelConverter;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordEventService(DiscordMapper mapper, Func<DiscordRestClient?>? getRest = null,
    Func<DiscordInteractionService?>? getInteractions = null) : IEventService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public event Func<BotEvent, Task>? EventReceived;
    public void Attach(DiscordGateway gateway) => gateway.DispatchReceived += OnDispatchAsync;
    public void Detach(DiscordGateway gateway) => gateway.DispatchReceived -= OnDispatchAsync;

    public async Task PublishAsync(BotEvent botEvent)
    {
        if (EventReceived is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<BotEvent, Task>>()) await handler(botEvent);
    }

    private Channel Channel(string id, string? guildId = null) => mapper.Channels.TryGet(id, out var known)
        ? DiscordMapper.ToChannel(known) : new(id, guildId is null ? ChannelType.Direct : ChannelType.Group) { GuildId = guildId };
    private static string? String(JsonElement data, string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task OnDispatchAsync(string eventName, JsonElement data)
    {
        if (eventName is "CHANNEL_CREATE" or "CHANNEL_UPDATE" or "THREAD_CREATE" or "THREAD_UPDATE")
        {
            var channel = data.Deserialize<DcChannel>(JsonOptions)!;
            mapper.Channels.Set(channel.Id, channel);
            if (channel.IsDm) mapper.KnownDmChannels.TryAdd(channel.Id, 0);
        }
        if (eventName == "GUILD_CREATE")
        {
            foreach (var key in new[] { "channels", "threads" })
                if (data.TryGetProperty(key, out var list))
                    foreach (var item in list.EnumerateArray())
                    {
                        var channel = item.Deserialize<DcChannel>(JsonOptions)!;
                        mapper.Channels.Set(channel.Id, channel with { GuildId = channel.GuildId ?? String(data, "id") });
                    }
        }
        if (eventName is "MESSAGE_CREATE" or "MESSAGE_UPDATE" && String(data, "guild_id") is not null
            && String(data, "channel_id") is { } id && !mapper.Channels.TryGet(id, out _) && getRest?.Invoke() is { } rest)
            mapper.Channels.Set(id, await rest.GetAsync<DcChannel>($"channels/{id}"));

        if (eventName == "MESSAGE_DELETE_BULK")
        {
            foreach (var idValue in data.GetProperty("ids").EnumerateArray())
                await PublishAsync(new MessageDeletedEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId,
                    MessageId = idValue.GetString()!, Channel = Channel(String(data, "channel_id")!, String(data, "guild_id")), Raw = data.Clone() });
            return;
        }
        if (eventName == "INTERACTION_CREATE" && data.TryGetProperty("channel_id", out var interactionChannel))
        {
            var channel = Channel(interactionChannel.GetString()!, String(data, "guild_id"));
            var service = getInteractions?.Invoke();
            if (service is not null) await service.RegisterAsync(data, channel);
            var userData = data.TryGetProperty("member", out var member) ? member.GetProperty("user") : data.GetProperty("user");
            var user = userData.Deserialize<DcUser>(JsonOptions)!;
            var custom = data.TryGetProperty("data", out var payload) ? String(payload, "custom_id") : null;
            var decoded = custom is null ? ((string?)null, (string?)null) : ButtonCodec.Decode(custom);
            var automatic = service?.AutoAcknowledge == true
                && !(data.TryGetProperty("type", out var interactionType) && interactionType.GetInt32() == 4);
            await PublishAsync(new InteractionEvent
            {
                Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = Interaction(data),
                Kind = custom is null ? "application_command" : "component_interaction", Channel = channel,
                InteractionId = String(data, "id")!, User = DiscordMapper.ToUser(user), ButtonId = decoded.Item1, Data = decoded.Item2,
                MessageId = data.TryGetProperty("message", out var message) ? String(message, "id") : null,
                Acknowledgement = automatic ? InteractionAcknowledgement.Acknowledged : InteractionAcknowledgement.Pending,
                IsAutomaticallyAcknowledged = automatic, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            });
            return;
        }
        var botEvent = Map(eventName, data);
        if (botEvent is not null) await PublishAsync(botEvent);
    }

    /// <summary>DiscordEventKinds 中列出的事件转换为 Model 负载，其他事件保留原始 JSON。</summary>
    private object Payload(string kind, JsonElement data, Channel? channel)
    {
        var guildId = OptionalId(data, "guild_id");
        ulong Guild() => guildId ?? throw new JsonException("事件缺少 guild_id。");
        return kind switch
        {
            DiscordEventKinds.GuildCreate => new DiscordGuildAvailable
            {
                GuildId = OptionalId(data, "id"), Json = data, Guild = DiscordModelConverter.Guild(data),
                JoinedAt = DateTimeOffset.TryParse(String(data, "joined_at"), out var joined) ? joined : null,
                MemberCount = data.TryGetProperty("member_count", out var count) && count.TryGetInt32(out var members) ? members : 0,
                Channels = Items(data, "channels").Select(c => DiscordModelConverter.Channel(c, OptionalId(data, "id"))).ToArray(),
                Roles = Items(data, "roles").Select(Role).ToArray()
            },
            DiscordEventKinds.GuildDelete => new DiscordGuildUnavailable
            {
                GuildId = OptionalId(data, "id"), Json = data,
                IsOutage = data.TryGetProperty("unavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True
            },
            DiscordEventKinds.ChannelCreate or DiscordEventKinds.ChannelUpdate or DiscordEventKinds.ChannelDelete
                or DiscordEventKinds.ThreadCreate or DiscordEventKinds.ThreadUpdate or DiscordEventKinds.ThreadDelete =>
                new DiscordChannelChanged { GuildId = guildId, Json = data, Channel = DiscordModelConverter.Channel(data) },
            DiscordEventKinds.GuildMemberUpdate => new DiscordMemberUpdated { GuildId = guildId, Json = data, Member = Member(data, Guild()) },
            DiscordEventKinds.GuildRoleCreate or DiscordEventKinds.GuildRoleUpdate => new DiscordRoleChanged
            {
                GuildId = guildId, Json = data, RoleId = Id(data.GetProperty("role"), "id"), Role = Role(data.GetProperty("role"))
            },
            DiscordEventKinds.GuildRoleDelete => new DiscordRoleChanged { GuildId = guildId, Json = data, RoleId = Id(data, "role_id") },
            DiscordEventKinds.GuildBanAdd or DiscordEventKinds.GuildBanRemove =>
                new DiscordBanChanged { GuildId = guildId, Json = data, User = User(data.GetProperty("user")) },
            DiscordEventKinds.VoiceStateUpdate => new DiscordVoiceStateUpdated
            {
                GuildId = guildId, Json = data, UserId = Id(data, "user_id"), ChannelId = OptionalId(data, "channel_id"),
                Member = guildId is { } voiceGuild && data.TryGetProperty("member", out var voiceMember) ? Member(voiceMember, voiceGuild) : null,
                IsMuted = Flag(data, "mute"), IsDeafened = Flag(data, "deaf"), IsSelfMuted = Flag(data, "self_mute"),
                IsSelfDeafened = Flag(data, "self_deaf"), IsStreaming = Flag(data, "self_stream"), IsVideoOn = Flag(data, "self_video")
            },
            DiscordEventKinds.TypingStart => new DiscordTypingStarted
            {
                GuildId = guildId, Json = data, ChannelId = Id(data, "channel_id"), UserId = Id(data, "user_id"),
                Timestamp = data.TryGetProperty("timestamp", out var at) ? DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()) : default
            },
            DiscordEventKinds.MessageReactionRemoveAll or DiscordEventKinds.MessageReactionRemoveEmoji => new DiscordReactionsCleared
            {
                GuildId = guildId, Json = data, ChannelId = Id(data, "channel_id"), MessageId = Id(data, "message_id"),
                Emoji = data.TryGetProperty("emoji", out var emoji)
                    ? String(emoji, "id") is { } emojiId ? String(emoji, "name") + ":" + emojiId : String(emoji, "name") : null
            },
            DiscordEventKinds.MessagePollVoteAdd or DiscordEventKinds.MessagePollVoteRemove => new DiscordPollVote
            {
                GuildId = guildId, Json = data, UserId = Id(data, "user_id"), ChannelId = Id(data, "channel_id"),
                MessageId = Id(data, "message_id"), AnswerId = data.GetProperty("answer_id").GetInt32(),
                IsAdded = kind == DiscordEventKinds.MessagePollVoteAdd
            },
            DiscordEventKinds.InviteCreate => new DiscordInviteChanged
                { GuildId = guildId, Json = data, Code = String(data, "code")!, ChannelId = Id(data, "channel_id"), Invite = Invite(data) },
            DiscordEventKinds.InviteDelete => new DiscordInviteChanged
                { GuildId = guildId, Json = data, Code = String(data, "code")!, ChannelId = Id(data, "channel_id") },
            _ => data
        };
    }

    private static bool Flag(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Items(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private BotEvent? Map(string eventName, JsonElement data)
    {
        switch (eventName)
        {
            case "READY":
                mapper.SelfId = data.Deserialize<DcReady>(JsonOptions)!.User.Id;
                if (data.TryGetProperty("application", out var application)) mapper.ApplicationId = String(application, "id");
                return null;
            case "MESSAGE_UPDATE" when data.TryGetProperty("author", out _):
            {
                var message = data.Deserialize<DcMessage>(JsonOptions)!;
                if (message.Author?.Id == mapper.SelfId) return null;
                var mapped = mapper.ToMessageEvent(message, data.Clone());
                return new MessageEditedEvent
                {
                    Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = mapped.Raw,
                    MessageId = mapped.MessageId, Channel = mapped.Channel, Sender = mapped.Sender, Member = mapped.Member,
                    Segments = mapped.Segments,
                    EditedAt = DateTimeOffset.TryParse(String(data, "edited_timestamp"), out var editedAt) ? editedAt : DateTimeOffset.UtcNow
                };
            }
            case "MESSAGE_CREATE":
            {
                var message = data.Deserialize<DcMessage>(JsonOptions)!;
                return message.Author?.Id == mapper.SelfId ? null : mapper.ToMessageEvent(message, data.Clone());
            }
            case "MESSAGE_DELETE":
            {
                var deleted = data.Deserialize<DcMessageDelete>(JsonOptions)!;
                return new MessageDeletedEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = deleted,
                    MessageId = deleted.Id, Channel = Channel(deleted.ChannelId, deleted.GuildId) };
            }
            case "GUILD_MEMBER_ADD":
            {
                var added = data.Deserialize<DcGuildMemberAdd>(JsonOptions)!;
                return new MemberJoinedEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = added,
                    Channel = new(added.GuildId, ChannelType.Group) { GuildId = added.GuildId }, UserId = added.User!.Id };
            }
            case "GUILD_MEMBER_REMOVE":
            {
                var removed = data.Deserialize<DcGuildMemberRemove>(JsonOptions)!;
                return new MemberLeftEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = removed,
                    Channel = new(removed.GuildId, ChannelType.Group) { GuildId = removed.GuildId }, UserId = removed.User.Id };
            }
            case "MESSAGE_REACTION_ADD":
            case "MESSAGE_REACTION_REMOVE":
            {
                var reaction = data.Deserialize<DcMessageReaction>(JsonOptions)!;
                return new MessageReactionEvent
                {
                    Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = reaction,
                    Kind = eventName == "MESSAGE_REACTION_ADD" ? "reaction_add" : "reaction_remove",
                    Channel = Channel(reaction.ChannelId, reaction.GuildId), MessageId = reaction.MessageId,
                    User = new(reaction.UserId), IsAdded = eventName == "MESSAGE_REACTION_ADD",
                    Emoji = reaction.Emoji.Id is { } emojiId
                        ? new PlatformReactionEmoji($"{reaction.Emoji.Name}:{emojiId}", "") { GuildId = reaction.GuildId }
                        : new UnicodeReactionEmoji(reaction.Emoji.Name ?? "")
                };
            }
            default:
            {
                // 只更新 Embed 等的部分 MESSAGE_UPDATE 没有作者和正文，保留为 message_update 平台事件。
                var kind = eventName.ToLowerInvariant();
                var channel = String(data, "channel_id") is { } id ? Channel(id, String(data, "guild_id"))
                    : eventName.StartsWith("CHANNEL_", StringComparison.Ordinal) || eventName.StartsWith("THREAD_", StringComparison.Ordinal)
                        ? Channel(String(data, "id")!, String(data, "guild_id")) : null;
                return new PlatformEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Kind = kind,
                    Raw = Payload(kind, data.Clone(), channel), Channel = channel };
            }
        }
    }
}
