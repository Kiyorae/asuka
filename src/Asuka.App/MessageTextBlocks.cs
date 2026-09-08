namespace Asuka.App;

internal sealed record MessageTextBlock(string Text, bool IsCode = false, string? Language = null);

/// <summary>Recognizes only complete, line-delimited triple-backtick code fences.</summary>
internal static class MessageTextBlocks
{
    internal static IReadOnlyList<MessageTextBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = new List<MessageTextBlock>();
        var plainStart = 0;
        var position = 0;
        while (position < text.Length)
        {
            var opening = ReadLine(text, position);
            if (!TryOpeningFence(text.AsSpan(position, opening.ContentEnd - position), out var language))
            {
                position = opening.End;
                continue;
            }

            var closingStart = opening.End;
            var closingEnd = -1;
            while (closingStart < text.Length)
            {
                var closing = ReadLine(text, closingStart);
                if (IsClosingFence(text.AsSpan(closingStart, closing.ContentEnd - closingStart)))
                {
                    closingEnd = closing.End;
                    break;
                }
                closingStart = closing.End;
            }
            if (closingEnd < 0) break;
            if (position > plainStart) blocks.Add(new MessageTextBlock(text[plainStart..position]));
            blocks.Add(new MessageTextBlock(text[opening.End..closingStart], true, language));
            position = plainStart = closingEnd;
        }
        if (plainStart < text.Length || blocks.Count == 0) blocks.Add(new MessageTextBlock(text[plainStart..]));
        return blocks;
    }

    private static bool TryOpeningFence(ReadOnlySpan<char> line, out string? language)
    {
        language = null;
        var start = FenceStart(line);
        if (start < 0) return false;
        var label = line[(start + 3)..].Trim();
        if (label.Length > 64 || label.Contains('`')) return false;
        if (!label.IsEmpty) language = label.ToString();
        return true;
    }

    private static bool IsClosingFence(ReadOnlySpan<char> line)
    {
        var start = FenceStart(line);
        if (start < 0) return false;
        foreach (var character in line[(start + 3)..])
            if (character is not (' ' or '\t')) return false;
        return true;
    }

    private static int FenceStart(ReadOnlySpan<char> line)
    {
        var start = 0;
        while (start < line.Length && line[start] == ' ') start++;
        return start <= 3 && line.Length >= start + 3
            && line.Slice(start, 3).SequenceEqual("```".AsSpan())
            && (line.Length == start + 3 || line[start + 3] != '`') ? start : -1;
    }

    private static (int ContentEnd, int End) ReadLine(string text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        var contentEnd = end;
        if (end < text.Length)
        {
            if (text[end++] == '\r' && end < text.Length && text[end] == '\n') end++;
        }
        return (contentEnd, end);
    }
}
