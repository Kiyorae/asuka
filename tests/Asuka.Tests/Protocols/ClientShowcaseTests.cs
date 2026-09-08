using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ClientShowcaseTests
{
    private static readonly string[] ExpectedUserIds = ["91001", "91002", "91003"];

    [TestMethod]
    public async Task SeedingUsesFixedIdentitiesTwoChatsAndIsIdempotent()
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        await showcase.EnsureSeededAsync();
        var before = await fixture.Store.GetAllUsersAsync();
        var groups = await fixture.Store.GetAllGroupsAsync();
        Assert.HasCount(3, before);
        Assert.HasCount(1, groups);
        CollectionAssert.AreEquivalent(ExpectedUserIds, before.Select(user => user.Id).ToArray());
        Assert.AreEqual("92001", groups[0].Id);
        Assert.HasCount(3, await fixture.Store.GetMembersAsync(ClientShowcase.GroupId));
        Assert.IsNotNull(await fixture.Store.GetFriendshipAsync(ClientShowcase.AliceId, ClientShowcase.BotId));
        Assert.IsNotNull(await fixture.Store.GetFriendshipAsync(ClientShowcase.BotId, ClientShowcase.AliceId));
        var changes = 0;
        fixture.Store.Changed += (_, _) => changes++;
        await showcase.EnsureSeededAsync();
        Assert.AreEqual(0, changes, "Repeated seeding must not rewrite entities or publish duplicate membership events.");
        CollectionAssert.AreEqual(before.ToArray(), (await fixture.Store.GetAllUsersAsync()).ToArray());
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.GroupChat));
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.PrivateChat));
        Assert.AreEqual(new Chat(ChatScene.Group, "92001", "91002"), ClientShowcase.GroupChat);
        Assert.AreEqual(new Chat(ChatScene.Friend, "91001", "91002"), ClientShowcase.PrivateChat);
        var manual = await fixture.Platform.SendMessageAsync(ChatScene.Group, ClientShowcase.GroupId, ClientShowcase.AliceId,
            ClientShowcase.BotId, [new TextSegment("A manual message")]);
        await showcase.EnsureSeededAsync();
        Assert.IsNotNull(await fixture.Store.GetMessageAsync(manual.Id));
    }

    [TestMethod]
    [DataRow(ProtocolKind.OneBotV11)]
    [DataRow(ProtocolKind.OneBotV12)]
    [DataRow(ProtocolKind.Milky)]
    public async Task AFullCycleAlternatesChatsAndOnlyUsesSupportedRichContent(ProtocolKind protocol)
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        await showcase.EnsureSeededAsync();
        var steps = new List<ShowcaseStep>();
        for (var index = 0; index < ClientShowcase.StepsPerCycle; index++)
        {
            var step = await showcase.StepAsync(protocol);
            steps.Add(step);
            Assert.AreEqual((long)index, step.Index);
            Assert.AreEqual(index % 2 == 0 ? ClientShowcase.GroupChat : ClientShowcase.PrivateChat, step.Chat);
            Assert.IsFalse(step.HistoryCleared);
        }
        var messages = steps.Where(step => step.Kind == ShowcaseStepKind.Message).Select(step => step.Message!).ToArray();
        Assert.AreEqual("logo.png", steps[2].Message!.Content.OfType<ImageSegment>().Single().Asset.Name);
        Assert.AreEqual("rotation.gif", steps[4].Message!.Content.OfType<ImageSegment>().Single().Asset.Name);
        var content = messages.SelectMany(message => message.Content).ToArray();
        Assert.IsTrue(content.All(ProtocolCapabilities.For(protocol).SupportsSegment));
        Assert.IsTrue(messages.Any(message => message.SenderId == ClientShowcase.BotId));
        Assert.IsTrue(messages.Any(message => message.SenderId == ClientShowcase.AliceId));
        Assert.IsTrue(messages.Any(message => message.SenderId == ClientShowcase.BobId));
        Assert.IsTrue(messages.Any(message => message.Direction == MessageDirection.Incoming));
        Assert.IsTrue(messages.Any(message => message.Direction == MessageDirection.Outgoing));
        foreach (var expected in new[] { typeof(TextSegment), typeof(ImageSegment), typeof(RecordSegment), typeof(VideoSegment), typeof(MentionSegment), typeof(ReplySegment) })
            Assert.IsTrue(content.Any(segment => segment.GetType() == expected), expected.Name);
        Assert.IsTrue(content.OfType<ImageSegment>().Any(image => image.Asset.Name.EndsWith(".png", StringComparison.Ordinal)));
        Assert.IsTrue(content.OfType<ImageSegment>().Any(image => image.Asset.Name.EndsWith(".gif", StringComparison.Ordinal)));
        Assert.IsTrue(content.OfType<RecordSegment>().Any(record => record.Asset.Name.EndsWith(".silk", StringComparison.Ordinal)));
        Assert.IsTrue(content.OfType<RecordSegment>().Any(record => record.Asset.Name.EndsWith(".wav", StringComparison.Ordinal)));
        Assert.IsTrue(content.OfType<TextSegment>().Any(text => text.Text.Contains('\n')));
        Assert.IsTrue(content.OfType<TextSegment>().Any(text => text.Text.Contains("```", StringComparison.Ordinal)));
        Assert.IsTrue(content.OfType<TextSegment>().Any(text => text.Text.Length > 300));
        Assert.AreEqual(protocol == ProtocolKind.OneBotV12, content.OfType<FileSegment>().Any());
        Assert.AreEqual(protocol == ProtocolKind.OneBotV12, content.OfType<AudioSegment>().Any());
        Assert.AreEqual(protocol != ProtocolKind.Milky, content.OfType<LocationSegment>().Any());
        Assert.AreEqual(protocol != ProtocolKind.OneBotV12, content.OfType<FaceSegment>().Any());
        Assert.AreEqual(protocol != ProtocolKind.OneBotV12, content.OfType<ForwardSegment>().Any());
        Assert.IsTrue(messages.Where(message => message.Scene == ChatScene.Friend).All(message => !message.Content.OfType<MentionSegment>().Any()));
        Assert.AreEqual(protocol == ProtocolKind.Milky, steps.Any(step => step.Kind == ShowcaseStepKind.ReactionAdded));
        Assert.AreEqual(protocol == ProtocolKind.Milky, steps.Any(step => step.Kind == ShowcaseStepKind.ReactionRemoved));
        Assert.AreEqual(2, steps.Count(step => step.Kind == ShowcaseStepKind.Recall));
    }

    [TestMethod]
    public async Task ReplyAndRecallTargetAnActiveMessageInTheSameConversation()
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        await showcase.EnsureSeededAsync();
        for (var index = 0; index < ClientShowcase.StepsPerCycle; index++)
        {
            var step = await showcase.StepAsync(ProtocolKind.Milky);
            if (step.Kind == ShowcaseStepKind.Message)
            {
                foreach (var reply in step.Message!.Content.OfType<ReplySegment>())
                {
                    var target = await fixture.Store.GetMessageAsync(reply.MessageId);
                    Assert.IsNotNull(target);
                    Assert.IsFalse(target.IsRecalled);
                    Assert.AreEqual(step.Chat, target.Chat);
                }
            }
            if (step.Kind == ShowcaseStepKind.Recall)
            {
                var recalled = await fixture.Store.GetMessageAsync(step.Message!.Id);
                Assert.IsTrue(recalled!.IsRecalled);
                Assert.AreEqual(recalled.SenderId, recalled.RecalledBy);
            }
            if (step.Kind == ShowcaseStepKind.ReactionAdded)
                Assert.IsGreaterThan(0, (await fixture.Store.GetMessageReactionsAsync(step.Message!.Id)).Count);
            if (step.Kind == ShowcaseStepKind.ReactionRemoved)
                Assert.IsFalse((await fixture.Store.GetMessageReactionsAsync(step.Message!.Id)).Any(reaction => reaction.Reaction == "76"));
        }
    }

    [TestMethod]
    public async Task HistoryIsBoundedAndOtherChatsAreNeverCleared()
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        await showcase.EnsureSeededAsync();
        var otherGroup = new Group("Unrelated", id: "93001");
        await fixture.Platform.CreateGroupAsync(otherGroup, ClientShowcase.AliceId);
        await fixture.Platform.AddMemberAsync(otherGroup.Id, ClientShowcase.BotId);
        var unrelated = await fixture.Platform.SendMessageAsync(ChatScene.Group, otherGroup.Id, ClientShowcase.AliceId,
            ClientShowcase.BotId, [new TextSegment("Keep this history")]);
        var cleared = new HashSet<Chat>();
        for (var index = 0; index < 330; index++)
        {
            var step = await showcase.StepAsync(ProtocolKind.Milky);
            if (step.HistoryCleared) cleared.Add(step.Chat);
            Assert.IsLessThanOrEqualTo(ClientShowcase.HistoryLimit,
                (await fixture.Store.GetMessagesAsync(step.Chat, ClientShowcase.HistoryLimit + 1)).Count);
        }
        Assert.HasCount(2, cleared);
        Assert.IsNotNull(await fixture.Store.GetMessageAsync(unrelated.Id));
        await showcase.ResetAsync();
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.GroupChat));
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.PrivateChat));
        Assert.IsNotNull(await fixture.Store.GetMessageAsync(unrelated.Id));
        Assert.AreEqual(0L, (await showcase.StepAsync(ProtocolKind.Milky)).Index);
    }

    [TestMethod]
    public async Task CancellationDoesNotSeedSendOrAdvanceTheNextStep()
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => showcase.EnsureSeededAsync(cancelled.Token));
        Assert.HasCount(0, await fixture.Store.GetAllUsersAsync());
        await showcase.EnsureSeededAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => showcase.StepAsync(ProtocolKind.Milky, cancelled.Token));
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.GroupChat));
        var first = await showcase.StepAsync(ProtocolKind.Milky);
        Assert.AreEqual(0L, first.Index);
        Assert.AreEqual(ClientShowcase.GroupChat, first.Chat);
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(ClientShowcase.GroupChat));
        Assert.HasCount(0, await fixture.Store.GetMessagesAsync(ClientShowcase.PrivateChat));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => showcase.ResetAsync(cancelled.Token));
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(ClientShowcase.GroupChat));
        Assert.AreEqual(1L, (await showcase.StepAsync(ProtocolKind.Milky)).Index);
    }

    [TestMethod]
    public async Task ProtocolChangesDuringTheCycleStillUseOnlyAvailableSegments()
    {
        await using var fixture = new ProtocolTestFixture();
        var showcase = Create(fixture);
        await showcase.EnsureSeededAsync();
        for (var index = 0; index < ClientShowcase.StepsPerCycle * 2; index++)
        {
            var protocol = (index % 3) switch { 0 => ProtocolKind.OneBotV11, 1 => ProtocolKind.OneBotV12, _ => ProtocolKind.Milky };
            var step = await showcase.StepAsync(protocol);
            if (step.Kind == ShowcaseStepKind.Message)
                Assert.IsTrue(Flatten(step.Message!.Content).All(ProtocolCapabilities.For(protocol).SupportsSegment));
        }
    }

    private static IEnumerable<MessageSegment> Flatten(IEnumerable<MessageSegment> content)
    {
        foreach (var segment in content)
        {
            yield return segment;
            if (segment is ForwardSegment forward)
                foreach (var child in Flatten(forward.Nodes.SelectMany(node => node.Content))) yield return child;
        }
    }

    private static ClientShowcase Create(ProtocolTestFixture fixture) => new(fixture.Platform, new ShowcaseAssets(
        new Asset("logo", "logo.png", "image/png"), new Asset("rotation", "rotation.gif", "image/gif"),
        new Asset("voice", "voice.silk", "audio/silk"), new Asset("video", "video.mp4", "video/mp4"),
        new Asset("notes", "notes.txt", "text/plain"), new Asset("wave", "voice.wav", "audio/wav")));
}
