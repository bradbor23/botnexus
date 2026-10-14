using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// The configured root is itself reached through a symbolic link. This is the ordinary case on
/// macOS, where <c>Path.GetTempPath()</c> hands back <c>/var/folders/...</c> and <c>/var</c> is a
/// link to <c>/private/var</c>: the validator's link-resolution pass sees the real target and must
/// not read it as an escape from a root it never resolved.
/// </summary>
public sealed class PathValidatorSymlinkedRootTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-symlinked-root-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateAndResolve_WorkspaceReachedThroughALink_AllowsItsOwnFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = CreateLinkedWorkspace();
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "AGENTS");

        var sut = new DefaultPathValidator(policy: null, workspace);

        sut.ValidateAndResolve(Path.Combine(workspace, "AGENTS.md"), FileAccessMode.Read)
            .ShouldNotBeNull();
    }

    [Fact]
    public void ValidateAndResolve_LinkOutOfALinkedWorkspace_IsStillRefused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = CreateLinkedWorkspace();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "SECRET");
        Directory.CreateSymbolicLink(Path.Combine(_root, "real", "workspace", "escape"), outside);

        var sut = new DefaultPathValidator(policy: null, workspace);

        sut.ValidateAndResolve(Path.Combine(workspace, "escape", "secret.md"), FileAccessMode.Read)
            .ShouldBeNull();
    }

    [Fact]
    public void ValidateAndResolve_DenyNamingTheRealPath_StillRefusesTheLinkedPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = CreateLinkedWorkspace();
        File.WriteAllText(Path.Combine(workspace, "SOUL.md"), "SOUL");
        var policy = new FileAccessPolicy
        {
            DeniedPaths = [Path.Combine(_root, "real", "workspace", "SOUL.md")]
        };

        var sut = new DefaultPathValidator(policy, workspace);

        sut.ValidateAndResolve(Path.Combine(workspace, "SOUL.md"), FileAccessMode.Read)
            .ShouldBeNull();
    }

    /// <summary>
    /// Builds <c>&lt;root&gt;/real/workspace</c> and returns it as <c>&lt;root&gt;/link/workspace</c>,
    /// so the configured root differs from its real location by one link — the shape macOS's temp
    /// directory has by default.
    /// </summary>
    private string CreateLinkedWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real", "workspace"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));

        return Path.Combine(_root, "link", "workspace");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
