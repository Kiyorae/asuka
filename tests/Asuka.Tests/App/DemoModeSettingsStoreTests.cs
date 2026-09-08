using System.Text;
using System.Text.Json;
using Asuka.App;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.App;

[TestClass]
public sealed class DemoModeSettingsStoreTests
{
    private const string ValidJson = "{\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3}";
    private static readonly string[] ExpectedFields = ["Enabled", "IntervalSeconds", "Protocol"];

    [TestMethod]
    public async Task MissingSettingsAndMissingParentReturnNullWithoutCreatingAnything()
    {
        using var fixture = new SettingsFixture();

        Assert.IsNull(await fixture.Store.LoadAsync());
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.SettingsPath)));
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.SettingsPath)!);
        Assert.IsNull(await fixture.Store.LoadAsync());
        Assert.IsEmpty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow(true, ProtocolKind.Milky, 1d)]
    [DataRow(false, ProtocolKind.Milky, 30d)]
    [DataRow(true, ProtocolKind.OneBotV11, 4.5d)]
    [DataRow(false, ProtocolKind.OneBotV11, 1d)]
    [DataRow(true, ProtocolKind.OneBotV12, 30d)]
    [DataRow(false, ProtocolKind.OneBotV12, 4.5d)]
    public async Task EnabledAndDisabledOptionsRoundTripWithExactProtocolAndInterval(bool enabled, ProtocolKind protocol, double interval)
    {
        using var fixture = new SettingsFixture();
        var options = new DemoLaunchOptions(enabled, protocol, interval);

        await fixture.Store.SaveAsync(options);

        var reopened = new DemoModeSettingsStore(fixture.SettingsPath);
        Assert.AreEqual(options, await reopened.LoadAsync());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.SettingsPath));
        Assert.AreEqual(enabled, json.RootElement.GetProperty("Enabled").GetBoolean());
        Assert.AreEqual(protocol.ToString(), json.RootElement.GetProperty("Protocol").GetString());
        Assert.AreEqual(interval, json.RootElement.GetProperty("IntervalSeconds").GetDouble());
        CollectionAssert.AreEqual(ExpectedFields,
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        AssertOnlySettingsFile(fixture);
    }

    [TestMethod]
    public async Task SavingReplacesPriorValuesWithoutSerializingOrChangingOtherPreferences()
    {
        using var fixture = new SettingsFixture();
        var preferencesPath = Path.Combine(fixture.Root, "preferences.json");
        const string preferences = """
            {"Protocol":"OneBotV11","AccessToken":"endpoint-secret-fixture","OneBotWebhookSecret":"signing-secret-fixture","Theme":"Dark"}
            """;
        await File.WriteAllTextAsync(preferencesPath, preferences);
        await fixture.Store.SaveAsync(new DemoLaunchOptions(true, ProtocolKind.Milky, 2));
        var replacement = new DemoLaunchOptions(false, ProtocolKind.OneBotV12, 7.25);

        await fixture.Store.SaveAsync(replacement);

        Assert.AreEqual(replacement, await fixture.Store.LoadAsync());
        var persisted = await File.ReadAllTextAsync(fixture.SettingsPath);
        using var json = JsonDocument.Parse(persisted);
        CollectionAssert.AreEqual(ExpectedFields,
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("AccessToken", persisted);
        Assert.DoesNotContain("OneBotWebhookSecret", persisted);
        Assert.DoesNotContain("endpoint-secret-fixture", persisted);
        Assert.DoesNotContain("signing-secret-fixture", persisted);
        Assert.DoesNotContain("Theme", persisted);
        Assert.AreEqual(preferences, await File.ReadAllTextAsync(preferencesPath));
        Assert.HasCount(2, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("true")]
    [DataRow("123")]
    [DataRow("\"settings\"")]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3,}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3}// comment")]
    public async Task EmptyMalformedAndNonObjectDocumentsAreRejected(string contents)
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());

        Assert.AreEqual(contents, await File.ReadAllTextAsync(fixture.SettingsPath));
        AssertOnlySettingsFile(fixture);
    }

    [TestMethod]
    [DataRow("{\"Protocol\":\"Milky\",\"IntervalSeconds\":3}")]
    [DataRow("{\"Enabled\":true,\"IntervalSeconds\":3}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\"}")]
    [DataRow("{\"enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3,\"Extra\":0}")]
    [DataRow("{\"Enabled\":true,\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\",\"Protocol\":\"OneBotV11\",\"IntervalSeconds\":3}")]
    [DataRow("{\"Enabled\":true,\"Protocol\":\"Milky\",\"IntervalSeconds\":3,\"IntervalSeconds\":4}")]
    public async Task MissingUnknownDuplicateAndIncorrectlyCasedFieldsAreRejected(string contents)
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());

        Assert.AreEqual(contents, await File.ReadAllTextAsync(fixture.SettingsPath));
    }

    [TestMethod]
    [DataRow("\"true\"", "\"Milky\"", "3")]
    [DataRow("1", "\"Milky\"", "3")]
    [DataRow("null", "\"Milky\"", "3")]
    [DataRow("true", "0", "3")]
    [DataRow("true", "null", "3")]
    [DataRow("true", "\"milky\"", "3")]
    [DataRow("true", "\"OneBot11\"", "3")]
    [DataRow("true", "\"Unknown\"", "3")]
    [DataRow("true", "\"0\"", "3")]
    [DataRow("true", "\"Milky\"", "\"3\"")]
    [DataRow("true", "\"Milky\"", "null")]
    [DataRow("true", "\"Milky\"", "0")]
    [DataRow("true", "\"Milky\"", "0.999")]
    [DataRow("true", "\"Milky\"", "30.001")]
    [DataRow("true", "\"Milky\"", "1e309")]
    [DataRow("true", "\"Milky\"", "-1e309")]
    [DataRow("true", "\"Milky\"", "NaN")]
    [DataRow("false", "\"Unknown\"", "3")]
    [DataRow("false", "\"Milky\"", "0")]
    public async Task WrongTypesUnknownProtocolsAndInvalidIntervalsAreRejectedEvenWhenDisabled(
        string enabled, string protocol, string interval)
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync($"{{\"Enabled\":{enabled},\"Protocol\":{protocol},\"IntervalSeconds\":{interval}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());
    }

    [TestMethod]
    public async Task Exactly4096Utf8BytesAreAcceptedButOneAdditionalByteIsRejected()
    {
        using var fixture = new SettingsFixture();
        var padded = ValidJson + new string(' ', 4096 - Encoding.UTF8.GetByteCount(ValidJson));
        await fixture.WriteAsync(padded);
        Assert.AreEqual(4096L, new FileInfo(fixture.SettingsPath).Length);

        Assert.AreEqual(new DemoLaunchOptions(true, ProtocolKind.Milky, 3), await fixture.Store.LoadAsync());

        await fixture.WriteAsync(padded + " ");
        Assert.AreEqual(4097L, new FileInfo(fixture.SettingsPath).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());
        await fixture.WriteAsync(ValidJson + new string(' ', 16 * 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(0d)]
    [DataRow(0.999d)]
    [DataRow(30.001d)]
    public async Task InvalidSaveIntervalsPreserveTheExactPriorFileAndLeaveNoTemporaryFiles(double interval)
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(ValidJson + "\n");
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.SaveAsync(new DemoLaunchOptions(false, ProtocolKind.Milky, interval)));

        Assert.AreEqual("options", error.ParamName);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.SettingsPath));
        AssertOnlySettingsFile(fixture);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(999)]
    public async Task InvalidSaveProtocolsNeverWriteAConfiguration(int protocol)
    {
        using var fixture = new SettingsFixture();
        var invalid = new DemoLaunchOptions(false, (ProtocolKind)protocol, 3);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(invalid));

        Assert.AreEqual("options", error.ParamName);
        Assert.IsFalse(File.Exists(fixture.SettingsPath));
        Assert.IsEmpty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
        await fixture.Store.SaveAsync(new DemoLaunchOptions(true, ProtocolKind.OneBotV11, 2));
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(invalid));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.SettingsPath));
        AssertOnlySettingsFile(fixture);
    }

    [TestMethod]
    public async Task PreCancelledSaveAndLoadPreserveExistingSettingsAndDoNotCreateMissingFiles()
    {
        using var fixture = new SettingsFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var options = new DemoLaunchOptions(true, ProtocolKind.OneBotV12, 10);

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(options, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.LoadAsync(cancellation.Token));
        Assert.IsEmpty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
        await fixture.WriteAsync(ValidJson + "\n");
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath);

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(options, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.LoadAsync(cancellation.Token));

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.SettingsPath));
        AssertOnlySettingsFile(fixture);
        Assert.AreEqual(new DemoLaunchOptions(true, ProtocolKind.Milky, 3), await fixture.Store.LoadAsync());
    }

    [TestMethod]
    public async Task FailedAtomicReplacementPreservesOldSettingsAndRemovesItsTemporaryFile()
    {
        RequireWindows();
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(ValidJson + "\n");
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var replacement = new DemoLaunchOptions(false, ProtocolKind.OneBotV12, 12);
        using (var locked = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<Exception>(() => fixture.Store.SaveAsync(replacement));
            Assert.IsTrue(error is IOException or UnauthorizedAccessException, error.ToString());
            Assert.AreEqual((long)before.Length, locked.Length);
            AssertOnlySettingsFile(fixture);
        }

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.SettingsPath));
        Assert.AreEqual(new DemoLaunchOptions(true, ProtocolKind.Milky, 3), await fixture.Store.LoadAsync());
        await fixture.Store.SaveAsync(replacement);
        Assert.AreEqual(replacement, await fixture.Store.LoadAsync());
        AssertOnlySettingsFile(fixture);
    }

    [TestMethod]
    public async Task ReadSharingFailuresPropagateInsteadOfBeingTreatedAsMissingOrMalformedSettings()
    {
        RequireWindows();
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(ValidJson);
        using (var locked = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.LoadAsync());
            Assert.IsGreaterThan(0L, locked.Length);
        }

        Assert.AreEqual(new DemoLaunchOptions(true, ProtocolKind.Milky, 3), await fixture.Store.LoadAsync());
    }

    [TestMethod]
    public async Task DirectoryAtTheSettingsPathPropagatesAccessFailure()
    {
        RequireWindows();
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.SettingsPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.LoadAsync());

        Assert.IsTrue(Directory.Exists(fixture.SettingsPath));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("This verifies Windows file sharing and access behavior.");
    }

    private static void AssertOnlySettingsFile(SettingsFixture fixture) =>
        CollectionAssert.AreEqual(new[] { fixture.SettingsPath }, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));

    private sealed class SettingsFixture : IDisposable
    {
        internal SettingsFixture()
        {
            Directory.CreateDirectory(Root);
            Store = new DemoModeSettingsStore(SettingsPath);
        }

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"asuka-demo-settings-tests-{Guid.NewGuid():N}");
        internal string SettingsPath => Path.Combine(Root, "settings", "demo-mode.json");
        internal DemoModeSettingsStore Store { get; }

        internal async Task WriteAsync(string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(SettingsPath, contents, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
