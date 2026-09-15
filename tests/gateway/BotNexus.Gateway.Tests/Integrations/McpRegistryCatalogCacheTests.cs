using BotNexus.Gateway.Api.Integrations;

namespace BotNexus.Gateway.Tests.Integrations;

/// <summary>
/// Pins the cache's freshness window, its stale-copy fallback, its bound, and that the caller's own
/// cancellation is never treated as a registry outage.
/// </summary>
public sealed class McpRegistryCatalogCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 17, 0, 0, TimeSpan.Zero);

    private readonly FakeRegistry _registry = new();
    private readonly ManualClock _clock = new() { Now = Start };

    private McpRegistryCatalogCache CreateCache() => new(_registry, _clock);

    [Fact]
    public async Task A_repeat_query_inside_the_freshness_window_does_not_ask_the_registry_again()
    {
        var cache = CreateCache();

        await cache.SearchAsync("github", null, 30, CancellationToken.None);
        _clock.Now = Start + McpRegistryCatalogCache.FreshFor - TimeSpan.FromSeconds(1);
        var second = await cache.SearchAsync("  GitHub ", null, 30, CancellationToken.None);

        Assert.Equal(1, _registry.SearchCalls);
        Assert.NotNull(second);
        Assert.False(second.IsStale);
        Assert.Equal(Start, second.FetchedAtUtc);
    }

    [Fact]
    public async Task Different_cursors_and_limits_are_different_answers()
    {
        var cache = CreateCache();

        await cache.SearchAsync("github", null, 30, CancellationToken.None);
        await cache.SearchAsync("github", "cursor-2", 30, CancellationToken.None);
        await cache.SearchAsync("github", null, 10, CancellationToken.None);

        Assert.Equal(3, _registry.SearchCalls);
    }

    [Fact]
    public async Task An_expired_answer_is_refetched()
    {
        var cache = CreateCache();

        await cache.SearchAsync("github", null, 30, CancellationToken.None);
        _clock.Now = Start + McpRegistryCatalogCache.FreshFor;
        var refreshed = await cache.SearchAsync("github", null, 30, CancellationToken.None);

        Assert.Equal(2, _registry.SearchCalls);
        Assert.Equal(_clock.Now, refreshed!.FetchedAtUtc);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("format")]
    [InlineData("timeout")]
    public async Task A_registry_failure_after_expiry_serves_the_last_good_copy_marked_stale(string failure)
    {
        var cache = CreateCache();
        await cache.SearchAsync("github", null, 30, CancellationToken.None);

        _clock.Now = Start + TimeSpan.FromHours(3);
        _registry.Failure = failure switch
        {
            "http" => new HttpRequestException("503"),
            "format" => new FormatException("not json"),
            _ => new TaskCanceledException("HttpClient.Timeout elapsed"),
        };
        var stale = await cache.SearchAsync("github", null, 30, CancellationToken.None);

        Assert.NotNull(stale);
        Assert.True(stale.IsStale);
        Assert.Equal(Start, stale.FetchedAtUtc);
        Assert.Equal("com.example/github", Assert.Single(stale.Value.Entries).Name);
    }

    [Fact]
    public async Task A_registry_failure_with_nothing_cached_is_null()
    {
        _registry.Failure = new HttpRequestException("unreachable");

        Assert.Null(await CreateCache().SearchAsync("github", null, 30, CancellationToken.None));
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_rather_than_reading_as_an_outage()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _registry.Failure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateCache().SearchAsync("github", null, 30, cts.Token));
    }

    [Fact]
    public async Task An_unexpected_exception_is_not_swallowed()
    {
        _registry.Failure = new InvalidOperationException("a bug, not an outage");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCache().SearchAsync("github", null, 30, CancellationToken.None));
    }

    [Fact]
    public async Task The_cache_is_bounded_and_evicts_the_oldest_answer_first()
    {
        var cache = CreateCache();

        for (var i = 0; i <= McpRegistryCatalogCache.MaxEntries; i++)
        {
            _clock.Now = Start + TimeSpan.FromMilliseconds(i);
            await cache.SearchAsync($"query-{i}", null, 30, CancellationToken.None);
        }

        var callsBefore = _registry.SearchCalls;
        await cache.SearchAsync($"query-{McpRegistryCatalogCache.MaxEntries}", null, 30, CancellationToken.None);
        Assert.Equal(callsBefore, _registry.SearchCalls);

        await cache.SearchAsync("query-0", null, 30, CancellationToken.None);
        Assert.Equal(callsBefore + 1, _registry.SearchCalls);
    }

    [Fact]
    public async Task An_unlisted_name_is_a_snapshot_with_no_value_and_an_outage_is_null()
    {
        var cache = CreateCache();

        var missing = await cache.GetAsync("com.example/missing", CancellationToken.None);
        Assert.NotNull(missing);
        Assert.Null(missing.Value);

        _registry.Failure = new HttpRequestException("unreachable");
        Assert.Null(await cache.GetAsync("com.example/other", CancellationToken.None));
    }

    private sealed class FakeRegistry : IMcpRegistryClient
    {
        public int SearchCalls { get; private set; }

        public Exception? Failure { get; set; }

        public Task<McpCatalogPage> SearchAsync(string? search, string? cursor, int limit, CancellationToken cancellationToken)
        {
            SearchCalls++;
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new McpCatalogPage([Entry($"com.example/{search}")], NextCursor: null));
        }

        public Task<McpCatalogEntry?> GetLatestAsync(string name, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                throw Failure;

            return Task.FromResult<McpCatalogEntry?>(name.EndsWith("/missing", StringComparison.Ordinal) ? null : Entry(name));
        }

        private static McpCatalogEntry Entry(string name) =>
            new(name, null, null, "1.0.0", null, null, [], [], "active", true, null);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
