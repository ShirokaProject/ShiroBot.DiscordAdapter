using ShiroBot.Discord.Model;
using ShiroBot.DiscordAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.DiscordAdapter;

[BotAdapter("discord",
    Name = "DiscordAdapter",
    Version = "0.1.0",
    Description = "Discord Gateway/REST adapter for ShiroBot",
    Protocol = "discord-gateway-v10")]
public class DiscordAdapter : IBotAdapter
{
    private DiscordRestClient? _rest;
    private DiscordGateway? _gateway;
    private readonly DiscordEventService _eventService = new();
    private CancellationTokenSource? _gatewayTokenSource;

    private IMessageService? _message;
    private IChannelService? _channel;
    private IUserService? _user;

    public string Platform => DiscordMapper.PlatformId;

    public IMessageService Message => _message ?? throw NotStarted();
    public IChannelService Channel => _channel ?? throw NotStarted();
    public IUserService User => _user ?? throw NotStarted();
    public IEventService Event => _eventService;
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;

    /// <summary>暴露 REST 客户端作为平台扩展(Embed、Reaction、Slash Command 等)。</summary>
    public TService? GetExtension<TService>() where TService : class =>
        this as TService ?? _rest as TService;

    public async Task StartAsync()
    {
        var config = Config.Load<DiscordAdapterConfig>();
        Config.Save(config);

        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            Logger.Error("请在适配器配置中填写 BotToken。");
            throw new InvalidOperationException("Discord BotToken 未配置。");
        }

        _rest = new DiscordRestClient(config.BotToken);
        _message = new DiscordMessageService(_rest);
        _channel = new DiscordChannelService(_rest);
        _user = new DiscordUserService(_rest);

        Logger.Info("开始连接 Discord...");
        var self = await User.GetSelfAsync();
        DiscordMapper.SelfId = self.Id;
        Logger.Success($"Discord 登录成功 - {self.Name} ({self.Id})");

        _gateway = new DiscordGateway(config.BotToken, config.Intents ?? DcIntents.Default);
        _eventService.Attach(_gateway);

        _gatewayTokenSource = new CancellationTokenSource();
        _ = Task.Run(() => RunGatewayLoopAsync(_gatewayTokenSource.Token));
    }

    public Task StopAsync()
    {
        _gatewayTokenSource?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunGatewayLoopAsync(CancellationToken token)
    {
        var retryCount = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                retryCount++;
                if (retryCount > 1)
                {
                    Logger.Info($"正在重连 Discord Gateway,第 {retryCount} 次。");
                }

                await _gateway!.RunAsync(token);
                Logger.Warning("Discord Gateway 连接已断开,准备重连。");
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("Discord Gateway 已取消。");
                break;
            }
            catch (Exception ex)
            {
                Logger.Error($"Discord Gateway 异常: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static InvalidOperationException NotStarted() =>
        new("Discord 适配器尚未启动。");
}

public sealed class DiscordAdapterConfig
{
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Gateway intents 位标志;不填使用默认(Guilds/Members/Messages/Reactions/DM/MessageContent)。</summary>
    public int? Intents { get; set; }
}
