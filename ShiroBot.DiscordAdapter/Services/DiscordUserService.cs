using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordUserService(DiscordRestClient rest) : IUserService
{
    public async Task<Sdk.User> GetSelfAsync()
    {
        var me = await rest.GetAsync<DcUser>("users/@me");
        return DiscordMapper.ToUser(me);
    }

    public async Task<Sdk.User?> GetUserAsync(string userId)
    {
        var user = await rest.GetAsync<DcUser>($"users/{userId}");
        return DiscordMapper.ToUser(user);
    }
}
