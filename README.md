# ShiroBot.DiscordAdapter

基于官方 `ShiroBot.Templates 1.0.1` 的 Discord Gateway v10 / REST 适配器，使用
`ShiroBot.SDK 1.0.1`、.NET 10。适配器版本为 `0.3.0`。

## 开发与运行

```sh
dotnet build ShiroBot.DiscordAdapter.slnx -c Debug
sh scripts/dev.sh
```

在 Rider 中打开 `ShiroBot.DiscordAdapter.slnx`，选择 Debug 配置和 `ShiroBot` 启动项。
Debug 构建自动下载匹配的宿主，将适配器和独立 TestPlugin 安装到 `.shirobot-dev/`。
Windows 使用 `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1`。

打开 `http://127.0.0.1:7002/dashboard/`，登录密钥见本地开发宿主配置中的 `[api].token`。
添加 Discord 适配器实例，填写 **Bot Token** 并连接；配置由宿主管理，修改后重启该实例。
在 Discord Developer Portal 开启 **Server Members Intent** 和 **Message Content Intent**，
并为机器人授予查看频道、发送消息、读取消息历史等所需权限。
`Intents` 留空时使用默认位标志，也可以按实际权限填写。

从 Discord 发送 `ping`（应回复 `pong`）、`adapter-info` 或 `adapter-echo hello`，
检查接收、模型映射和回复。TestPlugin 仅安装到开发宿主，不随适配器发布。

## 验证与发布

```sh
dotnet run --project Tests/Verification/Verification.csproj -c Release
dotnet build ShiroBot.DiscordAdapter.csproj -c Release
```

自动验证覆盖 SDK 接口调用和取消、消息能力、事件处理顺序、多个实例身份隔离及配置生命周期。
真实 Discord 收发需要有效 Bot Token，通过上述 TestPlugin 命令验证。

Release 将私有协议 DTO 程序集合并进 `bin/Release/net10.0/ShiroBot.DiscordAdapter.dll`；
SDK 和宿主提供的 Model 程序集不随包分发。可将该 DLL 安装到宿主适配器目录。
GitHub Actions 包含构建、验证、版本校验及打包；推送匹配版本的 `v0.3.0` 标签会发布产物。

## API 与 Model 覆盖

完整方法、Model 和平台限制见 [COVERAGE.md](COVERAGE.md)。可用能力已接入 SDK 的消息、
文件、交互确认和 Reaction 接口；平台没有等价接口的功能明确返回不支持。

文件可来自 HTTP(S)、本地绝对路径、file URI、base64:// 或 base64 data URI，真实使用 multipart 上传。
上传会同时发布文件，并返回当前实例和会话限定的凭据；不会伪造消息 ID。
原生按钮和回调保留 ButtonId/Data，重复交互确认幂等。

共享 `ShiroBot.Model.Discord.DiscordUser` 可通过 `IDiscordModelService` 查询：

```csharp
var api = Context.GetAdapterExtension<ShiroBot.DiscordAdapter.Services.IDiscordModelService>();
var me = await api!.GetDiscordUserAsync();
```
