using System.Text.Json;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordReactionService(DiscordRestClient rest) : IMessageReactionService
{
    public ReactionCapabilities GetReactionCapabilities(Channel channel) => ReactionCapabilities.Unicode | ReactionCapabilities.PlatformEmoji;

    public async Task SetReactionAsync(MessageReference message, ReactionEmoji emoji, bool isAdd = true, CancellationToken cancellationToken = default)
    {
        string value;
        switch (emoji)
        {
            case UnicodeReactionEmoji unicode:
                ArgumentException.ThrowIfNullOrWhiteSpace(unicode.Value);
                value = unicode.Value;
                break;
            case PlatformReactionEmoji custom:
                if (!string.Equals(custom.InstanceId, message.InstanceId, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("平台表情不能跨实例使用。");
                value = custom.Id;
                if (!value.Contains(':'))
                {
                    var guild = custom.GuildId ?? message.Channel.GuildId
                        ?? throw new ArgumentException("使用纯数字表情 ID 时需要 GuildId。");
                    var native = await rest.GetAsync<JsonElement>($"guilds/{guild}/emojis/{custom.Id}", cancellationToken);
                    value = native.GetProperty("name").GetString() + ":" + custom.Id;
                }
                break;
            default: throw new NotSupportedException("未知 Reaction 类型。");
        }
        var path = $"channels/{message.Channel.Id}/messages/{message.MessageId}/reactions/{Uri.EscapeDataString(value)}/@me";
        if (isAdd) await rest.PutAsync(path, cancellationToken: cancellationToken);
        else await rest.DeleteAsync(path, cancellationToken);
    }
}
