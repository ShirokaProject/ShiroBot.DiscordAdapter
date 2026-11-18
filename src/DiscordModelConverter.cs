using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Model.Discord;

namespace ShiroBot.DiscordAdapter;

/// <summary>Discord API JSON 与宿主共享的 <c>ShiroBot.Model.Discord</c> 契约之间的转换。</summary>
internal static class DiscordModelConverter
{
    // ─── JSON 读取 ───

    private static bool Has(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public static string? Str(JsonElement e, string name) =>
        Has(e, name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    public static ulong Id(JsonElement e, string name) => OptionalId(e, name) ?? 0;

    public static ulong? OptionalId(JsonElement e, string name) =>
        Str(e, name) is { } text && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static int Int(JsonElement e, string name) => Has(e, name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static int? OptionalInt(JsonElement e, string name) => Has(e, name, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static bool Bool(JsonElement e, string name) => Has(e, name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        Has(e, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static DiscordPermissions Permissions(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ? (DiscordPermissions)bits : DiscordPermissions.None;

    public static string PermissionsText(DiscordPermissions value) => ((ulong)value).ToString(CultureInfo.InvariantCulture);

    // ─── 实体 ───

    public static DiscordUser User(JsonElement user, string? nickname = null)
    {
        var id = Id(user, "id");
        var avatar = Str(user, "avatar");
        return new DiscordUser
        {
            UserId = id, Username = Str(user, "username") ?? id.ToString(CultureInfo.InvariantCulture),
            GlobalName = Str(user, "global_name"), Nickname = nickname, IsBot = Bool(user, "bot"),
            AvatarUrl = avatar is null ? null
                : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{(avatar.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png")}"
        };
    }

    public static DiscordGuild Guild(JsonElement guild)
    {
        var id = Id(guild, "id");
        var icon = Str(guild, "icon");
        return new DiscordGuild
        {
            GuildId = id, Name = Str(guild, "name") ?? string.Empty, OwnerId = Id(guild, "owner_id"),
            IconUrl = icon is null ? null : $"https://cdn.discordapp.com/icons/{id}/{icon}.png",
            Description = Str(guild, "description"), ApproximateMemberCount = OptionalInt(guild, "approximate_member_count"),
            ApproximatePresenceCount = OptionalInt(guild, "approximate_presence_count"),
            Features = Array(guild, "features").Select(f => f.GetString()!).ToArray(),
            PreferredLocale = Str(guild, "preferred_locale")
        };
    }

    public static DiscordChannel Channel(JsonElement channel, ulong? guildId = null)
    {
        Has(channel, "thread_metadata", out var thread);
        return new DiscordChannel
        {
            ChannelId = Id(channel, "id"), Type = (DiscordChannelType)Int(channel, "type"),
            GuildId = OptionalId(channel, "guild_id") ?? guildId, Name = Str(channel, "name"), Topic = Str(channel, "topic"),
            ParentId = OptionalId(channel, "parent_id"), Position = OptionalInt(channel, "position"), IsNsfw = Bool(channel, "nsfw"),
            RateLimitPerUser = OptionalInt(channel, "rate_limit_per_user") is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null,
            IsArchived = Bool(thread, "archived"), IsLocked = Bool(thread, "locked")
        };
    }

    public static DiscordRole Role(JsonElement role) => new()
    {
        RoleId = Id(role, "id"), Name = Str(role, "name") ?? string.Empty, Color = Int(role, "color"),
        Permissions = Permissions(Str(role, "permissions")), Position = Int(role, "position"), IsHoisted = Bool(role, "hoist"),
        IsMentionable = Bool(role, "mentionable"), IsManaged = Bool(role, "managed")
    };

    /// <summary>成员；提供服务器角色时按角色计算服务器级权限。</summary>
    public static DiscordMember Member(JsonElement member, ulong guildId, JsonElement? fallbackUser = null,
        ulong? ownerId = null, IReadOnlyList<DiscordRole>? roles = null)
    {
        var userJson = Has(member, "user", out var u) ? u : fallbackUser ?? default;
        var user = User(userJson, Str(member, "nick"));
        var roleIds = Array(member, "roles").Select(r => ulong.Parse(r.GetString()!, CultureInfo.InvariantCulture)).ToArray();
        var isOwner = ownerId == user.UserId;
        var permissions = Permissions(Str(member, "permissions"));
        if (roles is not null)
        {
            permissions = roles.Where(r => r.RoleId == guildId || roleIds.Contains(r.RoleId))
                .Aggregate(DiscordPermissions.None, (all, role) => all | role.Permissions);
            if (isOwner || permissions.HasFlag(DiscordPermissions.Administrator)) permissions = (DiscordPermissions)ulong.MaxValue;
        }

        return new DiscordMember
        {
            User = user, GuildId = guildId, Nickname = Str(member, "nick"), RoleIds = roleIds,
            JoinedAt = Time(member, "joined_at"), TimeoutUntil = Time(member, "communication_disabled_until"),
            IsPending = Bool(member, "pending"), IsOwner = isOwner, Permissions = permissions
        };
    }

    public static DiscordEmoji Emoji(JsonElement emoji) => new()
    {
        EmojiId = Id(emoji, "id"), Name = Str(emoji, "name") ?? string.Empty, IsAnimated = Bool(emoji, "animated"),
        IsAvailable = !Has(emoji, "available", out var available) || available.ValueKind == JsonValueKind.True
    };

    public static DiscordInvite Invite(JsonElement invite)
    {
        Has(invite, "guild", out var guild);
        Has(invite, "channel", out var channel);
        var created = Time(invite, "created_at");
        var maxAge = OptionalInt(invite, "max_age");
        return new DiscordInvite
        {
            Code = Str(invite, "code") ?? string.Empty, GuildId = OptionalId(guild, "id") ?? OptionalId(invite, "guild_id"),
            ChannelId = OptionalId(channel, "id") ?? OptionalId(invite, "channel_id"),
            Inviter = Has(invite, "inviter", out var inviter) ? User(inviter) : null,
            ExpiresAt = Time(invite, "expires_at") ?? (created is { } c && maxAge is > 0 ? c.AddSeconds(maxAge.Value) : null),
            Uses = OptionalInt(invite, "uses"), MaxUses = OptionalInt(invite, "max_uses"), IsTemporary = Bool(invite, "temporary")
        };
    }

    // ─── 消息 ───

    public static DiscordMessage Message(JsonElement message)
    {
        var guildId = OptionalId(message, "guild_id");
        Has(message, "author", out var author);
        return new DiscordMessage
        {
            MessageId = Id(message, "id"), ChannelId = Id(message, "channel_id"), GuildId = guildId,
            Author = author.ValueKind == JsonValueKind.Object ? User(author) : null,
            Member = guildId is { } guild && Has(message, "member", out var member) ? Member(member, guild, author) : null,
            Timestamp = Time(message, "timestamp") ?? default, EditedTimestamp = Time(message, "edited_timestamp"),
            Type = Int(message, "type"), Flags = (DiscordMessageFlags)Int(message, "flags"), IsPinned = Bool(message, "pinned"),
            IsTts = Bool(message, "tts"), WebhookId = OptionalId(message, "webhook_id"),
            InteractionUserId = Has(message, "interaction_metadata", out var interaction) && Has(interaction, "user", out var user)
                ? OptionalId(user, "id") : null,
            MentionedRoleIds = Array(message, "mention_roles").Select(r => ulong.Parse(r.GetString()!, CultureInfo.InvariantCulture)).ToArray(),
            ThreadId = Has(message, "thread", out var thread) ? OptionalId(thread, "id") : null,
            Json = message.Clone()
        };
    }

    public static DiscordEmbed Embed(JsonElement embed) => new()
    {
        Title = Str(embed, "title"), Description = Str(embed, "description"), Url = Str(embed, "url"),
        Timestamp = Time(embed, "timestamp"), Color = OptionalInt(embed, "color"),
        Footer = Has(embed, "footer", out var footer) ? new DiscordEmbedFooter(Str(footer, "text") ?? "") { IconUrl = Str(footer, "icon_url") } : null,
        ImageUrl = Has(embed, "image", out var image) ? Str(image, "url") : null,
        ThumbnailUrl = Has(embed, "thumbnail", out var thumbnail) ? Str(thumbnail, "url") : null,
        Author = Has(embed, "author", out var a) ? new DiscordEmbedAuthor(Str(a, "name") ?? "") { Url = Str(a, "url"), IconUrl = Str(a, "icon_url") } : null,
        Fields = Array(embed, "fields").Select(f => new DiscordEmbedField(Str(f, "name") ?? "", Str(f, "value") ?? "", Bool(f, "inline"))).ToArray()
    };

    public static DiscordPoll Poll(JsonElement poll)
    {
        Has(poll, "question", out var question);
        var counts = Has(poll, "results", out var results)
            ? Array(results, "answer_counts").ToDictionary(c => Int(c, "id"), c => (Int(c, "count"), Bool(c, "me_voted")))
            : [];
        return new DiscordPoll
        {
            Question = Str(question, "text") ?? string.Empty,
            Answers = Array(poll, "answers").Select(answer =>
            {
                var id = Int(answer, "answer_id");
                Has(answer, "poll_media", out var media);
                var (count, me) = counts.GetValueOrDefault(id);
                return new DiscordPollAnswer(id, Str(media, "text") ?? string.Empty, count, me);
            }).ToArray(),
            Expiry = Time(poll, "expiry"), AllowMultiselect = Bool(poll, "allow_multiselect"),
            IsFinalized = Has(poll, "results", out var finalResults) && Bool(finalResults, "is_finalized")
        };
    }

    public static DiscordSentMessage Sent(JsonElement message, SDK.Models.Channel channel) => new()
    {
        MessageId = Str(message, "id") ?? string.Empty, Channel = channel, Timestamp = Time(message, "timestamp") ?? default,
        AttachmentUrls = Array(message, "attachments").Select(a => Str(a, "url")!).Where(url => url is not null).ToArray()
    };

    // ─── 交互 ───

    public static DiscordInteraction Interaction(JsonElement data)
    {
        var guildId = OptionalId(data, "guild_id");
        Has(data, "data", out var payload);
        var hasMember = Has(data, "member", out var member);
        var userJson = hasMember && Has(member, "user", out var memberUser) ? memberUser : Has(data, "user", out var user) ? user : default;
        var modal = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectModalValues(payload, modal);
        // 交互令牌可以代表机器人回复，不暴露给插件。
        var json = JsonNode.Parse(data.GetRawText())!.AsObject();
        json.Remove("token");
        return new DiscordInteraction
        {
            InteractionId = Str(data, "id") ?? string.Empty, Kind = (DiscordInteractionKind)Int(data, "type"),
            ApplicationId = Id(data, "application_id"), GuildId = guildId,
            ChannelId = OptionalId(data, "channel_id") ?? (Has(data, "channel", out var channel) ? OptionalId(channel, "id") : null),
            User = userJson.ValueKind == JsonValueKind.Object ? User(userJson) : null,
            Member = hasMember && guildId is { } guild ? Member(member, guild) : null,
            CommandName = Str(payload, "name"), CommandId = OptionalId(payload, "id"),
            CommandType = OptionalInt(payload, "type") is { } type && Str(payload, "name") is not null ? (DiscordCommandType)type : null,
            Options = Options(payload), TargetId = OptionalId(payload, "target_id"), CustomId = Str(payload, "custom_id"),
            Values = Array(payload, "values").Select(v => v.ToString()).ToArray(), ModalValues = modal,
            MessageId = Has(data, "message", out var message) ? Str(message, "id") : null,
            Locale = Str(data, "locale"), GuildLocale = Str(data, "guild_locale"),
            AppPermissions = Permissions(Str(data, "app_permissions")),
            Json = JsonSerializer.SerializeToElement(json)
        };
    }

    private static IReadOnlyList<DiscordCommandOptionValue> Options(JsonElement container) =>
        Array(container, "options").Select(option => new DiscordCommandOptionValue(Str(option, "name") ?? "", (DiscordCommandOptionType)Int(option, "type"))
        {
            Value = Has(option, "value", out var value) ? value.Clone() : default, Focused = Bool(option, "focused"),
            Options = Options(option)
        }).ToArray();

    /// <summary>弹窗提交：经典 ActionRow 和新版 Label 组件中的输入都收集为 custom_id → 值。</summary>
    private static void CollectModalValues(JsonElement node, Dictionary<string, string> values)
    {
        foreach (var component in Array(node, "components"))
        {
            if (Str(component, "custom_id") is { } id)
            {
                if (Has(component, "value", out var value)) values[id] = value.ToString();
                else if (Has(component, "values", out var many)) values[id] = string.Join(",", many.EnumerateArray().Select(v => v.ToString()));
            }
            if (Has(component, "component", out var child) && Str(child, "custom_id") is { } childId && Has(child, "value", out var childValue))
                values[childId] = childValue.ToString();
            CollectModalValues(component, values);
        }
    }

    // ─── 出站 ───

    public static JsonObject EmbedJson(DiscordEmbed embed)
    {
        var json = new JsonObject();
        void Set(string name, JsonNode? value) { if (value is not null) json[name] = value; }
        Set("title", embed.Title);
        Set("description", embed.Description);
        Set("url", embed.Url);
        Set("timestamp", embed.Timestamp?.ToString("O", CultureInfo.InvariantCulture));
        Set("color", embed.Color);
        if (embed.Footer is { } footer) json["footer"] = new JsonObject { ["text"] = footer.Text, ["icon_url"] = footer.IconUrl };
        if (embed.ImageUrl is { } image) json["image"] = new JsonObject { ["url"] = image };
        if (embed.ThumbnailUrl is { } thumbnail) json["thumbnail"] = new JsonObject { ["url"] = thumbnail };
        if (embed.Author is { } author)
            json["author"] = new JsonObject { ["name"] = author.Name, ["url"] = author.Url, ["icon_url"] = author.IconUrl };
        if (embed.Fields.Count > 0)
            json["fields"] = new JsonArray(embed.Fields.Select(f => (JsonNode)new JsonObject
                { ["name"] = f.Name, ["value"] = f.Value, ["inline"] = f.Inline }).ToArray());
        return json;
    }

    private static JsonObject? EmojiJson(DiscordComponentEmoji? emoji) => emoji is null
        ? null
        : new JsonObject
        {
            ["name"] = emoji.Name, ["id"] = emoji.Id?.ToString(CultureInfo.InvariantCulture), ["animated"] = emoji.Animated ? true : null
        };

    public static JsonNode ComponentJson(DiscordComponent component) => component switch
    {
        DiscordActionRow row => new JsonObject
        {
            ["type"] = 1, ["components"] = new JsonArray(row.Components.Select(ComponentJson).ToArray())
        },
        DiscordButton button => ButtonJson(button),
        DiscordSelectMenu select => new JsonObject
        {
            ["type"] = (int)select.Kind, ["custom_id"] = select.CustomId, ["placeholder"] = select.Placeholder,
            ["min_values"] = select.MinValues, ["max_values"] = select.MaxValues, ["disabled"] = select.Disabled ? true : null,
            ["options"] = select.Kind == DiscordSelectKind.String
                ? new JsonArray(select.Options.Select(o => (JsonNode)new JsonObject
                {
                    ["label"] = o.Label, ["value"] = o.Value, ["description"] = o.Description, ["emoji"] = EmojiJson(o.Emoji),
                    ["default"] = o.Default ? true : null
                }).ToArray())
                : null,
            ["channel_types"] = select.ChannelTypes is { } types ? new JsonArray(types.Select(t => (JsonNode)(int)t).ToArray()) : null
        },
        DiscordTextInput input => new JsonObject
        {
            ["type"] = 4, ["custom_id"] = input.CustomId, ["label"] = input.Label, ["style"] = (int)input.Style,
            ["min_length"] = input.MinLength, ["max_length"] = input.MaxLength, ["required"] = input.Required,
            ["value"] = input.Value, ["placeholder"] = input.Placeholder
        },
        DiscordRawComponent raw => JsonNode.Parse(raw.Json.GetRawText())!,
        _ => throw new NotSupportedException("未知的 Discord 组件：" + component.GetType().Name)
    };

    private static JsonObject ButtonJson(DiscordButton button)
    {
        switch (button.Style)
        {
            case DiscordButtonStyle.Link when button.Url is null:
                throw new ArgumentException("链接按钮需要 Url。");
            case DiscordButtonStyle.Premium when button.SkuId is null:
                throw new ArgumentException("购买按钮需要 SkuId。");
            case not (DiscordButtonStyle.Link or DiscordButtonStyle.Premium) when string.IsNullOrEmpty(button.CustomId):
                throw new ArgumentException("交互按钮需要 CustomId。");
        }
        if (button.Label is null && button.Emoji is null && button.Style != DiscordButtonStyle.Premium)
            throw new ArgumentException("按钮需要 Label 或 Emoji。");
        return new JsonObject
        {
            ["type"] = 2, ["style"] = (int)button.Style, ["label"] = button.Label, ["emoji"] = EmojiJson(button.Emoji),
            ["custom_id"] = button.Style is DiscordButtonStyle.Link or DiscordButtonStyle.Premium ? null : button.CustomId,
            ["url"] = button.Style == DiscordButtonStyle.Link ? button.Url : null,
            ["sku_id"] = button.SkuId?.ToString(CultureInfo.InvariantCulture), ["disabled"] = button.Disabled ? true : null
        };
    }

    /// <summary>消息内容转为 API 负载并读取附件。<paramref name="isEdit"/> 时只写入设置了的部分。</summary>
    public static async Task<(JsonObject Payload, List<ResourceBytes> Files)> MessagePayloadAsync(DiscordMessageContent content,
        bool isEdit, CancellationToken token)
    {
        if (content.Content?.Length > 2000) throw new NotSupportedException("Discord 正文最多 2000 个字符。");
        if (content.Embeds.Count > 10) throw new NotSupportedException("Discord 每条消息最多 10 个 Embed。");
        if (content.StickerIds.Count > 3) throw new NotSupportedException("Discord 每条消息最多 3 个贴纸。");
        if (content.Attachments.Count > 10) throw new NotSupportedException("Discord 每条消息最多 10 个附件。");
        var payload = new JsonObject();
        if (content.Content is not null || !isEdit) payload["content"] = content.Content;
        if (content.Embeds.Count > 0 || !isEdit) payload["embeds"] = new JsonArray(content.Embeds.Select(e => (JsonNode)EmbedJson(e)).ToArray());
        if (content.Components.Count > 0 || !isEdit)
        {
            if (!content.Flags.HasFlag(DiscordMessageFlags.IsComponentsV2) && content.Components.Any(c => c is not (DiscordActionRow or DiscordRawComponent)))
                throw new ArgumentException("组件必须放在 DiscordActionRow 中。");
            payload["components"] = new JsonArray(content.Components.Select(ComponentJson).ToArray());
        }
        if (content.StickerIds.Count > 0)
            payload["sticker_ids"] = new JsonArray(content.StickerIds.Select(id => (JsonNode)id.ToString(CultureInfo.InvariantCulture)).ToArray());
        if (content.Poll is { } poll)
        {
            var hours = (int)Math.Round(poll.Duration.TotalHours);
            if (hours is < 1 or > 768) throw new ArgumentException("投票时长需要 1–768 小时。");
            if (poll.Answers.Count is < 1 or > 10) throw new ArgumentException("投票需要 1–10 个选项。");
            payload["poll"] = new JsonObject
            {
                ["question"] = new JsonObject { ["text"] = poll.Question },
                ["answers"] = new JsonArray(poll.Answers.Select(a => (JsonNode)new JsonObject
                    { ["poll_media"] = new JsonObject { ["text"] = a.Text, ["emoji"] = EmojiJson(a.Emoji) } }).ToArray()),
                ["duration"] = hours, ["allow_multiselect"] = poll.AllowMultiselect
            };
        }
        if (content.AllowedMentions is { } mentions)
        {
            var parse = new JsonArray();
            if (mentions.Users && mentions.UserIds.Count == 0) parse.Add("users");
            if (mentions.Roles && mentions.RoleIds.Count == 0) parse.Add("roles");
            if (mentions.Everyone) parse.Add("everyone");
            payload["allowed_mentions"] = new JsonObject
            {
                ["parse"] = parse,
                ["users"] = mentions.UserIds.Count > 0 ? new JsonArray(mentions.UserIds.Select(id => (JsonNode)id.ToString(CultureInfo.InvariantCulture)).ToArray()) : null,
                ["roles"] = mentions.RoleIds.Count > 0 ? new JsonArray(mentions.RoleIds.Select(id => (JsonNode)id.ToString(CultureInfo.InvariantCulture)).ToArray()) : null,
                ["replied_user"] = mentions.RepliedUser
            };
        }
        if (content.Flags != DiscordMessageFlags.None || !isEdit) payload["flags"] = (int)content.Flags;
        if (content.Tts) payload["tts"] = true;
        if (content.ReplyToMessageId is { } reply)
            payload["message_reference"] = new JsonObject { ["message_id"] = reply, ["fail_if_not_exists"] = false };

        var files = new List<ResourceBytes>();
        var attachments = new JsonArray();
        foreach (var attachment in content.Attachments)
        {
            var file = await ResourceReader.ReadAsync(attachment.Uri, attachment.FileName, token);
            var name = attachment.IsSpoiler && !file.FileName.StartsWith("SPOILER_", StringComparison.Ordinal) ? "SPOILER_" + file.FileName : file.FileName;
            attachments.Add(new JsonObject { ["id"] = files.Count, ["filename"] = name, ["description"] = attachment.Description });
            files.Add(file with { FileName = name });
        }
        if (files.Count > 0) payload["attachments"] = attachments;
        return (payload, files);
    }

    public static JsonObject CommandJson(DiscordApplicationCommand command)
    {
        var json = new JsonObject
        {
            ["name"] = command.Name, ["type"] = (int)command.Type,
            ["description"] = command.Type == DiscordCommandType.ChatInput ? command.Description : string.Empty,
            ["nsfw"] = command.Nsfw ? true : null,
            ["default_member_permissions"] = command.DefaultMemberPermissions is { } permissions ? PermissionsText(permissions) : null,
            ["name_localizations"] = Localizations(command.NameLocalizations),
            ["description_localizations"] = Localizations(command.DescriptionLocalizations)
        };
        if (command.Type == DiscordCommandType.ChatInput && string.IsNullOrWhiteSpace(command.Description))
            throw new ArgumentException($"斜杠命令 {command.Name} 需要描述。");
        if (command.Contexts is { } contexts)
            json["contexts"] = new JsonArray(Enumerable.Range(0, 3).Where(bit => ((int)contexts & (1 << bit)) != 0).Select(bit => (JsonNode)bit).ToArray());
        if (command.Options.Count > 0) json["options"] = new JsonArray(command.Options.Select(OptionJson).ToArray());
        return json;
    }

    private static JsonNode OptionJson(DiscordCommandOption option)
    {
        if (option.Autocomplete && option.Choices.Count > 0) throw new ArgumentException($"选项 {option.Name} 不能同时使用自动补全和固定选项。");
        return new JsonObject
        {
            ["type"] = (int)option.Type, ["name"] = option.Name, ["description"] = option.Description,
            ["required"] = option.Required ? true : null, ["autocomplete"] = option.Autocomplete ? true : null,
            ["choices"] = option.Choices.Count > 0 ? new JsonArray(option.Choices.Select(ChoiceJson).ToArray()) : null,
            ["options"] = option.Options.Count > 0 ? new JsonArray(option.Options.Select(OptionJson).ToArray()) : null,
            ["channel_types"] = option.ChannelTypes is { } types ? new JsonArray(types.Select(t => (JsonNode)(int)t).ToArray()) : null,
            ["min_value"] = option.MinValue, ["max_value"] = option.MaxValue,
            ["min_length"] = option.MinLength, ["max_length"] = option.MaxLength,
            ["name_localizations"] = Localizations(option.NameLocalizations),
            ["description_localizations"] = Localizations(option.DescriptionLocalizations)
        };
    }

    public static JsonNode ChoiceJson(DiscordCommandChoice choice) => new JsonObject
    {
        ["name"] = choice.Name,
        ["value"] = choice.Value switch
        {
            string text => text,
            int number => number,
            long number => number,
            double number => number,
            _ => throw new ArgumentException("命令选项值只能是字符串、整数或数字。")
        },
        ["name_localizations"] = Localizations(choice.NameLocalizations)
    };

    private static JsonObject? Localizations(IReadOnlyDictionary<string, string>? values) => values is null
        ? null
        : new JsonObject(values.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

    public static DiscordRegisteredCommand RegisteredCommand(JsonElement command) => new()
    {
        CommandId = Id(command, "id"), Name = Str(command, "name") ?? string.Empty, Type = (DiscordCommandType)Math.Max(1, Int(command, "type")),
        Description = Str(command, "description") ?? string.Empty, GuildId = OptionalId(command, "guild_id"), Version = Id(command, "version")
    };

    public static JsonObject PresenceJson(DiscordPresence presence) => new()
    {
        ["since"] = null,
        ["status"] = presence.Status switch
        {
            DiscordStatus.Idle => "idle", DiscordStatus.DoNotDisturb => "dnd", DiscordStatus.Invisible => "invisible", _ => "online"
        },
        ["afk"] = presence.Afk,
        ["activities"] = presence.Activity is { } activity
            ? new JsonArray(new JsonObject
            {
                ["name"] = activity.Name, ["type"] = (int)activity.Type, ["state"] = activity.State, ["url"] = activity.Url
            })
            : new JsonArray()
    };

    public static JsonObject ChannelOptionsJson(DiscordChannelOptions options, bool create)
    {
        if (create && string.IsNullOrWhiteSpace(options.Name)) throw new ArgumentException("创建频道需要名称。");
        return new JsonObject
        {
            ["name"] = options.Name, ["type"] = create ? (int)(options.Type ?? DiscordChannelType.GuildText) : null,
            ["topic"] = options.Topic, ["parent_id"] = options.ParentId?.ToString(CultureInfo.InvariantCulture),
            ["position"] = options.Position, ["nsfw"] = options.IsNsfw,
            ["rate_limit_per_user"] = options.RateLimitPerUser is { } delay ? (int)delay.TotalSeconds : null
        };
    }

    public static JsonObject RoleOptionsJson(DiscordRoleOptions options) => new()
    {
        ["name"] = options.Name, ["permissions"] = options.Permissions is { } p ? PermissionsText(p) : null,
        ["color"] = options.Color, ["hoist"] = options.IsHoisted, ["mentionable"] = options.IsMentionable
    };

    /// <summary>移除值为 null 的属性：Discord 把显式 null 理解为清除该字段。</summary>
    public static JsonObject Compact(JsonObject json)
    {
        foreach (var key in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) json.Remove(key);
        foreach (var (_, value) in json)
            if (value is JsonObject child) Compact(child);
            else if (value is JsonArray array)
                foreach (var item in array.OfType<JsonObject>()) Compact(item);
        return json;
    }
}
