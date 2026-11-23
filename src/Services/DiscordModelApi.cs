using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Discord.Model;
using ShiroBot.Model.Discord;
using ShiroBot.SDK.Models;
using static ShiroBot.DiscordAdapter.DiscordModelConverter;

namespace ShiroBot.DiscordAdapter.Services;

/// <summary>宿主共享 Discord Model 扩展接口的实现，插件无需引用本适配器程序集。</summary>
public sealed class DiscordModelApi(DiscordRestClient rest, DiscordMapper mapper, DiscordInteractionService? interactions = null,
    Func<DiscordGateway?>? getGateway = null)
    : IDiscordClientApi, IDiscordGuildApi, IDiscordMessageApi, IDiscordInteractionApi, IDiscordRawApi
{
    private static string Escape(string value) => Uri.EscapeDataString(value);

    private Task<JsonElement> Get(string path, CancellationToken token) => rest.SendAsync(HttpMethod.Get, path, cancellationToken: token);

    private Task<JsonElement> Send(HttpMethod method, string path, JsonNode? body, string? reason, CancellationToken token) =>
        rest.SendAsync(method, path, body, reason: reason, cancellationToken: token);

    private DiscordInteractionService Interactions => interactions ?? throw new NotSupportedException("交互服务未启动。");

    /// <summary>私聊频道使用用户 ID 时打开 DM，返回实际频道 ID。</summary>
    private async Task<(string Id, Channel Channel)> ChannelAsync(Channel channel, CancellationToken token)
    {
        if (channel.Type != ChannelType.Direct || mapper.KnownDmChannels.ContainsKey(channel.Id)) return (channel.Id, channel);
        var dm = await rest.PostAsync<DcChannel>("users/@me/channels", new { recipient_id = channel.Id }, token);
        mapper.KnownDmChannels.TryAdd(dm.Id, 0);
        mapper.Channels.Set(dm.Id, dm);
        return (dm.Id, SDK.Models.Channel.Direct(dm.Id));
    }

    // ─── IDiscordGuildApi ───

    public async Task<DiscordGuild> GetGuildAsync(string guildId, bool withCounts = false, CancellationToken cancellationToken = default) =>
        Guild(await Get($"guilds/{guildId}" + (withCounts ? "?with_counts=true" : ""), cancellationToken));

    public async Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(CancellationToken cancellationToken = default)
    {
        var guilds = new List<DiscordGuild>();
        string? after = null;
        while (true)
        {
            var page = await Get("users/@me/guilds?limit=200" + (after is null ? "" : "&after=" + after), cancellationToken);
            var items = page.EnumerateArray().ToArray();
            guilds.AddRange(items.Select(Guild));
            if (items.Length < 200) return guilds;
            after = Str(items[^1], "id");
        }
    }

    public async Task<IReadOnlyList<DiscordChannel>> GetGuildChannelsAsync(string guildId, CancellationToken cancellationToken = default) =>
        (await Get($"guilds/{guildId}/channels", cancellationToken)).EnumerateArray()
        .Select(c => Channel(c, ulong.Parse(guildId, CultureInfo.InvariantCulture))).ToArray();

    public async Task<DiscordChannel> CreateChannelAsync(string guildId, DiscordChannelOptions options, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Channel(await Send(HttpMethod.Post, $"guilds/{guildId}/channels", Compact(ChannelOptionsJson(options, true)), reason, cancellationToken));

    public async Task<DiscordChannel> ModifyChannelAsync(string channelId, DiscordChannelOptions options, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Channel(await Send(HttpMethod.Patch, $"channels/{channelId}", Compact(ChannelOptionsJson(options, false)), reason, cancellationToken));

    public Task DeleteChannelAsync(string channelId, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"channels/{channelId}", null, reason, cancellationToken);

    public Task SetChannelPermissionAsync(string channelId, string roleOrUserId, bool isRole, DiscordPermissions allow,
        DiscordPermissions deny, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Put, $"channels/{channelId}/permissions/{roleOrUserId}", new JsonObject
        {
            ["type"] = isRole ? 0 : 1, ["allow"] = PermissionsText(allow), ["deny"] = PermissionsText(deny)
        }, reason, cancellationToken);

    public Task DeleteChannelPermissionAsync(string channelId, string roleOrUserId, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"channels/{channelId}/permissions/{roleOrUserId}", null, reason, cancellationToken);

    public async Task<IReadOnlyList<DiscordRole>> GetRolesAsync(string guildId, CancellationToken cancellationToken = default) =>
        (await Get($"guilds/{guildId}/roles", cancellationToken)).EnumerateArray().Select(Role).ToArray();

    public async Task<DiscordRole> CreateRoleAsync(string guildId, DiscordRoleOptions options, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Role(await Send(HttpMethod.Post, $"guilds/{guildId}/roles", Compact(RoleOptionsJson(options)), reason, cancellationToken));

    public async Task<DiscordRole> ModifyRoleAsync(string guildId, string roleId, DiscordRoleOptions options, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Role(await Send(HttpMethod.Patch, $"guilds/{guildId}/roles/{roleId}", Compact(RoleOptionsJson(options)), reason, cancellationToken));

    public Task DeleteRoleAsync(string guildId, string roleId, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"guilds/{guildId}/roles/{roleId}", null, reason, cancellationToken);

    public async Task<DiscordMember> GetGuildMemberAsync(string guildId, string userId, CancellationToken cancellationToken = default)
    {
        var member = await Get($"guilds/{guildId}/members/{userId}", cancellationToken);
        var guild = await Get($"guilds/{guildId}", cancellationToken);
        var roles = Has(guild, "roles") ? guild.GetProperty("roles").EnumerateArray().Select(Role).ToArray()
            : (await GetRolesAsync(guildId, cancellationToken)).ToArray();
        return Member(member, ulong.Parse(guildId, CultureInfo.InvariantCulture), ownerId: Id(guild, "owner_id"), roles: roles);
    }

    private static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array;

    public async Task<IReadOnlyList<DiscordMember>> SearchMembersAsync(string guildId, string query, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var id = ulong.Parse(guildId, CultureInfo.InvariantCulture);
        var result = await Get($"guilds/{guildId}/members/search?query={Escape(query)}&limit={Math.Clamp(limit, 1, 1000)}", cancellationToken);
        return result.EnumerateArray().Select(m => Member(m, id)).ToArray();
    }

    public Task AddMemberRoleAsync(string guildId, string userId, string roleId, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Put, $"guilds/{guildId}/members/{userId}/roles/{roleId}", null, reason, cancellationToken);

    public Task RemoveMemberRoleAsync(string guildId, string userId, string roleId, string? reason = null,
        CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"guilds/{guildId}/members/{userId}/roles/{roleId}", null, reason, cancellationToken);

    public Task SetNicknameAsync(string guildId, string userId, string? nickname, string? reason = null,
        CancellationToken cancellationToken = default) =>
        // 显式 null 清除昵称，因此不压缩负载。
        Send(HttpMethod.Patch, $"guilds/{guildId}/members/{userId}", new JsonObject { ["nick"] = nickname }, reason, cancellationToken);

    public Task TimeoutMemberAsync(string guildId, string userId, DateTimeOffset? until, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (until > DateTimeOffset.UtcNow.AddDays(28)) throw new ArgumentOutOfRangeException(nameof(until), "Discord 超时最长 28 天。");
        return Send(HttpMethod.Patch, $"guilds/{guildId}/members/{userId}", new JsonObject
        {
            ["communication_disabled_until"] = until?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        }, reason, cancellationToken);
    }

    public Task RemoveGuildMemberAsync(string guildId, string userId, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"guilds/{guildId}/members/{userId}", null, reason, cancellationToken);

    public Task BanAsync(string guildId, string userId, TimeSpan? deleteMessages = null, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (deleteMessages > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(deleteMessages), "最多删除 7 天内的消息。");
        return Send(HttpMethod.Put, $"guilds/{guildId}/bans/{userId}", new JsonObject
        {
            ["delete_message_seconds"] = (int)(deleteMessages ?? TimeSpan.Zero).TotalSeconds
        }, reason, cancellationToken);
    }

    public Task UnbanAsync(string guildId, string userId, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"guilds/{guildId}/bans/{userId}", null, reason, cancellationToken);

    public async Task<IReadOnlyList<DiscordBan>> GetBansAsync(string guildId, CancellationToken cancellationToken = default)
    {
        var bans = new List<DiscordBan>();
        string? after = null;
        while (true)
        {
            var page = (await Get($"guilds/{guildId}/bans?limit=1000" + (after is null ? "" : "&after=" + after), cancellationToken))
                .EnumerateArray().ToArray();
            bans.AddRange(page.Select(b => new DiscordBan(User(b.GetProperty("user")), Str(b, "reason"))));
            if (page.Length < 1000) return bans;
            after = Str(page[^1].GetProperty("user"), "id");
        }
    }

    public async Task<IReadOnlyList<DiscordInvite>> GetInvitesAsync(string guildId, CancellationToken cancellationToken = default) =>
        (await Get($"guilds/{guildId}/invites", cancellationToken)).EnumerateArray().Select(Invite).ToArray();

    public async Task<DiscordInvite> CreateInviteAsync(string channelId, DiscordInviteOptions? options = null, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DiscordInviteOptions();
        return Invite(await Send(HttpMethod.Post, $"channels/{channelId}/invites", Compact(new JsonObject
        {
            ["max_age"] = options.MaxAge is { } age ? (int)age.TotalSeconds : null, ["max_uses"] = options.MaxUses,
            ["temporary"] = options.Temporary, ["unique"] = options.Unique
        }), reason, cancellationToken));
    }

    public Task DeleteInviteAsync(string code, string? reason = null, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"invites/{Escape(code)}", null, reason, cancellationToken);

    public async Task<IReadOnlyList<DiscordEmoji>> GetEmojisAsync(string guildId, CancellationToken cancellationToken = default) =>
        (await Get($"guilds/{guildId}/emojis", cancellationToken)).EnumerateArray().Select(Emoji).ToArray();

    public async Task<DiscordChannel> CreateThreadAsync(string channelId, string name, string? messageId = null, bool isPrivate = false,
        TimeSpan? autoArchive = null, CancellationToken cancellationToken = default)
    {
        var archive = (int)(autoArchive ?? TimeSpan.FromDays(1)).TotalMinutes;
        if (archive is not (60 or 1440 or 4320 or 10080)) throw new ArgumentException("子区自动归档时间只能是 1 小时、1 天、3 天或 7 天。");
        var body = new JsonObject { ["name"] = name, ["auto_archive_duration"] = archive };
        if (messageId is null) body["type"] = isPrivate ? (int)DiscordChannelType.PrivateThread : (int)DiscordChannelType.PublicThread;
        else if (isPrivate) throw new ArgumentException("从消息创建的子区只能是公开子区。");
        var path = messageId is null ? $"channels/{channelId}/threads" : $"channels/{channelId}/messages/{messageId}/threads";
        var thread = await Send(HttpMethod.Post, path, body, null, cancellationToken);
        mapper.Channels.Set(Str(thread, "id")!, thread.Deserialize<DcChannel>()!);
        return Channel(thread);
    }

    public Task JoinThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Put, $"channels/{threadId}/thread-members/@me", null, null, cancellationToken);

    public Task LeaveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Delete, $"channels/{threadId}/thread-members/@me", null, null, cancellationToken);

    public Task AddThreadMemberAsync(string threadId, string userId, CancellationToken cancellationToken = default) =>
        Send(HttpMethod.Put, $"channels/{threadId}/thread-members/{userId}", null, null, cancellationToken);

    // ─── IDiscordMessageApi ───

    public async Task<DiscordSentMessage> SendAsync(Channel channel, DiscordMessageContent content,
        CancellationToken cancellationToken = default)
    {
        var (id, target) = await ChannelAsync(channel, cancellationToken);
        var (payload, files) = await MessagePayloadAsync(content, false, cancellationToken);
        if (content.Flags.HasFlag(DiscordMessageFlags.Ephemeral)) throw new ArgumentException("临时可见消息只能用于交互回复。");
        var sent = await rest.SendAsync(HttpMethod.Post, $"channels/{id}/messages", Compact(payload), files, cancellationToken: cancellationToken);
        return Sent(sent, target);
    }

    public async Task<DiscordSentMessage> EditAsync(Channel channel, string messageId, DiscordMessageContent content,
        CancellationToken cancellationToken = default)
    {
        var (id, target) = await ChannelAsync(channel, cancellationToken);
        var (payload, files) = await MessagePayloadAsync(content, true, cancellationToken);
        if (files.Count > 0)
        {
            // 编辑时 attachments 是保留列表：先列出已有附件，再追加新上传的。
            var existing = await Get($"channels/{id}/messages/{messageId}", cancellationToken);
            var list = new JsonArray();
            foreach (var attachment in existing.GetProperty("attachments").EnumerateArray())
                list.Add(new JsonObject { ["id"] = Str(attachment, "id") });
            foreach (var added in payload["attachments"]!.AsArray()) list.Add(added!.DeepClone());
            payload["attachments"] = list;
        }
        var edited = await rest.SendAsync(HttpMethod.Patch, $"channels/{id}/messages/{messageId}", Compact(payload), files,
            cancellationToken: cancellationToken);
        return Sent(edited, target);
    }

    public async Task CrosspostAsync(Channel channel, string messageId, CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Post, $"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/{messageId}/crosspost", null, null, cancellationToken);

    public async Task PinAsync(Channel channel, string messageId, string? reason = null, CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Put, $"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/pins/{messageId}", null, reason, cancellationToken);

    public async Task UnpinAsync(Channel channel, string messageId, string? reason = null, CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Delete, $"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/pins/{messageId}", null, reason, cancellationToken);

    public async Task<IReadOnlyList<DiscordMessage>> GetPinnedAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        var id = (await ChannelAsync(channel, cancellationToken)).Id;
        var pins = new List<DiscordMessage>();
        string? before = null;
        while (true)
        {
            var page = await Get($"channels/{id}/messages/pins?limit=50" + (before is null ? "" : "&before=" + Escape(before)), cancellationToken);
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            pins.AddRange(items.Select(item => Message(item.GetProperty("message"))));
            if (!page.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True || items.Length == 0) return pins;
            before = Str(items[^1], "pinned_at");
        }
    }

    public async Task BulkDeleteAsync(Channel channel, IReadOnlyList<string> messageIds, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count is < 2 or > 100) throw new ArgumentException("批量删除需要 2–100 条消息。", nameof(messageIds));
        await Send(HttpMethod.Post, $"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/bulk-delete",
            new JsonObject { ["messages"] = new JsonArray(messageIds.Select(id => (JsonNode)id).ToArray()) }, reason, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordUser>> GetReactionUsersAsync(Channel channel, string messageId, string emoji, int limit = 25,
        CancellationToken cancellationToken = default) =>
        (await Get($"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/{messageId}/reactions/{Escape(emoji)}?limit={Math.Clamp(limit, 1, 100)}",
            cancellationToken)).EnumerateArray().Select(u => User(u)).ToArray();

    public async Task ClearReactionsAsync(Channel channel, string messageId, string? emoji = null,
        CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Delete, $"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/messages/{messageId}/reactions"
                                      + (emoji is null ? "" : "/" + Escape(emoji)), null, null, cancellationToken);

    public async Task<DiscordSentMessage> EndPollAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        var (id, target) = await ChannelAsync(channel, cancellationToken);
        return Sent(await Send(HttpMethod.Post, $"channels/{id}/polls/{messageId}/expire", null, null, cancellationToken), target);
    }

    public async Task<IReadOnlyList<DiscordUser>> GetPollVotersAsync(Channel channel, string messageId, int answerId, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var result = await Get($"channels/{(await ChannelAsync(channel, cancellationToken)).Id}/polls/{messageId}/answers/{answerId}?limit={Math.Clamp(limit, 1, 100)}",
            cancellationToken);
        return result.GetProperty("users").EnumerateArray().Select(u => User(u)).ToArray();
    }

    // ─── IDiscordInteractionApi ───

    public bool AutoAcknowledges => interactions?.AutoAcknowledge ?? true;

    public async Task RespondAsync(string interactionId, DiscordMessageContent content, CancellationToken cancellationToken = default)
    {
        var (payload, files) = await MessagePayloadAsync(content, false, cancellationToken);
        await Interactions.InitialResponseAsync(interactionId, null, new JsonObject { ["type"] = 4, ["data"] = Compact(payload) }, files,
            false, cancellationToken);
    }

    public Task DeferAsync(string interactionId, bool ephemeral = false, bool updateMessage = false,
        CancellationToken cancellationToken = default)
    {
        if (updateMessage && Interactions.InteractionType(interactionId) is not (3 or 5))
            throw new ArgumentException("只有组件交互和弹窗提交可以延迟更新消息。");
        var response = new JsonObject { ["type"] = updateMessage ? 6 : 5 };
        if (ephemeral) response["data"] = new JsonObject { ["flags"] = (int)DiscordMessageFlags.Ephemeral };
        return Interactions.InitialResponseAsync(interactionId, null, response, [], false, cancellationToken);
    }

    public async Task UpdateMessageAsync(string interactionId, DiscordMessageContent content, CancellationToken cancellationToken = default)
    {
        if (Interactions.InteractionType(interactionId) is not (3 or 5))
            throw new ArgumentException("只有组件交互和弹窗提交可以直接更新消息。");
        var (payload, files) = await MessagePayloadAsync(content, true, cancellationToken);
        await Interactions.InitialResponseAsync(interactionId, null, new JsonObject { ["type"] = 7, ["data"] = Compact(payload) }, files,
            false, cancellationToken);
    }

    public Task ShowModalAsync(string interactionId, DiscordModal modal, CancellationToken cancellationToken = default)
    {
        if (Interactions.InteractionType(interactionId) is 4 or 5) throw new ArgumentException("弹窗提交和自动补全不能再弹出弹窗。");
        if (modal.Inputs.Count is < 1 or > 5) throw new ArgumentException("弹窗需要 1–5 个输入框。");
        return Interactions.InitialResponseAsync(interactionId, null, new JsonObject
        {
            ["type"] = 9,
            ["data"] = new JsonObject
            {
                ["custom_id"] = modal.CustomId, ["title"] = modal.Title,
                ["components"] = new JsonArray(modal.Inputs.Select(input => ComponentJson(new DiscordActionRow([input]))).ToArray())
            }
        }, [], false, cancellationToken);
    }

    public Task AutocompleteAsync(string interactionId, IReadOnlyList<DiscordCommandChoice> choices,
        CancellationToken cancellationToken = default)
    {
        if (Interactions.InteractionType(interactionId) != 4) throw new ArgumentException("只能回答自动补全交互。");
        if (choices.Count > 25) throw new ArgumentException("自动补全最多 25 个选项。");
        return Interactions.InitialResponseAsync(interactionId, null, new JsonObject
        {
            ["type"] = 8, ["data"] = new JsonObject { ["choices"] = new JsonArray(choices.Select(ChoiceJson).ToArray()) }
        }, [], false, cancellationToken);
    }

    public async Task<DiscordSentMessage> EditOriginalAsync(string interactionId, DiscordMessageContent content,
        CancellationToken cancellationToken = default)
    {
        var (path, channel) = Interactions.Webhook(interactionId);
        var (payload, files) = await MessagePayloadAsync(content, true, cancellationToken);
        return Sent(await rest.SendAsync(HttpMethod.Patch, path + "/messages/@original", Compact(payload), files,
            cancellationToken: cancellationToken), channel);
    }

    public Task DeleteOriginalAsync(string interactionId, CancellationToken cancellationToken = default) =>
        rest.SendAsync(HttpMethod.Delete, Interactions.Webhook(interactionId).Path + "/messages/@original",
            cancellationToken: cancellationToken);

    public async Task<DiscordSentMessage> FollowupAsync(string interactionId, DiscordMessageContent content,
        CancellationToken cancellationToken = default)
    {
        var (path, channel) = Interactions.Webhook(interactionId);
        var (payload, files) = await MessagePayloadAsync(content, false, cancellationToken);
        return Sent(await rest.SendAsync(HttpMethod.Post, path, Compact(payload), files, cancellationToken: cancellationToken), channel);
    }

    private async Task<string> CommandsPathAsync(string? guildId, CancellationToken token)
    {
        var application = ApplicationId ?? Str(await Get("applications/@me", token), "id")
            ?? throw new InvalidOperationException("无法获取应用 ID。");
        mapper.ApplicationId ??= application;
        return guildId is null ? $"applications/{application}/commands" : $"applications/{application}/guilds/{guildId}/commands";
    }

    public async Task<IReadOnlyList<DiscordRegisteredCommand>> GetCommandsAsync(string? guildId = null,
        CancellationToken cancellationToken = default) =>
        (await Get(await CommandsPathAsync(guildId, cancellationToken), cancellationToken)).EnumerateArray().Select(RegisteredCommand).ToArray();

    public async Task<IReadOnlyList<DiscordRegisteredCommand>> RegisterCommandsAsync(IReadOnlyList<DiscordApplicationCommand> commands,
        string? guildId = null, CancellationToken cancellationToken = default)
    {
        if (commands.Count > 100) throw new ArgumentException("每类命令最多 100 个。", nameof(commands));
        var body = new JsonArray(commands.Select(c => (JsonNode)Compact(CommandJson(c))).ToArray());
        return (await Send(HttpMethod.Put, await CommandsPathAsync(guildId, cancellationToken), body, null, cancellationToken))
            .EnumerateArray().Select(RegisteredCommand).ToArray();
    }

    public async Task<DiscordRegisteredCommand> UpsertCommandAsync(DiscordApplicationCommand command, string? guildId = null,
        CancellationToken cancellationToken = default) =>
        RegisteredCommand(await Send(HttpMethod.Post, await CommandsPathAsync(guildId, cancellationToken), Compact(CommandJson(command)),
            null, cancellationToken));

    public async Task DeleteCommandAsync(string commandId, string? guildId = null, CancellationToken cancellationToken = default) =>
        await Send(HttpMethod.Delete, $"{await CommandsPathAsync(guildId, cancellationToken)}/{commandId}", null, null, cancellationToken);

    // ─── IDiscordClientApi ───

    public DiscordAccountKind AccountKind => DiscordAccountKind.Bot;

    /// <summary>机器人默认 10 MB；服务器助力等级提高的上限由 Discord 在上传时校验。</summary>
    public long MaxUploadBytes => 10L * 1024 * 1024;

    public string? ApplicationId => mapper.ApplicationId;

    public async Task<DiscordUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        User(await Get("users/@me", cancellationToken));

    public async Task<DiscordUser> FetchUserAsync(string userId, CancellationToken cancellationToken = default) =>
        User(await Get($"users/{userId}", cancellationToken));

    public async Task SetPresenceAsync(DiscordPresence presence, CancellationToken cancellationToken = default)
    {
        var gateway = getGateway?.Invoke() ?? throw new InvalidOperationException("Discord Gateway 未启动。");
        var json = PresenceJson(presence);
        gateway.Presence = json;
        if (gateway.IsConnected) await gateway.SendAsync(DcOpcode.PresenceUpdate, json, cancellationToken);
    }

    public async Task<DiscordUser> ModifyCurrentUserAsync(string? username = null, string? avatarUri = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["username"] = username };
        if (avatarUri is not null)
        {
            var image = await ResourceReader.ReadAsync(avatarUri, "avatar.png", cancellationToken);
            var type = Path.GetExtension(image.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png"
            };
            body["avatar"] = $"data:{type};base64,{Convert.ToBase64String(image.Bytes)}";
        }
        return User(await Send(HttpMethod.Patch, "users/@me", Compact(body), null, cancellationToken));
    }

    // ─── IDiscordRawApi ───

    public int ApiVersion => 10;

    public async Task<JsonElement> CallAsync(string method, string path, JsonNode? body = null,
        IReadOnlyList<DiscordUploadFile>? files = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var uploads = new List<ResourceBytes>();
        foreach (var file in files ?? [])
        {
            using var buffer = new MemoryStream();
            await file.Content.CopyToAsync(buffer, cancellationToken);
            uploads.Add(new ResourceBytes(buffer.ToArray(), file.FileName));
        }
        return await rest.SendAsync(new HttpMethod(method.ToUpperInvariant()), path, body?.DeepClone(), uploads, reason, cancellationToken);
    }

    public Task SendGatewayAsync(int op, JsonNode? data, CancellationToken cancellationToken = default) =>
        (getGateway?.Invoke() ?? throw new InvalidOperationException("Discord Gateway 未启动。")).SendAsync(op, data, cancellationToken);

    private Func<string, JsonElement, Task>? _dispatch;
    private DiscordGateway? _subscribed;

    public event Func<string, JsonElement, Task>? DispatchReceived
    {
        add
        {
            if (_dispatch is null && getGateway?.Invoke() is { } gateway)
            {
                gateway.DispatchReceived += OnDispatchAsync;
                _subscribed = gateway;
            }
            _dispatch += value;
        }
        remove
        {
            _dispatch -= value;
            if (_dispatch is null) Detach();
        }
    }

    private async Task OnDispatchAsync(string name, JsonElement data)
    {
        if (_dispatch is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<string, JsonElement, Task>>())
            await handler(name, data).ConfigureAwait(false);
    }

    internal void Detach()
    {
        if (_subscribed is not null) _subscribed.DispatchReceived -= OnDispatchAsync;
        _subscribed = null;
    }
}
