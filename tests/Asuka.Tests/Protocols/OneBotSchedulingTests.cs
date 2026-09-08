using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotSchedulingTests
{
    private static readonly string[] OnlyRunning = ["running"];
    private static readonly string[] BothRunning = ["ordinary-running", "limited-running"];
    // Official V11 api/README.md and communication/http.md: both suffixes
    // acknowledge async/1/null; rate_limit_interval defaults to 500 ms.
    [TestMethod]
    [DataRow("_async")]
    [DataRow("_rate_limited")]
    public async Task V11AcknowledgesScheduledWorkWithoutReturningItsFinalResult(string suffix)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var echo = new JsonObject { ["request"] = 17 };
        var reply = await protocol.HandleAsync(new ProtocolCall("set_group_name" + suffix, new JsonObject
        {
            ["group_id"] = 500_000_001L,
            ["group_name"] = "Scheduled name",
        }, echo));
        Assert.AreEqual(1, reply.RetCode);
        Assert.IsNull(reply.Data);
        var envelope = protocol.CreateEnvelope(reply, echo);
        Assert.AreEqual("async", envelope["status"]!.GetValue<string>());
        Assert.AreEqual(1, envelope["retcode"]!.GetValue<int>());
        Assert.IsNull(envelope["data"]);
        Assert.IsTrue(JsonNode.DeepEquals(echo, envelope["echo"]));
        await protocol.WaitForScheduledActionsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Scheduled name", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), protocol.RateLimitInterval);
    }

    [TestMethod]
    [DataRow("_async")]
    [DataRow("_rate_limited")]
    public async Task UnsupportedActionsAndV12SuffixesAreRejectedBeforeQueueing(string suffix)
    {
        await using var fixture = new ProtocolTestFixture();
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        using var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var unknown = await v11.HandleAsync(new ProtocolCall("missing_action" + suffix, new JsonObject()));
        Assert.AreEqual(1404, unknown.RetCode);
        Assert.AreEqual("failed", v11.CreateEnvelope(unknown)["status"]!.GetValue<string>());
        var nonstandard = await v12.HandleAsync(new ProtocolCall("get_status" + suffix, new JsonObject()));
        Assert.AreEqual(10002, nonstandard.RetCode);
        Assert.AreEqual("failed", v12.CreateEnvelope(nonstandard)["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AcknowledgedFailureIsObservableOnlyThroughInternalDiagnostics()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var failure = new TaskCompletionSource<OneBotScheduledActionFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.ScheduledActionFailed += (_, failed) => failure.TrySetResult(failed);
        var reply = await protocol.HandleAsync(new ProtocolCall("set_group_name_async", new JsonObject
        {
            ["group_id"] = 500_000_001L,
            ["group_name"] = "Missing group",
        }));
        Assert.AreEqual(1, reply.RetCode);
        var recorded = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("set_group_name", recorded.Action);
        Assert.AreNotEqual(0, recorded.RetCode);
        Assert.AreEqual("async", protocol.CreateEnvelope(reply)["status"]!.GetValue<string>());
        Assert.IsNull(protocol.CreateEnvelope(reply)["data"]);
    }

    [TestMethod]
    public async Task QueueAcknowledgesBeforeExecutionCompletesAndCopiesNestedParametersAndEcho()
    {
        var entered = Signal();
        var release = Signal();
        ProtocolCall? executed = null;
        using var scheduler = new OneBotActionScheduler(async (call, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            executed = call;
            return ProtocolReply.Success(new JsonObject { ["final_result"] = 123 });
        }, _ => { }, TimeSpan.Zero);
        var request = new ProtocolCall("operation", new JsonObject { ["nested"] = new JsonObject { ["text"] = "original" } },
            new JsonObject { ["echo"] = "original" });
        var reply = scheduler.TrySchedule(request, false);
        Assert.AreEqual(1, reply.RetCode);
        Assert.IsNull(reply.Data);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Parameters["nested"]!["text"] = "changed";
        request.Echo!["echo"] = "changed";
        Assert.IsNull(executed);
        release.TrySetResult();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("original", executed!.Parameters["nested"]!["text"]!.GetValue<string>());
        Assert.AreEqual("original", executed.Echo!["echo"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RateLimitedWorkIsSerialWithConfiguredIntervalsAndDoesNotBlockOrdinaryAsyncWork()
    {
        var executions = Channel.CreateUnbounded<string>();
        var intervals = Channel.CreateUnbounded<(TimeSpan Interval, TaskCompletionSource Release)>();
        var selectedInterval = TimeSpan.FromMilliseconds(137);
        using var scheduler = new OneBotActionScheduler((call, _) =>
        {
            executions.Writer.TryWrite(call.Name);
            return Task.FromResult(ProtocolReply.Success());
        }, _ => { }, selectedInterval, delay: async (interval, token) =>
        {
            var release = Signal();
            intervals.Writer.TryWrite((interval, release));
            await release.Task.WaitAsync(token);
        });
        Assert.AreEqual(1, scheduler.TrySchedule(Call("first"), true).RetCode);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("second"), true).RetCode);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("third"), true).RetCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.AreEqual("first", await executions.Reader.ReadAsync(timeout.Token));
        var firstWait = await intervals.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(selectedInterval, firstWait.Interval);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("ordinary"), false).RetCode);
        Assert.AreEqual("ordinary", await executions.Reader.ReadAsync(timeout.Token));
        Assert.IsFalse(executions.Reader.TryRead(out _));
        firstWait.Release.TrySetResult();
        Assert.AreEqual("second", await executions.Reader.ReadAsync(timeout.Token));
        var secondWait = await intervals.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(selectedInterval, secondWait.Interval);
        Assert.IsFalse(executions.Reader.TryRead(out _));
        secondWait.Release.TrySetResult();
        Assert.AreEqual("third", await executions.Reader.ReadAsync(timeout.Token));
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task QueueCapacityAndPayloadBudgetRejectWorkWithoutInventingAnAcknowledgement()
    {
        var entered = Signal();
        using var scheduler = new OneBotActionScheduler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ProtocolReply.Success();
        }, _ => { }, TimeSpan.Zero, capacity: 2, maximumQueuedBytes: 2048);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("running"), false).RetCode);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, scheduler.TrySchedule(Call("queued"), false).RetCode);
        Assert.AreEqual(1000, scheduler.TrySchedule(Call("overflow"), false).RetCode);
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var bytesLimited = new OneBotActionScheduler((_, _) => Task.FromResult(ProtocolReply.Success()), _ => { },
            TimeSpan.Zero, maximumQueuedBytes: 1024);
        Assert.AreEqual(1000, bytesLimited.TrySchedule(new ProtocolCall("large", new JsonObject
        {
            ["message"] = new string('x', 1024),
        }), false).RetCode);
        await bytesLimited.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CancellationSkipsQueuedWorkAndStopDrainsBothQueues()
    {
        var started = Signal();
        var release = Signal();
        var executed = new ConcurrentQueue<string>();
        using var scheduler = new OneBotActionScheduler(async (call, token) =>
        {
            executed.Enqueue(call.Name);
            if (call.Name == "running")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return ProtocolReply.Success();
        }, _ => { }, TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, scheduler.TrySchedule(Call("running"), false).RetCode);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        Assert.AreEqual(1, scheduler.TrySchedule(Call("cancelled"), false, cancelled.Token).RetCode);
        await cancelled.CancelAsync();
        release.TrySetResult();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(OnlyRunning, executed.ToArray());
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1000, scheduler.TrySchedule(Call("stopped"), false).RetCode);
    }

    [TestMethod]
    public async Task StopCancelsBothRunningWorkersAndDropsEveryQueuedOldCall()
    {
        var bothStarted = Signal();
        var executions = new ConcurrentQueue<string>();
        var count = 0;
        using var scheduler = new OneBotActionScheduler(async (call, token) =>
        {
            executions.Enqueue(call.Name);
            if (Interlocked.Increment(ref count) == 2) bothStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ProtocolReply.Success();
        }, _ => { }, TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, scheduler.TrySchedule(Call("ordinary-running"), false).RetCode);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("limited-running"), true).RetCode);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, scheduler.TrySchedule(Call("ordinary-old"), false).RetCode);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("limited-old"), true).RetCode);
        var stopped = scheduler.StopAsync();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(stopped, scheduler.StopAsync());
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEquivalent(BothRunning, executions.ToArray());
        Assert.AreEqual(1000, scheduler.TrySchedule(Call("after-stop"), false).RetCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedActionsAndThrowingObserversDoNotAbandonFollowingWork(bool throwFromAction)
    {
        var failures = new ConcurrentQueue<OneBotScheduledActionFailure>();
        var following = Signal();
        using var scheduler = new OneBotActionScheduler((call, _) =>
        {
            if (call.Name == "failed")
            {
                if (throwFromAction) throw new IOException("Deliberate test failure");
                return Task.FromResult(new ProtocolReply(1404));
            }

            following.TrySetResult();
            return Task.FromResult(ProtocolReply.Success());
        }, failure =>
        {
            failures.Enqueue(failure);
            throw new InvalidOperationException("A diagnostic subscriber failed");
        }, TimeSpan.Zero);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("failed"), false).RetCode);
        Assert.AreEqual(1, scheduler.TrySchedule(Call("following"), false).RetCode);
        await following.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, failures);
        Assert.AreEqual("failed", failures.Single().Action);
        Assert.AreEqual(throwFromAction ? 1000 : 1404, failures.Single().RetCode);
    }

    [TestMethod]
    public async Task ProtocolStopRejectsOldQueueAndResumeAcceptsANewGeneration()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await protocol.StopScheduledActionsAsync();
        var stopped = await protocol.HandleAsync(new ProtocolCall("get_status_async", new JsonObject()));
        Assert.AreEqual(1000, stopped.RetCode);
        protocol.ResumeScheduledActions();
        Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("get_status_async", new JsonObject()))).RetCode);
        await protocol.WaitForScheduledActionsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        protocol.Dispose();
        Assert.Throws<ObjectDisposedException>(() => protocol.ResumeScheduledActions());
    }

    [TestMethod]
    public async Task PrecancelledRequestsNeverEnterTheQueue()
    {
        var executions = 0;
        using var scheduler = new OneBotActionScheduler((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(ProtocolReply.Success());
        }, _ => { }, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => scheduler.TrySchedule(Call("cancelled"), false, cancellation.Token));
        Assert.AreEqual(0, executions);
    }

    private static ProtocolCall Call(string name) => new(name, new JsonObject());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
