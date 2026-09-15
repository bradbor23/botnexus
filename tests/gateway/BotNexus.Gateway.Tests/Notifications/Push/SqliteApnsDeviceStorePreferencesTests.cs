using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Notifications.Push;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins how a device's notification level and its per-conversation levels are stored (#168).
/// </summary>
/// <remarks>
/// The level is a person's choice, and the one thing that must not happen to it is to be quietly
/// reset: an app re-registers its token on every launch, and a database written before levels
/// existed has to open with every device at the default rather than failing or losing devices.
/// </remarks>
public sealed class SqliteApnsDeviceStorePreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-apns-prefs", Guid.NewGuid().ToString("N"));

    private const string Token = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private string DbPath => Path.Combine(_dir, "apns.sqlite");

    public SqliteApnsDeviceStorePreferencesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DbPath);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private SqliteApnsDeviceStore Store() => new(DbPath);

    private async Task<SqliteApnsDeviceStore> StoreWithDevice()
    {
        var store = Store();
        await store.SaveAsync(new ApnsDevice { DeviceToken = Token, Environment = ApnsEnvironment.Production });

        return store;
    }

    [Fact]
    public async Task A_new_device_starts_at_the_default_level()
    {
        var store = await StoreWithDevice();

        var device = Assert.Single(await store.ListAsync());
        Assert.Equal(ApnsNotificationLevel.NeedsMe, device.Level);
        Assert.Empty(device.ConversationLevels);
    }

    // An app re-registers on every launch. If that reset the level, choosing one would last until
    // the next time the app opened.
    [Fact]
    public async Task Re_registering_keeps_the_level_a_person_chose()
    {
        var store = await StoreWithDevice();
        Assert.True(await store.SetLevelAsync(Token, ApnsNotificationLevel.NeedsMeAndReplies));

        await store.SaveAsync(new ApnsDevice { DeviceToken = Token, Environment = ApnsEnvironment.Production });

        Assert.Equal(ApnsNotificationLevel.NeedsMeAndReplies, Assert.Single(await store.ListAsync()).Level);
    }

    [Fact]
    public async Task Setting_a_level_for_an_unknown_device_creates_nothing()
    {
        var store = Store();

        Assert.False(await store.SetLevelAsync(Token, ApnsNotificationLevel.Off));
        Assert.Empty(await store.ListAsync());
        Assert.Null(await store.GetAsync(Token));
    }

    [Fact]
    public async Task A_conversation_level_is_kept_and_can_be_cleared()
    {
        var store = await StoreWithDevice();

        Assert.True(await store.SetConversationLevelAsync(Token, ConversationId.From("c1"), ApnsConversationLevel.Mute));
        var device = Assert.IsType<ApnsDevice>(await store.GetAsync(Token));
        Assert.Equal(ApnsConversationLevel.Mute, device.ConversationLevels["c1"]);

        Assert.True(await store.SetConversationLevelAsync(Token, ConversationId.From("c1"), level: null));
        Assert.Empty(Assert.IsType<ApnsDevice>(await store.GetAsync(Token)).ConversationLevels);
    }

    [Fact]
    public async Task A_conversation_level_for_an_unknown_device_is_refused()
    {
        var store = Store();

        Assert.False(await store.SetConversationLevelAsync(Token, ConversationId.From("c1"), ApnsConversationLevel.Mute));
    }

    // A token that comes back after the app was deleted and reinstalled is a new install, and should
    // not inherit conversations muted by the old one.
    [Fact]
    public async Task Forgetting_a_device_forgets_its_conversation_levels()
    {
        var store = await StoreWithDevice();
        await store.SetConversationLevelAsync(Token, ConversationId.From("c1"), ApnsConversationLevel.Mute);

        await store.RemoveAsync(Token);
        await store.SaveAsync(new ApnsDevice { DeviceToken = Token, Environment = ApnsEnvironment.Production });

        Assert.Empty(Assert.IsType<ApnsDevice>(await store.GetAsync(Token)).ConversationLevels);
    }

    // Every gateway that already has iOS devices has this file without the new column.
    [Fact]
    public async Task A_database_from_before_levels_opens_with_every_device_at_the_default()
    {
        await using (var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadWriteCreate"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE apns_devices (
                    device_token TEXT PRIMARY KEY,
                    environment TEXT NOT NULL,
                    device_name TEXT NULL,
                    created_at TEXT NOT NULL,
                    last_success_at TEXT NULL
                );
                INSERT INTO apns_devices (device_token, environment, device_name, created_at)
                VALUES ('{Token}', 'production', 'Old phone', '2026-09-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var device = Assert.Single(await Store().ListAsync());

        Assert.Equal("Old phone", device.DeviceName);
        Assert.Equal(ApnsNotificationLevel.NeedsMe, device.Level);
    }
}
