using System.Text.Json;
using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordInteractionService(DiscordRestClient rest) : IMessageInteractionService
{
    private sealed class Pending(string token, string applicationId, Channel channel)
    {
        public string Token { get; } = token;
        public string ApplicationId { get; } = applicationId;
        public Channel Channel { get; } = channel;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Acknowledged { get; set; }
    }
    private readonly BoundedCache<Pending> _pending = new();

    internal async Task RegisterAsync(JsonElement data, Channel channel, CancellationToken token = default)
    {
        var id = data.GetProperty("id").GetString()!;
        _pending.Set(id, new(data.GetProperty("token").GetString()!, data.GetProperty("application_id").GetString()!, channel));
        await AcknowledgeCoreAsync(id, channel, token);
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

    private async Task AcknowledgeCoreAsync(string id, Channel? channel, CancellationToken token)
    {
        var pending = Get(id, channel);
        await pending.Gate.WaitAsync(token);
        try
        {
            if (pending.Acknowledged) return;
            await rest.PostAsync($"interactions/{id}/{Uri.EscapeDataString(pending.Token)}/callback", new { type = 5 }, token);
            pending.Acknowledged = true;
        }
        finally { pending.Gate.Release(); }
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
