using ShiroBot.Model.Discord;

namespace ShiroBot.DiscordAdapter.Services;

/// <summary>返回宿主共享 Discord Model；与私有协议 DTO 分离。</summary>
public interface IDiscordModelService
{
    Task<DiscordUser> GetDiscordUserAsync(string? userId = null, CancellationToken cancellationToken = default);
    Task<DiscordUser> GetDiscordMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default);
}
