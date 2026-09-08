using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AccountCredentialsTests
{
    [TestMethod]
    public async Task SnapshotsAreImmutableAndAccountScopedWithAtomicReplacementAndClear()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await platform.SaveUserAsync(new User("Alice", id: "100"));
        await platform.SaveUserAsync(new User("Bob", id: "200"));
        var input = new Dictionary<string, string> { ["QUN.QQ.COM."] = "session=alice", [""] = "session=default" };
        var snapshot = new AccountCredentials(input, "000123");
        await platform.SetAccountCredentialsAsync("100", snapshot);
        input["QUN.QQ.COM."] = "session=changed-outside-platform";
        await platform.SetAccountCredentialsAsync("200", new(new Dictionary<string, string> { ["qun.qq.com"] = "session=bob" }, "456"));

        var alice = await platform.GetAccountCredentialsAsync("100");
        Assert.AreEqual("session=alice", alice.Cookies["qun.qq.com"]);
        Assert.AreEqual("session=default", alice.Cookies[""]);
        Assert.AreEqual("000123", alice.CsrfToken);
        Assert.AreEqual("session=bob", (await platform.GetAccountCredentialsAsync("200")).Cookies["qun.qq.com"]);
        Assert.DoesNotContain("session=alice", alice.ToString());
        Assert.DoesNotContain("000123", alice.ToString());

        await platform.SetAccountCredentialsAsync("100", new(new Dictionary<string, string> { ["qzone.qq.com"] = "session=new" }));
        var replaced = await platform.GetAccountCredentialsAsync("100");
        Assert.HasCount(1, replaced.Cookies);
        Assert.IsNull(replaced.CsrfToken);
        Assert.AreEqual("session=new", replaced.Cookies["qzone.qq.com"]);
        Assert.AreEqual("session=alice", alice.Cookies["qun.qq.com"], "Already returned snapshots retain their original values.");
        await platform.ClearAccountCredentialsAsync("100");
        Assert.IsEmpty((await platform.GetAccountCredentialsAsync("100")).Cookies);
        Assert.AreEqual("456", (await platform.GetAccountCredentialsAsync("200")).CsrfToken);
    }

    [TestMethod]
    public async Task CredentialsNeverPersistToTheSharedStoreAndDeletedAccountsCannotRecoverThem()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await platform.SaveUserAsync(new User("Alice", id: "100"));
        await platform.SetAccountCredentialsAsync("100", new(new Dictionary<string, string> { [""] = "session=ephemeral" }, "123"));
        await using (var separateRuntime = new PlatformService(store))
        {
            var empty = await separateRuntime.GetAccountCredentialsAsync("100");
            Assert.IsEmpty(empty.Cookies);
            Assert.IsNull(empty.CsrfToken);
        }

        await platform.DeleteUserAsync("100");
        await platform.SaveUserAsync(new User("Recreated", id: "100"));
        var recreated = await platform.GetAccountCredentialsAsync("100");
        Assert.IsEmpty(recreated.Cookies);
        Assert.IsNull(recreated.CsrfToken);
        await platform.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => platform.GetAccountCredentialsAsync("100"));
    }

    [TestMethod]
    public async Task MissingAccountsAndCancellationCannotWriteCredentialState()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        var credentials = new AccountCredentials(new Dictionary<string, string> { [""] = "session=kept" }, "123");
        var unknown = await Assert.ThrowsAsync<PlatformException>(() => platform.SetAccountCredentialsAsync("100", credentials));
        Assert.AreEqual(PlatformError.UserNotFound, unknown.Error);
        await platform.SaveUserAsync(new User("Alice", id: "100"));
        await platform.SetAccountCredentialsAsync("100", credentials);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.SetAccountCredentialsAsync("100", AccountCredentials.Empty, cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.ClearAccountCredentialsAsync("100", cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.GetAccountCredentialsAsync("100", cancelled.Token));
        Assert.AreEqual("123", (await platform.GetAccountCredentialsAsync("100")).CsrfToken);
    }

    [TestMethod]
    [DataRow("https://qun.qq.com/path?secret=value")]
    [DataRow(".qq.com")]
    [DataRow("*.qq.com")]
    [DataRow("qun.qq.com:443")]
    [DataRow("qun.qq.com/secret")]
    [DataRow("qun.qq.com\r\nsecret")]
    [DataRow(" qun.qq.com")]
    [DataRow("qun..qq.com")]
    [DataRow("-qun.qq.com")]
    public void CookieScopeRejectsUrlsWildcardsAndHeaderInjectionWithoutEchoingInput(string domain)
    {
        var error = Assert.Throws<ArgumentException>(() => AccountCredentials.NormalizeDomain(domain));
        Assert.DoesNotContain("secret", error.Message);
    }

    [TestMethod]
    public void CredentialValidationIsBoundedAndDoesNotEchoSecretValues()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new AccountCredentials(new Dictionary<string, string> { ["qun.qq.com"] = "hidden-secret\r\nInjected: value" }));
        Assert.DoesNotContain("hidden-secret", error.Message);
        Assert.Throws<ArgumentException>(() => new AccountCredentials(new Dictionary<string, string>
        {
            ["qun.qq.com"] = "session=first",
            ["QUN.QQ.COM."] = "session=second",
        }));
        Assert.Throws<ArgumentException>(() => new AccountCredentials(new Dictionary<string, string>
        {
            [""] = new string('x', AccountCredentials.MaximumCookieLength + 1),
        }));
        Assert.Throws<ArgumentException>(() => new AccountCredentials(Enumerable.Range(0, AccountCredentials.MaximumCookieDomains + 1)
            .ToDictionary(index => $"scope{index}.qq.com", _ => "session=value")));
        Assert.Throws<ArgumentException>(() => new AccountCredentials(new Dictionary<string, string>(),
            new string('x', AccountCredentials.MaximumCsrfTokenLength + 1)));
        Assert.Throws<ArgumentException>(() => new AccountCredentials(new Dictionary<string, string>(), "hidden-secret\nvalue"));
    }
}
