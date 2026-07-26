using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordMessageService(DiscordRestClient rest) : IMessageService
{
    public async Task<SentMessage> SendMessageAsync(Sdk.Channel channel, IReadOnlyList<MessageSegment> segments)
    {
        var channelId = await ResolveChannelIdAsync(channel);
        var plan = DiscordMapper.ToOutgoingPlan(segments);

        // 附件用 URL 形式转为 embed image / 附带链接文本;真正的文件上传需 multipart,先走 URL
        var content = plan.Content;
        var embeds = new List<DcEmbed>(plan.Embeds);
        foreach (var attachment in plan.Attachments)
        {
            if (attachment is ImageSegment image)
            {
                embeds.Add(new DcEmbed { Url = image.Uri, Title = image.Summary });
                content += (content.Length > 0 ? "\n" : string.Empty) + image.Uri;
            }
            else
            {
                content += (content.Length > 0 ? "\n" : string.Empty) + attachment.Uri;
            }
        }

        var message = await rest.PostAsync<DcMessage>($"channels/{channelId}/messages", new
        {
            content,
            embeds = embeds.Count == 0 ? null : embeds,
            message_reference = plan.ReplyToMessageId is null
                ? null
                : new { message_id = plan.ReplyToMessageId, fail_if_not_exists = false }
        });

        return new SentMessage(message.Id) { Timestamp = message.Timestamp };
    }

    public async Task DeleteMessageAsync(Sdk.Channel channel, string messageId)
    {
        var channelId = await ResolveChannelIdAsync(channel);
        await rest.DeleteAsync($"channels/{channelId}/messages/{messageId}");
    }

    public async Task<MessageEvent?> GetMessageAsync(Sdk.Channel channel, string messageId)
    {
        var channelId = await ResolveChannelIdAsync(channel);
        var message = await rest.GetAsync<DcMessage>($"channels/{channelId}/messages/{messageId}");
        return DiscordMapper.ToMessageEvent(message);
    }

    public async Task<IReadOnlyList<MessageEvent>> GetHistoryMessagesAsync(
        Sdk.Channel channel,
        string? beforeMessageId = null,
        int limit = 20)
    {
        var channelId = await ResolveChannelIdAsync(channel);
        var query = $"channels/{channelId}/messages?limit={Math.Clamp(limit, 1, 100)}";
        if (beforeMessageId is not null)
        {
            query += $"&before={beforeMessageId}";
        }

        var messages = await rest.GetAsync<IReadOnlyList<DcMessage>>(query);
        return messages.Select(DiscordMapper.ToMessageEvent).ToArray();
    }

    public Task<string> GetResourceUrlAsync(string resourceId) =>
        // Discord 附件 URL 即为可下载地址,入站段的 Uri 已可直接使用
        Task.FromResult(resourceId);

    /// <summary>
    /// Direct channel 的 Id 约定为对方用户 ID(宿主 Channel.Direct(userId) 生成),
    /// 需要先开 DM channel;如果 Id 已是 DM channel id(入站消息回复场景)则直接使用。
    /// </summary>
    private async Task<string> ResolveChannelIdAsync(Sdk.Channel channel)
    {
        if (channel.Type != ChannelType.Direct || DiscordMapper.KnownDmChannels.Contains(channel.Id))
        {
            return channel.Id;
        }

        var dm = await rest.PostAsync<DcChannel>("users/@me/channels", new { recipient_id = channel.Id });
        DiscordMapper.KnownDmChannels.Add(dm.Id);
        return dm.Id;
    }
}
