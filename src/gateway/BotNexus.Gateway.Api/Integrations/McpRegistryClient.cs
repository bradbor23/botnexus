using System.Globalization;
using System.Net;

namespace BotNexus.Gateway.Api.Integrations;

/// <summary>Reads the official MCP Registry.</summary>
public interface IMcpRegistryClient
{
    /// <summary>Searches the latest version of each listed server.</summary>
    /// <exception cref="HttpRequestException">The registry answered with a non-success status or could not be reached.</exception>
    /// <exception cref="FormatException">The registry answered with something that is not JSON.</exception>
    Task<McpCatalogPage> SearchAsync(string? search, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>Looks up the latest version of one server.</summary>
    /// <returns>The entry, or <c>null</c> when the registry lists no server by that name.</returns>
    Task<McpCatalogEntry?> GetLatestAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMcpRegistryClient"/> over the public registry API. The gateway is the only caller: the
/// portal never talks to the registry directly, so there is one egress point and one cache.
/// </summary>
public sealed class McpRegistryClient(IHttpClientFactory httpClientFactory) : IMcpRegistryClient
{
    /// <summary>Named <see cref="HttpClient"/> registration this client uses.</summary>
    public const string HttpClientName = "McpRegistry";

    /// <summary>Registry root. Paths below are relative to it.</summary>
    public static readonly Uri BaseAddress = new("https://registry.modelcontextprotocol.io/");

    /// <inheritdoc />
    public async Task<McpCatalogPage> SearchAsync(string? search, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            "version=latest",
            "limit=" + limit.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(search))
            query.Add("search=" + Uri.EscapeDataString(search.Trim()));
        if (!string.IsNullOrEmpty(cursor))
            query.Add("cursor=" + Uri.EscapeDataString(cursor));

        var json = await GetStringAsync("v0.1/servers?" + string.Join('&', query), allowNotFound: false, cancellationToken);
        return McpRegistryParser.ParsePage(json!);
    }

    /// <inheritdoc />
    public async Task<McpCatalogEntry?> GetLatestAsync(string name, CancellationToken cancellationToken)
    {
        // Names contain '/', so the whole name is one escaped path segment.
        var json = await GetStringAsync(
            $"v0.1/servers/{Uri.EscapeDataString(name)}/versions/latest",
            allowNotFound: true,
            cancellationToken);

        return json is null ? null : McpRegistryParser.ParseServerResponse(json);
    }

    private async Task<string?> GetStringAsync(string relativeUri, bool allowNotFound, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(new Uri(BaseAddress, relativeUri), cancellationToken);

        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The MCP registry answered {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
