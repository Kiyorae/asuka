using System.Globalization;
using Asuka.Protocols;

namespace Asuka.App;

internal sealed record DemoLaunchOptions(bool Enabled = false, ProtocolKind Protocol = ProtocolKind.Milky, double IntervalSeconds = 3)
{
    public static DemoLaunchOptions Parse(IEnumerable<string> arguments, string? environmentValue = null, DemoLaunchOptions? saved = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var flags = arguments.ToArray();
        bool? explicitEnabled = null;
        foreach (var flag in flags)
        {
            if (flag == "--demo") explicitEnabled = true;
            else if (flag == "--no-demo") explicitEnabled = false;
        }
        var enabled = explicitEnabled ?? saved?.Enabled
            ?? (string.Equals(environmentValue, "1", StringComparison.Ordinal)
                || string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase));
        var defaults = saved ?? new DemoLaunchOptions();
        // Preserve the saved choices for Settings while ignoring irrelevant CLI
        // protocol/cadence arguments when the final launch mode is disabled.
        if (!enabled) return defaults with { Enabled = false };
        var protocol = defaults.Protocol;
        var interval = defaults.IntervalSeconds;
        foreach (var flag in flags)
        {
            if (flag.StartsWith("--demo-protocol=", StringComparison.Ordinal))
                protocol = flag[16..].ToLowerInvariant() switch
                {
                    "milky" => ProtocolKind.Milky,
                    "onebot11" or "v11" => ProtocolKind.OneBotV11,
                    "onebot12" or "v12" => ProtocolKind.OneBotV12,
                    _ => throw new ArgumentException("--demo-protocol must be milky, onebot11, or onebot12."),
                };
            if (flag.StartsWith("--demo-interval=", StringComparison.Ordinal)
                && (!double.TryParse(flag[16..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out interval)
                    || !double.IsFinite(interval) || interval is < 1 or > 30))
                throw new ArgumentException("--demo-interval must be between 1 and 30 seconds.");
        }
        var result = new DemoLaunchOptions(true, protocol, interval);
        result.Validate();
        return result;
    }

    public string[] ToRestartArguments()
    {
        if (!Enabled) return ["--no-demo"];
        Validate();
        var protocol = Protocol switch
        {
            ProtocolKind.Milky => "milky",
            ProtocolKind.OneBotV11 => "onebot11",
            ProtocolKind.OneBotV12 => "onebot12",
            _ => throw new ArgumentException("Unknown demo protocol."),
        };
        return ["--demo", $"--demo-protocol={protocol}", $"--demo-interval={IntervalSeconds.ToString("R", CultureInfo.InvariantCulture)}"];
    }

    internal void Validate(string? parameterName = null)
    {
        if (!Enum.IsDefined(Protocol) || !double.IsFinite(IntervalSeconds) || IntervalSeconds is < 1 or > 30)
            throw new ArgumentException("Demo options require a supported protocol and an interval between 1 and 30 seconds.", parameterName);
    }

    public string GetDataDirectory(string localApplicationData) => Path.Combine(localApplicationData, "Asuka", "Showcase", Protocol.ToString());
}
