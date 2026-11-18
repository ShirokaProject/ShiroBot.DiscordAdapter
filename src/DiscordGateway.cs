using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Discord.Model;

namespace ShiroBot.DiscordAdapter;

/// <summary>Gateway 关闭码表示配置错误（Token、Intents、版本），重连没有意义。</summary>
public sealed class DiscordGatewayFatalException(int closeCode, string reason)
    : InvalidOperationException($"Discord Gateway 拒绝连接 ({closeCode})：{reason}")
{
    public int CloseCode { get; } = closeCode;
}

/// <summary>
/// Discord Gateway WebSocket 连接：Hello/Identify/Resume/Heartbeat/Dispatch。
/// 单次连接由 RunAsync 管理；会话 ID 和序号跨连接保留，调用方重连时自动 Resume，不丢事件。
/// </summary>
public sealed class DiscordGateway(string botToken, int intents)
{
    public static readonly Uri DefaultUrl = new("wss://gateway.discord.gg/?v=10&encoding=json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long? _lastSequence;
    private string? _sessionId;
    private Uri? _resumeUrl;
    private volatile bool _heartbeatAcknowledged = true;
    private ClientWebSocket? _socket;

    /// <summary>Dispatch 事件:(事件名, data)。</summary>
    public event Func<string, JsonElement, Task>? DispatchReceived;

    /// <summary>当前会话可以 Resume。</summary>
    public bool CanResume => _sessionId is not null && _lastSequence is not null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var resuming = CanResume;
        var url = resuming && _resumeUrl is { } resume ? new Uri(resume, "?v=10&encoding=json") : DefaultUrl;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(url, cancellationToken);
        _socket = socket;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await ReceiveAsync(socket, cancellationToken);
                if (payload is null)
                {
                    HandleClose(socket.CloseStatus is { } status ? (int)status : null);
                    break;
                }

                await HandlePayloadAsync(payload, socket, resuming, () =>
                {
                    _heartbeatAcknowledged = true;
                    heartbeatTask = RunHeartbeatAsync(socket, payload.Data.Deserialize<DcHello>(JsonOptions)!.HeartbeatInterval,
                        heartbeatCts.Token);
                }, cancellationToken);
                if (payload.Op is DcOpcode.Reconnect or DcOpcode.InvalidSession) return;
            }
        }
        finally
        {
            _socket = null;
            heartbeatCts.Cancel();
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask; }
                catch (OperationCanceledException) { /* expected */ }
            }
        }
    }

    /// <summary>处理一帧；Reconnect 保留会话，InvalidSession 视 d 决定能否 Resume。</summary>
    public async Task HandlePayloadAsync(DcGatewayPayload payload, ClientWebSocket? socket, bool resuming,
        Action startHeartbeat, CancellationToken cancellationToken)
    {
        if (payload.Sequence is { } sequence) _lastSequence = sequence;
        switch (payload.Op)
        {
            case DcOpcode.Hello:
                startHeartbeat();
                if (socket is null) break;
                if (resuming) await ResumeAsync(socket, cancellationToken);
                else await IdentifyAsync(socket, cancellationToken);
                break;

            case DcOpcode.Heartbeat when socket is not null:
                await SendRawAsync(socket, new { op = DcOpcode.Heartbeat, d = _lastSequence }, cancellationToken);
                break;

            case DcOpcode.HeartbeatAck:
                _heartbeatAcknowledged = true;
                break;

            case DcOpcode.InvalidSession:
                if (payload.Data.ValueKind != JsonValueKind.True) ResetSession();
                // 文档要求等待 1–5 秒再重新连接。
                await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 4), cancellationToken);
                break;

            case DcOpcode.Dispatch when payload.EventName is { } eventName:
                if (eventName == "READY")
                {
                    var ready = payload.Data.Deserialize<DcReady>(JsonOptions)!;
                    _sessionId = ready.SessionId;
                    _resumeUrl = ready.ResumeGatewayUrl is { } resumeUrl ? new Uri(resumeUrl) : null;
                }

                if (DispatchReceived is { } handler)
                    foreach (var subscriber in handler.GetInvocationList().Cast<Func<string, JsonElement, Task>>())
                        await subscriber(eventName, payload.Data);
                break;
        }
    }

    /// <summary>发送 Gateway 指令，例如 op 3 更新在线状态、op 8 请求成员列表。</summary>
    public async Task SendAsync(int op, JsonNode? data, CancellationToken cancellationToken = default)
    {
        if (op is DcOpcode.Identify or DcOpcode.Resume or DcOpcode.Heartbeat)
            throw new ArgumentException("Identify、Resume 和心跳由适配器管理。", nameof(op));
        var socket = _socket ?? throw new InvalidOperationException("Discord Gateway 未连接。");
        await SendRawAsync(socket, new JsonObject { ["op"] = op, ["d"] = data?.DeepClone() }, cancellationToken);
    }

    private void ResetSession()
    {
        _sessionId = null;
        _resumeUrl = null;
        _lastSequence = null;
    }

    /// <summary>按关闭码决定：配置错误抛出不可重试异常；会话失效时下次重新 Identify；其他情况 Resume。</summary>
    public void HandleClose(int? code)
    {
        switch (code)
        {
            case 4004: throw new DiscordGatewayFatalException(4004, "Bot Token 无效。");
            case 4010: throw new DiscordGatewayFatalException(4010, "分片参数无效。");
            case 4011: throw new DiscordGatewayFatalException(4011, "机器人加入的服务器过多，需要分片。");
            case 4012: throw new DiscordGatewayFatalException(4012, "Gateway 版本无效。");
            case 4013: throw new DiscordGatewayFatalException(4013, "Intents 位标志无效。");
            case 4014:
                throw new DiscordGatewayFatalException(4014,
                    "使用了未授权的特权 Intents；请在 Developer Portal 开启 Server Members、Presence 或 Message Content，或调整配置。");
            case 4007 or 4009:
                ResetSession(); // 序号无效或会话超时，只能重新 Identify
                break;
        }
    }

    private Task IdentifyAsync(ClientWebSocket socket, CancellationToken cancellationToken) =>
        SendRawAsync(socket, new
        {
            op = DcOpcode.Identify,
            d = new
            {
                token = botToken,
                intents,
                properties = new { os = Environment.OSVersion.Platform.ToString(), browser = "shirobot", device = "shirobot" }
            }
        }, cancellationToken);

    private Task ResumeAsync(ClientWebSocket socket, CancellationToken cancellationToken) =>
        SendRawAsync(socket, new
        {
            op = DcOpcode.Resume,
            d = new { token = botToken, session_id = _sessionId, seq = _lastSequence }
        }, cancellationToken);

    private async Task RunHeartbeatAsync(ClientWebSocket socket, int intervalMs, CancellationToken cancellationToken)
    {
        // 首跳带 jitter
        await Task.Delay((int)(intervalMs * Random.Shared.NextDouble()), cancellationToken);
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!_heartbeatAcknowledged)
            {
                // 上一跳没有 ACK：连接已僵死，断开后由外层 Resume。
                socket.Abort();
                return;
            }

            _heartbeatAcknowledged = false;
            await SendRawAsync(socket, new { op = DcOpcode.Heartbeat, d = _lastSequence }, cancellationToken);
            await Task.Delay(intervalMs, cancellationToken);
        }
    }

    private async Task SendRawAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
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
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, cancellationToken); }
            catch (WebSocketException) when (!cancellationToken.IsCancellationRequested) { return null; }
            if (result.MessageType == WebSocketMessageType.Close) return null;

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<DcGatewayPayload>(stream, JsonOptions, cancellationToken);
    }
}
