using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins that every <c>StopAsync</c> call site in <c>GatewayCommand</c> hands the manager a binary
/// path.
/// </summary>
/// <remarks>
/// <para>
/// This lives at the SOURCE level on purpose. The manager's own tests cover discovery thoroughly
/// and could not have caught the original defect, because every one of their cases passes the path
/// in by hand while the real call sites passed nothing. The manager was correct throughout; the
/// caller dropped an optional argument, <c>FindProcessByBinaryPath(null)</c> returned on its first
/// line, and a live gateway was reported "not running".
/// </para>
/// <para>
/// Restart is the call site that matters most: one that believes nothing was running goes on to
/// redeploy extensions a live gateway still holds mapped, which is the
/// <c>&lt;ext&gt;.pdb ... used by another process</c> failure and a half-updated deployment.
/// </para>
/// </remarks>
public sealed class GatewayStopCallSiteFenceTests
{
    [Fact]
    public void NoStopCallSiteInGatewayCommand_OmitsTheBinaryPath()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "gateway", "BotNexus.Cli", "Commands", "GatewayCommand.cs"));

        var callSites = source
            .Split('\n')
            .Select((line, index) => (line: line.Trim(), number: index + 1))
            .Where(x => x.line.Contains("_processManager.StopAsync(", StringComparison.Ordinal))
            .ToList();

        callSites.ShouldNotBeEmpty("the fence is vacuous if it matches nothing");

        var offenders = callSites
            .Where(x => x.line.Contains("cancellationToken: cancellationToken", StringComparison.Ordinal))
            .Select(x => $"line {x.number}: {x.line}")
            .ToList();

        offenders.ShouldBeEmpty(
            "a StopAsync call that names cancellationToken is skipping gatewayBinaryPath, which "
            + "silently disables discovery for any gateway without a PID file");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // `.git` is a DIRECTORY in an ordinary clone and a FILE in a git worktree, and the
        // Directory.Packages.props anchor covers both - the same pair RepoLocator already uses.
        // Testing only for a directory made every test that walks for the repo root fail inside a
        // worktree, which is where this suite is run to escape the exFAT volume.
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("the repository root should be reachable from the test output directory");
        return dir.FullName;
    }
}
