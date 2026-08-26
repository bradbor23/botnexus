using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins the one thing that made the PID-file-less discovery of issue #2772 unreachable in
/// production: the CLI never told the process manager which binary to look for.
/// <para>
/// <c>GatewayProcessManager.FindProcessByBinaryPath</c> returns null immediately for a null or
/// blank path, so a stop issued without one can only ever consult the PID file. A gateway started
/// outside the CLI - by hand, by a script, by a second session - writes no PID file, and was
/// therefore reported "not running" while alive and holding extension assemblies mapped. The next
/// step of a restart then overwrote those assemblies and died mid-deploy.
/// </para>
/// <para>
/// The manager is substituted, so nothing is enumerated, signalled or spawned; the assertion is
/// purely that the command hands down a resolved binary path.
/// </para>
/// </summary>
[Collection("AnsiConsole")]
public sealed class GatewayStopWiringTests : IDisposable
{
    private readonly string _home;
    private readonly string _repoRoot;
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _consoleOutput;
    private readonly IGatewayProcessManager _processManager = Substitute.For<IGatewayProcessManager>();

    public GatewayStopWiringTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _home = Path.Combine(Path.GetTempPath(), $"bn-stopwire-home-{id}");
        _repoRoot = Path.Combine(Path.GetTempPath(), $"bn-stopwire-repo-{id}");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_repoRoot);

        _originalConsole = AnsiConsole.Console;
        _consoleOutput = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_consoleOutput),
            Interactive = InteractionSupport.No
        });

        _processManager
            .StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, "Gateway stopped (PID 1)", GatewayStopOutcome.Stopped));
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        _consoleOutput.Dispose();
        foreach (var dir in new[] { _home, _repoRoot })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The path spelled out rather than resolved through the production helper: deriving
    /// the expectation from the same code under test would assert nothing.</summary>
    private string ExpectedGatewayBinary => Path.Combine(
        _repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

    [Fact]
    public async Task Stop_PassesTheResolvedGatewayBinaryPath_SoDiscoveryCanRunWithoutAPidFile()
    {
        var root = BuildRootCommand();

        var exitCode = await root.InvokeAsync(
            ["gateway", "stop", "--target", _home, "--source", _repoRoot]);

        Assert.Equal(0, exitCode);
        await _processManager.Received(1).StopAsync(
            _home,
            ExpectedGatewayBinary,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_WithoutExplicitSource_StillPassesADiscoverableBinaryPath()
    {
        var root = BuildRootCommand();

        var exitCode = await root.InvokeAsync(["gateway", "stop", "--target", _home]);

        Assert.Equal(0, exitCode);
        // The default source resolves to ~/botnexus; what matters is that SOMETHING concrete is
        // handed down, because null is the value that disables discovery outright.
        await _processManager.Received(1).StopAsync(
            _home,
            Arg.Is<string?>(p => !string.IsNullOrWhiteSpace(p) && p.EndsWith("BotNexus.Gateway.Api.dll", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private RootCommand BuildRootCommand()
    {
        var verboseOption = new Option<bool>("--verbose");
        var targetOption = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verboseOption);
        root.AddGlobalOption(targetOption);
        root.AddCommand(new GatewayCommand(_processManager).Build(verboseOption, targetOption));
        return root;
    }
}
