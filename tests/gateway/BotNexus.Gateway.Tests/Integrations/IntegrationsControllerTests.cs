using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Integrations;

/// <summary>
/// Pins the Integrations catalog routes: status codes, limit clamping, and the stale flag reaching the
/// response. The registry is a fake behind the real cache.
/// </summary>
public sealed class IntegrationsControllerTests
{
    private readonly FakeRegistry _registry = new();

    private IntegrationsController CreateController() => new(new McpRegistryCatalogCache(_registry));

    [Fact]
    public async Task Catalog_returns_the_entries_cursor_and_freshness()
    {
        var result = await CreateController().Catalog("github", null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<IntegrationCatalogResponse>(ok.Value);
        Assert.Equal("com.example/github", Assert.Single(body.Entries).Name);
        Assert.Equal("next", body.NextCursor);
        Assert.False(body.Stale);
        Assert.Equal(IntegrationsController.DefaultLimit, _registry.LastLimit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(1000, IntegrationsController.MaxLimit)]
    public async Task Catalog_clamps_the_page_size_rather_than_refusing_it(int requested, int expected)
    {
        await CreateController().Catalog(null, null, requested, CancellationToken.None);

        Assert.Equal(expected, _registry.LastLimit);
    }

    [Fact]
    public async Task Catalog_refuses_overlong_search_text_without_asking_the_registry()
    {
        var result = await CreateController().Catalog(new string('x', IntegrationsController.MaxTextLength + 1), null, null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task Catalog_is_502_when_the_registry_is_unreachable_and_nothing_is_cached()
    {
        _registry.Failure = new HttpRequestException("unreachable");

        var result = await CreateController().Catalog("github", null, null, CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Entry_returns_the_named_server()
    {
        var result = await CreateController().Entry(" io.github.github/github-mcp-server ", CancellationToken.None);

        var body = Assert.IsType<IntegrationCatalogEntryResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("io.github.github/github-mcp-server", body.Entry.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Entry_requires_a_name(string? name)
    {
        var result = await CreateController().Entry(name, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task Entry_is_404_for_an_unlisted_name_and_502_when_unreachable()
    {
        var controller = CreateController();

        Assert.IsType<NotFoundObjectResult>((await controller.Entry("com.example/missing", CancellationToken.None)).Result);

        _registry.Failure = new HttpRequestException("unreachable");
        var unreachable = await controller.Entry("com.example/other", CancellationToken.None);
        Assert.Equal(502, Assert.IsType<ObjectResult>(unreachable.Result).StatusCode);
    }

    private sealed class FakeRegistry : IMcpRegistryClient
    {
        public int Calls { get; private set; }

        public int LastLimit { get; private set; }

        public Exception? Failure { get; set; }

        public Task<McpCatalogPage> SearchAsync(string? search, string? cursor, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            LastLimit = limit;
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new McpCatalogPage([Entry($"com.example/{search}")], "next"));
        }

        public Task<McpCatalogEntry?> GetLatestAsync(string name, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null)
                throw Failure;

            return Task.FromResult<McpCatalogEntry?>(name.EndsWith("/missing", StringComparison.Ordinal) ? null : Entry(name));
        }

        private static McpCatalogEntry Entry(string name) =>
            new(name, null, null, "1.0.0", null, null, [], [], "active", true, null);
    }
}
