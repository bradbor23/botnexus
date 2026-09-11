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

    [Fact]
    public void Fixtures_DoNotConstructHttpClientsThatReachTheNetwork()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ProjectRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // The helper and this fence both name the banned form in prose, by necessity.
            if (string.Equals(name, "OfflineTestHttp.cs", StringComparison.Ordinal)
                || string.Equals(name, "OfflineFixtureFenceTests.cs", StringComparison.Ordinal))
            {
                continue;
            }
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                var code = comment < 0 ? line : line[..comment];
                if (UnstubbedClient.IsMatch(code))
                    offenders.Add($"{name}:{index + 1}");
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
