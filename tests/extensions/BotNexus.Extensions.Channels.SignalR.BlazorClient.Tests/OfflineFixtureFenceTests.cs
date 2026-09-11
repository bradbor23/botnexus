using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Fences component fixtures against real network calls (#145).
/// </summary>
/// <remarks>
/// <para>
/// A bare <c>new HttpClient</c> in a fixture is a live client. Rendering <c>MainLayout</c> then
/// issues real requests from <c>OnAfterRenderAsync</c>, and because Blazor does not await that
/// method before returning, <c>Render()</c> gives the test back control while a TCP connection
/// attempt is still outstanding. The trailing <c>StateHasChanged()</c> lands whenever the socket
/// resolves - which on a loaded runner can be after the test has already queried an element and is
/// about to click it, dropping the click.
/// </para>
/// <para>
/// That is not a hypothetical: <c>SchedulesPanel_ClosedByDefault_AndOpensOnTheChevron</c> reddened
/// main this way, having passed twice on identical code in the PR behind it.
/// </para>
/// <para>
/// The rule is therefore the whole point of the fence rather than a style preference: a unit test
/// may not depend on what is or is not listening on a port of the machine running it. Pass a stub
/// handler - this project already has several - or <see cref="OfflineTestHttp"/> when the fixture
/// only needs the calls to fail the way they always did.
/// </para>
/// <para>
/// This is a sibling in spirit to the deadline fences in <c>BotNexus.Architecture.Tests</c>, and it
/// lives here rather than there because it is specific to this project's fixtures. It counts a
/// DEFECT, not a pattern: there is no legitimate reason for a bUnit fixture to open a socket, which
/// is why it carries no baseline and no allowance.
/// </para>
/// </remarks>
public sealed class OfflineFixtureFenceTests
{
    /// <summary>
    /// Matches a construction with no handler argument: <c>new HttpClient()</c>,
    /// <c>new HttpClient { ... }</c>, or a bare <c>new HttpClient</c>. A construction that passes a
    /// handler - <c>new HttpClient(_handler)</c> - is exactly what the fence asks for and is not
    /// matched.
    /// </summary>
    private static readonly Regex UnstubbedClient =
        new(@"new HttpClient\s*(?:\(\s*\))?\s*(?:\{|;|\)|,|$)", RegexOptions.Multiline);

    /// <summary>
    /// Matches a raw hub construction in BOTH shapes: <c>new GatewayHubConnection(...)</c> and the
    /// target-typed <c>GatewayHubConnection _hub = new();</c>.
    /// <para>
    /// The second alternative is not defensive padding. The first version of this fence matched only
    /// the explicit form, and six fixtures used the target-typed one - including
    /// <c>PortalLoadServiceTests</c>, the file whose failures prompted the fence. It passed its own
    /// mutation test because the mutation happened to use the shape it did match. A fence has to be
    /// pointed at the shape the code actually uses, not the shape you had in mind.
    /// </para>
    /// <para>
    /// Unlike the client above there is no "with a handler" form to exempt: the handler is a settable
    /// property, so it is set on a LATER line and a per-line regex could never see it. Requiring one
    /// factory keeps the rule checkable by a single line and gives the reason one place to live.
    /// </para>
    /// </summary>
    private static readonly Regex RawHubConstruction =
        new(@"new\s+GatewayHubConnection\s*\(|GatewayHubConnection\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*new\s*\(",
            RegexOptions.Multiline);

    /// <summary>
    /// SignalR builds its own HTTP client, so <see cref="OfflineTestHttp"/> does not cover the hub:
    /// a fixture holding a real <c>GatewayHubConnection</c> still opens a socket when something
    /// drives <c>InitializeAsync</c>.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical either. Both <c>PortalLoadServiceTests</c> 404 tests assert that a
    /// history 404 never reaches <c>InitializeAsync</c>'s top-level catch, by checking
    /// <c>LoadError</c> mentions no "404" - and the only other thing that can reach that catch is
    /// the hub connect. They passed while nothing was listening on the fixture's port and failed
    /// once a stray dev server answered 404 there, having caught ITS 404 instead of the one under
    /// test. Reaching the connect requires the REST calls to succeed first, which is why only a
    /// fixture that substitutes <c>IGatewayRestClient</c> ever got far enough to notice.
    /// </remarks>
    [Fact]
    public void Fixtures_DoNotConstructHubsThatReachTheNetwork()
    {
        var offenders = new List<string>();

        foreach (var file in FixtureSources())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var code = StripComment(lines[index]);
                if (RawHubConstruction.IsMatch(code))
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}");
            }
        }

        offenders.ShouldBeEmpty(
            "A fixture must build its hub with OfflineTestHub.Create(), so the suite never depends " +
            "on what is listening on a port of the host running it. SignalR builds its own HTTP " +
            "client, so OfflineTestHttp does not cover this - the hub's negotiate goes to a real " +
            $"socket.{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Pins the hub boundary, including that the approved factory is not an offender.</summary>
    [Theory]
    [InlineData("var hub = new GatewayHubConnection();", true)]
    [InlineData("        new GatewayHubConnection(),", true)]
    [InlineData("var h = new GatewayEventHandler(store, new GatewayHubConnection(), logger, store);", true)]
    // The shape the first version of this fence missed, in six files.
    [InlineData("    private readonly GatewayHubConnection _hub = new();", true)]
    [InlineData("GatewayHubConnection hub = new() { };", true)]
    [InlineData("var hub = OfflineTestHub.Create();", false)]
    [InlineData("        OfflineTestHub.Create(),", false)]
    [InlineData("    private readonly GatewayHubConnection _hub = OfflineTestHub.Create();", false)]
    public void HubFence_MatchesOnlyRawConstructions(string source, bool expectedOffender)
        => RawHubConstruction.IsMatch(source).ShouldBe(expectedOffender);

    /// <summary>The factory must refuse without a socket, like its HttpClient sibling.</summary>
    [Fact]
    public void OfflineHub_CarriesAHandlerThatRefusesWithoutASocket()
    {
        var hub = OfflineTestHub.Create();

        hub.TestHttpHandler.ShouldNotBeNull();
    }

    [Fact]
    public void Fixtures_DoNotConstructHttpClientsThatReachTheNetwork()
    {
        var offenders = new List<string>();

        foreach (var file in FixtureSources())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var code = StripComment(lines[index]);
                if (UnstubbedClient.IsMatch(code))
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}");
            }
        }

        offenders.ShouldBeEmpty(
            "A fixture's HttpClient must be given a handler, so the suite never depends on what is " +
            "listening on a port of the host running it. An unstubbed client makes MainLayout's " +
            "OnAfterRenderAsync issue real requests, and their completion re-renders the component at " +
            "an unpredictable moment - which is how a click between an element query and its dispatch " +
            "gets dropped. Pass a stub handler, or OfflineTestHttp.Create(...) to keep the calls " +
            $"failing exactly as they did before.{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Pins the boundary, including the mutation that a handler argument is what exempts.</summary>
    [Theory]
    [InlineData("var http = new HttpClient();", true)]
    [InlineData("var http = new HttpClient { BaseAddress = new Uri(\"http://localhost/\") };", true)]
    [InlineData("services.AddSingleton(new HttpClient());", true)]
    [InlineData("var http = new HttpClient(_handler);", false)]
    [InlineData("var http = new HttpClient(new StubHandler()) { BaseAddress = Root };", false)]
    [InlineData("var http = OfflineTestHttp.Create(\"http://localhost/\");", false)]
    public void Fence_MatchesOnlyConstructionsWithoutAHandler(string source, bool expectedOffender)
        => UnstubbedClient.IsMatch(source).ShouldBe(expectedOffender);

    /// <summary>
    /// The helper must refuse without a socket, and must refuse the way the callers already expect.
    /// <c>LoadCronJobsAsync</c> and its siblings catch <see cref="HttpRequestException"/>, which is
    /// what connection-refused threw before, so no caller's behaviour changes.
    /// </summary>
    [Fact]
    public async Task OfflineClient_RefusesWithTheSameExceptionConnectionRefusedThrew()
    {
        using var client = OfflineTestHttp.Create("http://localhost/");

        var thrown = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("/api/cron"));

        thrown.Message.ShouldContain("offline by design");
    }

    /// <summary>
    /// Fixture sources to scan. The offline helpers and this fence are excluded because all three
    /// necessarily name the banned forms - in an approved construction or in prose.
    /// </summary>
    private static IEnumerable<string> FixtureSources()
    {
        var excluded = new[] { "OfflineTestHttp.cs", "OfflineTestHub.cs", "OfflineFixtureFenceTests.cs" };

        foreach (var file in Directory.EnumerateFiles(ProjectRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>Drops a trailing line comment, so prose naming a banned form is not an offender.</summary>
    private static string StripComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    /// <summary>
    /// Walks up from the test binary to this project's source, so the scan reads the working tree
    /// rather than whatever was copied next to the assembly.
    /// </summary>
    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("*.csproj").Length > 0)
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the test project root from " + AppContext.BaseDirectory);
    }
}
