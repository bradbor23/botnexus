using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Api.Integrations;

/// <summary>
/// Caches MCP Registry answers for the Integrations page. Must be a singleton or it caches nothing.
/// </summary>
/// <remarks>
/// Two properties matter more than the hit rate. A registry outage serves the last good copy marked
/// stale instead of blanking a page the operator was already reading, and the caller's own
/// cancellation is never mistaken for an outage, so an aborted request cannot poison the log with a
/// registry failure that did not happen.
/// </remarks>
public sealed class McpRegistryCatalogCache
{
    /// <summary>How long an answer is served without asking the registry again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);

    /// <summary>
    /// Upper bound on cached answers. Search text is operator-typed, so without a bound every distinct
    /// keystroke-submitted query would be held for the life of the process.
    /// </summary>
    public const int MaxEntries = 256;

    private readonly IMcpRegistryClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Cached> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates the cache.</summary>
    public McpRegistryCatalogCache(
        IMcpRegistryClient client,
        TimeProvider? clock = null,
        ILogger<McpRegistryCatalogCache>? logger = null)
    {
        _client = client;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<McpRegistryCatalogCache>.Instance;
    }

    /// <summary>Searches the catalog.</summary>
    /// <returns><c>null</c> when the registry is unreachable and nothing is cached for this query.</returns>
    public Task<McpCatalogSnapshot<McpCatalogPage>?> SearchAsync(
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var trimmed = search?.Trim() ?? string.Empty;
        var key = $"search\n{trimmed.ToLowerInvariant()}\n{cursor}\n{limit}";
        return GetOrFetchAsync(key, "search", token => _client.SearchAsync(trimmed, cursor, limit, token), cancellationToken);
    }

    /// <summary>Looks up one server.</summary>
    /// <returns>
    /// <c>null</c> when the registry is unreachable and nothing is cached; otherwise a snapshot whose
    /// value is <c>null</c> when the registry lists no server by that name.
    /// </returns>
    public Task<McpCatalogSnapshot<McpCatalogEntry?>?> GetAsync(string name, CancellationToken cancellationToken) =>
        GetOrFetchAsync($"entry\n{name}", "entry", token => _client.GetLatestAsync(name, token), cancellationToken);

    private async Task<McpCatalogSnapshot<T>?> GetOrFetchAsync<T>(
        string key,
        string kind,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken)
    {
        _entries.TryGetValue(key, out var cached);
        if (cached is not null && _clock.GetUtcNow() - cached.FetchedAtUtc < FreshFor)
            return new McpCatalogSnapshot<T>((T)cached.Value!, cached.FetchedAtUtc, IsStale: false);

        try
        {
            var value = await fetch(cancellationToken);
            var fetchedAt = _clock.GetUtcNow();
            _entries[key] = new Cached(value, fetchedAt);
            TrimToCapacity();
            return new McpCatalogSnapshot<T>(value, fetchedAt, IsStale: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex, cancellationToken))
        {
            // The kind, not the key: the key carries operator-typed search text.
            _logger.LogWarning(
                ex,
                "MCP registry {Kind} request failed; serving {Outcome}.",
                kind,
                cached is null ? "nothing" : "the last good copy");

            return cached is null
                ? null
                : new McpCatalogSnapshot<T>((T)cached.Value!, cached.FetchedAtUtc, IsStale: true);
        }
    }

    private static bool IsRegistryFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or FormatException
        // HttpClient reports its own timeout as a cancellation. Only the caller's token is the caller.
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private void TrimToCapacity()
    {
        var excess = _entries.Count - MaxEntries;
        if (excess <= 0)
            return;

        foreach (var oldest in _entries.OrderBy(entry => entry.Value.FetchedAtUtc).Take(excess).ToArray())
            _entries.TryRemove(oldest.Key, out _);
    }

    private sealed record Cached(object? Value, DateTimeOffset FetchedAtUtc);
}
