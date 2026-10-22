using System.Reflection;
using System.Text.Json;
using ShiroBot.DiscordAdapter;
using ShiroBot.DiscordAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Dispatch(DiscordEventService events, string name, string json)
{
    using var document = JsonDocument.Parse(json);
    var method = typeof(DiscordEventService).GetMethod("OnDispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    await (Task)method.Invoke(events, [name, document.RootElement])!;
}

// Verify calls made through SDK interfaces reach the adapter, including cancellation.
using var rest = new DiscordRestClient("verification-token");
IMessageService messages = new DiscordMessageService(rest, new DiscordMapper());
IChannelService channels = new DiscordChannelService(rest);
IUserService users = new DiscordUserService(rest);
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
foreach (var operation in new Func<Task>[]
{
    () => messages.SendMessageAsync(new Channel("123", ChannelType.Group), new OutgoingMessage { Segments = [new TextSegment("hello")] }, cancelled.Token),
    () => messages.DeleteMessageAsync(new Channel("123", ChannelType.Group), "456", cancelled.Token),
    () => channels.GetChannelAsync("123", cancelled.Token),
    () => channels.GetMembersAsync("123", cancelled.Token),
    () => users.GetSelfAsync(cancelled.Token)
})
{
    try { await operation(); throw new Exception("Cancelled SDK call completed."); }
    catch (OperationCanceledException) { }
}
Check(messages.AssessMessage(new Channel("123", ChannelType.Group), new OutgoingMessage
    { Segments = [new MarkdownSegment("**hello**")] }).IsSupported, "Markdown capability missing.");

// READY must update identity even before a subscriber is attached. Identities and DM
// channel caches must remain isolated when the host runs two Discord instances.
var eventsA = new DiscordEventService(new DiscordMapper());
var eventsB = new DiscordEventService(new DiscordMapper());
await Dispatch(eventsA, "READY", """{"user":{"id":"bot-a","username":"A"},"session_id":"a"}""");
await Dispatch(eventsB, "READY", """{"user":{"id":"bot-b","username":"B"},"session_id":"b"}""");
var received = new List<MessageEvent>();
eventsA.EventReceived += async e => { await Task.Yield(); received.Add((MessageEvent)e); };
eventsA.EventReceived += e => { Check(received.Count == 1, "Subscriber was not awaited."); return Task.CompletedTask; };
eventsB.EventReceived += e => { received.Add((MessageEvent)e); return Task.CompletedTask; };
const string dm = """{"id":"1","channel_id":"dm-a","author":{"id":"human","username":"Human"},"content":"ping"}""";
await Dispatch(eventsA, "MESSAGE_CREATE", dm);
await Dispatch(eventsB, "MESSAGE_CREATE", """{"id":"2","channel_id":"guild-channel","guild_id":"guild","author":{"id":"human","username":"Human"},"content":"ping"}""");
Check(received.Count == 2 && received[0].SelfId == "bot-a" && received[1].SelfId == "bot-b", "Bot identities leaked between instances.");
Check(received[0].Channel.Type == ChannelType.Direct && received[1].Channel.Type == ChannelType.Group, "Channel mapping failed.");

var adapter = new DiscordAdapter();
var config = new DiscordAdapterConfig { BotToken = "first" };
await adapter.OnConfigLoadedAsync(config, default);
Check(ReferenceEquals(adapter.CurrentConfigValue, config), "Loaded config not applied.");
var replacement = new DiscordAdapterConfig { BotToken = "second" };
await adapter.OnConfigChangedAsync(config, replacement, default);
Check(ReferenceEquals(adapter.CurrentConfigValue, replacement), "Changed config not applied.");
Check(((IConfigurableAdapter)adapter).ApplyMode == ConfigApplyMode.RestartComponent, "Config must restart transport.");
await adapter.StopAsync();
await adapter.StopAsync();
await Coverage.RunAsync();
Console.WriteLine("Verified SDK dispatch/cancellation, capabilities, event ordering, instance isolation and configuration lifecycle.");
