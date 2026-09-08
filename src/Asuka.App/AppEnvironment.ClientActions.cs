using Asuka.Core;
using Asuka.Protocols;
using Windows.Storage;

namespace Asuka.App;

public sealed partial class AppEnvironment
{
    public async Task ForwardMessageAsync(string sourceId, Chat destination, CancellationToken cancellationToken = default)
    {
        RequireCurrentBotChat(destination);
        if (!Capabilities.ForwardedMessages) throw new InvalidOperationException("Forwarded messages are unavailable in this protocol.");
        var protocol = Preferences.Protocol;
        var actor = CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
        var source = await Store.GetMessageAsync(sourceId, cancellationToken);
        if (source is null || source.IsRecalled || source.SelfId != destination.SelfId)
            throw new InvalidOperationException("The source message is unavailable for this account.");
        var author = source.Anonymous is null ? await Store.GetUserAsync(source.SenderId, cancellationToken) : null;
        RequireCurrentBotChat(destination);
        if (CurrentPersona?.Id != actor.Id || Preferences.Protocol != protocol
            || !HasReadyMessageContext)
            throw new InvalidOperationException("Connect a ready session with the same sending identity before forwarding.");
        var publicSenderId = source.Anonymous?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? source.SenderId;
        var node = new ForwardNode(publicSenderId, source.Anonymous?.Name ?? author?.DisplayName ?? publicSenderId,
            source.Content, source.Id, source.Time);
        await Platform.SendMessageAsync(destination.Scene, destination.PeerId, actor.Id, destination.SelfId,
            [new ForwardSegment(IdGenerator.MessageId(), [node])], cancellationToken);
    }

    public Task SetPinnedAsync(Chat chat, bool pinned, CancellationToken cancellationToken = default)
    {
        RequireCurrentBotChat(chat);
        if (!Capabilities.PeerPins) throw new InvalidOperationException("Conversation pins are unavailable in this protocol.");
        return Platform.SetPeerPinAsync(chat, pinned, cancellationToken);
    }

    public async Task MarkReadAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        RequireCurrentBotChat(chat);
        if (!Capabilities.ReadReceipts) throw new InvalidOperationException("Read receipts are unavailable in this protocol.");
        var protocol = Preferences.Protocol;
        var messages = await Store.GetMessagesAsync(chat, 1, cancellationToken);
        if (messages.Count > 0)
        {
            RequireCurrentBotChat(chat);
            if (Preferences.Protocol != protocol) throw new InvalidOperationException("The protocol changed.");
            var latest = messages[^1];
            await Platform.MarkMessageAsReadAsync(chat, latest.Seq, cancellationToken);
        }
    }

    public async Task<SharedFile> UploadSharedFileAsync(Chat chat, StorageFile file, CancellationToken cancellationToken = default)
    {
        RequireCurrentBotChat(chat);
        if (!Capabilities.SupportsSharedFileUploads(chat.Scene)) throw new InvalidOperationException("File uploads are unavailable in this conversation.");
        var protocol = Preferences.Protocol;
        var actor = CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
        var selectedChat = SelectedChat;
        var recipientId = chat.Scene == ChatScene.Friend ? chat.CounterpartId(actor.Id) : null;
        if (chat.Scene != ChatScene.Group && recipientId is null)
        {
            throw new InvalidOperationException("Choose a friend or group conversation to upload a file.");
        }

        var draft = await CreateAttachmentAsync(file, cancellationToken);
        RequireCurrentBotChat(chat);
        if (CurrentPersona?.Id != actor.Id || Preferences.Protocol != protocol || SelectedChat != selectedChat
            || !Capabilities.SupportsSharedFileUploads(chat.Scene))
            throw new InvalidOperationException("The conversation, sending identity or protocol changed during import.");
        await using var input = File.OpenRead(Assets.LocationOf(draft.Asset.Id));
        if (input.Length != draft.Asset.ByteCount)
            throw new InvalidOperationException("The cached file changed during import. Choose the file again.");
        if (chat.Scene == ChatScene.Group)
        {
            return await Platform.ShareGroupFileAsync(chat.PeerId, actor.Id, draft.Asset, cancellationToken: cancellationToken);
        }

        var hash = await TriSha1.ComputeHashAsync(input, cancellationToken);
        RequireCurrentBotChat(chat);
        if (CurrentPersona?.Id != actor.Id || Preferences.Protocol != protocol || SelectedChat != selectedChat
            || !Capabilities.SupportsSharedFileUploads(chat.Scene))
            throw new InvalidOperationException("The conversation, sending identity or protocol changed during import.");
        return await Platform.SharePrivateFileAsync(recipientId!, actor.Id, draft.Asset, hash, cancellationToken);
    }

    public Task NudgeAsync(Chat chat, string targetId, CancellationToken cancellationToken = default)
    {
        RequireCurrentBotChat(chat);
        if (!Capabilities.SupportsNudges(chat.Scene)) throw new InvalidOperationException("Nudges are unavailable in this conversation.");
        var actor = CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
        var peerId = chat.Scene == ChatScene.Group ? chat.PeerId
            : chat.CounterpartId(actor.Id) ?? throw new InvalidOperationException("Select a conversation participant.");
        return Platform.PokeAsync(chat.Scene, peerId, actor.Id, targetId, cancellationToken);
    }

    private void RequireCurrentBotChat(Chat chat)
    {
        if (BotPersona?.Id != chat.SelfId) throw new InvalidOperationException("The bot account for this conversation changed.");
    }
}
