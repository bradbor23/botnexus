using BotNexus.Domain.Primitives;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// SQLite-backed <see cref="IApnsDeviceStore"/>, beside the web push subscriptions.
/// </summary>
/// <remarks>
/// Its own file for the same reason web push has one: these are long-lived device registrations
/// whose loss silently ends delivery to a phone that has no way to find out, and they must not be
/// collateral damage when a transient store is cleared.
/// </remarks>
public sealed class SqliteApnsDeviceStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    TimeProvider? timeProvider = null) : IApnsDeviceStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS apns_devices (
                    device_token TEXT PRIMARY KEY,
                    environment TEXT NOT NULL,
                    device_name TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_success_at TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS apns_conversation_levels (
                    device_token TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    level TEXT NOT NULL,
                    PRIMARY KEY (device_token, conversation_id)
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // #168. Every gateway that already has iOS devices has this table without the column, and
            // each of those devices starts at the default - which is exactly what it was receiving.
            await EnsureColumnAsync(
                connection,
                "notification_level",
                "ALTER TABLE apns_devices ADD COLUMN notification_level TEXT NOT NULL DEFAULT 'needsMe';",
                ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ApnsDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // created_at is kept across a re-register: iOS re-issues tokens on its own schedule,
            // and the same token arriving again is the same install continuing, not a new device.
            command.CommandText = """
                INSERT INTO apns_devices (device_token, environment, device_name, created_at)
                VALUES ($deviceToken, $environment, $deviceName, $createdAt)
                ON CONFLICT(device_token) DO UPDATE SET
                    environment = excluded.environment,
                    device_name = excluded.device_name;
                """;
            command.Parameters.AddWithValue("$deviceToken", device.DeviceToken);
            command.Parameters.AddWithValue("$environment", device.Environment);
            command.Parameters.AddWithValue("$deviceName", (object?)device.DeviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", _timeProvider.GetUtcNow().ToString("O"));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApnsDevice>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM apns_devices ORDER BY created_at;";

        var results = new List<ApnsDevice>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new ApnsDevice
                {
                    DeviceToken = reader.GetString(reader.GetOrdinal("device_token")),
                    Environment = reader.GetString(reader.GetOrdinal("environment")),
                    DeviceName = Nullable(reader, "device_name"),
                    CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                    LastSuccessAtUtc = Nullable(reader, "last_success_at") is { } last
                        ? DateTimeOffset.Parse(last)
                        : null,
                    Level = ApnsLevelNames.TryParseDevice(Nullable(reader, "notification_level"), out var level)
                        ? level
                        : ApnsNotificationLevel.NeedsMe,
                });
            }
        }

        if (results.Count == 0)
            return results;

        // Every device's conversation levels in one read. A gateway has a handful of phones, and a
        // push is decided per device, so they are loaded with the devices rather than per notification.
        var conversations = new Dictionary<string, Dictionary<string, ApnsConversationLevel>>(StringComparer.Ordinal);
        await using var levels = connection.CreateCommand();
        levels.CommandText = "SELECT device_token, conversation_id, level FROM apns_conversation_levels;";
        await using (var reader = await levels.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!ApnsLevelNames.TryParseConversation(reader.GetString(2), out var level))
                    continue;

                var token = reader.GetString(0);
                if (!conversations.TryGetValue(token, out var byConversation))
                    conversations[token] = byConversation = new Dictionary<string, ApnsConversationLevel>(StringComparer.Ordinal);

                byConversation[reader.GetString(1)] = level;
            }
        }

        return results
            .Select(device => conversations.TryGetValue(device.DeviceToken, out var byConversation)
                ? device with { ConversationLevels = byConversation }
                : device)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ApnsDevice?> GetAsync(string deviceToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return null;

        var devices = await ListAsync(ct).ConfigureAwait(false);

        return devices.FirstOrDefault(d => string.Equals(d.DeviceToken, deviceToken, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<bool> SetLevelAsync(string deviceToken, ApnsNotificationLevel level, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return false;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE apns_devices SET notification_level = $level WHERE device_token = $deviceToken;";
            command.Parameters.AddWithValue("$level", ApnsLevelNames.ToWire(level));
            command.Parameters.AddWithValue("$deviceToken", deviceToken);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetConversationLevelAsync(
        string deviceToken,
        ConversationId conversationId,
        ApnsConversationLevel? level,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken) || !conversationId.IsInitialized())
            return false;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // A level for a device that is not registered would be an orphan row nothing reads.
            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM apns_devices WHERE device_token = $deviceToken;";
                exists.Parameters.AddWithValue("$deviceToken", deviceToken);
                if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                    return false;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = level is { } value
                ? """
                  INSERT INTO apns_conversation_levels (device_token, conversation_id, level)
                  VALUES ($deviceToken, $conversationId, $level)
                  ON CONFLICT(device_token, conversation_id) DO UPDATE SET level = excluded.level;
                  """
                : "DELETE FROM apns_conversation_levels WHERE device_token = $deviceToken AND conversation_id = $conversationId;";
            command.Parameters.AddWithValue("$deviceToken", deviceToken);
            command.Parameters.AddWithValue("$conversationId", conversationId.Value);
            if (level is { } set)
                command.Parameters.AddWithValue("$level", ApnsLevelNames.ToWire(set));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string deviceToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return false;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            // A token that comes back after the app was deleted and reinstalled is a new install, and
            // must not inherit conversations the old one muted.
            await using (var levels = connection.CreateCommand())
            {
                levels.CommandText = "DELETE FROM apns_conversation_levels WHERE device_token = $deviceToken;";
                levels.Parameters.AddWithValue("$deviceToken", deviceToken);
                await levels.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM apns_devices WHERE device_token = $deviceToken;";
            command.Parameters.AddWithValue("$deviceToken", deviceToken);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkDeliveredAsync(string deviceToken, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE apns_devices SET last_success_at = $at WHERE device_token = $deviceToken;";
            command.Parameters.AddWithValue("$at", _timeProvider.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$deviceToken", deviceToken);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Adds a column to <c>apns_devices</c> when a database written before it existed does not have it.
    /// </summary>
    /// <remarks>
    /// Same shape and reasoning as <c>SqliteConfigStore.EnsureColumnAsync</c>: the write lock only
    /// serialises within one process, so two gateways opening the same old database can race the
    /// PRAGMA-then-ALTER, and the loser's "duplicate column name" is swallowed.
    /// </remarks>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string column,
        string ddl,
        CancellationToken ct)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(apns_devices);";

        await using (var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1
            && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Another process added it first.
        }
    }

    private static string? Nullable(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);
}
