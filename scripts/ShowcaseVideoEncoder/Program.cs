using System.Text.Json;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

// Console-only build helper: no MediaPlayer, window, picker, or UI activation.
if (args.Length == 2 && args[0] == "--verify")
{
    using var verifyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[1])).AsTask(verifyTimeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(await InspectAsync(file, verifyTimeout.Token)));
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: ShowcaseVideoEncoder INPUT.webm OUTPUT.mp4 | --verify OUTPUT.mp4");
    return 1;
}

var sourcePath = Path.GetFullPath(args[0]);
var destinationPath = Path.GetFullPath(args[1]);
if (sourcePath == destinationPath || !destinationPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The output must be a distinct MP4 path.");
    return 1;
}

var directory = Path.GetDirectoryName(destinationPath)!;
var temporaryPath = Path.Combine(directory, $".showcase-{Guid.NewGuid():N}.mp4");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
try
{
    var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(timeout.Token);
    var folder = await StorageFolder.GetFolderFromPathAsync(directory).AsTask(timeout.Token);
    var destination = await folder.CreateFileAsync(Path.GetFileName(temporaryPath), CreationCollisionOption.FailIfExists).AsTask(timeout.Token);
    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
    profile.Audio = null;
    profile.Video.Width = 256;
    profile.Video.Height = 256;
    profile.Video.Bitrate = 400_000;
    profile.Video.FrameRate.Numerator = 10;
    profile.Video.FrameRate.Denominator = 1;
    profile.Video.PixelAspectRatio.Numerator = 1;
    profile.Video.PixelAspectRatio.Denominator = 1;
    var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
    var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile).AsTask(timeout.Token);
    if (!prepared.CanTranscode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { success = false, failure = prepared.FailureReason.ToString() }));
        return 2;
    }

    await prepared.TranscodeAsync().AsTask(timeout.Token);
    var inspected = await InspectAsync(destination, timeout.Token);
    if (!inspected.VideoCodec.Equals("H264", StringComparison.OrdinalIgnoreCase)
        || inspected.Width != 256 || inspected.Height != 256 || Math.Abs(inspected.DurationSeconds - 3.6) > 0.15)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { success = false, failure = "Output format verification failed", inspected }));
        return 3;
    }

    timeout.Token.ThrowIfCancellationRequested();
    File.Move(temporaryPath, destinationPath, overwrite: true);
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, encoder = "Windows.Media.Transcoding.MediaTranscoder", output = inspected }));
    return 0;
}
catch (Exception error) when (error is System.Runtime.InteropServices.COMException or IOException
    or UnauthorizedAccessException or ArgumentException or OperationCanceledException or NotSupportedException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { success = false, failure = error.Message, hresult = error.HResult }));
    return 4;
}
finally
{
    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
}

static async Task<VideoInspection> InspectAsync(StorageFile file, CancellationToken cancellationToken)
{
    var encoding = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(cancellationToken);
    var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
    return new VideoInspection(encoding.Container.Subtype, encoding.Video.Subtype, properties.Width,
        properties.Height, properties.Duration.TotalSeconds, encoding.Video.FrameRate.Numerator,
        encoding.Video.FrameRate.Denominator, properties.Bitrate);
}

internal sealed record VideoInspection(string Container, string VideoCodec, uint Width, uint Height,
    double DurationSeconds, uint FrameRateNumerator, uint FrameRateDenominator, uint Bitrate);
