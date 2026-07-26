using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordEventService : IEventService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public event Func<BotEvent, Task>? EventReceived;

    public void Attach(DiscordGateway gateway)
    {
        gateway.DispatchReceived += OnDispatchAsync;
    }

    private async Task OnDispatchAsync(string eventName, JsonElement data)
    {
        if (EventReceived is null)
        {
            return;
        }

        var botEvent = Map(eventName, data);
        if (botEvent is not null)
        {
            await EventReceived(botEvent);
        }
    }

    private static BotEvent? Map(string eventName, JsonElement data)
    {
        switch (eventName)
        {
            case "READY":
            {
                var ready = data.Deserialize<DcReady>(JsonOptions)!;
                DiscordMapper.SelfId = ready.User.Id;
                return null;
            }

            case "MESSAGE_CREATE":
            {
                var message = data.Deserialize<DcMessage>(JsonOptions)!;
                // 忽略自己发的消息
                if (message.Author?.Id == DiscordMapper.SelfId)
                {
                    return null;
                }

                if (message.GuildId is null)
                {
                    DiscordMapper.KnownDmChannels.Add(message.ChannelId);
                }

                return DiscordMapper.ToMessageEvent(message);
            }

            case "MESSAGE_DELETE":
            {
                var deleted = data.Deserialize<DcMessageDelete>(JsonOptions)!;
                return new MessageDeletedEvent
                {
                    Platform = DiscordMapper.PlatformId,
                    SelfId = DiscordMapper.SelfId,
                    Raw = deleted,
                    MessageId = deleted.Id,
                    Channel = deleted.GuildId is null
                        ? new Channel(deleted.ChannelId, ChannelType.Direct)
                        : new Channel(deleted.ChannelId, ChannelType.Group) { GuildId = deleted.GuildId }
                };
            }

            case "GUILD_MEMBER_ADD":
            {
                var added = data.Deserialize<DcGuildMemberAdd>(JsonOptions)!;
                return new MemberJoinedEvent
                {
                    Platform = DiscordMapper.PlatformId,
                    SelfId = DiscordMapper.SelfId,
                    Raw = added,
                    Channel = new Channel(added.GuildId, ChannelType.Group) { GuildId = added.GuildId },
                    UserId = added.User?.Id ?? "0"
                };
            }

            case "GUILD_MEMBER_REMOVE":
            {
                var removed = data.Deserialize<DcGuildMemberRemove>(JsonOptions)!;
                return new MemberLeftEvent
                {
                    Platform = DiscordMapper.PlatformId,
                    SelfId = DiscordMapper.SelfId,
                    Raw = removed,
                    Channel = new Channel(removed.GuildId, ChannelType.Group) { GuildId = removed.GuildId },
                    UserId = removed.User.Id
                };
            }

            case "MESSAGE_REACTION_ADD":
            case "MESSAGE_REACTION_REMOVE":
            {
                var reaction = data.Deserialize<DcMessageReaction>(JsonOptions)!;
                return new PlatformEvent
                {
                    Platform = DiscordMapper.PlatformId,
                    SelfId = DiscordMapper.SelfId,
                    Raw = reaction,
                    Kind = eventName == "MESSAGE_REACTION_ADD" ? "reaction_add" : "reaction_remove",
                    Channel = new Channel(reaction.ChannelId, reaction.GuildId is null ? ChannelType.Direct : ChannelType.Group)
                    {
                        GuildId = reaction.GuildId
                    }
                };
            }

            case "MESSAGE_UPDATE":
            {
                var message = data.Deserialize<DcMessage>(JsonOptions)!;
                return new PlatformEvent
                {
                    Platform = DiscordMapper.PlatformId,
                    SelfId = DiscordMapper.SelfId,
                    Raw = message,
                    Kind = "message_edited",
                    Channel = new Channel(message.ChannelId, message.GuildId is null ? ChannelType.Direct : ChannelType.Group)
                    {
                        GuildId = message.GuildId
                    }
                };
            }

            default:
                return null;
        }
    }
}
