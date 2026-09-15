# Gateway bugs: config shadowing and undeletable API sessions

**Audience:** gateway developers. **Goal:** explain two open gateway defects and one related hazard, why they happen, and how to fix and test them.

1. [Config edits are silently dropped for portal-created agents](#bug-1-config-edits-are-silently-dropped-for-portal-created-agents)
2. [Sessions created by `POST /api/chat` can never be deleted](#bug-2-sessions-created-by-post-api-chat-can-never-be-deleted)
3. [An agent with `bash` can stop the gateway it runs in](#hazard-3-an-agent-with-bash-can-stop-the-gateway-it-runs-in)

## Bug 1: config edits are silently dropped for portal-created agents

### Symptom

On every config reload, the gateway logs:

> Config-based agent 'X' is already registered with a different descriptor, so config changes for this agent are not being applied.

Every edit to that agent in `~/.botnexus/config.json` is ignored until the gateway restarts.

### Cause

The defect is in the "first-seen" branch of the reload loop in
`src/gateway/BotNexus.Gateway.Configuration/AgentConfigurationHostedService.cs` (around line 226).

1. `POST /api/agents` registers the descriptor and also writes it to config. The reload watcher sees
   the config change about 2 seconds later. The adopt path added for #3561 handles this only when the
   registered descriptor is fingerprint-equal to the config descriptor.
2. `PUT /api/agents/{id}/persona` edits the live descriptor with `with { }`. If that edit, or the
   config write, does not round-trip identically through `PlatformConfigAgentSource`, the two
   fingerprints differ.
3. From then on, the agent id is never added to `_appliedConfigDescriptors`. Every later reload takes
   the first-seen branch again, logs the warning, and continues. The update branch, which calls
   `Unregister` and then `Register`, is never reached. The state is permanent, not a one-reload race.

### Proposed fix

When the id is in the registry but not in `_appliedConfigDescriptors`, and the descriptors differ,
config wins: unregister, register the config descriptor, record it as applied, and log at
Information level (for example, "Reconciled agent 'X' to its config entry"). This matches the update
branch directly above it and the documented contract that config is the owner of record.

Keep the warning only for a registration that really came from a non-config source, if that source
can be identified. Otherwise remove it. As written, it describes a state that the code then refuses
to repair.

Also find which field differs after a persona `PUT`. `AvatarHue` and `Responsibility` are in both the
fingerprint and `PlatformConfigAgentWriter`, so the field that does not round-trip is elsewhere
(`Kind`, `Metadata`, `Order`, or a null versus empty collection). Fix that too. Otherwise config will
keep overwriting persona edits that were never persisted.

### Tests to add

- Register descriptor A, then reload with a different config descriptor B: the registry holds B. A
  second reload with C: the registry holds C. This proves the id is no longer stuck.
- Create an agent through the API, send a persona `PUT`, then reload: no warning is logged, and a
  following config edit is applied.

### Workaround

Restart the gateway. On startup it registers every agent from config.

## Bug 2: sessions created by `POST /api/chat` can never be deleted

### Symptom

`DELETE /api/sessions/{id}` returns `403` with `Caller is not authorized for this session.` for every
session created through `POST /api/chat`. This happens for every caller, including the portal.

### Cause

1. `ChatController.Send` (`src/gateway/BotNexus.Gateway.Api/Controllers/ChatController.cs`) never
   records the caller on the session, so the session is saved with `callerId: null`.
   `GET /api/sessions/{id}` shows the null value.
2. `GatewayAuthMiddleware` always attaches a caller identity. When no API keys are configured,
   `ApiKeyGatewayAuthHandler` (around line 192) authenticates every request as
   `CallerId = "gateway-dev"`.
3. `SessionsController.AuthorizeSessionCaller` (around line 558) denies the request whenever the
   request identity's `CallerId` is non-empty and not ordinal-equal to `session.CallerId`.
   `"gateway-dev"` never equals `null`, so `DELETE` is refused permanently. Other routes that use this
   guard, such as the sub-agent kill route (around line 276), are refused the same way.

`AgentExchangeService` (around line 240) and the cross-world exchange paths set `CallerId = null`
deliberately, so those sessions are probably undeletable too. This needs confirming.

### Proposed fix

- **Record the caller at creation.** In `ChatController.Send`, set `session.CallerId` from
  `HttpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey]` when a new session is created, to
  match what the SignalR path records.
- **Decide the ownerless case explicitly.** In `AuthorizeSessionCaller`, a session with a null
  `CallerId` currently belongs to nobody. Either treat it as unowned (allow any authenticated caller,
  or only an admin identity), or deny it with a distinct message. The current generic `403` reads as
  a permissions problem, not a missing owner.

### Tests to add

- `POST /api/chat` creates a session that carries the request's caller id, and `DELETE` by the same
  caller returns `204`.
- For a session with a null `CallerId`, `DELETE` follows the chosen ownerless rule instead of
  returning a generic `403`.

### Workaround

Archive the session's conversation with `DELETE /api/conversations/{id}`. This is a soft delete
without a caller check. It hides the sessions from the portal but does not remove them. It is refused
with `409` for an agent's default conversation.

## Hazard 3: an agent with `bash` can stop the gateway it runs in

### Symptom

A user asks an agent to create another agent. The agent writes the new entry to `config.json`, then
tries to "apply" it by restarting the gateway from its own `bash` tool. The chat stops without a
reply. The session later shows the notification "The gateway was restarted while your last message
was being processed", and the gateway log shows "previous gateway run terminated uncleanly". The new
agent is created correctly, so the failure looks like a problem with the new agent when it is not.

A second form: the user asks an agent to restart the gateway, often "using the script in the
`scripts` directory". The gateway log shows `Application is shutting down...` and, one to two minutes
later, `Gateway starting`, so the gateway does come back. The portal chat stays on **Streaming...**
with the last tool call still spinning, because the turn that issued the restart never finishes.
Asking the agent "is it up?" starts a new turn, and the agent often restarts the gateway again.

### Cause

An in-process agent runs inside the gateway process. Its `bash` tool (`ShellTool`) runs commands as
the gateway's operating-system user, so it can run `botnexus gateway restart` or `kill` the gateway's
own process. Stopping the gateway ends the agent's run, and the reply is lost.

Using `scripts/gateway-restart.sh` does not avoid this. The script restarts the gateway correctly, but
any restart that an agent starts still stops the process that the agent's turn is running in, so the
turn ends whichever command is used. Agents also improvise when a command fails: one observed turn ran
`pkill -f "BotNexus.Gateway"` (which matches every gateway process) followed by
`botnexus gateway start`.

There is no restart needed in this flow. `create_agent` and `update_agent` register the agent with the
running gateway, and a direct `config.json` edit is picked up by the config reload watcher.

### Removing `bash` from one agent

`AgentDescriptor` has no tool deny-list, only the `toolIds` allowlist. An empty `toolIds` means all
tools. A non-empty `toolIds` filters more than the named tool:

- **Workspace tools** from `DefaultAgentToolFactory`: `read`, `write`, `edit`, `bash`, `ls`, `grep`,
  `glob`, `tool_output_continue`. Only the listed ones are kept.
- **Registry tools** from `_toolRegistry.ResolveTools`. Only the listed ones are kept.
- **Tool providers** that call `ToolProviderContext.ToolAllowed` in
  `src/gateway/BotNexus.Gateway/Isolation/ToolProviders/ToolProviders.cs`: `cron`, `ask_user`,
  `spawn_subagent`, `list_subagents`, `manage_subagent`, `agent_converse`, `list_agents`,
  `list_locations`, `create_agent`, `update_agent`, `canvas`, `todo`. These disappear unless listed.

Providers that do not call `ToolAllowed` (for example `sessions`, `conversation`, `delay`,
`get_datetime`) are not filtered by `toolIds`. Extension tool contributors (`IAgentToolContributor`,
merged in `InProcessIsolationStrategy`) are filtered only when the contributor checks `toolIds`
itself. `ExecToolContributor` does: it provides `exec` only when `toolIds` is empty, is `["*"]`, or
names `exec`. `WebToolsContributor` does not check `toolIds`; it is gated by the agent's
`botnexus-web` extension configuration.

So to remove only `bash`, set `toolIds` to every workspace tool except `bash`, plus every
`ToolAllowed`-gated tool the agent should keep:

```json
"toolIds": [
  "read", "write", "edit", "ls", "grep", "glob", "tool_output_continue",
  "cron", "ask_user", "spawn_subagent", "list_subagents", "manage_subagent",
  "agent_converse", "list_agents", "list_locations", "create_agent", "update_agent",
  "canvas", "todo"
]
```

This list also removes `exec`, the other shell tool, because `ExecToolContributor` checks `toolIds`.
Do not add `exec` back to an agent that must not be able to stop the gateway. A contributor that does
not check `toolIds` is not removed by this list, so review any other enabled extension that runs
commands.

### Proposed fix

- **Add a deny-list.** A descriptor field such as `deniedToolIds`, applied after every tool source,
  would let an operator remove one tool without restating the rest. The session tool override
  (`ApplySessionToolOverrideAsync`) already narrows the final list, so it is a natural place to apply it.
- **Refuse gateway lifecycle commands from inside the gateway.** `botnexus gateway restart`, `stop`
  and `start` could detect that they run as a child of the gateway process they would stop, and refuse
  with a message telling the agent to ask the user. The same check belongs in
  `scripts/gateway-restart.sh`, which an agent can call directly. A pattern kill such as
  `pkill -f "BotNexus.Gateway"` cannot be refused this way, so the deny-list above is still needed.
- **Tell the user when a restart ends a turn.** After an agent-started restart, the startup log reported
  `Interrupted-turn scan complete: 0 session(s) found with crash sentinels`, and the portal stayed on
  Streaming with no notice. Find why the interrupted turn left no crash sentinel, so the chat shows
  that the gateway restarted instead of appearing to hang.
- **Tell agent-authoring skills not to restart.** The agent-creation skills should state that
  `create_agent` and `update_agent` apply changes live and that the agent must never restart the
  gateway.

### Tests to add

- An agent with `toolIds` set to the list above receives every listed tool and no `bash`.
- With a deny-list, `deniedToolIds: ["bash"]` removes only `bash`, including when `toolIds` is empty.
- Running `botnexus gateway restart` from a gateway child process exits non-zero without stopping the
  gateway.

### Workaround

Remove `bash` from agents that create or manage other agents, using the allowlist above. To undo it,
remove `toolIds`, which restores the full default toolset.

Removing `bash` is not practical for agents that use it for real work, such as API calls with `curl`,
`ssh` to other hosts, or running scripts. For those agents, and for every agent at once:

1. **Stop a runaway turn.** `POST /api/agents/{agentId}/sessions/{sessionId}/stop` calls the
   supervisor's `StopAsync` for that one agent instance and returns `204`. It changes no configuration
   and keeps the session history; the next message rebuilds the agent.
2. **Add a world-level rule.** `WorkspaceContextBuilder` inserts `~/.botnexus/WORLD.md` ahead of every
   agent's own prompt files whenever the file exists, with no configuration. A short rule there reaches
   every current and future agent: never restart, stop, start, kill or rebuild the gateway, even when
   asked; changes apply live; if a restart is really needed, give the user the restart command and end
   the turn. This is an instruction, not an enforcement, so keep removing `bash` where it is not needed.
3. **Restart from a terminal, never through an agent.** Run `scripts/gateway-restart.sh` on the gateway
   host from your own shell.
4. **Measure real shell use before removing `bash`.** `GET /api/sessions` lists only active sessions,
   so it undercounts. Count `bash` and `exec` rows in `session_history` in `sessions.sqlite`, joining
   `session_history.session_id` to `sessions.id`, then `sessions.conversation_id` to
   `conversations.agent_id`. Cron sessions carry `caller_id` `cron:<agentId>` instead.
