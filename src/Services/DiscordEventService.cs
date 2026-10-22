using System.Text.Json;
using ShiroBot.Discord.Model;
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
            await PublishAsync(new InteractionEvent
            {
                Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId, Raw = data.Clone(),
                Kind = custom is null ? "application_command" : "component_interaction", Channel = channel,
                InteractionId = String(data, "id")!, User = DiscordMapper.ToUser(user), ButtonId = decoded.Item1, Data = decoded.Item2,
                MessageId = data.TryGetProperty("message", out var message) ? String(message, "id") : null,
                Acknowledgement = service is null ? InteractionAcknowledgement.Pending : InteractionAcknowledgement.Acknowledged,
                IsAutomaticallyAcknowledged = service is not null, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            });
            return;
        }
        var botEvent = Map(eventName, data);
        if (botEvent is not null) await PublishAsync(botEvent);
    }

    private BotEvent? Map(string eventName, JsonElement data)
    {
        switch (eventName)
        {
            case "READY":
                mapper.SelfId = data.Deserialize<DcReady>(JsonOptions)!.User.Id;
                return null;
            case "MESSAGE_CREATE":
            {
                var message = data.Deserialize<DcMessage>(JsonOptions)!;
                return message.Author?.Id == mapper.SelfId ? null : mapper.ToMessageEvent(message);
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
                return new PlatformEvent { Platform = DiscordMapper.PlatformId, SelfId = mapper.SelfId,
                    Kind = eventName == "MESSAGE_UPDATE" ? "message_edited" : eventName.ToLowerInvariant(), Raw = data.Clone(),
                    Channel = String(data, "channel_id") is { } id ? Channel(id, String(data, "guild_id")) : null };
        }
    }
}
