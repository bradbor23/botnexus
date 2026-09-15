using System.Net;
using BotNexus.Gateway.Api.Integrations;

namespace BotNexus.Gateway.Tests.Integrations;

/// <summary>
/// Pins the requests <see cref="McpRegistryClient"/> sends. Every test answers from a stub handler;
/// nothing here reaches the network.
/// </summary>
public sealed class McpRegistryClientTests
{
    [Fact]
    public async Task Search_asks_for_the_latest_versions_and_escapes_the_query()
    {
        var handler = new RecordingHandler(_ => Json("""{ "servers": [], "metadata": {} }"""));
        var client = new McpRegistryClient(new StubFactory(handler));

        await client.SearchAsync("  git hub  ", "io.github.x/y:1.0.0", 25, CancellationToken.None);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("registry.modelcontextprotocol.io", uri.Host);
        Assert.Equal("/v0.1/servers", uri.AbsolutePath);
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("version=latest", query);
        Assert.Contains("limit=25", query);
        Assert.Contains("search=git hub", query);
        Assert.Contains("cursor=io.github.x/y:1.0.0", query);
    }

    [Fact]
    public async Task Search_omits_empty_search_and_cursor()
    {
        var handler = new RecordingHandler(_ => Json("""{ "servers": [] }"""));
        var client = new McpRegistryClient(new StubFactory(handler));

        await client.SearchAsync("   ", null, 30, CancellationToken.None);

        var query = Assert.Single(handler.Requests).Query;
        Assert.DoesNotContain("search=", query);
        Assert.DoesNotContain("cursor=", query);
    }

    [Fact]
    public async Task Lookup_sends_the_whole_name_as_one_path_segment()
    {
        var handler = new RecordingHandler(_ => Json("""{ "server": { "name": "io.github.github/github-mcp-server" } }"""));
        var client = new McpRegistryClient(new StubFactory(handler));

        var entry = await client.GetLatestAsync("io.github.github/github-mcp-server", CancellationToken.None);

        Assert.NotNull(entry);
        var uri = Assert.Single(handler.Requests);
        Assert.Contains("/v0.1/servers/io.github.github%2Fgithub-mcp-server/versions/latest", uri.AbsoluteUri);
    }

    [Fact]
    public async Task Lookup_of_an_unlisted_name_is_null_not_an_error()
    {
        var client = new McpRegistryClient(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        Assert.Null(await client.GetLatestAsync("com.example/missing", CancellationToken.None));
    }

    [Fact]
    public async Task A_server_error_is_an_http_request_exception_carrying_the_status()
    {
        var client = new McpRegistryClient(new StubFactory(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(null, null, 30, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(McpRegistryClient.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
