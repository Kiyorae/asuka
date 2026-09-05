using Microsoft.Data.Sqlite;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Asuka.App;

/// <summary>
/// Resolves the app-owned storage locations. Packaged installs use the Windows
/// application-data containers; the unpackaged debug profile keeps its legacy
/// location so local development does not depend on package identity.
/// </summary>
internal sealed class AppStoragePaths
{
    private AppStoragePaths(
        string settingsPath,
        string databasePath,
        string assetsDirectory,
        bool hasPackageIdentity,
        IReadOnlyList<string> migrationMessages)
    {
        SettingsPath = settingsPath;
        DatabasePath = databasePath;
        AssetsDirectory = assetsDirectory;
        HasPackageIdentity = hasPackageIdentity;
        MigrationMessages = migrationMessages;
    }

    public string SettingsPath { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public bool HasPackageIdentity { get; }
    public IReadOnlyList<string> MigrationMessages { get; }

    public static AppStoragePaths Create()
    {
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asuka");

        if (!TryGetPackagedFolders(out var localFolder, out var cacheFolder))
        {
            var localPaths = new AppStoragePaths(
                Path.Combine(legacyRoot, "settings.json"),
                Path.Combine(legacyRoot, "Data", "asuka.sqlite3"),
                Path.Combine(legacyRoot, "Cache", "assets"),
                hasPackageIdentity: false,
                []);
            localPaths.EnsureDirectories();
            return localPaths;
        }

        var packagedPaths = new AppStoragePaths(
            Path.Combine(localFolder.Path, "settings.json"),
            Path.Combine(localFolder.Path, "Data", "asuka.sqlite3"),
            Path.Combine(cacheFolder.Path, "assets"),
            hasPackageIdentity: true,
            MigrateLegacyData(
                legacyRoot,
                Path.Combine(localFolder.Path, "settings.json"),
                Path.Combine(localFolder.Path, "Data", "asuka.sqlite3"),
                Path.Combine(cacheFolder.Path, "assets")));
        packagedPaths.EnsureDirectories();
        return packagedPaths;
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(AssetsDirectory);
    }

    private static bool TryGetPackagedFolders(
        out StorageFolder localFolder,
        out StorageFolder cacheFolder)
    {
        try
        {
            // Package.Current is the identity boundary. Do not infer packaging
            // from a writable folder because unpackaged processes have one too.
            if (string.IsNullOrWhiteSpace(Package.Current.Id.FullName))
            {
                localFolder = null!;
                cacheFolder = null!;
                return false;
            }

            var applicationData = ApplicationData.Current;
            localFolder = applicationData.LocalFolder;
            cacheFolder = applicationData.LocalCacheFolder;
            return !string.IsNullOrWhiteSpace(localFolder.Path)
                && !string.IsNullOrWhiteSpace(cacheFolder.Path);
        }
        catch (Exception)
        {
            localFolder = null!;
            cacheFolder = null!;
            return false;
        }
    }

    private static List<string> MigrateLegacyData(
        string legacyRoot,
        string settingsPath,
        string databasePath,
        string assetsDirectory)
    {
        var messages = new List<string>();
        try
        {
            if (!Directory.Exists(legacyRoot))
            {
                return messages;
            }

            var migrated = new List<string>();
            CopyFileWhenAbsent(Path.Combine(legacyRoot, "settings.json"), settingsPath, migrated);

            var legacyDatabase = Path.Combine(legacyRoot, "Data", "asuka.sqlite3");
            if (!File.Exists(databasePath) && File.Exists(legacyDatabase))
            {
                if (MigrateDatabaseSnapshotWhenAbsent(legacyDatabase, databasePath, out var databaseMessage))
                {
                    migrated.Add("consistent database snapshot");
                }
                else if (!string.IsNullOrWhiteSpace(databaseMessage))
                {
                    messages.Add(databaseMessage);
                }
            }

            var legacyAssets = Path.Combine(legacyRoot, "Cache", "assets");
            if (!Directory.Exists(assetsDirectory) && Directory.Exists(legacyAssets))
            {
                CopyDirectoryWhenAbsent(legacyAssets, assetsDirectory, migrated);
            }

            if (migrated.Count > 0)
            {
                messages.Add($"Migrated legacy unpackaged data into this package ({string.Join(", ", migrated)}). The original files were kept.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            messages.Add($"Legacy data migration was skipped: {exception.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Takes a consistent SQLite snapshot while the legacy application may still
    /// be running. Copying the main file together with WAL/SHM sidecars is not a
    /// transactionally valid migration, so this deliberately never copies them.
    /// </summary>
    private static bool MigrateDatabaseSnapshotWhenAbsent(
        string sourcePath,
        string destinationPath,
        out string? message)
    {
        message = null;
        if (File.Exists(destinationPath))
        {
            return false;
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var stagingPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.migration-{Guid.NewGuid():N}.sqlite3");

        try
        {
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            var stagingBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // The file is renamed immediately after disposal; retaining a
                // pooled handle would prevent that on Windows.
                Pooling = false,
            };

            using (var source = new SqliteConnection(sourceBuilder.ConnectionString))
            using (var staging = new SqliteConnection(stagingBuilder.ConnectionString))
            {
                source.Open();
                staging.Open();
                source.BackupDatabase(staging);

                using var integrityCheck = staging.CreateCommand();
                integrityCheck.CommandText = "PRAGMA integrity_check;";
                var result = Convert.ToString(integrityCheck.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result?.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SQLite integrity_check did not return ok.");
                }
            }

            // Same-directory rename is atomic on the package volume. The false
            // overload preserves a database created by a concurrent first run.
            File.Move(stagingPath, destinationPath, overwrite: false);
            return true;
        }
        catch (SqliteException exception)
        {
            message = $"Legacy SQLite migration was skipped because the source database could not be read safely: {exception.Message}";
            return false;
        }
        catch (InvalidDataException exception)
        {
            message = $"Legacy SQLite migration was skipped because its snapshot failed integrity verification: {exception.Message}";
            return false;
        }
        catch (IOException exception) when (File.Exists(destinationPath))
        {
            // Another first-run process won the no-overwrite publication race.
            message = $"Legacy SQLite migration was not needed because this package database was created concurrently: {exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            message = $"Legacy SQLite migration was skipped: {exception.Message}";
            return false;
        }
        finally
        {
            // A failed or lost publication must leave no destination database
            // behind, allowing the next launch to retry from the untouched source.
            try
            {
                foreach (var stagingFile in new[] { stagingPath, stagingPath + "-wal", stagingPath + "-shm", stagingPath + "-journal" })
                {
                    if (File.Exists(stagingFile))
                    {
                        File.Delete(stagingFile);
                    }
                }
            }
            catch (Exception)
            {
                // A later launch can safely use a different unique staging name.
            }
        }
    }

    private static void CopyFileWhenAbsent(string source, string destination, List<string> copied)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        copied.Add(Path.GetFileName(destination));
    }

    private static void CopyDirectoryWhenAbsent(string source, string destination, List<string> copied)
    {
        if (Directory.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }

        copied.Add("cached assets");
    }
}
