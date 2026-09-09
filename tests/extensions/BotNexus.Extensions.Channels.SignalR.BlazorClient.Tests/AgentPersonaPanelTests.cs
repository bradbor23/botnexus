using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Coverage for the agent persona slide-out: the fast-edit surface opened by clicking the agent
/// name in the top bar, with /agents/{id} kept as the deep settings page.
///
/// The save assertions are the ones worth having. The panel deliberately does NOT reuse
/// PUT /api/agents/{id} - that route binds a whole AgentDescriptor and full-replaces it, so a
/// persona-shaped body would silently delete every property the panel does not model. These tests
/// pin the narrow route and the exact body, because a future "simplification" back onto the
/// whole-descriptor endpoint would pass a smoke test and quietly destroy agent configuration.
/// </summary>
public sealed class AgentPersonaPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly RecordingHandler _handler = new();

    public AgentPersonaPanelTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(new HttpClient(_handler));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────────

    private IRenderedComponent<AgentPersonaPanel> Render() => _ctx.Render<AgentPersonaPanel>();

    private void Seed(
        string agentId,
        string displayName = "Gantry Manager",
        string? responsibility = null,
        string? description = null,
        string? boundaries = null,
        int? hue = null,
        string? emoji = null)
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = agentId,
            DisplayName = displayName,
            Responsibility = responsibility,
            Description = description,
            Boundaries = boundaries,
            AvatarHue = hue,
            Emoji = emoji,
            IsConnected = true
        });
    }

    private static async Task<IRenderedComponent<AgentPersonaPanel>> OpenAsync(
        IRenderedComponent<AgentPersonaPanel> cut, string agentId)
    {
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(agentId));
        return cut;
    }

    private static string TextAreaValue(IRenderedComponent<AgentPersonaPanel> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").GetAttribute("value") ?? "";

    private static bool IsOpen(IRenderedComponent<AgentPersonaPanel> cut) =>
        cut.FindAll("[data-testid='agent-persona-panel']").Count > 0;

    // ── 1. default / empty state ──────────────────────────────────────────────

    [Fact]
    public void Panel_renders_nothing_until_it_is_opened()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='agent-persona-panel']"));
        Assert.Empty(cut.FindAll("[data-testid='agent-persona-overlay']"));
    }

    [Fact]
    public async Task Panel_ignores_a_blank_agent_id()
    {
        var cut = Render();

        await OpenAsync(cut, "   ");

        Assert.False(IsOpen(cut));
    }

    // ── 2. rendering with data ────────────────────────────────────────────────

    [Fact]
    public async Task Panel_prefills_every_persona_field_from_the_store()
    {
        Seed("gantry-manager", "Gantry Manager", "Watches spend", "Tracks API usage", "Never writes config", 210);
        var cut = Render();

        await OpenAsync(cut, "gantry-manager");

        Assert.Equal("Gantry Manager", cut.Find("[data-testid='agent-persona-name']").GetAttribute("value"));
        Assert.Equal("Watches spend", cut.Find("[data-testid='agent-persona-responsibility']").GetAttribute("value"));
        // A bound <textarea> carries its value on the element, not as child text.
        Assert.Equal("Tracks API usage", TextAreaValue(cut, "agent-persona-description"));
        Assert.Equal("Never writes config", TextAreaValue(cut, "agent-persona-boundaries"));
    }

    [Fact]
    public async Task Panel_marks_the_saved_hue_as_selected()
    {
        Seed("gantry-manager", hue: 210);
        var cut = Render();

        await OpenAsync(cut, "gantry-manager");

        var selected = cut.FindAll("[data-testid='agent-persona-hue-swatch'].selected");
        Assert.Single(selected);
        Assert.Equal("210", selected[0].GetAttribute("data-hue"));
    }

    [Fact]
    public async Task Panel_marks_auto_as_selected_when_no_hue_is_set()
    {
        Seed("gantry-manager", hue: null);
        var cut = Render();

        await OpenAsync(cut, "gantry-manager");

        Assert.Contains("selected", cut.Find("[data-testid='agent-persona-hue-auto']").ClassName);
        Assert.Empty(cut.FindAll("[data-testid='agent-persona-hue-swatch'].selected"));
    }

    [Fact]
    public async Task Panel_links_to_the_deep_settings_page_for_that_agent()
    {
        Seed("gantry-manager");
        var cut = Render();

        await OpenAsync(cut, "gantry-manager");

        Assert.Equal("agents/gantry-manager", cut.Find("[data-testid='agent-persona-deep-link']").GetAttribute("href"));
    }

    [Fact]
    public async Task Panel_is_a_labelled_modal_dialog()
    {
        Seed("gantry-manager");
        var cut = Render();

        await OpenAsync(cut, "gantry-manager");

        var panel = cut.Find("[data-testid='agent-persona-panel']");
        Assert.Equal("dialog", panel.GetAttribute("role"));
        Assert.Equal("true", panel.GetAttribute("aria-modal"));
        var labelledBy = panel.GetAttribute("aria-labelledby");
        Assert.False(string.IsNullOrWhiteSpace(labelledBy));
        Assert.NotNull(cut.Find($"#{labelledBy}"));
    }

    // ── 3. interaction ────────────────────────────────────────────────────────

    [Fact]
    public async Task Escape_closes_the_panel()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-overlay']").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.False(IsOpen(cut));
    }

    [Fact]
    public async Task A_key_that_is_not_escape_leaves_the_panel_open()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-overlay']").KeyDown(new KeyboardEventArgs { Key = "a" });

        Assert.True(IsOpen(cut));
    }

    [Fact]
    public async Task Clicking_the_overlay_closes_the_panel()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-overlay']").Click();

        Assert.False(IsOpen(cut));
    }

    [Fact]
    public async Task Clicking_inside_the_panel_does_not_close_it()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-panel']").Click();

        Assert.True(IsOpen(cut));
    }

    [Fact]
    public async Task The_close_button_closes_the_panel()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-close']").Click();

        Assert.False(IsOpen(cut));
    }

    [Fact]
    public async Task Choosing_a_hue_moves_the_selection()
    {
        Seed("gantry-manager", hue: null);
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.FindAll("[data-testid='agent-persona-hue-swatch']")
            .First(s => s.GetAttribute("data-hue") == "120").Click();

        Assert.Equal("120", cut.Find("[data-testid='agent-persona-hue-swatch'].selected").GetAttribute("data-hue"));
        Assert.DoesNotContain("selected", cut.Find("[data-testid='agent-persona-hue-auto']").ClassName);
    }

    // ── 4. saving ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_puts_to_the_narrow_persona_route_not_the_whole_descriptor_route()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Put, _handler.LastRequest!.Method);
        Assert.EndsWith("/api/agents/gantry-manager/persona", _handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Save_sends_every_persona_field_and_nothing_else()
    {
        Seed("gantry-manager", "Gantry Manager", "Watches spend", "Tracks usage", "No config writes", 210, "\U0001F4B0");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        using var body = JsonDocument.Parse(_handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("Gantry Manager", root.GetProperty("displayName").GetString());
        Assert.Equal("Watches spend", root.GetProperty("responsibility").GetString());
        Assert.Equal("Tracks usage", root.GetProperty("description").GetString());
        Assert.Equal("No config writes", root.GetProperty("boundaries").GetString());
        Assert.Equal(210, root.GetProperty("avatarHue").GetInt32());

        // The body must carry the persona and NOTHING resembling a descriptor: sending
        // toolIds/modelId/apiProvider here would mean someone re-pointed this at the replace route.
        var names = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            { "displayName", "emoji", "avatarHue", "responsibility", "description", "boundaries" },
            names);
    }

    [Fact]
    public async Task Save_writes_the_edited_persona_into_the_store()
    {
        Seed("gantry-manager", "Gantry Manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-responsibility']").Input("Owns the budget");
        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        Assert.Equal("Owns the budget", _store.GetAgent("gantry-manager")!.Responsibility);
        Assert.True(_store.GetAgent("gantry-manager")!.IsConnected);
    }

    [Fact]
    public async Task Save_closes_the_panel()
    {
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        Assert.False(IsOpen(cut));
    }

    [Fact]
    public async Task Blank_optional_fields_are_sent_as_null_rather_than_empty_strings()
    {
        Seed("gantry-manager", "Gantry Manager", responsibility: "Watches spend");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-responsibility']").Input("   ");
        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        using var body = JsonDocument.Parse(_handler.LastBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("responsibility").ValueKind);
        Assert.Null(_store.GetAgent("gantry-manager")!.Responsibility);
    }

    // ── 5. edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_save_reports_the_error_and_keeps_the_panel_open()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        Seed("gantry-manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        Assert.True(IsOpen(cut));
        Assert.Contains("500", cut.Find("[data-testid='agent-persona-error']").TextContent);
    }

    [Fact]
    public async Task A_failed_save_leaves_the_store_untouched()
    {
        _handler.StatusCode = HttpStatusCode.BadRequest;
        Seed("gantry-manager", "Gantry Manager", responsibility: "Original");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-responsibility']").Input("Changed");
        await cut.Find("[data-testid='agent-persona-save']").ClickAsync(new MouseEventArgs());

        Assert.Equal("Original", _store.GetAgent("gantry-manager")!.Responsibility);
    }

    [Fact]
    public async Task Save_is_disabled_while_the_name_is_blank()
    {
        Seed("gantry-manager", "Gantry Manager");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");

        cut.Find("[data-testid='agent-persona-name']").Input("   ");

        Assert.True(cut.Find("[data-testid='agent-persona-save']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Opening_an_agent_the_store_does_not_know_falls_back_to_its_id()
    {
        var cut = Render();

        await OpenAsync(cut, "never-seen");

        Assert.True(IsOpen(cut));
        Assert.Equal("never-seen", cut.Find("[data-testid='agent-persona-name']").GetAttribute("value"));
    }

    [Fact]
    public async Task Reopening_discards_edits_from_the_previous_visit()
    {
        Seed("gantry-manager", "Gantry Manager", responsibility: "Watches spend");
        var cut = Render();
        await OpenAsync(cut, "gantry-manager");
        cut.Find("[data-testid='agent-persona-responsibility']").Input("Scratch edit");
        cut.Find("[data-testid='agent-persona-close']").Click();

        await OpenAsync(cut, "gantry-manager");

        Assert.Equal("Watches spend", cut.Find("[data-testid='agent-persona-responsibility']").GetAttribute("value"));
    }

    /// <summary>
    /// Records the outgoing request so the tests can assert the route and the exact body.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
