# Security Policy

## Network boundary

Matcha 模拟的是整个平台控制面。默认只监听 `127.0.0.1`，首次启动会生成 256-bit 随机 Access Token；包括 loopback 在内的服务端模式默认都要求认证。所有带 `Origin` 的 HTTP/WebSocket 请求都会被拒绝，因为协议端点只面向原生 Bot 客户端，不是浏览器 API。

当前内置监听和反向 WebSocket 使用明文 `http/ws`，因此非 loopback 地址默认 fail-closed；非 loopback HTTP WebHook 同样被拒绝，必须改用 HTTPS。`AllowInsecureRemoteAccess` 只为隔离测试网络提供显式危险覆盖，不会由普通界面自动启用。需要跨主机或容器连接时，应使用可信 TLS 终结、VPN/隧道，并确认 Windows Defender Firewall 仅开放必要网络。

OneBot 和 Milky 的鉴权使用精确 Token 比较。Milky API 的查询参数不会被当作凭据；只有 `/event` WebSocket 为兼容受限客户端接受 `access_token` 查询参数。

## Local files and media

正式构建是 MSIX `Windows.FullTrustApplication`。用户通过原生 Picker 选择的本地文件会复制到 Windows 管理的包 `LocalCache\assets`，以 SHA-256 内容寻址；unpackaged 调试配置仍回退到 `%LOCALAPPDATA%\Matcha\Cache\assets`。协议传入的绝对路径、`file:` URI、UNC 与设备路径不会被读取；本地文件权限只授予用户主动选择的可信应用流程。

远程媒体下载：

- 最大 64 MiB；
- 流式读取并在写入时计算哈希；
- 禁止自动重定向；
- 禁用系统代理、Cookie 与连接复用；
- 不携带协议 Access Token；
- 只接受明确的 `http`/`https` 引用；
- DNS 解析后只连接固定的公网 IP，拒绝 loopback、私网、CGNAT、链路本地、组播和保留地址。

`/assets/<id>` 需要与协议服务相同的 Bearer Token，并同样拒绝浏览器 `Origin`。

## Secrets and diagnostics

Access Token 存入 Windows Password Vault，普通设置不保存明文 Token。Raw Events 检查器可以显示完整协议 payload，可能包含消息内容；普通应用日志必须避免记录 Token、消息正文或原始 payload。导出或复制调试数据前请人工检查。

## Reporting

安全问题请通过项目维护者提供的私有安全报告渠道提交，不要在公开 issue 中附上有效凭据、私人消息或可直接利用的完整攻击步骤。
