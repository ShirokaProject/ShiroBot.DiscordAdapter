using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordChannelService(DiscordRestClient rest) : IChannelService
{
    public async Task<Sdk.Channel?> GetChannelAsync(string channelId)
    {
        var channel = await rest.GetAsync<DcChannel>($"channels/{channelId}");
        return DiscordMapper.ToChannel(channel);
    }

    public async Task<IReadOnlyList<Sdk.Channel>> GetChannelsAsync()
    {
        var guilds = await rest.GetAsync<IReadOnlyList<DcGuild>>("users/@me/guilds");
        var channels = new List<Sdk.Channel>();

        foreach (var guild in guilds)
        {
            var guildChannels = await rest.GetAsync<IReadOnlyList<DcChannel>>($"guilds/{guild.Id}/channels");
            channels.AddRange(guildChannels
                .Where(channel => channel.Type is DcChannelType.GuildText or DcChannelType.GuildAnnouncement)
                .Select(DiscordMapper.ToChannel));
        }

        return channels;
    }

    public async Task<Member?> GetMemberAsync(string channelId, string userId)
    {
        var guildId = await ResolveGuildIdAsync(channelId);
        var member = await rest.GetAsync<DcGuildMember>($"guilds/{guildId}/members/{userId}");
        return DiscordMapper.ToMember(member, member.User ?? new DcUser { Id = userId, Username = userId });
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId)
    {
        var guildId = await ResolveGuildIdAsync(channelId);
        var members = await rest.GetAsync<IReadOnlyList<DcGuildMember>>($"guilds/{guildId}/members?limit=1000");
        return members
            .Where(member => member.User is not null)
            .Select(member => DiscordMapper.ToMember(member, member.User!))
            .ToArray();
    }

    public Task SetChannelNameAsync(string channelId, string name) =>
        rest.PatchAsync($"channels/{channelId}", new { name });

    public async Task KickMemberAsync(string channelId, string userId)
    {
        var guildId = await ResolveGuildIdAsync(channelId);
        await rest.DeleteAsync($"guilds/{guildId}/members/{userId}");
    }

    public async Task MuteMemberAsync(string channelId, string userId, TimeSpan duration)
    {
        var guildId = await ResolveGuildIdAsync(channelId);
        await rest.PatchAsync($"guilds/{guildId}/members/{userId}", new
        {
            communication_disabled_until = DateTimeOffset.UtcNow.Add(duration).ToString("O")
        });
    }

    public async Task LeaveChannelAsync(string channelId)
    {
        var guildId = await ResolveGuildIdAsync(channelId);
        await rest.DeleteAsync($"users/@me/guilds/{guildId}");
    }

    private async Task<string> ResolveGuildIdAsync(string channelId)
    {
        var channel = await rest.GetAsync<DcChannel>($"channels/{channelId}");
        return channel.GuildId
               ?? throw new InvalidOperationException($"Channel {channelId} 不属于任何 guild。");
    }
}
