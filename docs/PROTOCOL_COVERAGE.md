# Protocol coverage

Asuka implements official protocol behavior in a local QQ platform simulator and native Windows client. This document describes implemented capabilities, implementation choices, and remaining work. It is not a claim of complete protocol conformance.

Controls are gated by [ProtocolCapabilities](../src/Asuka.Protocols/ProtocolCapabilities.cs). A feature absent from the selected standard is hidden. Defined but unfinished functionality is a remaining implementation task, not grounds for claiming an empty successful implementation.

## Official references

- **OneBot V11:** [public APIs](https://github.com/botuniverse/onebot-11/blob/master/api/public.md), [message segments](https://github.com/botuniverse/onebot-11/blob/master/message/segment.md), [message events and quick operations](https://github.com/botuniverse/onebot-11/blob/master/event/message.md), [notices](https://github.com/botuniverse/onebot-11/blob/master/event/notice.md), [meta events](https://github.com/botuniverse/onebot-11/blob/master/event/meta.md), [scheduling](https://github.com/botuniverse/onebot-11/blob/master/api/README.md), [HTTP](https://github.com/botuniverse/onebot-11/blob/master/communication/http.md), and [HTTP POST](https://github.com/botuniverse/onebot-11/blob/master/communication/http-post.md).
- **OneBot V12:** [standard site](https://12.onebot.dev/), [action requests](https://12.onebot.dev/connect/data-protocol/action-request/), [HTTP](https://12.onebot.dev/connect/communication/http/), [WebHook](https://12.onebot.dev/connect/communication/http-webhook/), [meta actions](https://12.onebot.dev/interface/meta/actions/), and [meta events](https://12.onebot.dev/interface/meta/events/).
- **Milky 1.3:** [system APIs](https://milky.ntqqrev.org/api/system), [message APIs](https://milky.ntqqrev.org/api/message), [group APIs](https://milky.ntqqrev.org/api/group), [file APIs](https://milky.ntqqrev.org/api/file), [group notifications](https://milky.ntqqrev.org/struct/GroupNotification), and [events](https://milky.ntqqrev.org/struct/Event).

The implementation's local limits and simulator defaults below are distinguished from requirements of those specifications. No OneBot implementation-specific reaction extension is enabled.

## Capability matrix

“—” means the feature has no corresponding standard capability in this scope; it does not mean an unimplemented standard feature is complete.

| Capability | OneBot V11 | OneBot V12 | Milky 1.3 | Native client / model |
| --- | --- | --- | --- | --- |
| Text, mentions, images, voice, video | Standard segments | Standard segments | Standard segments | Native rendering and protocol-gated composer |
| Recall | `delete_msg`, recall notices | `delete_message`, `message_delete` | `recall_*`, `message_recall` | Persisted placeholder, permissions, deduplication |
| Reply | `reply` | `reply` | `reply` | Draft, separate quote card, jump to loaded original |
| Group reactions | — | — | `face` / `emoji` | Persisted counts, add/remove, reaction-type isolation |
| Nudge/poke | Group poke notice | — | Friend/group nudge | V11 group-only notice simulation; Milky actions |
| QQ faces and forwards | Standard segments | — | Standard segments | Face selection, forward creation and summaries |
| Location | `location` | `location` | — | Native location entry and preview |
| Standalone audio | `record` for voice | `audio` and `voice` | `record` for voice | Native playback, bundled SILK decoding |
| File messaging and transfer | Group upload notice; no generic file segment | Basic/fragmented file APIs and `file_id` | Shared private/group files | Native upload and applicable management controls |
| Rename/leave group | Standard APIs | Standard APIs | Standard APIs | Shared permission checks |
| Admin/mute/card/title | Standard APIs | — | Standard APIs | Only supported management fields submitted |
| Group dissolution | Owner `set_group_leave(is_dismiss=true)` | No separate dissolve action | `group_disband` event | Native owner operation; appropriate standard events |
| Friend/join requests | Friend/group request events and actions | — | Four request kinds | Simulation, approval/rejection, persisted history |
| Group notification history | — | — | Five notification variants | Paging, filtering, request actions |
| Profile likes | `send_like` | — | `send_profile_like` | Native action, persisted counters and V11 daily limit |
| Group honors/lucky king | Honor query and standard notices | — | — | Five stored honor lists and local notice simulation |
| Anonymous group messages | Standard segment/events/moderation | — | — | Stable aliases, strict composer, alias mute |
| Favorites | No listing API | No listing API | `get_custom_face_url_list` | Per-account import, management, and image sending |
| Pins/read markers/profile edits | — | — | Standard APIs | Native controls and persisted state |
| Announcements/essence/group avatar | No standard management APIs | No standard management APIs | Six group-content APIs | Native panels, essence marks and actions |
| Simulator credentials | Cookies, CSRF, combined credentials | — | Cookies and CSRF | Explicit masked editor; optional Credential Locker |
| Bot online/offline | Status and heartbeat | Status and `status_update` | `bot_offline` | Runtime simulation and offline write guards |
| Service restart/cache cleanup | `set_restart`, `clean_cache` | — | — | Account-page maintenance controls |
| HTTP actions | GET/POST action routes | POST JSON action envelope | POST API routes | Native configuration and traffic inspection |
| Event polling/streaming | WebSocket/HTTP POST | HTTP polling/WebSocket/WebHook | WebSocket/SSE/WebHook | Session lifecycle and cancellation |
| Deferred calls | `_async`, `_rate_limited` | — | — | Bounded queues and asynchronous acknowledgement |

Native simulation controls may configure queryable state or emit an official event without introducing a new protocol action. The protocol registry and codecs live in [Asuka.Protocols](../src/Asuka.Protocols); state operations and transactions live in [Asuka.Core](../src/Asuka.Core).

## Message and account semantics

Recall and reaction operations check actor permissions, persist changes, and avoid duplicate events for ineffective repeats. New replies must target an active message in the same conversation/account. Reply cards use anonymous-safe display names and do not recursively repeat earlier quotes. Reuse keys include every referenced target's ID, availability, sender, preview, and recall state. The message view currently loads 500 messages; an absent quote is labeled “not loaded,” rather than assumed deleted.

Milky favorites are deduplicated by account and asset ID and retain insertion order. Native import validates PNG/JPEG/GIF/WebP/BMP decoding from the cached copy, with an 8192-pixel side limit and 16,777,216-pixel total limit. Only the selected preview is decoded, at up to 256 pixels. The API returns signed URLs for existing cached images; missing assets remain manageable in the client. Removing a favorite preserves shared media.

Bot presence is independent of transport connectivity. Registered bots start online; repeated states produce no duplicate transitions. Offline bot writes fail and recheck presence after acquiring the mutation lock. Cached reads and cache-only V12 uploads remain available. Native simulation as another user still works. Offline messages remain stored, but ordinary events suppressed for that account are not replayed on recovery.

V11 `online` and `good` reflect presence. V12 separates implementation health from `bots[].online` and emits `status_update`. Milky emits `bot_offline` with the supplied reason and no invented online event. Presence itself is runtime-only.

### Groups, requests, and notices

Milky notification history includes `join_request`, `invited_join_request`, `admin_change`, `kick`, and `quit`. Pagination uses descending sequence order, inclusive start sequence, and a next-page sequence. Filtered requests remain distinguishable; a bot's own `group_invitation` is not part of this list. Native pages show 20 records and retain processed requests with ID fallbacks for deleted entities.

Administrator/member changes save notification recipients atomically. Group dissolution requires the owner, removes the group and associated live content, and ignores pending related requests while retaining processed requests and notification history. Events follow commit and reach relevant registered bots, including a removed bot. V11 requires an owner to specify `is_dismiss=true`; ordinary members can leave without dissolving.

V11 honor queries expose only the requested lists among `talkative`, `performer`, `legend`, `strong_newbie`, and `emotion`, plus current talkative/day-count data where applicable. Native administration can set this persisted simulator state. Only the three defined honor notice types are emitted. Lucky-king simulation validates membership and produces a notice without transferring money. V11 group upload and poke are likewise event simulations, not added file-management or private-poke APIs.

### V11 anonymous messages

Anonymous sending is available only in V11 group chats. The group switch, group/sender alias, mute expiry, and historical message alias are persisted. Alias creation and send permission checks share the message transaction. Public identity fields use the alias; the true sender remains internal for authorization and does not gain an ordinary member activity timestamp.

The send-only `anonymous` marker is accepted once at the top level, not in private messages or forward nodes. Asuka defaults `ignore` to 0 and new groups to anonymous-disabled; these are simulator choices. The client always sends strictly. Explicit protocol `ignore=1` permits a normal-message fallback when anonymous mode is disabled or the alias is muted, subject to other send restrictions.

Owner/admin moderation accepts the full anonymous object or its flag, with object precedence. Default duration is 1800 seconds. Invalid expiry values fail; a mute cannot be canceled or shortened by this operation. Quick operations bind the original account/message/group/alias tuple, support reply/recall/alias ban, and ignore automatic mentions and kick for anonymous messages. V12/Milky do not reveal real identities by reinterpreting this stored content after a protocol switch.

### Explicit simulator credentials

Cookie scopes are exact normalized DNS names, with no suffix fallback. V11's omitted/empty domain selects a separately configured default scope; Milky requires a nonempty domain. V11 returns signed int32 CSRF fields, while Milky preserves the literal string. Missing or incompatible values fail rather than fabricating credentials.

Values stay in memory unless explicitly remembered in Windows Credential Locker, scoped to the data root and account. They never enter SQLite or settings JSON. Saving with Remember off removes a stored snapshot. Credential payloads are redacted in diagnostics while authenticated callers receive the configured values. See [SECURITY.md](../SECURITY.md).

## Media and files

The [bundled SILK helper](../native/Asuka.SilkDecoder/README.md) decodes pinned SDK formats to 24 kHz mono PCM WAV without a system ffmpeg dependency. Input is limited to 16 MiB, output to ten minutes, conversions to two concurrent helpers, and execution to 30 seconds. ARM64 currently uses the x64 helper through Windows emulation. V11 `get_record` supports SILK-to-WAV or identified original-format output; other conversions fail explicitly.

V12 supports basic and fragmented file transfer, URL request headers, and SHA-256 verification before publishing content-addressed files. Headers are not saved in asset metadata. Fragmented transfers accept out-of-order writes/replacement and impose a 1 MiB fragment limit, default 64 MiB file limit, ten-minute idle expiry, and concurrency/storage quotas.

Milky provides the 12 private/group file lifecycle APIs: uploads, signed downloads, listings, file rename/move/delete/persistence, and folder operations. Downloads recheck membership, deletion, and expiry. Resource grants last five minutes; shared-file grants also bind the account. No protocol input grants arbitrary local path access.

Native voice/video controls provide position, time, play/pause, mute, and conditional seeking. Video Fit/Fill changes scaling; expanded playback remains inside the window. Native audio owns its stream until the player is released. Decoder support does not imply universal Windows video/audio codec support.

## Transport behavior

### OneBot actions, scheduling, and polling

- V11 HTTP accepts GET query parameters and POST JSON/forms on action paths. Unknown actions use HTTP 404, malformed bodies 400, unsupported POST types 406, and known business failures HTTP 200.
- V12 HTTP accepts `POST /` JSON envelopes. HTTP, WebSocket, and WebHook response actions share validation for nonempty string `action`, object `params`, optional string `echo`, duplicate keys, and nesting. Malformed envelopes return `10001`; a valid mismatched account selector returns `10102`.
- V11 deferred suffixes return `status=async`, `retcode=1`, `data=null` on queue admission. Async and rate-limited queues are separate and bounded. Rate-limited execution is serial with a configurable 500 ms default delay after completion. There is no second action response; later errors produce safe diagnostics.
- Accepted deferred work belongs to the session, so an HTTP request finishing does not cancel it. Stop cancels and awaits outstanding work.
- Heartbeats are disabled by default with a configurable 15,000 ms interval. Delivery uses WebSocket and applicable WebHooks; V12 polling excludes meta events.
- V12 `get_latest_events` consumes oldest non-meta events. Asuka defaults to enabled with capacity 256; capacity 0 is unbounded. Four simultaneous polls and timeout 0–300 seconds are implementation limits. Buffers reset on stop/restart.

### WebHooks and Milky SSE

OneBot WebHooks can accompany any supported OneBot session mode. Each receiver has an independent FIFO queue bounded to 256 events and 16 MiB. Overflow evicts old pending events; no persistent retry queue exists.

V11 sends `X-Self-ID` and optional HMAC-SHA1 over the exact UTF-8 request bytes, using a separate signing secret. Successful response quick operations support replies, recall, moderation, and request handling, with current permission and event-target checks. V12 sends standard version/implementation headers, optional Bearer authentication, and executes JSON response action lists sequentially. Responses are bounded to 4 MiB and 64 actions.

Startup sends V11 enable or V12 initial status events. V11 shutdown attempts disable only for receivers that acknowledged enable, sharing a two-second deadline. Stop cancels deliveries and response actions. Timeout 0 means no delivery deadline, not immunity from cancellation. Non-loopback receivers require HTTPS and redirects are disabled.

Milky supports authenticated HTTP actions, WebSocket events, SSE with keepalive comments, and optional WebHooks. Its APIs require Bearer authentication; the event endpoint additionally accepts the standard query token. All protocols reject browser-origin requests. Detailed authentication and resource-download rules are in [SECURITY.md](../SECURITY.md).

## Maintenance and showcase

V11 `clean_cache` removes recognized derived WAVs and owned shell previews, not original attachments, database state, fragments, or unknown files. It skips links and files held open by playback. Handle-based validation checks normalized volume-GUID parents before deletion; an in-place junction change cannot redirect cleanup to an outside file. The API has null response data; the native client reports removed bytes/files and skipped entries.

V11 `set_restart` restarts the protocol runtime while preserving simulator data and the desktop process. It waits for the requested delay and initiating acknowledgement before shutdown. Concurrent pending requests coalesce into the first accepted restart. Stop/disposal cancels old requests, and generation checks prevent a stale restart from affecting a newer session. Both maintenance operations remain usable while the simulated account is offline.

Settings can save and restart into an isolated demo workspace. A bounded, validated `startup.json` stores only mode, protocol, and cadence. Explicit mode arguments override saved settings, which override `ASUKA_DEMO`. The showcase alternates a fixed group/private chat through 48 steps with protocol-specific content, actual logo media, SILK/WAV, H.264 video, recall, and replies. It runs without protocol networking or ordinary credentials.

The native loading overlay, media controls, quote cards, system-motion-aware About animation, and component license links are implemented. Source builds and protocol tests are not a substitute for verifying their interaction and visual quality on a Windows desktop.

## Known limits and remaining work

- **Local paths:** an explicitly authorized shared-directory mode is not implemented; arbitrary protocol paths remain rejected.
- **Rich content:** not every standard segment has a dedicated model, composer, or renderer. Some V11 structured/card/game segments remain generic unsupported content; Milky structured segments such as `light_app` can be preserved without full native presentation. Forward previews summarize node content.
- **Audio:** conversions beyond SILK-to-WAV/identified original formats remain incomplete. Other native playback depends on Windows decoder availability.
- **V12 scope:** optional MessagePack is not implemented. Guild/channel capabilities are not modeled by the current QQ private/group simulator.
- **History:** the native message view loads the latest 500 messages; older quote navigation does not fetch additional history.
- **Delivery:** WebHook queues are bounded and ephemeral, and offline-suppressed events are not replayed.
- **Interop and UI:** broader real-framework interoperability and native interaction testing remain necessary, including restart, seeking, expanded-player restoration, accessibility, and capability/draft changes across accounts/protocols.
- **Distribution:** CI packages are unsigned development artifacts. Build/package checks do not establish signed-release readiness or complete protocol conformance.

## Verification

The current recorded Windows Release baseline, dated **2026-09-08**, contains **727 cases: 726 passed, zero failed, and one platform-specific case excluded**. The excluded case is `NonWindowsCleanupConservativelyPreservesCandidates`, which checks behavior on non-Windows hosts. x64/ARM64 Release compilation, formatting, and the native browser-engine source guard have also passed. The reply-card adjustment received source review and an x64 Release build; it did not change protocol/store behavior.

Representative coverage is available in the repository:

| Area | Evidence |
| --- | --- |
| Protocol gates and message operations | [capability tests](../tests/Asuka.Tests/Protocols/ProtocolCapabilitiesTests.cs), [message interactions](../tests/Asuka.Tests/Core/MessageInteractionTests.cs) |
| Milky requests/files/notifications | [protocol tests](../tests/Asuka.Tests/Protocols), [core state tests](../tests/Asuka.Tests/Core) |
| Anonymous identity isolation | [V11 anonymous tests](../tests/Asuka.Tests/Protocols/OneBotAnonymousTests.cs), [Milky isolation tests](../tests/Asuka.Tests/Protocols/MilkyAnonymousIsolationTests.cs) |
| Credentials and redaction | [credential contract tests](../tests/Asuka.Tests/Protocols/AccountCredentialsProtocolTests.cs), [traffic redaction](../tests/Asuka.Tests/Protocols/ProtocolTrafficRedactionTests.cs) |
| Real SILK and safe cleanup | [playback fixtures](../tests/Asuka.Tests/Protocols/SilkAudioPlaybackTests.cs), [cleanup races and locks](../tests/Asuka.Tests/Protocols/MediaCacheCleanupTests.cs) |
| Service restart and transports | [restart tests](../tests/Asuka.Tests/Protocols/OneBotRestartTests.cs), [HTTP](../tests/Asuka.Tests/Protocols/OneBotHttpSessionTests.cs), [WebHooks](../tests/Asuka.Tests/Protocols/OneBotWebhookSessionTests.cs), [SSE](../tests/Asuka.Tests/Protocols/MilkySseTests.cs) |
| Isolated demo settings | [launch options](../tests/Asuka.Tests/App/DemoLaunchOptionsTests.cs), [mode persistence](../tests/Asuka.Tests/App/DemoModeSettingsStoreTests.cs) |

To produce a new test report:

```powershell
dotnet test tests/Asuka.Tests/Asuka.Tests.csproj --configuration Release -p:Platform=x64 --logger trx --results-directory artifacts/test-results
```

[CI](https://github.com/Kiyorae/asuka/actions) retains TRX reports for each validation run and publishes x64/ARM64 unsigned development artifacts only after source validation succeeds. Packaging verifies the Full Trust manifest, target architecture, bundled decoder/license, showcase media hashes, and self-contained payload. Follow the [download and verification instructions](../README.md#download-a-development-build) for the selected run.

Automated results prove the covered scenarios only. They do not claim a completed native UI acceptance test, universal codec compatibility, or full implementation of all three protocols.
