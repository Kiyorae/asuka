#!/usr/bin/env python3
"""Reproduce Asuka's local media showcase without network access or UI tools.

Requires Python 3.10+ and Pillow. Video uses an existing FFmpeg; --native-mp4
uses the existing .NET 10/Windows SDK and Windows MediaTranscoder for H.264 MP4.
No dependency download is performed. --verify checks existing fixtures without
rewriting them.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import wave

from PIL import Image, ImageChops, __version__ as pillow_version


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/Asuka.App/Assets/AsukaSquare150x150Logo.scale-400.png"
OUTPUT = ROOT / "src/Asuka.App/Assets/Showcase"
SIDE = 256
FRAME_COUNT = 36
FRAME_DURATION_MS = 100
BACKGROUND = (247, 246, 242)
SAMPLE_RATE = 24_000
NATIVE_PROJECT = ROOT / "scripts/ShowcaseVideoEncoder/ShowcaseVideoEncoder.csproj"
NATIVE_HELPER = NATIVE_PROJECT.parent / "bin/Release/net10.0-windows10.0.26100.0/ShowcaseVideoEncoder.dll"


def run(arguments: list[str], **kwargs) -> subprocess.CompletedProcess:
    if os.name == "nt":
        kwargs.setdefault("creationflags", subprocess.CREATE_NO_WINDOW)
    return subprocess.run(arguments, check=True, capture_output=True, timeout=60, **kwargs)


def build_native_helper() -> None:
    if os.name != "nt":
        raise RuntimeError("--native-mp4 requires Windows with the existing .NET 10/Windows SDK")
    packages = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    # Only use packages already in the local cache. No remote NuGet source is used.
    run(["dotnet", "restore", str(NATIVE_PROJECT), "--source", str(packages), "--ignore-failed-sources", "--verbosity", "quiet"])
    run(["dotnet", "build", str(NATIVE_PROJECT), "-c", "Release", "--no-restore", "--verbosity", "quiet"])


def write_native_mp4() -> dict:
    source = OUTPUT / "logo-spin.webm"
    if not source.is_file():
        raise FileNotFoundError("Generate logo-spin.webm with an existing VP8 FFmpeg before using --native-mp4")
    encoded = run(["dotnet", str(NATIVE_HELPER), str(source), str(OUTPUT / "logo-spin.mp4")])
    result = json.loads(encoded.stdout.decode("utf-8"))
    if not result.get("success"):
        raise RuntimeError(f"Native MP4 transcoding failed: {result}")
    return result


def verify_native_mp4() -> dict:
    inspected = run(["dotnet", str(NATIVE_HELPER), "--verify", str(OUTPUT / "logo-spin.mp4")])
    details = json.loads(inspected.stdout.decode("utf-8"))
    if details["Container"] != "MPEG4" or details["VideoCodec"] != "H264" \
            or (details["Width"], details["Height"]) != (SIDE, SIDE) \
            or abs(details["DurationSeconds"] - 3.6) > 0.15 \
            or details["FrameRateNumerator"] != 10 * details["FrameRateDenominator"]:
        raise ValueError(f"Native MP4 format verification failed: {details}")
    return {"width": details["Width"], "height": details["Height"],
            "duration_seconds": details["DurationSeconds"], "codec": details["VideoCodec"],
            "container": details["Container"], "frames_per_second": 10,
            "verification": "Windows MediaEncodingProfile and StorageFile VideoProperties; metadata validation, not a full frame decode"}


def find_ffmpeg(explicit: str | None) -> Path | None:
    if explicit:
        candidate = Path(explicit).resolve()
        if not candidate.is_file():
            raise FileNotFoundError(candidate)
        return candidate
    configured = os.environ.get("ASUKA_FFMPEG") or shutil.which("ffmpeg")
    if configured:
        return Path(configured).resolve()
    # The desktop test tooling may already carry a small VP8-only FFmpeg build.
    # Looking in this specific cache does not scan the disk or install anything.
    if local := os.environ.get("LOCALAPPDATA"):
        candidates = sorted((Path(local) / "ms-playwright").glob("ffmpeg-*/ffmpeg-win64.exe"))
        if candidates:
            return candidates[-1]
    return None


def rotation_frames() -> list[Image.Image]:
    supersample = 4
    with Image.open(SOURCE) as original:
        logo = original.convert("RGBA")
    bounds = logo.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("The existing logo is empty")
    logo = logo.crop(bounds)
    # Even at 45 degrees, a 160 px square fits with >14 px of canvas margin.
    logo.thumbnail((160 * supersample, 160 * supersample), Image.Resampling.LANCZOS)
    frames = []
    for index in range(FRAME_COUNT):
        rotated = logo.rotate(-360 * index / FRAME_COUNT, Image.Resampling.BICUBIC, expand=True)
        canvas = Image.new("RGBA", (SIDE * supersample, SIDE * supersample), (*BACKGROUND, 255))
        canvas.alpha_composite(rotated, ((canvas.width - rotated.width) // 2, (canvas.height - rotated.height) // 2))
        frames.append(canvas.convert("RGB").resize((SIDE, SIDE), Image.Resampling.LANCZOS))
    return frames


def write_gif(frames: list[Image.Image]) -> None:
    # A shared palette avoids color flicker and fixes quantization across frames.
    palette_source = Image.new("RGB", (SIDE * 6, SIDE * 6))
    for index, frame in enumerate(frames):
        palette_source.paste(frame, ((index % 6) * SIDE, (index // 6) * SIDE))
    palette = palette_source.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    quantized = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]
    quantized[0].save(
        OUTPUT / "logo-spin.gif", save_all=True, append_images=quantized[1:],
        duration=FRAME_DURATION_MS, loop=0, disposal=2, optimize=False,
        comment=b"Asuka existing app logo; generated by scripts/generate-showcase-assets.py",
    )


def write_wave() -> None:
    samples = bytearray()
    duration = 2.0
    # Original quiet C4/E4/G4 sine chord, with smooth attack/release and no recording.
    for index in range(round(SAMPLE_RATE * duration)):
        time = index / SAMPLE_RATE
        attack = min(1.0, time / 0.16)
        release = min(1.0, (duration - time) / 0.32)
        envelope = math.sin(math.pi * attack / 2) ** 2 * math.sin(math.pi * release / 2) ** 2
        amplitude = sum(math.sin(2 * math.pi * frequency * time) for frequency in (261.625565, 329.627557, 391.995435))
        sample = round(32767 * 0.055 * envelope * amplitude)
        samples.extend(struct.pack("<h", sample))
    with wave.open(str(OUTPUT / "voice.wav"), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        output.writeframes(samples)


def write_silk() -> None:
    test = ROOT / "tests/Asuka.Tests/Protocols/SilkAudioPlaybackTests.cs"
    match = re.search(r'private const string TencentTone\s*=\s*"([A-Za-z0-9+/=]+)";', test.read_text(encoding="utf-8"))
    if match is None:
        raise ValueError("The pinned TencentTone fixture could not be found")
    (OUTPUT / "voice.silk").write_bytes(base64.b64decode(match.group(1), validate=True))
    shutil.copyfile(ROOT / "native/Asuka.SilkDecoder/LICENSE.txt", OUTPUT / "SILK-SDK-LICENSE.txt")


def write_video(ffmpeg: Path, frames: list[Image.Image]) -> dict:
    executable = str(ffmpeg)
    encoders = run([executable, "-hide_banner", "-encoders"]).stdout.decode("utf-8", errors="replace")
    muxers = run([executable, "-hide_banner", "-muxers"]).stdout.decode("utf-8", errors="replace")
    version = run([executable, "-version"]).stdout.decode("utf-8", errors="replace").splitlines()[0]
    # JPEG image2pipe is available even in Playwright's intentionally small build.
    jpeg_frames = io.BytesIO()
    for frame in frames:
        frame.save(jpeg_frames, "JPEG", quality=96, subsampling=0, optimize=False)
    shared = [executable, "-hide_banner", "-loglevel", "error", "-y", "-f", "image2pipe",
              "-vcodec", "mjpeg", "-r", "10", "-i", "pipe:0", "-an", "-map_metadata", "-1",
              "-threads", "1", "-pix_fmt", "yuv420p", "-fflags", "+bitexact", "-flags:v", "+bitexact"]
    outputs = []
    if re.search(r"\blibvpx\s", encoders) and re.search(r"\bwebm\b", muxers):
        target = OUTPUT / "logo-spin.webm"
        run([*shared, "-c:v", "libvpx", "-b:v", "400k", "-g", "36", "-deadline", "good",
             "-f", "webm", str(target)], input=jpeg_frames.getvalue())
        outputs.append(target.name)
    mp4_codec = "libx264" if re.search(r"\blibx264\s", encoders) else "mpeg4" if re.search(r"\bmpeg4\s", encoders) else None
    if mp4_codec and re.search(r"\bmp4\b", muxers):
        target = OUTPUT / "logo-spin.mp4"
        codec_options = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-profile:v", "baseline"] if mp4_codec == "libx264" else ["-c:v", "mpeg4", "-q:v", "3"]
        run([*shared, *codec_options, "-movflags", "+faststart", "-f", "mp4", str(target)], input=jpeg_frames.getvalue())
        outputs.append(target.name)
    return {"ffmpeg": version, "generated": outputs,
            "mp4_available": "logo-spin.mp4" in outputs,
            "note": "Video is silent; voice.wav is the separate audio fixture."}


def verify(ffmpeg: Path | None) -> dict:
    result: dict = {}
    if (OUTPUT / "logo.png").read_bytes() != SOURCE.read_bytes():
        raise ValueError("logo.png must remain a byte-for-byte copy of the existing app logo")
    with Image.open(OUTPUT / "logo-spin.gif") as animation:
        if animation.size != (SIDE, SIDE) or animation.n_frames != FRAME_COUNT or animation.info.get("loop") != 0:
            raise ValueError("GIF dimensions, frame count or infinite loop metadata are incorrect")
        hashes = set()
        durations = []
        margins = []
        for index in range(animation.n_frames):
            animation.seek(index)
            frame = animation.convert("RGB")
            difference = ImageChops.difference(frame, Image.new("RGB", frame.size, frame.getpixel((0, 0))))
            bounds = difference.getbbox()
            if bounds is None or min(bounds[0], bounds[1], SIDE - bounds[2], SIDE - bounds[3]) < 2:
                raise ValueError(f"GIF frame {index} is blank or clipped")
            margins.append(min(bounds[0], bounds[1], SIDE - bounds[2], SIDE - bounds[3]))
            hashes.add(hashlib.sha256(frame.tobytes()).hexdigest())
            durations.append(animation.info["duration"])
        if len(hashes) != FRAME_COUNT or durations != [FRAME_DURATION_MS] * FRAME_COUNT:
            raise ValueError("Every rotation frame must differ and have the declared timing")
        result["gif"] = {"width": SIDE, "height": SIDE, "frames": FRAME_COUNT, "duration_ms": sum(durations),
                         "loop": 0, "unique_frames": len(hashes), "minimum_edge_margin_px": min(margins)}
    with wave.open(str(OUTPUT / "voice.wav"), "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getnframes()) != (1, 2, SAMPLE_RATE, 48000):
            raise ValueError("WAV must be two seconds of 24 kHz mono signed 16-bit PCM")
        samples = [sample[0] for sample in struct.iter_unpack("<h", audio.readframes(audio.getnframes()))]
        peak = max(abs(sample) for sample in samples)
        rms = math.sqrt(sum(sample * sample for sample in samples) / len(samples))
        if not 0 < rms < 4000 or peak > 6000:
            raise ValueError("Audio must be audible, quiet and unclipped")
        result["wav"] = {"sample_rate": SAMPLE_RATE, "channels": 1, "bits_per_sample": 16,
                         "duration_seconds": 2, "peak": peak, "rms": round(rms, 3)}
    silk = (OUTPUT / "voice.silk").read_bytes()
    if not silk.startswith(b"\x02#!SILK_V3"):
        raise ValueError("Expected the real Tencent SILK v3 fixture")
    offset, packets = 10, 0
    while offset < len(silk):
        length = struct.unpack_from("<H", silk, offset)[0]
        if not 0 < length <= 1024 or offset + 2 + length > len(silk):
            raise ValueError("Invalid SILK packet framing")
        offset += 2 + length
        packets += 1
    if packets != 10:
        raise ValueError("Expected ten 20 ms SILK packets")
    result["silk"] = {"container": "Tencent SILK v3", "packets": packets, "duration_seconds": 0.2}
    for configuration in ("Release", "Debug"):
        decoder = ROOT / f"src/Asuka.Protocols/bin/{configuration}/net10.0/media/asuka-silk-decoder.exe"
        if decoder.is_file():
            decoded = run([str(decoder), str(OUTPUT / "voice.silk")]).stdout
            if len(decoded) != 9600 or not any(decoded):
                raise ValueError("Bundled SILK decoder did not produce the expected non-silent 0.2 s PCM")
            result["silk"]["decoded_pcm_bytes"] = len(decoded)
            break
    (OUTPUT / "notes.txt").read_text(encoding="utf-8")
    if ffmpeg:
        for name in ("logo-spin.webm", "logo-spin.mp4"):
            if not (OUTPUT / name).is_file():
                continue
            if name.endswith(".mp4") and os.name == "nt" and NATIVE_HELPER.is_file():
                # The bundled VP8-only FFmpeg cannot decode H.264. Use actual
                # Windows file metadata and never claim a decoded-frame check.
                result[name] = verify_native_mp4()
                continue
            with tempfile.TemporaryDirectory(prefix="asuka-showcase-check-") as directory:
                decoded = run([str(ffmpeg), "-hide_banner", "-i", str(OUTPUT / name), "-vsync", "0",
                               "-f", "image2", "-vcodec", "png", str(Path(directory) / "%03d.png")])
                images = sorted(Path(directory).glob("*.png"))
                if len(images) != FRAME_COUNT:
                    raise ValueError(f"{name} must decode to exactly {FRAME_COUNT} frames")
                for path in images:
                    with Image.open(path) as frame:
                        if frame.size != (SIDE, SIDE) or ImageChops.difference(frame.convert("RGB"), Image.new("RGB", frame.size, BACKGROUND)).getbbox() is None:
                            raise ValueError(f"{name} contains a blank or incorrectly sized frame")
                diagnostics = decoded.stderr.decode("utf-8", errors="replace")
                duration = re.search(r"Duration:\s+(\d+):(\d+):(\d+\.\d+)", diagnostics)
                if duration is None or abs(int(duration[1]) * 3600 + int(duration[2]) * 60 + float(duration[3]) - 3.6) > 0.05:
                    raise ValueError(f"{name} does not declare a 3.6 s duration")
                result[name] = {"decoded_frames": len(images), "width": SIDE, "height": SIDE,
                                "duration_seconds": 3.6, "codec": "VP8" if name.endswith(".webm") else "H.264 or MPEG-4 (see encoder record)"}
    elif (OUTPUT / "logo-spin.mp4").is_file():
        if os.name != "nt" or not NATIVE_HELPER.is_file():
            raise RuntimeError("MP4 verification needs a full FFmpeg or the built Windows ShowcaseVideoEncoder helper")
        result["logo-spin.mp4"] = verify_native_mp4()
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", help="Existing FFmpeg executable; no download is attempted")
    parser.add_argument("--native-mp4", action="store_true", help="Build the helper from cached SDK packages and use Windows H.264 encoding (or native verification with --verify)")
    parser.add_argument("--verify", action="store_true", help="Verify existing outputs without rewriting them")
    arguments = parser.parse_args()
    ffmpeg = find_ffmpeg(arguments.ffmpeg)
    if arguments.native_mp4:
        build_native_helper()
    if arguments.verify:
        print(json.dumps(verify(ffmpeg), ensure_ascii=False, indent=2))
        return
    OUTPUT.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SOURCE, OUTPUT / "logo.png")
    frames = rotation_frames()
    write_gif(frames)
    write_wave()
    write_silk()
    (OUTPUT / "notes.txt").write_text(
        "Asuka 附件演示\n\n这是一份 UTF-8 文本文档。\n"
        "项目 Logo 可以作为图片发送，旋转 Logo 用于展示动态图片。\n"
        "这份文档用于验证文件名、下载、保存及中文内容。\n\n"
        "Hello, Asuka! 你好，明日香！ 🌿\n", encoding="utf-8", newline="\n")
    video = write_video(ffmpeg, frames) if ffmpeg else {"generated": [], "mp4_available": False, "note": "No existing FFmpeg found; video generation skipped."}
    if arguments.native_mp4:
        video["native_mp4"] = write_native_mp4()
        if "logo-spin.mp4" not in video["generated"]:
            video["generated"].append("logo-spin.mp4")
    if (OUTPUT / "logo-spin.mp4").is_file():
        video["mp4_available"] = True
        if "logo-spin.mp4" not in video["generated"]:
            video["mp4_note"] = "Existing MP4 retained; use --native-mp4 to regenerate it from the current WebM."
    report = {"generator": "scripts/generate-showcase-assets.py", "pillow": pillow_version,
              "logo_source": SOURCE.relative_to(ROOT).as_posix(), "video_generation": video,
              "verification": verify(ffmpeg)}
    report["files"] = {path.name: {"bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                       for path in sorted(OUTPUT.iterdir()) if path.suffix.lower() in {".png", ".gif", ".wav", ".silk", ".txt", ".webm", ".mp4"}}
    (OUTPUT / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
