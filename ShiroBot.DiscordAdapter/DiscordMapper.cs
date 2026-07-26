using System.Text;
using System.Text.RegularExpressions;
using ShiroBot.Discord.Model;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter;

/// <summary>Discord 模型 ↔ ShiroBot 核心模型映射。</summary>
public static partial class DiscordMapper
{
    public const string PlatformId = "discord";

    internal static string? SelfId { get; set; }

    /// <summary>已知 DM channel id 集合(Gateway 消息里 DM 无 guild_id,以此区分)。</summary>
    internal static readonly HashSet<string> KnownDmChannels = [];

    [GeneratedRegex("<@!?(\\d+)>")]
    private static partial Regex MentionPattern();

    // ─── Channel / User / Member ───

    public static Sdk.Channel ToChannel(DcChannel channel) => channel switch
    {
        { IsDm: true } => Sdk.Channel.Direct(channel.Id) with { Id = channel.Id, Type = ChannelType.Direct },
        { IsThread: true } => new Sdk.Channel(channel.Id, ChannelType.Thread)
        {
            Name = channel.Name,
            GuildId = channel.GuildId
        },
        _ => new Sdk.Channel(channel.Id, ChannelType.Group)
        {
            Name = channel.Name,
            GuildId = channel.GuildId
        }
    };

    public static Sdk.User ToUser(DcUser user) => new(user.Id)
    {
        Name = user.DisplayName,
        AvatarUrl = user.AvatarUrl,
        IsBot = user.IsBot
    };

    public static Member ToMember(DcGuildMember member, DcUser fallbackUser)
    {
        var user = member.User ?? fallbackUser;
        return new Member(ToUser(user))
        {
            Nick = member.Nick,
            JoinedAt = member.JoinedAt
        };
    }

    // ─── 入站消息 ───

    public static MessageEvent ToMessageEvent(DcMessage message)
    {
        var author = message.Author ?? new DcUser { Id = "0", Username = "unknown" };
        var isDm = message.GuildId is null && KnownDmChannels.Contains(message.ChannelId);

        return new MessageEvent
        {
            Platform = PlatformId,
            SelfId = SelfId,
            Raw = message,
            MessageId = message.Id,
            Channel = isDm
                ? Sdk.Channel.Direct(author.Id) with { Id = message.ChannelId, Type = ChannelType.Direct }
                : new Sdk.Channel(message.ChannelId, ChannelType.Group) { GuildId = message.GuildId },
            Sender = ToUser(author),
            Member = message.Member is null ? null : ToMember(message.Member, author),
            Segments = ToSegments(message),
            Timestamp = message.Timestamp
        };
    }

    public static IReadOnlyList<MessageSegment> ToSegments(DcMessage message)
    {
        var segments = new List<MessageSegment>();

        if (message.MessageReference?.MessageId is { } refId)
        {
            segments.Add(new QuoteSegment(refId));
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            segments.AddRange(ParseContent(message.Content, message));
        }

        foreach (var attachment in message.Attachments ?? [])
        {
            segments.Add(ToSegment(attachment));
        }

        foreach (var embed in message.Embeds ?? [])
        {
            segments.Add(new RawSegment(PlatformId, "embed", embed));
        }

        foreach (var sticker in message.StickerItems ?? [])
        {
            segments.Add(new EmojiSegment(sticker.Id) { Name = sticker.Name });
        }

        return segments;
    }

    /// <summary>把 content 中的 &lt;@id&gt; / @everyone 解析为 Mention 段。</summary>
    private static IEnumerable<MessageSegment> ParseContent(string content, DcMessage message)
    {
        var matches = MentionPattern().Matches(content);
        var position = 0;

        foreach (Match match in matches)
        {
            if (match.Index > position)
            {
                yield return new TextSegment(content[position..match.Index]);
            }

            var userId = match.Groups[1].Value;
            var mentioned = message.Mentions?.FirstOrDefault(user => user.Id == userId);
            yield return new MentionSegment(userId) { DisplayName = mentioned?.DisplayName };
            position = match.Index + match.Length;
        }

        if (position < content.Length)
        {
            var rest = content[position..];
            if (message.MentionEveryone && rest.Contains("@everyone"))
            {
                var parts = rest.Split("@everyone", 2);
                if (parts[0].Length > 0) yield return new TextSegment(parts[0]);
                yield return new MentionAllSegment();
                if (parts[1].Length > 0) yield return new TextSegment(parts[1]);
            }
            else
            {
                yield return new TextSegment(rest);
            }
        }
    }

    private static MessageSegment ToSegment(DcAttachment attachment)
    {
        var contentType = attachment.ContentType ?? string.Empty;

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageSegment(attachment.Url)
            {
                ResourceId = attachment.Id,
                FileName = attachment.Filename,
                Width = attachment.Width,
                Height = attachment.Height
            };
        }

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoSegment(attachment.Url)
            {
                ResourceId = attachment.Id,
                FileName = attachment.Filename
            };
        }

        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return new AudioSegment(attachment.Url)
            {
                ResourceId = attachment.Id,
                FileName = attachment.Filename,
                Duration = attachment.DurationSecs is { } secs ? TimeSpan.FromSeconds(secs) : null
            };
        }

        return new Sdk.FileSegment(attachment.Url)
        {
            ResourceId = attachment.Id,
            FileName = attachment.Filename,
            FileSize = attachment.Size
        };
    }

    // ─── 出站 ───

    public sealed record OutgoingPlan(
        string Content,
        string? ReplyToMessageId,
        IReadOnlyList<DcEmbed> Embeds,
        IReadOnlyList<ResourceSegment> Attachments);

    public static OutgoingPlan ToOutgoingPlan(IReadOnlyList<MessageSegment> segments)
    {
        var content = new StringBuilder();
        string? replyTo = null;
        var embeds = new List<DcEmbed>();
        var attachments = new List<ResourceSegment>();

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    content.Append(text.Text);
                    break;
                case MentionSegment mention:
                    content.Append("<@").Append(mention.UserId).Append('>');
                    break;
                case MentionAllSegment:
                    content.Append("@everyone");
                    break;
                case QuoteSegment quote:
                    replyTo = quote.MessageId;
                    break;
                case EmojiSegment emoji:
                    content.Append(emoji.Name ?? string.Empty);
                    break;
                case RawSegment { Kind: "embed", Payload: DcEmbed embed }:
                    embeds.Add(embed);
                    break;
                case ResourceSegment resource when resource.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase):
                    attachments.Add(resource);
                    break;
                case ResourceSegment resource:
                    throw new NotSupportedException(
                        $"Discord 适配器暂只支持 http(s) URL 资源,实际为 '{resource.Uri}'。");
                case RawSegment raw:
                    throw new NotSupportedException($"Discord 适配器不支持发送 RawSegment(kind={raw.Kind})。");
            }
        }

        return new OutgoingPlan(content.ToString(), replyTo, embeds, attachments);
    }
}
