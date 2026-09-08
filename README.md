# Asuka for Windows

Asuka is a local QQ platform simulator and debugger for bot frameworks. Connect a bot framework to its protocol endpoint, create simulated users and groups, send messages as different identities, and inspect the resulting events and API responses. No real QQ account is required.

The desktop client is built with WinUI 3, .NET 10, and Windows App SDK 2.4 for **Windows 11 24H2 (build 26100) or later**. It is inspired by [A-kirami's Matcha](https://github.com/A-kirami/matcha).

> [!NOTE]
> Asuka is under active development. Protocol support is incomplete; use the [capability matrix and known limits](docs/PROTOCOL_COVERAGE.md) when choosing a test scenario. The official protocol specifications remain authoritative.

## Download a development build

Open [GitHub Actions](https://github.com/Kiyorae/asuka/actions), select a successful **CI** run for the commit you intend to test, and download the matching artifact:

| Device | Artifact |
| --- | --- |
| Intel/AMD x64 | `Asuka-windows-x64-unsigned-dev-<run_number>` |
| Windows on ARM64 | `Asuka-windows-arm64-unsigned-dev-<run_number>` |

Artifacts are retained for 14 days. These are **unsigned development packages**, not signed production releases. They use a dedicated Windows AllowUnsigned development identity.

1. Extract the entire artifact ZIP into one folder. It contains the MSIX, `install-unsigned.ps1`, `README-Windows.txt`, `LICENSE`, and `SHA256SUMS.txt`.
2. Read `README-Windows.txt` and follow its checksum check for all four payload files. Compare the MSIX hash with the selected CI run's summary, which also records the source commit. Checksums detect changed bytes; trust still comes from selecting the intended repository, workflow run, and commit.
3. From the extracted folder, verify the package using its expected MSIX hash. The x64 example is:

```powershell
Get-Content .\SHA256SUMS.txt
Get-FileHash .\Asuka-windows-x64-unsigned-dev.msix -Algorithm SHA256

$expectedHash = '<MSIX SHA-256 from the selected CI run summary>'
.\install-unsigned.ps1 -Package .\Asuka-windows-x64-unsigned-dev.msix -ExpectedSha256 $expectedHash -VerifyOnly
```

4. Open **Administrator PowerShell**, return to that folder, set the same `$expectedHash`, and install:

```powershell
.\install-unsigned.ps1 -Package .\Asuka-windows-x64-unsigned-dev.msix -ExpectedSha256 $expectedHash
```

For ARM64, use `Asuka-windows-arm64-unsigned-dev.msix`. Follow the bundled instructions if downloaded scripts are blocked. The installer checks the hash, architecture, minimum Windows version, and dedicated unsigned manifest identity. It does not request elevation, trust a certificate, change execution policy, or remove an installed newer version. See [SECURITY.md](SECURITY.md).

## Connect a bot framework

Choose the protocol and transport in Settings, copy the generated access token, and start the service. The default listener is `127.0.0.1:5700`.

| Protocol | Available transports |
| --- | --- |
| OneBot V11 | Forward/reverse WebSocket; HTTP actions at `GET /<action>` or `POST /<action>`; optional HTTP POST event receivers |
| OneBot V12 | Forward/reverse WebSocket; `POST /` JSON actions and `get_latest_events` polling; optional WebHook receivers |
| Milky 1.3 | `POST /api/<action>`; WebSocket or SSE at `GET /event`; optional WebHook receivers |

Reverse WebSocket defaults are `/onebot/v11/ws` and `/onebot/v12/ws`; V12 uses subprotocol `12.asuka`. OneBot HTTP and WebSocket are separate session modes.

Server modes require authentication by default, including on loopback. OneBot action routes accept Bearer authentication or the standard `access_token` query parameter. Milky APIs require Bearer authentication; `/event` also accepts the query token. Protocol endpoints reject browser `Origin` headers. Non-loopback HTTP/WS connections are disabled by default; use trusted TLS termination or a tunnel for remote setups.

The [coverage document](docs/PROTOCOL_COVERAGE.md) describes scheduling, heartbeats, polling, WebHook quick operations, response formats, and implementation limits.

## Client features

Controls follow the selected protocol. Features absent from that protocol are hidden; actions requiring additional permissions are disabled.

- Private and group messages with recall, separate reply cards, and jumps to loaded originals.
- Native text/code, image/GIF, voice, video, and protocol-appropriate file, location, face, and forward rendering.
- Milky group reactions, favorites, pins, read markers, profile editing, shared files/folders, announcements, essence messages, and group notification history.
- V11/Milky request simulation and processing, profile likes, member moderation, and group administration.
- V11 group upload/poke notices, honors/lucky-king simulation, and anonymous messages with alias moderation.
- Runtime bot online/offline simulation, V11 service restart/cache cleanup, and explicit V11/Milky simulator cookie/CSRF responses.
- Protocol activity inspection, native settings, and component license links in About.

QQ SILK voice is converted to WAV by the bundled decoder. A system ffmpeg installation is unnecessary. ARM64 packages currently run the x64 decoder through Windows emulation; see [the decoder sources and license](native/Asuka.SilkDecoder/README.md).

Voice and video include play/pause, time displays, mute, and a seek bar when the source supports seeking. Video Fit/Fill controls change scaling; expanded playback stays in the window and exits with its button or Esc. Outgoing messages use white text on blue, with a darker background separating quoted content.

The loading overlay follows initialization and the Windows animation preference. In About, clicking the logo five times starts a 2500 ms rotation with spring scaling.

## Try the showcase

In **Settings → Demo**, enable **Demo mode** and choose **Save and restart**. Disable it and save to return to ordinary conversations. Switching workspaces clears unsent drafts.

The isolated showcase alternates messages between a fixed group and private chat in a 48-step loop, every three seconds by default. It includes the logo PNG, spinning-logo GIF, SILK/WAV voice, H.264 video, code blocks, replies, recalls, and protocol-specific content. Use Group/Private, Pause, and Follow new messages to control it. Each chat keeps at most 120 messages before resetting.

For scripted launches:

```powershell
./scripts/run-demo.ps1
./scripts/run-demo.ps1 -Protocol OneBot12 -IntervalSeconds 5
```

Equivalent executable arguments are `--demo --demo-protocol=milky --demo-interval=3`. Protocol choices are `milky`, `onebot11`, and `onebot12`; cadence is 1–30 seconds. Explicit `--demo`/`--no-demo` overrides the saved setting, which takes precedence over `ASUKA_DEMO`. Demo mode does not start protocol networking or use ordinary settings and credentials. [Showcase asset details](src/Asuka.App/Assets/Showcase/README.md).

## Build from source

Development requires Windows 11 24H2+, the .NET SDK selected by [global.json](global.json), Windows SDK 10.0.26100, and Visual Studio C++ build tools for the bundled SILK helper. Visual Studio 2026 provides the native debugging workflow. Packages are self-contained; end users do not need the development SDKs.

```powershell
dotnet restore Asuka.slnx -p:Platform=x64 -p:Configuration=Release
dotnet build Asuka.slnx --configuration Release --no-restore -p:Platform=x64
dotnet test tests/Asuka.Tests/Asuka.Tests.csproj --configuration Release --no-restore -p:Platform=x64
```

Run the repository checks, including formatting, the browser-engine source guard, and ARM64 compilation:

```powershell
./scripts/check.ps1
```

In Visual Studio, select `Asuka.App` and `Debug | x64`. Use **Asuka (Package)** for MSIX deployment, **Asuka (Unpackaged)** for source debugging, or **Asuka (Demo)** for the showcase. Press F5 and enable XAML Hot Reload/XAML Diagnostics in the debugger options. Debug builds also accept an absolute `ASUKA_DEV_DATA_ROOT` for isolated data.

To create the dedicated unsigned development package:

```powershell
./scripts/package.ps1 -Platform x64 -Configuration Release -PackageVersion 0.1.0.1 -UnsignedInstallable
./scripts/install-unsigned.ps1 -Package '<exact output .msix path>' -ExpectedSha256 '<printed SHA-256>' -VerifyOnly
```

Use `-Platform ARM64` for ARM64. [package-release.ps1](scripts/package-release.ps1) additionally creates a release ZIP with checksums and installation instructions.

The latest recorded regression baseline is **726 passed, zero failed, and one non-Windows-only case excluded on Windows**. This covers protocol/model/filesystem behavior, not complete conformance or a native UI acceptance test. See [verification scope](docs/PROTOCOL_COVERAGE.md#verification).

## Architecture and local data

```text
WinUI client / AppEnvironment
            |
      PlatformService ---- DomainEvent ---- Protocol adapters
            |                                    |
        AsukaStore                         ProtocolSession
        AssetStore                     Kestrel / WS / WebHook
```

`PlatformService` owns simulated actions, permission checks, and domain events. SQLite stores platform state; content-addressed files store attachments. Protocol adapters translate between this model and official wire formats.

All application rendering is native WinUI/XAML. The app does not host a browser engine. The Windows App SDK's transitive WebView2 payload is attributed in About but is not instantiated.

- Packaged installations use package-managed `LocalState` and `LocalCache`.
- Unpackaged launches use `%LOCALAPPDATA%\Asuka\Data` and `%LOCALAPPDATA%\Asuka\Cache\assets`.
- Demo data lives separately under `%LOCALAPPDATA%\Asuka\Showcase\<Protocol>`.
- Initial package startup can migrate existing unpackaged data by online SQLite backup and integrity checking, without overwriting an existing destination or deleting the source.
- Access tokens and WebHook signing secrets use Windows Password Vault. Simulator credentials stay in memory unless explicitly remembered in Credential Locker; neither is stored in plain-text settings or SQLite.

Read [SECURITY.md](SECURITY.md) for media-download restrictions, local-file boundaries, diagnostics, and unsigned-package installation.

## License

Asuka is licensed under the **GNU Affero General Public License version 3 or later**, SPDX `AGPL-3.0-or-later`. See [LICENSE](LICENSE). Third-party components retain their own licenses; the About page and [bundled SILK documentation](native/Asuka.SilkDecoder/README.md) provide their notices and sources.
