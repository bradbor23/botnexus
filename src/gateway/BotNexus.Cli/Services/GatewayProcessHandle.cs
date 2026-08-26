using System.Diagnostics;

namespace BotNexus.Cli.Services;

/// <summary>
/// Minimal view of a live OS process used by the PID-file-less gateway discovery introduced for
/// issue #2772. Exists so the discovery and stop path can be tested deterministically WITHOUT ever
/// enumerating, inspecting or signalling a real process: <see cref="System.Diagnostics.Process"/> is
/// sealed-in-practice for test purposes (its identity members are not virtual and cannot be faked).
/// </summary>
public interface IGatewayProcessHandle
{
    /// <summary>Operating-system process id.</summary>
    int Id { get; }

    /// <summary>
    /// Full path of the executable image backing this process, or null when it cannot be read
    /// (access denied, or the process exited). Null is ALWAYS treated as "not identifiable" and
    /// therefore never as the gateway.
    /// </summary>
    string? ExecutablePath { get; }

    /// <summary>Requests immediate termination of the process (SIGKILL on Unix).</summary>
    void Kill();

    /// <summary>
    /// Asks the process to shut down gracefully - SIGTERM on Unix - and reports whether the request
    /// was delivered. Returns false where the platform has no graceful signal, or delivery failed;
    /// the caller then escalates to <see cref="Kill"/>.
    /// <para>
    /// Defaulted to false so a handle that does not model signalling keeps its previous behaviour
    /// exactly: the stop path hard-kills, as it did before this member existed.
    /// </para>
    /// </summary>
    bool TryRequestGracefulStop() => false;

    /// <summary>Waits up to <paramref name="milliseconds"/> for exit; true when it exited.</summary>
    bool WaitForExit(int milliseconds);
}

/// <summary>
/// Production <see cref="IGatewayProcessHandle"/> backed by a real <see cref="Process"/>.
/// </summary>
internal sealed class LiveProcessHandle(Process process, Func<Process, int, bool>? waitForExitOverride = null)
    : IGatewayProcessHandle
{
    public int Id => process.Id;

    public string? ExecutablePath
    {
        get
        {
            try
            {
                return process.HasExited ? null : process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Kill() => process.Kill();

    public bool TryRequestGracefulStop()
    {
        // Process.Kill() maps to SIGKILL on Unix and the BCL offers no graceful alternative, so the
        // polite signal has to come from libc directly. Windows has no SIGTERM a console host
        // reliably honours, so it keeps the hard-kill path.
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            if (process.HasExited)
                return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return PosixSignals.TrySendTerm(process.Id);
    }

    public bool WaitForExit(int milliseconds)
        => waitForExitOverride is not null
            ? waitForExitOverride(process, milliseconds)
            : process.WaitForExit(milliseconds);

    /// <summary>
    /// Enumerates every live process on the machine as a handle. Wrapping happens lazily so a
    /// process that dies mid-enumeration simply reports a null executable path.
    /// </summary>
    public static IEnumerable<IGatewayProcessHandle> EnumerateAll()
    {
        foreach (var process in Process.GetProcesses())
            yield return new LiveProcessHandle(process);
    }
}

/// <summary>
/// SIGTERM delivery for Unix hosts. Exists because <see cref="Process.Kill()"/> is SIGKILL on Unix
/// and the BCL exposes no graceful counterpart, so a polite stop must go through libc.
/// </summary>
internal static class PosixSignals
{
    private const int Sigterm = 15;

    // DllImport rather than LibraryImport deliberately: the source-generated marshaller emits an
    // unsafe stub, which would mean turning on AllowUnsafeBlocks for the entire CLI project to
    // support one call taking two ints. The return value is all we read, so nothing is marshalled
    // and the generator buys us nothing here.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int sig);

    /// <summary>
    /// Sends SIGTERM to <paramref name="pid"/>. Any failure - including a host with no libc - is
    /// reported as false rather than thrown, because the caller's fallback (a hard kill) is always
    /// available and must never be skipped because the polite attempt blew up.
    /// </summary>
    internal static bool TrySendTerm(int pid)
    {
        try
        {
            return Kill(pid, Sigterm) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
