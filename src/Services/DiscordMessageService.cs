using System.Text;
using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordMessageService(DiscordRestClient rest, DiscordMapper mapper,
    DiscordInteractionService? interactions = null) : IMessageService, IFileService
{
    private sealed record Plan(string Content, string? Reply, List<DcEmbed> Embeds, List<ResourceSegment> Files, object[] Components, List<string> Stickers);

    public MessageCapabilities GetMessageCapabilities(Sdk.Channel channel) => new()
    {
        NativeFeatures = MessageFeatures.Text | MessageFeatures.Markdown | MessageFeatures.Card | MessageFeatures.Image
            | MessageFeatures.Audio | MessageFeatures.Video | MessageFeatures.File | MessageFeatures.Mention | MessageFeatures.MentionAll
            | MessageFeatures.Emoji | MessageFeatures.Quote | MessageFeatures.Raw | MessageFeatures.LinkButtons | MessageFeatures.CallbackButtons
            | (interactions is null ? MessageFeatures.None : MessageFeatures.InteractionReply),
        CanMixMarkdown = true, CanCombineButtonsWithMedia = true,
        MaxTextLength = 2000, MaxMediaSegments = 10, MaxButtonRows = 5, MaxButtonsPerRow = 5, MaxCallbackDataBytes = 100
    };

    private (PreparedMessage Prepared, Plan Plan) Prepare(Sdk.Channel channel, OutgoingMessage message)
    {
        if (message.ReplyTo is { } reply && reply.Channel.Id != channel.Id) throw new ArgumentException("引用来源频道不匹配。");
        if (message.ReplyToInteraction is { } interaction && interaction.Channel.Id != channel.Id) throw new ArgumentException("交互来源频道不匹配。");
        var prepared = MessagePreparation.Prepare(message, GetMessageCapabilities(channel));
        var content = new StringBuilder();
        string? quote = null;
        var embeds = new List<DcEmbed>();
        var files = new List<ResourceSegment>();
        var stickers = new List<string>();
        foreach (var segment in prepared.Message.Segments)
        {
            switch (segment)
            {
                case TextSegment text: content.Append(text.Text); break;
                case MarkdownSegment markdown: content.Append(markdown.Content); break;
                case MentionSegment mention:
                    if (!ulong.TryParse(mention.UserId, out _)) throw new ArgumentException("Discord 用户 ID 必须为数字。");
                    content.Append("<@").Append(mention.UserId).Append('>'); break;
                case MentionAllSegment: content.Append("@everyone"); break;
                case QuoteSegment quoteSegment: quote = quoteSegment.MessageId; break;
                case EmojiSegment emoji:
                    content.Append(ulong.TryParse(emoji.Id, out _) ? $"<:{emoji.Name?.Trim(':') ?? "emoji"}:{emoji.Id}>" : emoji.Name ?? emoji.Id); break;
                case CardSegment card:
                    embeds.Add(new DcEmbed { Title = card.Title, Description = card.Description, Url = card.Url,
                        Image = card.ImageUrl is null ? null : new(card.ImageUrl), Fields = card.Fields.Select(f => new DcEmbedField(f.Name, f.Value)).ToArray() });
                    break;
                case RawSegment { Platform: DiscordMapper.PlatformId, Kind: "embed", Payload: DcEmbed embed }: embeds.Add(embed); break;
                case RawSegment { Platform: DiscordMapper.PlatformId, Kind: "embed", Payload: JsonElement raw }:
                    embeds.Add(raw.Deserialize<DcEmbed>() ?? throw new ArgumentException("Embed 不能为空。")); break;
                case RawSegment { Platform: DiscordMapper.PlatformId, Kind: "sticker", Payload: DcStickerItem sticker }:
                    stickers.Add(sticker.Id); break;
                case ResourceSegment resource:
                    if (resource is VideoSegment { ThumbnailUri: not null })
                        throw new NotSupportedException("Discord 消息附件 API 不提供视频自定义封面参数。");
                    ValidateResource(resource, channel); files.Add(resource); break;
                default: throw new NotSupportedException($"Discord 不支持消息段 {segment.GetType().Name}。");
            }
        }
        if (stickers.Count > 3) throw new NotSupportedException("Discord 每条消息最多发送三个贴纸。");
        if (content.Length > 2000 || embeds.Count > 10) throw new NotSupportedException("Discord 单条消息超出正文或 Embed 数量限制。");
        var embedLength = 0;
        foreach (var embed in embeds)
        {
            if (embed.Title?.Length > 256 || embed.Description?.Length > 4096 || embed.Fields?.Count > 25
                || embed.Fields?.Any(f => f.Name.Length > 256 || f.Value.Length > 1024) == true)
                throw new NotSupportedException("Discord Embed 字段超出平台限制。");
            embedLength += (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0)
                + (embed.Fields?.Sum(f => f.Name.Length + f.Value.Length) ?? 0);
        }
        if (embedLength > 6000) throw new NotSupportedException("Discord Embed 总文本超过 6000 字符。");
        if (quote is not null && !ulong.TryParse(quote, out _)) throw new ArgumentException("引用消息 ID 必须为数字。");
        var components = prepared.Message.Buttons?.Rows.Select(row => (object)new
        {
            type = 1, components = row.Buttons.Select(button =>
            {
                if (button.Label.Length > 80) throw new NotSupportedException("Discord 按钮标签最多 80 字符。");
                if (button.Action is OpenUrlAction url)
                    return (object)new { type = 2, style = 5, label = button.Label, url = url.Url };
                var callback = (CallbackAction)button.Action;
                var id = ButtonCodec.Encode(button.Id, callback.Data);
                if (Encoding.UTF8.GetByteCount(id) > 100) throw new NotSupportedException("Discord 编码后的按钮 ID 和回调数据超过 100 字节。");
                return new { type = 2, style = 1, label = button.Label, custom_id = id };
            }).ToArray()
        }).ToArray() ?? [];
        return (prepared, new(content.ToString(), quote, embeds, files, components, stickers));
    }

    public MessageSendAssessment AssessMessage(Sdk.Channel channel, OutgoingMessage message)
    {
        try { var (prepared, _) = Prepare(channel, message); return new() { IsSupported = true, Transformations = prepared.Transformations }; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return new() { Issues = [ex.Message] }; }
    }

    public Task<SentMessage> SendMessageAsync(Sdk.Channel channel, IReadOnlyList<MessageSegment> segments, CancellationToken cancellationToken = default) =>
        SendMessageAsync(channel, new OutgoingMessage { Segments = segments }, cancellationToken);

    public async Task<SentMessage> SendMessageAsync(Sdk.Channel channel, OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (prepared, plan) = Prepare(channel, message);
        var files = new List<ResourceBytes>();
        foreach (var resource in plan.Files)
            files.Add(await ResourceReader.ReadAsync(ResolveUploadUri(resource.Uri, channel), resource.FileName, cancellationToken));
        var payload = new
        {
            content = plan.Content, embeds = plan.Embeds, components = plan.Components,
            sticker_ids = plan.Stickers.Count == 0 ? null : plan.Stickers,
            attachments = files.Select((file, index) => new { id = index, filename = file.FileName,
                description = plan.Files[index] is ImageSegment image ? image.Summary : null }).ToArray(),
            message_reference = plan.Reply is null ? null : new { message_id = plan.Reply, fail_if_not_exists = false }
        };
        var sent = prepared.Message.ReplyToInteraction is { } interaction
            ? await (interactions ?? throw new NotSupportedException("交互服务未启动。")).ReplyAsync(interaction, payload, files, cancellationToken)
            : await rest.SendPayloadAsync<DcMessage>(HttpMethod.Post, $"channels/{await ResolveChannelIdAsync(channel, cancellationToken)}/messages", payload, files, cancellationToken);
        foreach (var attachment in sent.Attachments ?? [])
            mapper.Resources.Set(attachment.Id, (sent.ChannelId, sent.Id, attachment.Url));
        return new(sent.Id) { Timestamp = sent.Timestamp, Transformations = prepared.Transformations };
    }

    public async Task DeleteMessageAsync(Sdk.Channel channel, string messageId, CancellationToken cancellationToken = default) =>
        await rest.DeleteAsync($"channels/{await ResolveChannelIdAsync(channel, cancellationToken)}/messages/{messageId}", cancellationToken);

    public async Task<MessageEvent?> GetMessageAsync(Sdk.Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        var id = await ResolveChannelIdAsync(channel, cancellationToken);
        var message = await rest.GetAsync<DcMessage>($"channels/{id}/messages/{messageId}", cancellationToken);
        await RememberChannelAsync(id, channel, cancellationToken);
        return mapper.ToMessageEvent(message);
    }

    public async Task<IReadOnlyList<MessageEvent>> GetHistoryMessagesAsync(Sdk.Channel channel, string? beforeMessageId = null,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var id = await ResolveChannelIdAsync(channel, cancellationToken);
        var query = $"channels/{id}/messages?limit={Math.Clamp(limit, 1, 100)}";
        if (beforeMessageId is not null) query += "&before=" + Uri.EscapeDataString(beforeMessageId);
        var messages = await rest.GetAsync<IReadOnlyList<DcMessage>>(query, cancellationToken);
        await RememberChannelAsync(id, channel, cancellationToken);
        return messages.Select(m => mapper.ToMessageEvent(m)).ToArray();
    }

    private async Task RememberChannelAsync(string id, Sdk.Channel channel, CancellationToken token)
    {
        if (channel.Type == ChannelType.Direct || mapper.Channels.TryGet(id, out _)) return;
        mapper.Channels.Set(id, await rest.GetAsync<DcChannel>($"channels/{id}", token));
    }

    public async Task<string> GetResourceUrlAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Uri.TryCreate(resourceId, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.AbsoluteUri;
        if (mapper.Resources.TryGet(resourceId, out var resource))
        {
            var message = await rest.GetAsync<DcMessage>($"channels/{resource.ChannelId}/messages/{resource.MessageId}", cancellationToken);
            var attachment = message.Attachments?.FirstOrDefault(a => a.Id == resourceId)
                ?? throw new KeyNotFoundException("附件已删除或不可访问。");
            mapper.Resources.Set(resourceId, (resource.ChannelId, resource.MessageId, attachment.Url));
            return attachment.Url;
        }
        if (mapper.Uploads.TryGet(resourceId, out var upload)) return upload.Url;
        throw new KeyNotFoundException("附件 ID 不属于当前实例，或其来源已不在缓存中；可直接使用入站资源段的 Uri。");
    }

    public FileCapabilities GetFileCapabilities(Sdk.Channel channel) => new() { CanUpload = true, UploadPublishes = true };

    public async Task<FileUploadResult> UploadAsync(Sdk.Channel channel, FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        var file = await ResourceReader.ReadAsync(ResolveUploadUri(request.Uri, channel), request.FileName, cancellationToken);
        var sent = await rest.SendPayloadAsync<DcMessage>(HttpMethod.Post,
            $"channels/{await ResolveChannelIdAsync(channel, cancellationToken)}/messages",
            new { attachments = new[] { new { id = 0, filename = file.FileName } } }, [file], cancellationToken);
        var attachment = sent.Attachments?.FirstOrDefault() ?? throw new InvalidOperationException("Discord 未返回上传后的附件凭据。");
        var handle = "dcfile://" + Guid.NewGuid().ToString("N");
        mapper.Uploads.Set(handle, (channel.Id, attachment.Url));
        mapper.Resources.Set(attachment.Id, (sent.ChannelId, sent.Id, attachment.Url));
        return new() { FileId = handle, IsPublished = true };
    }

    private void ValidateResource(ResourceSegment resource, Sdk.Channel channel) => ResourceReader.Validate(ResolveUploadUri(resource.Uri, channel));
    private string ResolveUploadUri(string uri, Sdk.Channel channel)
    {
        if (!uri.StartsWith("dcfile://", StringComparison.Ordinal)) return uri;
        if (!mapper.Uploads.TryGet(uri, out var upload) || upload.ChannelId != channel.Id)
            throw new NotSupportedException("上传凭据不属于当前实例和频道，或已过期。");
        return upload.Url;
    }

    private async Task<string> ResolveChannelIdAsync(Sdk.Channel channel, CancellationToken token)
    {
        if (channel.Type != ChannelType.Direct || mapper.KnownDmChannels.ContainsKey(channel.Id)) return channel.Id;
        var dm = await rest.PostAsync<DcChannel>("users/@me/channels", new { recipient_id = channel.Id }, token);
        mapper.KnownDmChannels.TryAdd(dm.Id, 0);
        mapper.Channels.Set(dm.Id, dm);
        return dm.Id;
    }
}
