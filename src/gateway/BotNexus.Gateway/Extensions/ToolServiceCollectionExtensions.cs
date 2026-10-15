using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using BotNexus.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration extensions for the built-in agent tools.
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in tools and tool registry.
    /// </summary>
    public static IServiceCollection AddBotNexusTools(this IServiceCollection services)
    {
        // One source of truth for both spawn sites: the built-in shell tool and the exec extension
        // read this same policy. See LocalChildEnvironment for why they must not answer differently.
        // Honours the older gatewayExtensions toolEnvironmentPassThrough key as well as
        // localChildEnvironmentPassThrough, so an operator who set either keeps their pass-through.
        services.AddSingleton<LocalChildEnvironmentPolicy>(sp =>
        {
            var gateway = sp.GetService<IOptions<PlatformConfig>>()?.Value.Gateway;
            var names = gateway?.LocalChildEnvironmentPassThrough
                ?? gateway?.ToolEnvironmentPassThrough;
            return new LocalChildEnvironmentPolicy(names);
        });

        services.AddSingleton<IAgentToolFactory>(sp =>
        {
            var config = sp.GetService<IOptions<PlatformConfig>>()?.Value;
            var preference = ParseShellPreference(config?.Gateway?.ShellPreference);
            var shellCommand = config?.Gateway?.ShellCommand;
            // Resolve the platform config path so file tools can deny direct writes to it (issue #633).
            var configPath = PlatformConfigLoader.GetDefaultConfigPath(new System.IO.Abstractions.FileSystem());
            return new DefaultAgentToolFactory(preference, configPath, shellCommand, BuildReadToolOptions(config),
                sp.GetRequiredService<LocalChildEnvironmentPolicy>());
        });

        // Tool registry collects extension IAgentTool registrations.
        services.AddSingleton<IToolRegistry>(sp => new DefaultToolRegistry(sp.GetServices<IAgentTool>()));

        return services;
    }

    /// <summary>
    /// Projects the operator-facing <see cref="ReadToolConfig"/> onto the tool-layer
    /// <see cref="ReadToolOptions"/> (#2689). Absent config yields the defaults, so an existing
    /// deployment gets the guardrails without editing config.json.
    /// </summary>
    internal static ReadToolOptions BuildReadToolOptions(PlatformConfig? config)
    {
        var readTool = config?.Gateway?.ReadTool;
        return readTool is null
            ? new ReadToolOptions()
            : new ReadToolOptions
            {
                LargeReadThresholdBytes = readTool.LargeReadThresholdBytes,
                ElideUnchangedRereads = readTool.ElideUnchangedRereads,
            };
    }

    private static ShellPreference ParseShellPreference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ShellPreference.Auto;

        return value.Trim().ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => ShellPreference.Pwsh,
            "bash" => ShellPreference.Bash,
            _ => ShellPreference.Auto,
        };
    }
}
