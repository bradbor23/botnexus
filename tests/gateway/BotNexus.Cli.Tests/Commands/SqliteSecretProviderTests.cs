using BotNexus.Cli.Commands;
using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// The sqlite backend is the only one BotNexus owns end to end, so these cover both halves: the
/// CLI that writes the store and the provider that reads it.
/// </summary>
public sealed class SqliteSecretProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sqlite-secret-tests").FullName;

    private string StorePath => Path.Combine(_directory, SqliteSecretProvider.StoreFileName);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task StoreAsync(string name, string value)
    {
        await SecretCommand.EnsureStoreAsync(StorePath, CancellationToken.None);
        await using var connection = SqliteConnectionFactory.Create(
            new SqliteConnectionStringBuilder { DataSource = StorePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO secrets (name, value, updated_utc) VALUES ($n, $v, $u);";
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$v", value);
        command.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTheStoredValue()
    {
        await StoreAsync("proxmox", "s3cret");

        var secret = await new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:proxmox"));

        secret.Reveal().ShouldBe("s3cret");
    }

    // The remedy matters more than the diagnosis: an operator hitting this needs the command.
    [Fact]
    public async Task ResolveAsync_NoStore_SaysHowToCreateOne()
    {
        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:proxmox")));

        ex.Message.ShouldContain("botnexus secret set proxmox");
    }

    [Fact]
    public async Task ResolveAsync_NameNotPresent_SaysHowToAddIt()
    {
        await StoreAsync("other", "value");

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:proxmox")));

        ex.Message.ShouldContain("botnexus secret set proxmox");
    }

    [Fact]
    public async Task ResolveAsync_FailureMessage_NeverContainsAStoredValue()
    {
        const string Value = "a-real-looking-credential";
        await StoreAsync("present", Value);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:absent")));

        ex.Message.ShouldNotContain(Value);
    }

    [Fact]
    public async Task ResolveAsync_ArbitraryCharacters_RoundTrip()
    {
        const string Awkward = "p@ss w0rd!:/\\\"'{}\nnaïve-😀";
        await StoreAsync("awkward", Awkward);

        (await new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:awkward")))
            .Reveal().ShouldBe(Awkward);
    }

    [Fact]
    public void Scheme_IsSqlite() => new SqliteSecretProvider(StorePath).Scheme.ShouldBe("sqlite");

    // ── The CLI half ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureStoreAsync_RestrictsTheStoreToItsOwner()
    {
        await SecretCommand.EnsureStoreAsync(StorePath, CancellationToken.None);

        File.Exists(StorePath).ShouldBeTrue();
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(StorePath);
            mode.HasFlag(UnixFileMode.GroupRead).ShouldBeFalse();
            mode.HasFlag(UnixFileMode.OtherRead).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheSecret()
    {
        await StoreAsync("temporary", "value");

        (await SecretCommand.RemoveAsync(StorePath, "temporary", CancellationToken.None)).ShouldBe(0);

        await Should.ThrowAsync<SecretResolutionException>(
            () => new SqliteSecretProvider(StorePath).ResolveAsync(SecretRef.Parse("sqlite:temporary")));
    }

    [Fact]
    public async Task RemoveAsync_UnknownName_ReportsFailure()
    {
        await SecretCommand.EnsureStoreAsync(StorePath, CancellationToken.None);

        (await SecretCommand.RemoveAsync(StorePath, "never-existed", CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task ListAsync_EmptyStore_IsNotAnError()
    {
        await SecretCommand.EnsureStoreAsync(StorePath, CancellationToken.None);

        (await SecretCommand.ListAsync(StorePath, CancellationToken.None)).ShouldBe(0);
    }
}
