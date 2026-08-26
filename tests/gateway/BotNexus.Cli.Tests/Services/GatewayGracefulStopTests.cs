using System.Diagnostics;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Pins the graceful-then-hard stop sequence.
/// <para>
/// A hard kill gives the gateway no chance to checkpoint SQLite, flush sessions or record a clean
/// shutdown, so every CLI-driven restart was reported by the next run as "previous gateway run
/// terminated uncleanly". The stop path must therefore ask politely first and escalate only when
/// that is refused or unavailable.
/// </para>
/// <para>
/// The handle here is a scripted fake rather than the discovery fake used by
/// <see cref="GatewayStopDiscoveryTests"/>: these assertions are about the ORDER of signals, so the
/// double has to record the call sequence, which that one deliberately does not. Nothing is
/// spawned or signalled for real.
/// </para>
/// </summary>
public sealed class GatewayGracefulStopTests : IDisposable
{
    private readonly string _home;
    private readonly IHealthChecker _healthChecker = Substitute.For<IHealthChecker>();

    public GatewayGracefulStopTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bn-graceful-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Records the exact sequence of stop operations. <see cref="WaitForExit"/> reports the process
    /// as gone once it has either been killed or honoured a graceful request it was scripted to
    /// accept, so the manager sees a coherent process rather than a canned answer.
    /// </summary>
    private sealed class ScriptedHandle(
        int id,
        string executablePath,
        bool gracefulAvailable,
        bool exitsOnGraceful) : IGatewayProcessHandle
    {
        public int Id { get; } = id;

        public string? ExecutablePath { get; } = executablePath;

        public List<string> Calls { get; } = [];

        public int KillCount { get; private set; }

        public bool TryRequestGracefulStop()
        {
            Calls.Add("graceful");
            return gracefulAvailable;
        }

        public void Kill()
        {
            KillCount++;
            Calls.Add("kill");
        }

        public bool WaitForExit(int milliseconds)
        {
            Calls.Add("wait");
            return KillCount > 0 || (gracefulAvailable && exitsOnGraceful);
        }
    }

    private string GatewayDll => Path.Combine(_home, "bin", "BotNexus.Gateway.Api.dll");

    private GatewayProcessManager NewManager(params IGatewayProcessHandle[] processes)
        => new(
            _healthChecker,
            NullLogger<GatewayProcessManager>.Instance,
            processEnumerator: () => processes,
            gracefulStopTimeout: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task StopAsync_AsksPolitelyFirst_AndNeverHardKillsWhenTheProcessComplies()
    {
        var handle = new ScriptedHandle(4321, GatewayDll, gracefulAvailable: true, exitsOnGraceful: true);
        var manager = NewManager(handle);

        var result = await manager.StopAsync(_home, GatewayDll);

        Assert.True(result.Success);
        Assert.Equal(GatewayStopOutcome.Stopped, result.Outcome);
        // The whole point: no SIGKILL was sent, so the gateway got to shut down cleanly.
        Assert.Equal(0, handle.KillCount);
        Assert.Equal(["graceful", "wait"], handle.Calls);
    }

    [Fact]
    public async Task StopAsync_EscalatesToHardKill_WhenTheGracefulRequestIsIgnored()
    {
        var handle = new ScriptedHandle(4322, GatewayDll, gracefulAvailable: true, exitsOnGraceful: false);
        var manager = NewManager(handle);

        var result = await manager.StopAsync(_home, GatewayDll);

        Assert.True(result.Success);
        Assert.Equal(GatewayStopOutcome.Stopped, result.Outcome);
        // A wedged gateway must still be stopped - politeness is the first attempt, not the only one.
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(["graceful", "wait", "kill", "wait"], handle.Calls);
    }

    [Fact]
    public async Task StopAsync_HardKillsWithoutWaiting_WhenThePlatformHasNoGracefulSignal()
    {
        // Windows, or a Unix host where delivery failed: TryRequestGracefulStop reports false and
        // the stop path must not burn the grace period waiting for a signal nobody received.
        var handle = new ScriptedHandle(4323, GatewayDll, gracefulAvailable: false, exitsOnGraceful: false);
        var manager = NewManager(handle);

        var result = await manager.StopAsync(_home, GatewayDll);

        Assert.True(result.Success);
        Assert.Equal(GatewayStopOutcome.Stopped, result.Outcome);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(["graceful", "kill", "wait"], handle.Calls);
    }

    /// <summary>
    /// The scripted tests above prove the manager's sequencing but say nothing about whether a
    /// signal is actually delivered - the whole feature rests on a libc P/Invoke that either
    /// resolves at runtime or silently does not. This drives the real handle against a real child
    /// process, so a broken import fails here rather than in production as a stop that quietly
    /// degrades to a hard kill.
    /// </summary>
    [Fact]
    public void TryRequestGracefulStop_DeliversSigtermToALiveProcess()
    {
        if (OperatingSystem.IsWindows())
            return; // No SIGTERM to deliver; the handle reports false and the manager hard-kills.

        using var process = Process.Start(new ProcessStartInfo("/bin/sleep", "300")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);

        try
        {
            var handle = new LiveProcessHandle(process);

            Assert.True(handle.TryRequestGracefulStop(), "libc kill() reported failure delivering SIGTERM");
            Assert.True(handle.WaitForExit(5000), "the child survived SIGTERM");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }
}
