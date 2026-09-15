using BotNexus.Gateway.Api.Integrations;

namespace BotNexus.Gateway.Tests.Integrations;

/// <summary>
/// Pins the registry parser against response shapes captured from the live registry on 2026-09-14.
/// The registry serves several schema revisions side by side, so tolerance is the contract: an
/// unknown field or a half-filled entry must never cost the operator the rest of the page.
/// </summary>
public sealed class McpRegistryParserTests
{
    // Shape of a hosted aggregator entry: remote only, a secret header, schema 2025-09-29.
    private const string RemoteEntry = """
        {
          "server": {
            "$schema": "https://static.modelcontextprotocol.io/schemas/2025-09-29/server.schema.json",
            "name": "ai.smithery/smithery-ai-github",
            "description": "Access the GitHub API.",
            "repository": { "url": "https://github.com/smithery-ai/mcp-servers", "source": "github", "subfolder": "github" },
            "version": "1.0.0",
            "remotes": [
              {
                "type": "streamable-http",
                "url": "https://server.smithery.ai/@smithery-ai/github/mcp",
                "headers": [
                  { "description": "Bearer token", "isRequired": true, "value": "Bearer {smithery_api_key}", "isSecret": true, "name": "Authorization" }
                ]
              }
            ]
          },
          "_meta": {
            "io.modelcontextprotocol.registry/official": {
              "status": "active", "publishedAt": "2026-04-13T17:32:20.852269Z", "isLatest": true
            }
          }
        }
        """;

    // Shape of the GitHub entry: an OCI package whose only secret is an argument variable, plus a remote.
    private const string PackageEntry = """
        {
          "server": {
            "$schema": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
            "name": "io.github.github/github-mcp-server",
            "title": "GitHub",
            "version": "1.12.1",
            "packages": [
              {
                "registryType": "oci",
                "identifier": "ghcr.io/github/github-mcp-server:1.12.1",
                "transport": { "type": "stdio" },
                "runtimeArguments": [
                  { "value": "127.0.0.1:8085:8085", "type": "named", "name": "-p" },
                  {
                    "description": "Optional GitHub Personal Access Token.",
                    "value": "GITHUB_PERSONAL_ACCESS_TOKEN={token}",
                    "variables": { "token": { "format": "string", "isSecret": true } },
                    "type": "named", "name": "-e"
                  }
                ]
              },
              {
                "registryType": "npm",
                "identifier": "remote-filesystem-mcp-server",
                "version": "0.1.5",
                "transport": { "type": "stdio" },
                "environmentVariables": [
                  { "description": "Bucket name.", "isRequired": true, "name": "GCS_BUCKET" },
                  { "description": "Private key.", "isSecret": true, "name": "GCS_PRIVATE_KEY" }
                ]
              }
            ],
            "remotes": [
              { "type": "streamable-http", "url": "https://api.githubcopilot.com/mcp/", "headers": [] }
            ]
          },
          "_meta": { "io.modelcontextprotocol.registry/official": { "status": "active", "isLatest": true } }
        }
        """;

    [Fact]
    public void Parses_a_remote_entry_with_its_secret_header()
    {
        var page = McpRegistryParser.ParsePage($$"""{ "servers": [ {{RemoteEntry}} ], "metadata": { "nextCursor": "ai.smithery/smithery-ai-github:1.0.0", "count": 1 } }""");

        var entry = Assert.Single(page.Entries);
        Assert.Equal("ai.smithery/smithery-ai-github", entry.Name);
        Assert.Null(entry.Title);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("https://github.com/smithery-ai/mcp-servers", entry.RepositoryUrl);
        Assert.Equal("active", entry.Status);
        Assert.True(entry.IsLatest);
        Assert.Equal(new DateTimeOffset(2026, 4, 13, 17, 32, 20, TimeSpan.Zero), entry.PublishedAt!.Value.AddTicks(-(entry.PublishedAt.Value.Ticks % TimeSpan.TicksPerSecond)));

        var remote = Assert.Single(entry.Remotes);
        Assert.Equal("streamable-http", remote.Type);
        Assert.Equal("https://server.smithery.ai/@smithery-ai/github/mcp", remote.Url);
        var header = Assert.Single(remote.Headers);
        Assert.Equal(new McpCatalogInput("Authorization", "Bearer token", IsRequired: true, IsSecret: true), header);

        Assert.Empty(entry.Packages);
        Assert.True(entry.NeedsCredentials);
        Assert.Equal("ai.smithery/smithery-ai-github:1.0.0", page.NextCursor);
    }

    [Fact]
    public void A_secret_declared_only_as_an_argument_variable_still_needs_credentials()
    {
        var entry = McpRegistryParser.ParseServerResponse(PackageEntry);

        Assert.NotNull(entry);
        Assert.Equal("GitHub", entry.Title);
        Assert.Equal(2, entry.Packages.Count);

        var oci = entry.Packages[0];
        Assert.Equal("oci", oci.RegistryType);
        Assert.Equal("stdio", oci.Transport);
        Assert.Null(oci.Version);
        var token = Assert.Single(oci.Inputs);
        Assert.Equal("token", token.Name);
        Assert.True(token.IsSecret);
        // The variable has no description of its own, so the argument's description is used.
        Assert.Equal("Optional GitHub Personal Access Token.", token.Description);

        var npm = entry.Packages[1];
        Assert.Equal(["GCS_BUCKET", "GCS_PRIVATE_KEY"], npm.Inputs.Select(input => input.Name));
        Assert.True(npm.Inputs[0].IsRequired);
        Assert.False(npm.Inputs[0].IsSecret);

        Assert.True(entry.NeedsCredentials);
    }

    [Theory]
    [InlineData("2025-09-16")]
    [InlineData("2025-09-29")]
    [InlineData("2025-12-11")]
    [InlineData("2099-01-01")]
    public void Accepts_every_schema_revision_and_ignores_fields_it_does_not_know(string schemaDate)
    {
        var json = $$"""
            {
              "servers": [
                {
                  "server": {
                    "$schema": "https://static.modelcontextprotocol.io/schemas/{{schemaDate}}/server.schema.json",
                    "name": "com.example/tool",
                    "version": "2.0.0",
                    "icons": [ { "src": "https://example.com/icon.png" } ],
                    "futureObject": { "nested": [1, 2, 3] },
                    "remotes": [ { "type": "sse", "url": "https://example.com/sse", "futureField": true } ]
                  },
                  "_meta": { "some.other/meta": { "anything": 1 } }
                }
              ],
              "metadata": { "count": 1 }
            }
            """;

        var entry = Assert.Single(McpRegistryParser.ParsePage(json).Entries);

        Assert.Equal("com.example/tool", entry.Name);
        Assert.Equal("sse", Assert.Single(entry.Remotes).Type);
        Assert.Null(entry.Status);
        Assert.False(entry.IsLatest);
        Assert.False(entry.NeedsCredentials);
    }

    [Fact]
    public void Skips_malformed_entries_without_dropping_the_rest_of_the_page()
    {
        var json = $$"""
            {
              "servers": [
                { "server": { "description": "no name" } },
                "not an object",
                { "server": { "name": "com.example/ok", "remotes": [ { "type": "sse" } ], "packages": [ { "registryType": "npm" } ] } },
                {{RemoteEntry}}
              ]
            }
            """;

        var page = McpRegistryParser.ParsePage(json);

        Assert.Equal(["com.example/ok", "ai.smithery/smithery-ai-github"], page.Entries.Select(entry => entry.Name));
        // A remote without a URL and a package without an identifier describe nothing installable.
        Assert.Empty(page.Entries[0].Remotes);
        Assert.Empty(page.Entries[0].Packages);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void A_bare_server_object_without_the_wrapper_still_parses()
    {
        var entry = McpRegistryParser.ParseServerResponse("""{ "name": "com.example/bare", "version": "1.0.0" }""");

        Assert.NotNull(entry);
        Assert.Equal("com.example/bare", entry.Name);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("")]
    public void A_body_that_is_not_a_json_object_is_a_format_error(string body)
    {
        Assert.Throws<FormatException>(() => McpRegistryParser.ParsePage(body));
    }
}
