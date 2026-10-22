using System.Net.WebSockets;
using System.Text.Json;
using ShiroBot.Discord.Model;

namespace ShiroBot.DiscordAdapter;

/// <summary>
/// Discord Gateway WebSocket 连接:Hello/Identify/Heartbeat/Dispatch。
/// 单次连接的生命周期由 RunAsync 管理,断开后由调用方决定重连。
/// </summary>
public sealed class DiscordGateway(string botToken, int intents)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private long? _lastSequence;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Dispatch 事件:(事件名, data)。</summary>
    public event Func<string, JsonElement, Task>? DispatchReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lastSequence = null;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),
            cancellationToken);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await ReceiveAsync(socket, cancellationToken);
                if (payload is null)
                {
                    break;
                }

                if (payload.Sequence is { } sequence)
                {
                    _lastSequence = sequence;
                }

                switch (payload.Op)
                {
                    case DcOpcode.Hello:
                        var hello = payload.Data.Deserialize<DcHello>(JsonOptions)!;
                        heartbeatTask = RunHeartbeatAsync(socket, hello.HeartbeatInterval, heartbeatCts.Token);
                        await IdentifyAsync(socket, cancellationToken);
                        break;

                    case DcOpcode.Heartbeat:
                        await SendAsync(socket, new { op = DcOpcode.Heartbeat, d = _lastSequence }, cancellationToken);
                        break;

                    case DcOpcode.Reconnect:
                    case DcOpcode.InvalidSession:
                        return; // 由外层重连

                    case DcOpcode.Dispatch when payload.EventName is { } eventName:
                        if (DispatchReceived is { } handler)
                        {
                            foreach (var subscriber in handler.GetInvocationList().Cast<Func<string, JsonElement, Task>>())
                                await subscriber(eventName, payload.Data);
                        }

                        break;
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask; }
                catch (OperationCanceledException) { /* expected */ }
            }
        }
    }

    private Task IdentifyAsync(ClientWebSocket socket, CancellationToken cancellationToken) =>
        SendAsync(socket, new
        {
            op = DcOpcode.Identify,
            d = new
            {
                token = botToken,
                intents,
                properties = new { os = Environment.OSVersion.Platform.ToString(), browser = "shirobot", device = "shirobot" }
            }
        }, cancellationToken);

    private async Task RunHeartbeatAsync(ClientWebSocket socket, int intervalMs, CancellationToken cancellationToken)
    {
        // 首跳带 jitter
        await Task.Delay((int)(intervalMs * Random.Shared.NextDouble()), cancellationToken);
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await SendAsync(socket, new { op = DcOpcode.Heartbeat, d = _lastSequence }, cancellationToken);
            await Task.Delay(intervalMs, cancellationToken);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await _sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private static async Task<DcGatewayPayload?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<DcGatewayPayload>(stream, JsonOptions, cancellationToken);
    }
}
