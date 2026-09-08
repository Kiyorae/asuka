using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace Asuka.App;

public abstract class BindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PersonaItem(User user)
{
    public User User { get; } = user;
    public string Id => User.Id;
    public string DisplayName => User.DisplayName;
    public string Description => $"{DisplayName}  ·  {Id}";
    public override string ToString() => Description;
}

public sealed class ConversationItem
{
    public ConversationItem(ConversationSummary summary)
        : this(
            summary.Chat,
            summary.Title,
            string.IsNullOrWhiteSpace(summary.Preview) ? "No text content" : summary.Preview,
            summary.LastMessage.Time)
    {
    }

    public ConversationItem(Chat chat, string title, string preview, DateTimeOffset? time = null, string? displayId = null)
    {
        Chat = chat;
        Title = title;
        Preview = preview;
        Time = time;
        DisplayId = displayId ?? chat.PeerId;
    }

    public Chat Chat { get; }
    public string Id => Chat.Id;
    public string Title { get; }
    public bool IsPinned { get; internal set; }
    public string DisplayTitle => IsPinned ? $"📌 {Title}" : Title;
    public string DisplayId { get; }
    public string Preview { get; }
    public DateTimeOffset? Time { get; }
    public string TimeText => Time?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;
    public string Glyph => Chat.Scene == ChatScene.Group ? "\uE716" : "\uE77B";
    public string IdentityText => $"{(Chat.Scene == ChatScene.Group ? "Group" : "Person")} · {DisplayId}";
    public string AccessibilityLabel => $"{DisplayTitle}, {IdentityText}, {Preview}, {TimeText}";
}

public sealed class PendingRequestItem
{
    public PendingRequestItem(PendingRequest request, User? requester, Group? group, User? target = null, bool canIgnore = false)
    {
        Request = request;
        Requester = requester;
        Group = group;
        Target = target;
        CanIgnore = canIgnore;
    }

    public PendingRequest Request { get; }
    public User? Requester { get; }
    public Group? Group { get; }
    public User? Target { get; }
    public bool CanIgnore { get; }
    public Visibility IgnoreVisibility => CanIgnore ? Visibility.Visible : Visibility.Collapsed;
    public string Title => Request.Kind switch
    {
        RequestKind.Friend => $"Friend request from {Requester?.DisplayName ?? Request.RequesterId}",
        RequestKind.GroupJoin => $"{Requester?.DisplayName ?? Request.RequesterId} wants to join {Group?.Name ?? Request.GroupId}",
        RequestKind.GroupInvite => $"Invitation to {Group?.Name ?? Request.GroupId}",
        RequestKind.GroupInvitedJoin => $"{Requester?.DisplayName ?? Request.RequesterId} invites {Target?.DisplayName ?? Request.TargetUserId} to {Group?.Name ?? Request.GroupId}",
        _ => "Pending request",
    };
    public string Detail => (Request.IsFiltered ? "Filtered · " : string.Empty)
        + (string.IsNullOrWhiteSpace(Request.Comment) ? Request.Flag : Request.Comment);
}

public sealed class MessageItem(Message message, User? sender, bool isCurrentPersona)
{
    public IReadOnlyList<MessageReactionState> Reactions { get; init; } = [];
    public bool IsEssence { get; init; }
    public Message Message { get; } = message;
    public string Id => Message.Id;
    public string DisplaySenderId => Message.Anonymous?.Id.ToString(CultureInfo.InvariantCulture) ?? Message.SenderId;
    public string Sender => Message.Anonymous?.Name ?? sender?.DisplayName ?? Message.SenderId;
    public string Text => Message.IsRecalled
        ? "This message was recalled"
        : string.IsNullOrWhiteSpace(Message.Content.TextPreview()) ? "(No text content)" : Message.Content.TextPreview();
    public string TimeText => Message.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
    public string Meta => (IsEssence ? "★ Essence  ·  " : string.Empty) + $"{Sender}  ·  {TimeText}";
    public HorizontalAlignment Alignment => isCurrentPersona ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public HorizontalAlignment MetaAlignment => Alignment;
    public Brush BubbleBrush => new SolidColorBrush(isCurrentPersona
        ? Windows.UI.Color.FromArgb(255, 31, 111, 235)
        : Windows.UI.Color.FromArgb(28, 128, 128, 128));
    public Brush ForegroundBrush => new SolidColorBrush(isCurrentPersona
        ? Microsoft.UI.Colors.White
        : Microsoft.UI.Colors.Transparent);
    public bool UseDefaultForeground => !isCurrentPersona;
    public string AccessibilityLabel => $"Message from {Sender} at {TimeText}: {Text}";
}

public sealed class AttachmentDraft(StorageFile file, Asset asset)
{
    public StorageFile File { get; } = file;
    public Asset Asset { get; } = asset;
    public string Name => File.Name;
    public string Description => $"{Name} ({FormatBytes(Asset.ByteCount)})";
    public string Glyph => Asset.MimeType switch
    {
        string value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "\uEB9F",
        string value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "\uE8B2",
        string value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "\uE8D6",
        _ => "\uE8A5",
    };

    public MessageSegment ToSegment() => Asset.MimeType switch
    {
        string value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => MessageSegment.Image(Asset),
        string value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => MessageSegment.Video(Asset),
        string value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => MessageSegment.Voice(Asset),
        _ => MessageSegment.File(Asset),
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.#} MB",
        >= 1024L => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}

public sealed record MentionDraft(string? UserId, string DisplayName)
{
    public string Description => UserId is null ? "@everyone" : $"@{DisplayName}";
    public MessageSegment ToSegment() => MessageSegment.Mention(UserId);
}

public sealed class MemberItem(MemberRosterItem roster)
{
    public MemberRosterItem Roster { get; } = roster;
    public GroupMember Member => Roster.Member;
    public User User => Roster.User;
    public string DisplayName => string.IsNullOrWhiteSpace(Member.Card) ? User.DisplayName : Member.Card;
    public string Detail => $"{Member.Role}  ·  {User.Id}" + (Member.IsMuted ? "  ·  Muted" : string.Empty);
    public override string ToString() => $"{DisplayName} — {Detail}";
}
