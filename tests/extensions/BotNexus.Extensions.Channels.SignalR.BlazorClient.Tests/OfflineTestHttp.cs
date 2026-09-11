namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// An <see cref="HttpClient"/> for component fixtures that never touches a socket (#145).
/// </summary>
/// <remarks>
/// <para>
/// <c>new HttpClient { BaseAddress = new Uri("http://localhost/") }</c> in a fixture is a REAL
/// client. Rendering <c>MainLayout</c> then issues real requests from <c>OnAfterRenderAsync</c> -
/// <c>LoadCronJobsAsync</c> among them - and Blazor does not await <c>OnAfterRenderAsync</c> before
/// returning, so <c>Render()</c> hands control back to the test while a TCP connection attempt is
/// still in flight. Its trailing <c>StateHasChanged()</c> then lands at an unpredictable moment,
/// including between a test's element query and the click it makes on that element, which drops the
/// click.
/// </para>
/// <para>
/// That is how <c>SchedulesPanel_ClosedByDefault_AndOpensOnTheChevron</c> reddened main while
/// passing twice on identical code in the PR behind it. The panel was not broken; the click never
/// reached the handler, so <c>aria-expanded</c> was still "false" a line later.
/// </para>
/// <para>
/// The handler throws <see cref="HttpRequestException"/>, which is exactly what connection-refused
/// produced before - so every caller's catch behaves as it already did, and no assertion changes.
/// What changes is that the failure is immediate and deterministic instead of a socket round trip
/// whose latency depends on how loaded the host is, and on whether anything happens to be listening
/// on port 80 of the machine running the suite.
/// </para>
/// <para>
/// <see cref="Handler"/> is exposed separately so a fixture can keep its own
/// <see cref="HttpClient.BaseAddress"/>, including keeping it unset: a client with no base address
/// rejects a relative URI before the handler is ever consulted, and some fixtures rely on that.
/// </para>
/// </remarks>
internal static class OfflineTestHttp
{
    /// <summary>A client with no base address, for fixtures that constructed one that way.</summary>
    internal static HttpClient Create() => new(Handler());

    /// <summary>A client whose base address matches what the fixtures used.</summary>
    internal static HttpClient Create(string baseAddress) =>
        new(Handler()) { BaseAddress = new Uri(baseAddress) };

    /// <summary>A handler that refuses every request immediately, without a socket.</summary>
    internal static HttpMessageHandler Handler() => new RefuseEverythingHandler();

    private sealed class RefuseEverythingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(
                $"This fixture is offline by design (#145): {request.Method} {request.RequestUri}. " +
                "Register a stub handler if the test needs a response.");
    }
}
