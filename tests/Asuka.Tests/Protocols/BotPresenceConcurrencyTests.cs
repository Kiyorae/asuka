using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class BotPresenceConcurrencyTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(11)]
    [DataRow(12)]
    public async Task WriteThatPassedItsEntryCheckCannotCommitAfterQueuedLogout(int version)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        IProtocolImplementation protocol = version == 1
            ? new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
            : new OneBotProtocol(version == 11 ? OneBotVersion.V11 : OneBotVersion.V12,
                ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        using var disposable = protocol as IDisposable;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        void HoldFirstChange(object? sender, StoreChangedEventArgs changes)
        {
            if (Interlocked.Exchange(ref observed, 1) != 0) return;
            entered.TrySetResult();
            release.Task.Wait(timeout.Token);
        }
        fixture.Store.Changed += HoldFirstChange;
        var blocker = Task.Run(() => fixture.Platform.SaveUserAsync(new User("Gate holder", "900"), timeout.Token));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            // The store observer holds SaveUserAsync's platform mutation gate.
            // Queue logout first, then enter the protocol while it still sees online.
            var logout = fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false,
                "Logout queued before this action", timeout.Token);
            var write = protocol.HandleAsync(RenameCall(version), timeout.Token);
            Assert.IsFalse(logout.IsCompleted);
            Assert.IsFalse(write.IsCompleted);
            Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
            release.TrySetResult();
            await blocker.WaitAsync(timeout.Token);
            Assert.IsTrue(await logout.WaitAsync(timeout.Token));
            var result = await write.WaitAsync(timeout.Token);
            Assert.IsFalse(result.IsSuccess, "The online check must run again after waiting for the mutation gate.");
            StringAssert.Contains(result.Message, "offline");
            Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, timeout.Token))!.Name);

            // The protocol scope has been disposed. Native simulator edits still
            // work after the failed protocol request.
            await fixture.Platform.SetGroupNameAsync(ProtocolTestFixture.GroupId,
                ProtocolTestFixture.SelfId, "Native edit", timeout.Token);
            Assert.AreEqual("Native edit", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, timeout.Token))!.Name);
        }
        finally
        {
            release.TrySetResult();
            fixture.Store.Changed -= HoldFirstChange;
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task ScheduledExecutionRechecksPresenceWithoutLeakingItsScopeToTheCaller()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId,
            fixture.Platform, fixture.Media)
        { RateLimitInterval = TimeSpan.Zero };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<OneBotScheduledActionFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ScheduledActionFailed += (_, failure) => failed.TrySetResult(failure);
        var observed = 0;
        void HoldFirstChange(object? sender, StoreChangedEventArgs changes)
        {
            if (Interlocked.Exchange(ref observed, 1) != 0) return;
            entered.TrySetResult();
            release.Task.Wait(timeout.Token);
        }
        fixture.Store.Changed += HoldFirstChange;
        var blocker = Task.Run(() => fixture.Platform.SaveUserAsync(new User("Gate holder", "900"), timeout.Token));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var logout = fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false,
                cancellationToken: timeout.Token);
            var reply = await protocol.HandleAsync(RenameCall(11) with { Name = "set_group_name_async" }, timeout.Token);
            Assert.AreEqual(1, reply.RetCode);
            Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
            release.TrySetResult();
            await blocker.WaitAsync(timeout.Token);
            await logout.WaitAsync(timeout.Token);
            Assert.AreEqual(1403, (await failed.Task.WaitAsync(timeout.Token)).RetCode);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(timeout.Token);
            Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, timeout.Token))!.Name);
            await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, true, cancellationToken: timeout.Token);
            var restored = await protocol.HandleAsync(RenameCall(11) with { Name = "set_group_name_async" }, timeout.Token);
            Assert.AreEqual(1, restored.RetCode);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(timeout.Token);
            Assert.AreEqual("Protocol rename", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, timeout.Token))!.Name);
        }
        finally
        {
            release.TrySetResult();
            fixture.Store.Changed -= HoldFirstChange;
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ProtocolCall RenameCall(int version) => new("set_group_name", new JsonObject
    {
        ["group_id"] = version == 12
            ? System.Text.Json.Nodes.JsonValue.Create(ProtocolTestFixture.GroupId)
            : System.Text.Json.Nodes.JsonValue.Create(500_000_001L),
        [version == 1 ? "new_group_name" : "group_name"] = "Protocol rename",
    });
}
