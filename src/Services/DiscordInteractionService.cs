using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

/// <param name="autoAcknowledge">收到交互立即发送延迟回复；关闭后由插件在 3 秒内首次回复。</param>
public sealed class DiscordInteractionService(DiscordRestClient rest, bool autoAcknowledge = true) : IMessageInteractionService
{
    private sealed class Pending(string token, string applicationId, Channel channel, int type)
    {
        public string Token { get; } = token;
        public string ApplicationId { get; } = applicationId;
        public Channel Channel { get; } = channel;
        public int Type { get; } = type;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Acknowledged { get; set; }
    }
    private readonly BoundedCache<Pending> _pending = new();

    public bool AutoAcknowledge => autoAcknowledge;

    internal async Task RegisterAsync(JsonElement data, Channel channel, CancellationToken token = default)
    {
        var id = data.GetProperty("id").GetString()!;
        var type = data.TryGetProperty("type", out var kind) ? kind.GetInt32() : 0;
        _pending.Set(id, new(data.GetProperty("token").GetString()!, data.GetProperty("application_id").GetString()!, channel, type));
        // 自动补全只能用选项回答，不能延迟；交给插件或在超时后失效。
        if (autoAcknowledge && type != 4) await AcknowledgeCoreAsync(id, channel, token);
    }

    public Task AcknowledgeAsync(InteractionEvent interaction, CancellationToken cancellationToken = default) =>
        AcknowledgeCoreAsync(interaction.InteractionId, interaction.Channel, cancellationToken);

    private Pending Get(string id, Channel? channel)
    {
        if (!_pending.TryGet(id, out var pending) || pending.Expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Discord 交互已过期或不属于当前适配器实例。");
        if (channel is not null && (channel.Id != pending.Channel.Id || channel.Type != pending.Channel.Type))
            throw new ArgumentException("交互与目标频道不匹配。");
        return pending;
    }

    private Task AcknowledgeCoreAsync(string id, Channel? channel, CancellationToken token) =>
        InitialResponseAsync(id, channel, new JsonObject { ["type"] = 5 }, [], ifPending: true, token);

    /// <summary>
    ///     发送首次回复（callback）。每个交互只能有一次；<paramref name="ifPending"/> 为 true 时已回复则跳过，
    ///     否则抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal async Task InitialResponseAsync(string id, Channel? channel, JsonObject response, IReadOnlyList<ResourceBytes> files,
        bool ifPending, CancellationToken token)
    {
        var pending = Get(id, channel);
        await pending.Gate.WaitAsync(token);
        try
        {
            if (pending.Acknowledged)
            {
                if (ifPending) return;
                throw new InvalidOperationException("该交互已经回复过（适配器可能已自动确认）；请使用 EditOriginalAsync 或 FollowupAsync。");
            }
            await rest.SendAsync(HttpMethod.Post, $"interactions/{id}/{Uri.EscapeDataString(pending.Token)}/callback", response, files,
                cancellationToken: token);
            pending.Acknowledged = true;
        }
        finally { pending.Gate.Release(); }
    }

    internal int InteractionType(string id) => Get(id, null).Type;

    /// <summary>交互的 Webhook 路径，用于编辑原回复和后续消息。</summary>
    internal (string Path, Channel Channel) Webhook(string id)
    {
        var pending = Get(id, null);
        return ($"webhooks/{pending.ApplicationId}/{Uri.EscapeDataString(pending.Token)}", pending.Channel);
    }

    internal async Task<DcMessage> ReplyAsync(InteractionReference reference, object payload,
        IReadOnlyList<ResourceBytes> files, CancellationToken token)
    {
        var pending = Get(reference.InteractionId, reference.Channel);
        await AcknowledgeCoreAsync(reference.InteractionId, reference.Channel, token);
        return await rest.SendPayloadAsync<DcMessage>(HttpMethod.Patch,
            $"webhooks/{pending.ApplicationId}/{Uri.EscapeDataString(pending.Token)}/messages/@original", payload, files, token);
    }
}
