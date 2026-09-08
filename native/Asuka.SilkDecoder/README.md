# Bundled legacy SILK decoder

`vendor/interface` and `vendor/src` contain the Skype SILK SDK 1.0.9.6 source,
copied without changes from `silk/interface` and `silk/src` in
[kn007/silk-v3-decoder](https://github.com/kn007/silk-v3-decoder/tree/507be6bca8ce1fb977a061481f1d79e8c610e309/silk),
commit `507be6bca8ce1fb977a061481f1d79e8c610e309`.
Each file retains its original Skype copyright and BSD-style license header.
`LICENSE.txt` is also copied beside the executable in application output and
publish packages. The mirror's CLI, conversion scripts, and binary releases
are not included.

The app builds its own `decoder.c` adapter using the Visual Studio C++ build
tools, with the static C runtime. There is no download or tool dependency at
runtime. Standard SILK, Tencent's `0x02` prefix, and the AMR-prefixed Tencent
variant are recognized by their bytes, regardless of attachment filename.
PCM output is 24 kHz, mono, signed 16-bit little endian; the C# wrapper writes
the WAV container and atomically caches the result. Ordinary FFmpeg's Opus
decoder does not replace this legacy SILK v3 decoder.

The helper validates every length-prefixed packet (at most 1024 bytes), allows
at most five frames per packet, rejects truncation and decoding errors, and
limits input to 16 MiB and decoded audio to ten minutes. The client also bounds
output independently, permits two concurrent conversions, and kills the
helper on cancellation or a 30-second timeout. The helper has process
isolation and resource bounds; it is not an operating-system security sandbox.

Builds automatically include the x64 helper in both x64 and ARM64 packages.
On Windows 11 ARM64 it uses Windows' supported
[x64 application emulation](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation);
the repository already requires Windows 11 24H2 or newer. The standalone
build script accepts `-Architecture arm64` for a native build when the MSVC
ARM64 build tools are installed.

## Test fixture

`fixtures/generate-tone.c` encodes an original synthetic 400 Hz triangle wave
(ten 20 ms packets) as Tencent SILK. No third-party sound recording is used.
The checked-in base64 test fixture is generated using the pinned SDK:

```powershell
./scripts/build-silk-decoder.ps1 -OutputDirectory artifacts/silk-fixture -GenerateTestFixture
./artifacts/silk-fixture/asuka-silk-fixture.exe artifacts/silk-fixture/tone.silk
[Convert]::ToBase64String([IO.File]::ReadAllBytes('artifacts/silk-fixture/tone.silk'))
```

The tests decode these actual compressed packets and verify WAV format,
duration, non-silent PCM, standard/Tencent/AMR headers, atomic cache reuse,
malformed input rejection, size bounds, and cancellation.
