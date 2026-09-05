# Security Policy

## Network boundary

Asuka simulates the platform's entire control plane. It listens only on `127.0.0.1` by default and generates a random 256-bit access token on first launch. Server modes require authentication by default even on loopback. Every HTTP or WebSocket request containing an `Origin` header is rejected because protocol endpoints are intended for native bot clients, not browser APIs.

Built-in listeners and reverse WebSocket connections currently use clear-text `http/ws`, so non-loopback addresses fail closed by default. Non-loopback HTTP WebHooks are also rejected and must use HTTPS. `AllowInsecureRemoteAccess` is an explicitly dangerous override for isolated test networks only and is never enabled automatically by the normal interface. Cross-host or container connections should use trusted TLS termination or a VPN/tunnel, with Windows Defender Firewall configured to expose only the required network.

OneBot and Milky authentication uses exact token comparison. Milky API query parameters are never accepted as credentials; only the `/event` WebSocket accepts an `access_token` query parameter for compatibility with constrained clients.

## Local files and media

Production builds use an MSIX `Windows.FullTrustApplication`. Local files selected through native pickers are copied into the Windows-managed package `LocalCache\assets` and addressed by SHA-256. Unpackaged debugging falls back to `%LOCALAPPDATA%\Asuka\Cache\assets`. Absolute paths, `file:` URIs, UNC paths, and device paths supplied through protocols are never read. Local file access is granted only through trusted application flows initiated by an explicit user selection.

Remote media downloads:

- Accept at most 64 MiB.
- Stream content and calculate its hash while writing.
- Reject automatic redirects.
- Disable system proxies, cookies, and connection reuse.
- Never include the protocol access token.
- Accept only explicit `http` or `https` references.
- Connect only to a pinned public IP after DNS resolution, rejecting loopback, private, CGNAT, link-local, multicast, and reserved addresses.

`/assets/<id>` requires the same Bearer token as the protocol service and also rejects browser `Origin` headers.

## Secrets and diagnostics

The access token is stored in Windows Password Vault and is never saved in plain text with ordinary settings. The Raw Events inspector can display complete protocol payloads, which may contain message content. Ordinary application logs must not record tokens, message bodies, or raw payloads. Review diagnostic data manually before exporting or copying it.

## Reporting

Report security issues through a private security-reporting channel provided by the project maintainers. Do not include valid credentials, private messages, or complete directly exploitable attack instructions in a public issue.
