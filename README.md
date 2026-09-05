# Asuka for Windows

> [!WARNING]
> Asuka is under active development. The current implementation covers only a subset of the intended functionality and must not be treated as a complete or normative description of the capabilities or behavior of OneBot, Milky, or any related protocol.

Asuka is a local simulator for the QQ platform and a debugger for bot frameworks. A bot framework connects to Asuka as if it were a real protocol endpoint. From the desktop interface, you can create users and groups, send messages as any identity, handle friend and group-join requests, and inspect the events and responses received by the framework. All simulated state remains on the local machine; no real QQ account is required.

This project draws its inspiration and original design from [A-kirami's original project](https://github.com/A-kirami/matcha). This Windows implementation targets Windows 11 24H2 (build 26100) or later and uses .NET 10, Windows App SDK 2.4, WinUI 3, and full-trust MSIX packaging.

## Native implementation

- The interface uses WinUI 3/XAML, native window APIs, Mica, Windows pickers, the clipboard, and drag-and-drop support.
- The MSIX system splash screen is followed by an extended WinUI splash experience. Its artwork and the application icons are derived from `Assets/Akame.png`.
- The HTTP service uses in-process ASP.NET Core Kestrel, WebSocket support uses `System.Net.WebSockets`, and persistence uses `Microsoft.Data.Sqlite`.
- Messages, rich content, media, settings, logs, and protocol diagnostics do not use WebView, WebView2, Blazor, HTML, JavaScript, Electron, or another browser engine.
- The quality script rejects forbidden browser-engine references in application source.

## Supported protocols

| Protocol | Asuka role | Endpoint |
| --- | --- | --- |
| OneBot V11 | Forward WebSocket server | The bot framework connects to Asuka's configured `host:port` |
| OneBot V11 | Reverse WebSocket client | Connects to `/onebot/v11/ws` by default |
| OneBot V12 | Forward WebSocket server | The bot framework connects to Asuka's configured `host:port` |
| OneBot V12 | Reverse WebSocket client | Connects to `/onebot/v12/ws` by default with subprotocol `12.asuka` |
| Milky 1.3 | HTTP and WebSocket service | `POST /api/<action>`, `GET /event`, and optional WebHooks |

The default listener is `127.0.0.1:5700`. On first launch, Asuka generates a 256-bit access token and stores it in Windows Password Vault. Server modes require authentication even on loopback. Protocol endpoints serve native bot clients only and reject requests containing a browser `Origin` header.

Built-in listeners and reverse WebSocket connections currently use clear-text `http/ws`, so non-loopback addresses fail closed by default. Non-loopback WebHooks must use HTTPS. The explicit unsafe override exists only for isolated test networks and is never enabled automatically by the normal interface.

Milky endpoints:

```text
API:        http://127.0.0.1:5700/api/get_login_info
Event WS:   ws://127.0.0.1:5700/event
Asset:      http://127.0.0.1:5700/assets/<sha256>
```

## Architecture

```text
Asuka.App (WinUI 3)
    |
    +-- AppEnvironment / native windows and dialogs
    |
    v
Asuka.Core
    PlatformService ---> DomainEvent
         |                  |
         v                  v
    AsukaStore       Asuka.Protocols
    AssetStore        OneBot / Milky translators
                            |
                            v
                     ProtocolSession
                     Kestrel / WebSocket / WebHook
```

Every state change passes through `PlatformService`. Operations originating from WinUI controls and protocol actions share the same permission checks, SQLite writes, and `DomainEvent` publication flow. Protocol adapters translate wire formats but never modify the database directly.

## Requirements

- Windows 11 24H2 (build 26100) or later
- .NET SDK 10.0.400 (`global.json` permits later SDKs in the same feature band)
- Visual Studio 2026, or a command-line environment with the .NET SDK and Windows SDK 10.0.26100

## Build and test

```powershell
dotnet restore Asuka.slnx
dotnet build Asuka.slnx -p:Platform=x64
dotnet test tests/Asuka.Tests/Asuka.Tests.csproj -p:Platform=x64
```

Run all repository checks, including the ARM64 build, with:

```powershell
./scripts/check.ps1
```

In Visual Studio, use the `Asuka (Package)` profile for MSIX deployment or `Asuka (Unpackaged)` for faster source debugging.

## Packaging

Create a self-contained MSIX verification package:

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion 0.1.0.1
```

Create and verify an unsigned Windows 11 preview package:

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion 0.1.0.1 -UnsignedInstallable
./scripts/install-unsigned.ps1 -Package '<exact .msix path>' -ExpectedSha256 '<SHA-256 printed by package.ps1>' -VerifyOnly
```

Create a GitHub Release-style archive:

```powershell
./scripts/package-release.ps1 -Platform x64 -Configuration Release -Version 0.1.0.1 -OutputDirectory ./dist
```

Unsigned packages are development artifacts for trusted testing only. See [`scripts/release/README-Windows.txt`](scripts/release/README-Windows.txt) for installation steps and [`SECURITY.md`](SECURITY.md) for the security model.

Use `ARM64` instead of `x64` for the `-Platform` argument when targeting ARM64.

## Local data

- MSIX deployments store settings and SQLite data in package-managed `LocalState`, with content-addressed attachments in `LocalCache`.
- Unpackaged debugging uses `%LOCALAPPDATA%\Asuka\Data\asuka.sqlite3`, `%LOCALAPPDATA%\Asuka\Cache\assets\<sha256>`, and `settings.json`.
- The access token is stored in Windows Password Vault and is never written to the plain-text settings file.

Remote attachments are streamed with a 64 MiB limit. Automatic redirects, system proxies, and cookies are disabled. Connections are pinned to a validated public IP; loopback, private, link-local, and reserved addresses are rejected. Protocol media cannot read local absolute paths, `file:` URIs, or UNC paths.

## License

This project is released under the GNU Affero General Public License version 3 or later (SPDX: `AGPL-3.0-or-later`). See [`LICENSE`](LICENSE) for the complete terms. Asuka is an independent native implementation for Windows.
