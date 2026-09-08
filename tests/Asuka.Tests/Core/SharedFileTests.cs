using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class SharedFileTests
{
    private const string GroupId = "50001";
    private const string OwnerId = "10001";
    private const string MemberId = "10002";
    private const string OtherMemberId = "10003";
    private const string OutsiderId = "10004";
    private static readonly Asset TestAsset = new("asset-content", "one.txt", "text/plain", 3);

    [TestMethod]
    public async Task SharesFoldersAndLifetimeSurviveDatabaseReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-shared-file-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "store.db");
        string fileId;
        string folderId;
        try
        {
            await using (var store = new AsukaStore(database))
            {
                await SeedAsync(store);
                await using var platform = new PlatformService(store);
                var folder = await platform.CreateGroupFolderAsync(GroupId, OwnerId, "Files");
                folderId = folder.Id;
                var file = await platform.ShareGroupFileAsync(GroupId, MemberId, TestAsset, folder.Id, DateTimeOffset.UtcNow.AddDays(7));
                fileId = file.Id;
                Assert.IsNotNull(file.ExpiresAt);
                await platform.PersistGroupFileAsync(GroupId, file.Id, MemberId);
                await platform.RenameGroupFileAsync(GroupId, file.Id, MemberId, "renamed.txt", folder.Id);
            }

            await using (var reopened = new AsukaStore(database))
            {
                await using var platform = new PlatformService(reopened);
                var file = await platform.GetGroupFileForDownloadAsync(GroupId, fileId, OwnerId);
                Assert.AreEqual("renamed.txt", file.Name);
                Assert.AreEqual(folderId, file.ParentFolderId);
                Assert.AreEqual(MemberId, file.UploaderId);
                Assert.IsNull(file.ExpiresAt);
                Assert.AreEqual(TestAsset, file.Asset);
                var folder = await reopened.GetGroupFolderAsync(GroupId, folderId);
                Assert.IsNotNull(folder);
                Assert.AreEqual(1, folder.FileCount);
                Assert.AreEqual("Files", folder.Name);
            }

            // Simulate an elapsed server expiry without a timing-dependent sleep.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE shared_files SET expires_at=$expired WHERE id=$id;";
                command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
                command.Parameters.AddWithValue("$id", fileId);
                await command.ExecuteNonQueryAsync();
            }

            await using (var reopened = new AsukaStore(database))
            {
                await using var platform = new PlatformService(reopened);
                Assert.IsEmpty(await reopened.GetGroupFilesAsync(GroupId, folderId));
                await Rejected(platform.GetSharedFileForDownloadAsync(fileId, OwnerId), PlatformError.FileNotFound);
                await Rejected(platform.PersistGroupFileAsync(GroupId, fileId, OwnerId), PlatformError.FileNotFound);
                await platform.DeleteGroupFileAsync(GroupId, fileId, OwnerId);
                Assert.IsNull(await reopened.GetSharedFileAsync(fileId));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ManagementRequiresMembershipAndUploaderOrAdministratorAuthority()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var file = await platform.ShareGroupFileAsync(GroupId, MemberId, TestAsset);
        await Rejected(platform.DeleteGroupFileAsync(GroupId, file.Id, OtherMemberId), PlatformError.NotPermitted);
        await Rejected(platform.RenameGroupFileAsync(GroupId, file.Id, OtherMemberId, "forbidden"), PlatformError.NotPermitted);
        await Rejected(platform.PersistGroupFileAsync(GroupId, file.Id, OtherMemberId), PlatformError.NotPermitted);
        await Rejected(platform.GetGroupFileForDownloadAsync(GroupId, file.Id, OutsiderId), PlatformError.NotAMember);
        await Rejected(platform.GetGroupFileListingAsync(GroupId, OutsiderId), PlatformError.NotAMember);
        await Rejected(platform.CreateGroupFolderAsync(GroupId, MemberId, "forbidden"), PlatformError.NotPermitted);
        await platform.RenameGroupFileAsync(GroupId, file.Id, OwnerId, "moderated.txt");
        await platform.DeleteGroupFileAsync(GroupId, file.Id, MemberId);
        Assert.IsNull(await store.GetGroupFileAsync(GroupId, file.Id));
    }

    [TestMethod]
    public async Task FolderDeletionRemovesOnlyContainedSharesAndPreservesDeduplicatedAsset()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var folder = await platform.CreateGroupFolderAsync(GroupId, OwnerId, "Remove me");
        var inside = await platform.ShareGroupFileAsync(GroupId, OwnerId, TestAsset, folder.Id);
        var outside = await platform.ShareGroupFileAsync(GroupId, OwnerId, TestAsset);
        await platform.DeleteGroupFolderAsync(GroupId, folder.Id, OwnerId);
        Assert.IsNull(await store.GetGroupFileAsync(GroupId, inside.Id));
        Assert.IsNotNull(await store.GetGroupFileAsync(GroupId, outside.Id));
        Assert.IsNotNull(await store.GetAssetAsync(TestAsset.Id));
        Assert.IsEmpty(await store.GetGroupFoldersAsync(GroupId));
    }

    [TestMethod]
    public async Task GroupUploadsPublishCommittedShareIdentityAndRespectMute()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        platform.RegisterBot(OwnerId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        var changes = 0;
        store.Changed += (_, args) => { if ((args.Changes & StoreChangeKind.Files) != 0) changes++; };
        var file = await platform.ShareGroupFileAsync(GroupId, MemberId, TestAsset);
        Assert.IsTrue(await next);
        var upload = ((GroupFileUploadedEvent)events.Current.Payload).Upload;
        Assert.AreEqual(file.Id, upload.FileId);
        Assert.AreEqual(file.Name, upload.FileName);
        Assert.IsNotNull(await store.GetGroupFileAsync(GroupId, upload.FileId!));
        Assert.AreEqual(1, changes);
        await platform.MoveGroupFileAsync(GroupId, file.Id, MemberId);
        await platform.RenameGroupFileAsync(GroupId, file.Id, MemberId, file.Name);
        await platform.PersistGroupFileAsync(GroupId, file.Id, MemberId);
        Assert.AreEqual(1, changes, "No-op mutations must not report a store change");
        await store.SaveAsync(new GroupMember(GroupId, OtherMemberId, mutedUntil: DateTimeOffset.UtcNow.AddHours(1)));
        await Rejected(platform.ShareGroupFileAsync(GroupId, OtherMemberId, TestAsset), PlatformError.Muted);
        Assert.HasCount(1, await store.GetGroupFilesAsync(GroupId));
    }

    [TestMethod]
    public async Task PrivateFilesRequireFriendshipAndBothParticipantsOwnTheirView()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        const string hash = "c9c260e50e371341201ccdd02621b73a6481000a";
        await Rejected(platform.SharePrivateFileAsync(MemberId, OwnerId, TestAsset, hash), PlatformError.NotPermitted);
        await platform.AddFriendshipAsync(OwnerId, MemberId);
        var file = await platform.SharePrivateFileAsync(MemberId, OwnerId, TestAsset, hash);
        Assert.AreEqual(file.Id, (await platform.GetPrivateFileForDownloadAsync(OwnerId, MemberId, file.Id, hash, true)).Id);
        Assert.AreEqual(file.Id, (await platform.GetPrivateFileForDownloadAsync(MemberId, OwnerId, file.Id, hash)).Id);
        Assert.AreEqual(file.Id, (await platform.GetSharedFileForDownloadAsync(file.Id, OwnerId)).Id);
        Assert.AreEqual(file.Id, (await platform.GetSharedFileForDownloadAsync(file.Id, MemberId)).Id);
        await Rejected(platform.GetSharedFileForDownloadAsync(file.Id, OutsiderId), PlatformError.FileNotFound);
        await Rejected(platform.GetPrivateFileForDownloadAsync(OutsiderId, OwnerId, file.Id, hash), PlatformError.FileNotFound);
        await Rejected(platform.GetPrivateFileForDownloadAsync(OwnerId, MemberId, file.Id, hash), PlatformError.FileNotFound);
        await Rejected(platform.GetPrivateFileForDownloadAsync(OwnerId, MemberId, file.Id, new string('0', 40), true), PlatformError.FileNotFound);
        Assert.HasCount(1, await store.GetPrivateFilesAsync(OwnerId, MemberId));
        Assert.HasCount(1, await store.GetPrivateFilesAsync(MemberId, OwnerId));
        Assert.IsEmpty(await store.GetPrivateFilesAsync(OutsiderId, OwnerId));
    }

    [TestMethod]
    public async Task FolderNamesAreUniqueUnderConcurrentCreateAndInvalidMovesPreserveSource()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await platform.CreateGroupFolderAsync(GroupId, OwnerId, "Unique"); return true; }
            catch (PlatformException exception) when (exception.Error == PlatformError.AlreadyExists) { return false; }
        }));
        Assert.AreEqual(1, outcomes.Count(success => success));
        var file = await platform.ShareGroupFileAsync(GroupId, MemberId, TestAsset);
        await Rejected(platform.MoveGroupFileAsync(GroupId, file.Id, MemberId, targetFolderId: "missing"), PlatformError.FolderNotFound);
        await Rejected(platform.MoveGroupFileAsync(GroupId, file.Id, MemberId, parentFolderId: "wrong"), PlatformError.FileNotFound);
        Assert.AreEqual("/", (await store.GetGroupFileAsync(GroupId, file.Id))!.ParentFolderId);
        await Rejected(platform.CreateGroupFolderAsync(GroupId, OwnerId, "../escape"), PlatformError.InvalidParameter);
    }

    [TestMethod]
    public async Task ActualDownloadsMatchTheShareAssetAndDoNotModifyFolderTimestamps()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var folder = await platform.CreateGroupFolderAsync(GroupId, OwnerId, "Downloads");
        var file = await platform.ShareGroupFileAsync(GroupId, MemberId, TestAsset, folder.Id);
        var modified = (await store.GetGroupFolderAsync(GroupId, folder.Id))!.LastModifiedAt;
        await platform.RecordGroupFileDownloadAsync(GroupId, file.Id, "wrong-asset");
        await platform.RecordGroupFileDownloadAsync("wrong-group", file.Id, TestAsset.Id);
        Assert.AreEqual(0, (await store.GetGroupFileAsync(GroupId, file.Id))!.DownloadedTimes);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => platform.RecordGroupFileDownloadAsync(GroupId, file.Id, TestAsset.Id)));
        Assert.AreEqual(20, (await store.GetGroupFileAsync(GroupId, file.Id))!.DownloadedTimes);
        Assert.AreEqual(modified, (await store.GetGroupFolderAsync(GroupId, folder.Id))!.LastModifiedAt);
    }

    private static async Task Rejected(Task operation, PlatformError error)
    {
        var exception = await Assert.ThrowsExactlyAsync<PlatformException>(() => operation);
        Assert.AreEqual(error, exception.Error);
    }

    private static async Task SeedAsync(AsukaStore store)
    {
        foreach (var id in new[] { OwnerId, MemberId, OtherMemberId, OutsiderId })
            await store.SaveAsync(new User(id, id: id));
        await store.SaveAsync(new Group("Files", id: GroupId));
        await store.SaveAsync(new GroupMember(GroupId, OwnerId, role: GroupRole.Owner));
        await store.SaveAsync(new GroupMember(GroupId, MemberId));
        await store.SaveAsync(new GroupMember(GroupId, OtherMemberId));
    }
}
