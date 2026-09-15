using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the Integrations nav entry: it renders, links to <c>/integrations</c>, sits between Plugins
/// and Guide by default, and obeys a user order override. The failure these guard against is the
/// one #3346 recorded for Plugins - a page that ships routed but unreachable from the menu.
/// </summary>
/// <remarks>
/// The fake nav order deliberately omits <c>integrations</c>, as a saved order from before this key
/// existed would. The entry appearing anyway is the "no migration needed" promise on
/// <c>NavOrderKeys.DefaultOrder</c>.
/// </remarks>
public sealed class IntegrationsNavEntryTests : IDisposable
{
    private const string OrderPredatingIntegrations = """
        [
          { "key": "home", "order": 5 },
          { "key": "activity", "order": 10 },
          { "key": "tools", "order": 20 },
          { "key": "chat", "order": 30 },
          { "key": "configuration", "order": 40 },
          { "key": "skills", "order": 50 },
          { "key": "agents", "order": 60 },
          { "key": "cron", "order": 70 },
          { "key": "plugins", "order": 80 }
        ]
        """;

    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;
    private string _navOrderJson = OrderPredatingIntegrations;

    public IntegrationsNavEntryTests()
    {
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        restClient.GetExtensionDetailsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ExtensionDetailDto> { new("botnexus-skills", "Skills", "1.0.0", true, null, null, null) });

        var http = OfflineTestHttp.Create("http://localhost/");
        _features = new ExtensionFeatureService(restClient);

        var prefs = Substitute.For<IPortalPreferencesService>();
        prefs.Current.Returns(new PortalPreferences());

        _ctx.Services.AddSingleton<IClientStateStore>(new ClientStateStore());
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(OfflineTestHub.Create());
        _ctx.Services.AddSingleton(new GatewayInfoService(http, restClient));
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(_features);
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new ToolsApiClient(new HttpClient(new JsonHandler(() => "[]")) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.Services.AddSingleton(new NavOrderApiClient(
            new HttpClient(new JsonHandler(() => _navOrderJson)) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_menu_renders_one_integrations_entry_linking_to_the_integrations_route()
    {
        var cut = RenderLayout();

        var anchor = Assert.Single(cut.FindAll("a.toolbar-item[data-testid='nav-integrations']"));
        Assert.Equal("integrations", anchor.GetAttribute("href"));
        Assert.Contains("Integrations", anchor.TextContent);
    }

    [Fact]
    public void By_default_integrations_sits_between_plugins_and_guide_even_for_an_order_saved_before_it_existed()
    {
        var ids = NavTestIdsInRenderOrder();

        var integrations = ids.IndexOf("nav-integrations");
        Assert.True(integrations > 0, "nav-integrations must render");
        Assert.Equal("nav-plugins", ids[integrations - 1]);
        Assert.Equal("nav-guide", ids[integrations + 1]);
    }

    [Fact]
    public void The_entry_moves_with_a_user_order_override()
    {
        var defaultOrder = NavTestIdsInRenderOrder();

        _navOrderJson = """
            [
              { "key": "integrations", "order": 1 },
              { "key": "home", "order": 5 },
              { "key": "activity", "order": 10 },
              { "key": "tools", "order": 20 },
              { "key": "chat", "order": 30 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 },
              { "key": "plugins", "order": 80 }
            ]
            """;

        var overridden = NavTestIdsInRenderOrder();

        Assert.Equal("nav-integrations", overridden[0]);
        Assert.NotEqual(defaultOrder[0], overridden[0]);
        Assert.Equal(
            defaultOrder.OrderBy(x => x, StringComparer.Ordinal),
            overridden.OrderBy(x => x, StringComparer.Ordinal));
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _features.LoadAsync().GetAwaiter().GetResult();
        return _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));
    }

    private List<string> NavTestIdsInRenderOrder() =>
        RenderLayout().FindAll("a.toolbar-item")
            .Select(a => a.GetAttribute("data-testid-alias") is { Length: > 0 } alias
                ? alias
                : a.GetAttribute("data-testid") ?? string.Empty)
            .ToList();

    private sealed class JsonHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json(), System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
