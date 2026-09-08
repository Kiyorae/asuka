using System.Text.Json;
using Asuka.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class RichSegmentPersistenceTests
{
    [TestMethod]
    public async Task ProtocolMetadataSurvivesMessageStorage()
    {
        using var store = new AsukaStore();
        var asset = new Asset("asset", "media.bin");
        MessageSegment[] content =
        [
            new FaceSegment("14", "Smile", true),
            new ImageSegment(asset, "sticker", "Preview"),
            new AudioSegment(asset),
            new LocationSegment(31.2, 121.5, "Shanghai", "Address"),
            new ReplySegment("42", "10001"),
            new VideoSegment(asset, new Asset("thumb", "thumb.png")),
            new ForwardSegment("forward", [new ForwardNode("2", "Sender", [
                new ForwardSegment("nested", [new ForwardNode("3", "Other", [new TextSegment("Nested reply")])])])],
                "Title", "Summary", ["First", "Second"], "Prompt"),
        ];
        var message = new Message(ChatScene.Friend, "2", "1", "1", content, MessageDirection.Outgoing);
        await store.AppendMessageAsync(message);
        var read = await store.GetMessageAsync(message.Id);
        Assert.IsNotNull(read);
        Assert.AreEqual(JsonSerializer.Serialize(content), JsonSerializer.Serialize(read.Content));
    }

    [TestMethod]
    public void ExistingStoredSegmentsKeepTheirDefaults()
    {
        var content = JsonSerializer.Deserialize<MessageSegment[]>("""
            [{"type":"face","id":"14"},{"type":"image","asset":{"Id":"1","Name":"old.png"}},
             {"type":"reply","message_id":"42"},{"type":"forward","id":"f","nodes":[]}]
            """);
        Assert.IsNotNull(content);
        Assert.IsFalse(((FaceSegment)content[0]).IsLarge);
        Assert.AreEqual("normal", ((ImageSegment)content[1]).SubType);
        Assert.IsNull(((ReplySegment)content[2]).UserId);
        Assert.IsNull(((ForwardSegment)content[3]).Preview);
    }
}
