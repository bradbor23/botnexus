using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Api.Integrations;

/// <summary>
/// Tolerant parser for MCP Registry responses. The registry serves entries written against several
/// <c>server.schema.json</c> revisions at once (2025-09-16, 2025-09-29 and 2025-12-11 were all live on
/// 2026-09-14), so this reads by field name and ignores anything it does not know. A malformed entry
/// is skipped; only a response that is not JSON at all is an error.
/// </summary>
public static class McpRegistryParser
{
    private const string OfficialMetaKey = "io.modelcontextprotocol.registry/official";

    /// <summary>Parses a <c>GET /v0.1/servers</c> page.</summary>
    /// <exception cref="FormatException">The body is not a JSON object.</exception>
    public static McpCatalogPage ParsePage(string json)
    {
        // Not named "root": ConfigPathFence reads a literal indexer on a root-named local as a
        // PlatformConfig path, and this is a registry payload, not configuration.
        var response = ParseObject(json);

        var entries = Objects(response["servers"])
            .Select(ParseEntry)
            .OfType<McpCatalogEntry>()
            .ToArray();

        var cursor = response["metadata"] is JsonObject metadata ? Str(metadata, "nextCursor") : null;
        return new McpCatalogPage(entries, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    /// <summary>Parses a <c>GET /v0.1/servers/{name}/versions/{version}</c> body.</summary>
    /// <returns>The entry, or <c>null</c> when the body carries no named server.</returns>
    /// <exception cref="FormatException">The body is not a JSON object.</exception>
    public static McpCatalogEntry? ParseServerResponse(string json) => ParseEntry(ParseObject(json));

    private static JsonObject ParseObject(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The MCP registry returned malformed JSON.", ex);
        }

        return node as JsonObject
            ?? throw new FormatException("The MCP registry response was not a JSON object.");
    }

    private static McpCatalogEntry? ParseEntry(JsonObject wrapper)
    {
        // Each result is wrapped as { server, _meta }. A bare server object is accepted too, so a
        // registry that drops the wrapper degrades to "no status" rather than to "no entries".
        var server = wrapper["server"] as JsonObject ?? wrapper;
        var name = Str(server, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var official = (wrapper["_meta"] as JsonObject)?[OfficialMetaKey] as JsonObject;

        return new McpCatalogEntry(
            Name: name,
            Title: Str(server, "title"),
            Description: Str(server, "description"),
            Version: Str(server, "version"),
            RepositoryUrl: server["repository"] is JsonObject repository ? Str(repository, "url") : null,
            WebsiteUrl: Str(server, "websiteUrl"),
            Remotes: Objects(server["remotes"]).Select(ParseRemote).OfType<McpCatalogRemote>().ToArray(),
            Packages: Objects(server["packages"]).Select(ParsePackage).OfType<McpCatalogPackage>().ToArray(),
            Status: official is null ? null : Str(official, "status"),
            IsLatest: official is not null && Bool(official, "isLatest"),
            PublishedAt: official is null ? null : Date(official, "publishedAt"));
    }

    private static McpCatalogRemote? ParseRemote(JsonObject remote)
    {
        var url = Str(remote, "url");
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return new McpCatalogRemote(
            Type: Str(remote, "type") ?? "unknown",
            Url: url,
            Headers: Objects(remote["headers"]).Select(ParseInput).OfType<McpCatalogInput>().ToArray());
    }

    private static McpCatalogPackage? ParsePackage(JsonObject package)
    {
        var identifier = Str(package, "identifier");
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var inputs = Objects(package["environmentVariables"])
            .Select(ParseInput)
            .OfType<McpCatalogInput>()
            .Concat(ArgumentVariables(package["runtimeArguments"]))
            .Concat(ArgumentVariables(package["packageArguments"]))
            .ToArray();

        return new McpCatalogPackage(
            RegistryType: Str(package, "registryType") ?? "unknown",
            Identifier: identifier,
            Version: Str(package, "version"),
            Transport: package["transport"] is JsonObject transport ? Str(transport, "type") : null,
            Inputs: inputs);
    }

    private static McpCatalogInput? ParseInput(JsonObject input)
    {
        var name = Str(input, "name");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new McpCatalogInput(name, Str(input, "description"), Bool(input, "isRequired"), Bool(input, "isSecret"));
    }

    // An argument such as "-e GITHUB_PERSONAL_ACCESS_TOKEN={token}" declares its placeholders under
    // "variables". A secret hidden there is still a secret the operator must supply, so it counts.
    private static IEnumerable<McpCatalogInput> ArgumentVariables(JsonNode? arguments)
    {
        foreach (var argument in Objects(arguments))
        {
            if (argument["variables"] is not JsonObject variables)
                continue;

            foreach (var (name, value) in variables)
            {
                if (value is not JsonObject variable || string.IsNullOrWhiteSpace(name))
                    continue;

                yield return new McpCatalogInput(
                    name,
                    Str(variable, "description") ?? Str(argument, "description"),
                    Bool(variable, "isRequired"),
                    Bool(variable, "isSecret"));
            }
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static DateTimeOffset? Date(JsonObject obj, string key) =>
        DateTimeOffset.TryParse(
            Str(obj, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
