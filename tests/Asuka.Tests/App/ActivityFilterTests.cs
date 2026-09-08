using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Asuka.App;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.App;

[TestClass]
public sealed class ActivityFilterTests
{
    [TestMethod]
    public void InspectorFiltersProtocolAndDirectionWithoutIncludingApplicationEvents()
    {
        var request = Entry("get_login_info", "{}", TrafficDirection.InboundCall, "Milky");
        LogEntryItem[] entries = [request, Entry("reply", "{}", TrafficDirection.Reply, "Milky"),
            Entry("request", "{}", TrafficDirection.InboundCall, "OneBot V11"),
            new(DateTimeOffset.UtcNow, "Protocol", "Session started", "{}")];
        CollectionAssert.AreEqual(new[] { request }, ActivityFilter.ProtocolEntries(entries, "", TrafficDirection.InboundCall, "Milky").ToArray());
        Assert.HasCount(4, entries);
    }

    [TestMethod]
    public void SearchCombinesTermsAcrossActionAndMessagePayloadIgnoringCase()
    {
        var message = Entry("message_receive", "{\"sender_id\":42,\"text\":\"Hello Asuka\"}", TrafficDirection.OutboundEvent, "Milky");
        LogEntryItem[] entries = [message, Entry("message_receive", "{\"sender_id\":13,\"text\":\"Hello\"}", TrafficDirection.OutboundEvent, "Milky")];
        CollectionAssert.AreEqual(new[] { message }, ActivityFilter.ProtocolEntries(entries, "  MESSAGE  hello\t42  ").ToArray());
        Assert.IsEmpty(ActivityFilter.ProtocolEntries(entries, "hello absent").ToArray());
    }

    [TestMethod]
    public void SearchIncludesTheDisplayedTypeAndLocalTimestamp()
    {
        var entry = Entry("get_login_info", "{}", TrafficDirection.Reply, "Milky");
        CollectionAssert.AreEqual(new[] { entry }, ActivityFilter.ProtocolEntries([entry], $"response {entry.TimeText}").ToArray());
    }

    [TestMethod]
    public void SearchCanFindOlderHistoryBeyondTheFormerThousandEntryLimit()
    {
        var oldest = Entry("old request", "{\"message\":\"keep this payload\"}", TrafficDirection.InboundCall, "OneBot V12");
        var entries = new List<LogEntryItem> { oldest };
        entries.AddRange(Enumerable.Range(0, 1500).Select(index => Entry($"event {index}", "{}", TrafficDirection.OutboundEvent, "Milky")));
        CollectionAssert.AreEqual(new[] { oldest }, ActivityFilter.ProtocolEntries(entries, "keep payload").ToArray());
        Assert.HasCount(1501, ActivityFilter.ProtocolEntries(entries, "").ToArray());
    }

    [TestMethod]
    public void ErrorsOnlyDistinguishesFailedRepliesFromSuccessfulAndNormalPayloads()
    {
        var failed = new TrafficEntry(TrafficDirection.Reply, "failed action", JsonNode.Parse("{\"retcode\":1400,\"status\":\"failed\"}")!);
        var success = new TrafficEntry(TrafficDirection.Reply, "success", JsonNode.Parse("{\"retcode\":0,\"data\":{\"text\":\"failed\"}}")!);
        var malformed = new TrafficEntry(TrafficDirection.InboundCall, "Unparseable frame", JsonValue.Create("bad json")!);
        Assert.IsTrue(ActivityFilter.IsFailure(failed));
        Assert.IsFalse(ActivityFilter.IsFailure(success));
        Assert.IsTrue(ActivityFilter.IsFailure(malformed));
        var failedEntry = new LogEntryItem(failed.Timestamp, "Protocol", failed.Summary, failed.Payload.ToJsonString(), failed.Direction, "Milky", true);
        LogEntryItem[] entries = [failedEntry, Entry("ok", "{}", TrafficDirection.Reply, "Milky")];
        CollectionAssert.AreEqual(new[] { failedEntry }, ActivityFilter.ProtocolEntries(entries, "", errorsOnly: true).ToArray());
    }

    [TestMethod]
    public void NonObjectTrafficCannotCrashErrorClassification()
    {
        Assert.IsFalse(ActivityFilter.IsFailure(new TrafficEntry(TrafficDirection.Reply, "reply", JsonValue.Create("text")!)));
        Assert.IsFalse(ActivityFilter.IsFailure(new TrafficEntry(TrafficDirection.OutboundEvent, "event", new JsonObject { ["retcode"] = 5 })));
        Assert.IsTrue(ActivityFilter.IsFailure(new TrafficEntry(TrafficDirection.Reply, "reply", new JsonObject { ["retcode"] = "-1" })));
        Assert.IsFalse(ActivityFilter.IsFailure(new TrafficEntry(TrafficDirection.Reply, "queued", new JsonObject { ["status"] = "async", ["retcode"] = 1, ["data"] = null })));
        Assert.IsTrue(ActivityFilter.IsFailure(new TrafficEntry(TrafficDirection.Reply, "failed", new JsonObject { ["status"] = "failed", ["retcode"] = 1 })));
    }

    [TestMethod]
    public void IncomingTrafficDoesNotResetExistingRows()
    {
        var selected = Entry("selected", "{}", TrafficDirection.Reply, "Milky");
        var older = Entry("older", "{}", TrafficDirection.OutboundEvent, "Milky");
        var incoming = Entry("incoming", "{}", TrafficDirection.InboundCall, "Milky");
        var visible = new ObservableCollection<LogEntryItem> { selected, older };
        var changes = new List<NotifyCollectionChangedAction>();
        visible.CollectionChanged += (_, args) => changes.Add(args.Action);
        CollectionSync.Apply(visible, [incoming, selected, older]);
        CollectionAssert.AreEqual(new[] { incoming, selected, older }, visible.ToArray());
        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Add }, changes.ToArray());
        CollectionSync.Apply(visible, [incoming, selected, older]);
        Assert.HasCount(1, changes);
    }

    [TestMethod]
    public void FilterChangesRemoveOnlyExcludedRowsAndPreserveRetainedObjects()
    {
        var first = Entry("first", "{}", TrafficDirection.InboundCall, "Milky");
        var second = Entry("second", "{}", TrafficDirection.OutboundEvent, "Milky");
        var third = Entry("third", "{}", TrafficDirection.Reply, "Milky");
        var visible = new ObservableCollection<LogEntryItem> { first, second, third };
        CollectionSync.Apply(visible, [third, first]);
        Assert.AreSame(third, visible[0]);
        Assert.AreSame(first, visible[1]);
        Assert.HasCount(2, visible);
    }

    // Timestamp text is searchable; fixed time avoids accidentally matching payload queries.
    private static LogEntryItem Entry(string summary, string detail, TrafficDirection direction, string protocol) =>
        new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), "Protocol", summary, detail, direction, protocol);
}
