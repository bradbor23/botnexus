using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// The operator's stated access policy, applied to BOTH ways an agent reaches another agent
/// (Plan 3.6).
///
/// <remarks>
/// <c>gateway:agentExchange:accessPolicy</c> already existed and was already stated. It was
/// enforced on <c>agent_converse</c> and not on <c>spawn_subagent</c> - and the spawn path is the
/// stronger of the two, because a Mirror spawn runs the TARGET's descriptor verbatim and the child
/// therefore holds the target's tools rather than the parent's.
/// </remarks>
/// </summary>
public sealed class DelegationPolicyTests
{
    private const string Uid = "abc123";

    /// <summary>The operator's stated policy, in the three shapes these cases cover.</summary>
    private static AgentExchangeOptions Whitelist => new() { AccessPolicy = "whitelist" };

    private static AgentExchangeOptions Open => new() { AccessPolicy = "open" };

    /// <summary>Nothing stated at all: the default, which is open.</summary>
    private static AgentExchangeOptions Default => new();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("the repository root should be reachable from the test output");
        return dir.FullName;
    }

    // ── The rule itself ───────────────────────────────────────────────────

    [Fact]
    public void A_named_grant_authorises_the_target()
    {
        var initiator = Descriptor("nova", subAgents: ["scribe"]);

        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("scribe"), Descriptor("scribe")).ShouldBeTrue();
    }

    [Fact]
    public void An_ungranted_target_is_not_authorised()
    {
        var initiator = Descriptor("nova", subAgents: ["scribe"]);

        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("auditor"), Descriptor("auditor")).ShouldBeFalse();
    }

    [Fact]
    public void A_role_grant_authorises_any_agent_carrying_that_role()
    {
        // How an operator says "may delegate to any reviewer" without naming each one.
        var initiator = Descriptor("nova", subAgentRoles: ["reviewer"]);
        var target = Descriptor("auditor", role: "reviewer");

        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("auditor"), target).ShouldBeTrue();
    }

    [Fact]
    public void A_role_grant_cannot_be_satisfied_by_an_agent_this_gateway_cannot_see()
    {
        // The role lives in the TARGET's metadata. A remote agent's descriptor is not resolvable
        // here, and a role asserted by a gateway we do not control is not a grant we made.
        var initiator = Descriptor("nova", subAgentRoles: ["reviewer"]);

        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("remote-auditor"), null).ShouldBeFalse();
    }

    [Fact]
    public void Grants_are_matched_case_insensitively()
    {
        var initiator = Descriptor("nova", subAgents: ["Scribe"], subAgentRoles: ["Reviewer"]);

        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("scribe"), Descriptor("scribe")).ShouldBeTrue();
        PeerAccessPolicy.IsAllowed(Whitelist, initiator, AgentId.From("auditor"), Descriptor("auditor", role: "REVIEWER")).ShouldBeTrue();
    }

    // ── The gap that was open on the spawn path ───────────────────────────

    [Fact]
    public void Under_whitelist_an_agent_cannot_mirror_an_agent_it_was_never_granted()
    {
        // THE test. Before this, spawn_subagent(targetAgentId: ...) consulted no allow-list at
        // all, so an operator who had written `whitelist` got it applied to conversation and
        // ignored on delegation - the path that hands the child the TARGET's toolset.
        var parent = Descriptor("nova", subAgents: ["scribe"]);
        var target = Descriptor("privileged", tools: ["exec"]);
        PeerAccessPolicy.IsAllowed(Whitelist, parent, AgentId.From("privileged"), target).ShouldBeFalse();
    }

    [Fact]
    public void The_refusal_says_how_to_fix_it()
    {
        // A bare "not allowed" leaves an operator guessing which of three knobs to reach for.
        // The refusal is thrown from the spawn path (SubAgentMirrorPeerAccessTests drives that and
        // pins the ids); what this pins is that it still carries the remedy.
        DefaultSubAgentManager.MirrorRefusalRemedy.ShouldContain("subAgents");
        DefaultSubAgentManager.MirrorRefusalRemedy.ShouldContain("subAgentRoles");
        DefaultSubAgentManager.MirrorRefusalRemedy.ShouldContain("accessPolicy");
    }

    [Fact]
    public void Under_whitelist_a_granted_target_is_still_allowed()
    {
        var parent = Descriptor("nova", subAgents: ["scribe"]);
        PeerAccessPolicy.IsAllowed(Whitelist, parent, AgentId.From("scribe"), Descriptor("scribe")).ShouldBeTrue();
    }

    [Fact]
    public void A_role_grant_is_honoured_on_the_spawn_path_too()
    {
        // Both paths share one implementation precisely so this cannot diverge.
        var parent = Descriptor("nova", subAgentRoles: ["reviewer"]);
        PeerAccessPolicy.IsAllowed(Whitelist, parent, AgentId.From("auditor"), Descriptor("auditor", role: "reviewer")).ShouldBeTrue();
    }

    // ── What must NOT change ──────────────────────────────────────────────

    [Fact]
    public void The_default_policy_changes_nothing()
    {
        // `open` is the default and the behaviour that shipped. An install that never stated a
        // policy must not start refusing spawns because this landed.
        var parent = Descriptor("nova");
        PeerAccessPolicy.IsAllowed(Open, parent, AgentId.From("anyone"), Descriptor("anyone")).ShouldBeTrue();
    }

    [Fact]
    public void An_unconfigured_gateway_changes_nothing()
    {
        // The options are optional on the constructor, so every existing construction site keeps
        // working. Absent must mean `open`, not "deny everything".
        var parent = Descriptor("nova");
        PeerAccessPolicy.IsAllowed(Default, parent, AgentId.From("anyone"), Descriptor("anyone")).ShouldBeTrue();
    }

    [Fact]
    public void An_embody_spawn_is_never_subject_to_the_grant_check()
    {
        // Embody clones the PARENT's own descriptor. It reaches no other agent, so there is no
        // grant to check - and checking one would break ordinary delegation under whitelist for
        // no security gain whatsoever.
        // The exemption is now STRUCTURAL: the peer check is guarded on `request.Mode is Mirror`,
        // so for Embody the policy is never consulted at all. Asserted against the source because
        // that guard is the guarantee - the same shape the gateway-stop call-site fence uses.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "gateway", "BotNexus.Gateway", "Agents", "DefaultSubAgentManager.cs"));

        var call = source.IndexOf("PeerAccessPolicy.IsAllowed", StringComparison.Ordinal);
        call.ShouldBeGreaterThan(0, "the peer check must still be in the spawn path");
        source[..call].TrimEnd().ShouldEndWith(
            "if (request.Mode is Mirror mirror\n            && !",
            Case.Sensitive,
            "the peer check must stay guarded on Mirror, or Embody is refused under whitelist");
    }

    [Fact]
    public void Mirroring_yourself_is_not_silently_exempt_from_the_policy()
    {
        // Self is NOT special-cased, and that is a decision rather than an oversight. Mirroring
        // yourself gains no authority, so an exemption would be harmless - but it would also be
        // an unstated rule, and the point of this work is that the policy reads the way it is
        // written. An operator who wants it grants it.
        var parent = Descriptor("nova", subAgents: []);
        PeerAccessPolicy.IsAllowed(Whitelist, parent, AgentId.From("nova"), parent).ShouldBeFalse();
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static AgentDescriptor Descriptor(
        string id,
        IReadOnlyList<string>? subAgents = null,
        IReadOnlyList<string>? subAgentRoles = null,
        IReadOnlyList<string>? tools = null,
        string? role = null)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (role is not null)
            metadata["role"] = role;

        return new AgentDescriptor
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = "test-model",
            ApiProvider = "test",
            SubAgentIds = subAgents ?? [],
            SubAgentRoles = subAgentRoles ?? [],
            ToolIds = tools ?? [],
            Metadata = metadata
        };
    }
}
