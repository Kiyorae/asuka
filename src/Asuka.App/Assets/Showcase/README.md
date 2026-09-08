# Local showcase media

These small, offline fixtures demonstrate actual media attachments in the client.
The artwork is the existing Asuka app logo; no new logo was generated or redesigned.

| File | Content |
| --- | --- |
| `logo.png` | Exact copy of the existing 600 × 600 transparent app icon. |
| `logo-spin.gif` | 256 × 256 clockwise rotation, 36 different frames, 100 ms per frame, 3.6-second infinite loop. A neutral background and rotation margin keep edges visible. |
| `logo-spin.mp4` | Silent H.264/MPEG-4 video, 256 × 256, 10 fps, 3.6 seconds. Generated from the same WebM through Windows MediaTranscoder; preferred for Windows playback. |
| `logo-spin.webm` | Silent VP8/WebM video of the same 36-frame, 3.6-second rotation. Requires a player with VP8/WebM support. It is not an MP4 file. |
| `voice.wav` | Original quiet C4/E4/G4 chord with smooth fades, two seconds, 24 kHz mono, signed 16-bit PCM. |
| `voice.silk` | Existing real Tencent SILK v3 test data: ten 20 ms packets of an original synthetic 400 Hz triangle wave, 0.2 seconds total. Exercises the bundled SILK decoder. |
| `notes.txt` | Small UTF-8 attachment containing Chinese, English and an emoji. |
| `manifest.json` | Generated hashes, format details and verification evidence. |

The local FFmpeg available during fixture generation is Playwright's VP8-only build;
it has no H.264 encoder or MP4 muxer. The checked-in MP4 was successfully transcoded
using the system Windows MediaTranscoder with hardware acceleration disabled.
Windows `MediaEncodingProfile.CreateFromFileAsync` and `StorageFile` video properties
confirm its actual MPEG4 container, H264 codec, dimensions, frame rate and duration.
This is native metadata validation, not a claimed FFmpeg H.264 frame decode. A WebM
is never renamed to `.mp4`.

## Reproduce and verify

From the repository root, with Python 3.10+ and Pillow installed:

```powershell
python scripts/generate-showcase-assets.py
python scripts/generate-showcase-assets.py --verify
# Windows: regenerate the MP4 using existing .NET 10/Windows SDK packages and codecs.
python scripts/generate-showcase-assets.py --native-mp4
# Build the native verifier from the local SDK cache without rewriting fixtures.
python scripts/generate-showcase-assets.py --verify --native-mp4
# Optional: select an already installed full FFmpeg to generate MP4 as well.
python scripts/generate-showcase-assets.py --ffmpeg C:/Tools/ffmpeg/bin/ffmpeg.exe
```

The native console helper lives in [`scripts/ShowcaseVideoEncoder`](../../../../scripts/ShowcaseVideoEncoder).
It creates no window or player, allows 45 seconds for transcoding, validates a
temporary output before publication, and cleans unsuccessful temporary outputs.
Its restore uses only the local NuGet cache; it does not acquire missing SDK/codecs.
If the native operation reports an unsupported format, retain the working WebM.
The base command preserves an existing MP4; use `--native-mp4` after changing the
logo or rotation inputs to regenerate the MP4 as well.

No dependencies are downloaded. The script detects `ffmpeg` on PATH,
`ASUKA_FFMPEG`, or an existing Playwright FFmpeg cache on Windows. Generation is
deterministic for PNG/GIF/WAV/SILK/WebM with the same logo, Python/Pillow and FFmpeg
versions. Single-threaded bit-exact FFmpeg flags suppress variable container
metadata. The native H.264 result is reproducible through the helper but its exact
bytes can vary with Windows codec versions or container timestamps. The script verifies
GIF frame count, timing, loop, changing pixels and uncut edges; PCM format, duration
and amplitude; SILK packet framing and, when already built, the bundled decoder's
output; WebM frame count, dimensions and duration through actual decoding; and MP4
codec/dimensions/frame rate/duration through native Windows file inspection when
the local FFmpeg lacks H.264 support.

## Sources and licensing

- `logo.png`, GIF and video derive from
  [`../AsukaSquare150x150Logo.scale-400.png`](../AsukaSquare150x150Logo.scale-400.png),
  itself the application's existing [`Akame.png`](../Akame.png) artwork. The source
  image is copied exactly for `logo.png`; only animation/scaling/background are
  added for GIF/video. This checkout contains no separate artwork attribution or
  license record; these fixtures retain the existing artwork's provenance and do
  not claim a new or independent artwork license.
- The project is distributed under the repository's
  [GNU AGPL v3 license](../../../../LICENSE). The generator, original synthetic
  chord and text are project material. They contain no microphone recording or
  downloaded audio/video content.
- `voice.silk` is decoded from the checked-in `TencentTone` constant in
  [`SilkAudioPlaybackTests.cs`](../../../../tests/Asuka.Tests/Protocols/SilkAudioPlaybackTests.cs).
  Its original source is
  [`generate-tone.c`](../../../../native/Asuka.SilkDecoder/fixtures/generate-tone.c),
  encoded by the pinned Skype SILK SDK 1.0.9.6. SDK provenance and regeneration
  instructions are in the [decoder README](../../../../native/Asuka.SilkDecoder/README.md).
  The SDK's original BSD-style notices are retained in `SILK-SDK-LICENSE.txt`.
  The SDK license is not being used to claim ownership of third-party recordings;
  this fixture is original synthetic test material.
