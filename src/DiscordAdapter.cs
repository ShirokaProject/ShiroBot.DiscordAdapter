using ShiroBot.Discord.Model;
using ShiroBot.DiscordAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.discord", MinimumVersion = "0.9.4")]

namespace ShiroBot.DiscordAdapter;

[BotAdapter("discord", Name = "DiscordAdapter", Version = "0.3.0", Author = "ShirokaProject",
    Description = "Discord Gateway/REST adapter for ShiroBot", Protocol = "discord-gateway-v10")]
public sealed class DiscordAdapter : IBotAdapter, IConfigurableAdapter, IConfigurableComponent<DiscordAdapterConfig>
{
    private readonly DiscordMapper _mapper = new();
    private readonly DiscordEventService _events;
    private DiscordRestClient? _rest;
    private DiscordGateway? _gateway;
    private DiscordInteractionService? _interactions;
    private DiscordReactionService? _reactions;
    private CancellationTokenSource? _gatewayTokenSource;
    private Task? _gatewayTask;
    private IMessageService? _message;
    private IChannelService? _channel;
    private IUserService? _user;

    public DiscordAdapter() => _events = new DiscordEventService(_mapper, () => _rest, () => _interactions);

    public string Platform => DiscordMapper.PlatformId;
    public IMessageService Message => _message ?? throw NotStarted();
    public IChannelService Channel => _channel ?? throw NotStarted();
    public IUserService User => _user ?? throw NotStarted();
    public IEventService Event => _events;
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;
    public DiscordAdapterConfig CurrentConfigValue { get; private set; } = new();
    ConfigApplyMode IConfigurableAdapter.ApplyMode => ConfigApplyMode.RestartComponent;

    public Task OnConfigLoadedAsync(DiscordAdapterConfig config, CancellationToken cancellationToken)
    {
        CurrentConfigValue = config;
        return Task.CompletedTask;
    }

    public Task OnConfigChangedAsync(DiscordAdapterConfig previous, DiscordAdapterConfig current, CancellationToken cancellationToken)
    {
        CurrentConfigValue = current;
        return Task.CompletedTask;
    }

    public TService? GetExtension<TService>() where TService : class => this as TService ?? _rest as TService ?? _message as TService ?? _user as TService ?? _interactions as TService ?? _reactions as TService;

    public async Task StartAsync()
    {
        if (_gatewayTokenSource is not null)
            throw new InvalidOperationException("Discord 适配器已启动。");
        var config = CurrentConfigValue;
        if (string.IsNullOrWhiteSpace(config.BotToken))
            throw new InvalidOperationException("请在适配器配置中填写 BotToken。");

        try
        {
            _rest = new DiscordRestClient(config.BotToken);
            _interactions = new DiscordInteractionService(_rest);
            _reactions = new DiscordReactionService(_rest);
            _message = new DiscordMessageService(_rest, _mapper, _interactions);
            _channel = new DiscordChannelService(_rest, _mapper);
            _user = new DiscordUserService(_rest);
            var self = await User.GetSelfAsync();
            _mapper.SelfId = self.Id;
            Logger.Success($"Discord 登录成功 - {self.Name} ({self.Id})");
            var gateway = new DiscordGateway(config.BotToken, config.Intents ?? DcIntents.Default);
            _gateway = gateway;
            _events.Attach(gateway);
            _gatewayTokenSource = new CancellationTokenSource();
            _gatewayTask = RunGatewayLoopAsync(gateway, _gatewayTokenSource.Token);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        _gatewayTokenSource?.Cancel();
        try
        {
            if (_gatewayTask is not null) await _gatewayTask;
        }
        finally
        {
            if (_gateway is not null) _events.Detach(_gateway);
            _gatewayTokenSource?.Dispose();
            _rest?.Dispose();
            _gatewayTokenSource = null;
            _gatewayTask = null;
            _gateway = null;
            _rest = null;
            _message = null;
            _channel = null;
            _user = null;
            _mapper.SelfId = null;
            _mapper.KnownDmChannels.Clear();
            _mapper.Channels.Clear();
            _mapper.Resources.Clear();
            _mapper.Uploads.Clear();
            _interactions = null;
            _reactions = null;
        }
    }

    private async Task RunGatewayLoopAsync(DiscordGateway gateway, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await gateway.RunAsync(token);
                if (!token.IsCancellationRequested)
                {
                    Logger.Warning("Discord Gateway 连接已断开，准备重连。");
                    await _events.PublishAsync(new ShiroBot.SDK.Models.BotOfflineEvent { Platform = Platform, SelfId = _mapper.SelfId, Reason = "Gateway disconnected" });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.Error($"Discord Gateway 异常: {ex.GetType().Name}: {ex.Message}");
                await _events.PublishAsync(new ShiroBot.SDK.Models.BotOfflineEvent { Platform = Platform, SelfId = _mapper.SelfId, Reason = ex.GetType().Name });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private static InvalidOperationException NotStarted() => new("Discord 适配器尚未启动。");
}

[ConfigModel]
public sealed class DiscordAdapterConfig
{
    [ConfigField("Discord Bot Token。", Label = "Bot Token", Type = "password", Group = "connection", GroupLabel = "连接", Order = 10)]
    public string BotToken { get; set; } = string.Empty;

    [ConfigField("Gateway intents 位标志；留空使用默认值。需在 Discord Developer Portal 开启 Server Members 和 Message Content intents。",
        Label = "Gateway Intents", Group = "connection", Min = 0, Order = 20)]
    public int? Intents { get; set; }
}
