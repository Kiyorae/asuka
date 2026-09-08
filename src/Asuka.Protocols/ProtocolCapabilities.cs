using Asuka.Core;

namespace Asuka.Protocols;

/// <summary>Client features backed by the official protocol, without implementation extensions.</summary>
public sealed record ProtocolCapabilities(ProtocolKind Protocol)
{
    public bool Reactions => Protocol == ProtocolKind.Milky;
    // Event simulation is available even when the protocol has no corresponding bot action.
    public bool GroupNudgeEvents => Protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11;
    public bool Nudges => GroupNudgeEvents;
    public bool Requests => Protocol is ProtocolKind.OneBotV11 or ProtocolKind.Milky;
    public bool MemberModeration => Protocol is ProtocolKind.OneBotV11 or ProtocolKind.Milky;
    public bool MemberProfiles => MemberModeration;
    public bool Faces => Protocol is ProtocolKind.OneBotV11 or ProtocolKind.Milky;
    public bool ForwardedMessages => Faces;
    public bool Locations => Protocol is ProtocolKind.OneBotV11 or ProtocolKind.OneBotV12;
    public bool Audio => Protocol == ProtocolKind.OneBotV12;
    public bool Files => Protocol == ProtocolKind.OneBotV12;
    public bool SharedFiles => Protocol == ProtocolKind.Milky;
    // Keep uploads separate from the Milky-only shared-file management APIs.
    public bool GroupFileUploadEvents => Protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11;
    public bool PeerPins => Protocol == ProtocolKind.Milky;
    public bool ReadReceipts => Protocol == ProtocolKind.Milky;
    public bool ProfileEditing => Protocol == ProtocolKind.Milky;
    public bool GroupContent => Protocol == ProtocolKind.Milky;
    public bool GroupNotifications => Protocol == ProtocolKind.Milky;
    public bool CustomFaces => Protocol == ProtocolKind.Milky;
    public bool ProfileLikes => Protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11;
    public bool GroupHonors => Protocol == ProtocolKind.OneBotV11;
    public bool AccountCredentials => Protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11;
    public bool AnonymousMessages => Protocol == ProtocolKind.OneBotV11;
    public bool CacheCleanup => Protocol == ProtocolKind.OneBotV11;
    public bool ImplementationRestart => Protocol == ProtocolKind.OneBotV11;
    public bool SupportsAnonymous(ChatScene scene) => AnonymousMessages && scene == ChatScene.Group;
    public bool SupportsReactions(ChatScene scene) => Reactions && scene == ChatScene.Group;
    public bool SupportsNudges(ChatScene scene) => scene == ChatScene.Group && GroupNudgeEvents
        || scene == ChatScene.Friend && Protocol == ProtocolKind.Milky;
    public bool SupportsSharedFileUploads(ChatScene scene) => scene == ChatScene.Group && GroupFileUploadEvents
        || scene == ChatScene.Friend && SharedFiles;

    public bool SupportsSegment(MessageSegment segment) => segment switch
    {
        TextSegment or MentionSegment or ImageSegment or RecordSegment or VideoSegment or ReplySegment => true,
        FaceSegment => Faces,
        AudioSegment => Audio,
        FileSegment => Files,
        LocationSegment => Locations,
        ForwardSegment => ForwardedMessages,
        AnonymousSegment => AnonymousMessages,
        _ => false,
    };

    public static ProtocolCapabilities For(ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.OneBotV11 or ProtocolKind.OneBotV12 or ProtocolKind.Milky => new(protocol),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };
}
