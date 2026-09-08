using Asuka.Core;

namespace Asuka.Protocols;

public sealed record ShowcaseAssets(Asset Logo, Asset RotatingLogo, Asset Voice, Asset Video, Asset Document, Asset? WaveAudio = null);

public enum ShowcaseStepKind
{
    Message,
    ReactionAdded,
    ReactionRemoved,
    Recall,
}

public sealed record ShowcaseStep(long Index, Chat Chat, ShowcaseStepKind Kind, Message? Message, bool HistoryCleared)
{
    public string? Caption => HistoryCleared ? "展示历史已开始新一轮。" : null;
}

/// <summary>
/// A deterministic, manually stepped client demo. The host owns timing, cancellation, media bytes and UI navigation.
/// This class never opens a connection or starts a background task; all writes pass through PlatformService.
/// </summary>
public sealed class ClientShowcase
{
    public const string BotId = "91002";
    public const string AliceId = "91001";
    public const string BobId = "91003";
    public const string GroupId = "92001";
    public const int HistoryLimit = 120;
    public const int StepsPerCycle = 48;
    public static Chat GroupChat { get; } = new(ChatScene.Group, GroupId, BotId);
    public static Chat PrivateChat { get; } = new(ChatScene.Friend, AliceId, BotId);

    private readonly PlatformService _platform;
    private readonly ShowcaseAssets _assets;
    private long _nextStep;
    private int _busy;
    private bool _seeded;
    private string? _reactionTargetId;

    public ClientShowcase(PlatformService platform, ShowcaseAssets assets)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(assets);
        foreach (var asset in new[] { assets.Logo, assets.RotatingLogo, assets.Voice, assets.Video, assets.Document })
            ArgumentNullException.ThrowIfNull(asset);
        _platform = platform;
        _assets = assets;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try
        {
            foreach (var user in new[] { new User("Alice", AliceId, sign: "先泡一杯茶，再慢慢聊天。"),
                new User("Asuka Bot", BotId, sign: "一直在线的演示伙伴"), new User("Bob", BobId, sign: "今天也要发现有趣的事") })
            {
                if (await _platform.Store.GetUserAsync(user.Id, cancellationToken).ConfigureAwait(false) is null)
                    await _platform.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);
            }
            if (await _platform.Store.GetGroupAsync(GroupId, cancellationToken).ConfigureAwait(false) is null)
                await _platform.CreateGroupAsync(new Group("抹茶会客室 · Showcase", GroupId, intro: "图片、语音与慢慢展开的对话"),
                    AliceId, cancellationToken).ConfigureAwait(false);
            foreach (var id in new[] { AliceId, BotId, BobId })
            {
                if (await _platform.Store.GetMemberAsync(GroupId, id, cancellationToken).ConfigureAwait(false) is null)
                    await _platform.AddMemberAsync(GroupId, id, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            if (await _platform.Store.GetFriendshipAsync(AliceId, BotId, cancellationToken).ConfigureAwait(false) is null
                || await _platform.Store.GetFriendshipAsync(BotId, AliceId, cancellationToken).ConfigureAwait(false) is null)
                await _platform.AddFriendshipAsync(AliceId, BotId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var asset in EnumerateAssets())
            {
                if (await _platform.Store.GetAssetAsync(asset.Id, cancellationToken).ConfigureAwait(false) is null)
                    await _platform.SaveAssetAsync(asset, cancellationToken).ConfigureAwait(false);
            }
            _seeded = true;
        }
        finally { Exit(); }
    }

    /// <summary>Clears only the two demo timelines. Users, groups, media and unrelated conversations remain intact.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try
        {
            await _platform.ClearMessageHistoryAsync(GroupChat, cancellationToken).ConfigureAwait(false);
            await _platform.ClearMessageHistoryAsync(PrivateChat, cancellationToken).ConfigureAwait(false);
            _nextStep = 0;
            _reactionTargetId = null;
        }
        finally { Exit(); }
    }

    public async Task<ShowcaseStep> StepAsync(ProtocolKind protocol, CancellationToken cancellationToken = default)
    {
        var capabilities = ProtocolCapabilities.For(protocol);
        Enter(cancellationToken);
        try
        {
            if (!_seeded) throw new InvalidOperationException("Call EnsureSeededAsync before stepping the client showcase.");
            var index = _nextStep;
            var chat = index % 2 == 0 ? GroupChat : PrivateChat;
            var phase = (int)(index / 2 % (StepsPerCycle / 2));
            var history = await _platform.Store.GetMessagesAsync(chat, HistoryLimit, cancellationToken).ConfigureAwait(false);
            var cleared = history.Count >= HistoryLimit;
            if (cleared)
            {
                await _platform.ClearMessageHistoryAsync(chat, cancellationToken).ConfigureAwait(false);
                if (chat.Scene == ChatScene.Group) _reactionTargetId = null;
                history = [];
            }
            var target = history.LastOrDefault(message => !message.IsRecalled);
            var sender = chat.Scene == ChatScene.Friend ? phase % 2 == 0 ? AliceId : BotId
                : (phase % 3) switch { 0 => AliceId, 1 => BobId, _ => BotId };
            ShowcaseStep result;
            if (chat.Scene == ChatScene.Group && capabilities.Reactions && phase is 18 or 19 or 20
                && await TryReactionAsync(phase, target, cancellationToken).ConfigureAwait(false) is { } reaction)
            {
                result = new ShowcaseStep(index, chat, phase == 20 ? ShowcaseStepKind.ReactionRemoved : ShowcaseStepKind.ReactionAdded,
                    reaction, cleared);
            }
            else if (phase == 22 && target is not null)
            {
                var recalled = await _platform.RecallMessageAsync(target.Id, target.SenderId, cancellationToken).ConfigureAwait(false);
                result = new ShowcaseStep(index, chat, ShowcaseStepKind.Recall, recalled, cleared);
            }
            else
            {
                var content = CreateContent(phase, chat, sender, target, capabilities);
                if (content.Any(segment => !SupportsRecursively(segment, capabilities)))
                    throw new InvalidOperationException("The showcase selected a segment unavailable in the current protocol.");
                var message = await _platform.SendMessageAsync(chat.Scene, chat.PeerId, sender, chat.SelfId, content, cancellationToken)
                    .ConfigureAwait(false);
                result = new ShowcaseStep(index, chat, ShowcaseStepKind.Message, message, cleared);
            }
            _nextStep++;
            return result;
        }
        finally { Exit(); }
    }

    private IReadOnlyList<MessageSegment> CreateContent(int phase, Chat chat, string sender, Message? target, ProtocolCapabilities capabilities) => phase switch
    {
        0 => [new TextSegment(chat.Scene == ChatScene.Group ? "早上好，抹茶会客室开门了。🍵" : "嗨，Alice。今天也慢慢聊吧。")],
        1 => [new TextSegment("这是我们的项目 logo。"), new ImageSegment(_assets.Logo, Summary: "Asuka logo")],
        2 => [new TextSegment("转一圈，换个角度看。✨"), new ImageSegment(_assets.RotatingLogo, Summary: "旋转的 Asuka logo")],
        3 => [new TextSegment("你好，世界 · Hello, world · こんにちは 🌏\n抹茶、晴天，还有一只路过的猫。🐈\n𝛑 ≈ 3.14159 · café · A → B → C")],
        4 => [new TextSegment("今天的小计划：\n• 把窗户打开，让风进来\n• 给朋友发一张照片\n• 留一点时间听完整段音乐\n\n完成一件也很好。")],
        5 => [new TextSegment(LongMessage)],
        6 => [new TextSegment("刚写好的小片段：\n```csharp\nvar greeting = \"Hello, Asuka 🍵\";\nforeach (var friend in friends)\n{\n    await SendAsync(friend, greeting);\n}\n```\n代码结束，继续聊天。")],
        7 => [new RecordSegment(_assets.Voice, TimeSpan.FromMilliseconds(200))],
        8 => [new TextSegment("再听一段轻松的声音。"), new RecordSegment(_assets.WaveAudio ?? _assets.Voice, _assets.WaveAudio is null ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2))],
        9 => [new TextSegment("一小段会动的画面。"), new VideoSegment(_assets.Video, _assets.Logo)],
        10 when chat.Scene == ChatScene.Group => [new MentionSegment(sender == BotId ? AliceId : BotId), new TextSegment(" 过来看看这张新图。"), new ImageSegment(_assets.Logo)],
        10 => [new TextSegment("这条消息只发给你。我们可以在这里慢慢回复。")],
        11 or 21 when target is not null => [new ReplySegment(target.Id, target.SenderId), new TextSegment(phase == 11 ? "收到，我接着这条继续说。" : "回复留在原来的上下文里；下一步演示撤回这条回复。")],
        12 when capabilities.Faces => [new TextSegment("今天的心情："), new FaceSegment("14", "Smile"), new FaceSegment("76", "Thumbs up")],
        12 => [new TextSegment("今天的心情：🙂 👍 ☕")],
        13 when capabilities.Locations => [new LocationSegment(31.2304, 121.4737, "上海 · 午后散步", "从街角出发，沿着树荫慢慢走。")],
        13 => [new TextSegment("约在街角的咖啡馆吧。窗边的位置，下午三点。")],
        14 when capabilities.ForwardedMessages => [CreateForward(target, capabilities)],
        14 => [new TextSegment("朋友捎来一句话：\n“别忘了停下来看看窗外。”\n我也想把它分享给你。")],
        15 when capabilities.Files => [new TextSegment("今天的随手笔记，放在这个文件里。"), new FileSegment(_assets.Document)],
        15 => [new TextSegment("今天的随手笔记：散步、拍照、读完一章书。明天再添几行。")],
        16 when capabilities.Audio => [new TextSegment("把这段声音当作聊天的背景。"), new AudioSegment(_assets.WaveAudio ?? _assets.Voice)],
        16 => [new RecordSegment(_assets.WaveAudio ?? _assets.Voice, _assets.WaveAudio is null ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2))],
        17 => [new TextSegment("这一刻值得留个回应：今天也有好好生活。🌱")],
        18 => [new TextSegment("看到了，给你一个赞。👍")],
        19 => [new TextSegment("我也收到了。晚点带上热茶再来。☕")],
        20 => [new TextSegment("稍等一下，我整理好思路再回复。")],
        23 => [new TextSegment("最后把这张图留在这里。\n下一轮见，我们继续慢慢聊。"), new ImageSegment(_assets.RotatingLogo, Summary: "旋转的 Asuka logo")],
        _ => [new TextSegment("新的对话，从这一条开始。")],
    };

    private ForwardSegment CreateForward(Message? target, ProtocolCapabilities capabilities)
    {
        var nodes = new List<ForwardNode>
        {
            new(AliceId, "Alice", [new TextSegment("把几个片段放在一起，就成了今天的小故事。")]),
            new(BobId, "Bob", [new ImageSegment(_assets.Logo), new TextSegment("我来补一张图。")]),
        };
        if (target is not null)
            nodes.Add(new ForwardNode(target.SenderId, target.SenderId switch { BotId => "Asuka Bot", BobId => "Bob", _ => "Alice" },
                target.Content.All(segment => SupportsRecursively(segment, capabilities)) ? target.Content : [new TextSegment(target.Content.TextPreview())],
                target.Id, target.Time));
        return new ForwardSegment(IdGenerator.MessageId(), nodes, "今天的小故事", "几位朋友的对话片段");
    }

    private async Task<Message?> TryReactionAsync(int phase, Message? latest, CancellationToken cancellationToken)
    {
        var target = phase == 18 || _reactionTargetId is null ? latest
            : await _platform.Store.GetMessageAsync(_reactionTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null || target.IsRecalled || target.Chat != GroupChat) return null;
        var added = phase != 20;
        var user = phase == 19 ? BobId : AliceId;
        var face = phase == 19 ? "66" : "76";
        var existing = await _platform.Store.GetMessageReactionsAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (existing.Any(reaction => reaction.UserId == user && reaction.Reaction == face && reaction.ReactionType == "face") == added)
            return null;
        await _platform.ReactAsync(target.Id, user, face, added, "face", cancellationToken).ConfigureAwait(false);
        _reactionTargetId = target.Id;
        return target;
    }

    private IEnumerable<Asset> EnumerateAssets()
    {
        yield return _assets.Logo;
        yield return _assets.RotatingLogo;
        yield return _assets.Voice;
        yield return _assets.Video;
        yield return _assets.Document;
        if (_assets.WaveAudio is { } wave) yield return wave;
    }

    private static bool SupportsRecursively(MessageSegment segment, ProtocolCapabilities capabilities) =>
        capabilities.SupportsSegment(segment) && (segment is not ForwardSegment forward
            || forward.Nodes.SelectMany(node => node.Content).All(child => SupportsRecursively(child, capabilities)));

    private void Enter(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A showcase operation is already in progress. Await it before starting the next step.");
    }

    private void Exit() => Volatile.Write(ref _busy, 0);

    private const string LongMessage = "有时候，一条长消息也可以像一次不着急的散步。我们从清晨的第一杯茶聊起，聊到窗外的树影，" +
        "再聊到最近读过的一段文字。聊天框会自然换行，头像和时间会安静地留在旁边，让每一句话都有自己的位置。\n\n" +
        "我喜欢这样慢慢展开的对话：有人发来一张图片，有人补上一段语音，也有人只是回复一个小小的表情。" +
        "不同的内容排在同一条时间线上，既能看清来龙去脉，也能随时回到前面的某句话。\n\n" +
        "如果现在有一点空闲，就把手里的事情放一放，望一眼远处。也许天色刚好，也许楼下有人牵着狗经过，" +
        "也许只是风把窗帘轻轻吹动。这些很普通的小事，值得被认真地记录下来，再分享给愿意听的人。\n\n" +
        "这一段故意留得长一些，好让我们看看宽度变化、段落间距和滚动时的阅读感受。读到这里就可以停下了，" +
        "不用急着回复。下一条消息会在合适的时候到来，像朋友走过来，轻轻敲了敲门。🍵";
}
