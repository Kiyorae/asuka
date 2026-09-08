using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Asuka.Core;

[Flags]
public enum StoreChangeKind
{
    None = 0,
    Users = 1 << 0,
    Groups = 1 << 1,
    Members = 1 << 2,
    Messages = 1 << 3,
    Requests = 1 << 4,
    Conversations = 1 << 5,
    Assets = 1 << 6,
    Friendships = 1 << 7,
    Files = 1 << 8,
    Notifications = 1 << 9,
    CustomFaces = 1 << 10,
    All = Users | Groups | Members | Messages | Requests | Conversations | Assets | Friendships | Files | Notifications | CustomFaces,
}

public sealed class StoreChangedEventArgs(StoreChangeKind changes) : EventArgs
{
    public StoreChangeKind Changes { get; } = changes;
}

public sealed partial class AsukaStore : IDisposable, IAsyncDisposable
{
    internal const int CurrentSchemaVersion = 12;
    private static readonly JsonSerializerOptions PersistenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _observerSync = new();
    private readonly Dictionary<long, StoreObserver> _observers = [];
    private long _nextObserverId;
    private volatile bool _disposed;

    public AsukaStore()
        : this(null)
    {
    }

    public AsukaStore(string? path)
    {
        IsInMemory = string.IsNullOrWhiteSpace(path);
        DatabasePath = IsInMemory ? ":memory:" : Path.GetFullPath(path!);
        if (!IsInMemory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)
                ?? throw new ArgumentException("The database path has no parent directory.", nameof(path)));
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = IsInMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            Initialize();
        }
        catch
        {
            _connection.Dispose();
            _gate.Dispose();
            throw;
        }
    }

    public string DatabasePath { get; }
    public bool IsInMemory { get; }

    public event EventHandler<StoreChangedEventArgs>? Changed;

    public static AsukaStore DefaultLocation()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AsukaStore(Path.Combine(root, "Asuka", "Data", "asuka.sqlite3"));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompleteObservers();
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompleteObservers();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Initialize()
    {
        using (var pragmas = _connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragmas.ExecuteNonQuery();
        }

        if (!IsInMemory)
        {
            using var wal = _connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            _ = wal.ExecuteScalar();
        }

        using var versionCommand = _connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new StorePersistenceException(
                $"Database schema version {version} is newer than this build supports ({CurrentSchemaVersion}).");
        }

        if (version < 1)
        {
            ApplyVersion1();
        }

        if (version < 2)
        {
            ApplyVersion2();
        }

        if (version < 3)
        {
            ApplyVersion3();
        }

        if (version < 4)
        {
            ApplyVersion4();
        }

        if (version < 5)
        {
            ApplyVersion5();
        }

        if (version < 6) ApplyVersion6();
        if (version < 7) ApplyVersion7();
        if (version < 8) ApplyVersion8();
        if (version < 9) ApplyVersion9();
        if (version < 10) ApplyVersion10();
        if (version < 11) ApplyVersion11();
        if (version < 12) ApplyVersion12();
    }

    private void ApplyVersion1()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                nickname TEXT NOT NULL,
                avatar TEXT NULL,
                sex TEXT NOT NULL,
                age INTEGER NULL,
                sign TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_users_created_at ON users(created_at);

            CREATE TABLE IF NOT EXISTS groups (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                avatar TEXT NULL,
                intro TEXT NOT NULL,
                level INTEGER NOT NULL,
                max_member_count INTEGER NOT NULL,
                whole_muted INTEGER NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_groups_created_at ON groups(created_at);

            CREATE TABLE IF NOT EXISTS group_members (
                group_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                card TEXT NOT NULL,
                role TEXT NOT NULL,
                title TEXT NOT NULL,
                joined_at INTEGER NOT NULL,
                last_sent_at INTEGER NULL,
                muted_until INTEGER NULL,
                PRIMARY KEY(group_id, user_id),
                FOREIGN KEY(group_id) REFERENCES groups(id) ON DELETE CASCADE,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_members_group_joined ON group_members(group_id, joined_at);
            CREATE INDEX IF NOT EXISTS ix_members_user ON group_members(user_id);

            CREATE TABLE IF NOT EXISTS friendships (
                user_id TEXT NOT NULL,
                friend_id TEXT NOT NULL,
                remark TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY(user_id, friend_id),
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
                FOREIGN KEY(friend_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_friendships_user_created ON friendships(user_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_friendships_friend ON friendships(friend_id);

            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY NOT NULL,
                seq INTEGER NOT NULL,
                scene TEXT NOT NULL,
                peer_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                self_id TEXT NOT NULL,
                content TEXT NOT NULL,
                time INTEGER NOT NULL,
                direction TEXT NOT NULL,
                recalled_at INTEGER NULL,
                recalled_by TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_chat_seq ON messages(scene, peer_id, self_id, seq);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_chat_seq
                ON messages(scene, peer_id, self_id, seq) WHERE seq > 0;
            CREATE INDEX IF NOT EXISTS ix_messages_self_time ON messages(self_id, time);
            CREATE INDEX IF NOT EXISTS ix_messages_time ON messages(time);

            CREATE TABLE IF NOT EXISTS pending_requests (
                id TEXT PRIMARY KEY NOT NULL,
                flag TEXT NOT NULL,
                kind TEXT NOT NULL,
                requester_id TEXT NOT NULL,
                group_id TEXT NULL,
                self_id TEXT NOT NULL,
                comment TEXT NOT NULL,
                time INTEGER NOT NULL,
                resolution_state TEXT NULL,
                resolution_reason TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_requests_flag ON pending_requests(flag);
            CREATE INDEX IF NOT EXISTS ix_requests_self_time ON pending_requests(self_id, time);

            CREATE TABLE IF NOT EXISTS assets (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                mime_type TEXT NULL,
                byte_count INTEGER NOT NULL,
                source_kind TEXT NOT NULL,
                source_value TEXT NULL
            );

            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void ApplyVersion2()
    {
        // V1 used Unix milliseconds. .NET ticks preserve DateTimeOffset's full
        // 100-nanosecond precision, avoiding values changing after a round trip.
        const long unixEpochTicks = 621_355_968_000_000_000L;
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE users SET created_at=(created_at * 10000) + {unixEpochTicks};
            UPDATE groups SET created_at=(created_at * 10000) + {unixEpochTicks};
            UPDATE group_members SET
                joined_at=(joined_at * 10000) + {unixEpochTicks},
                last_sent_at=CASE WHEN last_sent_at IS NULL THEN NULL ELSE (last_sent_at * 10000) + {unixEpochTicks} END,
                muted_until=CASE WHEN muted_until IS NULL THEN NULL ELSE (muted_until * 10000) + {unixEpochTicks} END;
            UPDATE friendships SET created_at=(created_at * 10000) + {unixEpochTicks};
            UPDATE messages SET
                time=(time * 10000) + {unixEpochTicks},
                recalled_at=CASE WHEN recalled_at IS NULL THEN NULL ELSE (recalled_at * 10000) + {unixEpochTicks} END;
            UPDATE pending_requests SET time=(time * 10000) + {unixEpochTicks};
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void ApplyVersion3()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE message_reactions (
                message_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                reaction TEXT NOT NULL,
                reaction_type TEXT NOT NULL,
                PRIMARY KEY(message_id, user_id, reaction_type, reaction),
                FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE INDEX ix_message_reactions_user ON message_reactions(user_id);
            CREATE TRIGGER clear_recalled_message_reactions
                AFTER UPDATE OF recalled_at ON messages
                WHEN NEW.recalled_at IS NOT NULL
            BEGIN
                DELETE FROM message_reactions WHERE message_id=NEW.id;
            END;
            PRAGMA user_version=3;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private async Task<T> WithGateAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithGateAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await WithGateAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WriteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        StoreChangeKind changes,
        CancellationToken cancellationToken)
    {
        var result = await WithGateAsync(operation, cancellationToken).ConfigureAwait(false);
        Publish(changes);
        return result;
    }

    private async Task WriteAsync(
        Func<CancellationToken, Task> operation,
        StoreChangeKind changes,
        CancellationToken cancellationToken)
    {
        await WithGateAsync(operation, cancellationToken).ConfigureAwait(false);
        Publish(changes);
    }

    private void Publish(StoreChangeKind changes)
    {
        List<ChannelWriter<bool>> writers;
        lock (_observerSync)
        {
            writers = _observers.Values
                .Where(observer => (observer.Interests & changes) != StoreChangeKind.None)
                .Select(observer => observer.Channel.Writer)
                .ToList();
        }

        foreach (var writer in writers)
        {
            _ = writer.TryWrite(true);
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new StoreChangedEventArgs(changes);
        foreach (EventHandler<StoreChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Observers must not make a committed store write appear to fail.
            }
        }
    }

    private async IAsyncEnumerable<T> ObserveAsync<T>(
        StoreChangeKind interests,
        Func<CancellationToken, Task<T>> fetch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var observer = new StoreObserver(interests);
        long id;
        lock (_observerSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextObserverId;
            _observers.Add(id, observer);
        }

        _ = observer.Channel.Writer.TryWrite(true);
        try
        {
            await foreach (var _ in observer.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return await fetch(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_observerSync)
            {
                _observers.Remove(id);
            }

            observer.Channel.Writer.TryComplete();
        }
    }

    private void CompleteObservers()
    {
        List<StoreObserver> observers;
        lock (_observerSync)
        {
            observers = _observers.Values.ToList();
            _observers.Clear();
        }

        foreach (var observer in observers)
        {
            observer.Channel.Writer.TryComplete();
        }
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static long ToTimestamp(DateTimeOffset value) => value.UtcTicks;
    private static DateTimeOffset FromTimestamp(long value) => new(value, TimeSpan.Zero);
    private static long? ToTimestamp(DateTimeOffset? value) => value?.UtcTicks;
    private static DateTimeOffset? GetOptionalTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromTimestamp(reader.GetInt64(ordinal));

    private static string SerializeContent(IReadOnlyList<MessageSegment> content) =>
        JsonSerializer.Serialize(content, PersistenceJsonOptions);

    private static IReadOnlyList<MessageSegment> DeserializeContent(string content) =>
        JsonSerializer.Deserialize<IReadOnlyList<MessageSegment>>(content, PersistenceJsonOptions)
        ?? throw new StorePersistenceException("A message has invalid content JSON.");

    private sealed record StoreObserver(StoreChangeKind Interests)
    {
        public Channel<bool> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }
}
