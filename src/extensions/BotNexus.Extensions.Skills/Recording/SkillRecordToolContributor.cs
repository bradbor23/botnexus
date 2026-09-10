using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// Contributes <see cref="SkillRecordTool"/> when the agent may create skills and recording is
/// enabled.
/// </summary>
/// <remarks>
/// <para>
/// The recorder binds three things per session: the write path (a <see cref="SkillManagerTool"/>,
/// so recorded skills go through exactly the gates hand-written ones do), the draft store, and the
/// trace source for THIS session.
/// </para>
/// <para>
/// <see cref="ISessionStore"/> is an optional constructor dependency, matching the telemetry
/// parameter beside it, because a contributor that cannot be activated takes the extension's whole
/// tool contribution down with it. When it is absent the recorder is still contributed and every
/// action REFUSES, naming the missing wiring — a visible refusal, not a tool that quietly proposes
/// skills with nothing to verify them against.
/// </para>
/// </remarks>
public sealed class SkillRecordToolContributor(
    ISessionStore? sessions = null,
    ISkillUsageTelemetry? telemetry = null) : IAgentToolContributor
{
    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ExtensionConfigBinder.Bind<SkillsConfig>(context.Descriptor, SkillsExtensionJson.ExtensionId)
                     ?? new SkillsConfig();

        // Recording installs skills, so it inherits the creation gate rather than bypassing it.
        if (!config.AllowSkillCreation || !config.AllowSkillRecording)
            return Task.FromResult(new AgentToolContribution(Array.Empty<IAgentTool>()));

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentDir = Path.Combine(homeDir, ".botnexus", "agents", context.Descriptor.AgentId.Value);
        var agentSkillsDir = Path.Combine(agentDir, "skills");
        var workspaceSkillsDir = Path.Combine(context.WorkspacePath, "skills");
        var globalSkillsDir = Path.Combine(homeDir, ".botnexus", "skills");

        var writer = new SkillManagerTool(
            agentSkillsDir,
            workspaceSkillsDir,
            globalSkillsDir,
            config,
            fileSystem: null,
            telemetry: telemetry,
            createdBy: context.Descriptor.AgentId.Value);

        // Beside the agent's skills directory, never inside it: the store's constructor rejects a
        // root that sits within any discovery root, so this is checked rather than assumed.
        var draftStore = new SkillDraftStore(
            SkillDraftStore.ResolveRoot(agentDir),
            [agentSkillsDir, workspaceSkillsDir, globalSkillsDir]);

        ISessionTraceSource? trace = sessions is null
            ? null
            : new SessionStoreTraceSource(sessions, context.ExecutionContext.SessionId);

        IReadOnlyList<IAgentTool> tools =
        [
            new SkillRecordTool(writer, draftStore, trace, config, context.Descriptor.AgentId.Value)
        ];

        return Task.FromResult(new AgentToolContribution(tools));
    }
}
