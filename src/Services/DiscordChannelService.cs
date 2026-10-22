using ShiroBot.Discord.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.DiscordAdapter.Services;

public sealed class DiscordChannelService(DiscordRestClient rest, DiscordMapper? mapper = null) : IChannelService
{
    public async Task<Sdk.Channel?> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var channel = await rest.GetAsync<DcChannel>($"channels/{channelId}", cancellationToken);
        mapper?.Channels.Set(channel.Id, channel);
        return DiscordMapper.ToChannel(channel);
    }

    public async Task<IReadOnlyList<Sdk.Channel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var guilds = new List<DcGuild>();
        string? after = null;
        while (true)
        {
            var page = await rest.GetAsync<IReadOnlyList<DcGuild>>("users/@me/guilds?limit=200" + (after is null ? "" : "&after=" + after), cancellationToken);
            guilds.AddRange(page);
            var next = page.LastOrDefault()?.Id;
            if (page.Count < 200 || next is null || next == after) break;
            after = next;
        }
        var channels = new Dictionary<string, Sdk.Channel>();
        foreach (var guild in guilds)
        {
            var guildChannels = await rest.GetAsync<IReadOnlyList<DcChannel>>($"guilds/{guild.Id}/channels", cancellationToken);
            foreach (var channel in guildChannels.Where(c => c.Type is DcChannelType.GuildText or DcChannelType.GuildAnnouncement))
            {
                var native = channel with { GuildId = channel.GuildId ?? guild.Id };
                mapper?.Channels.Set(native.Id, native);
                channels[native.Id] = DiscordMapper.ToChannel(native);
            }
            var active = await rest.GetAsync<System.Text.Json.JsonElement>($"guilds/{guild.Id}/threads/active", cancellationToken);
            if (active.TryGetProperty("threads", out var threads))
                foreach (var item in threads.EnumerateArray())
                {
                    var native = System.Text.Json.JsonSerializer.Deserialize<DcChannel>(item.GetRawText())!;
                    native = native with { GuildId = native.GuildId ?? guild.Id };
                    mapper?.Channels.Set(native.Id, native);
                    channels[native.Id] = DiscordMapper.ToChannel(native);
                }
        }
        return channels.Values.ToArray();
    }

    public async Task<Member?> GetMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        var guildId = await ResolveGuildIdAsync(channelId, cancellationToken);
        var member = await rest.GetAsync<DcGuildMember>($"guilds/{guildId}/members/{userId}", cancellationToken);
        var role = await GetRoleAsync(guildId, userId, member, cancellationToken);
        return DiscordMapper.ToMember(member, member.User ?? new DcUser { Id = userId, Username = userId }) with { Role = role };
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var guildId = await ResolveGuildIdAsync(channelId, cancellationToken);
        var result = new List<Member>();
        var guild = await rest.GetAsync<DcGuild>($"guilds/{guildId}", cancellationToken);
        var nativeRoles = await rest.GetAsync<System.Text.Json.JsonElement>($"guilds/{guildId}/roles", cancellationToken);
        var administratorRoles = nativeRoles.EnumerateArray()
            .Where(r => ulong.TryParse(r.GetProperty("permissions").GetString(), out var permissions) && (permissions & 8) != 0)
            .Select(r => r.GetProperty("id").GetString()!).ToHashSet();
        string? after = null;
        while (true)
        {
            var page = await rest.GetAsync<IReadOnlyList<DcGuildMember>>(
                $"guilds/{guildId}/members?limit=1000" + (after is null ? "" : "&after=" + after), cancellationToken);
            result.AddRange(page.Where(m => m.User is not null).Select(m => DiscordMapper.ToMember(m, m.User!) with
            {
                Role = m.User!.Id == guild.OwnerId ? MemberRole.Owner
                    : administratorRoles.Contains(guildId) || m.Roles?.Any(administratorRoles.Contains) == true ? MemberRole.Admin : MemberRole.Member
            }));
            var next = page.LastOrDefault()?.User?.Id;
            if (page.Count < 1000 || next is null || next == after) break;
            after = next;
        }
        return result;
    }

    public Task SetChannelNameAsync(string channelId, string name, CancellationToken cancellationToken = default) =>
        rest.PatchAsync($"channels/{channelId}", new { name }, cancellationToken);

    public async Task KickMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        var guildId = await ResolveGuildIdAsync(channelId, cancellationToken);
        await rest.DeleteAsync($"guilds/{guildId}/members/{userId}", cancellationToken);
    }

    public async Task MuteMemberAsync(string channelId, string userId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(28))
            throw new ArgumentOutOfRangeException(nameof(duration), "Discord 超时不能超过 28 天。");
        var guildId = await ResolveGuildIdAsync(channelId, cancellationToken);
        await rest.PatchAsync($"guilds/{guildId}/members/{userId}", new
        {
            communication_disabled_until = DateTimeOffset.UtcNow.Add(duration).ToString("O")
        }, cancellationToken);
    }

    public async Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var guildId = await ResolveGuildIdAsync(channelId, cancellationToken);
        await rest.DeleteAsync($"users/@me/guilds/{guildId}", cancellationToken);
    }

    private async Task<MemberRole> GetRoleAsync(string guildId, string userId, DcGuildMember member, CancellationToken token)
    {
        var guild = await rest.GetAsync<DcGuild>($"guilds/{guildId}", token);
        if (guild.OwnerId == userId) return MemberRole.Owner;
        var roles = await rest.GetAsync<System.Text.Json.JsonElement>($"guilds/{guildId}/roles", token);
        foreach (var role in roles.EnumerateArray())
        {
            var id = role.GetProperty("id").GetString();
            if ((id == guildId || member.Roles?.Contains(id!) == true)
                && ulong.TryParse(role.GetProperty("permissions").GetString(), out var permissions) && (permissions & 8) != 0)
                return MemberRole.Admin;
        }
        return MemberRole.Member;
    }

    private async Task<string> ResolveGuildIdAsync(string channelId, CancellationToken cancellationToken)
    {
        var channel = await rest.GetAsync<DcChannel>($"channels/{channelId}", cancellationToken);
        return channel.GuildId
               ?? throw new InvalidOperationException($"Channel {channelId} 不属于任何 guild。");
    }
}
