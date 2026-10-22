using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.DiscordAdapter;
using ShiroBot.DiscordAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

internal static class Coverage
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Declared(Type concrete, params Type[] contracts)
    {
        foreach (var contract in contracts)
            Check(concrete.GetInterfaceMap(contract).TargetMethods.All(m => m.DeclaringType == concrete),
                $"{concrete.Name} inherits default {contract.Name} method.");
    }
    public static async Task RunAsync()
    {
        Declared(typeof(DiscordMessageService), typeof(IMessageService), typeof(IFileService));
        Declared(typeof(DiscordChannelService), typeof(IChannelService));
        Declared(typeof(DiscordUserService), typeof(IUserService));
        Declared(typeof(DiscordInteractionService), typeof(IMessageInteractionService));
        Declared(typeof(DiscordReactionService), typeof(IMessageReactionService));
        var http = new Transport();
        using var rest = new DiscordRestClient("test-token", http);
        var mapper = new DiscordMapper();
        var interactions = new DiscordInteractionService(rest);
        var events = new DiscordEventService(mapper, () => rest, () => interactions);
        var messages = new DiscordMessageService(rest, mapper, interactions);
        var channels = new DiscordChannelService(rest, mapper);
        var users = new DiscordUserService(rest);
        var adapter = new DiscordAdapter();
        foreach (var (name, service) in new (string, object)[]
        {
            ("_message", messages), ("_user", users), ("_interactions", interactions),
            ("_reactions", new DiscordReactionService(rest))
        }) typeof(DiscordAdapter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(adapter, service);
        Check(adapter.GetExtension<IFileService>() is not null && adapter.GetExtension<IMessageReactionService>() is not null
            && adapter.GetExtension<IMessageInteractionService>() is not null && adapter.GetExtension<IDiscordModelService>() is not null,
            "Optional services are not exposed through the adapter.");
        var target = new Channel("100", ChannelType.Thread) { GuildId = "200" };
        var received = new List<BotEvent>();
        events.EventReceived += e => { received.Add(e); return Task.CompletedTask; };
        async Task Emit(string kind, string json)
        {
            using var data = JsonDocument.Parse(json);
            await (Task)typeof(DiscordEventService).GetMethod("OnDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(events, [kind, data.RootElement])!;
        }
        await Emit("THREAD_CREATE", """{"id":"100","type":11,"guild_id":"200","name":"Thread"}""");
        await Emit("MESSAGE_CREATE", """{"id":"1","channel_id":"100","guild_id":"200","timestamp":"2026-01-01T00:00:00Z","author":{"id":"7","username":"user"},"content":"@everyone hello <@7> @here","mention_everyone":true,"attachments":[{"id":"8","url":"https://example.org/old","filename":"a.png","content_type":"image/png"}],"future_field":true}""");
        var incoming = (MessageEvent)received.Last();
        Check(incoming.Channel.Type == ChannelType.Thread && incoming.Segments.OfType<MentionAllSegment>().Count() == 2, "Thread or mentions not mapped.");
        Check(((DcMessage)incoming.Raw!).AdditionalData!["future_field"].GetBoolean(), "Unknown fields lost.");
        Check(await messages.GetResourceUrlAsync("8") == "https://example.org/new", "Attachment ID not resolved/refreshed.");
        Check((await users.GetDiscordUserAsync("7")).GlobalName == "Global", "Shared user Model lost fields.");
        Check((await users.GetDiscordMemberAsync("100", "7")).Nickname == "Nick", "Shared member Model lost nickname.");
        Check((await channels.GetMemberAsync("100", "7"))?.Nick == "Nick", "Member API missing.");
        Check((await channels.GetMembersAsync("100")).Count == 1, "Member list API missing.");
        await channels.GetChannelsAsync();
        await channels.SetChannelNameAsync("100", "Name");
        await channels.KickMemberAsync("100", "7");
        await channels.MuteMemberAsync("100", "7", TimeSpan.FromMinutes(5));
        await channels.LeaveChannelAsync("100");
        Check((await messages.GetMessageAsync(target, "1"))?.Channel.Type == ChannelType.Thread, "Message lookup lost thread type.");
        Check((await messages.GetHistoryMessagesAsync(target)).Count == 1, "History API missing.");
        await messages.DeleteMessageAsync(target, "1");
        foreach (var action in new Func<Task>[] { () => users.GetFriendsAsync(), () => users.AcceptFriendRequestAsync("x"), () => users.RejectFriendRequestAsync("x") })
        {
            try { await action(); throw new Exception("Unsupported API pretended to succeed."); } catch (NotSupportedException) { }
        }
        await messages.SendMessageAsync(target, new OutgoingMessage
        {
            Segments = [new RawSegment("discord", "sticker", new DcStickerItem { Id = "9", Name = "Sticker" })]
        });
        Check(http.LastJson.GetProperty("sticker_ids")[0].GetString() == "9", "Native sticker send missing.");
        var button = new MessageButtonLayout([new([new("button", "Click", new CallbackAction("value")), new("link", "Open", new OpenUrlAction("https://example.org"))])]);
        await messages.SendMessageAsync(target, new OutgoingMessage { Segments = [new TextSegment("hello"), new CardSegment
            { Title = "Card", Description = "Description", ImageUrl = "https://example.org/image.png", Fields = [new("Field", "Value")] }], Buttons = button });
        var sentPayload = http.LastJson;
        Check(sentPayload.GetProperty("embeds")[0].GetProperty("image").GetProperty("url").GetString()!.EndsWith("image.png"), "Card image lost.");
        var customId = sentPayload.GetProperty("components")[0].GetProperty("components")[0].GetProperty("custom_id").GetString();
        await Emit("INTERACTION_CREATE", JsonSerializer.Serialize(new { id = "interaction", application_id = "app", token = "private-token",
            channel_id = "100", guild_id = "200", type = 3, member = new { user = new { id = "7", username = "user" } },
            data = new { custom_id = customId }, message = new { id = "1" } }));
        var interaction = (InteractionEvent)received.Last();
        Check(interaction.ButtonId == "button" && interaction.Data == "value" && interaction.IsAutomaticallyAcknowledged, "Interaction mapping failed.");
        var ackCount = http.Paths.Count(p => p.Contains("/callback"));
        await interactions.AcknowledgeAsync(interaction);
        Check(http.Paths.Count(p => p.Contains("/callback")) == ackCount, "Acknowledgement not idempotent.");
        await messages.SendMessageAsync(target, new OutgoingMessage { Segments = [new TextSegment("reply")], ReplyToInteraction = new("instance", target, "interaction") });
        Check(http.LastPath.Contains("messages/@original") && http.LastVerb == HttpMethod.Patch, "Interaction reply endpoint wrong.");
        var uploaded = await messages.UploadAsync(target, new() { Uri = "base64://aGVsbG8=", FileName = "hello.txt" });
        Check(uploaded.IsPublished && http.LastMultipart?.Contains("hello.txt") == true && http.LastMultipart.Contains("hello"), "Multipart upload missing.");
        Check(messages.AssessMessage(target, new() { Segments = [new FileSegment(uploaded.FileId)] }).IsSupported, "Uploaded handle not reusable.");
        Check(!messages.AssessMessage(new Channel("999", ChannelType.Group), new() { Segments = [new FileSegment(uploaded.FileId)] }).IsSupported, "Uploaded handle crossed channels.");
        await messages.SendMessageAsync(target, new OutgoingMessage { Segments = [new ImageSegment("data:image/png;base64,aGVsbG8=")] });
        Check(http.LastMultipart!.Contains("files[0]"), "Native image upload missing.");
        await Emit("MESSAGE_REACTION_ADD", """{"user_id":"7","channel_id":"100","guild_id":"200","message_id":"1","emoji":{"id":"9","name":"custom"}}""");
        Check(received.Last() is MessageReactionEvent { Emoji: PlatformReactionEmoji, IsAdded: true }, "Typed reaction event missing.");
        var reactions = new DiscordReactionService(rest);
        await reactions.SetReactionAsync(new("instance", target, "1"), new UnicodeReactionEmoji("👍"));
        Check(http.LastVerb == HttpMethod.Put && http.LastPath.Contains("reactions/"), "Reaction add missing.");
        await reactions.SetReactionAsync(new("instance", target, "1"), new PlatformReactionEmoji("custom:9", "instance"), false);
        Check(http.LastVerb == HttpMethod.Delete, "Reaction removal missing.");
        await Emit("MESSAGE_DELETE_BULK", """{"ids":["1","2"],"channel_id":"100","guild_id":"200"}""");
        Check(received.TakeLast(2).OfType<MessageDeletedEvent>().Count() == 2, "Bulk deletion mapping missing.");
        await Emit("FUTURE_EVENT", """{"keep":true}""");
        Check(received.Last() is PlatformEvent { Kind: "future_event", Raw: JsonElement }, "Future event lost.");
        Console.WriteLine("Discord: every SDK API explicitly implemented; native send/upload/buttons/interaction/reaction/model tests passed.");
    }
    private sealed class Transport : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string LastPath { get; private set; } = "";
        public HttpMethod LastVerb { get; private set; } = HttpMethod.Get;
        public JsonElement LastJson { get; private set; }
        public string? LastMultipart { get; private set; }
        private const string Message = """{"id":"1","channel_id":"100","guild_id":"200","timestamp":"2026-01-01T00:00:00Z","author":{"id":"7","username":"user"},"content":"hello","attachments":[{"id":"8","filename":"hello.txt","url":"https://example.org/new"}]}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastPath = request.RequestUri!.PathAndQuery; Paths.Add(LastPath); LastVerb = request.Method;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(token);
                if (request.Content is MultipartFormDataContent) LastMultipart = body;
                else LastJson = JsonDocument.Parse(body).RootElement.Clone();
            }
            var bodyText = LastPath switch
            {
                var p when p.Contains("/users/7") || p.EndsWith("/users/@me") => """{"id":"7","username":"user","global_name":"Global"}""",
                var p when p.EndsWith("/guilds/200") => """{"id":"200","owner_id":"999"}""",
                var p when p.EndsWith("/guilds/200/roles") => """[{"id":"200","permissions":"0"}]""",
                var p when p.Contains("/members/7") => """{"user":{"id":"7","username":"user","global_name":"Global"},"nick":"Nick"}""",
                var p when p.Contains("/members?") => """[{"user":{"id":"7","username":"user"},"nick":"Nick"}]""",
                var p when p.Contains("users/@me/guilds") && request.Method == HttpMethod.Get => """[{"id":"200","name":"Guild"}]""",
                var p when p.EndsWith("/guilds/200/threads/active") => """{"threads":[]}""",
                var p when p.EndsWith("/guilds/200/channels") => """[{"id":"101","type":0,"guild_id":"200"}]""",
                var p when p.EndsWith("/channels/100") && request.Method == HttpMethod.Get => """{"id":"100","type":11,"guild_id":"200"}""",
                var p when p.Contains("/messages?") => "[" + Message + "]",
                _ => Message
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(bodyText, Encoding.UTF8, "application/json") };
        }
    }
}
