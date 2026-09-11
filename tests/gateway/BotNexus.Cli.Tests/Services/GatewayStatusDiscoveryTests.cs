using System.Net;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Pins the defect that made `botnexus gateway status` report a live gateway as not running.
///
/// <para>
/// #2772 gave the manager PID-file-less discovery by binary-path identity, and #102 wired it into
/// <c>stop</c>. <c>GetStatusAsync</c> was left behind: it took no binary path, so the fallback was
/// structurally unreachable and state was decided purely by <c>gateway.pid</c>. Because
/// <c>scripts/gateway-restart.sh</c> and the systemd unit both launch the binary directly and write
/// no PID file, that is the ORDINARY state of a healthy deployment -- the portal served traffic
/// while the CLI said the gateway was down.
/// </para>
///
/// <para>
/// Every process here is an <see cref="IGatewayProcessHandle"/> fake: nothing is spawned,
/// enumerated or signalled for real.
/// </para>
/// </summary>
public sealed class GatewayStatusDiscoveryTests : IDisposable
{
    private readonly string _home;
    private readonly IHealthChecker _healthChecker = Substitute.For<IHealthChecker>();

    public GatewayStatusDiscoveryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bn-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeProcessHandle(int id, string? executablePath) : IGatewayProcessHandle
    {
        public int Id { get; } = id;

        public int KillCount { get; private set; }

        public int GracefulStopCount { get; private set; }

        public string? ExecutablePath => executablePath;

        public bool RequestGracefulStop()
        {
            GracefulStopCount++;
            return true;
        }

        public void Kill() => KillCount++;

        public bool WaitForExit(int milliseconds) => true;

        public bool WasSignalled => GracefulStopCount > 0 || KillCount > 0;
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode) : DelegatingHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private GatewayProcessManager NewManager(
        DelegatingHandler? handler = null,
        params IGatewayProcessHandle[] processes)
        => new(
            _healthChecker,
            NullLogger<GatewayProcessManager>.Instance,
            probeClient: new HttpClient(handler ?? new FakeHttpHandler(HttpStatusCode.OK))
            {
                Timeout = TimeSpan.FromSeconds(3)
            },
            processEnumerator: () => processes);

    private string GatewayDll => Path.Combine(_home, "bin", "BotNexus.Gateway.Api.dll");

    private string PidFilePath => Path.Combine(_home, "gateway.pid");

    // -------------------------------------------------------------------------------------
    // The reported defect, stated as a test.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// THE REGRESSION. A gateway started by scripts/gateway-restart.sh: alive, serving, no PID file.
    /// Before the fix this returned NotRunning / "No PID file found".
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_ReportsRunning_WhenAGatewayIsAliveWithNoPidFile()
    {
        File.Exists(PidFilePath).ShouldBeFalse("the fixture must start with no PID file");
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        var status = await NewManager(null, gateway).GetStatusAsync(_home, GatewayDll, CancellationToken.None);

        status.State.ShouldBe(GatewayState.Running,
            "a live gateway with no PID file is the ordinary state of a restart-script deployment");
        status.Pid.ShouldBe(4242);
        status.Message.ShouldNotBeNull();
        status.Message!.ShouldContain("discovered by binary path");
    }

    /// <summary>
    /// NON-VACUITY. Omitting the binary path is precisely the pre-fix call shape, and it must still
    /// report NotRunning -- proving the assertion above is carried by the new argument and not by
    /// something incidental in the fixture.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_StillReportsNotRunning_WhenNoBinaryPathIsSupplied()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        var status = await NewManager(null, gateway).GetStatusAsync(_home, null, CancellationToken.None);

        status.State.ShouldBe(GatewayState.NotRunning,
            "with no binary path there is nothing to match on; this is the defect being fixed");
    }

    /// <summary>
    /// #2369 must not be weakened: a live process that is NOT the gateway is never claimed as one,
    /// and discovery never signals anything.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_DoesNotClaimAForeignProcess_AndNeverSignalsAnything()
    {
        var foreign = new FakeProcessHandle(11, Path.Combine(_home, "bin", "notepad.exe"));

        var status = await NewManager(null, foreign).GetStatusAsync(_home, GatewayDll, CancellationToken.None);

        status.State.ShouldBe(GatewayState.NotRunning);
        foreign.WasSignalled.ShouldBeFalse("status is a diagnostic; it signals nothing, ever");
    }

    /// <summary>
    /// Status is read-only. It used to delete an unverifiable PID file, which removed the only
    /// record `stop` could have used.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_LeavesAnUnverifiablePidFileInPlace()
    {
        // A legacy bare-PID file for a dead process: unverifiable by every route.
        await File.WriteAllTextAsync(PidFilePath, "99999");

        var status = await NewManager().GetStatusAsync(_home, GatewayDll, CancellationToken.None);

        status.State.ShouldBe(GatewayState.NotRunning);
        File.Exists(PidFilePath).ShouldBeTrue(
            "a read-only diagnostic must not destroy the state a later `stop` depends on");
    }

    /// <summary>
    /// A discovered gateway is still probed, so the operator learns whether it actually answers --
    /// the probe result is what distinguishes "alive and serving" from "alive but wedged".
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_ProbesTheHealthEndpoint_ForADiscoveredGateway()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        var status = await NewManager(handler, gateway).GetStatusAsync(_home, GatewayDll, CancellationToken.None);

        status.ProbeResult.ShouldBe(GatewayProbeResult.Healthy);
        handler.Requests.ShouldBe(1, "the probe must actually be issued, not assumed");
    }

    /// <summary>
    /// Uptime is unknown for a discovered process rather than fabricated as zero: the handle carries
    /// no start time, and a confident 00:00:00 would read as a gateway that just restarted.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_ReportsUnknownUptime_ForADiscoveredGateway()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        var status = await NewManager(null, gateway).GetStatusAsync(_home, GatewayDll, CancellationToken.None);

        status.Uptime.ShouldBeNull();
    }
}
