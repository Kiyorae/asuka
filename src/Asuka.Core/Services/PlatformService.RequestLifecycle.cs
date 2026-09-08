namespace Asuka.Core;

public sealed partial class PlatformService
{
    /// <summary>Creates a member invitation that requires a group moderator's approval.</summary>
    public Task<PendingRequest> RequestInvitedJoinGroupAsync(
        string groupId,
        string inviterId,
        string inviteeId,
        string targetBotId,
        bool isFiltered = false,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
            _ = await Store.GetUserAsync(inviterId, token).ConfigureAwait(false) ?? throw UserNotFound(inviterId);
            _ = await Store.GetUserAsync(inviteeId, token).ConfigureAwait(false) ?? throw UserNotFound(inviteeId);
            _ = await Store.GetMemberAsync(groupId, inviterId, token).ConfigureAwait(false)
                ?? throw NotAMember(groupId, inviterId);
            await RequireRequestModeratorAsync(groupId, targetBotId, token).ConfigureAwait(false);
            if (await Store.GetMemberAsync(groupId, inviteeId, token).ConfigureAwait(false) is not null)
            {
                throw AlreadyExists("The invitee is already in the group");
            }

            var request = new PendingRequest(RequestKind.GroupInvitedJoin, inviterId, targetBotId, groupId,
                targetUserId: inviteeId, isFiltered: isFiltered);
            await Store.SaveAsync(request, token).ConfigureAwait(false);
            request = (await Store.GetRequestAsync(request.Id, CancellationToken.None).ConfigureAwait(false))!;
            Publish(new DomainEvent(targetBotId, new RequestReceivedEvent(request)), inviterId);
            return request;
        }, cancellationToken);

    public Task IgnoreRequestAsync(string flag, CancellationToken cancellationToken = default) =>
        IgnoreRequestAsync(flag, null, null, cancellationToken);

    public Task IgnoreRequestAsync(string flag, string? expectedSelfId, string? expectedRequestId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var request = expectedRequestId is null
                ? await Store.GetRequestByFlagAsync(flag, expectedSelfId, token).ConfigureAwait(false)
                : await Store.GetRequestAsync(expectedRequestId, token).ConfigureAwait(false);
            if (request is null || request.Flag != flag || expectedSelfId is not null && request.SelfId != expectedSelfId)
                throw RequestNotFound(flag);
            if (request.Resolution is not null) throw NotPermitted("This request has already been resolved");
            await ValidateRequestResolutionAsync(request, token).ConfigureAwait(false);
            _ = await Store.ResolveRequestAsync(request.Id, RequestResolution.Ignored,
                resolvedBy: request.SelfId, cancellationToken: token).ConfigureAwait(false)
                ?? throw NotPermitted("This request has already been resolved");
        }, cancellationToken);

    private async Task ValidateRequestResolutionAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        _ = await Store.GetUserAsync(request.SelfId, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound(request.SelfId);
        if (request.Kind is RequestKind.GroupJoin or RequestKind.GroupInvitedJoin)
        {
            if (request.GroupId is null) throw InvalidParameter("The group request is missing a group ID");
            await RequireRequestModeratorAsync(request.GroupId, request.SelfId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RequireRequestModeratorAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        var member = await Store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw NotAMember(groupId, userId);
        if (member.Role == GroupRole.Member)
        {
            throw NotPermitted("Administrator privileges are required to resolve group requests");
        }
    }
}
