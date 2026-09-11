using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// A <see cref="GatewayHubConnection"/> for fixtures that never touches a socket (#145's sibling).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OfflineTestHttp"/> took fixtures off the network for <see cref="HttpClient"/>, but
/// SignalR builds its own client, so a fixture holding a real hub stayed on it. Reaching the connect
/// needs the REST calls to SUCCEED first - which is why only a fixture that SUBSTITUTES
/// <c>IGatewayRestClient</c> ever got that far, and why this went unnoticed until one did.
/// </para>
/// <para>
/// It cost two failures on the build host: both <c>PortalLoadServiceTests</c> 404 tests assert that
/// a history 404 never reaches the top-level catch by checking <c>LoadError</c> mentions no "404" -
/// and the only other thing that can reach that catch is this connect. With nothing on the
/// fixture's port the connect failed "connection refused" and they passed; with a stray dev server
/// answering 404 they failed, having caught its 404 instead of the one under test.
/// </para>
/// <para>
/// The rule is blanket rather than "only fixtures that can reach a connect", deliberately. Whether
/// a fixture gets that far depends on call ordering inside <c>InitializeAsync</c> that no fixture
/// controls and that is free to change; and for a fixture that never connects, this costs nothing.
/// A uniform rule is also checkable by a one-line regex, where "can it reach a connect" is not.
/// </para>
/// </remarks>
internal static class OfflineTestHub
{
    /// <summary>A hub whose negotiate and transport requests are refused without a socket.</summary>
    internal static GatewayHubConnection Create() =>
        new() { TestHttpHandler = OfflineTestHttp.Handler() };
}
