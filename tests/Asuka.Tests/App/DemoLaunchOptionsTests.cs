using System.Globalization;
using Asuka.App;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.App;

[TestClass]
public sealed class DemoLaunchOptionsTests
{
    [TestMethod]
    public void DemoRequiresExplicitOptInAndDefaultsToSlowMilkyPlayback()
    {
        Assert.IsFalse(DemoLaunchOptions.Parse(["--demo-protocol=onebot12"]).Enabled);
        Assert.IsFalse(DemoLaunchOptions.Parse(["--demo-ish"], "0").Enabled);
        var options = DemoLaunchOptions.Parse(["--demo"]);
        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(ProtocolKind.Milky, options.Protocol);
        Assert.AreEqual(3d, options.IntervalSeconds);
        Assert.IsTrue(DemoLaunchOptions.Parse([], "true").Enabled);
    }

    [TestMethod]
    [DataRow("onebot11", ProtocolKind.OneBotV11)]
    [DataRow("onebot12", ProtocolKind.OneBotV12)]
    [DataRow("milky", ProtocolKind.Milky)]
    public void ProtocolAndCadenceAreExplicitAndStorageIsIsolated(string protocol, ProtocolKind expected)
    {
        var options = DemoLaunchOptions.Parse(["--demo", "--demo-protocol=" + protocol, "--demo-interval=4.5"]);
        Assert.AreEqual(expected, options.Protocol);
        Assert.AreEqual(4.5, options.IntervalSeconds);
        Assert.AreEqual(Path.Combine("local", "Asuka", "Showcase", expected.ToString()), options.GetDataDirectory("local"));
    }

    [TestMethod]
    [DataRow("--demo-interval=0")]
    [DataRow("--demo-interval=NaN")]
    [DataRow("--demo-interval=31")]
    [DataRow("--demo-protocol=custom")]
    public void InvalidDemoOptionsFailClearly(string flag) =>
        Assert.ThrowsExactly<ArgumentException>(() => DemoLaunchOptions.Parse(["--demo", flag]));

    [TestMethod]
    public void SavedModeOverridesEnvironmentWhileExplicitFlagsOverrideSavedMode()
    {
        var savedOff = new DemoLaunchOptions(false, ProtocolKind.OneBotV12, 7.5);
        var savedOn = savedOff with { Enabled = true };
        Assert.AreEqual(savedOff, DemoLaunchOptions.Parse([], "1", savedOff));
        Assert.AreEqual(savedOn, DemoLaunchOptions.Parse([], "0", savedOn));
        Assert.AreEqual(savedOn, DemoLaunchOptions.Parse(["--demo"], "0", savedOff));
        Assert.AreEqual(savedOff, DemoLaunchOptions.Parse(["--no-demo"], "true", savedOn));
        Assert.IsFalse(DemoLaunchOptions.Parse(["--no-demo"], "1").Enabled);
    }

    [TestMethod]
    public void LastExplicitModeFlagWinsAndDisabledModeIgnoresIrrelevantInvalidParameters()
    {
        var saved = new DemoLaunchOptions(true, ProtocolKind.OneBotV11, 9);
        Assert.IsTrue(DemoLaunchOptions.Parse(["--no-demo", "--demo"], "0", saved).Enabled);
        Assert.IsFalse(DemoLaunchOptions.Parse(["--demo", "--no-demo"], "1", saved).Enabled);
        var disabled = DemoLaunchOptions.Parse(["--demo", "--demo-protocol=broken", "--demo-interval=NaN", "--no-demo"], "1", saved);
        Assert.AreEqual(saved with { Enabled = false }, disabled);
        Assert.IsFalse(DemoLaunchOptions.Parse(["--demo-protocol=broken", "--demo-interval=NaN"], "1", saved with { Enabled = false }).Enabled);
    }

    [TestMethod]
    public void ExplicitProtocolAndCadenceIndependentlyOverrideSavedChoices()
    {
        var saved = new DemoLaunchOptions(true, ProtocolKind.OneBotV11, 6.25);
        Assert.AreEqual(saved with { Protocol = ProtocolKind.OneBotV12 },
            DemoLaunchOptions.Parse(["--demo-protocol=onebot12"], saved: saved));
        Assert.AreEqual(saved with { IntervalSeconds = 2.75 },
            DemoLaunchOptions.Parse(["--demo-interval=2.75"], saved: saved));
        Assert.AreEqual(new DemoLaunchOptions(true, ProtocolKind.Milky, 30),
            DemoLaunchOptions.Parse(["--demo-protocol=milky", "--demo-interval=30"], saved: saved));
    }

    [TestMethod]
    [DataRow(ProtocolKind.Milky, "milky")]
    [DataRow(ProtocolKind.OneBotV11, "onebot11")]
    [DataRow(ProtocolKind.OneBotV12, "onebot12")]
    public void RestartArgumentsRoundTripThroughInvariantParsing(ProtocolKind protocol, string expectedProtocol)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var expected = new DemoLaunchOptions(true, protocol, 7.25);
            var arguments = expected.ToRestartArguments();
            CollectionAssert.AreEqual(new[] { "--demo", "--demo-protocol=" + expectedProtocol, "--demo-interval=7.25" }, arguments);
            Assert.AreEqual(expected, DemoLaunchOptions.Parse(arguments, "0", new DemoLaunchOptions(false)));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [TestMethod]
    public void DisabledRestartExplicitlyOverridesEnvironmentAndStaleSavedMode()
    {
        var arguments = new DemoLaunchOptions(false, ProtocolKind.OneBotV11, 4).ToRestartArguments();
        Assert.HasCount(1, arguments);
        Assert.AreEqual("--no-demo", arguments[0]);
        Assert.IsFalse(DemoLaunchOptions.Parse(arguments, "true", new DemoLaunchOptions(true)).Enabled);
    }

    [TestMethod]
    public void RestartNeverSerializesUnsupportedOrNonFiniteEnabledOptions()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DemoLaunchOptions(true, (ProtocolKind)100).ToRestartArguments());
        foreach (var interval in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, 31 })
            Assert.ThrowsExactly<ArgumentException>(() => new DemoLaunchOptions(true, IntervalSeconds: interval).ToRestartArguments());
    }
}
