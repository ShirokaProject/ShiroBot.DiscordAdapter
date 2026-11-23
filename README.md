# ShiroBot.DiscordAdapter

基于官方 `ShiroBot.Templates 1.0.4` 和 `ShiroBot.SDK 1.0.4` 的 Discord Gateway v10 / REST 适配器，
目标框架 .NET 11，适配器版本 `0.4.0`。实现宿主共享 Model `ShiroBot.Model.Discord` 1.1 的全部扩展接口。

> SDK 1.0.4 尚未发布。发布前在本地打包宿主仓库的 SDK 再构建：
> `dotnet pack ../ShiroBot/SDK/Packaging/ShiroBot.SDK.Package.csproj -c Release -o /tmp/shirobot-feed`，
> 然后 `dotnet build -c Release -p:RestoreAdditionalProjectSources=/tmp/shirobot-feed`。
> Debug 构建会下载 v1.0.4 宿主，发布前需按宿主文档放入本地构建的宿主。

## 开发与运行

```sh
dotnet build ShiroBot.DiscordAdapter.slnx -c Debug
sh scripts/dev.sh
```

在 Rider 打开 `ShiroBot.DiscordAdapter.slnx`，构建配置选 Debug，在右上角选择 `ShiroBot`
（Windows 选 `ShiroBot (Windows)`），Run 与 Debug 共用这一配置，保留“启动前构建”。
Debug 构建会自动下载与 SDK 版本一致的宿主，并把适配器和独立的 TestPlugin 安装到 `.shirobot-dev/`。
Windows 命令行使用 `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1`。

打开 `http://127.0.0.1:7002/dashboard/`，登录密钥见开发宿主 `config.toml` 的 `[api].token`。
添加 Discord 适配器实例，填写 **Bot Token** 并连接；配置由宿主管理，修改后重启该实例。
在 Discord Developer Portal 开启 **Server Members Intent** 和 **Message Content Intent**，
并为机器人授予查看频道、发送消息、读取消息历史等所需权限。

`Intents` 留空时订阅除在线状态和输入状态外的全部事件；需要 `typing_start` 或在线状态时加上对应位
（Presence 为特权 Intent，需同样在 Portal 开启）。Token 无效或使用了未授权的特权 Intent 时，Gateway
以 4004 / 4014 拒绝连接，适配器记录原因并停止重连。

从 Discord 发送 `ping`（应回复 `pong`）、`adapter-info` 或 `adapter-echo hello`，
检查接收、模型映射和回复。TestPlugin 仅安装到开发宿主，不随适配器发布。

## Discord Model（插件推荐）

插件只依赖宿主提供的 `ShiroBot.Model.Discord`，不需要引用本适配器的 DLL：

| 接口 | 内容 |
| --- | --- |
| `IDiscordGuildApi` | 服务器与频道查询；频道增删改与权限覆盖；角色；成员查询、搜索、角色、昵称、超时、移出；封禁；邀请；表情；子区 |
| `IDiscordMessageApi` | Embed、按钮、下拉菜单、投票、附件、贴纸、允许提及；编辑（保留已有附件）、公告推送、置顶、批量删除（审计日志原因）、Reaction 用户与清除、投票结束与投票者 |
| `IDiscordInteractionApi` | 应用 ID；回复（含临时可见）、延迟、更新组件消息、弹窗、自动补全、编辑/删除原回复、后续消息；命令查询、整体注册、单个新增或更新、删除 |
| `IDiscordClientApi` | 账号类型（机器人）、上传上限、自身资料、用户查询、在线状态与活动（重新登录后保持）、修改用户名和头像 |
| `IDiscordRawApi` | 任意 REST 路由（文件上传、审计日志原因）、Gateway 指令、原始 Dispatch，只使用 System.Text.Json 类型 |

`MessageEvent.Raw` 为 `DiscordMessage`，`InteractionEvent.Raw` 为 `DiscordInteraction`（命令选项、下拉值、
弹窗输入，不含交互令牌）。Embed、贴纸、投票和组件以 `RawSegment` 上报，`Payload` 为对应 Model 记录；
发送时 `RawSegment("discord", "embed", DiscordEmbed)` 同样可用。消息编辑、服务器、频道与子区、成员、角色、
封禁、语音状态、输入、Reaction 清除、投票和邀请以 `PlatformEvent` 上报，`Raw` 为 `DiscordEventPayload`；
其他 Gateway 事件为 `JsonElement`。完整说明见宿主文档的 Discord Model 页面。

```csharp
using ShiroBot.Model.Discord;

var interactions = Context.GetAdapterExtension<IDiscordInteractionApi>()!;
await interactions.RegisterCommandsAsync([new DiscordApplicationCommand("ping") { Description = "Ping" }], guildId: "123");
```

交互默认自动确认（延迟回复），此时只能编辑原回复或发送后续消息；SDK 的 `ReplyToInteraction` 也走这一路径。
需要临时可见回复、弹窗、自动补全或直接更新组件消息时，在适配器配置中关闭 **自动确认交互**，并在 3 秒内首次回复。
自动补全交互不会被自动确认。

## Gateway

断线后使用会话 ID 和序号 Resume，事件不丢失，只有会话失效需要重新登录时才发布 `BotOfflineEvent`。
心跳没有收到 ACK 时判定连接僵死并重连；Invalid Session 按 Discord 要求等待后处理。

## 验证与发布

```sh
dotnet run --project Tests/Verification/Verification.csproj -c Release
dotnet build ShiroBot.DiscordAdapter.csproj -c Release
```

自动验证使用模拟 HTTP，覆盖 SDK 接口调用和取消、消息能力、事件处理顺序、多实例身份隔离、配置生命周期、
Gateway Resume 与关闭码，以及 Model 扩展的请求负载、交互回复流程、命令注册、事件负载和原始调用。
真实 Discord 收发需要有效 Bot Token，通过上面的 TestPlugin 命令验证。

Release 将私有协议 DTO 程序集合并进 `bin/Release/net11.0/ShiroBot.DiscordAdapter.dll`；
SDK 和宿主提供的 Model 程序集不随包分发。GitHub Actions 包含构建、验证、版本校验及打包；
推送与版本一致的 `v0.4.0` 标签会发布产物。

## SDK 接口覆盖

完整方法、Model 和平台限制见 [COVERAGE.md](COVERAGE.md)。文件可来自 HTTP(S)、本地绝对路径、file URI、
base64:// 或 base64 data URI，使用 multipart 上传。原生按钮和回调保留 ButtonId/Data，重复交互确认幂等。

## 许可证

[GNU General Public License v3.0](LICENSE)。
