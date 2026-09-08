namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task SendProfileLikeAsync(
        string userId,
        string senderId,
        int count,
        int dailyLimit,
        CancellationToken cancellationToken = default) =>
        SendProfileLikeCoreAsync(userId, senderId, count, dailyLimit, cancellationToken);

    private Task SendProfileLikeCoreAsync(
        string userId,
        string senderId,
        int count,
        int? dailyLimit,
        CancellationToken cancellationToken) =>
        MutateAsync(async token =>
        {
            if (count <= 0) throw InvalidParameter("count must be a positive int32");
            if (dailyLimit is <= 0) throw InvalidParameter("dailyLimit must be a positive int32");
            await ValidatePeerAccessAsync(new Chat(ChatScene.Friend, userId, senderId), token).ConfigureAwait(false);
            await Store.AddProfileLikesAsync(userId, senderId, count, dailyLimit, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        }, cancellationToken);
}
