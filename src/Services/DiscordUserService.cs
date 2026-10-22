using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordUserService(DiscordRestClient rest) : IUserService, IDiscordModelService
{
    public async Task<Sdk.User> GetSelfAsync(CancellationToken cancellationToken = default)
    {
        var me = await rest.GetAsync<DcUser>("users/@me", cancellationToken);
        return DiscordMapper.ToUser(me);
    }

    public async Task<Sdk.User?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await rest.GetAsync<DcUser>($"users/{userId}", cancellationToken);
        return DiscordMapper.ToUser(user);
    }

    public Task<IReadOnlyList<Sdk.User>> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Discord Bot API 不提供好友列表。");
    public Task AcceptFriendRequestAsync(string token, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Discord 机器人不能接受好友请求。");
    public Task RejectFriendRequestAsync(string token, string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Discord 机器人不能拒绝好友请求。");

    public async Task<ShiroBot.Model.Discord.DiscordUser> GetDiscordUserAsync(string? userId = null, CancellationToken cancellationToken = default) =>
        DiscordMapper.ToPlatformUser(await rest.GetAsync<DcUser>($"users/{userId ?? "@me"}", cancellationToken));

    public async Task<ShiroBot.Model.Discord.DiscordUser> GetDiscordMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        var channel = await rest.GetAsync<DcChannel>($"channels/{channelId}", cancellationToken);
        var guild = channel.GuildId ?? throw new ArgumentException("私聊没有服务器成员资料。");
        var member = await rest.GetAsync<DcGuildMember>($"guilds/{guild}/members/{userId}", cancellationToken);
        var user = member.User ?? await rest.GetAsync<DcUser>($"users/{userId}", cancellationToken);
        return DiscordMapper.ToPlatformUser(user, member.Nick);
    }
}
