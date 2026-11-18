using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.DiscordAdapter;

/// <summary>Gateway 会话恢复与关闭码处理。</summary>
internal static class GatewayCoverage
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static DcGatewayPayload Frame(string json) => JsonSerializer.Deserialize<DcGatewayPayload>(json)!;

    public static async Task RunAsync()
    {
        var gateway = new DiscordGateway("token", DcIntents.Default);
        var dispatched = new List<string>();
        gateway.DispatchReceived += (name, _) => { dispatched.Add(name); return Task.CompletedTask; };
        Check(!gateway.CanResume, "A new gateway must Identify.");

        await gateway.HandlePayloadAsync(Frame("""{"op":0,"s":1,"t":"READY","d":{"user":{"id":"1","username":"Bot"},"session_id":"s","resume_gateway_url":"wss://resume.example"}}"""),
            null, false, () => { }, default);
        await gateway.HandlePayloadAsync(Frame("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{}}"""), null, false, () => { }, default);
        Check(gateway.CanResume && dispatched.SequenceEqual(["READY", "MESSAGE_CREATE"]), "Session not kept for Resume.");

        await gateway.HandlePayloadAsync(Frame("""{"op":7,"d":null}"""), null, false, () => { }, default);
        Check(gateway.CanResume, "Reconnect (op 7) must keep the session.");
        await gateway.HandlePayloadAsync(Frame("""{"op":9,"d":true}"""), null, false, () => { }, default);
        Check(gateway.CanResume, "Resumable invalid session dropped the session.");
        await gateway.HandlePayloadAsync(Frame("""{"op":9,"d":false}"""), null, false, () => { }, default);
        Check(!gateway.CanResume, "Non-resumable invalid session kept the session.");

        await gateway.HandlePayloadAsync(Frame("""{"op":0,"s":1,"t":"READY","d":{"user":{"id":"1","username":"Bot"},"session_id":"s"}}"""),
            null, false, () => { }, default);
        gateway.HandleClose(1001);
        Check(gateway.CanResume, "Ordinary close dropped the session.");
        gateway.HandleClose(4009);
        Check(!gateway.CanResume, "Session timeout must force a fresh Identify.");
        foreach (var fatal in new[] { 4004, 4010, 4011, 4012, 4013, 4014 })
        {
            try
            {
                gateway.HandleClose(fatal);
                throw new Exception($"Close code {fatal} must stop reconnecting.");
            }
            catch (DiscordGatewayFatalException error) when (error.CloseCode == fatal) { }
        }

        Check((DcIntents.Default & DcIntents.GuildPresences) == 0 && (DcIntents.Default & DcIntents.MessageContent) != 0,
            "Default intents changed unexpectedly.");
        Console.WriteLine("Discord Gateway: Resume, invalid sessions and fatal close codes verified.");
    }
}
