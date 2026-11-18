using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.DiscordAdapter;
using ShiroBot.DiscordAdapter.Services;
using ShiroBot.Model.Discord;
using ShiroBot.SDK.Models;

/// <summary>宿主共享 ShiroBot.Model.Discord 契约的实现。</summary>
internal static class ModelCoverage
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        foreach (var contract in new[] { typeof(IDiscordGuildApi), typeof(IDiscordMessageApi), typeof(IDiscordInteractionApi),
                     typeof(IDiscordBotApi), typeof(IDiscordRawApi) })
            Check(typeof(DiscordModelApi).GetInterfaceMap(contract).TargetMethods.All(m => m.DeclaringType == typeof(DiscordModelApi)),
                $"DiscordModelApi inherits an unimplemented default member of {contract.Name}.");

        var http = new Transport();
        using var rest = new DiscordRestClient("test-token", http);
        var mapper = new DiscordMapper();
        var interactions = new DiscordInteractionService(rest, autoAcknowledge: false);
        var gateway = new DiscordGateway("test-token", 0);
        var model = new DiscordModelApi(rest, mapper, interactions, () => gateway);
        var channel = new Channel("100", ChannelType.Group) { GuildId = "200" };

        // 富消息：Embed、按钮、下拉菜单、投票、附件，提及控制。
        var sent = await model.SendAsync(channel, new DiscordMessageContent
        {
            Content = "hi",
            Embeds = [new DiscordEmbed { Title = "T", Color = 0x112233, Fields = [new("a", "b", Inline: true)], Footer = new("f") }],
            Components =
            [
                new DiscordActionRow([
                    new DiscordButton { Label = "Go", CustomId = "go", Style = DiscordButtonStyle.Success },
                    new DiscordButton { Label = "Docs", Url = "https://example.org", Style = DiscordButtonStyle.Link }
                ]),
                new DiscordActionRow([new DiscordSelectMenu(DiscordSelectKind.String, "pick") { Options = [new("One", "1")], MaxValues = 1 }])
            ],
            Poll = new DiscordPollContent("Q", [new("Yes"), new("No")]) { Duration = TimeSpan.FromHours(2) },
            Attachments = [new DiscordAttachment("base64://aGVsbG8=", "a.png") { IsSpoiler = true, Description = "alt" }],
            AllowedMentions = DiscordAllowedMentions.None, ReplyToMessageId = "55"
        });
        var payload = http.PayloadJson!.Value;
        Check(http.Last == "POST channels/100/messages" && http.Multipart!.Contains("filename=SPOILER_a.png")
              && payload.GetProperty("attachments")[0].GetProperty("filename").GetString() == "SPOILER_a.png"
              && payload.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("inline").GetBoolean()
              && payload.GetProperty("components")[0].GetProperty("components")[1].GetProperty("style").GetInt32() == 5
              && !payload.GetProperty("components")[0].GetProperty("components")[1].TryGetProperty("custom_id", out _)
              && payload.GetProperty("components")[1].GetProperty("components")[0].GetProperty("type").GetInt32() == 3
              && payload.GetProperty("poll").GetProperty("duration").GetInt32() == 2
              && payload.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0
              && !payload.GetProperty("allowed_mentions").GetProperty("replied_user").GetBoolean()
              && payload.GetProperty("message_reference").GetProperty("message_id").GetString() == "55", "Rich message payload is wrong.");
        Check(sent is { MessageId: "900", Channel.Id: "100", AttachmentUrls: ["https://cdn.example/a.png"] }, "Sent message not mapped.");

        await Throws<ArgumentException>(() => model.SendAsync(channel, new DiscordMessageContent { Content = "x", Flags = DiscordMessageFlags.Ephemeral }),
            "Ephemeral channel message accepted.");
        await Throws<ArgumentException>(() => model.SendAsync(channel, new DiscordMessageContent
            { Components = [new DiscordActionRow([new DiscordButton { Label = "x", Style = DiscordButtonStyle.Link }])] }), "Link button without URL accepted.");
        await Throws<ArgumentException>(() => model.SendAsync(channel, new DiscordMessageContent
            { Components = [new DiscordButton { Label = "x", CustomId = "x" }] }), "Button outside an action row accepted.");

        // 私聊：用户 ID 打开 DM。
        await model.SendAsync(Channel.Direct("7"), new DiscordMessageContent { Content = "dm" });
        Check(http.Requests.Contains("POST users/@me/channels") && http.Last == "POST channels/dm-7/messages", "DM channel not opened.");

        // 编辑时保留已有附件。
        await model.EditAsync(channel, "900", new DiscordMessageContent { Attachments = [new DiscordAttachment("base64://aGk=", "b.txt")] });
        var kept = http.PayloadJson!.Value.GetProperty("attachments");
        Check(http.Last == "PATCH channels/100/messages/900" && kept[0].GetProperty("id").GetString() == "old"
              && kept[1].GetProperty("filename").GetString() == "b.txt" && !http.PayloadJson.Value.TryGetProperty("content", out _),
            "Edit dropped existing attachments or overwrote content.");

        // 管理与审计原因。
        await model.BanAsync("200", "7", TimeSpan.FromDays(1), "spam");
        Check(http.Last == "PUT guilds/200/bans/7" && http.Reason == "spam" && http.Json.GetProperty("delete_message_seconds").GetInt32() == 86400,
            "Ban payload or audit reason is wrong.");
        await model.TimeoutMemberAsync("200", "7", null);
        Check(http.Json.GetProperty("communication_disabled_until").ValueKind == JsonValueKind.Null, "Clearing a timeout must send null.");
        await model.SetNicknameAsync("200", "@me", null);
        Check(http.Last == "PATCH guilds/200/members/@me" && http.Json.GetProperty("nick").ValueKind == JsonValueKind.Null, "Nickname not cleared.");
        await model.ModifyRoleAsync("200", "300", new DiscordRoleOptions { Permissions = DiscordPermissions.KickMembers | DiscordPermissions.BanMembers });
        Check(http.Json.GetProperty("permissions").GetString() == "6" && !http.Json.TryGetProperty("name", out _), "Role edit must only send set fields.");
        var member = await model.GetGuildMemberAsync("200", "7");
        Check(member is { Nickname: "Nick", IsOwner: false, RoleIds: [300] } && member.Permissions.HasFlag(DiscordPermissions.ManageGuild)
              && member.User.AvatarUrl == "https://cdn.discordapp.com/avatars/7/a_hash.gif", "Member permissions or avatar not computed.");
        var thread = await model.CreateThreadAsync("100", "Topic", messageId: "900");
        Check(http.Last == "POST channels/100/messages/900/threads" && thread is { IsThread: true, ChannelId: 400 }, "Thread from message is wrong.");

        // 命令注册。
        var registered = await model.RegisterCommandsAsync([
            new DiscordApplicationCommand("roll")
            {
                Description = "Roll", DefaultMemberPermissions = DiscordPermissions.None, Contexts = DiscordCommandContexts.Guild | DiscordCommandContexts.BotDirectMessage,
                Options = [new DiscordCommandOption(DiscordCommandOptionType.Integer, "sides", "Sides") { Required = true, MinValue = 2 }]
            }
        ], "200");
        var commands = http.JsonArray!.Value;
        Check(http.Last == "PUT applications/app/guilds/200/commands" && commands[0].GetProperty("default_member_permissions").GetString() == "0"
              && commands[0].GetProperty("contexts").GetArrayLength() == 2 && commands[0].GetProperty("options")[0].GetProperty("min_value").GetDouble() == 2
              && registered is [{ Name: "roll", CommandId: 1 }], "Command registration is wrong.");
        await Throws<ArgumentException>(() => model.UpsertCommandAsync(new DiscordApplicationCommand("x")), "Slash command without description accepted.");

        // 交互：关闭自动确认后由插件首次回复。
        var events = new DiscordEventService(mapper, () => rest, () => interactions);
        var received = new List<BotEvent>();
        events.EventReceived += e => { received.Add(e); return Task.CompletedTask; };
        async Task Emit(string kind, string json)
        {
            using var data = JsonDocument.Parse(json);
            await (Task)typeof(DiscordEventService).GetMethod("OnDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(events, [kind, data.RootElement.Clone()])!;
        }

        var before = http.Requests.Count(r => r.EndsWith("/callback"));
        await Emit("INTERACTION_CREATE", """{"id":"i1","application_id":"app","token":"secret","type":2,"guild_id":"200","channel_id":"100","member":{"user":{"id":"7","username":"u"},"roles":[],"permissions":"8"},"app_permissions":"2048","locale":"zh-CN","data":{"id":"1","name":"roll","type":1,"options":[{"name":"sides","type":4,"value":20}]}}""");
        Check(received.Last() is InteractionEvent { IsAutomaticallyAcknowledged: false, Raw: DiscordInteraction
              { Kind: DiscordInteractionKind.ApplicationCommand, CommandName: "roll", Locale: "zh-CN", Member.User.UserId: 7 } slash }
              && slash.Options[0].AsInteger() == 20 && slash.AppPermissions.HasFlag(DiscordPermissions.SendMessages)
              && !slash.Json.TryGetProperty("token", out _) && http.Requests.Count(r => r.EndsWith("/callback")) == before,
            "Slash command interaction mapped wrongly or leaked its token.");
        await model.RespondAsync("i1", new DiscordMessageContent { Content = "20", Flags = DiscordMessageFlags.Ephemeral });
        Check(http.Last == "POST interactions/i1/secret/callback" && http.Json.GetProperty("type").GetInt32() == 4
              && http.Json.GetProperty("data").GetProperty("flags").GetInt32() == 64, "Ephemeral response is wrong.");
        await Throws<InvalidOperationException>(() => model.RespondAsync("i1", new DiscordMessageContent { Content = "again" }), "Second initial response accepted.");
        await model.EditOriginalAsync("i1", new DiscordMessageContent { Content = "edited" });
        Check(http.Last == "PATCH webhooks/app/secret/messages/@original", "Original response edit path is wrong.");
        await model.FollowupAsync("i1", new DiscordMessageContent { Content = "more" });
        Check(http.Last == "POST webhooks/app/secret", "Follow-up path is wrong.");

        await Emit("INTERACTION_CREATE", """{"id":"i2","application_id":"app","token":"t2","type":4,"channel_id":"100","user":{"id":"7","username":"u"},"data":{"id":"1","name":"roll","type":1,"options":[{"name":"sides","type":4,"value":"1","focused":true}]}}""");
        await Throws<ArgumentException>(() => model.ShowModalAsync("i2", new DiscordModal("m", "M", [new DiscordTextInput("a", "A")])), "Modal on autocomplete accepted.");
        await model.AutocompleteAsync("i2", [new DiscordCommandChoice("Six", 6), new DiscordCommandChoice("Twenty", 20L)]);
        Check(http.Json.GetProperty("type").GetInt32() == 8 && http.Json.GetProperty("data").GetProperty("choices")[1].GetProperty("value").GetInt64() == 20,
            "Autocomplete response is wrong.");

        await Emit("INTERACTION_CREATE", """{"id":"i3","application_id":"app","token":"t3","type":3,"channel_id":"100","user":{"id":"7","username":"u"},"message":{"id":"900"},"data":{"custom_id":"pick","component_type":3,"values":["1"]}}""");
        Check(received.Last() is InteractionEvent { Raw: DiscordInteraction { CustomId: "pick", Values: ["1"], MessageId: "900" } }, "Select values lost.");
        await model.ShowModalAsync("i3", new DiscordModal("form", "Form", [new DiscordTextInput("name", "Name") { Style = DiscordTextInputStyle.Paragraph }]));
        Check(http.Json.GetProperty("type").GetInt32() == 9 && http.Json.GetProperty("data").GetProperty("components")[0]
            .GetProperty("components")[0].GetProperty("style").GetInt32() == 2, "Modal payload is wrong.");

        await Emit("INTERACTION_CREATE", """{"id":"i4","application_id":"app","token":"t4","type":5,"channel_id":"100","user":{"id":"7","username":"u"},"data":{"custom_id":"form","components":[{"type":1,"components":[{"type":4,"custom_id":"name","value":"Ann"}]},{"type":18,"component":{"type":4,"custom_id":"bio","value":"Hi"}}]}}""");
        Check(received.Last() is InteractionEvent { Raw: DiscordInteraction { Kind: DiscordInteractionKind.ModalSubmit } modal }
              && modal.ModalValues["name"] == "Ann" && modal.ModalValues["bio"] == "Hi", "Modal values lost.");

        // 自动确认开启时不对自动补全发送延迟回复。
        var auto = new DiscordInteractionService(rest);
        var autoEvents = new DiscordEventService(mapper, () => rest, () => auto);
        var callbacks = http.Requests.Count(r => r.EndsWith("/callback"));
        using (var data = JsonDocument.Parse("""{"id":"i5","application_id":"app","token":"t5","type":4,"channel_id":"100","user":{"id":"7","username":"u"},"data":{"name":"roll"}}"""))
            await (Task)typeof(DiscordEventService).GetMethod("OnDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(autoEvents, ["INTERACTION_CREATE", data.RootElement.Clone()])!;
        Check(http.Requests.Count(r => r.EndsWith("/callback")) == callbacks, "Autocomplete was deferred automatically.");

        // 事件与入站内容。
        await Emit("GUILD_CREATE", """{"id":"200","name":"G","owner_id":"1","member_count":5,"joined_at":"2026-01-01T00:00:00Z","roles":[{"id":"200","name":"@everyone","permissions":"1024"}],"channels":[{"id":"100","type":0,"name":"general"}],"features":["COMMUNITY"]}""");
        Check(received.Last() is PlatformEvent { Kind: DiscordEventKinds.GuildCreate, Raw: DiscordGuildAvailable
              { MemberCount: 5, Guild.Features: ["COMMUNITY"], Channels: [{ ChannelId: 100, GuildId: 200 }], Roles: [{ Permissions: DiscordPermissions.ViewChannel }] } },
            "Guild payload is wrong.");
        await Emit("VOICE_STATE_UPDATE", """{"guild_id":"200","channel_id":null,"user_id":"7","self_mute":true}""");
        Check(received.Last() is PlatformEvent { Raw: DiscordVoiceStateUpdated { UserId: 7, ChannelId: null, IsSelfMuted: true } }, "Voice state is wrong.");
        await Emit("GUILD_ROLE_DELETE", """{"guild_id":"200","role_id":"300"}""");
        Check(received.Last() is PlatformEvent { Raw: DiscordRoleChanged { RoleId: 300, Role: null } }, "Role delete is wrong.");
        await Emit("MESSAGE_UPDATE", """{"id":"900","channel_id":"100","guild_id":"200","content":"edited","author":{"id":"7","username":"u"},"edited_timestamp":"2026-01-02T00:00:00Z"}""");
        Check(received.Last() is PlatformEvent { Kind: DiscordEventKinds.MessageEdited, Raw: DiscordMessageEdited
              { MessageId: "900", Segments: [TextSegment { Text: "edited" }], Message.EditedTimestamp: not null } }, "Message edit payload is wrong.");
        await Emit("MESSAGE_CREATE", """{"id":"901","channel_id":"100","guild_id":"200","timestamp":"2026-01-01T00:00:00Z","author":{"id":"8","username":"v"},"content":"","embeds":[{"title":"E","fields":[{"name":"n","value":"v","inline":true}]}],"sticker_items":[{"id":"5","name":"s","format_type":4}],"poll":{"question":{"text":"Q"},"answers":[{"answer_id":1,"poll_media":{"text":"Yes"}}],"results":{"is_finalized":false,"answer_counts":[{"id":1,"count":3,"me_voted":false}]}}}""");
        var segments = ((MessageEvent)received.Last()).Segments.OfType<RawSegment>().ToArray();
        Check(segments is [{ Payload: DiscordEmbed { Title: "E", Fields: [{ Inline: true }] } }, { Payload: DiscordSticker { Format: DiscordStickerFormat.Gif } },
              { Kind: DiscordSegmentKinds.Poll, Payload: DiscordPoll { Answers: [{ Text: "Yes", Count: 3 }] } }], "Native segments are wrong.");
        await Emit("AUTO_MODERATION_ACTION_EXECUTION", """{"guild_id":"200","rule_id":"1"}""");
        Check(received.Last() is PlatformEvent { Kind: "auto_moderation_action_execution", Raw: JsonElement }, "Unmodeled event lost.");

        // 原始调用、在线状态。
        await using (var stream = new MemoryStream("x"u8.ToArray()))
        {
            var result = await model.CallAsync("post", "channels/100/messages", new JsonObject { ["content"] = "raw" },
                [new DiscordUploadFile("r.txt", stream)], "why");
            Check(http.Last == "POST channels/100/messages" && http.Multipart!.Contains("name=\"files[0]\"; filename=r.txt")
                  && http.Reason == "why" && result.GetProperty("id").GetString() == "900", "Raw multipart call is wrong.");
        }
        var empty = await model.CallAsync("DELETE", "channels/100/messages/900");
        Check(empty.ValueKind == JsonValueKind.Undefined, "204 must return Undefined.");
        await model.SetPresenceAsync(new DiscordPresence { Status = DiscordStatus.Idle, Activity = new(DiscordActivityType.Watching, "logs") });
        Check(gateway.Presence?["status"]?.GetValue<string>() == "idle" && gateway.Presence["activities"]![0]!["type"]!.GetValue<int>() == 3,
            "Presence not kept for Identify.");

        Console.WriteLine("Discord Model 1.1: guild/message/interaction/bot/raw APIs, native segments and typed events verified.");
    }

    private sealed class Transport : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? Last { get; private set; }
        public JsonElement Json { get; private set; }
        public JsonElement? JsonArray { get; private set; }
        public JsonElement? PayloadJson { get; private set; }
        public string? Multipart { get; private set; }
        public string? Reason { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v10/".Length..];
            Last = request.Method.Method + " " + Uri.UnescapeDataString(path);
            Requests.Add(Last);
            Reason = request.Headers.TryGetValues("X-Audit-Log-Reason", out var reasons) ? Uri.UnescapeDataString(reasons.First()) : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            if (request.Content is MultipartFormDataContent form)
            {
                Multipart = body;
                foreach (var part in form)
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json")
                        PayloadJson = JsonDocument.Parse(await part.ReadAsStringAsync(token)).RootElement.Clone();
            }
            else if (!string.IsNullOrEmpty(body))
            {
                var parsed = JsonDocument.Parse(body).RootElement.Clone();
                if (parsed.ValueKind == JsonValueKind.Array) JsonArray = parsed;
                else { Json = parsed; PayloadJson = parsed; }
            }

            string? result = Last switch
            {
                "POST users/@me/channels" => """{"id":"dm-7","type":1}""",
                "GET channels/100/messages/900" => """{"id":"900","channel_id":"100","attachments":[{"id":"old","url":"https://cdn.example/old"}]}""",
                "GET guilds/200/members/7" => """{"user":{"id":"7","username":"u","avatar":"a_hash"},"nick":"Nick","roles":["300"],"joined_at":"2026-01-01T00:00:00Z"}""",
                "GET guilds/200" => """{"id":"200","name":"G","owner_id":"1","roles":[{"id":"200","permissions":"1024"},{"id":"300","permissions":"32"}]}""",
                "POST channels/100/messages/900/threads" => """{"id":"400","type":11,"guild_id":"200","parent_id":"100","name":"Topic"}""",
                "GET applications/@me" => """{"id":"app"}""",
                "PUT applications/app/guilds/200/commands" => """[{"id":"1","name":"roll","type":1,"description":"Roll","guild_id":"200","version":"2"}]""",
                "DELETE channels/100/messages/900" => null,
                _ when request.Method == HttpMethod.Post && path.Contains("/callback") => null,
                _ when request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete => null,
                _ => """{"id":"900","channel_id":"100","timestamp":"2026-01-01T00:00:00Z","attachments":[{"id":"1","url":"https://cdn.example/a.png"}]}"""
            };
            if (Last.StartsWith("PATCH guilds/200/members", StringComparison.Ordinal)) result = null;
            return result is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
