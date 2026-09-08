using Asuka.App;

namespace Asuka.Tests.App;

[TestClass]
public sealed class MessageTextBlocksTests
{
    [TestMethod]
    public void PlainUnicodeWhitespaceAndLineEndingsRemainUnchanged()
    {
        const string text = "你好 👋\r\n第二行\n\t  spaced\r最後一行";
        var blocks = MessageTextBlocks.Parse(text);
        Assert.HasCount(1, blocks);
        Assert.IsFalse(blocks[0].IsCode);
        Assert.AreEqual(text, blocks[0].Text);
        Assert.IsNull(blocks[0].Language);
    }

    [TestMethod]
    public void FencedCodeKeepsItsLanguageIndentationUnicodeAndLineEndings()
    {
        const string text = "说明 👋\r\n```csharp\r\n    var 文本 = \"hello\";\r\n\treturn 文本;\r\n```\r\n完成\n";
        var blocks = MessageTextBlocks.Parse(text);
        Assert.HasCount(3, blocks);
        Assert.AreEqual(new MessageTextBlock("说明 👋\r\n"), blocks[0]);
        Assert.AreEqual(new MessageTextBlock("    var 文本 = \"hello\";\r\n\treturn 文本;\r\n", true, "csharp"), blocks[1]);
        Assert.AreEqual(new MessageTextBlock("完成\n"), blocks[2]);
    }

    [TestMethod]
    public void MultipleCodeBlocksAndEmptyCodeBlocksRemainSeparate()
    {
        var blocks = MessageTextBlocks.Parse("```\n```\nBetween\n```json\n{\"text\":\"``` is literal here\"}\n```\n");
        Assert.HasCount(3, blocks);
        Assert.AreEqual(new MessageTextBlock("", true), blocks[0]);
        Assert.AreEqual(new MessageTextBlock("Between\n"), blocks[1]);
        Assert.AreEqual(new MessageTextBlock("{\"text\":\"``` is literal here\"}\n", true, "json"), blocks[2]);
    }

    [TestMethod]
    [DataRow("```csharp\nunfinished\n")]
    [DataRow("Before\r\n```\r\nnot closed")]
    [DataRow("Inline ```code``` stays text")]
    [DataRow("````json\nnot a triple fence\n````")]
    [DataRow("    ```json\nindented plain text\n    ```")]
    public void UnclosedInlineAndUnsupportedFencesAreDisplayedLiterally(string text)
    {
        var blocks = MessageTextBlocks.Parse(text);
        Assert.HasCount(1, blocks);
        Assert.AreEqual(new MessageTextBlock(text), blocks[0]);
    }

    [TestMethod]
    public void ClosedBlockBeforeUnclosedFenceIsPreservedWithoutConsumingTheUnclosedText()
    {
        var blocks = MessageTextBlocks.Parse("```text\nok\n```\nthen\n```json\nunfinished");
        Assert.HasCount(2, blocks);
        Assert.AreEqual(new MessageTextBlock("ok\n", true, "text"), blocks[0]);
        Assert.AreEqual(new MessageTextBlock("then\n```json\nunfinished"), blocks[1]);
    }

    [TestMethod]
    public void UpToThreeSpacesAroundFenceDoNotChangeCodeIndentation()
    {
        var blocks = MessageTextBlocks.Parse("   ```  c++  \r  auto value = 1;\r  ``` \t\r");
        Assert.HasCount(1, blocks);
        Assert.AreEqual(new MessageTextBlock("  auto value = 1;\r", true, "c++"), blocks[0]);
    }

    [TestMethod]
    public void EmptyTextAndLongCodeAreNotTruncated()
    {
        Assert.AreEqual(new MessageTextBlock(""), MessageTextBlocks.Parse("")[0]);
        var code = new string('x', 32 * 1024) + "\n你好";
        var blocks = MessageTextBlocks.Parse("```text\n" + code + "\n```");
        Assert.HasCount(1, blocks);
        Assert.AreEqual(code + "\n", blocks[0].Text);
    }
}
