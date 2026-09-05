# Asuka for Windows

> [!WARNING]
> Asuka 目前处于开发阶段。当前实现仅覆盖部分功能，不应被视为对 OneBot、Milky 等相关协议能力与行为的完整描述或规范性参考。

Asuka 是一个本地 QQ 平台模拟器与 Bot 框架调试器。Bot 框架把 Asuka 当作真实协议端点连接；你可以在桌面界面中创建身份和群组、以任意身份发消息、处理好友/入群请求，并观察框架收到的事件与返回结果。所有模拟状态都保存在本机，不需要真实 QQ 账号。

本项目的灵感与最初设计来源于 [A-kirami 的原项目](https://github.com/A-kirami/matcha)。本 Windows 版本面向 Windows 11 24H2（build 26100）及以上版本，使用 .NET 10、Windows App SDK 2.4 和 WinUI 3，并以 full-trust MSIX 作为正式分发模型。

## 原生实现边界

- 界面只使用 WinUI 3/XAML、原生 `TitleBar`、`AppWindow`、Mica、`Storyboard`、Windows 选择器、剪贴板和拖放能力。
- MSIX 包清单提供 Windows 系统开屏；随后由 WinUI 扩展开屏完成淡入、缩放与过渡动画。开屏图案和 Shell/窗口图标都源自 `Assets/Akame.png`。
- HTTP 服务使用进程内 ASP.NET Core Kestrel；WebSocket 使用 `System.Net.WebSockets`；持久化使用微软的 `Microsoft.Data.Sqlite`。
- 消息、富文本、媒体、设置、日志与协议调试均不使用 WebView、WebView2、Blazor、HTML、JavaScript、Electron 或其他浏览器内核实现。
- Windows App SDK 的 NuGet 依赖图可能包含未使用的 WebView2 传递组件；Asuka 源码不会实例化或调用它，质量脚本会检查应用源码中的禁用引用。

## 支持的协议

| 协议 | Asuka 角色 | 端点 |
| --- | --- | --- |
| OneBot V11 | WebSocket Server（正向） | Bot 框架连接 Asuka 配置的 `host:port` |
| OneBot V11 | WebSocket Client（反向） | 默认连接 `/onebot/v11/ws` |
| OneBot V12 | WebSocket Server（正向） | Bot 框架连接 Asuka 配置的 `host:port` |
| OneBot V12 | WebSocket Client（反向） | 默认连接 `/onebot/v12/ws`，子协议 `12.asuka` |
| Milky 1.3 | HTTP + WebSocket 服务 | `POST /api/<action>`、`GET /event`，可附加多个 WebHook |

默认监听 `127.0.0.1:5700`，首次启动会自动生成 256-bit Access Token 并保存到 Windows Password Vault。服务端模式即使只监听 loopback 也要求认证：OneBot WebSocket 接受 `Authorization: Bearer <token>`，也兼容 `Sec-WebSocket-Protocol: token.<token>`；Milky API 只接受 Bearer，`/event` 额外允许 `access_token` 查询参数。协议端点只服务原生 Bot 客户端，带浏览器 `Origin` 的 HTTP/WebSocket 请求会被拒绝。

内置监听与反向 WebSocket 当前使用明文 `http/ws`，所以非 loopback 地址默认拒绝启动；非 loopback WebHook 必须使用 HTTPS。代码中的显式不安全覆盖只供隔离测试网络使用，普通界面不会自动开启。

Milky 示例：

```text
API:        http://127.0.0.1:5700/api/get_login_info
Event WS:   ws://127.0.0.1:5700/event
Asset:      http://127.0.0.1:5700/assets/<sha256>
```

## 架构

```text
Asuka.App (WinUI 3)
    │
    ├── AppEnvironment / native windows and dialogs
    │
    ▼
Asuka.Core
    PlatformService ──► DomainEvent
         │                  │
         ▼                  ▼
    AsukaStore       Asuka.Protocols
    AssetStore        OneBot / Milky translators
                            │
                            ▼
                     ProtocolSession
                     Kestrel / WebSocket / WebHook
```

所有状态变更都必须经过 `PlatformService`。无论操作来自 WinUI 按钮还是协议 action，都会经过同一套权限校验、SQLite 写入和 `DomainEvent` 发布流程；协议适配器只做 wire 翻译，不直接修改数据库。

## 开发环境

- Windows 11 24H2（build 26100）或更高版本
- .NET SDK 10.0.400（`global.json` 会允许同 feature band 的更新版本）
- Visual Studio 2026，或可用 .NET SDK 与 Windows SDK 10.0.26100 的命令行环境

## 构建、测试与运行

```powershell
dotnet restore Asuka.slnx
dotnet build Asuka.slnx -p:Platform=x64
dotnet test tests/Asuka.Tests/Asuka.Tests.csproj -p:Platform=x64
```

配置开发签名证书后，在 Visual Studio 中选择 `Asuka (Package)` 启动配置可部署 MSIX，并看到 Windows 系统开屏；`Asuka (Unpackaged)` 用于快速源码调试，只显示 WinUI 扩展开屏。

统一质量检查：

```powershell
./scripts/check.ps1
```

生成 self-contained MSIX 验证包：

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion 0.1.0.1
```

每次调用都会在 `artifacts/msix/runs` 下创建独立 staging 与输出目录，不会把旧包误认为本次产物。脚本会检查四段版本（每段 0–65535）、目标架构、Full Trust 清单入口，以及包内 .NET/CoreCLR 自包含运行时。默认产物不签名，仅用于构建与清单验证。本机已有主题严格匹配 `CN=Asuka Development` 的代码签名证书时，可以生成签名包：

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion <a.b.c.d> -CertificateThumbprint <thumbprint>
```

### Windows 11 无签名预览包

如果需要在没有开发证书的 Windows 11 24H2（build 26100）或更新设备上测试，可生成专用的 `AllowUnsigned` 包：

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion 0.1.0.1 -UnsignedInstallable
# 可先在普通 PowerShell 只读验包：
./scripts/install-unsigned.ps1 -Package '<exact .msix path>' -ExpectedSha256 '<SHA-256 printed by package.ps1>' -VerifyOnly
# 再从已提升的管理员 PowerShell 执行实际安装：
./scripts/install-unsigned.ps1 -Package '<exact .msix path>' -ExpectedSha256 '<SHA-256 printed by package.ps1>'
```

要生成与 GitHub Release 相同结构的压缩包，可运行：

```powershell
./scripts/package-release.ps1 -Platform x64 -Configuration Release -Version 0.1.0.1 -OutputDirectory ./dist
```

压缩包包含 MSIX、内部 `SHA256SUMS`、`Install-Asuka.ps1` 安装入口、底层校验脚本、说明和许可证。解压后先执行 `./Install-Asuka.ps1 -VerifyOnly`；实际安装必须从已提升的管理员 PowerShell 执行 `./Install-Asuka.ps1`。

GitHub Action 由 `v<a.b.c.d>` tag（例如 `v0.1.0.1`）自动触发，也可以对一个已存在的 tag 手动运行。首次发布前需在仓库 Settings 的 Releases 中启用 **release immutability**，为 `v*` 建立禁止更新/删除的 tag ruleset，并创建带 required reviewers 的 `release` Environment；确认配置后，在该 Environment 中设置 `RELEASE_IMMUTABILITY_ENABLED=true` 和 `RELEASE_TAGS_PROTECTED=true`。工作流只接受默认分支可达的 tag，生成 x64 与 arm64 两个 ZIP 及外层 `SHA256SUMS`；它先上传到可恢复 draft，下载回验，确认 tag 未移动后才发布，最后再次核对 tag、正文、发布者、精确资产、GitHub SHA-256 digest 与 `immutable=true`。

这条路径不导入证书、不修改任何证书库，但只允许已提升的管理员在 Windows 11 24H2 或更新版本上安装。它使用 Microsoft 固定的 Windows 无签名标记 `OID.2.25.311729368913984317654407730594956997722=1` 作为 Publisher 属性，故 package family/`LocalState` 与正式签名包**刻意不同**。这是供可信测试者使用的未签名预览产物，没有发布者签名或 SmartScreen 声誉；Microsoft 不建议用 `AllowUnsigned` 做广泛分发，不能把它当成生产发布渠道，也不能用它绕过任意第三方包的签名失败。

如果已签名的开发包出现 `0x800B010A`（发布者证书无法验证），正确做法是先核验证书与包发布者匹配，再将该证书的**公钥**导入 `LocalMachine\TrustedPeople`，或改用受信任的正式签名/Store 签名。不要把开发者或第三方证书放入 `Trusted Root Certification Authorities`，也不要对任意签名包使用 `-AllowUnsigned`。

ARM64 将 `-Platform` 改为 `ARM64`。正式渠道必须先固定 MSIX Identity/Publisher，并为每次升级递增版本；Microsoft Store 分配的 Publisher 应在首个正式包之前写入清单，否则会形成不同的 package family，旧 `LocalState` 也不会自动沿用。开发证书、企业证书、Azure Artifact Signing 或 Store 签名应按实际渠道管理；仓库不会提交私钥或 PFX。应用使用 `Windows.FullTrustApplication`，因此 Kestrel、本地 Bot 进程和用户经原生 Picker 选择的文件仍可正常工作。

## 本地数据

- MSIX：设置和 SQLite 位于 Windows 管理的包 `LocalState`，内容寻址附件位于包 `LocalCache`。
- Unpackaged 调试回退：`%LOCALAPPDATA%\Asuka\Data\asuka.sqlite3`、`%LOCALAPPDATA%\Asuka\Cache\assets\<sha256>` 和同目录 `settings.json`。
- 首次 MSIX 启动会在目标不存在时迁移旧 unpackaged 数据；SQLite 通过在线 Backup 与 `integrity_check` 生成一致快照，不直接复制 WAL/SHM，也不会覆盖或删除旧文件。
- Access Token：Windows Password Vault；不写入普通设置 JSON

远程附件按流下载，限制为 64 MiB，并禁用 HTTP 自动重定向、代理与 Cookie。连接会固定到 DNS 解析后验证过的公网 IP，拒绝 loopback、私网、链路本地及保留地址；协议媒体也不能读取本地绝对路径、`file:` 或 UNC。应用诊断日志与 Raw Events 分开处理；协议 payload 不应进入普通应用日志。

## 许可证

本项目按 GNU Affero General Public License v3.0 或更高版本发布（SPDX：`AGPL-3.0-or-later`）。完整条款见 [`LICENSE`](LICENSE)。Asuka 是面向 Windows 的独立原生重写。
