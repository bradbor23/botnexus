namespace BotNexus.Gateway.Api.Integrations;

/// <summary>
/// One MCP server as listed in the official MCP Registry (<c>registry.modelcontextprotocol.io</c>),
/// reduced to what the portal Integrations page shows. Unknown registry fields are dropped at the
/// parser, so a newer registry schema adds nothing here rather than failing the whole page.
/// </summary>
/// <param name="Name">Reverse-DNS registry name, e.g. <c>io.github.github/github-mcp-server</c>.</param>
/// <param name="Title">Display title, when the publisher set one.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Version">The version this entry describes.</param>
/// <param name="RepositoryUrl">Source repository, when listed.</param>
/// <param name="WebsiteUrl">Publisher website, when listed.</param>
/// <param name="Remotes">Hosted endpoints: a URL the gateway would connect to, running nothing locally.</param>
/// <param name="Packages">Local packages: something the gateway host would have to run.</param>
/// <param name="Status">Registry status (<c>active</c>, <c>deprecated</c>, ...), when the registry reported one.</param>
/// <param name="IsLatest">Whether this is the latest published version.</param>
/// <param name="PublishedAt">When this version was published.</param>
public sealed record McpCatalogEntry(
    string Name,
    string? Title,
    string? Description,
    string? Version,
    string? RepositoryUrl,
    string? WebsiteUrl,
    IReadOnlyList<McpCatalogRemote> Remotes,
    IReadOnlyList<McpCatalogPackage> Packages,
    string? Status,
    bool IsLatest,
    DateTimeOffset? PublishedAt)
{
    /// <summary>
    /// Whether any transport asks for a secret value. Computed here rather than in the portal so every
    /// client answers the question the same way.
    /// </summary>
    public bool NeedsCredentials =>
        Remotes.Any(remote => remote.Headers.Any(header => header.IsSecret))
        || Packages.Any(package => package.Inputs.Any(input => input.IsSecret));
}

/// <summary>A value the operator would have to supply: an HTTP header, environment variable or argument variable.</summary>
public sealed record McpCatalogInput(string Name, string? Description, bool IsRequired, bool IsSecret);

/// <summary>A hosted endpoint for a server.</summary>
/// <param name="Type">Transport, e.g. <c>streamable-http</c> or <c>sse</c>.</param>
/// <param name="Url">Endpoint URL.</param>
/// <param name="Headers">Headers the endpoint expects.</param>
public sealed record McpCatalogRemote(string Type, string Url, IReadOnlyList<McpCatalogInput> Headers);

/// <summary>A locally-run package for a server.</summary>
/// <param name="RegistryType">Package ecosystem, e.g. <c>npm</c>, <c>pypi</c> or <c>oci</c>.</param>
/// <param name="Identifier">Package identifier within that ecosystem.</param>
/// <param name="Version">Package version, when listed separately from the identifier.</param>
/// <param name="Transport">Transport the running package speaks, e.g. <c>stdio</c>.</param>
/// <param name="Inputs">Environment variables and argument variables the package reads.</param>
public sealed record McpCatalogPackage(
    string RegistryType,
    string Identifier,
    string? Version,
    string? Transport,
    IReadOnlyList<McpCatalogInput> Inputs);

/// <summary>One page of registry search results.</summary>
public sealed record McpCatalogPage(IReadOnlyList<McpCatalogEntry> Entries, string? NextCursor);

/// <summary>A cached registry answer and whether it is being served past its freshness window.</summary>
/// <param name="Value">The answer.</param>
/// <param name="FetchedAtUtc">When the registry produced it.</param>
/// <param name="IsStale">
/// <c>true</c> when the registry could not be reached and this is the last good copy.
/// </param>
public sealed record McpCatalogSnapshot<T>(T Value, DateTimeOffset FetchedAtUtc, bool IsStale);

/// <summary>Response body for <c>GET /api/integrations/catalog</c>.</summary>
public sealed record IntegrationCatalogResponse(
    IReadOnlyList<McpCatalogEntry> Entries,
    string? NextCursor,
    DateTimeOffset FetchedAtUtc,
    bool Stale);

/// <summary>Response body for <c>GET /api/integrations/catalog/entry</c>.</summary>
public sealed record IntegrationCatalogEntryResponse(
    McpCatalogEntry Entry,
    DateTimeOffset FetchedAtUtc,
    bool Stale);
