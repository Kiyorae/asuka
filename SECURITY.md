# Security Policy

Asuka is a full-trust desktop application and local platform simulator. It does not log in to QQ. Its protocol API can change simulated users, groups, messages, and account state, so access tokens grant control over that simulator.

## Network boundary

Listeners default to `127.0.0.1`. First launch generates a random 256-bit access token, and server modes require authentication by default even on loopback. Requests containing a browser `Origin` header are rejected.

Built-in HTTP listeners and reverse WebSocket connections use clear-text `http/ws`. Non-loopback addresses fail closed by default. Non-loopback WebHook receivers require HTTPS. `AllowInsecureRemoteAccess` is an explicit override for isolated test networks; the normal interface never enables it automatically. Use trusted TLS termination or a tunnel and appropriate firewall rules for remote connections.

Authentication rules are transport-specific:

| Route | Accepted authentication |
| --- | --- |
| OneBot HTTP actions | Bearer header, or `access_token` query only when the header is absent |
| Milky `/api/<action>` | Bearer header |
| Milky `/event` | Bearer header or `access_token` query |
| `/assets/<id>`, `/files/<id>` | Bearer header or resource-scoped `download_token` |

An invalid existing Authorization header does not fall back to a query token. Resource routes never accept a global `access_token` query credential. Download grants expire after five minutes and bind the resource; shared-file grants also bind the bot account. Shared-file downloads recheck expiry, deletion, and group membership.

## Local files and media

The MSIX application uses `Windows.FullTrustApplication`; packaging is not a sandbox. Native file selections are copied into a SHA-256-addressed cache. Protocol-supplied absolute paths, `file:` URIs, UNC paths, and device paths do not grant local file access. An explicitly authorized shared-directory mode is not yet implemented.

Remote media ingestion:

- Streams at most 64 MiB per attachment by default.
- Disables automatic redirects, system proxies, cookies, and connection reuse.
- Does not forward the protocol access token.
- Accepts only HTTP(S) references and connects to a pinned, validated public IP.
- Rejects loopback, private, CGNAT, link-local, multicast, and reserved addresses.

The bundled SILK decoder has input, output-duration, concurrency, and execution-time limits. Its helper process is isolated from the UI process but is not an operating-system security sandbox. See [decoder details](native/Asuka.SilkDecoder/README.md).

V11 `clean_cache` accepts no caller-supplied path. It removes recognized derived SILK WAVs and owned attachment previews while preserving original assets, upload fragments, temporary conversion files, unknown entries, reparse points, hard links, and files held open by playback. Windows cleanup validates fixed directory handles and each file's normalized volume-GUID parent using exact case matching, including against in-place reparse changes. Conversion, preview creation, and cleanup share coordination; native audio opens its stream before releasing that coordination. Other operating systems fail explicitly rather than using a path-based deletion fallback.

## Secrets and diagnostics

Protocol access tokens and the separate V11 WebHook HMAC secret use Windows Password Vault. Settings serialization and deserialization ignore those secret fields. WebHook errors use bounded, application-owned diagnostics instead of remote response bodies or exceptions containing receiver URLs and headers.

**Account credentials** configures explicit simulator cookie/CSRF responses. It neither retrieves real credentials nor fabricates successful defaults. Cookie lookup uses an exact normalized domain, without parent-domain or subdomain fallback. V11's empty/default domain is a separate scope.

Simulator credentials remain in memory by default. **Remember on this device** saves a bounded snapshot in Windows Credential Locker, keyed by the bot account and application data root. They are not saved in SQLite, ordinary settings, or demo data. Windows may synchronize Credential Locker entries through the user's Microsoft account. Saving with Remember off removes the earlier saved snapshot; Clear all takes effect when saved. Account/protocol changes cancel the editor, and failed persistence does not silently appear successful.

Credential API response payloads, sensitive fields, and download grants are redacted in the traffic inspector. Authenticated protocol callers still receive the configured values they requested. Raw Events contains other protocol payloads, including message content; it is not a general anonymizer. Ordinary application logs must not record tokens, message bodies, or raw payloads. Review diagnostics before sharing them.

## Anonymous simulation

V11 anonymous messages retain the true sender internally for authorization but expose a separate group-specific alias in events, supported query responses, and client display. Anonymous moderation targets that alias rather than the ordinary group member. The client uses strict anonymous sending; only an explicit protocol `ignore=1` request permits fallback to an ordinary message.

Switching to V12 or Milky does not reinterpret existing anonymous messages as normal identified messages. Unsupported anonymous events and sensitive reads/replies are suppressed or rejected. User-authored message content can still disclose an identity; alias handling does not anonymize arbitrary text.

## Unsigned development packages

CI artifacts are for trusted development testing. Their dedicated AllowUnsigned publisher identity is separate from a future signed production identity. Download from the intended [repository workflow run](https://github.com/Kiyorae/asuka/actions), check its commit, and compare artifact files with the included checksums before running scripts. A checksum establishes byte integrity, not publisher authenticity.

[install-unsigned.ps1](scripts/install-unsigned.ps1) requires an explicit expected SHA-256. `-VerifyOnly` checks the package without deployment. Installation requires an already elevated Administrator PowerShell session; the script does not request UAC elevation, change execution policy, or install a trusted certificate.

The installer checks architecture, Windows minimum version, package identity, Full Trust entry point, and absence of a signature. It locks the source, copies it into protected staging, verifies the staged hash, and calls `Add-AppxPackage -AllowUnsigned`. It refuses downgrades instead of removing an installed newer package or its data. These checks apply only to Asuka's dedicated unsigned development identity.

## Reporting

Use a private security-reporting channel offered by the project maintainers. Do not post valid credentials, private messages, or a complete directly exploitable report in a public issue. Include the affected commit/version and the smallest reproducible scenario with synthetic data.
