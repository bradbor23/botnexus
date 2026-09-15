using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>A value a server asks the operator to supply: a header, environment variable or argument variable.</summary>
public sealed record IntegrationInputDto
{
    /// <summary>Header, variable or placeholder name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>What the value is for.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Whether the server needs it to start.</summary>
    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; init; }

    /// <summary>Whether it is a credential.</summary>
    [JsonPropertyName("isSecret")]
    public bool IsSecret { get; init; }
}

/// <summary>A hosted endpoint for a catalog server.</summary>
public sealed record IntegrationRemoteDto
{
    /// <summary>Transport, e.g. <c>streamable-http</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Endpoint URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Headers the endpoint expects.</summary>
    [JsonPropertyName("headers")]
    public List<IntegrationInputDto> Headers { get; init; } = [];
}

/// <summary>A locally-run package for a catalog server.</summary>
public sealed record IntegrationPackageDto
{
    /// <summary>Package ecosystem, e.g. <c>npm</c> or <c>oci</c>.</summary>
    [JsonPropertyName("registryType")]
    public string RegistryType { get; init; } = string.Empty;

    /// <summary>Package identifier.</summary>
    [JsonPropertyName("identifier")]
    public string Identifier { get; init; } = string.Empty;

    /// <summary>Package version, when listed separately.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Transport the package speaks, e.g. <c>stdio</c>.</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    /// <summary>Environment and argument variables the package reads.</summary>
    [JsonPropertyName("inputs")]
    public List<IntegrationInputDto> Inputs { get; init; } = [];
}

/// <summary>One MCP server from the registry catalog.</summary>
public sealed record IntegrationCatalogEntryDto
{
    /// <summary>Reverse-DNS registry name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Display title, when set.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>One-line description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Latest version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Source repository, when listed.</summary>
    [JsonPropertyName("repositoryUrl")]
    public string? RepositoryUrl { get; init; }

    /// <summary>Publisher website, when listed.</summary>
    [JsonPropertyName("websiteUrl")]
    public string? WebsiteUrl { get; init; }

    /// <summary>Hosted endpoints.</summary>
    [JsonPropertyName("remotes")]
    public List<IntegrationRemoteDto> Remotes { get; init; } = [];

    /// <summary>Local packages.</summary>
    [JsonPropertyName("packages")]
    public List<IntegrationPackageDto> Packages { get; init; } = [];

    /// <summary>Registry status, e.g. <c>active</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>When this version was published.</summary>
    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Whether any transport asks for a secret. Computed by the gateway.</summary>
    [JsonPropertyName("needsCredentials")]
    public bool NeedsCredentials { get; init; }
}

/// <summary>One page of catalog results.</summary>
public sealed record IntegrationCatalogPageDto
{
    /// <summary>Servers on this page.</summary>
    [JsonPropertyName("entries")]
    public List<IntegrationCatalogEntryDto> Entries { get; init; } = [];

    /// <summary>Cursor for the next page, or <c>null</c> on the last page.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    /// <summary>When the registry produced this answer.</summary>
    [JsonPropertyName("fetchedAtUtc")]
    public DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Whether this is the last good copy served during a registry outage.</summary>
    [JsonPropertyName("stale")]
    public bool Stale { get; init; }
}

/// <summary>How a catalog request ended.</summary>
public enum IntegrationLookupOutcome
{
    /// <summary>The gateway answered with a value.</summary>
    Ok = 0,

    /// <summary>The gateway lists nothing by that name.</summary>
    NotFound = 1,

    /// <summary>The gateway could not reach the registry and had nothing cached.</summary>
    Unavailable = 2,

    /// <summary>The gateway answered with an unexpected status.</summary>
    Failed = 3,
}

/// <summary>A catalog answer, or why there is none.</summary>
public sealed record IntegrationLookup<T>(T? Value, IntegrationLookupOutcome Outcome, string? Error)
    where T : class;

/// <summary>Client for the gateway's <c>/api/integrations</c> routes.</summary>
public sealed class IntegrationsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    public IntegrationsApiClient(HttpClient http) => _http = http;

    /// <summary>Searches the MCP registry catalog through the gateway.</summary>
    /// <exception cref="HttpRequestException">The gateway itself could not be reached.</exception>
    public Task<IntegrationLookup<IntegrationCatalogPageDto>> SearchAsync(string? search, string? cursor, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
            query.Add("search=" + Uri.EscapeDataString(search.Trim()));
        if (!string.IsNullOrEmpty(cursor))
            query.Add("cursor=" + Uri.EscapeDataString(cursor));

        var url = query.Count == 0
            ? "/api/integrations/catalog"
            : "/api/integrations/catalog?" + string.Join('&', query);

        return GetAsync<IntegrationCatalogPageDto>(url, ct);
    }

    private async Task<IntegrationLookup<T>> GetAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(null, IntegrationLookupOutcome.NotFound, await ReadErrorAsync(response, ct));
        if (response.StatusCode == HttpStatusCode.BadGateway)
            return new(null, IntegrationLookupOutcome.Unavailable, await ReadErrorAsync(response, ct));
        if (!response.IsSuccessStatusCode)
            return new(null, IntegrationLookupOutcome.Failed, await ReadErrorAsync(response, ct) ?? $"The gateway answered {(int)response.StatusCode}.");

        var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return body is null
            ? new(null, IntegrationLookupOutcome.Failed, "The gateway answered with an empty body.")
            : new(body, IntegrationLookupOutcome.Ok, null);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
